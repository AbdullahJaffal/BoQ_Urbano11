using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.Models
{
    // =========================================================================
    // Pseudo-3D BoQ overlap engine — geometry data structures
    //
    // ARCHITECTURE: "Pre-calculate and Cache".
    //   At URBANO_BOQ time the engine resolves every cross-section for ALL THREE
    //   user-preference scenarios (Keep Upper / Keep Lower / 50-50 Split) and
    //   caches the final polygon coordinates + net areas for every layer. Later
    //   commands (URBANO_SOLIDS, cross-section plotting) and the results dialog
    //   drop-down simply QUERY the cached scenario — no Clipper boolean ops are
    //   ever re-run downstream.
    //
    // Conventions
    //   • All polygons live in the local (U, Z) perpendicular frame of a pipe:
    //       U = signed horizontal offset from the centreline (U = 0 = axis),
    //       Z = absolute elevation (m).
    //   • ring   = List<double[2]{U,Z}>          (one closed polygon)
    //   • region = List<ring>                     (0..n disjoint rings / holes)
    // Boolean operations (and the "chunking" rule) can split one input into
    // several pieces or punch a hole (the pipe void), so every cached NET shape
    // is a region, never a single ring.
    // =========================================================================

    /// <summary>
    /// The four trench layers, ordered by the priority matrix. Excavation (Kazı)
    /// is special: it only ever interacts with other Excavation, and the pipe
    /// body is NEVER deducted from it. Among the structural layers the strength
    /// order is Bedding &gt; Surround &gt; Backfill.
    /// </summary>
    public enum TrenchLayerType
    {
        Excavation = 0,   // Kazı       – never deducted by the pipe body
        Bedding    = 1,   // Yataklama  – overrides Surround + Backfill
        Surround   = 2,   // Gömlekleme – overrides Backfill, yields to Bedding
        Backfill   = 3    // Geri dolgu – weakest, yields to all
    }

    /// <summary>
    /// Priority helpers for the resolution matrix. Higher rank = stronger layer
    /// (wins the overlap and deducts from weaker layers). The pipe body is
    /// modelled as an absolute pseudo-layer above every real layer.
    /// </summary>
    public static class TrenchLayerPriority
    {
        /// <summary>Absolute priority of the solid pipe body (above all layers).</summary>
        public const int PipeBodyRank = 100;

        /// <summary>
        /// Rank of a real layer. Excavation is given its own high rank because it
        /// is resolved on a separate track (Kazı-vs-Kazı only) and is never
        /// touched by the structural layers or the pipe body.
        /// </summary>
        public static int Rank(TrenchLayerType layer)
        {
            switch (layer)
            {
                case TrenchLayerType.Excavation: return 50; // isolated track
                case TrenchLayerType.Bedding:    return 3;
                case TrenchLayerType.Surround:   return 2;
                case TrenchLayerType.Backfill:   return 1;
                default:                         return 0;
            }
        }

        /// <summary>True when the pipe body is deducted from this layer.</summary>
        public static bool PipeBodyDeducts(TrenchLayerType layer)
            => layer != TrenchLayerType.Excavation;
    }

    /// <summary>
    /// The three resolution scenarios cached per cross-section. Each is a single
    /// global preference applied to every same-type layer tie at the station.
    /// Mirrors <see cref="OverlapAssignment"/> 1-to-1 (Split / LowerPipe / UpperPipe).
    /// </summary>
    public enum TiePreference
    {
        Split     = 0,   // 50/50 vertical split (maintains vertical retaining walls)
        KeepLower = 1,   // deeper-invert pipe keeps the overlap
        KeepUpper = 2,   // shallower-invert pipe keeps the overlap
        Ignore    = 3    // no clipping — each pipe keeps its full gross section
    }

    /// <summary>
    /// One layer's final cached geometry for one scenario: the resolved polygon
    /// coordinates (region — may be empty, one ring, or several) and its net area.
    /// </summary>
    public class LayerNet
    {
        /// <summary>Final resolved region (U,Z) after booleans + priority matrix.</summary>
        public List<List<double[]>> Polygon { get; set; } = new List<List<double[]>>();

        /// <summary>Net cross-section area (m²) of <see cref="Polygon"/>.</summary>
        public double NetArea { get; set; }
    }

    /// <summary>
    /// A complete cached state profile for one preference scenario at one
    /// cross-section: the final geometry + net area of all four layers.
    /// </summary>
    public class ScenarioProfile
    {
        public TiePreference Preference { get; set; }

        public LayerNet Excavation { get; set; } = new LayerNet();  // Kazı
        public LayerNet Bedding    { get; set; } = new LayerNet();  // Yataklama
        public LayerNet Surround   { get; set; } = new LayerNet();  // Gömlekleme
        public LayerNet Backfill   { get; set; } = new LayerNet();  // Geri dolgu

        /// <summary>Returns the cached layer geometry for the given type.</summary>
        public LayerNet Layer(TrenchLayerType t)
        {
            switch (t)
            {
                case TrenchLayerType.Excavation: return Excavation;
                case TrenchLayerType.Bedding:    return Bedding;
                case TrenchLayerType.Surround:   return Surround;
                default:                         return Backfill;
            }
        }
    }
}
