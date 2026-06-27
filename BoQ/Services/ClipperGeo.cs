using System;
using System.Collections.Generic;
using System.Linq;
using Clipper2Lib;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Polygon boolean engine for the pseudo-3D BoQ overlap methodology.
    ///
    /// Every cross-sectional area calculation goes through Clipper2 boolean
    /// operations (Difference / Intersection / Union) rather than manual vertex
    /// if/else node tracking — this is the explicit design commitment of the
    /// methodology. Clipper runs in integer space, so all (U, Z) coordinates are
    /// scaled by <see cref="Scale"/> (1e6 → micron precision) on the way in and
    /// divided back out on the way out.
    ///
    /// Vocabulary
    ///   • ring   = List&lt;double[2]{U,Z}&gt;            (one closed polygon)
    ///   • region = List&lt;ring&gt;                        (0..n disjoint rings / holes)
    /// Booleans always return a region because intersection/difference can split
    /// a shape into several pieces (the "chunking" rule) or punch a hole (the
    /// pipe void inside the surround layer).
    /// </summary>
    public static class ClipperGeo
    {
        /// <summary>
        /// Integer scaling factor (1 unit = 10 nm). 100× finer than the old 1e6, so that
        /// sub-millimetre slope differences between adjacent trapezoids survive the
        /// double→long rounding without collapsing to zero — the root cause of Clipper
        /// spurs on same-slope trench edges.
        /// </summary>
        public const double Scale = 100_000_000.0;

        /// <summary>
        /// Grid-snap resolution (1 mm). Every coordinate is snapped to this grid
        /// before entering Clipper integer space, so floating-point noise smaller than
        /// 1 mm is eliminated and the same nominal geometry always maps to the same
        /// integer path regardless of how it was computed — making cross-scenario
        /// excavation areas exactly reproducible.
        /// </summary>
        public const double SnapGrid = 0.001;

        /// <summary>Areas below this (m²) are treated as numerical noise and dropped.</summary>
        private const double AreaEps = 1e-7;

        private static double Snap(double v) => Math.Round(v / SnapGrid) * SnapGrid;

        // =====================================================================
        // Conversions  (double (U,Z) rings  ↔  Clipper integer paths)
        // =====================================================================

        private static Path64 ToPath(List<double[]> ring)
        {
            var p = new Path64(ring.Count);
            foreach (var v in ring)
                p.Add(new Point64((long)Math.Round(Snap(v[0]) * Scale),
                                  (long)Math.Round(Snap(v[1]) * Scale)));
            return p;
        }

        private static Paths64 ToPaths(List<double[]> ring)
            => new Paths64 { ToPath(ring) };

        private static Paths64 ToPaths(IEnumerable<List<double[]>> region)
        {
            var ps = new Paths64();
            if (region != null)
                foreach (var ring in region)
                    if (ring != null && ring.Count >= 3) ps.Add(ToPath(ring));
            return ps;
        }

        private static List<double[]> FromPath(Path64 path)
        {
            var ring = new List<double[]>(path.Count);
            foreach (var pt in path)
                ring.Add(new[] { Math.Round(pt.X / Scale, 3),
                                 Math.Round(pt.Y / Scale, 3) });
            return ring;
        }

        /// <summary>Converts Clipper output to a region, dropping sub-noise slivers.</summary>
        private static List<List<double[]>> FromPaths(Paths64 paths)
        {
            var region = new List<List<double[]>>();
            if (paths == null) return region;
            foreach (var path in paths)
            {
                if (path.Count < 3) continue;
                if (Math.Abs(Clipper.Area(path)) / (Scale * Scale) < AreaEps) continue;
                region.Add(RemoveSpurs(FromPath(path)));
            }
            return region;
        }

        /// <summary>
        /// Removes redundant vertices from a ring. Two cases are dropped, both of
        /// which are Clipper artefacts that add a visible-but-meaningless vertex:
        ///
        ///   • SPUR  — incoming/outgoing edges nearly anti-parallel (dot ≤ -0.98):
        ///     a degenerate "backtrack" emitted when a clip boundary passes exactly
        ///     through a subject vertex (shared corner after NudgeU).
        ///   • COLLINEAR — incoming/outgoing edges nearly parallel (dot ≥ 0.99999):
        ///     an extra point sitting ON a straight edge, e.g. the (U=0) centre-axis
        ///     point left on the horizontal trench bottom by a split/body clip.
        ///
        /// Both carry negligible area but draw an extra segment in cross-sections.
        /// </summary>
        /// <summary>
        /// Public ring cleanup — strips coincident, spur and collinear vertices.
        /// Call this at any serialization boundary (e.g. before storing a polygon)
        /// as a last line of defence so no degenerate vertex is ever persisted.
        /// </summary>
        public static List<double[]> CleanRing(List<double[]> ring) => RemoveSpurs(ring);

        private static List<double[]> RemoveSpurs(List<double[]> ring)
        {
            if (ring == null || ring.Count < 4) return ring;
            const double SpurDot      = -0.98;     // cos ~168° — nearly reversed
            const double CollinearDot =  0.99999;  // cos ~0.26° — nearly straight
            // Two vertices closer than 1 mm are treated as the SAME point (one is
            // dropped). With SnapGrid=1mm all coordinates are already on the mm grid,
            // so any residual duplicate will differ by at most ~1 mm — this tolerance
            // catches those without eating real corners.
            const double DupTolM      =  1e-3;          // 1 mm
            const double DupEps2      =  DupTolM * DupTolM;  // (1 mm)²

            // Iterate until stable: removing a spike's apex can expose a duplicate
            // vertex (the spike's two legs share an endpoint), which a single pass
            // would leave behind. Each pass also collapses coincident vertices.
            var pts = new List<double[]>(ring);
            bool changed = true;
            while (changed && pts.Count >= 4)
            {
                changed = false;
                int n = pts.Count;
                var result = new List<double[]>(n);
                for (int i = 0; i < n; i++)
                {
                    var prev = pts[(i + n - 1) % n];
                    var curr = pts[i];
                    var next = pts[(i + 1) % n];

                    // Coincident with PREVIOUS → drop curr. Test only against prev
                    // (never next): for an adjacent duplicate pair this keeps the
                    // first and drops the second. Testing both sides would erase a
                    // real corner (drop BOTH members → lost vertex).
                    double dx1 = curr[0] - prev[0], dz1 = curr[1] - prev[1];
                    double len1Sq = dx1 * dx1 + dz1 * dz1;
                    if (len1Sq < DupEps2) { changed = true; continue; }

                    // Outgoing edge zero (next duplicates curr) → keep curr now; next
                    // gets dropped when processed (its prev == curr).
                    double dx2 = next[0] - curr[0], dz2 = next[1] - curr[1];
                    double len2Sq = dx2 * dx2 + dz2 * dz2;
                    if (len2Sq < DupEps2) { result.Add(curr); continue; }

                    double dot = (dx1 * dx2 + dz1 * dz2) / Math.Sqrt(len1Sq * len2Sq);
                    if (dot <= SpurDot)      { changed = true; continue; }  // spur (reversal)
                    if (dot >= CollinearDot) { changed = true; continue; }  // collinear (straight)
                    result.Add(curr);
                }
                if (result.Count < 3) return pts.Count >= 3 ? pts : ring;   // safety floor
                pts = result;
            }
            return pts.Count >= 3 ? pts : ring;
        }

        // =====================================================================
        // Boolean primitives  (region-level)
        // =====================================================================

        /// <summary>subject − clip (Boolean Difference). Automatically chunks/holes.</summary>
        public static List<List<double[]>> Difference(
            IEnumerable<List<double[]>> subject, IEnumerable<List<double[]>> clip)
            => FromPaths(Clipper.Difference(ToPaths(subject), ToPaths(clip), FillRule.NonZero));

        public static List<List<double[]>> Difference(
            List<double[]> subject, List<double[]> clip)
            => FromPaths(Clipper.Difference(ToPaths(subject), ToPaths(clip), FillRule.NonZero));

        /// <summary>subject ∩ clip (Boolean Intersection).</summary>
        public static List<List<double[]>> Intersect(
            IEnumerable<List<double[]>> subject, IEnumerable<List<double[]>> clip)
            => FromPaths(Clipper.Intersect(ToPaths(subject), ToPaths(clip), FillRule.NonZero));

        public static List<List<double[]>> Intersect(
            List<double[]> subject, List<double[]> clip)
            => FromPaths(Clipper.Intersect(ToPaths(subject), ToPaths(clip), FillRule.NonZero));

        /// <summary>a ∪ b (Boolean Union).</summary>
        public static List<List<double[]>> Union(
            IEnumerable<List<double[]>> a, IEnumerable<List<double[]>> b)
        {
            var subj = ToPaths(a);
            subj.AddRange(ToPaths(b));
            return FromPaths(Clipper.Union(subj, FillRule.NonZero));
        }

        /// <summary>Self-union of a region (merges overlapping pieces, fixes orientation).</summary>
        public static List<List<double[]>> Union(IEnumerable<List<double[]>> region)
            => FromPaths(Clipper.Union(ToPaths(region), FillRule.NonZero));

        // =====================================================================
        // Measurement helpers
        // =====================================================================

        /// <summary>Net area (m²) of a region, accounting for holes, rounded to nearest mm² (3 d.p.).</summary>
        public static double Area(IEnumerable<List<double[]>> region)
        {
            if (region == null) return 0;
            double a = 0;
            foreach (var ring in region)
                if (ring != null && ring.Count >= 3)
                    a += Clipper.Area(ToPath(ring));
            return Math.Round(Math.Abs(a) / (Scale * Scale), 3);
        }

        /// <summary>Area (m²) of a single ring, rounded to nearest mm² (3 d.p.).</summary>
        public static double Area(List<double[]> ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            return Math.Round(Math.Abs(Clipper.Area(ToPath(ring))) / (Scale * Scale), 3);
        }

        /// <summary>
        /// Ensures <paramref name="ring"/> has CCW winding (positive signed area in a
        /// right-handed U-Z frame). Reverses the vertex list in place if needed.
        /// Safe to call on any ring before handing it to Clipper.
        /// </summary>
        public static List<double[]> EnsureCCW(List<double[]> ring)
        {
            if (ring == null || ring.Count < 3) return ring;
            double a = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var v0 = ring[i];
                var v1 = ring[(i + 1) % ring.Count];
                a += v0[0] * v1[1] - v1[0] * v0[1];
            }
            if (a < 0) ring.Reverse();
            return ring;
        }

        /// <summary>True when <paramref name="pt"/> is strictly inside <paramref name="ring"/>.</summary>
        public static bool PointStrictlyInside(double[] pt, List<double[]> ring)
        {
            var p = new Point64((long)Math.Round(Snap(pt[0]) * Scale),
                                (long)Math.Round(Snap(pt[1]) * Scale));
            return Clipper.PointInPolygon(p, ToPath(ring)) == PointInPolygonResult.IsInside;
        }

        /// <summary>
        /// True when <paramref name="pt"/> is inside OR on the boundary of
        /// <paramref name="ring"/>. Used by the Rule 3 split-point search: for two
        /// equal-depth trenches the interior base node sits ON the other trench's
        /// base edge (co-planar), so a strict test would wrongly reject it.
        /// </summary>
        public static bool PointInsideOrOn(double[] pt, List<double[]> ring)
        {
            var p = new Point64((long)Math.Round(Snap(pt[0]) * Scale),
                                (long)Math.Round(Snap(pt[1]) * Scale));
            return Clipper.PointInPolygon(p, ToPath(ring)) != PointInPolygonResult.IsOutside;
        }

        // =====================================================================
        // Pipe body  (absolute-priority deduction shape)
        // =====================================================================

        /// <summary>
        /// Builds the solid pipe body as an N-gon in the local (U, Z) frame:
        /// centre on the axis (U = 0), centre elevation = invert + OD/2, radius =
        /// OD/2. 32 segments by default. The pipe body is subtracted from Bedding,
        /// Surround and Backfill — but NEVER from Excavation.
        /// </summary>
        public static List<double[]> PipeBody(double odM, double invertZ, int segments = 32)
        {
            if (odM <= 1e-9) return null;
            double r  = odM * 0.5;
            double cz = invertZ + r;                 // pipe centre elevation
            var ring = new List<double[]>(segments);
            for (int i = 0; i < segments; i++)
            {
                double ang = 2.0 * Math.PI * i / segments;
                ring.Add(new[] { r * Math.Cos(ang), cz + r * Math.Sin(ang) });
            }
            return ring;
        }

        // =====================================================================
        // Phase 3.5 — geometric resolution rules
        // =====================================================================
        //
        // These operate in ONE pipe's (U, Z) frame: `self` is this pipe's layer
        // ring; `other` is the clashing pipe's same-type layer already projected
        // into this frame. They return the NET region kept by `self`. The mirror
        // computation runs independently in the other pipe's frame.
        // =====================================================================

        /// <summary>
        /// Resolves a same-layer clash for <paramref name="self"/> against
        /// <paramref name="other"/> following Phase 3.5 Rules 1–3.
        ///
        /// Rule 1 (Complete Inclusion) overrides the preference: if one polygon is
        /// swallowed by the other (both base OR both top nodes strictly inside),
        /// the inner stays intact and the outer is the difference.
        /// Rule 2 (Keep Upper/Lower): winner intact, loser = loser − winner.
        /// Rule 3 (Split): vertical slice of the overlap; each pipe keeps its side.
        /// </summary>
        /// <param name="self">This pipe's layer ring (U,Z).</param>
        /// <param name="other">Clashing pipe's layer ring, projected into self frame.</param>
        /// <param name="pref">User tie preference.</param>
        /// <param name="selfIsLower">True when this pipe's invert is the deeper one.</param>
        /// <param name="otherCentreU">U-offset of the other pipe's centreline in this frame.</param>
        public static List<List<double[]>> ResolveSameLayer(
            List<double[]> self, List<double[]> other,
            TiePreference pref, bool selfIsLower, double otherCentreU)
            => Difference(new List<List<double[]>> { self },
                          SurrenderRegion(self, other, pref, selfIsLower, otherCentreU));

        /// <summary>
        /// The sub-region of <paramref name="self"/> that this pipe must CEDE to a
        /// same-type neighbour under Phase 3.5. The net kept geometry is then simply
        /// <c>self − Union(all surrenders)</c>.
        ///
        /// Expressing resolution as "what to subtract" is what lets the orchestrator
        /// fold every clash — multiple pipes, multiple layers, the pipe body — into a
        /// single Boolean Difference against a union, so the "chunk the overlap and
        /// apply each rule per sub-area" requirement falls out for free.
        ///
        /// Rule 1 (Complete Inclusion) overrides the preference; Rule 2 cedes all or
        /// nothing; Rule 3 cedes the half of the overlap on the other pipe's side.
        /// </summary>
        public static List<List<double[]>> SurrenderRegion(
            List<double[]> self, List<double[]> other,
            TiePreference pref, bool selfIsLower, double otherCentreU)
        {
            var none = new List<List<double[]>>();

            var overlap = Intersect(self, other);
            if (overlap.Count == 0) return none;                // no clash → cede nothing

            // ── Rule 1: Complete Inclusion (absolute override) ────────────────
            if (IsSwallowedBy(self, other)) return none;        // self is inner → keep all
            if (IsSwallowedBy(other, self)) return overlap;     // self is outer → cede the inner zone

            // ── Rule 2: Keep Upper / Keep Lower ───────────────────────────────
            if (pref != TiePreference.Split)
            {
                bool selfWins = (pref == TiePreference.KeepLower &&  selfIsLower)
                             || (pref == TiePreference.KeepUpper && !selfIsLower);
                return selfWins ? none : overlap;
            }

            // ── Rule 3: 50/50 vertical split (maintain vertical retaining walls)─
            // upper = shallower trench, lower = deeper trench.
            List<double[]> lower = selfIsLower ? self : other;
            List<double[]> upper = selfIsLower ? other : self;
            double xSplit = FindSplitX(upper, lower, otherCentreU, selfIsLower);

            var (leftHalf, rightHalf) = VerticalSplit(overlap, xSplit);
            bool selfIsLeft = otherCentreU >= 0;                // other on the right → self is left
            return selfIsLeft ? rightHalf : leftHalf;           // cede the far half
        }

        /// <summary>
        /// Phase 3.5 Rule 1 test: is <paramref name="inner"/> swallowed by
        /// <paramref name="outer"/>? True when BOTH base nodes OR BOTH top nodes of
        /// inner are strictly inside outer.
        /// </summary>
        public static bool IsSwallowedBy(List<double[]> inner, List<double[]> outer)
        {
            if (inner == null || inner.Count < 3 || outer == null || outer.Count < 3)
                return false;

            var byZ = inner.OrderBy(v => v[1]).ToList();
            // two lowest-Z vertices = base nodes; two highest-Z = top nodes
            bool baseIn = PointStrictlyInside(byZ[0], outer)
                       && PointStrictlyInside(byZ[1], outer);
            bool topIn  = PointStrictlyInside(byZ[byZ.Count - 1], outer)
                       && PointStrictlyInside(byZ[byZ.Count - 2], outer);
            return baseIn || topIn;
        }

        /// <summary>
        /// Phase 3.5 Rule 3 step 2: the split X-coordinate is the U of the lowest
        /// node of the upper trench that lies inside the lower trench.
        ///
        /// For two equal-depth trenches whose bases overlap, the relevant node is an
        /// INTERIOR BASE NODE — the upper trench's base corner sitting within the
        /// lower trench's base. Because both bases are at the same elevation that
        /// corner lies ON the lower trench's base edge, so the test must accept
        /// on-boundary points (<see cref="PointInsideOrOn"/>); a strict test would
        /// reject it and the split would wrongly collapse to the centreline midpoint.
        /// Dropping the vertical wall from this node slices the V-shaped sloped
        /// overlap above it, distributing it between both networks while preserving
        /// the prioritised trench's full base width as a retaining wall.
        ///
        /// The midpoint is only a last-resort fallback when no node qualifies (e.g.
        /// degenerate vertical-wall geometry with no sloped overlap to slice).
        /// </summary>
        public static double FindSplitX(
            List<double[]> upper, List<double[]> lower, double otherCentreU, bool selfIsLower)
        {
            double bestZ = double.MaxValue, bestU = double.NaN;
            foreach (var v in upper)
            {
                if (v[1] < bestZ && PointInsideOrOn(v, lower))
                {
                    bestZ = v[1];
                    bestU = v[0];
                }
            }
            if (!double.IsNaN(bestU)) return bestU;

            // Fallback: midpoint between self centre (U=0) and other centre.
            return otherCentreU * 0.5;
        }

        /// <summary>
        /// Slices a region with a vertical line at U = <paramref name="xSplit"/> and
        /// returns the (left, right) halves. Implemented as two boolean
        /// intersections with tall clip rectangles — keeps the vertical retaining
        /// wall exactly, instead of just halving the numeric area.
        /// </summary>
        public static (List<List<double[]>> left, List<List<double[]>> right) VerticalSplit(
            IEnumerable<List<double[]>> region, double xSplit)
        {
            double minU = double.MaxValue, maxU = double.MinValue;
            double minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var ring in region)
                foreach (var v in ring)
                {
                    if (v[0] < minU) minU = v[0]; if (v[0] > maxU) maxU = v[0];
                    if (v[1] < minZ) minZ = v[1]; if (v[1] > maxZ) maxZ = v[1];
                }
            if (minU > maxU) return (new List<List<double[]>>(), new List<List<double[]>>());

            double padU = (maxU - minU) + 1.0, padZ = (maxZ - minZ) + 1.0;
            var leftRect = new List<double[]>
            {
                new[] { minU - padU, minZ - padZ }, new[] { xSplit,      minZ - padZ },
                new[] { xSplit,      maxZ + padZ }, new[] { minU - padU, maxZ + padZ }
            };
            var rightRect = new List<double[]>
            {
                new[] { xSplit,      minZ - padZ }, new[] { maxU + padU, minZ - padZ },
                new[] { maxU + padU, maxZ + padZ }, new[] { xSplit,      maxZ + padZ }
            };
            return (Intersect(region, new List<List<double[]>> { leftRect }),
                    Intersect(region, new List<List<double[]>> { rightRect }));
        }

        // =====================================================================
        // Conservation utilities
        // =====================================================================

        /// <summary>
        /// Snaps output ring vertices to the nearest input vertex when within
        /// <paramref name="tol"/> metres (default 0.5 mm). Corrects sub-millimetre
        /// floating-point drift from the Snap→integer→double round-trip: an input
        /// vertex on the 1 mm grid that Clipper preserves should appear identically
        /// in the output — this restores the exact input value so shared boundary
        /// points between adjacent pipes are bit-for-bit identical in both rings.
        /// </summary>
        public static List<List<double[]>> SnapToInputs(
            List<List<double[]>> output,
            IEnumerable<List<double[]>> inputs,
            double tol = 0.0005)
        {
            if (output == null || output.Count == 0) return output;

            var inputVerts = new List<double[]>();
            if (inputs != null)
                foreach (var ring in inputs)
                    if (ring != null)
                        foreach (var v in ring)
                            inputVerts.Add(v);
            if (inputVerts.Count == 0) return output;

            double tol2 = tol * tol;
            var result = new List<List<double[]>>(output.Count);
            foreach (var ring in output)
            {
                if (ring == null) { result.Add(ring); continue; }
                var snapped = new List<double[]>(ring.Count);
                foreach (var v in ring)
                {
                    double[] best = null;
                    double bestD2 = tol2;
                    foreach (var iv in inputVerts)
                    {
                        double du = v[0] - iv[0], dz = v[1] - iv[1];
                        double d2 = du * du + dz * dz;
                        if (d2 < bestD2) { bestD2 = d2; best = iv; }
                    }
                    snapped.Add(best != null ? new[] { best[0], best[1] } : v);
                }
                result.Add(snapped);
            }
            return result;
        }
    }
}
