// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoDataExtractor.cs  —  Deep Urbano 11 data extraction for BOQ / hydraulics
//
// DATA SOURCE PRIORITY (highest → lowest):
//   1. Entity geometry  (AcDbLine length, AcDbCircle centre) — always exact
//   2. NOD  ARSX_NETWORKTOPOLOGY / ARSX_DCT_PIPE / __SA__.LD — when committed
//   3. Extension-dict XRecords  — per-entity XML, if Urbano writes them
//   4. Label entities via AG_LAB_ENTITY + AG_LAB_DATAID      — after ARS_LABEL_N/S
//
// HOW URBANO 11 ACTUALLY STORES DATA
// ────────────────────────────────────────────────────────────────────────────────
// Master entity XData groups (keyed by registered-app name):
//   AG_GUID           → permanent GUID for the pipe/manhole (survives handle changes)
//   TOPOGUID          → topology GUID (usually same as AG_GUID)
//   DRAWER_ID.{guid}  → presence of this key identifies a MASTER entity; value
//                        encodes the drawer name ("0001_01_Pipe_contours", etc.)
//   AG_LAB_HANDLES    → semicolon list of label child handles (may go stale!)
//
// Label entity XData (on AcDbText / MText objects):
//   AG_GUID           → this label's own GUID
//   AG_LAB_ENTITY     → GUID of the master this label belongs to   ← use this
//   AG_LAB_DATAID     → property field name, e.g. "PIPE_DIA_NOM"  ← use this
//   AG_LAB_ST         → display state (0 = hidden, 1 = visible)
//
// Extension dictionary of master entities:
//   BIN_LAY_ENT${drawer-guid}  → Xrecord with binary chunk(s)
//                                  Format: ASCII "HANDLE1#HANDLE2#HANDLE3#"
//                                  These are graphical child handles, NOT property data.
//
// Named Objects Dictionary keys (Urbano global store):
//   ARSX_NETWORKTOPOLOGY  → committed network XML (may be nullData)
//   ARSX_DCT_PIPE         → pipe catalogue XML
//   ARSX_DCT_MANHOLE      → manhole catalogue XML
//   ARSX_DCT_TRENCH       → trench dimension catalogue
//   __SA__.LD             → main session data record
//
// IMPORTANT: Call ARS_LABEL_N and ARS_LABEL_S (Urbano AutoCAD commands) before
// scanning labels. They regenerate label entities with current data.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(UrbanoBoQExtraction.UrbanoDataExtractor))]

namespace UrbanoBoQExtraction
{
    // ───────────────────────────────────────────────────────────────────────────
    // Data model
    // ───────────────────────────────────────────────────────────────────────────
    public class UrbanoEntityData
    {
        // ── Identity ────────────────────────────────────────────────────────────
        public string Handle        { get; set; }
        public string AgGuid        { get; set; }
        public string AcadType      { get; set; }
        public bool   IsPipe        { get; set; }
        public bool   IsManhole     { get; set; }
        public string DrawerContent { get; set; }   // raw drawer name string

        // ── Geometry (filled from AcDbLine / AcDbCircle — always authoritative) ─
        public Point3d? StartPoint  { get; set; }   // pipe start
        public Point3d? EndPoint    { get; set; }   // pipe end
        public double   Length2D    { get; set; }   // horizontal length
        public double   SlopePermil { get; set; }   // vertical / horizontal * 1000
        public Point3d? Center      { get; set; }   // manhole circle centre
        public double   Radius      { get; set; }   // manhole circle radius

