using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Xml.Linq;

namespace UrbanoMetraj.BoQ.Sections
{
    // =========================================================================
    // Cross-section drawing style (UT_CROSS_SECTIONS) — everything the user can
    // control about the output: per-element layer / colour / linetype / weight /
    // hatch / transparency, what is written, the info table and the layout.
    // AutoCAD-free; (de)serialised to XML by hand (no reflection — the suite build
    // is obfuscated). No static enum arrays (ConfuserEx "constants" lesson).
    // =========================================================================

    /// <summary>Keys of every drawable element. Strings, so the XML stays readable and tolerant.</summary>
    public static class SectionElements
    {
        public const string Kazi            = "Kazi";             // own excavation outline
        public const string Yataklama       = "Yataklama";
        public const string Gomlekleme      = "Gomlekleme";
        public const string GeriDolgu       = "GeriDolgu";
        public const string Boru            = "Boru";             // own pipe body
        public const string AltKatmanCizgi  = "AltKatmanCizgi";   // sub-layer division lines
        public const string BrutHat         = "BrutHat";          // own gross outline before deduction
        public const string Kaybedilen      = "Kaybedilen";       // own gross part given to another pipe
        public const string DigerKazi       = "DigerKazi";        // clashing pipe's excavation
        public const string DigerDolgu      = "DigerDolgu";       // clashing pipe's fill layers
        public const string DigerBoru       = "DigerBoru";        // clashing pipe's body
        public const string AyrimCizgisi    = "AyrimCizgisi";     // SP split line
        public const string Eksen           = "Eksen";            // pipe axis (vertical)
        public const string ZeminKazi       = "ZeminKazi";        // Kazı level line
        public const string ZeminDolgu      = "ZeminDolgu";       // Dolgu level line
        public const string Baslik          = "Baslik";           // title texts
        public const string Etiket          = "Etiket";           // level / area / note labels
        public const string Olcu            = "Olcu";             // dimensions
        public const string Tablo           = "Tablo";            // info table lines + texts
        public const string Cerceve         = "Cerceve";          // frame around a section
        public const string Izgara          = "Izgara";           // reference grid
        public const string KiyasCizgisi    = "KiyasCizgisi";     // datum line (KK) the vertical scale is read from

        /// <summary>Every key, in UI order (fresh array each call).</summary>
        public static string[] All() => new[]
        {
            Kazi, Yataklama, Gomlekleme, GeriDolgu, Boru, AltKatmanCizgi, BrutHat, Kaybedilen,
            DigerKazi, DigerDolgu, DigerBoru, AyrimCizgisi, Eksen, ZeminKazi, ZeminDolgu,
            Baslik, Etiket, Olcu, Tablo, Cerceve, Izgara, KiyasCizgisi
        };

        public static string DisplayName(string key)
        {
            switch (key)
            {
                case Kazi:           return "Hafriyat (kazı sınırı)";
                case Yataklama:      return "Yataklama";
                case Gomlekleme:     return "Gömlekleme";
                case GeriDolgu:      return "Geri Dolgu";
                case Boru:           return "Boru gövdesi";
                case AltKatmanCizgi: return "Alt katman ayrım çizgileri";
                case BrutHat:        return "Brüt kazı hattı (kesinti öncesi)";
                case Kaybedilen:     return "Diğer boruya bırakılan kısım";
                case DigerKazi:      return "Diğer boru — kazı";
                case DigerDolgu:     return "Diğer boru — dolgu katmanları";
                case DigerBoru:      return "Diğer boru — gövde";
                case AyrimCizgisi:   return "Ayrım çizgisi (%50 senaryosu)";
                case Eksen:          return "Boru ekseni";
                case ZeminKazi:      return "Kazı kotu çizgisi";
                case ZeminDolgu:     return "Dolgu kotu çizgisi";
                case Baslik:         return "Başlık yazıları";
                case Etiket:         return "Etiketler (kot / alan / not)";
                case Olcu:           return "Ölçüler";
                case Tablo:          return "Bilgi tablosu";
                case Cerceve:        return "Kesit çerçevesi";
                case Izgara:         return "Referans ızgarası";
                case KiyasCizgisi:   return "Kıyas çizgisi (KK)";
                default:             return key;
            }
        }
    }

