using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Autodesk.AutoCAD.EditorInput;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Calculation Engine – Urbano topology XML.
    ///
    /// ── Why Regex-based extraction is mandatory ───────────────────────────────
    ///
    ///  Every numeric value (coordinates, elevations, diameters) is stored as a
    ///  C99 hex-float with a garbage prefix attached:
    ///
    ///    @pos  → "8005-0x1.4d13d6a9e1ad0p+15=EF=BE=890x1.2c1a89f4926c0p+12=EF=BE=89…"
    ///             ^^^^                        ^^^^^^^^^ ← literal ASCII "=EF=BE=89"
    ///             prefix                      separator (NOT the Unicode character U+FE09)
    ///
    ///    TH1   → "80013.0000000000000e+000"  or  "80010x1.8p+0"
    ///    MHB   → "80010x1.43d70a3d70a4p+3"
    ///    LL10  → "80010x1.…p+…"
    ///
    ///  The separator inside @pos is the 9-character ASCII string "=EF=BE=89"
    ///  (quoted-printable encoding of UTF-8 bytes 0xEF 0xBE 0x89).
    ///  Splitting on U+FE09 or stripping fixed-length prefixes therefore fails.
    ///
    ///  Solution: apply HexFloatRx to the entire raw string.
    ///    • @pos          → Matches[0]=X, Matches[1]=Y, Matches[2]=Z
    ///    • scalar prop   → Match(raw).Value  (or decimal fallback)
    ///
    /// ── XML schema ───────────────────────────────────────────────────────────
    ///
    ///  TOPOLOGY NODES  drawing/topology/networkTopology/main/tpl/ns/n
    ///    TH1  – absolute terrain elevation (Arazi Kotu), metres
    ///    MHB  – gap between lowest pipe invert and manhole floor, metres
    ///    MH   – GUID of the linked MANHOLE catalog entry
    ///
    ///  TOPOLOGY SECTIONS  …/tpl/ss/s
    ///    LL10  – elevation at START node; meaning depends on LLPOS (see below)
    ///    LL11  – elevation at END   node; same LLPOS applies
    ///    LLPOS – cross-section point measured by LL10/LL11 (5003-prefixed integer):
    ///              1=Üst dış(outer top)  2=Üst iç(inner top)  4=Aks(centre)
    ///              8=Alt iç(AkarKot=inner bottom, most common)  16=Alt dış(outer bottom)
    ///    PPR  – GUID of the linked PIPE   catalog entry
    ///    TRNC – GUID of the linked TRENCH catalog entry
    ///
    ///  PIPE CATALOG
    ///    PIPE_DV       – outer diameter in mm (primary;  ÷1000 → metres)
    ///    PIPE_DU       – outer diameter in mm (fallback; ÷1000 → metres)
    ///    PIPE_NO       – nominal diameter in mm
    ///    PIPE_MATERIAL – material string
    ///
    ///  TRENCH CATALOG
    ///    TR_WIDTH     – trench bottom width (m)
    ///    TR_ANGLE-L/R – side-wall angles from horizontal (°)
    ///
    ///  MANHOLE CATALOG
    ///    MANHOLE_DN       – nominal internal diameter (mm), preferred
    ///    MANHOLE_D2       – internal diameter (m), secondary
    ///    CATALOGITEM_NAME – e.g. "SD_Tip1 Φ1000_Φ400-Φ500 1300 mm" – fallback
    ///
    /// ── Formulas ─────────────────────────────────────────────────────────────
    ///
    ///  Outer_Diameter_m  =  PIPE_DV / 1000   (PIPE_DU fallback)
    ///
    ///  Invert_Start  =  LlToInvert(LL10, LLPOS, OD, ID)
    ///  Invert_End    =  LlToInvert(LL11, LLPOS, OD, ID)
    ///
    ///  Length_2D  =  √( (X₂−X₁)² + (Y₂−Y₁)² )
    ///
    ///  Trench depth at start/end  =  TH1  −  Invert
    ///  Excavation area  A(D)  =  TR_WIDTH × D  +  ½(cot αL + cot αR) × D²
    ///  Excavation volume  =  (A_start + A_end) / 2  ×  Length_2D
    ///
    ///  Manhole depth  =  ( TH1  −  Lowest_Invert )  +  MHB
    ///                 ≡  Sirt_Derinligi  +  Outer_Diameter_m  +  MHB
    /// </summary>
    public class BoQParserService : IBoQParserService
    {
        // ── Instance configuration ────────────────────────────────────────────

        private readonly bool _enableClashDetection;
        private readonly OverlapAssignment _excavAssignment;
        private readonly OverlapAssignment _backfillAssignment;

        /// <summary>Initialises the parser with clash detection enabled (default, 50/50).</summary>
        public BoQParserService()
            : this(enableClashDetection: true,
                   excavAssignment:   OverlapAssignment.Split,
                   backfillAssignment: OverlapAssignment.Split) { }

        /// <summary>
        /// Backwards-compatible overload: enables/disables clash detection with the
        /// historical 50/50 split for both excavation and backfill.
        /// </summary>
        public BoQParserService(bool enableClashDetection)
            : this(enableClashDetection,
                   OverlapAssignment.Split,
                   OverlapAssignment.Split) { }

        /// <summary>
        /// Initialises the parser.
        /// When <paramref name="enableClashDetection"/> is false, the
        /// <see cref="ComputeTrenchClashes"/> pass is skipped entirely, leaving
        /// all ExcavVol and VBackfill values at their raw calculated amounts.
        ///
        /// <paramref name="excavAssignment"/> and <paramref name="backfillAssignment"/>
        /// control, independently, how the shared overlap volume is assigned between
        /// the deeper ("lower") and shallower ("upper") pipe of each clashing pair.
        /// </summary>
        public BoQParserService(
            bool enableClashDetection,
            OverlapAssignment excavAssignment,
            OverlapAssignment backfillAssignment)
        {
            _enableClashDetection = enableClashDetection;
            _excavAssignment      = excavAssignment;
            _backfillAssignment   = backfillAssignment;
        }

        // ── IEEE 754 hex-float Regex (user-specified exact pattern) ───────────
        //
        //  Matches a complete C99 hex-float token anywhere in a dirty string:
        //    -0x1.4d13d6a9e1ad0p+15   0x1.2c1a89f4926c0p+12   0x0.0p+0
        //
        //  Requires a decimal point in the mantissa (matches the Urbano format).
        //  The outer group makes every Match.Value the clean token (no outer
        //  parentheses needed when we use match.Value directly).
        private static readonly Regex HexFloatRx = new Regex(
            @"[-+]?0x[0-9a-fA-F]+\.[0-9a-fA-F]+p[-+]?[0-9]+",
            RegexOptions.Compiled);

        // ── Other compiled helpers ─────────────────────────────────────────────
        private static readonly Regex XmlDeclRx = new Regex(
            @"<\?xml\b[^?]*\?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex GuidRx = new Regex(
            @"[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}",
            RegexOptions.Compiled);

        // =====================================================================
        // Public entry point
        // =====================================================================

        public BoQReport Parse(string xmlPath, Editor ed = null)
        {
            if (!File.Exists(xmlPath))
                throw new FileNotFoundException($"Urbano export not found: {xmlPath}");

            XDocument doc;
            try   { doc = LoadXmlRobust(xmlPath); }
            catch (Exception ex)
            { throw new InvalidOperationException($"XML load failed: {ex.Message}", ex); }

            var report = new BoQReport();
            var notes  = new List<string>();

            Dbg(ed, "\n  [BoQ] Parsing system names…");
            var sysNames = ParseSystemNames(doc);
            Dbg(ed, $"\n         {sysNames.Count} system(s): {string.Join(", ", sysNames.Values)}");

            Dbg(ed, "\n  [BoQ] Building catalog index…");
            var catDict = BuildCatalogDict(doc);
            Dbg(ed, $"\n         {catDict.Count} catalog entries indexed.");

            Dbg(ed, "\n  [BoQ] Parsing nodes…");
            var nodes = ParseNodes(doc, catDict, sysNames, notes, ed);
            Dbg(ed, $"\n         {nodes.Count} node(s) found.");

            Dbg(ed, "\n  [BoQ] Parsing sections + excavation…");
            var sections = ParseSections(doc, catDict, sysNames, nodes, notes, ed);
            Dbg(ed, $"\n         {sections.Count} section(s) calculated.");

            ComputeManholeDepths(nodes, sections, ed);

            Dbg(ed, "\n  [BoQ] Aggregating report + per-station clash detection…");
            AggregateIntoReport(report, sysNames, nodes, sections,
                _enableClashDetection, _excavAssignment, _backfillAssignment, ed);
            report.DiscoveryNotes = notes;

            return report;
        }

        // =====================================================================
        // Step 1 – System names
        // =====================================================================

        private static Dictionary<int, string> ParseSystemNames(XDocument doc)
        {
            var result = new Dictionary<int, string>();
            foreach (var gs in doc.Descendants("gisSystem"))
            {
                string idStr = (string)gs.Attribute("id")   ?? "";
                string name  = (string)gs.Attribute("name") ?? "";
                if (int.TryParse(idStr, out int id) && !string.IsNullOrEmpty(name))
                    result[id] = name;
            }
            if (result.Count == 0) result[0] = "System_0";
            return result;
        }

        // =====================================================================
        // Step 2 – Catalog dictionary   GUID.upper → { key → raw-value }
        // =====================================================================

        private static Dictionary<string, Dictionary<string, string>> BuildCatalogDict(
            XDocument doc)
        {
            var dict = new Dictionary<string, Dictionary<string, string>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var item in doc.Descendants("catalogItem"))
            {
                string guid = (string)item.Attribute("guid") ?? "";
                if (string.IsNullOrEmpty(guid) || guid == "SYS") continue;

                var props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var ct in item.Elements("ppsEx").Elements("ct"))
                    foreach (var pEx in ct.Elements("pEx"))
                    {
                        string t = (string)pEx.Attribute("t") ?? "";
                        string v = (string)pEx.Attribute("v") ?? "";
                        if (!string.IsNullOrEmpty(t) && !props.ContainsKey(t))
                            props[t] = v;
                    }

                foreach (var attr in item.Attributes())
                    props["@" + attr.Name.LocalName] = attr.Value;

                dict[guid.ToUpperInvariant()] = props;
            }
            return dict;
        }

        // =====================================================================
        // Internal parse-phase models
        // =====================================================================

        private sealed class NodeInfo
        {
            public string Guid           { get; set; }
            public int    SystemId       { get; set; }
            public string Name           { get; set; }   // AG_NAME  e.g. "4Y"
            public double X              { get; set; }   // easting  (hex-float decoded)
            public double Y              { get; set; }   // northing (hex-float decoded)
            public double TerrainZ       { get; set; }   // TH1  – absolute terrain elevation
            public double Mhb            { get; set; }   // MHB  – invert-to-floor gap
            public string MhGuid         { get; set; }
            public int    MhDiameter     { get; set; }   // nominal shaft Ø, mm
            // Computed in ComputeManholeDepths
            public double Depth            { get; set; }
            public double ExcavationDepth  { get; set; }   // H = TerrainZ − lowestInvert
            public double ExcavationVolume { get; set; }   // Simpson's 1/3 rule (m³)
        }

        private sealed class SectionInfo
        {
            // ── Identity ──────────────────────────────────────────────────────
            public string Guid            { get; set; }
            public int    SystemId        { get; set; }
            public string SnGuid          { get; set; }
            public string EnGuid          { get; set; }
            public string StartNodeName   { get; set; }
            public string EndNodeName     { get; set; }
            // ── Pipe ──────────────────────────────────────────────────────────
            public double LL10            { get; set; }
            public double LL11            { get; set; }
            public double PipeOuterDiamM  { get; set; }   // PIPE_DV (or DU) ÷ 1000  (m)
            public int    NominalDiamMm   { get; set; }   // PIPE_NO
            public string Material        { get; set; }
            public double InvertStart     { get; set; }   // LL10 − OD_m
            public double InvertEnd       { get; set; }   // LL11 − OD_m
            public double Length2D        { get; set; }   // √(dX²+dY²)
            // ── Trench catalog ─────────────────────────────────────────────────
            public double TrWidth         { get; set; }   // TR_WIDTH  (m)
            public double TrBedHeight     { get; set; }   // TR_BEDHEIGHT  (m)
            public double TrSandOverPipe  { get; set; }   // TR_SANDOVERPIPE  (m)
            public double TrAngleL        { get; set; }   // TR_ANGLE-L  (°)
            public double TrAngleR        { get; set; }   // TR_ANGLE-R  (°)
            // ── Constant cross-section geometry ────────────────────────────────
            public double SlopeRatio      { get; set; }   // cot(TR_ANGLE_L)
            public double TopWidthBed     { get; set; }   // W + 2×Hbed×SlopeRatio
            public double ABed            { get; set; }   // bedding area per m (m²)
            public double HSurround       { get; set; }   // D + TR_SANDOVERPIPE (m)
            public double BaseWidthSurr   { get; set; }   // = TopWidthBed
            public double TopWidthSurr    { get; set; }   // BaseWidthSurr + 2×Hsurr×SlopeRatio
            public double ASurroundGross  { get; set; }   // surround trapezoid area (m²)
            public double PipeArea        { get; set; }   // π×(D/2)²  (m²)
            public double ASurroundNet    { get; set; }   // ASurroundGross − PipeArea (m²)
            // ── Variable cross-section geometry ────────────────────────────────
            public double DepthToInvStart { get; set; }   // TH1_s − InvertStart  (m)
            public double DepthToInvEnd   { get; set; }   // TH1_e − InvertEnd    (m)
            public double TrueDepthStart  { get; set; }   // DepthToInv_s + TrBedHeight
            public double TrueDepthEnd    { get; set; }   // DepthToInv_e + TrBedHeight
            public double TopWidthExcavS  { get; set; }   // W + 2×TrueDepth_s×SlopeRatio
            public double TopWidthExcavE  { get; set; }
            public double AExcavStart     { get; set; }   // (W+TopW)/2 × TrueDepth_s  (m²)
            public double AExcavEnd       { get; set; }
            public double ABackfillStart  { get; set; }   // AExcavStart − ABed − ASurrGross
            public double ABackfillEnd    { get; set; }
            // ── Volumes ────────────────────────────────────────────────────────
            public double VBedding              { get; set; }   // ABed         × Length2D  (m³)
            public double VSurround             { get; set; }   // ASurroundNet × Length2D  (m³)
            public double ExcavVol              { get; set; }   // avg(AExcav)  × Length2D  (m³)  [modified in-place by clash detection]
            public double VBackfill             { get; set; }   // avg(ABackfill) × Length2D (m³) [modified in-place by clash detection]
            // ── Clash detection (populated by ComputeTrenchClashes) ────────────
            public double OverlapExcavDeducted    { get; set; }   // kazı  deduction from trench clash
            public double OverlapBackfillDeducted { get; set; }   // dolgu deduction from trench clash
            public List<string> ClashLog        { get; set; } = new List<string>();
        }

        // =====================================================================
        // Step 3 – Parse topology nodes
        // =====================================================================

        private static Dictionary<string, NodeInfo> ParseNodes(
            XDocument doc,
            Dictionary<string, Dictionary<string, string>> catDict,
            Dictionary<int, string> sysNames,
            List<string> notes,
            Editor ed)
        {
            var result  = new Dictionary<string, NodeInfo>(StringComparer.OrdinalIgnoreCase);
            var tplMain = FindMainTpl(doc);
            if (tplMain == null) return result;

            foreach (var nEl in tplMain.Descendants("ns").Elements("n"))
            {
                string guid = ((string)nEl.Attribute("g") ?? "").ToUpperInvariant();
                if (string.IsNullOrEmpty(guid)) continue;

                var props  = ReadTopoProps(nEl);
                int    sysId = DecodeIntProp  (GetProp(props, "AG_ID_SYSTEM"));
                string name  = DecodeStrProp  (GetProp(props, "AG_NAME"));
                double th1   = DecodeFloatProp(GetProp(props, "TH1"));
                double mhb   = DecodeFloatProp(GetProp(props, "MHB"));
                string mhGuid= DecodeGuidStr  (GetProp(props, "MH"));

                string rawPos = (string)nEl.Attribute("pos") ?? "";
                ParsePos(rawPos, out double x, out double y);

                // First-node diagnostic
                if (result.Count == 0)
                {
                    Dbg(ed, $"\n  [BoQ-DBG] First node '{name}': " +
                            $"x={x:F3}  y={y:F3}  TH1={th1:F3}  MHB={mhb:F3}");

                    // Print up to 80 codepoints so the separator is visible
                    var cpDump = new StringBuilder();
                    for (int ci = 0; ci < Math.Min(rawPos.Length, 80); ci++)
                        cpDump.Append(((int)rawPos[ci]).ToString("X4")).Append(' ');
                    Dbg(ed, $"\n  [BoQ-DBG] pos hex: [{cpDump}]");

                    // Also print the raw string printably (non-ASCII → '?')
                    var readable = new StringBuilder();
                    for (int ci = 0; ci < Math.Min(rawPos.Length, 80); ci++)
                        readable.Append(rawPos[ci] > 127 ? '?' : rawPos[ci]);
                    Dbg(ed, $"\n  [BoQ-DBG] pos txt: [{readable}]");

                    // Report how many hex-float matches were found
                    var testMatches = HexFloatRx.Matches(rawPos);
                    Dbg(ed, $"\n  [BoQ-DBG] HexFloatRx matches in pos: {testMatches.Count}");
                    for (int mi = 0; mi < testMatches.Count; mi++)
                        Dbg(ed, $"\n  [BoQ-DBG]   [{mi}] = {testMatches[mi].Value}");
                }

                // Manhole catalog – nominal diameter
                int mhDiam = 0;
                if (!string.IsNullOrEmpty(mhGuid))
                {
                    var mhProps = CatLookup(catDict, mhGuid);
                    if (mhProps != null)
                        mhDiam = ExtractManholeNominalDiam(mhProps);
                }

                result[guid] = new NodeInfo
                {
                    Guid       = guid,
                    SystemId   = sysId,
                    Name       = name,
                    X          = x,
                    Y          = y,
                    TerrainZ   = th1,
                    Mhb        = mhb,
                    MhGuid     = mhGuid,
                    MhDiameter = mhDiam
                };
            }
            return result;
        }

        // =====================================================================
        // Step 4 – Parse topology sections
        // =====================================================================

        private static List<SectionInfo> ParseSections(
            XDocument doc,
            Dictionary<string, Dictionary<string, string>> catDict,
            Dictionary<int, string> sysNames,
            Dictionary<string, NodeInfo> nodes,
            List<string> notes,
            Editor ed)
        {
            var result  = new List<SectionInfo>();
            var tplMain = FindMainTpl(doc);
            if (tplMain == null) return result;

            bool warnPipe = false, warnTrnc = false;

            foreach (var sEl in tplMain.Descendants("ss").Elements("s"))
            {
                string guid    = ((string)sEl.Attribute("g")  ?? "").ToUpperInvariant();
                string snGuid  = ((string)sEl.Attribute("sn") ?? "").ToUpperInvariant();
                string enGuid  = ((string)sEl.Attribute("en") ?? "").ToUpperInvariant();
                if (string.IsNullOrEmpty(guid)) continue;

                var props    = ReadTopoProps(sEl);
                int    sysId = DecodeIntProp  (GetProp(props, "AG_ID_SYSTEM"));
                double ll10  = DecodeFloatProp(GetProp(props, "LL10"));
                double ll11  = DecodeFloatProp(GetProp(props, "LL11"));
                // LLPOS tells us which pipe cross-section point LL10/LL11 measures:
                //  1=Üst dış(outer top)  2=Üst iç(inner top)  4=Aks(centre)
                //  8=Alt iç(inner btm=AkarKot)  16=Alt dış(outer btm)
                int llpos = DecodeIntProp(GetProp(props, "LLPOS"));
                if (llpos == 0) llpos = 8; // default: Alt iç
                string pprGuid  = DecodeGuidStr(DecodeStrProp(GetProp(props, "PPR")));
                string trncGuid = DecodeGuidStr(DecodeStrProp(GetProp(props, "TRNC")));

                // ── Pipe catalog ──────────────────────────────────────────────
                double odMm    = 0;
                int    nomMm   = 0;
                string material= "?";

                var pipeProps = CatLookup(catDict, pprGuid);
                if (pipeProps != null)
                {
                    // PIPE_DV = outer diameter (primary); PIPE_DU = fallback
                    odMm = DecodeFloatProp(GetProp(pipeProps, "PIPE_DV"));
                    if (odMm <= 0)
                        odMm = DecodeFloatProp(GetProp(pipeProps, "PIPE_DU"));
                    if (odMm <= 0)
                        odMm = DecodeFloatProp(GetProp(pipeProps, "PIPE_NO"));

                    double nomRaw = DecodeFloatProp(GetProp(pipeProps, "PIPE_NO"));
                    if (nomRaw <= 0) nomRaw = odMm;
                    nomMm = (int)Math.Round(nomRaw);

                    material = DecodeStrProp(GetProp(pipeProps, "PIPE_MATERIAL"));
                    if (string.IsNullOrEmpty(material) || material == "?")
                    {
                        string cn = DecodeStrProp(GetProp(pipeProps, "CATALOGITEM_NAME"));
                        var ww = cn.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (ww.Length > 0) material = ww[ww.Length - 1];
                    }
                }
                else if (!warnPipe)
                {
                    notes.Add($"[WARN] Pipe catalog entry not found for GUID '{pprGuid}'.");
                    warnPipe = true;
                }

                double odM = odMm / 1000.0;
                double idM = nomMm / 1000.0; // inner (nominal) diameter

                // Convert LL10/LL11 → AkarKot using the per-pipe LLPOS measurement position.
                double invertStart = LlToInvert(ll10, llpos, odM, idM);
                double invertEnd   = LlToInvert(ll11, llpos, odM, idM);

                // ── Trench catalog ────────────────────────────────────────────
                double trWidth        = 1.0;
                double trBedHeight    = 0.0;   // TR_BEDHEIGHT    – sand bed below pipe
                double trSandOverPipe = 0.0;   // TR_SANDOVERPIPE – sand cover above pipe
                double trAngleL       = 90.0;  // TR_ANGLE-L (°)
                double trAngleR       = 90.0;  // TR_ANGLE-R (°)

                var trProps = CatLookup(catDict, trncGuid);
                if (trProps != null)
                {
                    double w   = DecodeFloatProp(GetProp(trProps, "TR_WIDTH"));
                    double bh  = DecodeFloatProp(GetProp(trProps, "TR_BEDHEIGHT"));
                    double sop = DecodeFloatProp(GetProp(trProps, "TR_SANDOVERPIPE"));
                    double aL  = DecodeFloatProp(GetProp(trProps, "TR_ANGLE-L"));
                    double aR  = DecodeFloatProp(GetProp(trProps, "TR_ANGLE-R"));
                    if (w   > 0) trWidth        = w;
                    if (bh  > 0) trBedHeight    = bh;
                    if (sop > 0) trSandOverPipe = sop;
                    if (aL  > 0) trAngleL       = aL;
                    if (aR  > 0) trAngleR       = aR;
                }
                else if (!warnTrnc && !string.IsNullOrEmpty(trncGuid))
                {
                    notes.Add($"[WARN] Trench catalog entry not found for GUID '{trncGuid}'.");
                    warnTrnc = true;
                }

                // ── 2-D pipe length ───────────────────────────────────────────
                NodeInfo snNode = nodes.ContainsKey(snGuid) ? nodes[snGuid] : null;
                NodeInfo enNode = nodes.ContainsKey(enGuid) ? nodes[enGuid] : null;

                double len2D = 0;
                if (snNode != null && enNode != null)
                {
                    double dx = enNode.X - snNode.X;
                    double dy = enNode.Y - snNode.Y;
                    len2D = Math.Sqrt(dx * dx + dy * dy);
                }

                // ── Layer geometry ─────────────────────────────────────────────
                //
                //  SlopeRatio = cot(TR_ANGLE_L) = 1 / tan(αL)
                //  Width at height H above trench base: W(H) = TR_WIDTH + 2×H×SlopeRatio
                //
                //  CONSTANT cross-section values (same at both ends of the pipe):
                //
                //  ① Bedding zone   (H = 0 … TR_BEDHEIGHT)
                //       TopWidthBed   = TR_WIDTH + 2×TR_BEDHEIGHT×SlopeRatio
                //       A_bed         = (TR_WIDTH + TopWidthBed) / 2 × TR_BEDHEIGHT
                //
                //  ② Surround zone  (H = TR_BEDHEIGHT … TR_BEDHEIGHT + D + TR_SANDOVERPIPE)
                //       H_surround    = D + TR_SANDOVERPIPE
                //       BaseWidthSurr = TopWidthBed
                //       TopWidthSurr  = BaseWidthSurr + 2×H_surround×SlopeRatio
                //       A_surr_gross  = (BaseWidthSurr + TopWidthSurr) / 2 × H_surround
                //       PipeArea      = π × (D/2)²
                //       A_surr_net    = A_surr_gross − PipeArea
                //
                //  VARIABLE cross-section values (different at each node, terrain-dependent):
                //
                //  ③ Total excavation (H = 0 … TrueDepth)
                //       TrueDepth  = (TH1 − Invert) + TR_BEDHEIGHT
                //       TopWidthEx = TR_WIDTH + 2×TrueDepth×SlopeRatio
                //       A_excav    = (TR_WIDTH + TopWidthEx) / 2 × TrueDepth
                //
                //  ④ Backfill = everything above the surround zone
                //       A_backfill = A_excav − A_bed − A_surr_gross
                //
                //  VOLUMES
                //       V_Bedding  = A_bed         × Length2D          (const × length)
                //       V_Surround = A_surr_net    × Length2D          (const × length)
                //       V_Excav    = avg(A_excav)  × Length2D          (prismatoid)
                //       V_Backfill = avg(A_backfill) × Length2D        (prismatoid)

                double slopeRatio = (trAngleL > 0 && trAngleL < 90)
                    ? 1.0 / Math.Tan(trAngleL * Math.PI / 180.0)
                    : 0.0;   // 90° → vertical wall → no spread

                // ── ① Bedding (constant) ──────────────────────────────────────
                double topWidthBed   = trWidth + 2.0 * trBedHeight * slopeRatio;
                double aBed          = (trWidth + topWidthBed) / 2.0 * trBedHeight;

                // ── ② Surround (constant) ─────────────────────────────────────
                double hSurround     = odM + trSandOverPipe;
                double baseWidthSurr = topWidthBed;
                double topWidthSurr  = baseWidthSurr + 2.0 * hSurround * slopeRatio;
                double aSurrGross    = (baseWidthSurr + topWidthSurr) / 2.0 * hSurround;
                double pipeArea      = Math.PI * Math.Pow(odM / 2.0, 2);
                double aSurrNet      = Math.Max(0, aSurrGross - pipeArea);

                // ── ③ + ④ Excavation & Backfill (variable per end) ────────────
                double depthToInvS = 0, depthToInvE = 0;
                double trueDeptS   = 0, trueDeptE   = 0;
                double topWExcavS  = 0, topWExcavE  = 0;
                double aExcavS     = 0, aExcavE     = 0;
                double aBackfillS  = 0, aBackfillE  = 0;
                double excavVol    = 0, vBackfill   = 0;

                if (snNode != null && enNode != null && len2D > 0)
                {
                    depthToInvS = Math.Max(0, snNode.TerrainZ - invertStart);
                    depthToInvE = Math.Max(0, enNode.TerrainZ - invertEnd);

                    trueDeptS = depthToInvS + trBedHeight;
                    trueDeptE = depthToInvE + trBedHeight;

                    topWExcavS = trWidth + 2.0 * trueDeptS * slopeRatio;
                    topWExcavE = trWidth + 2.0 * trueDeptE * slopeRatio;

                    aExcavS = (trWidth + topWExcavS) / 2.0 * trueDeptS;
                    aExcavE = (trWidth + topWExcavE) / 2.0 * trueDeptE;

                    aBackfillS = Math.Max(0, aExcavS - aBed - aSurrGross);
                    aBackfillE = Math.Max(0, aExcavE - aBed - aSurrGross);

                    excavVol  = (aExcavS   + aExcavE)   * 0.5 * len2D;
                    vBackfill = (aBackfillS + aBackfillE) * 0.5 * len2D;
                }

                double vBedding  = aBed    * len2D;
                double vSurround = aSurrNet * len2D;

                result.Add(new SectionInfo
                {
                    Guid             = guid,
                    SystemId         = sysId,
                    SnGuid           = snGuid,
                    EnGuid           = enGuid,
                    StartNodeName    = snNode?.Name ?? snGuid.Substring(0, Math.Min(8, snGuid.Length)),
                    EndNodeName      = enNode?.Name ?? enGuid.Substring(0, Math.Min(8, enGuid.Length)),
                    LL10             = ll10,
                    LL11             = ll11,
                    PipeOuterDiamM   = odM,
                    NominalDiamMm    = nomMm,
                    Material         = material,
                    InvertStart      = invertStart,
                    InvertEnd        = invertEnd,
                    Length2D         = len2D,
                    // trench catalog
                    TrWidth          = trWidth,
                    TrBedHeight      = trBedHeight,
                    TrSandOverPipe   = trSandOverPipe,
                    TrAngleL         = trAngleL,
                    TrAngleR         = trAngleR,
                    // constant geometry
                    SlopeRatio       = slopeRatio,
                    TopWidthBed      = topWidthBed,
                    ABed             = aBed,
                    HSurround        = hSurround,
                    BaseWidthSurr    = baseWidthSurr,
                    TopWidthSurr     = topWidthSurr,
                    ASurroundGross   = aSurrGross,
                    PipeArea         = pipeArea,
                    ASurroundNet     = aSurrNet,
                    // variable geometry
                    DepthToInvStart  = depthToInvS,
                    DepthToInvEnd    = depthToInvE,
                    TrueDepthStart   = trueDeptS,
                    TrueDepthEnd     = trueDeptE,
                    TopWidthExcavS   = topWExcavS,
                    TopWidthExcavE   = topWExcavE,
                    AExcavStart      = aExcavS,
                    AExcavEnd        = aExcavE,
                    ABackfillStart   = aBackfillS,
                    ABackfillEnd     = aBackfillE,
                    // volumes
                    VBedding         = vBedding,
                    VSurround        = vSurround,
                    ExcavVol         = excavVol,
                    VBackfill        = vBackfill
                });
            }
            return result;
        }

        // =====================================================================
        // Step 5 – Manhole depths
        // =====================================================================

        private static void ComputeManholeDepths(
            Dictionary<string, NodeInfo> nodes,
            List<SectionInfo>            sections,
            Editor ed)
        {
            foreach (var n in nodes.Values)
                n.Depth = 0;

            // Collect pipe-invert elevations that arrive at each node
            var invertsByNode = new Dictionary<string, List<double>>(
                StringComparer.OrdinalIgnoreCase);

            foreach (var sec in sections)
            {
                AddInvert(invertsByNode, sec.SnGuid, sec.InvertStart);
                AddInvert(invertsByNode, sec.EnGuid, sec.InvertEnd);
            }

            // Depth = ( TH1 − lowest_invert ) + MHB
            //       ≡ Sirt_Derinligi + Outer_Diameter + MHB
            foreach (var nd in nodes.Values)
            {
                if (!invertsByNode.ContainsKey(nd.Guid) ||
                    invertsByNode[nd.Guid].Count == 0)
                    continue;

                double lowestInvert = invertsByNode[nd.Guid].Min();
                nd.Depth = Math.Max(0, (nd.TerrainZ - lowestInvert) + nd.Mhb);

                // Isolated manhole excavation — no trench overlap deduction yet.
                // Base square side = 1.0m (shaft) + 0.5m + 0.5m (working space) = 2.0m.
                // Slope 1H:3V → each side grows by h/3 per metre of rise on each face.
                // Side(h) = 2.0 + 2*(h/3);  A(h) = Side²
                // Volume by Simpson's 1/3: V = (H/6)*(A_bot + 4*A_mid + A_top)
                double excavH = Math.Max(0, nd.TerrainZ - lowestInvert);
                nd.ExcavationDepth = excavH;
                if (excavH > 1e-6)
                {
                    double sideBot = 2.0;
                    double sideMid = 2.0 + 2.0 * (excavH * 0.5) / 3.0;
                    double sideTop = 2.0 + 2.0 * excavH / 3.0;
                    double aBot = sideBot * sideBot;
                    double aMid = sideMid * sideMid;
                    double aTop = sideTop * sideTop;
                    nd.ExcavationVolume = (excavH / 6.0) * (aBot + 4.0 * aMid + aTop);
                }

                Dbg(ed, $"\n  [BoQ-DBG] Node {nd.Name,-4}: " +
                        $"TH1={nd.TerrainZ:F3}  lowestInv={lowestInvert:F3}" +
                        $"  MHB={nd.Mhb:F3}  Depth={nd.Depth:F3}" +
                        $"  MhExcavV={nd.ExcavationVolume:F3}");
            }
        }

        private static void AddInvert(
            Dictionary<string, List<double>> dict, string guid, double invert)
        {
            if (string.IsNullOrEmpty(guid)) return;
            if (!dict.ContainsKey(guid)) dict[guid] = new List<double>();
            dict[guid].Add(invert);
        }

        // =====================================================================
        // Step 6 – Aggregate into BoQReport
        // =====================================================================

        private static void AggregateIntoReport(
            BoQReport                    report,
            Dictionary<int, string>      sysNames,
            Dictionary<string, NodeInfo> nodes,
            List<SectionInfo>            sections,
            bool                         enableClash,
            OverlapAssignment            excavAssign,
            OverlapAssignment            backfillAssign,
            Editor                       ed)
        {
            // ── Step 1: Build SectionDebugRows + per-station polygons ─────────
            var sdrs = new List<SectionDebugRow>(sections.Count);
            foreach (var sec in sections)
            {
                string sysName = sysNames.ContainsKey(sec.SystemId)
                    ? sysNames[sec.SystemId] : $"System_{sec.SystemId}";

                NodeInfo snNode = nodes.ContainsKey(sec.SnGuid) ? nodes[sec.SnGuid] : null;
                NodeInfo enNode = nodes.ContainsKey(sec.EnGuid) ? nodes[sec.EnGuid] : null;

                var sdr = new SectionDebugRow
                {
                    SystemName            = sysName,
                    PipeName              = $"{sec.StartNodeName} → {sec.EndNodeName}",
                    StartNodeName         = sec.StartNodeName,
                    EndNodeName           = sec.EndNodeName,
                    StartNodeGuid         = sec.SnGuid,
                    EndNodeGuid           = sec.EnGuid,
                    DiameterMm            = sec.NominalDiamMm,
                    Material              = sec.Material,
                    PipeOuterDiamM        = sec.PipeOuterDiamM,
                    Length2D              = sec.Length2D,
                    StartX                = snNode?.X ?? 0,
                    StartY                = snNode?.Y ?? 0,
                    StartTerrainZ         = snNode?.TerrainZ ?? 0,
                    EndX                  = enNode?.X ?? 0,
                    EndY                  = enNode?.Y ?? 0,
                    EndTerrainZ           = enNode?.TerrainZ ?? 0,
                    InvertStart           = sec.InvertStart,
                    InvertEnd             = sec.InvertEnd,
                    DepthToInvStart       = sec.DepthToInvStart,
                    DepthToInvEnd         = sec.DepthToInvEnd,
                    TrWidth               = sec.TrWidth,
                    TrBedHeight           = sec.TrBedHeight,
                    TrSandOverPipe        = sec.TrSandOverPipe,
                    TrAngleL              = sec.TrAngleL,
                    TrAngleR              = sec.TrAngleR,
                    SlopeRatio            = sec.SlopeRatio,
                    TopWidthBed           = sec.TopWidthBed,
                    ABed                  = sec.ABed,
                    HSurround             = sec.HSurround,
                    BaseWidthSurr         = sec.BaseWidthSurr,
                    TopWidthSurr          = sec.TopWidthSurr,
                    ASurroundGross        = sec.ASurroundGross,
                    PipeArea              = sec.PipeArea,
                    ASurroundNet          = sec.ASurroundNet,
                    TrueDepthStart        = sec.TrueDepthStart,
                    TrueDepthEnd          = sec.TrueDepthEnd,
                    TopWidthExcavS        = sec.TopWidthExcavS,
                    TopWidthExcavE        = sec.TopWidthExcavE,
                    AExcavStart           = sec.AExcavStart,
                    AExcavEnd             = sec.AExcavEnd,
                    ABackfillStart        = sec.ABackfillStart,
                    ABackfillEnd          = sec.ABackfillEnd,
                    VBedding              = sec.VBedding,
                    VSurround             = sec.VSurround,
                    VExcav                = sec.ExcavVol,
                    VBackfill             = sec.VBackfill,
                    OverlapExcavDeducted    = 0,
                    OverlapBackfillDeducted = 0
                };
                sdrs.Add(sdr);
                report.SectionDebug.Add(sdr);
            }

            // ── Phase 1/2: collision-station injection ────────────────────────
            // Find where trench footprints overlap in plan view and inject the
            // collision start/end chainages as mandatory stations on both pipes,
            // so overlap boundaries fall exactly on a cross-section.
            // ComputeInjections returns forced stations (boundaries + micro-gaps)
            // that bypass both distance-from-end and dedup guards in ComputeStations.
            var (forced, boundaries, crossingBands) = BoQOverlapResolver.ComputeInjections(sdrs);
            for (int i = 0; i < sdrs.Count; i++)
                sdrs[i].Stations = ComputeStations(sdrs[i], 0.5,
                    forcedStations:      forced[i],
                    crossingBoundaries:  boundaries[i]);

            // ── Step 2: Resolve all 3 preference scenarios per station (cached) ─
            // The new Clipper2 engine pre-computes every layer's net geometry under
            // Keep Upper / Keep Lower / 50-50 Split and caches them on each station.
            // (Legacy ApplyStationClashes is superseded and no longer called.)
            Dbg(ed, $"\n  [BoQ] Resolving overlap scenarios for {sdrs.Count} section(s)…");
            BoQOverlapResolver.Resolve(sdrs);
            BoQOverlapResolver.ApplyExcavationAveraging(sdrs, crossingBands);

            // Surface any "two pipes in exactly the same place" warnings.
            foreach (var w in BoQOverlapResolver.CoincidentPipeWarnings)
                Dbg(ed, $"\n  [UYARI] {w}");

            // ── Step 3: Aggregate volumes from the chosen (Kazı, Dolgu) prefs ──
            // Excavation follows the Kazı preference; bedding/surround/backfill all
            // follow the single Dolgu preference. Volumes come straight from the
            // cached scenario areas (prismatoid) — no geometry is recomputed.
            var kaziPref  = BoQScenarioAggregator.Map(excavAssign);
            var dolguPref = BoQScenarioAggregator.Map(backfillAssign);
            for (int i = 0; i < sections.Count && i < sdrs.Count; i++)
            {
                BoQScenarioAggregator.RecomputeRow(sdrs[i], kaziPref, dolguPref);
                sections[i].ExcavVol              = sdrs[i].VExcav;
                sections[i].VBedding              = sdrs[i].VBedding;
                sections[i].VSurround             = sdrs[i].VSurround;
                sections[i].VBackfill             = sdrs[i].VBackfill;
                sections[i].OverlapExcavDeducted    = sdrs[i].OverlapExcavDeducted;
                sections[i].OverlapBackfillDeducted = sdrs[i].OverlapBackfillDeducted;
            }

            // ── Step 3b: Print diagnostic coordinate table to command line ────
            PrintStationDebug(ed, sdrs);

            // ── Step 4: Aggregate Systems (PipeItems + Manholes) ─────────────
            var allIds = new SortedSet<int>(
                sections.Select(s => s.SystemId)
                .Concat(nodes.Values.Select(n => n.SystemId)));

            foreach (int sid in allIds)
            {
                string sysName = sysNames.ContainsKey(sid) ? sysNames[sid] : $"System_{sid}";
                var boq = new SystemBoQ { SystemName = sysName };

                foreach (var grp in sections
                    .Where(s => s.SystemId == sid)
                    .GroupBy(s => new { s.NominalDiamMm, s.Material })
                    .OrderBy(g => g.Key.NominalDiamMm))
                {
                    boq.Pipes.Add(new PipeItem
                    {
                        Diameter              = grp.Key.NominalDiamMm,
                        Material              = grp.Key.Material,
                        TotalLength           = grp.Sum(s => s.Length2D),
                        TotalExcavationVolume = grp.Sum(s => s.ExcavVol),
                        TotalBeddingVolume    = grp.Sum(s => s.VBedding),
                        TotalSurroundVolume   = grp.Sum(s => s.VSurround),
                        TotalBackfillVolume   = grp.Sum(s => s.VBackfill),
                        OverlapExcavDeducted    = grp.Sum(s => s.OverlapExcavDeducted),
                        OverlapBackfillDeducted = grp.Sum(s => s.OverlapBackfillDeducted)
                    });
                }

                foreach (var nd in nodes.Values
                    .Where(n => n.SystemId == sid)
                    .OrderBy(n => n.Name))
                {
                    boq.Manholes.Add(new ManholeItem
                    {
                        NodeName          = nd.Name,
                        X                 = nd.X,
                        Y                 = nd.Y,
                        TerrainElevation  = nd.TerrainZ,
                        Depth             = nd.Depth,
                        Diameter          = nd.MhDiameter,
                        Count             = 1,
                        ExcavationDepth   = nd.ExcavationDepth,
                        ExcavationVolume  = nd.ExcavationVolume,
                    });
                }

                report.Systems.Add(boq);
            }
        }

        // ── Volume update from per-station prismatoid ─────────────────────────

        private static void UpdateVolumesFromStations(SectionInfo sec, SectionDebugRow sdr)
        {
            var stats = sdr.Stations;
            if (stats == null || stats.Count < 2) return;

            double vExcav = 0, vBedding = 0, vSurround = 0, vBackfill = 0;
            double vExcavFull = 0, vBackfillFull = 0;

            for (int i = 0; i < stats.Count - 1; i++)
            {
                double dist = stats[i + 1].StationDist - stats[i].StationDist;
                if (dist <= 1e-9) continue;
                vExcav    += (stats[i].AreaExcavNet    + stats[i + 1].AreaExcavNet)    * 0.5 * dist;
                vBedding  += (stats[i].AreaBedding     + stats[i + 1].AreaBedding)     * 0.5 * dist;
                vSurround += (stats[i].AreaSurround    + stats[i + 1].AreaSurround)    * 0.5 * dist;
                vBackfill += (stats[i].AreaBackfillNet + stats[i + 1].AreaBackfillNet) * 0.5 * dist;
                vExcavFull    += (stats[i].AreaExcav    + stats[i + 1].AreaExcav)    * 0.5 * dist;
                vBackfillFull += (stats[i].AreaBackfill + stats[i + 1].AreaBackfill) * 0.5 * dist;
            }

            sec.ExcavVol             = vExcav;
            sec.VBedding             = vBedding;
            sec.VSurround            = vSurround;
            sec.VBackfill            = vBackfill;
            sec.OverlapExcavDeducted    = Math.Max(0, vExcavFull - vExcav);
            sec.OverlapBackfillDeducted = Math.Max(0, vBackfillFull - vBackfill);

            sdr.VExcav                  = vExcav;
            sdr.VBedding                = vBedding;
            sdr.VSurround               = vSurround;
            sdr.VBackfill               = vBackfill;
            sdr.OverlapExcavDeducted    = sec.OverlapExcavDeducted;
            sdr.OverlapBackfillDeducted = sec.OverlapBackfillDeducted;
        }

        // =====================================================================
        // Diagnostic: print per-station coordinate data to AutoCAD command line
        // =====================================================================

        private static void PrintStationDebug(Editor ed, List<SectionDebugRow> sdrs)
        {
            if (ed == null) return;
            ed.WriteMessage("\n\n══════════════ STATION COORDINATE DEBUG ══════════════");
            foreach (var sdr in sdrs)
            {
                double dx  = sdr.EndX - sdr.StartX, dy = sdr.EndY - sdr.StartY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                double nx  = len > 1e-9 ? -dy / len : 0;
                double ny  = len > 1e-9 ?  dx / len : 0;
                ed.WriteMessage($"\n\n── {sdr.PipeName}  Ø{sdr.DiameterMm}mm  L={sdr.Length2D:F1}m");
                ed.WriteMessage($"   Start=({sdr.StartX:F3},{sdr.StartY:F3})  End=({sdr.EndX:F3},{sdr.EndY:F3})  N=({nx:F4},{ny:F4})");

                foreach (var st in sdr.Stations)
                {
                    double uMinEx = 0, uMaxEx = 0;
                    if (st.ExcavPoly != null && st.ExcavPoly.Count > 0)
                    {
                        uMinEx = st.ExcavPoly[0][0]; uMaxEx = st.ExcavPoly[0][0];
                        foreach (var p in st.ExcavPoly) { if (p[0] < uMinEx) uMinEx = p[0]; if (p[0] > uMaxEx) uMaxEx = p[0]; }
                    }

                    string modInfo;
                    if (!st.HasOverlap)
                        modInfo = "no-overlap";
                    else if (st.ExcavPolyModified == null)
                        modInfo = "wins(full-poly)";
                    else if (st.ExcavPolyModified.Count == 0)
                        modInfo = "loses(no-solid)";
                    else
                    {
                        double uMin = st.ExcavPolyModified[0][0], uMax = st.ExcavPolyModified[0][0];
                        foreach (var p in st.ExcavPolyModified) { if (p[0] < uMin) uMin = p[0]; if (p[0] > uMax) uMax = p[0]; }
                        modInfo = $"MOD[{st.ExcavPolyModified.Count}vt] U=[{uMin:F3}..{uMax:F3}]";
                    }

                    ed.WriteMessage(
                        $"\n   st={st.StationDist,6:F1}m  WX={st.WorldX:F3} WY={st.WorldY:F3}" +
                        $"  Zinv={st.InvertZ:F3}  dep={st.TrueDepth:F3}" +
                        $"  ExcavU=[{uMinEx:F3}..{uMaxEx:F3}]  {modInfo}");
                }
            }
            ed.WriteMessage("\n══════════════════════════════════════════════════════\n");
        }

        // =====================================================================
        // Per-station cross-section interpolation
        // =====================================================================

        /// <summary>
        /// Samples the trench cross-section at regular intervals along the pipe.
        /// At every station, computes the full polygon coordinates for all four
        /// excavation layers in the local (U, Z) perpendicular frame (U=0 = pipe
        /// centreline axis).  Areas are computed from the polygons via the
        /// shoelace formula. Terrain and invert elevations are linearly
        /// interpolated between the start and end nodes.
        /// Always includes the start (t=0) and end (t=Length2D) stations.
        /// </summary>
        internal static List<CrossSectionStation> ComputeStations(
            SectionDebugRow row, double interval = 5.0,
            IEnumerable<double> extraStations = null,
            IEnumerable<double> forcedStations = null,
            IEnumerable<double> crossingBoundaries = null)
        {
            var stations = new List<CrossSectionStation>();
            if (row.Length2D <= 1e-9) return stations;

            // Constant half-widths (section-level — do not vary per station)
            double hwBase = row.TrWidth      * 0.5;
            double hwBed  = row.TopWidthBed  * 0.5;   // TrWidth/2 + TrBedHeight*SlopeRatio
            double hwSurr = row.TopWidthSurr * 0.5;   // hwBed + HSurround*SlopeRatio

            // ── Regular stations: grid + optional extras (both subject to guards) ──
            // Guards: must be > 1 mm from each pipe end; dedup within 1 mm.
            var dists = new List<double>();
            for (double g = 0; ; )
            {
                dists.Add(g);
                if (g >= row.Length2D - 1e-9) break;
                g = Math.Min(g + interval, row.Length2D);
            }
            if (extraStations != null)
                foreach (double d in extraStations)
                    if (d > 1e-3 && d < row.Length2D - 1e-3) dists.Add(d);
            dists.Sort();

            var allDists = new List<double>();
            double prevR = double.NegativeInfinity;
            foreach (double t in dists)
            {
                if (t - prevR < 1e-3) continue;
                prevR = t;
                allDists.Add(t);
            }

            // ── Forced stations: bypass BOTH guards (no distance-from-end limit,
            //    no 1 mm dedup). Only exact floating-point duplicates are skipped
            //    (1e-9 tolerance). Collision boundaries and their micro-stations
            //    are always forced so they survive even at the pipe endpoints or
            //    within 1 mm of a regular station.
            if (forcedStations != null)
            {
                double prevF = double.NegativeInfinity;
                foreach (double f in forcedStations
                    .Select(d => Math.Max(0.0, Math.Min(row.Length2D, d)))
                    .OrderBy(d => d))
                {
                    if (f - prevF < 1e-9) continue;
                    prevF = f;
                    allDists.Add(f);
                }
                allDists.Sort();
            }

            // Build a fast-lookup set of crossing-boundary chainages (1 µm tolerance).
            var boundarySet = new System.Collections.Generic.HashSet<double>();
            if (crossingBoundaries != null)
                foreach (double b in crossingBoundaries)
                    boundarySet.Add(Math.Max(0.0, Math.Min(row.Length2D, b)));

            double prevT = double.NegativeInfinity;
            foreach (double t in allDists)
            {
                if (t - prevT < 1e-9) continue;   // exact-dup safety on merged list
                prevT = t;

                double f        = Math.Min(t / row.Length2D, 1.0);
                double x        = row.StartX        + (row.EndX        - row.StartX)        * f;
                double y        = row.StartY        + (row.EndY        - row.StartY)        * f;
                double terrainZ = row.StartTerrainZ + (row.EndTerrainZ - row.StartTerrainZ) * f;
                double invertZ  = row.InvertStart   + (row.InvertEnd   - row.InvertStart)   * f;

                double depthToInv = Math.Max(0, terrainZ - invertZ);
                double trueDepth  = depthToInv + row.TrBedHeight;
                double topWExcav  = row.TrWidth + 2.0 * trueDepth * row.SlopeRatio;
                double hwExcav    = topWExcav * 0.5;

                // Z-levels at this station
                double zBot     = invertZ - row.TrBedHeight;                       // trench bottom
                double zTop     = terrainZ;                                         // ground surface
                double zSurrTop = Math.Min(invertZ + row.HSurround, zTop);         // top of surround

                // ── Polygon definitions (CCW in U-Z frame) ────────────────────
                //
                //  Excavation  : full trench from zBot → zTop
                //  Bedding     : zBot → invertZ       (constant widths hwBase/hwBed)
                //  Surround    : invertZ → zSurrTop   (constant widths hwBed/hwSurr)
                //  Backfill    : zSurrTop → zTop      (widths hwSurr/hwExcav vary per station)

                var excavPoly = new List<double[]>
                {
                    new[] { -hwBase,  zBot  },
                    new[] {  hwBase,  zBot  },
                    new[] {  hwExcav, zTop  },
                    new[] { -hwExcav, zTop  }
                };
                var beddingPoly = new List<double[]>
                {
                    new[] { -hwBase, zBot    },
                    new[] {  hwBase, zBot    },
                    new[] {  hwBed,  invertZ },
                    new[] { -hwBed,  invertZ }
                };
                var surroundPoly = new List<double[]>
                {
                    new[] { -hwBed,  invertZ  },
                    new[] {  hwBed,  invertZ  },
                    new[] {  hwSurr, zSurrTop },
                    new[] { -hwSurr, zSurrTop }
                };
                var backfillPoly = new List<double[]>
                {
                    new[] { -hwSurr,  zSurrTop },
                    new[] {  hwSurr,  zSurrTop },
                    new[] {  hwExcav, zTop     },
                    new[] { -hwExcav, zTop     }
                };

                // ── Areas (shoelace, snapped to (0.1 mm)² = 1e-8 m²) ────────
                double aExcav    = Math.Round(PolyArea2D(excavPoly),    8);
                double aBedding  = Math.Round(PolyArea2D(beddingPoly),  8);
                double aSurround = Math.Round(Math.Max(0, PolyArea2D(surroundPoly) - row.PipeArea), 8);
                double aBackfill = Math.Round(PolyArea2D(backfillPoly), 8);

                stations.Add(new CrossSectionStation
                {
                    StationDist     = t,
                    WorldX          = x,
                    WorldY          = y,
                    TerrainZ        = terrainZ,
                    InvertZ         = invertZ,
                    TrueDepth       = trueDepth,
                    TopWidthExcav   = topWExcav,
                    ExcavPoly       = excavPoly,
                    BeddingPoly     = beddingPoly,
                    SurroundPoly    = surroundPoly,
                    BackfillPoly    = backfillPoly,
                    AreaExcav            = aExcav,
                    AreaBedding          = aBedding,
                    AreaSurround         = aSurround,
                    AreaBackfill         = aBackfill,
                    AreaExcavNet         = aExcav,      // net = gross until overlap is applied
                    AreaBackfillNet      = aBackfill,
                    IsCrossingBoundary   = boundarySet.Count > 0 &&
                                           boundarySet.Any(b => Math.Abs(b - t) < 1e-6)
                });
            }

            return stations;
        }

        // ── 2-D polygon area (shoelace) ───────────────────────────────────────

        private static double PolyArea2D(List<double[]> poly)
        {
            double sum = 0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                double[] a = poly[i], b = poly[(i + 1) % n];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum) * 0.5;
        }

        // ── List<double[]> ↔ List<Vec2> converters ───────────────────────────

        private static List<Vec2> ToVec2List(List<double[]> poly)
        {
            var r = new List<Vec2>(poly?.Count ?? 0);
            if (poly != null)
                foreach (var p in poly)
                    if (p != null && p.Length >= 2) r.Add(new Vec2(p[0], p[1]));
            return r;
        }

        private static List<double[]> FromVec2List(List<Vec2> poly)
        {
            var r = new List<double[]>(poly?.Count ?? 0);
            if (poly != null)
                foreach (var v in poly) r.Add(new[] { v.X, v.Y });
            return r;
        }

        // ── Closest point on a segment ────────────────────────────────────────

        private static void ClosestPointOnSegment(
            double px, double py,
            double ax, double ay, double bx, double by,
            out double t, out double cx, out double cy)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-16) { t = 0; cx = ax; cy = ay; return; }
            t  = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
            cx = ax + t * dx;
            cy = ay + t * dy;
        }

        // =====================================================================
        // Per-station clash detection
        // =====================================================================
        //
        //  For every unique pair of sections (A, B) whose plan-view bounding
        //  boxes overlap, each station of A is tested against B's trench
        //  cross-section projected into A's local (U, Z) perpendicular frame.
        //
        //  Projection formula: B has a normalised axis T_B and perpendicular
        //  N_B.  A vertex of B at local coordinate (V, Z) in B's own frame
        //  maps to A's U-axis as:
        //
        //    U_A = U_center_B  +  V × (N_A · N_B)
        //        = U_center_B  +  V × (T_A · T_B)          [dot product identity]
        //    Z_A = Z                                         [unchanged — vertical]
        //
        //  The intersection area of A's ExcavPoly and the projected B polygon
        //  gives the per-station overlap area.  The deduction (full, half, or
        //  zero depending on OverlapAssignment and which pipe is deeper) is
        //  subtracted from AreaExcavNet / AreaBackfillNet.
        //  ExcavPolyModified stores the REMAINING polygon (A minus the overlap zone)
        //  so the 3-D solid builder can loft it directly.
        //
        //  Limitation: for nearly perpendicular pipes (T_A · T_B ≈ 0) the
        //  projection collapses to a line → zero intersection area → no
        //  per-station deduction.  This is geometrically correct in the
        //  cross-section frame but means the volume overlap for crossing pipes
        //  is captured at the stations nearest the crossing point only.
        // =====================================================================

        private static void ApplyStationClashes(
            List<SectionDebugRow> rows,
            OverlapAssignment     excavAssign,
            OverlapAssignment     backfillAssign)
        {
            if (rows == null || rows.Count < 2) return;

            // Pre-compute normalised axis + perpendicular for every section
            var axisDir = new Vec2[rows.Count];
            var perpDir = new Vec2[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var r  = rows[i];
                double dx  = r.EndX - r.StartX, dy = r.EndY - r.StartY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-9) { axisDir[i] = new Vec2(1, 0); perpDir[i] = new Vec2(0, 1); }
                else
                {
                    axisDir[i] = new Vec2(dx / len, dy / len);
                    perpDir[i] = new Vec2(-dy / len, dx / len);
                }
            }

            int clashPairs = 0;

            for (int ai = 0; ai < rows.Count - 1; ai++)
            {
                var rowA = rows[ai];
                if (rowA.Stations == null || rowA.Stations.Count == 0) continue;
                var Na = perpDir[ai];

                double amHW  = Math.Max(rowA.TopWidthExcavS, rowA.TopWidthExcavE) * 0.5;
                double aMinX = Math.Min(rowA.StartX, rowA.EndX) - amHW;
                double aMaxX = Math.Max(rowA.StartX, rowA.EndX) + amHW;
                double aMinY = Math.Min(rowA.StartY, rowA.EndY) - amHW;
                double aMaxY = Math.Max(rowA.StartY, rowA.EndY) + amHW;

                for (int bi = ai + 1; bi < rows.Count; bi++)
                {
                    var rowB = rows[bi];
                    if (rowB.Stations == null || rowB.Stations.Count == 0) continue;
                    var Nb = perpDir[bi];

                    // Quick AABB reject
                    double bmHW  = Math.Max(rowB.TopWidthExcavS, rowB.TopWidthExcavE) * 0.5;
                    double bMinX = Math.Min(rowB.StartX, rowB.EndX) - bmHW;
                    double bMaxX = Math.Max(rowB.StartX, rowB.EndX) + bmHW;
                    double bMinY = Math.Min(rowB.StartY, rowB.EndY) - bmHW;
                    double bMaxY = Math.Max(rowB.StartY, rowB.EndY) + bmHW;
                    if (aMaxX < bMinX || bMaxX < aMinX || aMaxY < bMinY || bMaxY < aMinY) continue;

                    // projFactor = N_A · N_B = T_A · T_B
                    double proj = axisDir[ai].X * axisDir[bi].X + axisDir[ai].Y * axisDir[bi].Y;

                    // Which pipe is deeper (lower invert = lower pipe)
                    double avgInvA = (rowA.InvertStart + rowA.InvertEnd) * 0.5;
                    double avgInvB = (rowB.InvertStart + rowB.InvertEnd) * 0.5;
                    bool aIsLower  = avgInvA <= avgInvB;

                    bool anyClash = false;

                    // ── A's stations: project B into A's U-Z frame ────────────
                    foreach (var sta in rowA.Stations)
                    {
                        if (sta.ExcavPoly == null || sta.ExcavPoly.Count < 3) continue;

                        ClosestPointOnSegment(sta.WorldX, sta.WorldY,
                            rowB.StartX, rowB.StartY, rowB.EndX, rowB.EndY,
                            out double tB, out double cxB, out double cyB);

                        // uBc  = B's centreline offset in A's perpendicular frame (for clip direction)
                        // uAinB = A's station offset in B's perpendicular frame (for correct boundary)
                        double uBc   = (cxB - sta.WorldX) * Na.X + (cyB - sta.WorldY) * Na.Y;
                        double uAinB = (sta.WorldX - cxB) * Nb.X + (sta.WorldY - cyB) * Nb.Y;

                        // Interpolate B's cross-section at tB
                        double invZB = rowB.InvertStart   + tB * (rowB.InvertEnd   - rowB.InvertStart);
                        double terZB = rowB.StartTerrainZ + tB * (rowB.EndTerrainZ - rowB.StartTerrainZ);
                        double depB  = Math.Max(0, terZB - invZB);
                        double tdB   = depB + rowB.TrBedHeight;
                        double topWB = rowB.TrWidth + 2.0 * tdB * rowB.SlopeRatio;
                        double zBotB = invZB - rowB.TrBedHeight;
                        double hwBB  = rowB.TrWidth * 0.5;
                        double hwTB  = topWB * 0.5;

                        // Build B's region in A's U-Z frame.
                        // Correct formula: (u,Z) in A's cross-section is inside B's trench iff
                        //   |uAinB + u*proj| <= hw_B(Z)
                        // => boundary at u = (±hw_B - uAinB) / proj
                        // For proj ≈ 0 (perpendicular pipes): B covers A's full U range if
                        //   A's centreline (uAinB) is within B's half-width, else no overlap.
                        if (sta.ExcavPolyModified != null && sta.ExcavPolyModified.Count == 0) continue;
                        var aVec    = (sta.ExcavPolyModified != null && sta.ExcavPolyModified.Count >= 3)
                                      ? ToVec2List(sta.ExcavPolyModified)
                                      : ToVec2List(sta.ExcavPoly);
                        double uMinA = aVec.Min(p => p.X), uMaxA = aVec.Max(p => p.X);
                        List<Vec2> bInA;
                        {
                            double absP = Math.Abs(proj);
                            if (absP < 1e-4)
                            {
                                if (Math.Abs(uAinB) > (hwBB + hwTB) * 0.5 + 1e-9) continue;
                                bInA = EnsureCCW(new List<Vec2>
                                {
                                    new Vec2(uMinA, zBotB), new Vec2(uMaxA, zBotB),
                                    new Vec2(uMaxA, terZB), new Vec2(uMinA, terZB)
                                });
                            }
                            else
                            {
                                double uLB = (-hwBB - uAinB) / proj, uRB = ( hwBB - uAinB) / proj;
                                double uLT = (-hwTB - uAinB) / proj, uRT = ( hwTB - uAinB) / proj;
                                if (proj < 0) { double t; t=uLB; uLB=uRB; uRB=t; t=uLT; uLT=uRT; uRT=t; }
                                uLB = Math.Max(uMinA, Math.Min(uMaxA, uLB));
                                uRB = Math.Max(uMinA, Math.Min(uMaxA, uRB));
                                uLT = Math.Max(uMinA, Math.Min(uMaxA, uLT));
                                uRT = Math.Max(uMinA, Math.Min(uMaxA, uRT));
                                if (Math.Abs(uRB - uLB) < 1e-9 && Math.Abs(uRT - uLT) < 1e-9) continue;
                                bInA = EnsureCCW(new List<Vec2>
                                {
                                    new Vec2(uLB, zBotB), new Vec2(uRB, zBotB),
                                    new Vec2(uRT, terZB), new Vec2(uLT, terZB)
                                });
                            }
                        }
                        var inter = PolygonIntersection(aVec, bInA);
                        if (inter.Count < 3) continue;
                        double interArea = PolygonArea(inter);
                        if (interArea < 1e-7) continue;

                        sta.HasOverlap = true;

                        // Clip excav cumulatively: each clash cuts from the previous result
                        var remEx = ComputeRemainingPoly(aVec, bInA, uBc, aIsLower, excavAssign);
                        if (remEx != null)  // null = A wins this clash → polygon unchanged
                            sta.ExcavPolyModified = remEx.Count >= 3
                                ? FromVec2List(remEx) : new List<double[]>();

                        // Clip backfill cumulatively
                        if (sta.BackfillPoly != null && sta.BackfillPoly.Count >= 3)
                        {
                            var bfBase = (sta.BackfillPolyModified != null && sta.BackfillPolyModified.Count >= 3)
                                         ? ToVec2List(sta.BackfillPolyModified)
                                         : ToVec2List(sta.BackfillPoly);
                            var remBf = ComputeRemainingPoly(bfBase, bInA, uBc, aIsLower, backfillAssign);
                            if (remBf != null)
                                sta.BackfillPolyModified = remBf.Count >= 3
                                    ? FromVec2List(remBf) : new List<double[]>();
                        }
                        anyClash = true;
                    }

                    // ── B's stations: project A into B's U-Z frame ────────────
                    foreach (var stb in rowB.Stations)
                    {
                        if (stb.ExcavPoly == null || stb.ExcavPoly.Count < 3) continue;

                        ClosestPointOnSegment(stb.WorldX, stb.WorldY,
                            rowA.StartX, rowA.StartY, rowA.EndX, rowA.EndY,
                            out double tA, out double cxA, out double cyA);

                        double uAc   = (cxA - stb.WorldX) * Nb.X + (cyA - stb.WorldY) * Nb.Y;
                        double uBinA = (stb.WorldX - cxA) * Na.X + (stb.WorldY - cyA) * Na.Y;

                        double invZA = rowA.InvertStart   + tA * (rowA.InvertEnd   - rowA.InvertStart);
                        double terZA = rowA.StartTerrainZ + tA * (rowA.EndTerrainZ - rowA.StartTerrainZ);
                        double depA  = Math.Max(0, terZA - invZA);
                        double tdA   = depA + rowA.TrBedHeight;
                        double topWA = rowA.TrWidth + 2.0 * tdA * rowA.SlopeRatio;
                        double zBotA = invZA - rowA.TrBedHeight;
                        double hwBA  = rowA.TrWidth * 0.5;
                        double hwTA  = topWA * 0.5;

                        if (stb.ExcavPolyModified != null && stb.ExcavPolyModified.Count == 0) continue;
                        var bVec    = (stb.ExcavPolyModified != null && stb.ExcavPolyModified.Count >= 3)
                                      ? ToVec2List(stb.ExcavPolyModified)
                                      : ToVec2List(stb.ExcavPoly);
                        double uMinB = bVec.Min(p => p.X), uMaxB = bVec.Max(p => p.X);
                        List<Vec2> aInB;
                        {
                            double absP = Math.Abs(proj);
                            if (absP < 1e-4)
                            {
                                if (Math.Abs(uBinA) > (hwBA + hwTA) * 0.5 + 1e-9) continue;
                                aInB = EnsureCCW(new List<Vec2>
                                {
                                    new Vec2(uMinB, zBotA), new Vec2(uMaxB, zBotA),
                                    new Vec2(uMaxB, terZA), new Vec2(uMinB, terZA)
                                });
                            }
                            else
                            {
                                double uLB = (-hwBA - uBinA) / proj, uRB = ( hwBA - uBinA) / proj;
                                double uLT = (-hwTA - uBinA) / proj, uRT = ( hwTA - uBinA) / proj;
                                if (proj < 0) { double t; t=uLB; uLB=uRB; uRB=t; t=uLT; uLT=uRT; uRT=t; }
                                uLB = Math.Max(uMinB, Math.Min(uMaxB, uLB));
                                uRB = Math.Max(uMinB, Math.Min(uMaxB, uRB));
                                uLT = Math.Max(uMinB, Math.Min(uMaxB, uLT));
                                uRT = Math.Max(uMinB, Math.Min(uMaxB, uRT));
                                if (Math.Abs(uRB - uLB) < 1e-9 && Math.Abs(uRT - uLT) < 1e-9) continue;
                                aInB = EnsureCCW(new List<Vec2>
                                {
                                    new Vec2(uLB, zBotA), new Vec2(uRB, zBotA),
                                    new Vec2(uRT, terZA), new Vec2(uLT, terZA)
                                });
                            }
                        }
                        var inter = PolygonIntersection(bVec, aInB);
                        if (inter.Count < 3) continue;
                        double interArea = PolygonArea(inter);
                        if (interArea < 1e-7) continue;

                        stb.HasOverlap = true;

                        // Clip excav cumulatively
                        var remExB = ComputeRemainingPoly(bVec, aInB, uAc, !aIsLower, excavAssign);
                        if (remExB != null)
                            stb.ExcavPolyModified = remExB.Count >= 3
                                ? FromVec2List(remExB) : new List<double[]>();

                        // Clip backfill cumulatively
                        if (stb.BackfillPoly != null && stb.BackfillPoly.Count >= 3)
                        {
                            var bfBase2 = (stb.BackfillPolyModified != null && stb.BackfillPolyModified.Count >= 3)
                                          ? ToVec2List(stb.BackfillPolyModified)
                                          : ToVec2List(stb.BackfillPoly);
                            var remBfB = ComputeRemainingPoly(bfBase2, aInB, uAc, !aIsLower, backfillAssign);
                            if (remBfB != null)
                                stb.BackfillPolyModified = remBfB.Count >= 3
                                    ? FromVec2List(remBfB) : new List<double[]>();
                        }
                        anyClash = true;
                    }

                    if (anyClash)
                    {
                        clashPairs++;
                        string la = rowA.PipeName, lb = rowB.PipeName;
                        rowA.ClashLog.Add(
                            $"OVERLAP with [{lb}] ({(aIsLower ? "A=lower" : "A=upper")}): " +
                            $"excav={excavAssign}, backfill={backfillAssign}");
                        rowB.ClashLog.Add(
                            $"OVERLAP with [{la}] ({(aIsLower ? "B=upper" : "B=lower")}): " +
                            $"excav={excavAssign}, backfill={backfillAssign}");
                    }
                }
            }

            // Derive net areas from the final clipped polygons — single source of truth
            // so that AreaExcavNet is always consistent with the geometry used for 3-D lofting.
            foreach (var row in rows)
            {
                if (row.Stations == null) continue;
                foreach (var sta in row.Stations)
                {
                    if (sta.ExcavPolyModified != null)
                        sta.AreaExcavNet = sta.ExcavPolyModified.Count >= 3
                            ? Math.Round(PolyArea2D(sta.ExcavPolyModified), 8) : 0.0;
                    if (sta.BackfillPolyModified != null)
                        sta.AreaBackfillNet = sta.BackfillPolyModified.Count >= 3
                            ? Math.Round(PolyArea2D(sta.BackfillPolyModified), 8) : 0.0;
                }
            }
        }

        // =====================================================================
        // Helpers
        // =====================================================================

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
                if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t)) d[t] = v;
            }
            return d;
        }

        private static string GetProp(Dictionary<string, string> d, string key)
            => (d != null && d.ContainsKey(key)) ? d[key] : "";

        private static Dictionary<string, string> CatLookup(
            Dictionary<string, Dictionary<string, string>> cat, string guid)
        {
            if (string.IsNullOrEmpty(guid)) return null;
            return cat.ContainsKey(guid.ToUpperInvariant()) ? cat[guid.ToUpperInvariant()] : null;
        }

        // ── Value decoders ────────────────────────────────────────────────────

        /// <summary>
        /// Converts an LL10/LL11 elevation to AkarKot (pipe invert = inner-bottom flow level)
        /// using the per-pipe LLPOS measurement position stored in Urbano XML.
        /// Verified against test4.7.xml (AkarKot=97.93, OD=1.14m, ID=1.00m, t=0.07m):
        ///   1 = Üst dış  (outer top)    → AkarKot = LL − (OD+ID)/2   [NOT LL−OD]
        ///   2 = Üst iç   (inner top)    → AkarKot = LL − ID
        ///   4 = Aks      (centreline)   → AkarKot = LL − ID/2
        ///   8 = Alt iç   (inner bottom) → AkarKot = LL  (direct, most common)
        ///  16 = Alt dış  (outer bottom) → AkarKot = LL + (OD−ID)/2
        /// Geometry: Üst dış = AkarKot + ID + t = AkarKot + (OD+ID)/2
        /// </summary>
        private static double LlToInvert(double ll, int llpos, double odM, double idM)
        {
            switch (llpos)
            {
                case 1:  return ll - (odM + idM) / 2.0;  // Üst dış: outer top
                case 2:  return ll - idM;                 // Üst iç:  inner top
                case 4:  return ll - idM / 2.0;           // Aks:     centreline
                case 16: return ll + (odM - idM) / 2.0;  // Alt dış: outer bottom
                default: return ll;                        // 8 = Alt iç (AkarKot direct)
            }
        }

        private static int DecodeIntProp(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;
            string s = raw.StartsWith("5003") ? raw.Substring(4)
                     : raw.StartsWith("5005") ? raw.Substring(4)
                     : raw;
            return int.TryParse(s, out int v) ? v : 0;
        }

        private static string DecodeStrProp(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("5005")) return raw.Substring(4);
            if (raw.StartsWith("5003")) return raw.Substring(4);
            return raw;
        }

        /// <summary>
        /// Universal float decoder.
        ///
        /// Strategy (in order):
        ///  1. Apply HexFloatRx to the raw string – extracts the clean hex-float
        ///     token regardless of any "8001"/"8005" prefix or surrounding garbage.
        ///  2. If no hex-float found, strip known prefixes and try plain decimal.
        /// </summary>
        private static double DecodeFloatProp(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;

            // Strategy 1: Regex extracts the clean hex-float token
            Match m = HexFloatRx.Match(raw);
            if (m.Success) return DecodeHexDouble(m.Value);

            // Strategy 2: strip prefix, parse as plain decimal
            string s = raw.StartsWith("8001") ? raw.Substring(4)
                     : raw.StartsWith("8005") ? raw.Substring(4)
                     : raw.StartsWith("5003") ? raw.Substring(4)
                     : raw.StartsWith("5005") ? raw.Substring(4)
                     : raw;
            return double.TryParse(s.Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        private static string DecodeGuidStr(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            string s = DecodeStrProp(raw);
            var m = GuidRx.Match(s);
            return m.Success ? m.Value.ToUpperInvariant() : s.ToUpperInvariant().Trim();
        }

        // ── @pos coordinate parser ────────────────────────────────────────────

        /// <summary>
        /// Parse the @pos attribute using HexFloatRx directly on the full string.
        ///
        /// The separator inside @pos is the literal 9-char ASCII string "=EF=BE=89"
        /// (quoted-printable for UTF-8 bytes 0xEF 0xBE 0x89).  It is NOT the Unicode
        /// character U+FE09, so Split-based approaches always fail.
        ///
        /// By running Regex.Matches on the entire raw string we get:
        ///   Matches[0] → X hex-float
        ///   Matches[1] → Y hex-float
        ///   Matches[2] → Z hex-float
        /// regardless of any prefix, suffix, or separator format.
        /// </summary>
        private static void ParsePos(string raw, out double x, out double y)
        {
            x = 0; y = 0;
            if (string.IsNullOrEmpty(raw)) return;

            MatchCollection matches = HexFloatRx.Matches(raw);
            if (matches.Count >= 2)
            {
                x = DecodeHexDouble(matches[0].Value);
                y = DecodeHexDouble(matches[1].Value);
                return;
            }

            // Fallback: plain-decimal Readable XML (no hex-floats present)
            // Strip "8005" prefix then walk for decimal numbers
            string s = raw.StartsWith("8005", StringComparison.Ordinal)
                ? raw.Substring(4) : raw;

            int i = 0;
            // X
            int xStart = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
            if (i > xStart)
                double.TryParse(s.Substring(xStart, i - xStart),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out x);

            while (i < s.Length && !char.IsDigit(s[i]) && s[i] != '-') i++;

            // Y
            int yStart = i;
            if (i < s.Length && s[i] == '-') i++;
            while (i < s.Length && char.IsDigit(s[i])) i++;
            if (i < s.Length && s[i] == '.') { i++; while (i < s.Length && char.IsDigit(s[i])) i++; }
            if (i > yStart)
                double.TryParse(s.Substring(yStart, i - yStart),
                    NumberStyles.Float, CultureInfo.InvariantCulture, out y);
        }

        // ── Manhole nominal diameter ───────────────────────────────────────────

        /// <summary>
        /// Extract the nominal internal diameter of a manhole from CATALOGITEM_NAME.
        ///
        /// Urbano encodes the Φ separator as the quoted-printable sequence "=EF=BF=98"
        /// and places the nominal diameter immediately after it:
        ///   "SD_Tip1 =EF=BF=981000_=EF=BF=98400-=EF=BF=98500 1300 mm"
        ///                       ^^^^                                    ← nominal (1000)
        ///   "SD_Tip2 =EF=BF=981500_=EF=BF=98600"
        ///                       ^^^^                                    ← nominal (1500)
        ///
        /// Strategy: match one or more "=XX" tokens immediately followed by a 3–4 digit
        /// run.  The FIRST such group is always the shaft nominal diameter; subsequent
        /// groups are pipe diameters that are irrelevant for this column.
        /// </summary>
        private static int ExtractManholeNominalDiam(
            Dictionary<string, string> mhProps)
        {
            string catName = DecodeStrProp(GetProp(mhProps, "CATALOGITEM_NAME"));

            // Match the digits that sit directly after the Φ encoded as =XX=XX=XX...
            // e.g. "=EF=BF=981500" → Groups[1] = "1500"
            Match m = Regex.Match(catName, @"(?:=[0-9A-Fa-f]{2})+(\d{3,4})");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int v))
                return v;

            return 0;
        }

        // ── Geometry ─────────────────────────────────────────────────────────

        private static double CotDeg(double deg)
        {
            if (deg <= 0 || deg >= 90) return 0;
            double rad = deg * Math.PI / 180.0;
            return Math.Cos(rad) / Math.Sin(rad);
        }

        private static int RoundToNearest(double value, int nearest)
            => (int)(Math.Round(value / nearest) * nearest);

        private static void Dbg(Editor ed, string msg) => ed?.WriteMessage(msg);

        // ── C99 hex-float decoder ─────────────────────────────────────────────
        //   "0x1.52CAEp+15" → 42633.xxx
        //   "-0x1.4d13d6a9e1ad0p+15" → -42633.92

        private static double DecodeHexDouble(string hex)
        {
            try
            {
                bool   neg = hex.StartsWith("-");
                string s   = neg ? hex.Substring(1) : hex;
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s.Substring(2);

                int pIdx = s.IndexOfAny(new[] { 'p', 'P' });
                if (pIdx < 0) return 0;

                int exp = int.Parse(s.Substring(pIdx + 1),
                    NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);

                string mantPart  = s.Substring(0, pIdx);
                string[] mParts  = mantPart.Split('.');
                long intBits  = long.Parse(mParts[0], NumberStyles.HexNumber);
                long fracBits = 0; int fracLen = 0;
                if (mParts.Length > 1 && mParts[1].Length > 0)
                {
                    fracBits = long.Parse(mParts[1], NumberStyles.HexNumber);
                    fracLen  = mParts[1].Length * 4;
                }

                // ── Zero check ────────────────────────────────────────────────
                // "0x0.0000000000000p+0" → intBits=0, fracBits=0.
                // Without this guard biasedExp = 0+1023 = 1023 and the
                // IEEE-754 bit pattern evaluates to 1.0 instead of 0.0.
                if (intBits == 0 && fracBits == 0) return 0.0;

                long biasedExp = exp + 1023;
                long frac52    = fracLen <= 52 ? (fracBits << (52 - fracLen))
                                               : (fracBits >> (fracLen - 52));
                long bits = (biasedExp << 52) | (frac52 & 0x000FFFFFFFFFFFFFL);
                double val = BitConverter.Int64BitsToDouble(bits);
                return neg ? -val : val;
            }
            catch { return 0; }
        }

        // ── Robust XML loader ─────────────────────────────────────────────────

        private static XDocument LoadXmlRobust(string path)
        {
            try { return XDocument.Load(path); }
            catch (System.Xml.XmlException) { }

            byte[] raw = File.ReadAllBytes(path);
            foreach (int cp in new[] { 0, 1254, 1252, 28591 })
            {
                try
                {
                    string txt = cp == 0
                        ? Encoding.Default.GetString(raw)
                        : Encoding.GetEncoding(cp).GetString(raw);
                    return XDocument.Parse(
                        XmlDeclRx.Replace(txt,
                            "<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
                }
                catch { }
            }
            throw new System.Exception(
                "XML load failed – tried UTF-8, system default, cp1254, cp1252, iso-8859-1.");
        }

        // =====================================================================
        // 2-D Trench Clash Detection
        // =====================================================================
        //
        //  Algorithm overview
        //  ──────────────────
        //  For every pipe section we build a 2-D quadrilateral "trench footprint"
        //  that represents the top opening of the excavation trench as seen from
        //  above. The footprint is a trapezoid because TopWidthExcav can differ
        //  at the start and end nodes.
        //
        //  Pairs of footprints are tested with the Sutherland-Hodgman polygon
        //  clipping algorithm (requires CCW convex polygons) to obtain the exact
        //  intersection polygon, whose area is then computed with the shoelace
        //  formula.
        //
        //  Excavation overlap ≈ Intersection_Area × avg(TrueDepth_A, TrueDepth_B)
        //
        //  The shared volume is split into an EXCAVATION part and a (smaller)
        //  BACKFILL part. The backfill fraction R_bf comes from each pipe's own
        //  cross-section areas (ABackfill / AExcav) — so Bedding (Yataklama) and
        //  Surround (Gömlekleme) are NEVER double-deducted, exactly as required.
        //
        //  Each part is then assigned between the deeper pipe ("lower line",
        //  الخط الأدنى) and the shallower pipe ("upper line", الخط الأعلى)
        //  according to an OverlapAssignment chosen INDEPENDENTLY for excavation
        //  and for backfill (3 × 3 = 9 combinations):
        //
        //    Split     → 50/50: each pipe loses half           (مناصفة)
        //    LowerPipe → lower keeps full, all deducted from upper
        //    UpperPipe → upper keeps full, all deducted from lower
        // =====================================================================

        // ── Lightweight 2-D vector ────────────────────────────────────────────

        private struct Vec2
        {
            public readonly double X, Y;
            public Vec2(double x, double y) { X = x; Y = y; }

            public static Vec2 operator +(Vec2 a, Vec2 b) => new Vec2(a.X + b.X, a.Y + b.Y);
            public static Vec2 operator -(Vec2 a, Vec2 b) => new Vec2(a.X - b.X, a.Y - b.Y);
            public static Vec2 operator *(Vec2 v, double s) => new Vec2(v.X * s, v.Y * s);

            /// <summary>2-D "cross product" (z-component of the 3-D cross product).</summary>
            public double Cross(Vec2 o) => X * o.Y - Y * o.X;

            public double LengthSq => X * X + Y * Y;
            public double Length   => Math.Sqrt(LengthSq);

            public Vec2 Normalized()
            {
                double l = Length;
                return l > 1e-12 ? new Vec2(X / l, Y / l) : new Vec2(0, 0);
            }
        }

        // ── Footprint builder ─────────────────────────────────────────────────

        /// <summary>
        /// Returns the 4-corner 2-D trench-opening polygon for one pipe section.
        ///
        ///  Width at the start node : TopWidthExcavS   (W + 2 × TrueDepthStart × SlopeRatio)
        ///  Width at the end   node : TopWidthExcavE   (W + 2 × TrueDepthEnd   × SlopeRatio)
        ///
        ///  The resulting quadrilateral is a trapezoid in the general case and a
        ///  rectangle when both widths are equal.  Vertices are ordered CCW.
        /// </summary>
        private static List<Vec2> BuildTrenchFootprint(
            SectionInfo sec, NodeInfo sn, NodeInfo en)
        {
            var d = new Vec2(en.X - sn.X, en.Y - sn.Y);
            if (d.LengthSq < 1e-16) return new List<Vec2>();  // degenerate pipe

            var dir = d.Normalized();
            // Left-normal (CCW 90° rotation of the direction vector)
            var nor = new Vec2(-dir.Y, dir.X);

            double hwS = sec.TopWidthExcavS * 0.5;
            double hwE = sec.TopWidthExcavE * 0.5;
            var ps = new Vec2(sn.X, sn.Y);
            var pe = new Vec2(en.X, en.Y);

            // Vertices in counter-clockwise order:
            //   bottom-left → bottom-right → top-right → top-left
            //   (where "bottom" = start node, "top" = end node)
            return new List<Vec2>
            {
                ps + nor * (-hwS),   // start, right side  (-normal)
                pe + nor * (-hwE),   // end,   right side
                pe + nor *   hwE,    // end,   left  side
                ps + nor *   hwS,    // start, left  side
            };
        }

        // ── Remaining-polygon computation (for 3-D solid lofting) ────────────

        /// <summary>
        /// Computes the cross-section polygon that remains in A's trench after
        /// removing the overlap zone with B's projected polygon.
        ///
        /// Returns null when A wins the assignment (keeps full polygon) or there is
        /// no Z-range overlap between A and B. Returns an empty list (Count == 0)
        /// when B's region consumes A's entire cross-section (no solid here).
        ///
        /// The clip boundary is the dividing line between A and B:
        ///   Split       → midpoint between A's facing edge and B's inner edge
        ///   LowerPipe / UpperPipe (B wins) → B's inner edge itself
        /// </summary>
        private static List<Vec2> ComputeRemainingPoly(
            List<Vec2> aVec,            // A's polygon (Vec2.X = U, Vec2.Y = Z)
            List<Vec2> bInA,            // B projected into A's U-Z frame
            double     uBc,             // B's centreline U in A's frame
            bool       aIsLower,        // true when A's invert is deeper
            OverlapAssignment assign)
        {
            // A wins → keep full polygon → no modification
            bool aWins = (assign == OverlapAssignment.LowerPipe &&  aIsLower)
                      || (assign == OverlapAssignment.UpperPipe && !aIsLower);
            if (aWins) return null;

            // Z range of the intersection zone
            double aZbot = double.MaxValue, aZtop = double.MinValue;
            foreach (var p in aVec)  { if (p.Y < aZbot) aZbot = p.Y; if (p.Y > aZtop) aZtop = p.Y; }
            double bZbot = double.MaxValue, bZtop = double.MinValue;
            foreach (var p in bInA) { if (p.Y < bZbot) bZbot = p.Y; if (p.Y > bZtop) bZtop = p.Y; }
            double zBot = Math.Max(aZbot, bZbot);
            double zTop = Math.Min(aZtop, bZtop);
            if (zTop <= zBot + 1e-9) return null;   // no Z overlap → nothing to clip

            bool bToRight = uBc >= 0;

            // B's inner edge at the Z-overlap extremes
            double bEdgeBot = bToRight ? PolyLeftEdgeAtZ(bInA, zBot)  : PolyRightEdgeAtZ(bInA, zBot);
            double bEdgeTop = bToRight ? PolyLeftEdgeAtZ(bInA, zTop)  : PolyRightEdgeAtZ(bInA, zTop);

            double divBot, divTop;
            if (assign == OverlapAssignment.Split)
            {
                // Midpoint between A's facing edge and B's inner edge
                double aEdgeBot = bToRight ? PolyRightEdgeAtZ(aVec, zBot) : PolyLeftEdgeAtZ(aVec, zBot);
                double aEdgeTop = bToRight ? PolyRightEdgeAtZ(aVec, zTop) : PolyLeftEdgeAtZ(aVec, zTop);
                divBot = (aEdgeBot + bEdgeBot) * 0.5;
                divTop = (aEdgeTop + bEdgeTop) * 0.5;
            }
            else   // B wins entirely → clip A at B's inner boundary
            {
                divBot = bEdgeBot;
                divTop = bEdgeTop;
            }

            // Directed edge for Sutherland-Hodgman: keep the LEFT side
            //   bToRight → keep smaller-U side → directed edge goes upward (bot→top)
            //   bToLeft  → keep larger-U  side → directed edge goes downward (top→bot)
            Vec2 e0, e1;
            if (bToRight) { e0 = new Vec2(divBot, zBot); e1 = new Vec2(divTop, zTop); }
            else          { e0 = new Vec2(divTop, zTop); e1 = new Vec2(divBot, zBot); }

            var clipped = ClipByHalfplane(aVec, e0, e1);
            return clipped.Count >= 3 ? clipped : new List<Vec2>();
        }

        /// <summary>Returns the maximum U value on any polygon edge at the given Z.</summary>
        private static double PolyRightEdgeAtZ(List<Vec2> poly, double z)
        {
            double best = double.MinValue;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                double zlo = Math.Min(a.Y, b.Y), zhi = Math.Max(a.Y, b.Y);
                if (zhi - zlo < 1e-12 || z < zlo - 1e-9 || z > zhi + 1e-9) continue;
                double t = (z - a.Y) / (b.Y - a.Y);
                double u = a.X + (b.X - a.X) * t;
                if (u > best) best = u;
            }
            return best == double.MinValue ? poly.Max(p => p.X) : best;
        }

        /// <summary>Returns the minimum U value on any polygon edge at the given Z.</summary>
        private static double PolyLeftEdgeAtZ(List<Vec2> poly, double z)
        {
            double best = double.MaxValue;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % poly.Count];
                double zlo = Math.Min(a.Y, b.Y), zhi = Math.Max(a.Y, b.Y);
                if (zhi - zlo < 1e-12 || z < zlo - 1e-9 || z > zhi + 1e-9) continue;
                double t = (z - a.Y) / (b.Y - a.Y);
                double u = a.X + (b.X - a.X) * t;
                if (u < best) best = u;
            }
            return best == double.MaxValue ? poly.Min(p => p.X) : best;
        }

        // ── CCW normalisation ─────────────────────────────────────────────────

        /// <summary>
        /// Ensures the polygon vertices are in counter-clockwise order.
        /// Sutherland-Hodgman requires the clip polygon to be CCW.
        /// The shoelace formula for the signed area: positive ↔ CCW.
        /// </summary>
        private static List<Vec2> EnsureCCW(List<Vec2> poly)
        {
            double signedArea = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                signedArea += a.Cross(b);   // shoelace term  a.X*b.Y − a.Y*b.X
            }
            // signedArea > 0 → CCW (standard math / CAD coords with Y-up)
            if (signedArea < 0)
            {
                var rev = new List<Vec2>(poly);
                rev.Reverse();
                return rev;
            }
            return poly;
        }

        // ── Quick AABB pre-check ──────────────────────────────────────────────

        /// <summary>
        /// Axis-aligned bounding-box overlap test.  Cheap first pass that skips
        /// the full polygon intersection for clearly separated pipe pairs.
        /// </summary>
        private static bool AabbOverlap(List<Vec2> a, List<Vec2> b)
        {
            double aX0 = double.MaxValue, aX1 = double.MinValue;
            double aY0 = double.MaxValue, aY1 = double.MinValue;
            foreach (var v in a)
            {
                if (v.X < aX0) aX0 = v.X; if (v.X > aX1) aX1 = v.X;
                if (v.Y < aY0) aY0 = v.Y; if (v.Y > aY1) aY1 = v.Y;
            }
            double bX0 = double.MaxValue, bX1 = double.MinValue;
            double bY0 = double.MaxValue, bY1 = double.MinValue;
            foreach (var v in b)
            {
                if (v.X < bX0) bX0 = v.X; if (v.X > bX1) bX1 = v.X;
                if (v.Y < bY0) bY0 = v.Y; if (v.Y > bY1) bY1 = v.Y;
            }
            return aX1 >= bX0 && bX1 >= aX0 && aY1 >= bY0 && bY1 >= aY0;
        }

        // ── Sutherland-Hodgman clipping ───────────────────────────────────────

        /// <summary>
        /// Clips the <paramref name="subject"/> polygon against the half-plane
        /// defined by the directed edge <c>edgeStart → edgeEnd</c>.
        /// "Inside" = to the LEFT of the directed edge (CCW convention).
        /// </summary>
        private static List<Vec2> ClipByHalfplane(
            List<Vec2> subject, Vec2 edgeStart, Vec2 edgeEnd)
        {
            var output = new List<Vec2>(subject.Count + 1);
            if (subject.Count == 0) return output;

            Vec2 edir = edgeEnd - edgeStart;

            for (int i = 0; i < subject.Count; i++)
            {
                Vec2 cur = subject[i];
                Vec2 nxt = subject[(i + 1) % subject.Count];

                bool curIn = edir.Cross(cur - edgeStart) >= 0;
                bool nxtIn = edir.Cross(nxt - edgeStart) >= 0;

                if (curIn) output.Add(cur);

                if (curIn != nxtIn)
                {
                    // Parametric intersection of segment [cur, nxt] with the clip line:
                    //   t = −edir.Cross(cur − edgeStart) / edir.Cross(nxt − cur)
                    Vec2   seg   = nxt - cur;
                    double denom = edir.Cross(seg);
                    if (Math.Abs(denom) > 1e-14)
                    {
                        double t = -edir.Cross(cur - edgeStart) / denom;
                        output.Add(cur + seg * t);
                    }
                }
            }
            return output;
        }

        /// <summary>
        /// Returns the intersection polygon of two convex CCW polygons using
        /// the Sutherland-Hodgman algorithm.  Returns an empty list if the
        /// polygons do not intersect.
        /// </summary>
        private static List<Vec2> PolygonIntersection(
            List<Vec2> subject, List<Vec2> clip)
        {
            var result = new List<Vec2>(subject);
            for (int i = 0; i < clip.Count && result.Count > 0; i++)
                result = ClipByHalfplane(result, clip[i], clip[(i + 1) % clip.Count]);
            return result;
        }

        /// <summary>Shoelace area of a simple polygon (sign-agnostic).</summary>
        private static double PolygonArea(List<Vec2> poly)
        {
            double sum = 0;
            for (int i = 0; i < poly.Count; i++)
            {
                var a = poly[i];
                var b = poly[(i + 1) % poly.Count];
                sum += a.Cross(b);
            }
            return Math.Abs(sum) * 0.5;
        }

        // ── Main clash-detection pass ─────────────────────────────────────────

        /// <summary>
        /// For every pair of pipe sections, checks whether their 2-D trench
        /// footprints overlap.  When they do:
        ///
        ///   Excavation_Overlap = Intersection_Area × avg(TrueDepth_A, TrueDepth_B)
        ///   Backfill_Overlap   = Excavation_Overlap × R_bf
        ///        where R_bf = avg( ABackfill / AExcav )  over the two pipes,
        ///        i.e. the share of the trench that is backfill (not bedding/surround).
        ///
        /// The two overlap volumes are then assigned between the deeper ("lower")
        /// and shallower ("upper") pipe according to <paramref name="excavAssignment"/>
        /// and <paramref name="backfillAssignment"/>:
        ///
        ///       Split     → 50/50              (مناصفة)
        ///       LowerPipe → all to lower pipe  (deducted from upper)
        ///       UpperPipe → all to upper pipe  (deducted from lower)
        ///
        ///   Applied to: ExcavVol  and  VBackfill  (Kazı + Geri Dolgu)
        ///   NEVER applied to: VBedding  or  VSurround  (Yataklama + Gömlekleme)
        ///
        /// Results are written back into each SectionInfo in-place so that
        /// AggregateIntoReport picks up the corrected values automatically.
        /// </summary>
        private static void ComputeTrenchClashes(
            List<SectionInfo>            sections,
            Dictionary<string, NodeInfo> nodes,
            OverlapAssignment            excavAssignment,
            OverlapAssignment            backfillAssignment,
            Editor                       ed)
        {
            // ── Step 1: Build and normalise all footprints ────────────────────
            var footprints = new List<Vec2>[sections.Count];
            for (int i = 0; i < sections.Count; i++)
            {
                var s = sections[i];
                if (!nodes.ContainsKey(s.SnGuid) || !nodes.ContainsKey(s.EnGuid))
                    continue;
                var fp = BuildTrenchFootprint(s, nodes[s.SnGuid], nodes[s.EnGuid]);
                if (fp.Count >= 3)
                    footprints[i] = EnsureCCW(fp);
            }

            // ── Step 2: Check every unique pair (i, j) ────────────────────────
            int clashCount = 0;
            for (int i = 0; i < sections.Count - 1; i++)
            {
                if (footprints[i] == null) continue;
                var secA = sections[i];

                for (int j = i + 1; j < sections.Count; j++)
                {
                    if (footprints[j] == null) continue;
                    var secB = sections[j];

                    // Quick reject
                    if (!AabbOverlap(footprints[i], footprints[j])) continue;

                    // Full convex polygon intersection
                    var inter = PolygonIntersection(footprints[i], footprints[j]);
                    if (inter.Count < 3) continue;

                    double area2D = PolygonArea(inter);
                    if (area2D < 1e-6) continue;  // sub-millimetre — skip

                    // Average excavation depth of the overlapping zone
                    double avgDepthA = (secA.TrueDepthStart + secA.TrueDepthEnd) * 0.5;
                    double avgDepthB = (secB.TrueDepthStart + secB.TrueDepthEnd) * 0.5;
                    double avgDepth  = (avgDepthA + avgDepthB) * 0.5;

                    // ── Total shared volumes ──────────────────────────────────
                    //  Excavation overlap = full intersection prism.
                    //  Backfill overlap   = excavation overlap × backfill share,
                    //  so bedding + surround are never double-deducted.
                    double excavOverlap    = area2D * avgDepth;
                    double rBf             = AvgBackfillFraction(secA, secB);
                    double backfillOverlap = excavOverlap * rBf;

                    // ── Identify lower (deeper) vs upper (shallower) pipe ──────
                    double avgInvA = (secA.InvertStart + secA.InvertEnd) * 0.5;
                    double avgInvB = (secB.InvertStart + secB.InvertEnd) * 0.5;
                    bool aIsLower  = avgInvA <= avgInvB;          // deeper invert → lower line
                    SectionInfo lower = aIsLower ? secA : secB;
                    SectionInfo upper = aIsLower ? secB : secA;

                    // ── Apply the chosen assignment to each pay item ──────────
                    SplitOverlap(excavOverlap,    excavAssignment,
                                 out double exLow, out double exUp);
                    SplitOverlap(backfillOverlap, backfillAssignment,
                                 out double bfLow, out double bfUp);

                    lower.ExcavVol  = Math.Max(0, lower.ExcavVol  - exLow);
                    upper.ExcavVol  = Math.Max(0, upper.ExcavVol  - exUp);
                    lower.VBackfill = Math.Max(0, lower.VBackfill - bfLow);
                    upper.VBackfill = Math.Max(0, upper.VBackfill - bfUp);

                    lower.OverlapExcavDeducted    += exLow;
                    lower.OverlapBackfillDeducted += bfLow;
                    upper.OverlapExcavDeducted    += exUp;
                    upper.OverlapBackfillDeducted += bfUp;

                    string nameLow = $"{lower.StartNodeName} → {lower.EndNodeName}";
                    string nameUp  = $"{upper.StartNodeName} → {upper.EndNodeName}";

                    lower.ClashLog.Add(
                        $"OVERLAP with [{nameUp}] (upper): " +
                        $"Excav −{exLow:F4} m³ ({excavAssignment}), " +
                        $"Backfill −{bfLow:F4} m³ ({backfillAssignment})  " +
                        $"[Area={area2D:F4} m²  AvgDepth={avgDepth:F3} m  R_bf={rBf:F3}]");
                    upper.ClashLog.Add(
                        $"OVERLAP with [{nameLow}] (lower): " +
                        $"Excav −{exUp:F4} m³ ({excavAssignment}), " +
                        $"Backfill −{bfUp:F4} m³ ({backfillAssignment})  " +
                        $"[Area={area2D:F4} m²  AvgDepth={avgDepth:F3} m  R_bf={rBf:F3}]");

                    Dbg(ed,
                        $"\n  [BoQ-CLASH] lower[{nameLow}]  ↔  upper[{nameUp}]" +
                        $"\n               Intersection Area = {area2D:F4} m²" +
                        $"   Avg Depth = {avgDepth:F3} m   R_bf = {rBf:F3}" +
                        $"\n               Excav overlap = {excavOverlap:F4} m³ → lower −{exLow:F4} / upper −{exUp:F4}  ({excavAssignment})" +
                        $"\n               Backfill overlap = {backfillOverlap:F4} m³ → lower −{bfLow:F4} / upper −{bfUp:F4}  ({backfillAssignment})");

                    clashCount++;
                }
            }

            Dbg(ed,
                $"\n  [BoQ] Clash detection complete — {clashCount} overlapping pair(s) found.");
        }

        // ── Overlap helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Backfill share of the trench cross-section, averaged over the two
        /// clashing pipes:  R_bf = avg( ABackfill / AExcav ).
        ///
        /// Uses the ORIGINAL cross-section areas (never modified by the clash
        /// pass), so the value is stable regardless of deduction order. The
        /// remaining (1 − R_bf) corresponds to bedding + surround, which are
        /// intentionally excluded from any overlap deduction.
        /// </summary>
        private static double AvgBackfillFraction(SectionInfo a, SectionInfo b)
        {
            double Frac(SectionInfo s)
            {
                double exc = s.AExcavStart + s.AExcavEnd;
                if (exc <= 1e-9) return 0.0;
                double bf = s.ABackfillStart + s.ABackfillEnd;
                return Math.Min(1.0, Math.Max(0.0, bf / exc));
            }
            return (Frac(a) + Frac(b)) * 0.5;
        }

        /// <summary>
        /// Splits a shared overlap volume between the lower (deeper) and upper
        /// (shallower) pipe according to the chosen <see cref="OverlapAssignment"/>.
        ///   Split     → 50/50
        ///   LowerPipe → lower keeps it all ⇒ deduct the whole amount from upper
        ///   UpperPipe → upper keeps it all ⇒ deduct the whole amount from lower
        /// </summary>
        private static void SplitOverlap(
            double volume, OverlapAssignment mode,
            out double deductLower, out double deductUpper)
        {
            switch (mode)
            {
                case OverlapAssignment.LowerPipe:
                    deductLower = 0.0;          deductUpper = volume;       break;
                case OverlapAssignment.UpperPipe:
                    deductLower = volume;       deductUpper = 0.0;          break;
                case OverlapAssignment.Ignore:
                    deductLower = 0.0;          deductUpper = 0.0;          break;
                default: // Split
                    deductLower = volume * 0.5; deductUpper = volume * 0.5; break;
            }
        }
    }
}