        // ── Properties (filled from NOD / ExtDict / labels) ─────────────────────
        // Key = Urbano field name (e.g. PIPE_MATERIAL, ENTITY_NAME)
        public Dictionary<string, string> Props { get; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // ── Typed convenience accessors ─────────────────────────────────────────
        public string Material      => Prop("PIPE_MATERIAL");
        public string DiameterNom   => Prop("PIPE_DIA_NOM");       // e.g. "400"
        public string EntityName    => Prop("ENTITY_NAME");
        public string TerrainElev   => Prop("TERRAIN_ELEV_1");
        public string InvertElev    => Prop("LEVEL_LINE_ELEVATION_BI");
        public string TrenchWidth   => Prop("TRENCH_WIDTH");

        public double  Depth
        {
            get
            {
                double t, i;
                if (double.TryParse(TerrainElev, NumberStyles.Any, CultureInfo.InvariantCulture, out t)
                 && double.TryParse(InvertElev,   NumberStyles.Any, CultureInfo.InvariantCulture, out i)
                 && t > i) return t - i;
                return 0;
            }
        }

        public string Prop(string key, string def = "")
            => Props.TryGetValue(key, out var v) ? v : def;

        /// <summary>Write a property only if not already set by a higher-priority source.</summary>
        public void SetIfEmpty(string key, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (!Props.ContainsKey(key) || string.IsNullOrEmpty(Props[key]))
                Props[key] = value;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // XData reader  —  static helper
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UrbanoXData
    {
        /// <summary>
        /// Reads all XData groups from a DBObject.
        /// Returns Dictionary keyed by registered-app name.
        /// Each value is the list of TypedValues that follow the 1001 app-name entry.
        /// </summary>
        public static Dictionary<string, List<TypedValue>> ReadGroups(DBObject obj)
        {
            var result = new Dictionary<string, List<TypedValue>>(StringComparer.OrdinalIgnoreCase);
            using (ResultBuffer rb = obj.XData)
            {
                if (rb == null) return result;
                string app = null;
                foreach (TypedValue tv in rb)
                {
                    if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        app = tv.Value.ToString();
                        if (!result.ContainsKey(app)) result[app] = new List<TypedValue>();
                    }
                    else if (app != null)
                        result[app].Add(tv);
                }
            }
            return result;
        }

        /// <summary>
        /// Returns the string value of the first TypedValue in the named group.
        /// Returns null if the group doesn't exist or is empty.
        /// </summary>
        public static string First(Dictionary<string, List<TypedValue>> xd, string app)
            => xd.TryGetValue(app, out var v) && v.Count > 0 ? v[0].Value?.ToString() : null;

        public static List<string> All(Dictionary<string, List<TypedValue>> xd, string app)
            => xd.TryGetValue(app, out var v)
               ? v.Select(t => t.Value?.ToString() ?? "").ToList()
               : new List<string>();

        /// <summary>
        /// Detects a MASTER entity — must have DRAWER_ID.* XData.
        /// Labels, contour lines and other sub-entities do NOT have DRAWER_ID.
        /// Returns the drawer content string (encodes pipe vs manhole type), or null.
        /// </summary>
        public static string GetDrawerContent(Dictionary<string, List<TypedValue>> xd)
        {
            const string pfx = "DRAWER_ID.";
            foreach (string k in xd.Keys)
            {
                if (!k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;
                if (xd.TryGetValue(k, out var v) && v.Count > 0)
                    return v[0].Value?.ToString() ?? k.Substring(pfx.Length);
                return k.Substring(pfx.Length);
            }
            return null;
        }

        /// <summary>
        /// True if the drawer content string indicates a pipe.
        /// Urbano uses "Pipe_contours" for the pipe master line entity.
        /// </summary>
        public static bool DrawerIsPipe(string drawerContent)
            => drawerContent != null &&
               drawerContent.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>
        /// True if the drawer content string indicates a manhole.
        /// Urbano uses "RealView" for the manhole master circle entity.
        /// </summary>
        public static bool DrawerIsManhole(string drawerContent)
            => drawerContent != null &&
               drawerContent.IndexOf("realview", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Extension-dictionary reader  —  static helper
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UrbanoExtDict
    {
        /// <summary>
        /// Reads the BIN_LAY_ENT$ binary record from a master entity's extension
        /// dictionary and returns the list of graphical child handles it contains.
        ///
        /// The binary format is plain ASCII: "HANDLE1#HANDLE2#HANDLE3#"
        /// These are DWG object handles (hex), NOT GUID strings.
        /// Use db.GetObjectId(false, new Handle(Convert.ToInt64(h,16)), 0) to resolve.
        ///
        /// NOTE: These handles go stale when Urbano regenerates the drawing.
        ///       The GUID-based label scan (AG_LAB_ENTITY) is more reliable.
        /// </summary>
        public static List<string> ReadBinChildHandles(Transaction tr, DBObject obj)
        {
            var handles = new List<string>();
            if (obj.ExtensionDictionary.IsNull) return handles;

            var extDict = tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (extDict == null) return handles;

            foreach (DBDictionaryEntry entry in extDict)
            {
                if (!entry.Key.StartsWith("BIN_LAY_ENT$")) continue;
                var xrec = tr.GetObject(entry.Value, OpenMode.ForRead) as Xrecord;
                if (xrec?.Data == null) continue;

                foreach (TypedValue tv in xrec.Data)
                {
                    byte[] b = null;
                    if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                        b = tv.Value as byte[];
                    if (b == null) continue;

                    string raw = Encoding.ASCII.GetString(b);
                    foreach (string h in raw.Split('#'))
                    {
                        string trimmed = h.Trim();
                        if (trimmed.Length > 0) handles.Add(trimmed);
                    }
                }
            }
            return handles;
        }

        /// <summary>
        /// Searches extension dictionary for XRecords that contain XML or
        /// structured string data (not binary handle lists).
        ///
        /// Urbano may write per-entity property XRecords in future versions or
        /// when specific modules are active. Returns key → decoded string pairs.
        /// </summary>
        public static Dictionary<string, string> ReadStringRecords(Transaction tr, DBObject obj)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (obj.ExtensionDictionary.IsNull) return result;

            var extDict = tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
            if (extDict == null) return result;

            foreach (DBDictionaryEntry entry in extDict)
            {
                // Skip the known binary handle-list records
                if (entry.Key.StartsWith("BIN_LAY_ENT$")) continue;

                var xrec = tr.GetObject(entry.Value, OpenMode.ForRead) as Xrecord;
                if (xrec?.Data == null) continue;

                var bytes  = new List<byte>();
                string str = null;

                foreach (TypedValue tv in xrec.Data)
                {
                    if (tv.TypeCode == 1)
                        str = tv.Value as string;
                    else if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                    {
                        if (tv.Value is byte[] b) bytes.AddRange(b);
                    }
                }

                // Prefer binary decoded as UTF-8, then fall back to string
                string decoded = null;
                if (bytes.Count > 0)
                {
                    decoded = TrySafeDecodeBytes(bytes.ToArray());
                }
                decoded = decoded ?? str;

                if (!string.IsNullOrEmpty(decoded) && decoded != "nullData")
                    result[entry.Key] = decoded;
            }
            return result;
        }

        private static string TrySafeDecodeBytes(byte[] b)
        {
            // UTF-8 first
            try
            {
                string s = Encoding.UTF8.GetString(b);
                if (!s.Contains('�')) return s;  // no replacement chars → valid
            }
            catch { }
            // UTF-16 LE fallback
            try { return Encoding.Unicode.GetString(b); } catch { return null; }
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // NOD (Named Objects Dictionary) reader  —  static helper
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UrbanoNod
    {
        // All top-level NOD keys used by Urbano 11
        public static readonly string[] KnownKeys =
        {
            "ARSX_NETWORKTOPOLOGY",   // ← most likely to contain committed pipe/manhole data
            "ARSX_AUXTOPOLOGY",
            "ARSX_DCT_PIPE",          // pipe type catalogue
            "ARSX_DCT_MANHOLE",       // manhole type catalogue
            "ARSX_DCT_TRENCH",        // trench dimension catalogue
            "ARSX_LSINSTANCES",
            "CALC_CONF_SEWAGE",
            "__SA__"                  // session data, including LD (main data record)
        };

        /// <summary>
        /// Reads all Urbano NOD entries and returns decoded XML strings.
        /// Key = "{nodKey}/{subKey}", Value = XML string (or null if nullData).
        ///
        /// HOW TO TRIGGER POPULATION:
        ///   Urbano only writes to NOD after you run a network analysis or explicitly
        ///   save from within Urbano (not just Ctrl+S). If all values are "nullData",
        ///   open Urbano's Analysis dialog, run the hydraulic calculation, then save.
        /// </summary>
        public static Dictionary<string, string> ReadAllXml(Transaction tr, Database db)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var nod    = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead) as DBDictionary;
            if (nod == null) return result;

            foreach (string nodKey in KnownKeys)
            {
                if (!nod.Contains(nodKey)) continue;

                var subDict = tr.GetObject(nod.GetAt(nodKey), OpenMode.ForRead) as DBDictionary;
                if (subDict == null) continue;

                foreach (DBDictionaryEntry sub in subDict)
                {
                    var xrec = tr.GetObject(sub.Value, OpenMode.ForRead) as Xrecord;
                    if (xrec?.Data == null) continue;

                    string decoded = XRecordToString(xrec);
                    if (!string.IsNullOrEmpty(decoded) && decoded != "nullData")
                        result[$"{nodKey}/{sub.Key}"] = decoded;
                }
            }
            return result;
        }

        private static string XRecordToString(Xrecord xrec)
        {
            var bytes  = new List<byte>();
            string str = null;

            foreach (TypedValue tv in xrec.Data)
            {
                if (tv.TypeCode == 1)
                    str = tv.Value as string;
                else if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                {
                    if (tv.Value is byte[] b) bytes.AddRange(b);
                }
            }

            if (bytes.Count > 0)
            {
                try
                {
                    string s = Encoding.UTF8.GetString(bytes.ToArray());
                    if (!s.Contains('�')) return s;
                }
                catch { }
                try { return Encoding.Unicode.GetString(bytes.ToArray()); } catch { }
            }
            return str;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // XML parser  —  static helper
    //
    // Urbano's XML schema is proprietary and varies by version and module.
    // The parser tries a wide set of candidate element/attribute names so it works
    // across schema versions without needing to know the exact format upfront.
    // ═══════════════════════════════════════════════════════════════════════════
    public static class UrbanoXml
    {
        // ── Property → candidate XML element/attribute names ─────────────────────
        // Add entries here as you discover more schema variants.
        private static readonly Dictionary<string, string[]> PipeMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["PIPE_MATERIAL"]        = new[] { "Material", "Mat", "PipeMat", "Malzeme", "material" },
                ["PIPE_DIA_NOM"]         = new[] { "Diameter", "Dia", "DN", "NominalDiameter", "Cap", "CapMm", "diameter" },
                ["SECTION_LENGTH_2D"]    = new[] { "Length", "Len", "SectionLength", "Uzunluk" },
                ["SECTION_SLOPE_RATIO"]  = new[] { "Slope", "SlopeRatio", "Grade", "Egim", "slope" },
                ["TRENCH_WIDTH"]         = new[] { "TrenchWidth", "HendekGenislik", "Width" },
                ["PIPE_CLASS"]           = new[] { "Class", "PipeClass", "Sinif" },
                ["PIPE_ROUGHNESS"]       = new[] { "Roughness", "Manning", "Strickler" },
            };

        private static readonly Dictionary<string, string[]> ManholeMap =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ENTITY_NAME"]               = new[] { "Name", "ID", "Label", "Ad", "BacaAd", "ManholeID", "name" },
                ["TERRAIN_ELEV_1"]            = new[] { "TerrainElev", "Terrain", "TZ", "ZGround", "ZeminKot", "Surface" },
                ["LEVEL_LINE_ELEVATION_BI"]   = new[] { "Invert", "InvertElev", "IZ", "AkarKot", "FlowElev", "Elevation" },
                ["MANHOLE_DEPTH"]             = new[] { "Depth", "Derinlik", "depth" },
                ["MANHOLE_DIA"]               = new[] { "ManholeDia", "BacaCap", "InnerDia" },
            };

        /// <summary>
        /// Parses a complete Urbano XML document (from NOD or ExtDict) and applies
        /// all found properties to matching entities in the map.
        ///
        /// Strategy: for each entity GUID, find any XML element that references it
        /// as an attribute value, then read sibling attributes for the properties.
        /// </summary>
        public static int ApplyToMap(string xml, Dictionary<string, UrbanoEntityData> map)
        {
            if (string.IsNullOrWhiteSpace(xml)) return 0;
            int count = 0;
            try
            {
                XDocument doc = XDocument.Parse(NormaliseXml(xml));
                foreach (var data in map.Values)
                {
                    // Find every XML element that contains this entity's GUID as any attribute
                    var matches = doc.Descendants()
                        .Where(el => el.Attributes()
                            .Any(a => string.Equals(a.Value, data.AgGuid,
                                      StringComparison.OrdinalIgnoreCase)))
                        .ToList();

                    if (matches.Count == 0) continue;

                    var propMap = data.IsPipe ? PipeMap : ManholeMap;
                    foreach (XElement el in matches)
                        ApplyElementProps(el, data, propMap);

                    count++;
                }
            }
            catch { /* XML may not be valid — ignore */ }
            return count;
        }

        /// <summary>
        /// Parses a per-entity XML string (from extension dict) and applies
        /// properties directly to the given data object.
        /// </summary>
        public static void ApplyToEntity(string xml, UrbanoEntityData data)
        {
            if (string.IsNullOrWhiteSpace(xml)) return;
            try
            {
                XDocument doc = XDocument.Parse(NormaliseXml(xml));
                var propMap = data.IsPipe ? PipeMap : ManholeMap;

                // Try every descendant as a potential property carrier
                foreach (XElement el in doc.Descendants())
                    ApplyElementProps(el, data, propMap);
            }
            catch { }
        }

        private static void ApplyElementProps(XElement el, UrbanoEntityData data,
                                               Dictionary<string, string[]> propMap)
        {
            foreach (var kv in propMap)
            {
                foreach (string candidateName in kv.Value)
                {
                    // Try as XML attribute on this element
                    string val = el.Attribute(candidateName)?.Value;

                    // Try as child element text
                    if (string.IsNullOrEmpty(val))
                        val = el.Element(candidateName)?.Value;

                    if (!string.IsNullOrEmpty(val))
                    {
                        data.SetIfEmpty(kv.Key, val);
                        break;
                    }
                }
            }
        }

        /// <summary>
        /// Attempts key=value parsing for non-XML structured strings.
        /// Supports "KEY=VALUE" lines separated by newline or semicolon.
        /// </summary>
        public static void ApplyKeyValue(string raw, UrbanoEntityData data)
        {
            if (string.IsNullOrEmpty(raw)) return;
            foreach (string line in raw.Split(new[] { '\n', '\r', ';' },
                                              StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = line.IndexOf('=');
                if (eq < 1) continue;
                string k = line.Substring(0, eq).Trim();
                string v = line.Substring(eq + 1).Trim();
                if (k.Length > 0) data.SetIfEmpty(k, v);
            }
        }

        // Wraps fragments in a root element so XDocument.Parse doesn't fail
        private static string NormaliseXml(string xml)
        {
            xml = xml.Trim();
            return xml.StartsWith("<") ? xml : $"<root>{xml}</root>";
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Main command class
    // ═══════════════════════════════════════════════════════════════════════════
    public class UrbanoDataExtractor
    {
        // ── AutoCAD text-code cleaner ─────────────────────────────────────────
        private static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"%%[a-zA-Z]", "");              // %%c → Ø etc.
            s = Regex.Replace(s, @"\{\\[^}]+\}", "");             // {\Ffonts\...;text}
            s = Regex.Replace(s, @"\\[a-zA-Z][^;]*;?", " ");     // \P \N \l etc.
            return s.Trim();
        }

        // ─────────────────────────────────────────────────────────────────────────
        // COMMAND: ExtractUrbanoDataDeep
        //
        // Usage:   select any Urbano pipe or manhole entities when prompted.
        // Output:  writes to AutoCAD command line + Desktop\UrbanoExtract.txt
        //
        // The command uses all four data sources in priority order:
        //   geometry → NOD → extension dict → label entities
        // ─────────────────────────────────────────────────────────────────────────
        [CommandMethod("ExtractUrbanoDataDeep")]
        public void ExtractUrbanoDataDeep()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            // ── User selection ────────────────────────────────────────────────
            var pso = new PromptSelectionOptions
                { MessageForAdding = "\nUrban boru ve baca entity'lerini sec: " };
            PromptSelectionResult psr = ed.GetSelection(pso);
            if (psr.Status != PromptStatus.OK) return;

            // Master entity map: AgGuid → UrbanoEntityData
            var map = new Dictionary<string, UrbanoEntityData>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                // ════════════════════════════════════════════════════════════════
                // PASS 1 — Master entities + geometry
                // ════════════════════════════════════════════════════════════════
                ed.WriteMessage("\n[Pass 1] Master entities + geometry...\n");

                foreach (SelectedObject so in psr.Value)
                {
                    DBObject obj = tr.GetObject(so.ObjectId, OpenMode.ForRead);
                    var xd      = UrbanoXData.ReadGroups(obj);

                    string guid    = UrbanoXData.First(xd, "AG_GUID");
                    string drawer  = UrbanoXData.GetDrawerContent(xd);

                    // Skip: must be a master entity (has DRAWER_ID.* XData)
                    if (guid == null || drawer == null)
                    {
                        ed.WriteMessage($"\n  Handle {obj.Handle}: no DRAWER_ID — skipped " +
                                        $"(label or sub-entity)");
                        continue;
                    }

                    var data = new UrbanoEntityData
                    {
                        Handle        = obj.Handle.ToString(),
                        AgGuid        = guid,
                        AcadType      = obj.GetRXClass().Name,
                        DrawerContent = drawer,
                        IsPipe        = UrbanoXData.DrawerIsPipe(drawer),
                        IsManhole     = UrbanoXData.DrawerIsManhole(drawer)
                    };

                    // ── 1a. Geometry (highest priority — always reliable) ───────
                    if (obj is Line ln)
                    {
                        data.StartPoint = ln.StartPoint;
                        data.EndPoint   = ln.EndPoint;

                        double dx  = ln.EndPoint.X - ln.StartPoint.X;
                        double dy  = ln.EndPoint.Y - ln.StartPoint.Y;
                        double dz  = ln.EndPoint.Z - ln.StartPoint.Z;
                        data.Length2D = Math.Sqrt(dx * dx + dy * dy);

                        data.Props["SECTION_LENGTH_2D"] =
                            data.Length2D.ToString("F4", CultureInfo.InvariantCulture);

                        if (Math.Abs(dz) > 0.0001 && data.Length2D > 0.001)
                        {
                            data.SlopePermil = Math.Abs(dz) / data.Length2D * 1000.0;
                            data.Props["SECTION_SLOPE_PERMIL_CALC"] =
                                data.SlopePermil.ToString("F2", CultureInfo.InvariantCulture);
                        }
                    }
                    else if (obj is Circle cr)
                    {
                        data.Center = cr.Center;
                        data.Radius = cr.Radius;
                    }

                    // ── 1b. Extension dictionary ────────────────────────────────
                    // BIN_LAY_ENT$ → child handle list (graphical only, not property data)
                    // Other XRecords → try XML / key-value parse
                    var binHandles = UrbanoExtDict.ReadBinChildHandles(tr, obj);
                    if (binHandles.Count > 0)
                        ed.WriteMessage($"\n  Handle {obj.Handle}: {binHandles.Count} BIN child handles " +
                                        $"({string.Join(" ", binHandles.Take(4))}{(binHandles.Count > 4 ? "…" : "")})");

                    var strRecords = UrbanoExtDict.ReadStringRecords(tr, obj);
                    foreach (var kv in strRecords)
                    {
                        ed.WriteMessage($"\n  ExtDict[{kv.Key}]: {kv.Value.Substring(0, Math.Min(80, kv.Value.Length))}");

                        if (kv.Value.TrimStart().StartsWith("<"))
                            UrbanoXml.ApplyToEntity(kv.Value, data);  // XML
                        else
                            UrbanoXml.ApplyKeyValue(kv.Value, data);  // key=value fallback
                    }

                    map[guid] = data;
                    ed.WriteMessage($"\n  + {(data.IsPipe ? "PIPE" : data.IsManhole ? "MANHOLE" : "OTHER")} " +
                                    $"Handle:{data.Handle} GUID:{guid.Substring(0, 8)}… " +
                                    $"L={data.Length2D:F2}m");
                }

                ed.WriteMessage($"\n[Pass 1] {map.Count} master entities read.\n");

                // ════════════════════════════════════════════════════════════════
                // PASS 2 — NOD (ARSX_NETWORKTOPOLOGY etc.)
                //
                // These contain the authoritative network data when Urbano has
                // committed it. If all values are "nullData", the user must:
                //   a) Open the drawing with Urbano (not plain AutoCAD)
                //   b) Run the network analysis / hydraulic calculation
                //   c) Save from within Urbano
                // ════════════════════════════════════════════════════════════════
                ed.WriteMessage("\n[Pass 2] Named Objects Dictionary (ARSX)...\n");

                var nodData = UrbanoNod.ReadAllXml(tr, db);
                if (nodData.Count == 0)
                {
                    ed.WriteMessage("  All ARSX NOD entries are nullData or absent.\n" +
                                    "  → Open DWG in Urbano, run analysis, then save.\n");
                }
                else
                {
                    foreach (var kv in nodData)
                    {
                        ed.WriteMessage($"  NOD[{kv.Key}]: {kv.Value.Length} chars\n");
                        int updated = UrbanoXml.ApplyToMap(kv.Value, map);
                        ed.WriteMessage($"  → {updated} entities updated from this record\n");
                    }
                }

                // ════════════════════════════════════════════════════════════════
                // PASS 3 — Label entities (GUID-based cross-reference)
                //
                // Scans ALL entities in model space for AG_LAB_ENTITY + AG_LAB_DATAID
                // XData. This is the reliable fallback when NOD/ExtDict are empty.
                //
                // PREREQUISITE: Before calling this command, run:
                //   ARS_LABEL_N   — refreshes node (manhole) labels
                //   ARS_LABEL_S   — refreshes section (pipe) labels
                // Both commands are built-in Urbano commands. Urbano regenerates all
                // label entities with current data when these run.
                //
                // WHY GUID-BASED (not handle-based):
                //   Urbano can regenerate entities with new handles but keeps GUIDs
                //   stable. Using AG_LAB_ENTITY (GUID) instead of AG_LAB_HANDLES
                //   (handles) ensures we never get stale references.
                // ════════════════════════════════════════════════════════════════
                ed.WriteMessage("\n[Pass 3] Label entity cross-reference (AG_LAB_ENTITY)...\n");

                int labelCount = 0, matchCount = 0;

                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                var ms = tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead)
                           as BlockTableRecord;

                foreach (ObjectId id in ms)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    var xd = UrbanoXData.ReadGroups(obj);

                    string masterGuid = UrbanoXData.First(xd, "AG_LAB_ENTITY");
                    string dataId     = UrbanoXData.First(xd, "AG_LAB_DATAID");
                    if (masterGuid == null || dataId == null) continue;

                    labelCount++;

                    UrbanoEntityData target;
                    if (!map.TryGetValue(masterGuid, out target)) continue;

                    // Extract text from DBText or MText
                    string raw = null;
                    if (obj is DBText dt) raw = dt.TextString;
                    else if (obj is MText mt) raw = mt.Text;
                    if (raw == null) continue;

                    string val = CleanText(raw.Trim());
                    if (val.Length == 0) continue;

                    // Only fill if not already set by geometry / NOD (pass 1 & 2)
                    if (!target.Props.ContainsKey(dataId) ||
                         string.IsNullOrEmpty(target.Props[dataId]))
                    {
                        target.Props[dataId] = val;
                        matchCount++;
                    }
                }

                ed.WriteMessage($"  Found {labelCount} label entities, " +
                                $"matched {matchCount} properties to {map.Count} masters.\n");

                // ════════════════════════════════════════════════════════════════
                // OUTPUT
                // ════════════════════════════════════════════════════════════════
                string logPath = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    "UrbanoExtract.txt");

                using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
                {
                    w.WriteLine($"=== URBANO DATA EXTRACTION ===");
                    w.WriteLine($"DWG  : {db.Filename}");
                    w.WriteLine($"Date : {DateTime.Now}");
                    w.WriteLine($"Total: {map.Count} entities ({map.Values.Count(v => v.IsPipe)} pipes, " +
                                $"{map.Values.Count(v => v.IsManhole)} manholes)");
                    w.WriteLine();

                    foreach (var data in map.Values.OrderBy(d => d.IsPipe ? 0 : 1)
                                                    .ThenBy(d => d.Handle))
                    {
                        w.WriteLine($"{'─',60}");
                        w.WriteLine($"{(data.IsPipe ? "PIPE" : data.IsManhole ? "MANHOLE" : "OTHER"),8} " +
                                    $"Handle:{data.Handle,-8} GUID:{data.AgGuid}");
                        w.WriteLine($"  AcadType : {data.AcadType}");
                        w.WriteLine($"  Drawer   : {data.DrawerContent}");

                        if (data.IsPipe)
                        {
                            w.WriteLine($"  Material : {data.Material}");
                            w.WriteLine($"  Diam. DN : {data.DiameterNom} mm");
                            w.WriteLine($"  Length2D : {data.Length2D:F3} m");
                            w.WriteLine($"  Slope    : {data.SlopePermil:F2} ‰ (from geometry)");
                            if (data.StartPoint.HasValue)
                                w.WriteLine($"  Start    : ({data.StartPoint.Value.X:F2}, " +
                                            $"{data.StartPoint.Value.Y:F2}, {data.StartPoint.Value.Z:F2})");
                            if (data.EndPoint.HasValue)
                                w.WriteLine($"  End      : ({data.EndPoint.Value.X:F2}, " +
                                            $"{data.EndPoint.Value.Y:F2}, {data.EndPoint.Value.Z:F2})");
                        }
                        else if (data.IsManhole)
                        {
                            w.WriteLine($"  Name     : {data.EntityName}");
                            w.WriteLine($"  Terrain  : {data.TerrainElev}");
                            w.WriteLine($"  Invert   : {data.InvertElev}");
                            w.WriteLine($"  Depth    : {data.Depth:F2} m");
                            if (data.Center.HasValue)
                                w.WriteLine($"  Centre   : ({data.Center.Value.X:F2}, " +
                                            $"{data.Center.Value.Y:F2}, {data.Center.Value.Z:F2})");
                        }

                        if (data.Props.Count > 0)
                        {
                            w.WriteLine($"  All properties ({data.Props.Count}):");
                            foreach (var kv in data.Props.OrderBy(k => k.Key))
                                w.WriteLine($"    {kv.Key,-40} = {kv.Value}");
                        }
                        w.WriteLine();
                    }
                }

                // Quick summary to command line
                ed.WriteMessage($"\n{'═',60}\n");
                foreach (var data in map.Values)
                {
                    ed.WriteMessage(data.IsPipe
                        ? $"\n  PIPE     {data.Handle,-8} " +
                          $"DN{data.DiameterNom,-6} {data.Material,-8} L={data.Length2D:F2}m"
                        : $"\n  MANHOLE  {data.Handle,-8} " +
                          $"{data.EntityName,-12} ZK={data.TerrainElev} AK={data.InvertElev} h={data.Depth:F2}m");
                }
                ed.WriteMessage($"\n{'═',60}");
                ed.WriteMessage($"\nDetay: {logPath}\n");

                tr.Commit();
            }
        }

