using System;
using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.Models
{
    // =========================================================================
    // Manhole BOQ Catalog – v2 (Group → SubTemplate hierarchy)
    // =========================================================================

    /// <summary>
    /// One sub-base layer below the manhole base slab (top-to-bottom order).
    /// </summary>
    public class BeddingSubLayer
    {
        public string Name       { get; set; }
        public double ThicknessM { get; set; }  // m
    }

    /// <summary>
    /// Geometric parameters of the excavation pit around a manhole.
    /// </summary>
    public class ExcavationShape
    {
        /// <summary>4 (square) … 32 (near-circle). Clamped at runtime.</summary>
        public int PolygonSides { get; set; } = 4;

        /// <summary>
        /// True → vertical trench box (no batter).
        /// False → sloped walls described by <see cref="SlopeX"/>.
        /// </summary>
        public bool IsVertical { get; set; } = true;

        /// <summary>
        /// x in the 1:x format (V per 1H).
        /// x = 2 → 1:2 batter (0.5 m horizontal per 1 m vertical).
        /// Only used when <see cref="IsVertical"/> is false. Must be > 0.
        /// </summary>
        public double SlopeX { get; set; } = 2.0;

        /// <summary>Computed H:V ratio used by the excavation engine.</summary>
        public double HtoV => IsVertical ? 0.0 : (SlopeX > 0 ? 1.0 / SlopeX : 0.0);
    }

    // ── Sub-template (one manhole definition) ────────────────────────────────

    /// <summary>
    /// One manhole definition inside a <see cref="ManholeTemplateGroup"/>.
    ///
    /// Mode = "Explicit": DN and clearance are fixed, no pipe-diameter condition.
    /// Mode = "Dynamic" : applies only when the max connected pipe diameter falls
    ///                    in [<see cref="MinPipeDiamMm"/>, <see cref="MaxPipeDiamMm"/>].
    /// </summary>
    public class ManholeSubTemplate
    {
        /// <summary>Auto-generated GUID-based Id (hidden from the user).</summary>
        public string Id              { get; set; }

        public string Name            { get; set; }

        // Pipe diameter condition – used only when the parent group Mode is "Dynamic".
        public int MinPipeDiamMm      { get; set; }
        public int MaxPipeDiamMm      { get; set; }

        // Manhole dimensions
        public int    ManholeDn       { get; set; } = 1000;   // shaft nominal diameter, mm
        public int    WallThicknessMm { get; set; } = 120;    // shaft wall thickness, mm
        public double ClearanceM      { get; set; } = 0.50;   // working clearance per side, m

        public ExcavationShape       ExcavShape    { get; set; } = new ExcavationShape();
        public List<BeddingSubLayer> BeddingLayers { get; set; } = new List<BeddingSubLayer>();

        /// <summary>Sum of all bedding layer thicknesses (m).</summary>
        public double TotalBeddingDepthM
        {
            get
            {
                double s = 0;
                if (BeddingLayers != null) foreach (var l in BeddingLayers) s += l.ThicknessM;
                return s;
            }
        }
    }

    // ── Group (first-level container) ─────────────────────────────────────────

    /// <summary>
    /// A named group containing one or more <see cref="ManholeSubTemplate"/> entries.
    /// Displayed as a parent node in the catalog tree.
    /// </summary>
    public class ManholeTemplateGroup
    {
        /// <summary>Auto-generated Id (hidden from the user).</summary>
        public string Id        { get; set; }

        public string Name      { get; set; }

        /// <summary>
        /// "Explicit" – fixed DN, no pipe-diameter condition.
        /// "Dynamic"  – each sub-template covers a pipe-diameter range.
        /// All sub-templates in this group share the same mode.
        /// </summary>
        public string Mode      { get; set; } = "Explicit";

        public List<ManholeSubTemplate> Templates { get; set; }
            = new List<ManholeSubTemplate>();
    }

    // ── Catalog root ──────────────────────────────────────────────────────────

    public class ManholeCatalog
    {
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        public List<ManholeTemplateGroup> Groups { get; set; }
            = new List<ManholeTemplateGroup>();
    }
}