    public sealed class SectionElementStyle
    {
        public string Key          { get; set; }
        public bool   Visible      { get; set; } = true;
        /// <summary>Layer name template — tokens {AG} (network), {KATMAN} (element key).</summary>
        public string Layer        { get; set; } = "URB_KS_{KATMAN}";
        /// <summary>ACI 1..255; 256 = ByLayer (the layer gets <see cref="LayerColorAci"/>).</summary>
        public short  ColorAci     { get; set; } = 256;
        /// <summary>Colour given to the layer when it is created.</summary>
        public short  LayerColorAci { get; set; } = 7;
        /// <summary>True colour 0xRRGGBB overriding the ACI, or -1.</summary>
        public int    TrueColor    { get; set; } = -1;
        public string Linetype     { get; set; } = "ByLayer";
        /// <summary>Lineweight in 1/100 mm; -1 = ByLayer.</summary>
        public int    LineWeight   { get; set; } = -1;
        /// <summary>Hatch pattern ("" = no hatch, "SOLID", "ANSI31", "AR-SAND", …).</summary>
        public string Hatch        { get; set; } = "";
        public double HatchScale   { get; set; } = 1.0;
        public double HatchAngle   { get; set; }
        /// <summary>Hatch colour ACI (256 = same as the element).</summary>
        public short  HatchColorAci { get; set; } = 256;
        /// <summary>Hatch transparency 0..90 %.</summary>
        public int    HatchTransparency { get; set; } = 50;

        public SectionElementStyle Clone() => (SectionElementStyle)MemberwiseClone();
    }

    /// <summary>
    /// Own hatch of one catalog sub-layer (by its layer name) - used when
    /// <see cref="SectionStyle.HatchSubLayers"/> turns a fill group into bands. Drawn on the
    /// group's layer; a sub-layer with no entry keeps the group's hatch.
    /// </summary>
    public sealed class SubLayerStyle
    {
        public string Name              { get; set; }
        public string Hatch             { get; set; } = "";
        public double HatchScale        { get; set; } = 1.0;
        public double HatchAngle        { get; set; }
        /// <summary>256 = the group element's hatch colour.</summary>
        public short  HatchColorAci     { get; set; } = 256;
        public int    HatchTransparency { get; set; } = 50;

        public SubLayerStyle Clone() => (SubLayerStyle)MemberwiseClone();
    }

    public enum CrossingDisplayMode
    {
        /// <summary>Only this network's share (the given-away part is not drawn).</summary>
        OwnOnly = 0,
        /// <summary>This network's share + the clashing pipes' parts in their own style.</summary>
        FullExcavation = 1,
        /// <summary>As FullExcavation + this pipe's gross outline and given-away part highlighted.</summary>
        FullWithGhost = 2
    }

    public enum OtherNetworkColorMode
    {
        /// <summary>The Diğer* element styles as they are.</summary>
        Single = 0,
        /// <summary>Colour per network from <see cref="SectionStyle.NetworkColors"/>.</summary>
        PerNetwork = 1
    }

    public enum ChainageFormat { Station = 0, Metric = 1 }        // 0+005.00  /  5.00 m
    public enum AreaDisplay { Net = 0, Gross = 1, Both = 2 }
    public enum SectionGrouping { Block = 0, Group = 1, Free = 2 }
    public enum SectionOrdering { RowPerPipe = 0, Continuous = 1 }

    /// <summary>Info-table row keys (the table shows the enabled ones in list order).</summary>
    public static class SectionTableRows
    {
        public const string KaziKotu      = "KaziKotu";
        public const string DolguKotu     = "DolguKotu";
        public const string AkarKotu      = "AkarKotu";
        public const string KaziTabani    = "KaziTabani";
        public const string Derinlik      = "Derinlik";
        public const string TabanGenislik = "TabanGenislik";
        public const string UstGenislik   = "UstGenislik";
        public const string Sev           = "Sev";
        public const string KaziAlani     = "KaziAlani";
        public const string YataklamaAlani = "YataklamaAlani";
        public const string GomleklemeAlani = "GomleklemeAlani";
        public const string GeriDolguAlani = "GeriDolguAlani";
        public const string YerelKm       = "YerelKm";
        public const string GenelKm       = "GenelKm";
        /// <summary>Expands to one row per catalog sub-layer (its owned area).</summary>
        public const string AltKatmanAlanlari = "AltKatmanAlanlari";

        public static string[] All() => new[]
        {
            YerelKm, GenelKm, KaziKotu, DolguKotu, AkarKotu, KaziTabani, Derinlik, TabanGenislik, UstGenislik, Sev,
            KaziAlani, YataklamaAlani, GomleklemeAlani, GeriDolguAlani, AltKatmanAlanlari
        };

