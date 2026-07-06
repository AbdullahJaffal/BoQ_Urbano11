namespace UrbanoMetraj.BoQ.Models
{
    // ── Enumeration types ────────────────────────────────────────────────────────

    public enum ManholeType
    {
        PreCast,
        CastInPlace
    }

    public enum ExportLanguage
    {
        English = 0,
        Turkish = 1,
        Russian = 2
    }

    /// <summary>
    /// How the shared (double-counted) trench-overlap volume is assigned between
    /// the two clashing pipes. "Lower" = the deeper pipe (الخط الأدنى),
    /// "Upper" = the shallower pipe (الخط الأعلى).
    ///
    /// Applied independently to Excavation and to Backfill, giving 3 × 3 = 9
    /// possible combinations.
    /// </summary>
    /// <summary>
    /// How ManholeAIService.ComputeFamilyStack fills a remaining gap with
    /// Gövde Halkası / Boyun bileziği pieces (user directive 2026-07-06).
    /// </summary>
    public enum RingFillMode
    {
        /// <summary>Largest-first greedy fill (original behaviour) — tries each
        /// available size in descending order, using as many of that size as fit,
        /// then moves to the next-smaller size. Simple, but can leave a larger
        /// gap than necessary when a different combination of sizes would fit
        /// better.</summary>
        Greedy = 0,
        /// <summary>Bounded-knapsack search for the combination of available
        /// sizes (respecting the role's Max piece count) that reaches the
        /// closest achievable depth without exceeding the target — e.g. an 8cm +
        /// 7cm combination closing a 15cm gap exactly instead of a single 10cm
        /// piece leaving 5cm unclosed.</summary>
        BestFit = 1
    }

    /// <summary>
    /// How PipeNetLengthService reduces a pipe's raw Length2D at each connected
    /// manhole end (user directive 2026-07-06).
    /// </summary>
    public enum NetLengthMode
    {
        /// <summary>Deduct the manhole's outer shell: inner half-width + the wall
        /// thickness of the precast ring at the pipe's invert elevation.</summary>
        OuterDiameter = 0,
        /// <summary>Deduct only the manhole's inner half-width — no wall thickness.</summary>
        InnerDiameter = 1
    }

    public enum OverlapAssignment
    {
        /// <summary>50/50 — each pipe keeps half of the overlap (مناصفة).</summary>
        Split = 0,
        /// <summary>Full quantity to the lower (deeper) pipe; deducted from the upper.</summary>
        LowerPipe = 1,
        /// <summary>Full quantity to the upper (shallower) pipe; deducted from the lower.</summary>
        UpperPipe = 2,
        /// <summary>Ignore overlap entirely — each pipe keeps its full gross section, no deduction.</summary>
        Ignore = 3
    }

    /// <summary>
    /// Options chosen in the 3-D solids startup dialog (URBANO_SOLIDS).
    /// The two overlap assignments drive how overlapping excavation / backfill
    /// solids are resolved geometrically (subtract from one pipe, or split).
    /// </summary>
    public class SolidsSettings
    {
        public OverlapAssignment ExcavationOverlap { get; set; } = OverlapAssignment.Split;
        public OverlapAssignment BackfillOverlap   { get; set; } = OverlapAssignment.Split;

        public bool DrawExcavation { get; set; } = true;   // Hafriyat
        public bool DrawBackfill   { get; set; } = true;   // Geri Dolgu
        public bool DrawBedding    { get; set; } = true;   // Yataklama
        public bool DrawSurround   { get; set; } = true;   // Gömlekleme
    }

    // ── Settings model ───────────────────────────────────────────────────────────

    /// <summary>
    /// All options selected in the BoQ startup dialog.
    /// Passed as a unit through the pipeline from dialog close to Excel export.
    /// </summary>
    public class BoQSettings
    {
        /// <summary>
        /// When true, ComputeTrenchClashes runs and the Overlap column is written.
        /// </summary>
        public bool EnableClashDetection { get; set; } = true;

        /// <summary>
        /// How the overlapping EXCAVATION volume is split between the two clashing
        /// pipes. Default Split (50/50) preserves the historical behaviour.
        /// </summary>
        public OverlapAssignment ExcavationOverlap { get; set; } = OverlapAssignment.Split;

        /// <summary>
        /// How the overlapping BACKFILL volume is split between the two clashing
        /// pipes. Independent of <see cref="ExcavationOverlap"/>.
        /// </summary>
        public OverlapAssignment BackfillOverlap { get; set; } = OverlapAssignment.Split;

        /// <summary>
        /// Controls which manhole cost catalog is applied (reserved for Phase 2).
        /// </summary>
        public ManholeType ManholeType { get; set; } = ManholeType.PreCast;

        /// <summary>
        /// How ManholeAIService fills a Gövde/Boyun gap when stacking a precast
        /// manhole — greedy (original) or best-fit combination search.
        /// </summary>
        public RingFillMode RingFillMode { get; set; } = RingFillMode.Greedy;

        /// <summary>
        /// Language used for all Excel column headers and labels.
        /// </summary>
        public ExportLanguage Language { get; set; } = ExportLanguage.English;

        /// <summary>
        /// How PipeNetLengthService reduces each pipe's raw length at a connected
        /// manhole — outer shell (radius + wall thickness, default/original
        /// behaviour) or inner radius only.
        /// </summary>
        public NetLengthMode NetLengthMode { get; set; } = NetLengthMode.OuterDiameter;

        /// <summary>
        /// Absolute path of the .xlsx file to write.
        /// </summary>
        public string ExportFilePath { get; set; } = "";

        /// <summary>
        /// Absolute path of the pre-cast manhole catalog .xlsx file.
        /// </summary>
        public string ManholeConfigPath { get; set; } = "";

        /// <summary>
        /// Station grouping interval used only for 3-D solid display (metres, min 0.5 m).
        /// Has no effect on volume calculations, which always use 0.5 m stations.
        /// </summary>
        public double SolidDisplayInterval { get; set; } = 5.0;

        /// <summary>
        /// Station sampling interval used for 2-D cross-section drawings (metres, min 0.5 m).
        /// Controls how many cross-sections are printed side by side (URBANO_SECTIONS command).
        /// Crossing boundary stations are always forced regardless of this value.
        /// </summary>
        public double CrossSectionInterval { get; set; } = 5.0;

        /// <summary>
        /// When true, the overlap between each pipe trench and its connected manhole
        /// excavations is deducted from the pipe's excavation volume, and the manhole
        /// excavation total is shown. When false (Yoksay), no deduction is applied
        /// and the manhole excavation total is displayed as 0.
        /// </summary>
        public bool BacaKaziHesapla { get; set; } = false;

        /// <summary>Selected surface (Arazi1…Arazi10) used for the manhole "Kırmızı Kot".</summary>
        public string BacaKirmiziKotSurface { get; set; } = "Arazi1";

        /// <summary>Selected surface (Arazi1…Arazi10) used for the manhole "Arazi Kotu".</summary>
        public string BacaAraziKotuSurface { get; set; } = "Arazi1";

        /// <summary>Selected surface (Arazi1…Arazi10) used for the manhole "Terrasman Kotu".</summary>
        public string BacaTerrasmanKotuSurface { get; set; } = "Arazi1";

        /// <summary>Real Civil 3D surface name (from the active drawing) linked to "Kırmızı Kot".</summary>
        public string BacaKirmiziKotC3DSurface { get; set; } = "";

        /// <summary>Real Civil 3D surface name (from the active drawing) linked to "Arazi Kotu".</summary>
        public string BacaAraziKotuC3DSurface { get; set; } = "";

        /// <summary>Real Civil 3D surface name (from the active drawing) linked to "Terrasman Kotu".</summary>
        public string BacaTerrasmanKotuC3DSurface { get; set; } = "";

        /// <summary>Which kot ("Kırmızı Kot" / "Arazi Kotu" / "Terrasman Kotu") the excavation (Kazı) level uses.</summary>
        public string KaziSeviyesi { get; set; } = "Kırmızı Kot";

        /// <summary>Which kot the backfill (Dolgu) level uses.</summary>
        public string DolguSeviyesi { get; set; } = "Kırmızı Kot";

        /// <summary>Which kot the manhole cover (Baca Kapak) level uses.</summary>
        public string BacaKapakSeviyesi { get; set; } = "Kırmızı Kot";
    }
}