        // ─────────────────────────────────────────────────────────────────────────
        // COMMAND: ExtractUrbanoNOD
        //
        // Standalone diagnostic: dumps all Urbano NOD entries to Desktop.
        // Run this AFTER triggering Urbano's hydraulic analysis to confirm
        // whether ARSX_NETWORKTOPOLOGY has been populated.
        // ─────────────────────────────────────────────────────────────────────────
        [CommandMethod("ExtractUrbanoNOD")]
        public void ExtractUrbanoNOD()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "UrbanoNOD.txt");

            using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                w.WriteLine($"=== URBANO NOD DUMP ===  {DateTime.Now}");
                w.WriteLine();

                var nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead) as DBDictionary;

                foreach (string nodKey in UrbanoNod.KnownKeys)
                {
                    if (!nod.Contains(nodKey))
                    {
                        w.WriteLine($"[{nodKey}]  — not present in NOD");
                        continue;
                    }

                    var subDict = tr.GetObject(nod.GetAt(nodKey), OpenMode.ForRead) as DBDictionary;
                    if (subDict == null) { w.WriteLine($"[{nodKey}]  — not a dictionary"); continue; }

                    w.WriteLine($"[{nodKey}]  ({subDict.Count} sub-entries)");
                    foreach (DBDictionaryEntry sub in subDict)
                    {
                        var xrec = tr.GetObject(sub.Value, OpenMode.ForRead) as Xrecord;
                        if (xrec == null) { w.WriteLine($"  {sub.Key}: not an XRecord"); continue; }

                        // Read raw content
                        var bytes  = new List<byte>();
                        string str = null;
                        foreach (TypedValue tv in xrec.Data)
                        {
                            if (tv.TypeCode == 1) str = tv.Value as string;
                            else if (tv.TypeCode == 310 || tv.TypeCode == 311)
                            {
                                if (tv.Value is byte[] b) bytes.AddRange(b);
                            }
                        }

                        string content = null;
                        if (bytes.Count > 0)
                        {
                            try { content = Encoding.UTF8.GetString(bytes.ToArray()); } catch { }
                        }
                        content = content ?? str ?? "(empty)";

                        if (content == "nullData")
                        {
                            // Urbano hasn't committed this record yet
                            w.WriteLine($"  {sub.Key}: nullData  ← run Urbano analysis then save");
                        }
                        else
                        {
                            w.WriteLine($"  {sub.Key}: {content.Length} chars");
                            w.WriteLine(content.Length > 2000
                                ? content.Substring(0, 2000) + "\n  … (truncated)"
                                : content);
                        }
                        w.WriteLine();
                    }
                    w.WriteLine();
                }

                tr.Commit();
            }

            ed.WriteMessage($"\nNOD dump: {logPath}\n");
        }
    }
}