        public static string Label(string key)
        {
            switch (key)
            {
                case KaziKotu:        return "Kazı Kotu";
                case DolguKotu:       return "Dolgu Kotu";
                case AkarKotu:        return "Akar Kotu";
                case KaziTabani:      return "Kazı Tabanı";
                case Derinlik:        return "Kazı Derinliği";
                case TabanGenislik:   return "Taban Genişliği";
                case UstGenislik:     return "Üst Genişlik";
                case Sev:             return "Şev (1:x)";
                case KaziAlani:       return "Kazı Alanı";
                case YataklamaAlani:  return "Yataklama Alanı";
                case GomleklemeAlani: return "Gömlekleme Alanı";
                case GeriDolguAlani:  return "Geri Dolgu Alanı";
                case YerelKm:         return "Yerel Km";
                case GenelKm:         return "Genel Km";
                case AltKatmanAlanlari: return "Alt katman alanları (her katman bir satır)";
                default:              return key;
            }
        }
    }

    public sealed class SectionTableRow
    {
        public string Key     { get; set; }
        public bool   Visible { get; set; } = true;
    }

    public sealed class SectionStyle
    {
        public string Name { get; set; } = "Varsayılan";

        public Dictionary<string, SectionElementStyle> Elements { get; set; }
            = new Dictionary<string, SectionElementStyle>(StringComparer.OrdinalIgnoreCase);

        // ── Crossings ────────────────────────────────────────────────────────
        public CrossingDisplayMode   Crossing        { get; set; } = CrossingDisplayMode.FullExcavation;
        public OtherNetworkColorMode OtherColorMode  { get; set; } = OtherNetworkColorMode.Single;
        public Dictionary<string, short> NetworkColors { get; set; } = new Dictionary<string, short>(StringComparer.OrdinalIgnoreCase);
        public bool ShowClashNote   { get; set; } = true;
        public bool ShowOtherLabel  { get; set; } = true;

        // ── Texts ────────────────────────────────────────────────────────────
        /// <summary>Text style; the default "URB_KS" is created with Arial when missing (SHX fonts lack ı ş ğ Ø ² °).</summary>
        public string TextStyle      { get; set; } = "URB_KS";
        /// <summary>Entity linetype scale for dashed elements (acadiso.lin patterns in a metre drawing).</summary>
        public double LinetypeScale  { get; set; } = 0.005;
        /// <summary>Plotted text height (mm on paper) at the horizontal scale.</summary>
        public double TextHeightMm   { get; set; } = 2.0;
        public double TitleHeightMm  { get; set; } = 3.0;
        public bool ShowTitle           { get; set; } = true;
        public bool ShowPipeName        { get; set; } = true;
        public bool ShowNetwork         { get; set; } = true;
        public bool ShowDiameter        { get; set; } = true;
        public bool ShowLocalChainage   { get; set; } = true;
        public bool ShowGlobalChainage  { get; set; } = true;
        public ChainageFormat ChainageFormat { get; set; } = ChainageFormat.Station;
        public string LocalPrefix       { get; set; } = "Yerel: ";
        public string GlobalPrefix      { get; set; } = "Genel: ";
        public bool ShowKolName         { get; set; } = true;
        public bool ShowLevels          { get; set; } = true;
        public bool ShowDimensions      { get; set; } = true;
        public bool ShowAreas           { get; set; } = true;
        public AreaDisplay AreaDisplay  { get; set; } = AreaDisplay.Net;
        public int  AreaDecimals        { get; set; } = 3;
        public int  LevelDecimals       { get; set; } = 2;
        public bool ShowSubLayerNames   { get; set; } = true;
        public bool ShowStationNote     { get; set; } = true;   // snapped / oblique (bisector) cut note

        // ── Sub-layers (decision 4) ──────────────────────────────────────────
        /// <summary>Hatch every catalog sub-layer separately (bands) instead of one hatch per group.</summary>
        public bool HatchSubLayers { get; set; }
        public Dictionary<string, SubLayerStyle> SubLayers { get; set; } = new Dictionary<string, SubLayerStyle>(StringComparer.OrdinalIgnoreCase);

        // ── Info table ───────────────────────────────────────────────────────
        public bool ShowTable { get; set; } = true;
        public List<SectionTableRow> TableRows { get; set; } = new List<SectionTableRow>();
        public double TableLabelWidthMm { get; set; } = 28;
        public double TableValueWidthMm { get; set; } = 18;
        public double TableRowHeightMm  { get; set; } = 5;

