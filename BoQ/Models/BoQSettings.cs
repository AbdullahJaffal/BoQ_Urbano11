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
        /// Language used for all Excel column headers and labels.
        /// </summary>
        public ExportLanguage Language { get; set; } = ExportLanguage.English;

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
    }
}
