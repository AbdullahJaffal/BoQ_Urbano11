using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

// Tell AutoCAD to scan this class for [CommandMethod] attributes.
// Required because UrbanoPlugin.cs already uses [assembly: CommandClass(...)],
// which puts AutoCAD into "explicit" mode — only listed classes are scanned.
[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoDataMiner))]

namespace UrbanoMetraj
{
    /// <summary>
    /// AutoCAD command: URBANO_MINE
    ///
    /// Loads an Urbano topology XML and writes a complete raw-data dump to
    ///   %USERPROFILE%\Desktop\Urbano_Variables_Dump.txt
    ///
    /// Purpose: pure data extraction — NO BoQ maths.  The dump lets the user
    /// identify exactly which XML field holds each physical quantity.
    /// </summary>
    public class UrbanoDataMiner
    {
        // ─────────────────────────────────────────────────────────────────────
        // AutoCAD command entry point
        // ─────────────────────────────────────────────────────────────────────

        [CommandMethod("URBANO_MINE")]
        public void MineData()
        {
            var acDoc = Application.DocumentManager.MdiActiveDocument;
            if (acDoc == null) return;
            var ed = acDoc.Editor;

            ed.WriteMessage("\n╔══════════════════════════════════════════╗");
            ed.WriteMessage("\n║   URBANO RAW DATA MINER  (URBANO_MINE)   ║");
            ed.WriteMessage("\n╚══════════════════════════════════════════╝");

            var pathOpts = new PromptStringOptions(
                "\nEnter full path to Urbano export XML: ")
            {
                AllowSpaces = true,
                UseDefaultValue = false
            };
            var pathRes = ed.GetString(pathOpts);
            if (pathRes.Status != PromptStatus.OK) return;

            string xmlPath = pathRes.StringResult.Trim().Trim('"');
            if (!File.Exists(xmlPath))
            {
                ed.WriteMessage($"\n[ERROR] File not found: {xmlPath}");
                return;
            }

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Urbano_Variables_Dump.txt");

            try
            {
                ed.WriteMessage($"\n[MINE] Loading XML …");
                MinerCore.Run(xmlPath, outPath, ed);
                ed.WriteMessage($"\n[MINE] ✓ Dump saved → {outPath}");
            }
            catch (System.Exception ex)
            {
                ed.WriteMessage($"\n[ERROR] {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    // =========================================================================
    // Core miner — can also be called from unit tests / other commands
    // =========================================================================

    internal static class MinerCore
    {
        // ── Regexes ───────────────────────────────────────────────────────────
        private static readonly Regex HexFloatRx = new Regex(
            @"^-?0x[0-9A-Fa-f]+(?:\.[0-9A-Fa-f]+)?[pP][+\-]?\d+$",
            RegexOptions.Compiled);

        // =====================================================================
        public static void Run(string xmlPath, string outPath, Editor ed = null)
        {
            XDocument xdoc = LoadXml(xmlPath);

            // Catalog map:  GUID.upper → CatalogEntry
            var catMap = BuildCatalogMap(xdoc);
            Dbg(ed, $"\n[MINE] Catalog entries: {catMap.Count}");

            using (var sw = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                WriteHeader(sw, xmlPath, catMap);
                WriteSystemInfo(sw, xdoc);
                WriteLongitudinalInfo(sw, xdoc);
                WriteCatalogSummary(sw, catMap);
                MineNodes(sw, xdoc, catMap, ed);
                MineSections(sw, xdoc, catMap, ed);
            }
        }

        // =====================================================================
        // FILE HEADER
        // =====================================================================

        private static void WriteHeader(StreamWriter sw, string xmlPath,
            Dictionary<string, CatalogEntry> catMap)
        {
            var sep = new string('=', 72);
            sw.WriteLine(sep);
            sw.WriteLine("  URBANO RAW DATA MINER — Complete Variable Dump");
            sw.WriteLine($"  Source : {xmlPath}");
            sw.WriteLine($"  Created: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sw.WriteLine($"  Catalog entries indexed: {catMap.Count}");
            sw.WriteLine(sep);
            sw.WriteLine();
        }

        // =====================================================================
        // SYSTEM INFO
        // =====================================================================

        private static void WriteSystemInfo(StreamWriter sw, XDocument xdoc)
        {
            sw.WriteLine("══════ SYSTEMS ══════════════════════════════════════════════════════════");
            foreach (var gs in xdoc.Descendants("gisSystem"))
            {
                sw.WriteLine($"  id={gs.Attribute("id")?.Value ?? "?"}  " +
                             $"name={gs.Attribute("name")?.Value ?? "?"}");
            }
            sw.WriteLine();
        }

        // =====================================================================
        // LONGITUDINAL SECTION INFO
        // =====================================================================

        private static void WriteLongitudinalInfo(StreamWriter sw, XDocument xdoc)
        {
            sw.WriteLine("══════ LONGITUDINAL PROFILE METADATA ═══════════════════════════════════");

            foreach (var lsInst in xdoc.Descendants("lsInstance"))
            {
                string name  = lsInst.Attribute("name")?.Value ?? "";
                string desc  = lsInst.Attribute("description")?.Value ?? "";
                string refT  = lsInst.Attribute("refTerrain")?.Value ?? "";
                string scale = lsInst.Attribute("scale")?.Value ?? "";

                sw.WriteLine($"  lsInstance: name=\"{name}\"  description=\"{desc}\"");
                sw.WriteLine($"    refTerrain={refT}  scale={scale}");

                var seq = lsInst.Descendants("layoutSequence").FirstOrDefault();
                if (seq != null)
                {
                    string startSt = seq.Attribute("startSt")?.Value?.Trim() ?? "?";
                    string maxSt   = seq.Attribute("maxGrAxSt")?.Value?.Trim() ?? "?";
                    string seqVal  = seq.Attribute("seq")?.Value ?? "";
                    var guids = seqVal.Split(new[]{';'}, StringSplitOptions.RemoveEmptyEntries);

                    sw.WriteLine($"    Sequence start={startSt}  maxStation={maxSt}");
                    sw.WriteLine($"    Ordered GUIDs ({guids.Length}):");
                    for (int i = 0; i < guids.Length; i++)
                        sw.WriteLine($"      [{i + 1}] {guids[i].ToUpperInvariant()}");
                }
            }
            sw.WriteLine();
        }

        // =====================================================================
        // CATALOG SUMMARY
        // =====================================================================

        private static void WriteCatalogSummary(StreamWriter sw,
            Dictionary<string, CatalogEntry> catMap)
        {
            sw.WriteLine("══════ CATALOG SUMMARY ══════════════════════════════════════════════════");

            var byType = catMap.Values
                .GroupBy(e => e.CatalogType)
                .OrderBy(g => g.Key);

            foreach (var grp in byType)
            {
                sw.WriteLine($"\n  [{grp.Key}]  ({grp.Count()} entries)");
                foreach (var entry in grp.OrderBy(e => e.Name))
                {
                    sw.WriteLine($"    GUID: {entry.Guid}");
                    sw.WriteLine($"    Name: {entry.Name}");
                    foreach (var grpName in entry.Groups.Keys.OrderBy(k => k))
                    {
                        sw.WriteLine($"    ─── Group: {grpName}");
                        foreach (var kv in entry.Groups[grpName].OrderBy(p => p.Key))
                        {
                            string decoded = DecodeValue(kv.Value);
                            sw.WriteLine($"        {kv.Key,-28} = {decoded}");
                            if (decoded != kv.Value)
                                sw.WriteLine($"        {"",28}   [raw: {kv.Value}]");
                        }
                    }
                    sw.WriteLine();
                }
            }
        }

        // =====================================================================
        // NODE MINING
        // =====================================================================

        private static void MineNodes(StreamWriter sw, XDocument xdoc,
            Dictionary<string, CatalogEntry> catMap, Editor ed)
        {
            sw.WriteLine("══════ NODES (Manholes / Junctions) ════════════════════════════════════");

            var tplMain = FindMainTpl(xdoc);
            if (tplMain == null)
            {
                sw.WriteLine("  [WARNING] <main><tpl> element not found!");
                return;
            }

            var nodes = tplMain.Descendants("ns").Elements("n").ToList();
            sw.WriteLine($"  Total nodes found: {nodes.Count}");
            sw.WriteLine();

            int nodeIdx = 0;
            foreach (var nEl in nodes)
            {
                nodeIdx++;
                string guid = (nEl.Attribute("g")?.Value ?? "").ToUpperInvariant();
                string nt   = nEl.Attribute("nt")?.Value ?? "?";
                string h    = nEl.Attribute("h")?.Value ?? "?";

                // Read all topology properties
                var props = ReadTopoProps(nEl);
                string name    = DecodeStr(GetP(props, "AG_NAME"));
                string sysIdRaw= GetP(props, "AG_ID_SYSTEM");
                int    sysId   = DecodeInt(sysIdRaw);
                string mhRaw   = GetP(props, "MH");
                string mhGuid  = ExtractGuid(DecodeStr(mhRaw)).ToUpperInvariant();

                // Parse @pos
                string rawPos = nEl.Attribute("pos")?.Value ?? "";
                ParsePos3D(rawPos, out double px, out double py, out double pz);

                sw.WriteLine($"┌─ NODE [{nodeIdx}]: {name}");
                sw.WriteLine($"│  GUID        : {guid}");
                sw.WriteLine($"│  @h (handle) : {h}   @nt (node type): {nt}");
                sw.WriteLine($"│  System ID   : {sysId}");

                // ── Coordinates ─────────────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── @pos COORDINATES ──────────────────────────────────────────");
                sw.WriteLine($"│  raw pos     : {rawPos}");
                sw.WriteLine($"│  pos codepoints (first 50): [{DumpCodepoints(rawPos, 50)}]");
                sw.WriteLine($"│  X           : {px:F6}  m");
                sw.WriteLine($"│  Y           : {py:F6}  m");
                sw.WriteLine($"│  Z           : {pz:F6}  m");

                // ── All <ps><p> properties ───────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── TOPOLOGY PROPERTIES (<ps><p t v/>) ────────────────────────");
                foreach (var kv in props.OrderBy(p => p.Key))
                {
                    string decoded = DecodeValue(kv.Value);
                    sw.WriteLine($"│  {kv.Key,-20} = {decoded}");
                    if (decoded != kv.Value)
                        sw.WriteLine($"│  {"",20}   [raw: {kv.Value}]");
                }

                // ── Computed from standard keys ──────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── KEY DECODED VALUES ────────────────────────────────────────");
                double th1 = DecodeFloat(GetP(props, "TH1"));
                double nlr = DecodeFloat(GetP(props, "NLR"));
                double mhb = DecodeFloat(GetP(props, "MHB"));
                double mhbd= DecodeFloat(GetP(props, "MHBD"));
                double mhch= DecodeFloat(GetP(props, "MHCH"));
                sw.WriteLine($"│  TH1 (terrain elev)  : {th1:F6}  m");
                sw.WriteLine($"│  NLR (pipe inv level): {nlr:F6}  m");
                sw.WriteLine($"│  TH1 - NLR           : {th1 - nlr:F6}  m  ← candidate depth");
                sw.WriteLine($"│  NLR - TH1           : {nlr - th1:F6}  m  ← candidate depth (if NLR>TH1)");
                sw.WriteLine($"│  |NLR - TH1|         : {Math.Abs(nlr - th1):F6}  m  ← absolute depth");
                sw.WriteLine($"│  MHB  (base)         : {mhb:F6}  m");
                sw.WriteLine($"│  MHBD (base depth)   : {mhbd:F6}  m");
                sw.WriteLine($"│  MHCH (cone height)  : {mhch:F6}  m");

                // ── Manhole catalog ──────────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── MANHOLE CATALOG  (MH GUID: {mhGuid}) ──────────────────────");
                if (!string.IsNullOrEmpty(mhGuid) && catMap.ContainsKey(mhGuid))
                {
                    var entry = catMap[mhGuid];
                    sw.WriteLine($"│  Catalog type: {entry.CatalogType}");
                    sw.WriteLine($"│  Name        : {entry.Name}");
                    foreach (var grpName in entry.Groups.Keys.OrderBy(k => k))
                    {
                        sw.WriteLine($"│  ─── Group: {grpName}");
                        foreach (var kv in entry.Groups[grpName].OrderBy(p => p.Key))
                        {
                            string decoded = DecodeValue(kv.Value);
                            sw.WriteLine($"│      {kv.Key,-28} = {decoded}");
                            if (decoded != kv.Value)
                                sw.WriteLine($"│      {"",28}   [raw: {kv.Value}]");
                        }
                    }
                }
                else if (!string.IsNullOrEmpty(mhGuid))
                {
                    sw.WriteLine($"│  [NOT FOUND IN CATALOG]  mhRaw=\"{mhRaw}\"  guid=\"{mhGuid}\"");
                }
                else
                {
                    sw.WriteLine($"│  [No MH GUID on this node]");
                }

                // ── Connected sections (those referencing this node GUID) ────
                var connSections = xdoc
                    .Descendants("s")
                    .Where(s =>
                        ((string)s.Attribute("sn") ?? "").Equals(guid, StringComparison.OrdinalIgnoreCase) ||
                        ((string)s.Attribute("en") ?? "").Equals(guid, StringComparison.OrdinalIgnoreCase))
                    .ToList();

                sw.WriteLine($"│");
                sw.WriteLine($"│  ── CONNECTED SECTIONS ({connSections.Count}) ────────────────────────────");
                foreach (var cSec in connSections)
                {
                    string sg  = ((string)cSec.Attribute("g") ?? "").ToUpperInvariant();
                    string sn  = ((string)cSec.Attribute("sn") ?? "").ToUpperInvariant();
                    string en  = ((string)cSec.Attribute("en") ?? "").ToUpperInvariant();
                    bool isStart = sn.Equals(guid, StringComparison.OrdinalIgnoreCase);
                    var sp = ReadTopoProps(cSec);
                    string sName = DecodeStr(GetP(sp, "AG_NAME"));
                    double ll10 = DecodeFloat(GetP(sp, "LL10"));
                    double ll11 = DecodeFloat(GetP(sp, "LL11"));
                    sw.WriteLine($"│    Section {sName} ({sg})  " +
                                 $"role={(isStart ? "START" : "END")}  " +
                                 $"LL10={ll10:F4}  LL11={ll11:F4}");
                    double relevantLL = isStart ? ll10 : ll11;
                    sw.WriteLine($"│      → invert_offset_here = {relevantLL:F4} m");
                    sw.WriteLine($"│      → pipe_invert_abs    = TH1 + {relevantLL:F4} = {th1 + relevantLL:F4} m");
                    sw.WriteLine($"│      → depth_at_this_end  = TH1 - pipe_inv = {-relevantLL:F4} m");
                }

                sw.WriteLine($"└─────────────────────────────────────────────────────────────────");
                sw.WriteLine();

                Dbg(ed, $"\n[MINE]   Node {nodeIdx}/{nodes.Count}: {name}  X={px:F2} Y={py:F2} TH1={th1:F3} NLR={nlr:F3}");
            }
        }

        // =====================================================================
        // SECTION MINING
        // =====================================================================

        private static void MineSections(StreamWriter sw, XDocument xdoc,
            Dictionary<string, CatalogEntry> catMap, Editor ed)
        {
            sw.WriteLine("══════ SECTIONS (Pipes) ═════════════════════════════════════════════════");

            var tplMain = FindMainTpl(xdoc);
            if (tplMain == null)
            {
                sw.WriteLine("  [WARNING] <main><tpl> element not found!");
                return;
            }

            // We also need the node map to compute distances
            var nodeMap = BuildNodeCoordMap(tplMain);

            var sections = tplMain.Descendants("ss").Elements("s").ToList();
            sw.WriteLine($"  Total sections found: {sections.Count}");
            sw.WriteLine();

            int secIdx = 0;
            foreach (var sEl in sections)
            {
                secIdx++;
                string guid   = ((string)sEl.Attribute("g") ?? "").ToUpperInvariant();
                string snGuid = ((string)sEl.Attribute("sn") ?? "").ToUpperInvariant();
                string enGuid = ((string)sEl.Attribute("en") ?? "").ToUpperInvariant();
                string h      = sEl.Attribute("h")?.Value ?? "?";

                var props = ReadTopoProps(sEl);
                string name    = DecodeStr(GetP(props, "AG_NAME"));
                string sysIdRaw= GetP(props, "AG_ID_SYSTEM");
                int    sysId   = DecodeInt(sysIdRaw);

                double ll10    = DecodeFloat(GetP(props, "LL10"));
                double ll11    = DecodeFloat(GetP(props, "LL11"));
                double slp     = DecodeFloat(GetP(props, "SLP"));
                string pprRaw  = GetP(props, "PPR");
                string trncRaw = GetP(props, "TRNC");
                string pprGuid = ExtractGuid(DecodeStr(pprRaw)).ToUpperInvariant();
                string trncGuid= ExtractGuid(DecodeStr(trncRaw)).ToUpperInvariant();

                // Distances from node coordinates
                bool hasCoords = false;
                double dist2D = 0, dist3D = 0, dz = 0;
                if (nodeMap.ContainsKey(snGuid) && nodeMap.ContainsKey(enGuid))
                {
                    var sn = nodeMap[snGuid];
                    var en = nodeMap[enGuid];
                    double dx = en.X - sn.X;
                    double dy = en.Y - sn.Y;
                    dist2D = Math.Sqrt(dx * dx + dy * dy);
                    double invertSn = sn.TH1 + ll10;
                    double invertEn = en.TH1 + ll11;
                    dz = invertEn - invertSn;
                    dist3D = Math.Sqrt(dist2D * dist2D + dz * dz);
                    hasCoords = true;
                }

                sw.WriteLine($"┌─ SECTION [{secIdx}]: {name}");
                sw.WriteLine($"│  GUID        : {guid}");
                sw.WriteLine($"│  @h (handle) : {h}");
                sw.WriteLine($"│  System ID   : {sysId}");
                sw.WriteLine($"│  Start node  : {snGuid}");
                sw.WriteLine($"│  End node    : {enGuid}");

                // ── Computed geometry ────────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── COMPUTED GEOMETRY ─────────────────────────────────────────");
                if (hasCoords)
                {
                    var sn = nodeMap[snGuid];
                    var en = nodeMap[enGuid];
                    sw.WriteLine($"│  Start coords: X={sn.X:F4}  Y={sn.Y:F4}  TH1={sn.TH1:F4}");
                    sw.WriteLine($"│  End coords  : X={en.X:F4}  Y={en.Y:F4}  TH1={en.TH1:F4}");
                    sw.WriteLine($"│  dX={en.X - sn.X:F4}  dY={en.Y - sn.Y:F4}");
                    sw.WriteLine($"│  dist2D (horiz length from coords) : {dist2D:F4}  m  ← ACTUAL 2D LENGTH");
                    sw.WriteLine($"│  Invert at start (TH1+LL10)        : {sn.TH1 + ll10:F4}  m");
                    sw.WriteLine($"│  Invert at end   (TH1+LL11)        : {en.TH1 + ll11:F4}  m");
                    sw.WriteLine($"│  dZ (invert diff)                  : {dz:F4}  m");
                    sw.WriteLine($"│  dist3D = sqrt(2D²+dZ²)            : {dist3D:F4}  m  ← ACTUAL 3D LENGTH");
                    sw.WriteLine($"│  SLP (stored slope ‰)              : {slp:F4}  ‰");
                    if (dist2D > 0)
                        sw.WriteLine($"│  Computed slope from invert diff   : {Math.Abs(dz) / dist2D * 1000:F4}  ‰");
                }
                else
                {
                    sw.WriteLine($"│  [Node coordinates not available for distance calculation]");
                }

                // ── All <ps><p> properties ────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── TOPOLOGY PROPERTIES (<ps><p t v/>) ────────────────────────");
                foreach (var kv in props.OrderBy(p => p.Key))
                {
                    string decoded = DecodeValue(kv.Value);
                    sw.WriteLine($"│  {kv.Key,-20} = {decoded}");
                    if (decoded != kv.Value)
                        sw.WriteLine($"│  {"",20}   [raw: {kv.Value}]");
                }

                // ── Pipe catalog ──────────────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── PIPE PROFILE CATALOG  (PPR GUID: {pprGuid}) ────────────────");
                WriteCatalogEntry(sw, catMap, pprGuid, pprRaw);

                // ── Trench catalog ────────────────────────────────────────────
                sw.WriteLine($"│");
                sw.WriteLine($"│  ── TRENCH CATALOG  (TRNC GUID: {trncGuid}) ───────────────────");
                WriteCatalogEntry(sw, catMap, trncGuid, trncRaw);

                sw.WriteLine($"└─────────────────────────────────────────────────────────────────");
                sw.WriteLine();

                Dbg(ed, $"\n[MINE]   Section {secIdx}/{sections.Count}: {name}  " +
                        $"L2D={dist2D:F2}m  L3D={dist3D:F2}m  SLP={slp:F2}‰");
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void WriteCatalogEntry(StreamWriter sw,
            Dictionary<string, CatalogEntry> catMap, string guid, string rawRef)
        {
            if (string.IsNullOrEmpty(guid))
            {
                sw.WriteLine($"│  [No GUID]  rawRef=\"{rawRef}\"");
                return;
            }
            if (!catMap.ContainsKey(guid))
            {
                sw.WriteLine($"│  [NOT FOUND IN CATALOG]  guid=\"{guid}\"  rawRef=\"{rawRef}\"");
                return;
            }
            var entry = catMap[guid];
            sw.WriteLine($"│  Catalog type: {entry.CatalogType}");
            sw.WriteLine($"│  Name        : {entry.Name}");
            foreach (var grpName in entry.Groups.Keys.OrderBy(k => k))
            {
                sw.WriteLine($"│  ─── Group: {grpName}");
                foreach (var kv in entry.Groups[grpName].OrderBy(p => p.Key))
                {
                    string decoded = DecodeValue(kv.Value);
                    sw.WriteLine($"│      {kv.Key,-28} = {decoded}");
                    if (decoded != kv.Value)
                        sw.WriteLine($"│      {"",28}   [raw: {kv.Value}]");
                }
            }
        }

        // ── Catalog builder ───────────────────────────────────────────────────

        private sealed class CatalogEntry
        {
            public string Guid        { get; set; }
            public string CatalogType { get; set; }   // PIPE / TRENCH / MANHOLE / …
            public string Name        { get; set; }
            // group-name → (property-key → raw-value)
            public Dictionary<string, Dictionary<string, string>> Groups { get; set; }
                = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        }

        private static Dictionary<string, CatalogEntry> BuildCatalogMap(XDocument xdoc)
        {
            var map = new Dictionary<string, CatalogEntry>(
                StringComparer.OrdinalIgnoreCase);

            // Each <catalog reference="TYPE"> contains <catalogItem guid="...">
            foreach (var cat in xdoc.Descendants("catalog"))
            {
                string catType = cat.Attribute("reference")?.Value ?? "UNKNOWN";

                foreach (var item in cat.Elements("catalogItem"))
                {
                    string guid = item.Attribute("guid")?.Value ?? "";
                    if (string.IsNullOrEmpty(guid) || guid == "SYS") continue;
                    string guidUpper = guid.ToUpperInvariant();

                    if (!map.ContainsKey(guidUpper))
                    {
                        map[guidUpper] = new CatalogEntry
                        {
                            Guid        = guidUpper,
                            CatalogType = catType
                        };
                    }

                    var entry = map[guidUpper];

                    // Get the display name from multiple possible locations
                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        // Try direct attribute first
                        string n = item.Attribute("name")?.Value ?? "";
                        if (string.IsNullOrEmpty(n))
                        {
                            // Then try to find CATALOGITEM_NAME pEx inside any ct
                            var namePex = item.Descendants("pEx")
                                .FirstOrDefault(p =>
                                    ((string)p.Attribute("t") ?? "") == "CATALOGITEM_NAME");
                            if (namePex != null)
                                n = DecodeStr((string)namePex.Attribute("v") ?? "");
                        }
                        entry.Name = n;
                    }

                    // All ppsEx → ct groups
                    foreach (var ppsEx in item.Elements("ppsEx"))
                    {
                        foreach (var ct in ppsEx.Elements("ct"))
                        {
                            string grpId   = ct.Attribute("id")?.Value ?? "";
                            string grpName = ct.Attribute("name")?.Value ?? grpId;
                            if (string.IsNullOrEmpty(grpName))
                                grpName = "(default)";

                            if (!entry.Groups.ContainsKey(grpName))
                                entry.Groups[grpName] = new Dictionary<string, string>(
                                    StringComparer.OrdinalIgnoreCase);

                            var grpDict = entry.Groups[grpName];
                            foreach (var pEx in ct.Elements("pEx"))
                            {
                                string t = (string)pEx.Attribute("t") ?? "";
                                string v = (string)pEx.Attribute("v") ?? "";
                                if (!string.IsNullOrEmpty(t) && !grpDict.ContainsKey(t))
                                    grpDict[t] = v;
                            }
                        }
                    }

                    // Also store item-level @attributes (except guid itself)
                    const string itemAttGrp = "@item_attributes";
                    if (!entry.Groups.ContainsKey(itemAttGrp))
                        entry.Groups[itemAttGrp] = new Dictionary<string, string>(
                            StringComparer.OrdinalIgnoreCase);
                    var attGrp = entry.Groups[itemAttGrp];
                    foreach (var attr in item.Attributes())
                    {
                        string k = "@" + attr.Name.LocalName;
                        if (!attGrp.ContainsKey(k))
                            attGrp[k] = attr.Value;
                    }
                }
            }
            return map;
        }

        // ── Node coordinate map ───────────────────────────────────────────────

        private sealed class NodeCoords
        {
            public double X    { get; set; }
            public double Y    { get; set; }
            public double Z    { get; set; }
            public double TH1  { get; set; }  // terrain elevation
            public double NLR  { get; set; }  // pipe invert reference
        }

        private static Dictionary<string, NodeCoords> BuildNodeCoordMap(XElement tplMain)
        {
            var map = new Dictionary<string, NodeCoords>(StringComparer.OrdinalIgnoreCase);
            foreach (var nEl in tplMain.Descendants("ns").Elements("n"))
            {
                string guid = ((string)nEl.Attribute("g") ?? "").ToUpperInvariant();
                if (string.IsNullOrEmpty(guid)) continue;
                var props = ReadTopoProps(nEl);
                ParsePos3D(nEl.Attribute("pos")?.Value ?? "", out double x, out double y, out double z);
                map[guid] = new NodeCoords
                {
                    X   = x,
                    Y   = y,
                    Z   = z,
                    TH1 = DecodeFloat(GetP(props, "TH1")),
                    NLR = DecodeFloat(GetP(props, "NLR"))
                };
            }
            return map;
        }

        // ── XML navigation ────────────────────────────────────────────────────

        private static XElement FindMainTpl(XDocument doc)
            => doc.Descendants("topology")
                  .Descendants("networkTopology")
                  .Descendants("main")
                  .Descendants("tpl")
                  .FirstOrDefault();

        private static Dictionary<string, string> ReadTopoProps(XElement el)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.Elements("ps").Elements("p"))
            {
                string t = (string)p.Attribute("t") ?? "";
                string v = (string)p.Attribute("v") ?? "";
                if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t))
                    d[t] = v;
            }
            return d;
        }

        private static string GetP(Dictionary<string, string> d, string key)
            => (d != null && d.ContainsKey(key)) ? d[key] : "";

        // ── Coordinate parser ─────────────────────────────────────────────────

        private static void ParsePos3D(string raw,
            out double x, out double y, out double z)
        {
            x = 0; y = 0; z = 0;
            if (string.IsNullOrEmpty(raw)) return;

            // Strip "8005" type prefix
            string s = raw.StartsWith("8005", StringComparison.Ordinal)
                ? raw.Substring(4) : raw;

            // Also try "5009" prefix (alternative coordinate type seen in lsInstance)
            if (s.StartsWith("5009", StringComparison.Ordinal))
                s = s.Substring(4);

            var nums = ExtractDecimalNumbers(s);
            if (nums.Count >= 1) x = nums[0];
            if (nums.Count >= 2) y = nums[1];
            if (nums.Count >= 3) z = nums[2];
        }

        /// <summary>
        /// Extract all decimal numbers from a string that may contain
        /// arbitrary separator characters (U+FE09, quoted-printable =EF=BE=89, etc.)
        ///
        /// Strategy: walk char-by-char; start a number on '-' (if followed by digit) or digit;
        /// collect digits, '.', 'e'/'E' (only if followed by sign+digit for exponent).
        /// After each number candidate, we require the NEXT char to be a non-digit
        /// (prevents merging "89" separator tail with "4801.6587").
        /// </summary>
        private static List<double> ExtractDecimalNumbers(string s)
        {
            var result = new List<double>();
            int i = 0;
            int len = s.Length;

            while (i < len && result.Count < 10)
            {
                char c = s[i];

                // Start condition: digit or '-' followed by digit
                bool isDigit = char.IsDigit(c);
                bool isMinus = c == '-' && i + 1 < len && char.IsDigit(s[i + 1]);

                if (!isDigit && !isMinus) { i++; continue; }

                // Collect this number token
                int start = i;
                if (isMinus) i++;  // consume '-'
                while (i < len && char.IsDigit(s[i])) i++;  // integer part
                if (i < len && s[i] == '.')                  // optional '.'
                {
                    i++;
                    while (i < len && char.IsDigit(s[i])) i++; // fractional
                }
                // Optional exponent: 'e'/'E' followed by optional sign and digits
                if (i < len && (s[i] == 'e' || s[i] == 'E'))
                {
                    int e0 = i;
                    i++;
                    if (i < len && (s[i] == '+' || s[i] == '-')) i++;
                    if (i < len && char.IsDigit(s[i]))
                    {
                        while (i < len && char.IsDigit(s[i])) i++;
                    }
                    else
                    {
                        i = e0; // rollback — 'E' wasn't followed by sign+digit
                    }
                }

                string token = s.Substring(start, i - start);
                if (double.TryParse(token, NumberStyles.Float,
                        CultureInfo.InvariantCulture, out double v))
                    result.Add(v);
                // else skip this malformed token
            }
            return result;
        }

        // ── Value decoder ─────────────────────────────────────────────────────

        /// <summary>
        /// Decode an Urbano property value string.
        /// Returns a human-readable string representation.
        /// </summary>
        private static string DecodeValue(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "(empty)";

            if (raw.StartsWith("5003", StringComparison.Ordinal))
            {
                string rest = raw.Substring(4);
                return int.TryParse(rest, out int iv)
                    ? $"{iv}  [int/enum]"
                    : $"{rest}  [5003-string]";
            }
            if (raw.StartsWith("5005", StringComparison.Ordinal))
            {
                return $"\"{raw.Substring(4)}\"  [string]";
            }
            if (raw.StartsWith("8001", StringComparison.Ordinal))
            {
                string rest = raw.Substring(4);
                double fv = TryParseFloat(rest);
                return $"{fv:G10}  [float]";
            }
            if (raw.StartsWith("8005", StringComparison.Ordinal) ||
                raw.StartsWith("5009", StringComparison.Ordinal))
            {
                ParsePos3D(raw, out double x, out double y, out double z);
                return $"X={x:F6}  Y={y:F6}  Z={z:F6}  [3D coord]";
            }

            // Unknown prefix — try numeric, then string
            if (double.TryParse(raw, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double dv))
                return $"{dv:G10}  [raw-float]";

            return raw;  // return as-is
        }

        private static double DecodeFloat(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = raw.StartsWith("8001") ? raw.Substring(4)
                     : raw.StartsWith("8005") ? raw.Substring(4)
                     : raw;
            return TryParseFloat(s);
        }

        private static double TryParseFloat(string s)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            if (HexFloatRx.IsMatch(s)) return DecodeHexFloat(s);
            return double.TryParse(s, NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private static double DecodeHexFloat(string hex)
        {
            try
            {
                bool neg = hex.StartsWith("-");
                string s = neg ? hex.Substring(1) : hex;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);
                int pi = s.IndexOfAny(new[] { 'p', 'P' });
                if (pi < 0) return 0;
                int exp = int.Parse(s.Substring(pi + 1),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                string[] parts = s.Substring(0, pi).Split('.');
                long intBits = long.Parse(parts[0], NumberStyles.HexNumber);
                long fracBits = 0; int fracLen = 0;
                if (parts.Length > 1 && parts[1].Length > 0)
                {
                    fracBits = long.Parse(parts[1], NumberStyles.HexNumber);
                    fracLen  = parts[1].Length * 4;
                }
                long biasedExp = exp + 1023;
                long frac52 = fracLen <= 52 ? (fracBits << (52 - fracLen))
                                            : (fracBits >> (fracLen - 52));
                long bits = (biasedExp << 52) | (frac52 & 0x000FFFFFFFFFFFFFL);
                double v = BitConverter.Int64BitsToDouble(bits);
                return neg ? -v : v;
            }
            catch { return 0; }
        }

        private static string DecodeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("5005")) return raw.Substring(4);
            if (raw.StartsWith("5003")) return raw.Substring(4);
            return raw;
        }

        private static int DecodeInt(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = raw.StartsWith("5003") ? raw.Substring(4)
                     : raw.StartsWith("5005") ? raw.Substring(4)
                     : raw;
            return int.TryParse(s, out int v) ? v : 0;
        }

        private static readonly Regex GuidRx = new Regex(
            @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}",
            RegexOptions.Compiled);