        // ── Layout ───────────────────────────────────────────────────────────
        /// <summary>Horizontal drawing scale 1:N (model units are metres).</summary>
        public double HorizontalScale { get; set; } = 100;
        /// <summary>Vertical drawing scale 1:N — may differ (vertical exaggeration = H / V).</summary>
        public double VerticalScale   { get; set; } = 100;
        public int    Columns         { get; set; } = 6;
        public SectionOrdering Ordering { get; set; } = SectionOrdering.RowPerPipe;
        public double GapXmm          { get; set; } = 15;
        public double GapYmm          { get; set; } = 20;
        /// <summary>Level lines extend this far beyond the widest trench edge (m).</summary>
        public double GroundExtendM   { get; set; } = 1.0;
        public bool   ShowFrame       { get; set; } = true;
        public bool   ShowGrid        { get; set; }
        public double GridStepM       { get; set; } = 0.5;
        public bool   ShowRowTitle    { get; set; } = true;
        /// <summary>One datum (KK) per sheet row - the sections of a row share a common elevation axis.</summary>
        public bool   RowCommonDatum  { get; set; }
        public SectionGrouping Grouping { get; set; } = SectionGrouping.Group;

        public double VerticalExaggeration => VerticalScale > 1e-9 ? HorizontalScale / VerticalScale : 1.0;
        /// <summary>Model-space height of a text of <paramref name="mm"/> plotted mm.</summary>
        public double ModelText(double mm) => mm * HorizontalScale / 1000.0;

        public SectionElementStyle E(string key)
        {
            if (!Elements.TryGetValue(key, out var e))
            {
                e = Default().Elements.TryGetValue(key, out var d) ? d.Clone() : new SectionElementStyle { Key = key };
                Elements[key] = e;
            }
            return e;
        }

