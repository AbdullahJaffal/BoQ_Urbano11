using System;
using System.Collections.ObjectModel;

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog.Models
{
    /// <summary>
    /// A single material layer used either as bedding (below/around the pipe)
    /// or as backfill (above the pipe to the surface).
    /// When IsFillToSurface is true, ThicknessM is ignored and the engine calculates
    /// the volume needed to fill the remaining trench void up to ground level.
    /// </summary>
    public class TrenchLayer
    {
        public string LayerName    { get; set; } = "";
        public string MaterialType { get; set; } = "";

        /// <summary>Fixed layer thickness (m). Ignored when IsFillToSurface is true.</summary>
        public double ThicknessM   { get; set; }

        /// <summary>
        /// When true, this layer fills the remaining trench void to the surface.
        /// Only one layer per collection should have this flag set; the engine uses the first match.
        /// </summary>
        public bool IsFillToSurface { get; set; }
    }

    /// <summary>
    /// Excavation geometry and layer stack for a specific burial-depth band.
    /// Stepped excavation fields are only active when IsSteppedExcavation is true.
    /// </summary>
    public class PipeTrenchDepthTier
    {
        /// <summary>Lower bound of trench depth (m, inclusive).</summary>
        public double MinDepthM { get; set; }

        /// <summary>Upper bound of trench depth (m, inclusive). 0 = unlimited.</summary>
        public double MaxDepthM { get; set; }

        /// <summary>
        /// Extra clearance added to each side of the pipe's outer diameter to obtain
        /// the bottom trench width: BottomWidth = OD + 2 * TrenchWidthClearanceM.
        /// </summary>
        public double TrenchWidthClearanceM { get; set; } = 0.2;

        /// <summary>
        /// H:V batter ratio applied to the trench walls.
        /// 0 = vertical walls; 1 = 1:1 slope (1 m horizontal per 1 m deep).
        /// </summary>
        public double SlopeRatio { get; set; }

        /// <summary>True when a trench box (İksa) is required for this depth band.</summary>
        public bool RequiresShoring { get; set; }

        // ── Stepped / benched excavation ─────────────────────────────────────

        /// <summary>Enable benching (Kademeli Kazı) for this depth tier.</summary>
        public bool IsSteppedExcavation { get; set; }

        /// <summary>
        /// Maximum vertical cut height before a horizontal berm must be cut (m).
        /// Only used when IsSteppedExcavation is true.
        /// </summary>
        public double MaxStepHeightM { get; set; } = 1.5;

        /// <summary>
        /// Horizontal width of each step/berm (m).
        /// Only used when IsSteppedExcavation is true.
        /// </summary>
        public double StepBermWidthM { get; set; } = 0.5;

        // ── Material layers ───────────────────────────────────────────────────

        /// <summary>
        /// Layers placed below and immediately around the pipe (Yataklama / Gömlekleme).
        /// Ordered from the trench bottom upward.
        /// </summary>
        public ObservableCollection<TrenchLayer> BeddingLayers { get; set; }
            = new ObservableCollection<TrenchLayer>();

        /// <summary>
        /// Layers placed above the pipe up to the ground surface (Geri Dolgu).
        /// Ordered from the pipe crown upward.
        /// </summary>
        public ObservableCollection<TrenchLayer> BackfillLayers { get; set; }
            = new ObservableCollection<TrenchLayer>();
    }

    /// <summary>
    /// Root trench rule covering a pipe outer-diameter range.
    /// The matching driver is the pipe diameter band and the computed trench depth —
    /// never a fixed pipe ID or material.
    /// </summary>
    public class PipeTrenchRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Human-readable rule name (e.g. "Standard KRG Trench").</summary>
        public string RuleName { get; set; } = "";

        /// <summary>Lower bound of the applicable pipe diameter (mm, inclusive).</summary>
        public double MinPipeDiameterMm { get; set; }

        /// <summary>Upper bound of the applicable pipe diameter (mm, inclusive). 0 = unlimited.</summary>
        public double MaxPipeDiameterMm { get; set; }

        public ObservableCollection<PipeTrenchDepthTier> DepthTiers { get; set; }
            = new ObservableCollection<PipeTrenchDepthTier>();
    }
}