        private static string ExtractGuid(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var m = GuidRx.Match(s);
            return m.Success ? m.Value : s.Trim();
        }

        // ── Utility ───────────────────────────────────────────────────────────

        private static string DumpCodepoints(string s, int maxChars)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(s.Length, maxChars); i++)
            {
                if (i > 0) sb.Append(' ');
                sb.Append(((int)s[i]).ToString("X4"));
            }
            if (s.Length > maxChars) sb.Append(" …");
            return sb.ToString();
        }

        private static void Dbg(Editor ed, string msg)
            => ed?.WriteMessage(msg);

        // ── Robust XML loader ─────────────────────────────────────────────────

        private static readonly Regex XmlDeclRx = new Regex(
            @"<\?xml\b[^?]*\?>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static XDocument LoadXml(string path)
        {
            try { return XDocument.Load(path); }
            catch (System.Xml.XmlException) { }

            byte[] raw = File.ReadAllBytes(path);

            foreach (int cp in new[] { 0 /* default */, 1254, 1252, 28591 /* iso-8859-1 */ })
            {
                try
                {
                    string txt = cp == 0
                        ? Encoding.Default.GetString(raw)
                        : Encoding.GetEncoding(cp).GetString(raw);
                    return XDocument.Parse(
                        XmlDeclRx.Replace(txt, "<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
                }
                catch { }
            }
            throw new InvalidOperationException(
                "Cannot load XML — tried UTF-8, system default, cp1254, cp1252, iso-8859-1.");
        }
    }
}