        public SectionStyle Clone()
        {
            var c = (SectionStyle)MemberwiseClone();
            c.Elements = Elements.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            c.NetworkColors = new Dictionary<string, short>(NetworkColors, StringComparer.OrdinalIgnoreCase);
            c.TableRows = TableRows.Select(r => new SectionTableRow { Key = r.Key, Visible = r.Visible }).ToList();
            c.SubLayers = SubLayers.ToDictionary(kv => kv.Key, kv => kv.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            return c;
        }

        // ── Default (reproduces the old URB_KS_* look, then adds the new elements) ──
        public static SectionStyle Default()
        {
            var s = new SectionStyle();
            void Add(string key, short aci, string lt = "Continuous", int lw = -1, string hatch = "", double hs = 1, short hAci = 256,
                     int hTr = 60, bool vis = true)
                => s.Elements[key] = new SectionElementStyle
                {
                    Key = key, Visible = vis, Layer = "URB_KS_" + key, ColorAci = 256, LayerColorAci = aci,
                    Linetype = lt, LineWeight = lw, Hatch = hatch, HatchScale = hs, HatchColorAci = hAci, HatchTransparency = hTr
                };
            Add(SectionElements.Kazi,           30, lw: 35);
            Add(SectionElements.Yataklama,       2, hatch: "AR-SAND", hs: 0.01);
            Add(SectionElements.Gomlekleme,      3, hatch: "AR-SAND", hs: 0.01);
            Add(SectionElements.GeriDolgu,       6, hatch: "ANSI31",  hs: 0.05);
            Add(SectionElements.Boru,            4, lw: 30);
            Add(SectionElements.AltKatmanCizgi,  8, lt: "DASHED");
            Add(SectionElements.BrutHat,         8, lt: "DASHED");
            Add(SectionElements.Kaybedilen,      1, hatch: "SOLID", hAci: 1, hTr: 75);
            Add(SectionElements.DigerKazi,       9, lt: "DASHED");
            Add(SectionElements.DigerDolgu,      9, hatch: "SOLID", hAci: 9, hTr: 80);
            Add(SectionElements.DigerBoru,       9);
            Add(SectionElements.AyrimCizgisi,    1, lt: "DASHDOT");
            Add(SectionElements.Eksen,           1, lt: "CENTER");
            Add(SectionElements.ZeminKazi,      94, lw: 30);
            Add(SectionElements.ZeminDolgu,    150, lt: "DASHED");
            Add(SectionElements.Baslik,          7);
            Add(SectionElements.Etiket,          7);
            Add(SectionElements.Olcu,            7);
            Add(SectionElements.Tablo,           7);
            Add(SectionElements.Cerceve,         8);
            Add(SectionElements.Izgara,          9, vis: false);
            Add(SectionElements.KiyasCizgisi,    8);
            s.TableRows = SectionTableRows.All().Select(k => new SectionTableRow
            {
                Key = k,
                Visible = k != SectionTableRows.YataklamaAlani && k != SectionTableRows.GomleklemeAlani
                       && k != SectionTableRows.AltKatmanAlanlari
            }).ToList();
            return s;
        }

        // ── XML ──────────────────────────────────────────────────────────────
        private static string F(double v) => v.ToString("R", CultureInfo.InvariantCulture);
        private static double D(XAttribute a, double def)
            => a != null && double.TryParse(a.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        private static int I(XAttribute a, int def)
            => a != null && int.TryParse(a.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
        private static bool B(XAttribute a, bool def) => a != null ? a.Value == "1" || a.Value.Equals("true", StringComparison.OrdinalIgnoreCase) : def;
        private static string S(XAttribute a, string def) => a?.Value ?? def;
        private static T En<T>(XAttribute a, T def) where T : struct
            => a != null && Enum.TryParse(a.Value, true, out T v) ? v : def;

        public XElement ToXml()
        {
            var x = new XElement("SectionStyle",
                new XAttribute("name", Name ?? ""),
                new XAttribute("crossing", Crossing), new XAttribute("otherColorMode", OtherColorMode),
                new XAttribute("clashNote", ShowClashNote ? 1 : 0), new XAttribute("otherLabel", ShowOtherLabel ? 1 : 0),
                new XAttribute("textStyle", TextStyle ?? "URB_KS"), new XAttribute("ltScale", F(LinetypeScale)),
                new XAttribute("textMm", F(TextHeightMm)), new XAttribute("titleMm", F(TitleHeightMm)),
                new XAttribute("title", ShowTitle ? 1 : 0), new XAttribute("pipeName", ShowPipeName ? 1 : 0),
                new XAttribute("network", ShowNetwork ? 1 : 0), new XAttribute("diameter", ShowDiameter ? 1 : 0),
                new XAttribute("localKm", ShowLocalChainage ? 1 : 0), new XAttribute("globalKm", ShowGlobalChainage ? 1 : 0),
                new XAttribute("kmFormat", ChainageFormat),
                new XAttribute("localPrefix", LocalPrefix ?? ""), new XAttribute("globalPrefix", GlobalPrefix ?? ""),
                new XAttribute("kolName", ShowKolName ? 1 : 0),
                new XAttribute("levels", ShowLevels ? 1 : 0), new XAttribute("dims", ShowDimensions ? 1 : 0),
                new XAttribute("areas", ShowAreas ? 1 : 0), new XAttribute("areaDisplay", AreaDisplay),
                new XAttribute("areaDec", AreaDecimals), new XAttribute("levelDec", LevelDecimals),
                new XAttribute("subNames", ShowSubLayerNames ? 1 : 0), new XAttribute("stationNote", ShowStationNote ? 1 : 0),
                new XAttribute("table", ShowTable ? 1 : 0),
                new XAttribute("tableLabelMm", F(TableLabelWidthMm)), new XAttribute("tableValueMm", F(TableValueWidthMm)),
                new XAttribute("tableRowMm", F(TableRowHeightMm)),
                new XAttribute("hScale", F(HorizontalScale)), new XAttribute("vScale", F(VerticalScale)),
                new XAttribute("columns", Columns), new XAttribute("ordering", Ordering),
                new XAttribute("gapXmm", F(GapXmm)), new XAttribute("gapYmm", F(GapYmm)),
                new XAttribute("groundExt", F(GroundExtendM)), new XAttribute("frame", ShowFrame ? 1 : 0),
                new XAttribute("grid", ShowGrid ? 1 : 0), new XAttribute("gridStep", F(GridStepM)),
                new XAttribute("rowTitle", ShowRowTitle ? 1 : 0), new XAttribute("grouping", Grouping),
                new XAttribute("rowDatum", RowCommonDatum ? 1 : 0), new XAttribute("subHatch", HatchSubLayers ? 1 : 0));

            foreach (var key in SectionElements.All())
            {
                var e = E(key);
                x.Add(new XElement("Element",
                    new XAttribute("key", key), new XAttribute("visible", e.Visible ? 1 : 0),
                    new XAttribute("layer", e.Layer ?? ""), new XAttribute("color", e.ColorAci),
                    new XAttribute("layerColor", e.LayerColorAci), new XAttribute("trueColor", e.TrueColor),
                    new XAttribute("linetype", e.Linetype ?? "ByLayer"), new XAttribute("lineweight", e.LineWeight),
                    new XAttribute("hatch", e.Hatch ?? ""), new XAttribute("hatchScale", F(e.HatchScale)),
                    new XAttribute("hatchAngle", F(e.HatchAngle)), new XAttribute("hatchColor", e.HatchColorAci),
                    new XAttribute("hatchTransparency", e.HatchTransparency)));
            }
            foreach (var r in TableRows)
                x.Add(new XElement("TableRow", new XAttribute("key", r.Key), new XAttribute("visible", r.Visible ? 1 : 0)));
            foreach (var kv in NetworkColors)
                x.Add(new XElement("NetworkColor", new XAttribute("network", kv.Key), new XAttribute("color", kv.Value)));
            foreach (var sl in SubLayers.Values.OrderBy(v => v.Name, StringComparer.OrdinalIgnoreCase))
                x.Add(new XElement("SubLayer",
                    new XAttribute("name", sl.Name ?? ""), new XAttribute("hatch", sl.Hatch ?? ""),
                    new XAttribute("hatchScale", F(sl.HatchScale)), new XAttribute("hatchAngle", F(sl.HatchAngle)),
                    new XAttribute("hatchColor", sl.HatchColorAci), new XAttribute("hatchTransparency", sl.HatchTransparency)));
            return x;
        }

        public static SectionStyle FromXml(XElement x)
        {
            var s = Default();
            if (x == null) return s;
            s.Name           = S(x.Attribute("name"), s.Name);
            s.Crossing       = En(x.Attribute("crossing"), s.Crossing);
            s.OtherColorMode = En(x.Attribute("otherColorMode"), s.OtherColorMode);
            s.ShowClashNote  = B(x.Attribute("clashNote"), s.ShowClashNote);
            s.ShowOtherLabel = B(x.Attribute("otherLabel"), s.ShowOtherLabel);
            s.TextStyle      = S(x.Attribute("textStyle"), s.TextStyle);
            s.LinetypeScale  = D(x.Attribute("ltScale"), s.LinetypeScale);
            s.TextHeightMm   = D(x.Attribute("textMm"), s.TextHeightMm);
            s.TitleHeightMm  = D(x.Attribute("titleMm"), s.TitleHeightMm);
            s.ShowTitle      = B(x.Attribute("title"), s.ShowTitle);
            s.ShowPipeName   = B(x.Attribute("pipeName"), s.ShowPipeName);
            s.ShowNetwork    = B(x.Attribute("network"), s.ShowNetwork);
            s.ShowDiameter   = B(x.Attribute("diameter"), s.ShowDiameter);
            s.ShowLocalChainage  = B(x.Attribute("localKm"), s.ShowLocalChainage);
            s.ShowGlobalChainage = B(x.Attribute("globalKm"), s.ShowGlobalChainage);
            s.ChainageFormat = En(x.Attribute("kmFormat"), s.ChainageFormat);
            s.LocalPrefix    = S(x.Attribute("localPrefix"), s.LocalPrefix);
            s.GlobalPrefix   = S(x.Attribute("globalPrefix"), s.GlobalPrefix);
            s.ShowKolName    = B(x.Attribute("kolName"), s.ShowKolName);
            s.ShowLevels     = B(x.Attribute("levels"), s.ShowLevels);
            s.ShowDimensions = B(x.Attribute("dims"), s.ShowDimensions);
            s.ShowAreas      = B(x.Attribute("areas"), s.ShowAreas);
            s.AreaDisplay    = En(x.Attribute("areaDisplay"), s.AreaDisplay);
            s.AreaDecimals   = I(x.Attribute("areaDec"), s.AreaDecimals);
            s.LevelDecimals  = I(x.Attribute("levelDec"), s.LevelDecimals);
            s.ShowSubLayerNames = B(x.Attribute("subNames"), s.ShowSubLayerNames);
            s.ShowStationNote   = B(x.Attribute("stationNote"), s.ShowStationNote);
            s.ShowTable      = B(x.Attribute("table"), s.ShowTable);
            s.TableLabelWidthMm = D(x.Attribute("tableLabelMm"), s.TableLabelWidthMm);
            s.TableValueWidthMm = D(x.Attribute("tableValueMm"), s.TableValueWidthMm);
            s.TableRowHeightMm  = D(x.Attribute("tableRowMm"), s.TableRowHeightMm);
            s.HorizontalScale = D(x.Attribute("hScale"), s.HorizontalScale);
            s.VerticalScale   = D(x.Attribute("vScale"), s.VerticalScale);
            s.Columns         = Math.Max(1, I(x.Attribute("columns"), s.Columns));
            s.Ordering        = En(x.Attribute("ordering"), s.Ordering);
            s.GapXmm          = D(x.Attribute("gapXmm"), s.GapXmm);
            s.GapYmm          = D(x.Attribute("gapYmm"), s.GapYmm);
            s.GroundExtendM   = D(x.Attribute("groundExt"), s.GroundExtendM);
            s.ShowFrame       = B(x.Attribute("frame"), s.ShowFrame);
            s.ShowGrid        = B(x.Attribute("grid"), s.ShowGrid);
            s.GridStepM       = D(x.Attribute("gridStep"), s.GridStepM);
            s.ShowRowTitle    = B(x.Attribute("rowTitle"), s.ShowRowTitle);
            s.Grouping        = En(x.Attribute("grouping"), s.Grouping);
            s.RowCommonDatum  = B(x.Attribute("rowDatum"), s.RowCommonDatum);
            s.HatchSubLayers  = B(x.Attribute("subHatch"), s.HatchSubLayers);

            foreach (var ex in x.Elements("Element"))
            {
                string key = S(ex.Attribute("key"), null);
                if (string.IsNullOrEmpty(key)) continue;
                var e = s.E(key);
                e.Visible       = B(ex.Attribute("visible"), e.Visible);
                e.Layer         = S(ex.Attribute("layer"), e.Layer);
                e.ColorAci      = (short)I(ex.Attribute("color"), e.ColorAci);
                e.LayerColorAci = (short)I(ex.Attribute("layerColor"), e.LayerColorAci);
                e.TrueColor     = I(ex.Attribute("trueColor"), e.TrueColor);
                e.Linetype      = S(ex.Attribute("linetype"), e.Linetype);
                e.LineWeight    = I(ex.Attribute("lineweight"), e.LineWeight);
                e.Hatch         = S(ex.Attribute("hatch"), e.Hatch);
                e.HatchScale    = D(ex.Attribute("hatchScale"), e.HatchScale);
                e.HatchAngle    = D(ex.Attribute("hatchAngle"), e.HatchAngle);
                e.HatchColorAci = (short)I(ex.Attribute("hatchColor"), e.HatchColorAci);
                e.HatchTransparency = I(ex.Attribute("hatchTransparency"), e.HatchTransparency);
            }
            var rows = x.Elements("TableRow").Select(r => new SectionTableRow
            {
                Key = S(r.Attribute("key"), ""), Visible = B(r.Attribute("visible"), true)
            }).Where(r => r.Key.Length > 0).ToList();
            if (rows.Count > 0)
            {
                // Keep the saved order; append rows added in newer versions.
                foreach (var k in SectionTableRows.All())
                    if (!rows.Any(r => r.Key == k)) rows.Add(new SectionTableRow { Key = k, Visible = false });
                s.TableRows = rows;
            }
            foreach (var nc in x.Elements("NetworkColor"))
            {
                string n = S(nc.Attribute("network"), "");
                if (n.Length > 0) s.NetworkColors[n] = (short)I(nc.Attribute("color"), 7);
            }
            foreach (var sx in x.Elements("SubLayer"))
            {
                string n = S(sx.Attribute("name"), "");
                if (n.Length == 0) continue;
                s.SubLayers[n] = new SubLayerStyle
                {
                    Name = n, Hatch = S(sx.Attribute("hatch"), ""),
                    HatchScale = D(sx.Attribute("hatchScale"), 1.0), HatchAngle = D(sx.Attribute("hatchAngle"), 0.0),
                    HatchColorAci = (short)I(sx.Attribute("hatchColor"), 256),
                    HatchTransparency = I(sx.Attribute("hatchTransparency"), 50)
                };
            }
            return s;
        }
    }

    /// <summary>Per-run options (what to draw), remembered per drawing.</summary>
    public sealed class SectionRunOptions
    {
        /// <summary>When true the interval and both scenarios come from Proje Ayarları.</summary>
        public bool UseProjectSettings { get; set; } = true;
        public double Interval         { get; set; } = 5.0;
        public UrbanoMetraj.BoQ.Models.TiePreference ExcavScenario    { get; set; } = UrbanoMetraj.BoQ.Models.TiePreference.Split;
        public UrbanoMetraj.BoQ.Models.TiePreference BackfillScenario { get; set; } = UrbanoMetraj.BoQ.Models.TiePreference.Split;
        public bool IncludeZoneBoundaries { get; set; } = true;
        public bool OnlyClashZones     { get; set; }
        public string ManualChainages  { get; set; } = "";
        public double MinGap           { get; set; } = 0.25;
        /// <summary>Networks to draw (empty = all computed).</summary>
        public List<string> Networks   { get; set; } = new List<string>();
        /// <summary>Pipe keys (SystemName|StartNode|EndNode) to draw (empty = all pipes of the chosen networks).</summary>
        public List<string> Pipes      { get; set; } = new List<string>();

        /// <summary>Scenario comparison: every station is drawn once per scenario in <see cref="CompareSet"/>
        /// (each applied to Kazı and Dolgu alike), side by side in one row.</summary>
        public bool CompareScenarios   { get; set; }
        public List<UrbanoMetraj.BoQ.Models.TiePreference> CompareSet { get; set; } = new List<UrbanoMetraj.BoQ.Models.TiePreference>
        {
            UrbanoMetraj.BoQ.Models.TiePreference.KeepUpper, UrbanoMetraj.BoQ.Models.TiePreference.KeepLower, UrbanoMetraj.BoQ.Models.TiePreference.Split
        };

        public static string PipeKey(string system, string sn, string en) => (system ?? "") + "|" + (sn ?? "") + "|" + (en ?? "");

        public List<double> ParseManual()
        {
            var res = new List<double>();
            foreach (var tok in (ManualChainages ?? "").Split(new[] { ';', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
                if (double.TryParse(tok.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) res.Add(v);
            return res;
        }

        public XElement ToXml()
        {
            var x = new XElement("SectionRun",
                new XAttribute("useProject", UseProjectSettings ? 1 : 0),
                new XAttribute("interval", Interval.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("excav", ExcavScenario), new XAttribute("backfill", BackfillScenario),
                new XAttribute("zoneEdges", IncludeZoneBoundaries ? 1 : 0), new XAttribute("onlyClash", OnlyClashZones ? 1 : 0),
                new XAttribute("manual", ManualChainages ?? ""),
                new XAttribute("minGap", MinGap.ToString("R", CultureInfo.InvariantCulture)),
                new XAttribute("compare", CompareScenarios ? 1 : 0),
                new XAttribute("compareSet", string.Join(",", CompareSet)));
            foreach (var n in Networks) x.Add(new XElement("Network", n));
            foreach (var p in Pipes)    x.Add(new XElement("Pipe", p));
            return x;
        }

        public static SectionRunOptions FromXml(XElement x)
        {
            var o = new SectionRunOptions();
            if (x == null) return o;
            o.UseProjectSettings = x.Attribute("useProject")?.Value != "0";
            if (double.TryParse(x.Attribute("interval")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var iv)) o.Interval = iv;
            if (Enum.TryParse(x.Attribute("excav")?.Value, out UrbanoMetraj.BoQ.Models.TiePreference ex)) o.ExcavScenario = ex;
            if (Enum.TryParse(x.Attribute("backfill")?.Value, out UrbanoMetraj.BoQ.Models.TiePreference bf)) o.BackfillScenario = bf;
            o.IncludeZoneBoundaries = x.Attribute("zoneEdges")?.Value != "0";
            o.OnlyClashZones = x.Attribute("onlyClash")?.Value == "1";
            o.ManualChainages = x.Attribute("manual")?.Value ?? "";
            if (double.TryParse(x.Attribute("minGap")?.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var mg)) o.MinGap = mg;
            o.CompareScenarios = x.Attribute("compare")?.Value == "1";
            if (x.Attribute("compareSet") != null)
            {
                o.CompareSet = new List<UrbanoMetraj.BoQ.Models.TiePreference>();
                foreach (var tok in x.Attribute("compareSet").Value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    if (Enum.TryParse(tok.Trim(), out UrbanoMetraj.BoQ.Models.TiePreference tp) && !o.CompareSet.Contains(tp)) o.CompareSet.Add(tp);
            }
            o.Networks = x.Elements("Network").Select(e => e.Value).ToList();
            o.Pipes    = x.Elements("Pipe").Select(e => e.Value).ToList();
            return o;
        }
    }
}
