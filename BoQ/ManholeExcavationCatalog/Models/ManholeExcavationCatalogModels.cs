using System;
using System.Collections.ObjectModel;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models
{
    /// <summary>
    /// One preparatory layer placed below the manhole base slab (ordered top-to-bottom).
    /// Thickness stored in mm for precision; the excavation engine converts to metres.
    /// </summary>
    public class SubBaseLayer
    {
        public string LayerName   { get; set; } = "";
        public double ThicknessMm { get; set; }
    }

    /// <summary>
    /// One backfill layer placed around the manhole shaft after installation (ordered top-to-bottom).
    /// When IsFillToSurface is true, ThicknessM is ignored and the engine calculates the volume
    /// needed to fill the remaining annular gap up to the ground surface.
    /// </summary>
    public class ManholeBackfillLayer
    {
        public string LayerName      { get; set; } = "";
        public string MaterialType   { get; set; } = "";

        /// <summary>Fixed thickness of this backfill layer (m). Ignored when IsFillToSurface is true.</summary>
        public double ThicknessM     { get; set; }

        /// <summary>
        /// When true, this layer fills the remaining annular void to the surface.
        /// Only one layer per tier should have this flag set; the engine uses the first match.
        /// </summary>
        public bool   IsFillToSurface { get; set; }
    }

    /// <summary>
    /// Excavation parameters for a specific burial-depth band.
    /// Stepped excavation fields (IsSteppedExcavation, MaxStepHeightM, StepBermWidthM)
    /// are only active when IsSteppedExcavation is true; the engine ignores them otherwise.
    /// </summary>
    public class ManholeExcavationDepthTier
    {
        /// <summary>Lower bound of burial depth (m, inclusive).</summary>
        public double MinDepthM { get; set; }

        /// <summary>Upper bound of burial depth (m, inclusive). 0 = unlimited.</summary>
        public double MaxDepthM { get; set; }

        /// <summary>Extra width added to each side of the base footprint for workers/machinery (m).</summary>
        public double WorkingClearanceM { get; set; } = 0.5;

        /// <summary>
        /// H:V batter ratio. 0 = vertical trench wall, 1 = 1:1 slope (1 m horizontal per 1 m deep).
        /// </summary>
        public double SlopeRatio { get; set; }

        /// <summary>True when a trench box (iksa) is required for this depth band.</summary>
        public bool RequiresShoring { get; set; }

        // ── Stepped / benched excavation ─────────────────────────────────────

        /// <summary>Enable benching for this depth tier.</summary>
        public bool IsSteppedExcavation { get; set; }

        /// <summary>
        /// Maximum vertical height before a horizontal berm must be cut (m).
        /// Only used when IsSteppedExcavation is true.
        /// </summary>
        public double MaxStepHeightM { get; set; } = 1.5;

        /// <summary>
        /// Horizontal width of each step/berm (m).
        /// Only used when IsSteppedExcavation is true.
        /// </summary>
        public double StepBermWidthM { get; set; } = 0.5;

        // ── Sub-base preparation layers (below the base slab) ────────────────

        public ObservableCollection<SubBaseLayer> SubBaseLayers { get; set; }
            = new ObservableCollection<SubBaseLayer>();

        /// <summary>Sum of all sub-base layer thicknesses converted to metres.</summary>
        public double TotalSubBaseDepthM
        {
            get
            {
                double total = 0;
                if (SubBaseLayers != null)
                    foreach (var l in SubBaseLayers) total += l.ThicknessMm / 1000.0;
                return total;
            }
        }

        // ── Backfill layers (around the shaft, placed after installation) ─────

        public ObservableCollection<ManholeBackfillLayer> BackfillLayers { get; set; }
            = new ObservableCollection<ManholeBackfillLayer>();
    }

    /// <summary>
    /// Root excavation rule covering a manhole base diameter range.
    /// The driver is the base diameter band and burial depth — never a fixed base ID or pipe diameter.
    /// </summary>
    public class ManholeExcavationRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Lower bound of the applicable manhole base diameter (mm, inclusive).</summary>
        public double MinBaseDiameterMm { get; set; }

        /// <summary>Upper bound of the applicable manhole base diameter (mm, inclusive). 0 = unlimited.</summary>
        public double MaxBaseDiameterMm { get; set; }

        public ObservableCollection<ManholeExcavationDepthTier> DepthTiers { get; set; }
            = new ObservableCollection<ManholeExcavationDepthTier>();
    }
}
