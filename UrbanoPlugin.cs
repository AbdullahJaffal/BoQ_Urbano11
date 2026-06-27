using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UrbanoMetraj.BoQ.UI;

[assembly: ExtensionApplication(typeof(UrbanoMetraj.UrbanoPlugin))]
[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoCommands))]

namespace UrbanoMetraj
{
    // ── Extension application ─────────────────────────────────────────────────

    public class UrbanoPlugin : IExtensionApplication
    {
        public void Initialize()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                "\nUrbanoMetraj loaded.\n" +
                "  URBANO_BOQ       - Open BoQ export dialog (Excel output)\n" +
                "  URBANO_METRAJ    - Legacy label refresh + CSV BoQ\n" +
                "  URBANO_SCAN      - Scan all entities -> Desktop/UrbanoScan.txt\n");

            // ── Ribbon setup ─────────────────────────────────────────────────
            // If the ribbon is already initialised (common when reloading the DLL),
            // set it up immediately.  Otherwise subscribe to the ItemInitialized
            // event so we run as soon as the ribbon becomes available.
            if (ComponentManager.Ribbon != null)
            {
                RibbonSetup.Initialize();
            }
            else
            {
                ComponentManager.ItemInitialized += OnRibbonReady;
            }
        }

        private static void OnRibbonReady(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnRibbonReady;
            RibbonSetup.Initialize();
        }

        public void Terminate() { }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Data models
    // ═══════════════════════════════════════════════════════════════════════════

    class UrbanoObject
    {
        public string AgGuid;
        public string MasterHandle;
        public string AcadType;
        public string DrawerContent;
        public List<string> LabelHandles = new List<string>();
        public Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Geometry (read from master entity)
        public Point3d? StartPt;   // pipes: line start
        public Point3d? EndPt;     // pipes: line end
        public Point3d? Center;    // manholes: circle center
        public double   Radius;    // manholes: circle radius

        public bool IsPipe     => DrawerContent != null &&
            DrawerContent.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0;
        public bool IsManhole  => DrawerContent != null &&
            DrawerContent.IndexOf("realview", StringComparison.OrdinalIgnoreCase) >= 0;

        public string Prop(string key, string def = "")
        { string v; return Props.TryGetValue(key, out v) ? v : def; }
    }

    // ── Config models ─────────────────────────────────────────────────────────

    class KaziPoz    { public string PozNo, MalKodu, PozKodu, Aciklama, Birim; }
    class HendekDim  { public int CapMm; public double Genislik, Yataklama, BoruDisCap, OrtDerinlik; }
    class BoruPoz    { public string Malzeme, Cap, PozNo, MalKodu, PozKodu, Aciklama, Birim; }
    class BacaBileseni { public string PozNo, MalKodu, PozKodu, Aciklama, Birim, Tip; public double Yukseklik; }

    class BoQConfig
    {
        public List<KaziPoz>      KaziPozlar    = new List<KaziPoz>();
        public List<HendekDim>    Hendekler     = new List<HendekDim>();
        public List<BoruPoz>      BoruPozlar    = new List<BoruPoz>();
        public List<BacaBileseni> BacaBilesenleri = new List<BacaBileseni>();

        // Returns trench dims for given diameter (mm), or null if not found.
        public HendekDim GetHendek(int capMm)
        {
            // Try exact match first, then nearest smaller
            HendekDim best = null;
            foreach (var h in Hendekler)
            {
                if (h.CapMm == capMm) return h;
                if (h.CapMm <= capMm && (best == null || h.CapMm > best.CapMm)) best = h;
            }
            return best;
        }

        public BoruPoz GetBoruPoz(string malzeme, string cap)
        {
            foreach (var b in BoruPozlar)
                if (string.Equals(b.Malzeme, malzeme, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(b.Cap, cap, StringComparison.OrdinalIgnoreCase))
                    return b;
            return null;
        }
    }

    // ── BoQ line item ─────────────────────────────────────────────────────────

    class BoQRow
    {
        public string PozNo, MalKodu, PozKodu, Aciklama, Birim;
        public double Miktar;
        public bool IsHeader; // section/group header row (no quantity)
        public string HeaderText; // used when IsHeader=true

        public static BoQRow Header(string text) =>
            new BoQRow { IsHeader = true, HeaderText = text };
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // XData helper
    // ═══════════════════════════════════════════════════════════════════════════

    static class XData
    {
        public static Dictionary<string, List<TypedValue>> Read(DBObject obj)
        {
            var result = new Dictionary<string, List<TypedValue>>();
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

        public static string First(Dictionary<string, List<TypedValue>> xd, string app)
        {
            List<TypedValue> v;
            if (xd.TryGetValue(app, out v) && v.Count > 0) return v[0].Value?.ToString();
            return null;
        }

        public static List<string> All(Dictionary<string, List<TypedValue>> xd, string app)
        {
            List<TypedValue> v;
            if (xd.TryGetValue(app, out v)) return v.Select(t => t.Value?.ToString() ?? "").ToList();
            return new List<string>();
        }

        public static string DrawerGuid(Dictionary<string, List<TypedValue>> xd)
        {
            const string pfx = "DRAWER_ID.";
            foreach (string k in xd.Keys)
                if (k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                    return k.Substring(pfx.Length);
            return null;
        }

        public static string DrawerContent(Dictionary<string, List<TypedValue>> xd)
        {
            const string pfx = "DRAWER_ID.";
            foreach (string k in xd.Keys)
            {
                if (!k.StartsWith(pfx, StringComparison.OrdinalIgnoreCase)) continue;
                List<TypedValue> v;
                if (xd.TryGetValue(k, out v) && v.Count > 0)
                    return v[0].Value?.ToString() ?? k.Substring(pfx.Length);
                return k.Substring(pfx.Length);
            }
            return null;
        }
    }

    // ═══════════════════════════════════════════════════════════════════════════
    // Commands
    // ═══════════════════════════════════════════════════════════════════════════

    public class UrbanoCommands
    {
        // ── Utility: resolve hex handle ───────────────────────────────────────
        static ObjectId ResolveHandle(Database db, string hex)
        {
            try { return db.GetObjectId(false, new Handle(Convert.ToInt64(hex, 16)), 0); }
            catch { return ObjectId.Null; }
        }

        // ── Utility: strip AutoCAD text codes ────────────────────────────────
        static string CleanText(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            s = Regex.Replace(s, @"%%[cCdDpPoOuU]", "");
            s = Regex.Replace(s, @"\{\\[^;]+;([^}]*)\}", "$1");
            s = Regex.Replace(s, @"\\[PpNnLlOoKkFfHhQqWwBbIiSs][^;]*;?", " ");
            return s.Trim();
        }

        // ── Utility: parse first number from formatted text ───────────────────
        static double ParseNumber(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return 0;
            s = CleanText(s).Replace(',', '.');
            var sb = new StringBuilder();
            bool dot = false, started = false;
            foreach (char c in s)
            {
                if (c == '-' && !started) { sb.Append(c); started = true; }
                else if (char.IsDigit(c)) { sb.Append(c); started = true; }
                else if ((c == '.' || c == ',') && !dot) { sb.Append('.'); dot = true; started = true; }
                else if (started) break;
            }
            double r;
            return double.TryParse(sb.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out r) ? r : 0;
        }

        // ── Config reader ─────────────────────────────────────────────────────
        // CadAddinManager copies the DLL to a temp folder, so ConfigPath searches
        // several candidate locations:
        //   1. Same directory as the DLL  (works when loaded directly)
        //   2. Same directory as the open DWG  (project-specific config)
        //   3. Desktop                          (global fallback)
        static string ConfigPath
        {
            get
            {
                const string CFG = "UrbanoMetraj_Config.csv";

                // 1. Next to the assembly (bin\Debug\ or CadAddinManager temp)
                string dll = typeof(UrbanoCommands).Assembly.Location;
                string p = Path.Combine(Path.GetDirectoryName(dll), CFG);
                if (File.Exists(p)) return p;

                // 2. Next to the open DWG
                try
                {
                    var doc = Application.DocumentManager.MdiActiveDocument;
                    if (doc != null && !string.IsNullOrEmpty(doc.Database?.Filename))
                    {
                        p = Path.Combine(
                            Path.GetDirectoryName(doc.Database.Filename), CFG);
                        if (File.Exists(p)) return p;
                    }
                }
                catch { }

                // 3. Desktop
                p = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop), CFG);
                if (File.Exists(p)) return p;

                // Return DLL-relative path so the warning message names a useful target
                return Path.Combine(Path.GetDirectoryName(dll), CFG);
            }
        }

        static BoQConfig ReadConfig(Editor ed)
        {
            var cfg = new BoQConfig();
            if (!File.Exists(ConfigPath))
            {
                ed.WriteMessage("\nUYARI: Config dosyasi bulunamadi: " + ConfigPath + "\n");
                return cfg;
            }

            string section = "";
            bool headerSkipped = false;

            foreach (string rawLine in File.ReadAllLines(ConfigPath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line) || line.StartsWith("#")) continue;

                if (line.StartsWith("[") && line.EndsWith("]"))
                {
                    section = line.Substring(1, line.Length - 2).ToUpperInvariant();
                    headerSkipped = false;
                    continue;
                }

                // Skip column header row
                if (!headerSkipped)
                {
                    headerSkipped = true;
                    continue;
                }

                string[] f = line.Split(';');

                switch (section)
                {
                    case "KAZI":
                        if (f.Length >= 5 && f[0].Trim() != "")
                            cfg.KaziPozlar.Add(new KaziPoz {
                                PozNo    = f[0].Trim(), MalKodu  = f.Length > 1 ? f[1].Trim() : "",
                                PozKodu  = f.Length > 2 ? f[2].Trim() : "",
                                Aciklama = f.Length > 3 ? f[3].Trim() : "",
                                Birim    = f.Length > 4 ? f[4].Trim() : ""
                            });
                        break;

                    case "HENDEK":
                        if (f.Length >= 5 && f[0].Trim() != "")
                        {
                            int cap; double g, y, d, ort;
                            if (int.TryParse(f[0].Trim(), out cap))
                                cfg.Hendekler.Add(new HendekDim {
                                    CapMm      = cap,
                                    Genislik   = double.TryParse(f[1].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out g) ? g : 1.0,
                                    Yataklama  = double.TryParse(f[2].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out y) ? y : 0.1,
                                    BoruDisCap = double.TryParse(f[3].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out d) ? d : 0,
                                    OrtDerinlik= f.Length > 4 && double.TryParse(f[4].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out ort) ? ort : 3.0
                                });
                        }
                        break;

                    case "BORU":
                        if (f.Length >= 7 && f[0].Trim() != "")
                            cfg.BoruPozlar.Add(new BoruPoz {
                                Malzeme  = f[0].Trim(), Cap      = f[1].Trim(),
                                PozNo    = f[2].Trim(), MalKodu  = f[3].Trim(),
                                PozKodu  = f[4].Trim(), Aciklama = f[5].Trim(),
                                Birim    = f[6].Trim()
                            });
                        break;

                    case "BACA":
                        if (f.Length >= 7 && f[0].Trim() != "")
                        {
                            double h;
                            cfg.BacaBilesenleri.Add(new BacaBileseni {
                                PozNo    = f[0].Trim(), MalKodu  = f[1].Trim(),
                                PozKodu  = f[2].Trim(), Aciklama = f[3].Trim(),
                                Birim    = f[4].Trim(), Tip      = f[5].Trim().ToUpperInvariant(),
                                Yukseklik= double.TryParse(f[6].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out h) ? h : 0
                            });
                        }
                        break;
                }
            }

            ed.WriteMessage(string.Format(
                "Config yuklendi: {0} kazi, {1} hendek, {2} boru, {3} baca bileseni\n",
                cfg.KaziPozlar.Count, cfg.Hendekler.Count,
                cfg.BoruPozlar.Count, cfg.BacaBilesenleri.Count));
            return cfg;
        }

        // ── Scan all Urbano objects ───────────────────────────────────────────
        // Data source priority (highest to lowest):
        //   1. Line/Circle geometry  — always present, always exact
        //   2. ARSX_NETWORKTOPOLOGY  — Urbano's committed network XML (when present)
        //   3. __SA__.LD             — Urbano's main data record (when present)
        //   4. Label entities        — AG_LAB_ENTITY/AG_LAB_DATAID (GUID-based lookup)
        static List<UrbanoObject> ScanAll(Database db, Transaction tr)
        {
            var map = new Dictionary<string, UrbanoObject>(StringComparer.OrdinalIgnoreCase);

            var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

            // ── Pass 1: collect master entities + geometry ────────────────────
            foreach (ObjectId id in ms)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                var xd = XData.Read(obj);

                string guid = XData.First(xd, "AG_GUID");
                if (guid == null) continue;
                if (XData.DrawerGuid(xd) == null) continue;  // only masters

                UrbanoObject uo;
                if (!map.TryGetValue(guid, out uo))
                { uo = new UrbanoObject { AgGuid = guid }; map[guid] = uo; }

                uo.MasterHandle  = obj.Handle.ToString();
                uo.AcadType      = obj.GetRXClass().Name;
                uo.DrawerContent = XData.DrawerContent(xd);
                uo.LabelHandles  = XData.All(xd, "AG_LAB_HANDLES");

                Line   ln = obj as Line;
                Circle cr = obj as Circle;
                if (ln != null) { uo.StartPt = ln.StartPoint; uo.EndPt = ln.EndPoint; }
                if (cr != null) { uo.Center  = cr.Center;     uo.Radius = cr.Radius; }
            }

            // ── Pass 2 (geometry): set pipe length from Line geometry ─────────
            // This is always exact and doesn't depend on any data store.
            foreach (UrbanoObject uo in map.Values)
            {
                if (!uo.IsPipe) continue;
                if (!uo.StartPt.HasValue || !uo.EndPt.HasValue) continue;
                double dx  = uo.EndPt.Value.X - uo.StartPt.Value.X;
                double dy  = uo.EndPt.Value.Y - uo.StartPt.Value.Y;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len > 0.001)
                    uo.Props["SECTION_LENGTH_2D"] = len.ToString("F4", CultureInfo.InvariantCulture);

                // Slope from Z if line is 3D
                double dz = uo.EndPt.Value.Z - uo.StartPt.Value.Z;
                if (Math.Abs(dz) > 0.0001 && len > 0.001)
                {
                    double ratio = len / Math.Abs(dz);  // 1:X format
                    uo.Props["SECTION_SLOPE_RATIO_CALC"] = ratio.ToString("F1", CultureInfo.InvariantCulture);
                }
            }

            // ── Pass 3 (ARSX): try reading Urbano's committed network XML ─────
            // Reads ARSX_NETWORKTOPOLOGY and __SA__.LD from the Named Objects Dict.
            // These contain pipe/manhole properties when Urbano has committed data.
            TryReadArsx(db, tr, map);

            // ── Pass 4 (labels): GUID-based label entity scan ─────────────────
            // Reads AG_LAB_ENTITY/AG_LAB_DATAID labels — works even after Urbano
            // regenerates entity handles (GUID never changes).
            // Does NOT overwrite properties already set by geometry/ARSX.
            foreach (ObjectId id in ms)
            {
                DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                var xd = XData.Read(obj);

                string masterGuid = XData.First(xd, "AG_LAB_ENTITY");
                string dataId     = XData.First(xd, "AG_LAB_DATAID");
                if (masterGuid == null || dataId == null) continue;

                UrbanoObject uo;
                if (!map.TryGetValue(masterGuid, out uo)) continue;

                string text = null;
                DBText dt = obj as DBText;
                MText  mt = obj as MText;
                if (dt != null) text = dt.TextString;
                if (mt != null) text = mt.Text;
                if (text == null) continue;

                string val = CleanText(text.Trim());
                // Only write if not already populated (geometry/ARSX takes priority)
                if (!uo.Props.ContainsKey(dataId) && val != "")
                    uo.Props[dataId] = val;
                else if (uo.Props.ContainsKey(dataId) && uo.Props[dataId] == "" && val != "")
                    uo.Props[dataId] = val;
            }

            return map.Values.ToList();
        }

        // ── Try reading from Urbano's NOD ARSX entries (XML) ─────────────────
        static void TryReadArsx(Database db, Transaction tr,
                                 Dictionary<string, UrbanoObject> map)
        {
            try
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);

                // Keys to check, in order of preference
                var candidates = new[] { "ARSX_NETWORKTOPOLOGY", "ARSX_AUXTOPOLOGY", "__SA__" };

                foreach (string nodKey in candidates)
                {
                    if (!nod.Contains(nodKey)) continue;
                    var subDict = tr.GetObject(nod.GetAt(nodKey), OpenMode.ForRead) as DBDictionary;
                    if (subDict == null) continue;

                    foreach (DBDictionaryEntry entry in subDict)
                    {
                        string xml = ReadXRecordAsString(tr, entry.Value);
                        if (string.IsNullOrEmpty(xml) || xml == "nullData") continue;
                        ParseArsxXml(xml, map);
                    }
                }
            }
            catch { /* ARSX read failed silently */ }
        }

        static string ReadXRecordAsString(Transaction tr, ObjectId id)
        {
            var xr = tr.GetObject(id, OpenMode.ForRead) as Xrecord;
            if (xr == null) return null;
            var bytes = new List<byte>();
            string strVal = null;
            foreach (TypedValue tv in xr.Data)
            {
                if (tv.TypeCode == 1)
                {
                    string s = tv.Value as string;
                    if (!string.IsNullOrEmpty(s)) strVal = s;
                }
                else if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                {
                    byte[] b = tv.Value as byte[];
                    if (b != null) bytes.AddRange(b);
                }
            }
            if (bytes.Count > 0) return Encoding.UTF8.GetString(bytes.ToArray());
            return strVal;
        }

        // ── Generic ARSX XML parser ───────────────────────────────────────────
        // We don't know Urbano's exact XML schema until we see a populated drawing.
        // This searches every XML element for any attribute containing a known GUID,
        // then tries common attribute names for pipe/manhole properties.
        static void ParseArsxXml(string xml, Dictionary<string, UrbanoObject> map)
        {
            try
            {
                XDocument xdoc = XDocument.Parse(xml);

                foreach (UrbanoObject uo in map.Values)
                {
                    string guid = uo.AgGuid;
                    // Find any element whose attribute value matches this GUID
                    var matches = xdoc.Descendants()
                        .Where(el => el.Attributes()
                            .Any(a => string.Equals(a.Value, guid,
                                      StringComparison.OrdinalIgnoreCase)));

                    foreach (XElement el in matches)
                    {
                        if (uo.IsPipe)
                        {
                            ArxSetProp(uo, "PIPE_DIA_NOM",
                                el, "Diameter","Dia","DN","NominalDiameter","Cap","CapMm");
                            ArxSetProp(uo, "PIPE_MATERIAL",
                                el, "Material","Mat","PipeMat","Malzeme");
                            ArxSetProp(uo, "SECTION_SLOPE_RATIO",
                                el, "Slope","Egim","SlopeRatio","Grade");
                        }
                        else if (uo.IsManhole)
                        {
                            ArxSetProp(uo, "ENTITY_NAME",
                                el, "Name","ID","Label","Ad","Baca","ManholeID");
                            ArxSetProp(uo, "TERRAIN_ELEV_1",
                                el, "TerrainElev","Terrain","TZ","Surface","ZGround","ZeминKot");
                            ArxSetProp(uo, "LEVEL_LINE_ELEVATION_BI",
                                el, "Invert","InvertElev","IZ","AkarKot","FlowElev","Elevation");
                        }
                    }
                }
            }
            catch { /* XML parse failed silently */ }
        }

        // Set a prop from the first matching XML attribute, but don't overwrite.
        static void ArxSetProp(UrbanoObject uo, string propKey,
                                XElement el, params string[] attrNames)
        {
            if (uo.Props.ContainsKey(propKey) && !string.IsNullOrEmpty(uo.Props[propKey]))
                return;  // already set
            foreach (string name in attrNames)
            {
                XAttribute attr = el.Attribute(name);
                if (attr != null && !string.IsNullOrEmpty(attr.Value))
                {
                    uo.Props[propKey] = attr.Value;
                    return;
                }
            }
        }

        // ── Spatial: find nearest manhole to a point ──────────────────────────
        static UrbanoObject NearestManhole(List<UrbanoObject> manholes, Point3d pt, double tolerance = 5.0)
        {
            UrbanoObject best = null;
            double minD = double.MaxValue;
            foreach (UrbanoObject m in manholes)
            {
                if (!m.Center.HasValue) continue;
                double d = Math.Sqrt(
                    Math.Pow(pt.X - m.Center.Value.X, 2) +
                    Math.Pow(pt.Y - m.Center.Value.Y, 2));
                if (d < minD) { minD = d; best = m; }
            }
            return (best != null && minD <= tolerance) ? best : null;
        }

        // ── Manhole component count calculation ───────────────────────────────
        // Returns dict: TIP → count
        static Dictionary<string, int> CalcBacaBilesenleri(double depth, List<BacaBileseni> bilesenleri)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            double tabanH   = 0, konikH = 0, ayarH = 0, boyunH = 0;
            double govdeLH  = 0, govedeSH = 0;

            foreach (var b in bilesenleri)
            {
                switch (b.Tip)
                {
                    case "TABAN":   tabanH  = b.Yukseklik; break;
                    case "KONIK":   konikH  = b.Yukseklik; break;
                    case "AYAR":    ayarH   = b.Yukseklik; break;
                    case "BOYUN":   boyunH  = b.Yukseklik; break;
                    case "GOVDE_L": govdeLH = b.Yukseklik; break;
                    case "GOVDE_S": govedeSH= b.Yukseklik; break;
                }
            }

            double fixedH   = tabanH + konikH + ayarH + boyunH;  // ~1.60 m
            double govdeH   = Math.Max(0, depth - fixedH);

            int govdeLCount = 0, govdeSCount = 0;
            if (govdeLH > 0)
            {
                govdeLCount  = (int)Math.Floor(govdeH / govdeLH);
                double rem   = govdeH - govdeLCount * govdeLH;
                if (govedeSH > 0 && rem >= govedeSH * 0.5)
                    govdeSCount = 1;
            }

            foreach (var b in bilesenleri)
            {
                int cnt = 0;
                switch (b.Tip)
                {
                    case "TABAN":   cnt = 1;           break;
                    case "KONIK":   cnt = 1;           break;
                    case "AYAR":    cnt = 1;           break;
                    case "BOYUN":   cnt = 1;           break;
                    case "KAPAK":   cnt = 1;           break;
                    case "GOVDE_L": cnt = govdeLCount; break;
                    case "GOVDE_S": cnt = govdeSCount; break;
                }
                result[b.Tip] = cnt;
            }
            return result;
        }

        // ═══════════════════════════════════════════════════════════════════════
        // URBANO_METRAJ — main BoQ command
        // Queues: ARS_LABEL_N → ARS_LABEL_S → URBANO_METRAJ_EXEC
        // The two Urbano label-refresh commands regenerate all label entities
        // so that the subsequent scan reads up-to-date values.
        // ═══════════════════════════════════════════════════════════════════════
        [CommandMethod("URBANO_METRAJ")]
        public void ComputeBoQ()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            ed.WriteMessage("\nURBANO_METRAJ: Etiketler yenileniyor (ARS_LABEL_N + ARS_LABEL_S)...\n");
            // Queue label refresh first; AutoCAD executes queued commands in order
            // after this command returns.
            doc.SendStringToExecute("ARS_LABEL_N ", true, false, false);
            doc.SendStringToExecute("ARS_LABEL_S ", true, false, false);
            doc.SendStringToExecute("URBANO_METRAJ_EXEC ", true, false, false);
        }

        // URBANO_METRAJ_EXEC — runs after label refresh; does the actual scan+compute
        [CommandMethod("URBANO_METRAJ_EXEC")]
        public void ComputeBoQExec()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            ed.WriteMessage("\nURBANO_METRAJ_EXEC: Metraj hesaplaniyor...\n");
            try
            {
                ComputeBoQCore(doc, db, ed);
            }
            catch (System.Exception ex)
            {
                // Surface the real inner exception (CadAddinManager wraps it)
                System.Exception inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                ed.WriteMessage("\nHATA (" + inner.GetType().Name + "): " + inner.Message + "\n");
                string[] lines = (inner.StackTrace ?? "").Split('\n');
                if (lines.Length > 0) ed.WriteMessage("  " + lines[0].Trim() + "\n");
                if (lines.Length > 1) ed.WriteMessage("  " + lines[1].Trim() + "\n");
            }
        }

        void ComputeBoQCore(Document docArg, Database db, Editor ed)
        {
            // Load config
            BoQConfig cfg = ReadConfig(ed);

            // Scan drawing
            List<UrbanoObject> all;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                all = ScanAll(db, tr);
                tr.Commit();
            }

            var pipes    = all.Where(o => o.IsPipe).ToList();
            var manholes = all.Where(o => o.IsManhole).ToList();
            ed.WriteMessage(string.Format("{0} boru, {1} baca bulundu.\n", pipes.Count, manholes.Count));

            // Diagnostic: dump what was actually read for the first pipe and first manhole
            if (pipes.Count > 0)
            {
                var p0 = pipes[0];
                ed.WriteMessage(string.Format(
                    "  Boru[0]: MAT={0}  DN={1}  L={2}  Egim={3}\n",
                    p0.Prop("PIPE_MATERIAL","<bos>"), p0.Prop("PIPE_DIA_NOM","<bos>"),
                    p0.Prop("SECTION_LENGTH_2D","<bos>"), p0.Prop("SECTION_SLOPE_RATIO","<bos>")));
            }
            if (manholes.Count > 0)
            {
                var m0 = manholes[0];
                ed.WriteMessage(string.Format(
                    "  Baca[0]: AD={0}  ZK={1}  AK={2}  ({3} alan)\n",
                    m0.Prop("ENTITY_NAME","<bos>"), m0.Prop("TERRAIN_ELEV_1","<bos>"),
                    m0.Prop("LEVEL_LINE_ELEVATION_BI","<bos>"), m0.Props.Count));
            }

            // ── Average network depth (fallback when spatial lookup fails) ────
            double avgNetworkDepth = 3.0;
            var validDepths = manholes
                .Where(m => m.Prop("TERRAIN_ELEV_1") != "" && m.Prop("LEVEL_LINE_ELEVATION_BI") != "")
                .Select(m => ParseNumber(m.Prop("TERRAIN_ELEV_1")) - ParseNumber(m.Prop("LEVEL_LINE_ELEVATION_BI")))
                .Where(d => d > 0.5 && d < 30)
                .ToList();
            if (validDepths.Count > 0) avgNetworkDepth = validDepths.Average();

            // ═══════════════════════════════════════════════════════════════════
            // Calculate quantities
            // ═══════════════════════════════════════════════════════════════════

            // Pipe groups: (material × diameter) → length + pipe segments
            var pipeGroups = pipes
                .GroupBy(p => new {
                    Mat = p.Prop("PIPE_MATERIAL", "(bilinmiyor)"),
                    Cap = p.Prop("PIPE_DIA_NOM",  "(bilinmiyor)")
                })
                .Select(g => new {
                    g.Key.Mat, g.Key.Cap,
                    Segments  = g.ToList(),
                    Count     = g.Count(),
                    TotalLen  = g.Sum(p => ParseNumber(p.Prop("SECTION_LENGTH_2D", "0")))
                })
                .OrderBy(g => g.Mat).ThenBy(g => ParseNumber(g.Cap))
                .ToList();

            // Excavation totals per pipe group
            double totalKazi = 0, totalGeriDolgu = 0, totalYataklama = 0;

            var pipeCalcRows = pipeGroups.Select(g =>
            {
                int capMm;
                int.TryParse(g.Cap, out capMm);
                HendekDim hd = cfg.GetHendek(capMm);

                double genislik    = hd != null ? hd.Genislik    : 1.0;
                double yataklama   = hd != null ? hd.Yataklama   : 0.1;
                double boruDisCap  = hd != null ? hd.BoruDisCap  : 0;
                double defaultDep  = hd != null ? hd.OrtDerinlik : avgNetworkDepth;

                // For each pipe segment, find connected manhole depths
                double sumKazi = 0, sumYataklama = 0, sumGeriDolgu = 0;

                foreach (UrbanoObject pipe in g.Segments)
                {
                    double len = ParseNumber(pipe.Prop("SECTION_LENGTH_2D", "0"));

                    // Try spatial depth lookup
                    double depthStart = defaultDep, depthEnd = defaultDep;

                    if (pipe.StartPt.HasValue)
                    {
                        UrbanoObject mhStart = NearestManhole(manholes, pipe.StartPt.Value, 5.0);
                        if (mhStart != null)
                        {
                            double t = ParseNumber(mhStart.Prop("TERRAIN_ELEV_1", "0"));
                            double i = ParseNumber(mhStart.Prop("LEVEL_LINE_ELEVATION_BI", "0"));
                            double d = t - i;
                            if (d > 0.3 && d < 30) depthStart = d;
                        }
                    }

                    if (pipe.EndPt.HasValue)
                    {
                        UrbanoObject mhEnd = NearestManhole(manholes, pipe.EndPt.Value, 5.0);
                        if (mhEnd != null)
                        {
                            double t = ParseNumber(mhEnd.Prop("TERRAIN_ELEV_1", "0"));
                            double i = ParseNumber(mhEnd.Prop("LEVEL_LINE_ELEVATION_BI", "0"));
                            double d = t - i;
                            if (d > 0.3 && d < 30) depthEnd = d;
                        }
                    }

                    double avgDepth    = (depthStart + depthEnd) / 2.0;
                    double trenchDepth = avgDepth + yataklama; // excavation below invert
                    double areaKazi    = genislik * trenchDepth;
                    double areaYat     = genislik * yataklama;
                    double areaGeri    = areaKazi - areaYat - Math.PI * Math.Pow(boruDisCap / 2.0, 2);
                    if (areaGeri < 0) areaGeri = 0;

                    sumKazi       += len * areaKazi;
                    sumYataklama  += len * areaYat;
                    sumGeriDolgu  += len * areaGeri;
                }

                totalKazi       += sumKazi;
                totalGeriDolgu  += sumGeriDolgu;
                totalYataklama  += sumYataklama;

                return new {
                    g.Mat, g.Cap, g.Count, g.TotalLen,
                    Kazi = sumKazi, GeriDolgu = sumGeriDolgu, Yataklama = sumYataklama,
                    Hendek = hd
                };
            }).ToList();

            // Manhole component totals
            var bacaTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (BacaBileseni b in cfg.BacaBilesenleri) bacaTotals[b.Tip] = 0;

            var manholeCalcRows = manholes.Select(m =>
            {
                double terrain = ParseNumber(m.Prop("TERRAIN_ELEV_1", "0"));
                double invert  = ParseNumber(m.Prop("LEVEL_LINE_ELEVATION_BI", "0"));
                double depth   = (terrain != 0 || invert != 0) ? terrain - invert : avgNetworkDepth;
                if (depth < 0.5 || depth > 30) depth = avgNetworkDepth;
                var counts = CalcBacaBilesenleri(depth, cfg.BacaBilesenleri);
                foreach (var kv in counts)
                {
                    if (!bacaTotals.ContainsKey(kv.Key)) bacaTotals[kv.Key] = 0;
                    bacaTotals[kv.Key] += kv.Value;
                }
                return new { Name = m.Prop("ENTITY_NAME", "?"), Terrain = terrain, Invert = invert, Depth = depth, Counts = counts };
            }).OrderBy(m => m.Name).ToList();

            // ═══════════════════════════════════════════════════════════════════
            // Build BoQ rows
            // ═══════════════════════════════════════════════════════════════════
            var rows = new List<BoQRow>();

            // ── AS — header ──
            rows.Add(BoQRow.Header("AS"));
            rows.Add(BoQRow.Header("ATIKSU İŞLERİ"));

            // ── AS.01 — Kazı-Dolgu ──
            rows.Add(BoQRow.Header("AS.01"));
            rows.Add(BoQRow.Header("KAZI-DOLGU İŞLERİ"));

            rows.Add(BoQRow.Header("AS.01.01"));
            rows.Add(BoQRow.Header("KAZI İŞLERİ"));

            if (cfg.KaziPozlar.Count > 0)
            {
                var kazi = cfg.KaziPozlar[0];
                rows.Add(new BoQRow {
                    PozNo    = kazi.PozNo,    MalKodu  = kazi.MalKodu,
                    PozKodu  = kazi.PozKodu,  Aciklama = kazi.Aciklama,
                    Birim    = kazi.Birim,    Miktar   = totalKazi
                });
            }

            rows.Add(BoQRow.Header("AS.01.02"));
            rows.Add(BoQRow.Header("DOLGU İŞLERİ"));

            if (cfg.KaziPozlar.Count > 1)
            {
                var dolgu = cfg.KaziPozlar[1];
                rows.Add(new BoQRow {
                    PozNo    = dolgu.PozNo,   MalKodu  = dolgu.MalKodu,
                    PozKodu  = dolgu.PozKodu, Aciklama = dolgu.Aciklama,
                    Birim    = dolgu.Birim,   Miktar   = totalGeriDolgu
                });
            }

            if (cfg.KaziPozlar.Count > 2)
            {
                var yat = cfg.KaziPozlar[2];
                rows.Add(new BoQRow {
                    PozNo    = yat.PozNo,    MalKodu  = yat.MalKodu,
                    PozKodu  = yat.PozKodu,  Aciklama = yat.Aciklama,
                    Birim    = yat.Birim,    Miktar   = totalYataklama
                });
            }

            // ── AS.02 — Baca ──
            rows.Add(BoQRow.Header("AS.02"));
            rows.Add(BoQRow.Header("BACA İŞLERİ"));
            rows.Add(BoQRow.Header("AS.02.01"));
            rows.Add(BoQRow.Header("Ø1000 MM İÇ ÇAPLI BACALAR"));

            foreach (BacaBileseni b in cfg.BacaBilesenleri)
            {
                double qty = 0;
                bacaTotals.TryGetValue(b.Tip, out qty);
                rows.Add(new BoQRow {
                    PozNo    = b.PozNo,    MalKodu  = b.MalKodu,
                    PozKodu  = b.PozKodu,  Aciklama = b.Aciklama,
                    Birim    = b.Birim,    Miktar   = qty
                });
            }

            // ── AS.03 — Boru ──
            rows.Add(BoQRow.Header("AS.03"));
            rows.Add(BoQRow.Header("BORU İŞLERİ"));

            foreach (var g in pipeGroups)
            {
                BoruPoz poz = cfg.GetBoruPoz(g.Mat, g.Cap);
                if (poz != null)
                {
                    rows.Add(new BoQRow {
                        PozNo    = poz.PozNo,    MalKodu  = poz.MalKodu,
                        PozKodu  = poz.PozKodu,  Aciklama = poz.Aciklama,
                        Birim    = poz.Birim,    Miktar   = g.TotalLen
                    });
                }
                else
                {
                    // No config entry — output raw data so nothing is lost
                    rows.Add(new BoQRow {
                        PozNo    = "?",
                        MalKodu  = "",
                        PozKodu  = "",
                        Aciklama = string.Format("Ø{0}mm {1} boru dösenmesi [Config eksik!]", g.Cap, g.Mat),
                        Birim    = "müf",
                        Miktar   = g.TotalLen
                    });
                }
            }

            // ═══════════════════════════════════════════════════════════════════
            // Print to command line
            // ═══════════════════════════════════════════════════════════════════
            ed.WriteMessage("\n" + new string('=', 80) + "\n");
            ed.WriteMessage(string.Format("  {0,-14} {1,-8} {2,-6} {3,-38} {4,-6} {5}\n",
                "POZ NO", "MAL.KOD", "POZ KD", "İMALAT AÇIKLAMASI", "BİRİM", "MİKTAR"));
            ed.WriteMessage(new string('-', 80) + "\n");

            foreach (BoQRow r in rows)
            {
                if (r.IsHeader)
                {
                    ed.WriteMessage("\n  " + r.HeaderText + "\n");
                }
                else
                {
                    string desc = r.Aciklama.Length > 38 ? r.Aciklama.Substring(0, 35) + "..." : r.Aciklama;
                    ed.WriteMessage(string.Format("  {0,-14} {1,-8} {2,-6} {3,-38} {4,-6} {5:N2}\n",
                        r.PozNo, r.MalKodu, r.PozKodu, desc, r.Birim, r.Miktar));
                }
            }
            ed.WriteMessage(new string('=', 80) + "\n");

            // ═══════════════════════════════════════════════════════════════════
            // Export CSV (Excel compatible, UTF-8 BOM, semicolon)
            // ═══════════════════════════════════════════════════════════════════
            string folder = !string.IsNullOrEmpty(db.Filename)
                ? Path.GetDirectoryName(db.Filename)
                : Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

            string stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string csvPath = Path.Combine(folder, "UrbanoBoQ_" + stamp + ".csv");

            var csv = new StringBuilder();

            // BoQ table
            csv.AppendLine("POZ NO;MALZEME KODU;POZ KODU;İMALAT AÇIKLAMASI;BİRİM;MİKTAR");

            foreach (BoQRow r in rows)
            {
                if (r.IsHeader)
                    csv.AppendLine(string.Format("{0};;;;;" , r.HeaderText));
                else
                    csv.AppendLine(string.Format("{0};{1};{2};{3};{4};{5}",
                        r.PozNo, r.MalKodu, r.PozKodu,
                        r.Aciklama.Replace(";", " "),
                        r.Birim,
                        r.Miktar.ToString("F2", CultureInfo.InvariantCulture)));
            }

            csv.AppendLine();

            // Baca per-manhole detail
            csv.AppendLine("BACA DETAY");
            csv.AppendLine("Baca Adı;Zemin Kotu;Akar Kotu;Derinlik (m);Taban;Konik;Ayar;GövdeL;GövdeS;Boyun;Kapak");
            foreach (var m in manholeCalcRows)
            {
                int taban   = 0, konik  = 0, ayar = 0, govdeL = 0, govdeS = 0, boyun = 0, kapak = 0;
                m.Counts.TryGetValue("TABAN",   out taban);
                m.Counts.TryGetValue("KONIK",   out konik);
                m.Counts.TryGetValue("AYAR",    out ayar);
                m.Counts.TryGetValue("GOVDE_L", out govdeL);
                m.Counts.TryGetValue("GOVDE_S", out govdeS);
                m.Counts.TryGetValue("BOYUN",   out boyun);
                m.Counts.TryGetValue("KAPAK",   out kapak);
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0};{1:F2};{2:F2};{3:F2};{4};{5};{6};{7};{8};{9};{10}",
                    m.Name, m.Terrain, m.Invert, m.Depth,
                    taban, konik, ayar, govdeL, govdeS, boyun, kapak));
            }

            csv.AppendLine();

            // Pipe per-group excavation detail
            csv.AppendLine("BORU HENDEK DETAY");
            csv.AppendLine("Malzeme;Çap(mm);Kesit;Uzunluk(m);HendekGenişlik(m);OrtDerinlik(m);Kazı(m³);GeriDolgu(m³);Yataklama(m³)");
            foreach (var g in pipeCalcRows)
            {
                double avgD = g.Count > 0 ? (g.Kazi / Math.Max(g.TotalLen, 0.001) / (g.Hendek?.Genislik ?? 1.0)) : 0;
                csv.AppendLine(string.Format(CultureInfo.InvariantCulture,
                    "{0};{1};{2};{3:F2};{4:F2};{5:F2};{6:F2};{7:F2};{8:F2}",
                    g.Mat, g.Cap, g.Count, g.TotalLen,
                    g.Hendek?.Genislik ?? 0, avgD,
                    g.Kazi, g.GeriDolgu, g.Yataklama));
            }

            File.WriteAllText(csvPath, csv.ToString(), new UTF8Encoding(true));

            ed.WriteMessage("\nCSV kaydedildi:\n  " + csvPath + "\n");
        }

        // ── URBANO_ALANLARI ───────────────────────────────────────────────────
        [CommandMethod("URBANO_ALANLARI")]
        public void DiscoverFields()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            var pipeFields    = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var manholeFields = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var otherFields   = new SortedDictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                foreach (UrbanoObject uo in ScanAll(db, tr))
                {
                    SortedSet<string> target;
                    if      (uo.IsPipe)     target = pipeFields;
                    else if (uo.IsManhole)  target = manholeFields;
                    else
                    {
                        string dc = uo.DrawerContent ?? "?";
                        if (!otherFields.TryGetValue(dc, out target))
                        { target = new SortedSet<string>(StringComparer.OrdinalIgnoreCase); otherFields[dc] = target; }
                    }
                    foreach (string k in uo.Props.Keys) target.Add(k);
                }
                tr.Commit();
            }

            ed.WriteMessage("\n=== URBANO_ALANLARI ===\n\nBORU:\n");
            foreach (string f in pipeFields)   ed.WriteMessage("  " + f + "\n");
            ed.WriteMessage("\nBACA:\n");
            foreach (string f in manholeFields) ed.WriteMessage("  " + f + "\n");
            foreach (var kv in otherFields)
            {
                ed.WriteMessage("\n[" + kv.Key + "]:\n");
                foreach (string f in kv.Value) ed.WriteMessage("  " + f + "\n");
            }
            ed.WriteMessage("=======================\n");
        }

        // ── URBANO_OKU ────────────────────────────────────────────────────────
        [CommandMethod("URBANO_OKU")]
        public void ReadUrbanoObjects()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            List<UrbanoObject> all;
            using (Transaction tr = db.TransactionManager.StartTransaction())
            { all = ScanAll(db, tr); tr.Commit(); }

            var pipes    = all.Where(o => o.IsPipe).ToList();
            var manholes = all.Where(o => o.IsManhole).ToList();
            var others   = all.Where(o => !o.IsPipe && !o.IsManhole && o.DrawerContent != null).ToList();

            ed.WriteMessage(string.Format("\n=== URBANO_OKU ===\nToplam:{0}  Boru:{1}  Baca:{2}  Diger:{3}\n",
                all.Count, pipes.Count, manholes.Count, others.Count));

            ed.WriteMessage("\nBorular:\n");
            foreach (var p in pipes)
                ed.WriteMessage(string.Format("  {0} DN{1}  L={2}m  Egim={3}\n",
                    p.Prop("PIPE_MATERIAL","?"), p.Prop("PIPE_DIA_NOM","?"),
                    p.Prop("SECTION_LENGTH_2D","?"), p.Prop("SECTION_SLOPE_RATIO","?")));

            ed.WriteMessage("\nBacalar:\n");
            foreach (var m in manholes)
                ed.WriteMessage(string.Format("  {0,-16} ZK={1}  AK={2}\n",
                    m.Prop("ENTITY_NAME","?"),
                    m.Prop("TERRAIN_ELEV_1","?"),
                    m.Prop("LEVEL_LINE_ELEVATION_BI","?")));

            ed.WriteMessage("==================\n");
        }

        // ── URBANO_ETIKET ─────────────────────────────────────────────────────
        [CommandMethod("URBANO_ETIKET")]
        public void ShowObjectLabels()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            var opt = new PromptEntityOptions("\nBir Urbano nesnesi sec: ");
            PromptEntityResult res = ed.GetEntity(opt);
            if (res.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(res.ObjectId, OpenMode.ForRead);
                var xd = XData.Read(obj);
                string guid   = XData.First(xd, "AG_GUID");
                string drawer = XData.DrawerContent(xd);
                List<string> handles = XData.All(xd, "AG_LAB_HANDLES");

                ed.WriteMessage("\n=== URBANO_ETIKET ===\n");
                ed.WriteMessage("Tip    : " + obj.GetRXClass().Name + "\n");
                ed.WriteMessage("GUID   : " + (guid ?? "(yok)") + "\n");
                ed.WriteMessage("Drawer : " + (drawer ?? "(yok)") + "\n");
                ed.WriteMessage("Etiket : " + handles.Count + " adet\n");

                if (handles.Count == 0)
                { ed.WriteMessage("\nMaster entity'yi seciniz.\n"); tr.Commit(); return; }

                ed.WriteMessage("\n");
                foreach (string hStr in handles)
                {
                    ObjectId lid = ResolveHandle(db, hStr);
                    if (lid.IsNull) { ed.WriteMessage("  Handle:" + hStr + " → cozulemedi\n"); continue; }
                    DBObject lobj = tr.GetObject(lid, OpenMode.ForRead);
                    var lxd = XData.Read(lobj);
                    string dataId = XData.First(lxd, "AG_LAB_DATAID");
                    DBText txt    = lobj as DBText;
                    string raw    = txt?.TextString;
                    string clean  = raw != null ? CleanText(raw) : null;

                    ed.WriteMessage("  [" + lobj.GetRXClass().Name + "] Handle:" + hStr + "\n");
                    if (dataId != null)
                    {
                        ed.WriteMessage("    DATAID    : " + dataId + "\n");
                        ed.WriteMessage("    Ham metin : " + (raw   ?? "(bos)") + "\n");
                        ed.WriteMessage("    Temiz     : " + (clean ?? "(bos)") + "\n");
                    }
                    else ed.WriteMessage("    (geometrik eleman)\n");
                }
                tr.Commit();
            }
            ed.WriteMessage("====================\n");
        }

        // ── URBANO_DETAY ──────────────────────────────────────────────────────
        [CommandMethod("URBANO_DETAY")]
        public void ShowEntityDetail()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            var opt = new PromptEntityOptions("\nNesneyi sec: ");
            PromptEntityResult res = ed.GetEntity(opt);
            if (res.Status != PromptStatus.OK) return;

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                DBObject obj = tr.GetObject(res.ObjectId, OpenMode.ForRead);
                ed.WriteMessage("\n=== URBANO_DETAY: " + obj.GetRXClass().Name +
                    " Handle:" + obj.Handle + " ===\n");
                DBText txt = obj as DBText;
                if (txt != null) ed.WriteMessage("  TextString: " + txt.TextString + "\n");
                var xd = XData.Read(obj);
                foreach (var kv in xd)
                {
                    ed.WriteMessage("  [" + kv.Key + "]\n");
                    foreach (TypedValue tv in kv.Value)
                        ed.WriteMessage("    (" + tv.TypeCode + ") " + tv.Value + "\n");
                }
                tr.Commit();
            }
        }

        // ═══════════════════════════════════════════════════════════════════════
        // URBANO_BIN — Decode the BIN_LAY_ENT$ binary XRecords on every
        //              Urbano entity and dump as hex + attempted text.
        //              This reveals the actual stored property format.
        // ═══════════════════════════════════════════════════════════════════════
        [CommandMethod("URBANO_BIN")]
        public void DecodeBinary()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "UrbanoBinDump.txt");

            ed.WriteMessage("\nURBANO_BIN: Binary XRecord verisi cozumleniyor...\n");

            using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                w.WriteLine("=== URBANO BIN_LAY_ENT$ BINARY DUMP ===");
                w.WriteLine("Tarih: " + DateTime.Now);
                w.WriteLine();

                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                foreach (ObjectId id in ms)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    var xd = XData.Read(obj);

                    string guid       = XData.First(xd, "AG_GUID");
                    string topoGuid   = XData.First(xd, "TOPOGUID");
                    string drawerCont = XData.DrawerContent(xd);
                    string drawerGuid = XData.DrawerGuid(xd);
                    if (guid == null || obj.ExtensionDictionary.IsNull) continue;

                    // Find BIN_LAY_ENT$ entry in extension dictionary
                    var extDict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
                    string binKey = null;
                    foreach (DBDictionaryEntry e in extDict)
                        if (e.Key.StartsWith("BIN_LAY_ENT$")) { binKey = e.Key; break; }
                    if (binKey == null) continue;

                    var xrec = tr.GetObject(extDict.GetAt(binKey), OpenMode.ForRead) as Xrecord;
                    if (xrec == null) continue;

                    w.WriteLine("══════════════════════════════════════════════════");
                    w.WriteLine("Entity  : " + obj.GetRXClass().Name + " Handle:" + obj.Handle);
                    w.WriteLine("AG_GUID : " + guid);
                    w.WriteLine("TOPOGUID: " + (topoGuid ?? "(none)"));
                    w.WriteLine("Drawer  : " + (drawerCont ?? "(none)"));
                    w.WriteLine("BinKey  : " + binKey);
                    w.WriteLine();

                    // Collect all binary chunks
                    var allBytes = new List<byte>();
                    int chunkIdx = 0;
                    foreach (TypedValue tv in xrec.Data)
                    {
                        byte[] chunk = null;
                        if      (tv.TypeCode == (int)DxfCode.BinaryChunk)    chunk = tv.Value as byte[];
                        else if (tv.TypeCode == 311)                          chunk = tv.Value as byte[];

                        if (chunk != null)
                        {
                            allBytes.AddRange(chunk);
                            w.WriteLine("  Chunk[" + chunkIdx + "] " + chunk.Length + " bytes:");
                            // Hex dump — 16 bytes per line
                            for (int i = 0; i < chunk.Length; i += 16)
                            {
                                int len = Math.Min(16, chunk.Length - i);
                                string hex = "";
                                string asc = "";
                                for (int j = 0; j < len; j++)
                                {
                                    hex += chunk[i + j].ToString("X2") + " ";
                                    char c = (char)chunk[i + j];
                                    asc += (c >= 0x20 && c < 0x7F) ? c : '.';
                                }
                                w.WriteLine("    " + (i + 0).ToString("X4") + "  " + hex.PadRight(49) + " " + asc);
                            }
                            chunkIdx++;
                        }
                        else
                        {
                            w.WriteLine("  OtherTV: (" + tv.TypeCode + ") " + tv.Value);
                        }
                    }

                    // Try to interpret all bytes as UTF-8 text
                    byte[] all = allBytes.ToArray();
                    w.WriteLine();
                    w.WriteLine("  -- Full text attempt (UTF-8) --");
                    try
                    {
                        string txt = Encoding.UTF8.GetString(all);
                        // Print only printable + newlines
                        var sb2 = new StringBuilder();
                        foreach (char c in txt)
                            if (c >= 0x20 || c == '\n' || c == '\r' || c == '\t') sb2.Append(c);
                            else sb2.Append('·');
                        w.WriteLine(sb2.ToString());
                    }
                    catch { w.WriteLine("  (UTF-8 decode failed)"); }

                    // Try UTF-16 LE
                    w.WriteLine();
                    w.WriteLine("  -- Full text attempt (UTF-16 LE) --");
                    try
                    {
                        string txt = Encoding.Unicode.GetString(all);
                        var sb2 = new StringBuilder();
                        foreach (char c in txt)
                            if (c >= 0x20 || c == '\n' || c == '\r' || c == '\t') sb2.Append(c);
                            else if (c != '\0') sb2.Append('·');
                        w.WriteLine(sb2.ToString());
                    }
                    catch { w.WriteLine("  (UTF-16 decode failed)"); }

                    w.WriteLine();
                }

                // Also dump the ARSX_NETWORKTOPOLOGY, ARSX_DCT_PIPE etc. raw XML XRecords
                w.WriteLine("══════════════════════════════════════════════════");
                w.WriteLine("NOD ARSX entries raw dump");
                w.WriteLine("══════════════════════════════════════════════════");
                string[] arsxKeys = new[] {
                    "ARSX_NETWORKTOPOLOGY", "ARSX_AUXTOPOLOGY",
                    "ARSX_DCT_PIPE",        "ARSX_DCT_MANHOLE",
                    "ARSX_DCT_TRENCH",      "ARSX_LSINSTANCES",
                    "CALC_CONF_SEWAGE",     "__SA__"
                };

                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                foreach (string key in arsxKeys)
                {
                    if (!nod.Contains(key)) { w.WriteLine(key + ": (not found)"); continue; }
                    w.WriteLine(key + ":");
                    var sub = tr.GetObject(nod.GetAt(key), OpenMode.ForRead) as DBDictionary;
                    if (sub == null) { w.WriteLine("  (not a dictionary)"); continue; }
                    foreach (DBDictionaryEntry e in sub)
                    {
                        w.WriteLine("  " + e.Key + ":");
                        var xr = tr.GetObject(e.Value, OpenMode.ForRead) as Xrecord;
                        if (xr == null) { w.WriteLine("    (not an xrecord)"); continue; }
                        var allB = new List<byte>();
                        foreach (TypedValue tv in xr.Data)
                        {
                            if (tv.TypeCode == 1)
                            {
                                w.WriteLine("    (str) " + tv.Value);
                            }
                            else if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                            {
                                byte[] b = tv.Value as byte[];
                                if (b != null) allB.AddRange(b);
                            }
                            else
                            {
                                w.WriteLine("    (" + tv.TypeCode + ") " + tv.Value);
                            }
                        }
                        if (allB.Count > 0)
                        {
                            w.WriteLine("    Binary " + allB.Count + " bytes:");
                            // Try decode as UTF-8
                            string utfTxt = Encoding.UTF8.GetString(allB.ToArray());
                            var sb3 = new StringBuilder();
                            foreach (char c in utfTxt)
                                if (c >= 0x20 || c == '\n' || c == '\r') sb3.Append(c);
                            w.WriteLine(sb3.ToString());
                        }
                    }
                }

                tr.Commit();
            }

            ed.WriteMessage("TAMAMLANDI: " + logPath + "\n");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // URBANO_SCAN — refreshes labels then scans all entities to Desktop log
        // ═══════════════════════════════════════════════════════════════════════
        [CommandMethod("URBANO_SCAN")]
        public void ScanDump()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            ed.WriteMessage("\nURBANO_SCAN: Etiketler yenileniyor (ARS_LABEL_N + ARS_LABEL_S)...\n");
            doc.SendStringToExecute("ARS_LABEL_N ", true, false, false);
            doc.SendStringToExecute("ARS_LABEL_S ", true, false, false);
            doc.SendStringToExecute("URBANO_SCAN_EXEC ", true, false, false);
        }

        [CommandMethod("URBANO_SCAN_EXEC")]
        public void ScanAll2()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "UrbanoScan.txt");

            ed.WriteMessage("\nURBANO_SCAN: Tum entity'ler taranıyor...\n");

            using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                // ── Step 1: Build handle → entity map for the entire model space ──
                var handleMap = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ms)
                    handleMap[id.Handle.ToString()] = id;

                w.WriteLine("=== URBANO_SCAN: All model-space entities ===");
                w.WriteLine("Total entities: " + handleMap.Count);
                w.WriteLine();

                // ── Step 2: Find all Urbano master entities (have TOPOGUID or AG_GUID) ──
                var masters = new List<(ObjectId id, string guid, string drawer,
                                        List<string> labHandles, List<string> binHandles)>();

                foreach (ObjectId id in ms)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    var xd  = XData.Read(obj);
                    string guid = XData.First(xd, "AG_GUID");
                    if (guid == null) continue;
                    if (XData.DrawerGuid(xd) == null) continue; // only masters

                    // Parse BIN_LAY_ENT$ handles
                    var binHandles = new List<string>();
                    if (!obj.ExtensionDictionary.IsNull)
                    {
                        var extDict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
                        foreach (DBDictionaryEntry e in extDict)
                        {
                            if (!e.Key.StartsWith("BIN_LAY_ENT$")) continue;
                            var xrec = tr.GetObject(e.Value, OpenMode.ForRead) as Xrecord;
                            if (xrec == null) continue;
                            foreach (TypedValue tv in xrec.Data)
                            {
                                byte[] b = null;
                                if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                                    b = tv.Value as byte[];
                                if (b == null) continue;
                                string raw = Encoding.ASCII.GetString(b);
                                foreach (string h in raw.Split('#'))
                                    if (h.Trim() != "") binHandles.Add(h.Trim());
                            }
                        }
                    }

                    masters.Add((id, guid, XData.DrawerContent(xd),
                                 XData.All(xd, "AG_LAB_HANDLES"), binHandles));
                }

                w.WriteLine("Urbano master entities: " + masters.Count);
                w.WriteLine();

                // ── Step 3: For each master, dump it and all its children ──────
                foreach (var m in masters)
                {
                    DBObject masterObj = tr.GetObject(m.id, OpenMode.ForRead);
                    bool isPipe = m.drawer != null &&
                        m.drawer.IndexOf("pipe", StringComparison.OrdinalIgnoreCase) >= 0;

                    w.WriteLine("╔══════════════════════════════════════════════════");
                    w.WriteLine("║ " + (isPipe ? "PIPE" : "MANHOLE") +
                                " Handle:" + masterObj.Handle + " GUID:" + m.guid);
                    w.WriteLine("║ Drawer: " + (m.drawer ?? "?"));
                    w.WriteLine("╚══════════════════════════════════════════════════");

                    // Set of all related handles: bin children + lab handles + master
                    var allHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    allHandles.Add(masterObj.Handle.ToString());
                    foreach (string h in m.binHandles)    allHandles.Add(h);
                    foreach (string h in m.labHandles)    allHandles.Add(h);

                    w.WriteLine("  BIN children (" + m.binHandles.Count + "): " + string.Join(", ", m.binHandles));
                    w.WriteLine("  LAB handles  (" + m.labHandles.Count + "): " + string.Join(", ", m.labHandles));
                    w.WriteLine();

                    // Dump every related entity
                    foreach (string hStr in allHandles)
                    {
                        ObjectId childId = ResolveHandle(db, hStr);
                        if (childId.IsNull) { w.WriteLine("  [" + hStr + "] → NULL"); continue; }

                        DBObject child = tr.GetObject(childId, OpenMode.ForRead);
                        string cName = child.GetRXClass().Name;

                        // Get layer
                        string layer = "";
                        Entity ent = child as Entity;
                        if (ent != null) layer = ent.Layer;

                        // Get text content
                        string textContent = "";
                        DBText dt = child as DBText;
                        MText   mt = child as MText;
                        if (dt != null) textContent = dt.TextString ?? "";
                        if (mt != null) textContent = mt.Text ?? "";

                        w.WriteLine("  ── [" + hStr + "] " + cName + " Layer:" + layer);
                        if (!string.IsNullOrEmpty(textContent))
                            w.WriteLine("     TEXT: [" + textContent + "]");

                        // XData on this child
                        var cxd = XData.Read(child);
                        foreach (var kv in cxd)
                        {
                            w.Write("     XD[" + kv.Key + "]:");
                            foreach (TypedValue tv in kv.Value)
                                w.Write(" (" + tv.TypeCode + ")" + tv.Value);
                            w.WriteLine();
                        }

                        // Extension dict
                        if (!child.ExtensionDictionary.IsNull)
                        {
                            w.WriteLine("     ExtDict:");
                            var ed2 = (DBDictionary)tr.GetObject(child.ExtensionDictionary, OpenMode.ForRead);
                            foreach (DBDictionaryEntry e in ed2)
                            {
                                w.Write("       " + e.Key + ":");
                                var xr2 = tr.GetObject(e.Value, OpenMode.ForRead) as Xrecord;
                                if (xr2 != null)
                                {
                                    var allB = new List<byte>();
                                    foreach (TypedValue tv in xr2.Data)
                                    {
                                        if (tv.TypeCode == 1) w.Write(" (str)" + tv.Value);
                                        else if (tv.TypeCode == (int)DxfCode.BinaryChunk || tv.TypeCode == 311)
                                        {
                                            byte[] b = tv.Value as byte[];
                                            if (b != null) allB.AddRange(b);
                                        }
                                        else w.Write(" (" + tv.TypeCode + ")" + tv.Value);
                                    }
                                    if (allB.Count > 0)
                                        w.Write(" [BIN:" + Encoding.UTF8.GetString(allB.ToArray()).Replace('\0', '.') + "]");
                                }
                                w.WriteLine();
                            }
                        }
                    }
                    w.WriteLine();
                }

                // ── Step 4: All entities NOT in any Urbano object — by layer ──
                w.WriteLine("╔══════════════════════════════════════════════════");
                w.WriteLine("║ ALL OTHER ENTITIES (not belonging to any master)");
                w.WriteLine("╚══════════════════════════════════════════════════");

                // Build set of all handles accounted for
                var accountedHandles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in masters)
                {
                    accountedHandles.Add(m.id.Handle.ToString());
                    foreach (string h in m.binHandles) accountedHandles.Add(h);
                    foreach (string h in m.labHandles) accountedHandles.Add(h);
                }

                // Group unaccounted entities by layer
                var byLayer = new SortedDictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId id in ms)
                {
                    if (accountedHandles.Contains(id.Handle.ToString())) continue;
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    Entity ent = obj as Entity;
                    string layer = ent?.Layer ?? "?";
                    if (!byLayer.ContainsKey(layer)) byLayer[layer] = new List<string>();

                    string info = id.Handle + " [" + obj.GetRXClass().Name + "]";
                    DBText dt = obj as DBText;
                    if (dt != null && !string.IsNullOrEmpty(dt.TextString))
                        info += " TEXT:[" + dt.TextString + "]";
                    byLayer[layer].Add(info);
                }

                foreach (var kv in byLayer)
                {
                    // Only show Urbano-related layers
                    bool urban = kv.Key.StartsWith("ET1_", StringComparison.OrdinalIgnoreCase)
                              || kv.Key.StartsWith("ARSX", StringComparison.OrdinalIgnoreCase)
                              || kv.Key.StartsWith("AG_",  StringComparison.OrdinalIgnoreCase);
                    if (!urban && kv.Value.Count > 20) continue; // skip big generic layers

                    w.WriteLine("  Layer: " + kv.Key + " (" + kv.Value.Count + " entities)");
                    int shown = 0;
                    foreach (string s in kv.Value)
                    {
                        w.WriteLine("    " + s);
                        if (++shown >= 50) { w.WriteLine("    ... (" + (kv.Value.Count - shown) + " more)"); break; }
                    }
                }

                tr.Commit();
            }

            ed.WriteMessage("TAMAMLANDI: " + logPath + "\n");
        }

        // ═══════════════════════════════════════════════════════════════════════
        // URBANO_DERIN — Full database structure dump to Desktop log file
        // Explores: Named Objects Dict, Extension Dicts, Proxy objects, XRecords
        // This reveals how Urbano actually stores its data internally.
        // ═══════════════════════════════════════════════════════════════════════
        [CommandMethod("URBANO_DERIN")]
        public void DeepAnalysis()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "UrbanoDerinAnaliz.txt");

            ed.WriteMessage("\nURBANO_DERIN: Veritabani analiz ediliyor, bu biraz sürebilir...\n");
            ed.WriteMessage("Cikti: " + logPath + "\n");

            using (var w = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                w.WriteLine("=== URBANO DERIN ANALIZ ===");
                w.WriteLine("DWG: " + db.Filename);
                w.WriteLine("Tarih: " + DateTime.Now);
                w.WriteLine();

                // ── 1. Named Objects Dictionary (NOD) ─────────────────────────
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("1. NAMED OBJECTS DICTIONARY (NOD)");
                w.WriteLine("════════════════════════════════════════════════════");
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                DumpDict(w, db, tr, nod, "  ", 0);
                w.WriteLine();

                // ── 2. Model Space — extension dicts + XData on ALL entities ──
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("2. MODEL SPACE ENTITIES (extension dicts + XData)");
                w.WriteLine("════════════════════════════════════════════════════");
                var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForRead);
                var ms = (BlockTableRecord)tr.GetObject(bt[BlockTableRecord.ModelSpace], OpenMode.ForRead);

                int entityCount = 0;
                var proxyObjects = new List<string>();
                var uniqueAppNames = new SortedSet<string>();

                foreach (ObjectId id in ms)
                {
                    DBObject obj = tr.GetObject(id, OpenMode.ForRead);
                    string className = obj.GetRXClass().Name;
                    string dxfName  = obj.GetRXClass().DxfName ?? "";

                    // Collect proxy objects separately for summary
                    bool isProxy = dxfName.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0
                                || className.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0;

                    // Collect XData app names
                    using (ResultBuffer rb = obj.XData)
                    {
                        if (rb != null)
                        {
                            foreach (TypedValue tv in rb)
                                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                                    uniqueAppNames.Add(tv.Value.ToString());
                        }
                    }

                    // Only dump entities that:
                    //  (a) have extension dictionary, OR
                    //  (b) are proxy objects, OR
                    //  (c) have non-standard XData (not just AG_GUID etc.)
                    bool hasExtDict = !obj.ExtensionDictionary.IsNull;

                    if (isProxy || hasExtDict)
                    {
                        entityCount++;
                        w.WriteLine("─── Handle:" + obj.Handle +
                                    " Class:" + className +
                                    " DXF:" + dxfName + " ───");

                        if (isProxy)
                        {
                            proxyObjects.Add("Handle:" + obj.Handle + " " + className);
                            w.WriteLine("  *** PROXY OBJECT ***");
                        }

                        // XData dump
                        using (ResultBuffer rb = obj.XData)
                        {
                            if (rb != null)
                            {
                                w.WriteLine("  [XData]");
                                string app = null;
                                foreach (TypedValue tv in rb)
                                {
                                    if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                                    { app = tv.Value.ToString(); w.WriteLine("    APP: " + app); }
                                    else
                                        w.WriteLine("      (" + tv.TypeCode + ") " + tv.Value);
                                }
                            }
                        }

                        // Extension dictionary
                        if (hasExtDict)
                        {
                            w.WriteLine("  [ExtDict]");
                            var extDict = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
                            DumpDict(w, db, tr, extDict, "    ", 0);
                        }

                        w.WriteLine();
                    }
                }

                // ── 3. All XData application names found in model space ────────
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("3. ALL XDATA APP NAMES IN MODEL SPACE");
                w.WriteLine("════════════════════════════════════════════════════");
                foreach (string a in uniqueAppNames) w.WriteLine("  " + a);
                w.WriteLine();

                // ── 4. Proxy objects summary ──────────────────────────────────
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("4. PROXY OBJECTS SUMMARY");
                w.WriteLine("════════════════════════════════════════════════════");
                if (proxyObjects.Count == 0)
                    w.WriteLine("  (proxy nesne bulunamadi)");
                foreach (string p in proxyObjects) w.WriteLine("  " + p);
                w.WriteLine();

                // ── 5. Scan ALL blocks (not just model space) for Urbano data ─
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("5. BLOCK TABLE — ALL BLOCK DEFINITIONS");
                w.WriteLine("════════════════════════════════════════════════════");
                foreach (ObjectId btrId in bt)
                {
                    var btr = (BlockTableRecord)tr.GetObject(btrId, OpenMode.ForRead);
                    if (btr.IsLayout) continue; // skip model/paper space layouts already done
                    int cnt = 0;
                    foreach (ObjectId eid in btr)
                    {
                        DBObject obj = tr.GetObject(eid, OpenMode.ForRead);
                        using (ResultBuffer rb = obj.XData)
                        {
                            if (rb != null)
                            {
                                if (cnt == 0)
                                    w.WriteLine("  Block: " + btr.Name + " (" + btr.ObjectId + ")");
                                cnt++;
                                string appStr = "";
                                foreach (TypedValue tv in rb)
                                    if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                                        appStr += tv.Value + " ";
                                if (!string.IsNullOrEmpty(appStr))
                                    w.WriteLine("    Handle:" + obj.Handle + " [" + obj.GetRXClass().Name + "] apps: " + appStr);
                            }
                        }
                    }
                }
                w.WriteLine();

                // ── 6. Object Snap Tables (named objects that might store data) ─
                w.WriteLine("════════════════════════════════════════════════════");
                w.WriteLine("6. SYMBOL TABLE EXTENSIONS (LayerTable, etc.)");
                w.WriteLine("════════════════════════════════════════════════════");
                DumpTableExtDicts(w, db, tr, db.LayerTableId,    "LayerTable");
                DumpTableExtDicts(w, db, tr, db.LinetypeTableId, "LinetypeTable");
                DumpTableExtDicts(w, db, tr, db.RegAppTableId,   "RegAppTable");

                w.WriteLine();
                w.WriteLine("=== ANALIZ TAMAMLANDI ===");
                w.WriteLine("Model space entity sayisi (ExtDict veya Proxy): " + entityCount);
                w.WriteLine("Benzersiz XData app sayisi: " + uniqueAppNames.Count);
                w.WriteLine("Proxy nesne sayisi: " + proxyObjects.Count);

                tr.Commit();
            }

            ed.WriteMessage("TAMAMLANDI. Log dosyasi:\n  " + logPath + "\n");
            ed.WriteMessage("Dosyayi acmak icin: Not defteri veya VS Code ile acin.\n");
        }

        // ── Recursive dictionary dump ─────────────────────────────────────────
        static void DumpDict(StreamWriter w, Database db, Transaction tr,
                             DBDictionary dict, string indent, int depth)
        {
            if (depth > 8) { w.WriteLine(indent + "... (max depth reached)"); return; }

            foreach (DBDictionaryEntry entry in dict)
            {
                string key = entry.Key;
                ObjectId valId = entry.Value;
                if (valId.IsNull || !valId.IsValid) { w.WriteLine(indent + key + " [null/invalid]"); continue; }

                DBObject obj;
                try { obj = tr.GetObject(valId, OpenMode.ForRead); }
                catch (System.Exception ex) { w.WriteLine(indent + key + " [ERR: " + ex.Message + "]"); continue; }

                string className = obj.GetRXClass().Name;
                w.WriteLine(indent + key + "  [" + className + "]  Handle:" + obj.Handle);

                if (obj is DBDictionary subDict)
                {
                    DumpDict(w, db, tr, subDict, indent + "  ", depth + 1);
                }
                else if (obj is Xrecord xrec)
                {
                    foreach (TypedValue tv in xrec.Data)
                        w.WriteLine(indent + "  (" + tv.TypeCode + ") " + tv.Value);
                }
                else
                {
                    // For other object types, also dump their XData and extension dict
                    using (ResultBuffer rb = obj.XData)
                    {
                        if (rb != null)
                        {
                            w.WriteLine(indent + "  [XData]");
                            foreach (TypedValue tv in rb)
                            {
                                if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                                    w.WriteLine(indent + "    APP:" + tv.Value);
                                else
                                    w.WriteLine(indent + "    (" + tv.TypeCode + ") " + tv.Value);
                            }
                        }
                    }
                    if (!obj.ExtensionDictionary.IsNull)
                    {
                        w.WriteLine(indent + "  [ExtDict]");
                        var ed2 = (DBDictionary)tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead);
                        DumpDict(w, db, tr, ed2, indent + "    ", depth + 1);
                    }
                }
            }
        }

        // ── Dump extension dicts from symbol table entries ────────────────────
        static void DumpTableExtDicts(StreamWriter w, Database db, Transaction tr,
                                      ObjectId tableId, string tableName)
        {
            try
            {
                var table = (SymbolTable)tr.GetObject(tableId, OpenMode.ForRead);
                foreach (ObjectId id in table)
                {
                    DBObject rec = tr.GetObject(id, OpenMode.ForRead);
                    if (rec.ExtensionDictionary.IsNull) continue;
                    w.WriteLine("  " + tableName + ": " + ((SymbolTableRecord)rec).Name);
                    var ed2 = (DBDictionary)tr.GetObject(rec.ExtensionDictionary, OpenMode.ForRead);
                    DumpDict(w, db, tr, ed2, "    ", 0);
                }
            }
            catch (System.Exception ex)
            {
                w.WriteLine("  " + tableName + " [ERR: " + ex.Message + "]");
            }
        }
    }
}
