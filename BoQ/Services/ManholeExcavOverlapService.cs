using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Step 2 (diagnostic only): calculates the raw overlap volume between each
    /// manhole's isolated excavation frustum and every connected pipe trench,
    /// using horizontal Z-slicing + 2-D Clipper intersection + Simpson's 1/3 rule.
    ///
    /// The manhole square footprint is rotated to the bisector angle between the
    /// lowest inlet pipe and the lowest outlet pipe directions (or to the single
    /// available pipe's direction when only one type exists).
    ///
    /// The overlap volumes are NOT yet deducted from the BoQ totals — results are
    /// returned as human-readable strings for printing to the AutoCAD command line.
    /// </summary>
    public static class ManholeExcavOverlapService
    {
        // ── Hardcoded manhole excavation parameters (Step 1 constants) ────────
        private const double MhWorkingSpace = 0.5;          // m each side
        private const double MhDiam         = 1.0;          // m
        // Half-side at the very base = radius + working space = 0.5 + 0.5 = 1.0 m
        private const double MhBaseHalfSide = (MhDiam / 2.0) + MhWorkingSpace;
        private const double MhSlopeH       = 1.0 / 3.0;   // 1H:3V → Δhalf-side per metre of height

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// For every manhole in the report, computes the total volume of overlap
        /// between the manhole excavation frustum and all connected pipe trenches.
        ///
        /// Returns one diagnostic line per manhole (ready for WriteMessage).
        /// </summary>
        public static List<string> Compute(BoQReport report)
        {
            var lines = new List<string>();
            if (report?.Systems == null || report.SectionDebug == null) return lines;

            // Index sections by node name for fast lookup.
            // "outlet" = pipe that STARTS at this manhole (water flows out).
            // "inlet"  = pipe that ENDS   at this manhole (water flows in).
            var outlets = report.SectionDebug          // StartNodeName == manhole
                .Where(s => s.StartNodeName != null)
                .GroupBy(s => s.StartNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var inlets = report.SectionDebug           // EndNodeName == manhole
                .Where(s => s.EndNodeName != null)
                .GroupBy(s => s.EndNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var sys in report.Systems)
            {
                foreach (var mh in sys.Manholes)
                {
                    if (mh.ExcavationDepth <= 1e-6) continue;

                    double zTop    = mh.TerrainElevation;
                    double zBottom = zTop - mh.ExcavationDepth;  // absolute lowest invert

                    // ── Compute the square's rotation angle ───────────────────
                    double rotAngle = ComputeRotationAngle(mh, outlets, inlets);

                    // ── Collect connected pipes: (sdr, invertAtMh, dirX, dirY) ─
                    // dirX/dirY = unit vector FROM manhole outward along the pipe.
                    var connectedPipes = new List<(SectionDebugRow sdr,
                                                   double invertAtMh,
                                                   double dirX, double dirY)>();

                    if (outlets.TryGetValue(mh.NodeName, out var outList))
                    {
                        foreach (var sdr in outList)
                        {
                            double dx = sdr.EndX - sdr.StartX;
                            double dy = sdr.EndY - sdr.StartY;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < 1e-6) continue;
                            connectedPipes.Add((sdr, sdr.InvertStart, dx / len, dy / len));
                        }
                    }

                    if (inlets.TryGetValue(mh.NodeName, out var inList))
                    {
                        foreach (var sdr in inList)
                        {
                            // Direction away from manhole = back along the pipe toward its start
                            double dx = sdr.StartX - sdr.EndX;
                            double dy = sdr.StartY - sdr.EndY;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < 1e-6) continue;
                            connectedPipes.Add((sdr, sdr.InvertEnd, dx / len, dy / len));
                        }
                    }

                    // Simpson's rule uses 3 Z-slices tied to the manhole span:
                    // Z_bottom (base) → Z_mid → Z_top (terrain).
                    double zManhMid = (zBottom + zTop) * 0.5;
                    double Hmh      = zTop - zBottom;
                    if (Hmh <= 1e-6) continue;

                    // At each slice: manhole square polygon
                    var mhBot = ManholeSquareAt(mh.X, mh.Y, zBottom, zBottom, rotAngle);
                    var mhMid = ManholeSquareAt(mh.X, mh.Y, zBottom, zManhMid, rotAngle);
                    var mhTop = ManholeSquareAt(mh.X, mh.Y, zBottom, zTop,     rotAngle);

                    // Per-pipe intersection polygons at each slice (union across all pipes)
                    var unionBot = new List<List<double[]>>();
                    var unionMid = new List<List<double[]>>();
                    var unionTop = new List<List<double[]>>();

                    // Per-pipe individual intersection volumes (Value 1 & 2 per pipe)
                    var perPipeVolumes = new List<(string label, double vol)>();

                    foreach (var (sdr, invertAtMh, dirX, dirY) in connectedPipes)
                    {
                        // Trench polygon at each manhole slice elevation
                        var trBot = TrenchRectAt(mh.X, mh.Y, zBottom,  invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                        var trMid = TrenchRectAt(mh.X, mh.Y, zManhMid, invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                        var trTop = TrenchRectAt(mh.X, mh.Y, zTop,     invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);

                        // Individual intersection with manhole at each slice
                        double aBot = AreaOfIntersect(mhBot, trBot);
                        double aMid = AreaOfIntersect(mhMid, trMid);
                        double aTop = AreaOfIntersect(mhTop, trTop);

                        double pipeVol = (Hmh / 6.0) * (aBot + 4.0 * aMid + aTop);
                        string lbl = $"{sdr.StartNodeName}→{sdr.EndNodeName}";
                        perPipeVolumes.Add((lbl, pipeVol));

                        // Accumulate into union (for Value 3 calculation)
                        if (trBot != null) unionBot.Add(trBot);
                        if (trMid != null) unionMid.Add(trMid);
                        if (trTop != null) unionTop.Add(trTop);
                    }

                    // Value 3: manhole area NOT covered by any trench = Difference(mh, Union(trenches))
                    double freeBot = AreaOfDiff(mhBot, unionBot);
                    double freeMid = AreaOfDiff(mhMid, unionMid);
                    double freeTop = AreaOfDiff(mhTop, unionTop);
                    double freeVol = (Hmh / 6.0) * (freeBot + 4.0 * freeMid + freeTop);

                    // Build output line
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"\n  Manhole {mh.NodeName}" +
                              $"  (rot={rotAngle * 180.0 / Math.PI:F1}°)");
                    foreach (var (lbl, vol) in perPipeVolumes)
                        sb.Append($"\n    Pipe [{lbl}] overlap = {vol:F4} m3");
                    sb.Append($"\n    Free (no pipe)      = {freeVol:F4} m3");
                    lines.Add(sb.ToString());
                }
            }

            return lines;
        }

        // =====================================================================
        // Public API – Manhole vs Manhole
        // =====================================================================

        /// <summary>
        /// Geometry State Retention pass. For each overlapping pair:
        ///   GeoLower = segment from ZBottom → zTouch  (pure raw, no overlap)
        ///   GeoUpper = segment from zTouch  → ZTop    (overlap zone, split Inside/Outside)
        ///
        /// OutsideRegion = Difference(Raw, totalIntersect)  — exclusive zone.
        /// InsideRegion  = this manhole's share of totalIntersect (split via TouchPoly_High).
        /// </summary>
        public static List<string> ComputeManholeVsManhole(BoQReport report)
        {
            var lines = new List<string>();
            if (report?.Systems == null || report.SectionDebug == null) return lines;

            var outlets = report.SectionDebug
                .Where(s => s.StartNodeName != null)
                .GroupBy(s => s.StartNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var inlets = report.SectionDebug
                .Where(s => s.EndNodeName != null)
                .GroupBy(s => s.EndNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Build MhInfo list (no geo initialisation here — done per pair)
            var all = new List<MhInfo>();
            foreach (var sys in report.Systems)
            {
                foreach (var mh in sys.Manholes)
                {
                    if (mh.ExcavationDepth <= 1e-6) continue;
                    double zTop    = mh.TerrainElevation;
                    double zBottom = zTop - mh.ExcavationDepth;
                    double rot     = ComputeRotationAngle(mh, outlets, inlets);
                    double halfTop = MhBaseHalfSide + mh.ExcavationDepth * MhSlopeH;
                    double extent  = halfTop * (Math.Abs(Math.Cos(rot)) + Math.Abs(Math.Sin(rot)));
                    all.Add(new MhInfo
                    {
                        Mh       = mh,
                        ZTop     = zTop,
                        ZBottom  = zBottom,
                        RotAngle = rot,
                        AabbMinX = mh.X - extent,
                        AabbMaxX = mh.X + extent,
                        AabbMinY = mh.Y - extent,
                        AabbMaxY = mh.Y + extent,
                    });
                }
            }

            for (int i = 0; i < all.Count - 1; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    var a = all[i];
                    var b = all[j];

                    // AABB pre-filter
                    if (a.AabbMaxX < b.AabbMinX || b.AabbMaxX < a.AabbMinX) continue;
                    if (a.AabbMaxY < b.AabbMinY || b.AabbMaxY < a.AabbMinY) continue;

                    double dist = Math.Sqrt(
                        (a.Mh.X - b.Mh.X) * (a.Mh.X - b.Mh.X) +
                        (a.Mh.Y - b.Mh.Y) * (a.Mh.Y - b.Mh.Y));
                    if (dist < 1e-6) continue;

                    double zTouchRaw = (dist - 2.0 * MhBaseHalfSide) / (2.0 * MhSlopeH)
                                       + (a.ZBottom + b.ZBottom) / 2.0;
                    double zTopEff   = Math.Min(a.ZTop, b.ZTop);
                    double zTouch    = Math.Max(zTouchRaw, Math.Max(a.ZBottom, b.ZBottom));
                    if (zTouch >= zTopEff) continue;

                    // M_high = higher ZBottom (shallower). Its footprint at zTouch is the cutter.
                    var mHigh = a.ZBottom >= b.ZBottom ? a : b;
                    var mLow  = a.ZBottom >= b.ZBottom ? b : a;

                    // Half-plane: large CCW polygon on M_high's side of the facing edge
                    // at zTouch. This edge is fixed in XY for all elevations above zTouch;
                    // only its active length changes as the intersection polygon grows.
                    var halfPlane = BuildHalfPlane(mHigh, mLow, zTouch);
                    if (halfPlane == null) continue;

                    // Build geo segments for both manholes
                    BuildGeoSegments(mHigh, mHigh, mLow, zTouch, zTopEff, halfPlane, isHigh: true);
                    BuildGeoSegments(mLow,  mHigh, mLow, zTouch, zTopEff, halfPlane, isHigh: false);

                    // Volumes
                    double volHigh = EffectiveVolume(mHigh.Mh);
                    double volLow  = EffectiveVolume(mLow.Mh);

                    lines.Add(
                        $"\n  [{mHigh.Mh.NodeName}](HIGH) & [{mLow.Mh.NodeName}](LOW)" +
                        $"  dist={dist:F3}  zTouch={zTouch:F3}  zTopEff={zTopEff:F3}" +
                        $"\n  [{mHigh.Mh.NodeName}] zBot={mHigh.ZBottom:F3}  zTop={mHigh.ZTop:F3}" +
                        SegmentSummary(mHigh.Mh.GeoLower, "Lower") +
                        SegmentSummary(mHigh.Mh.GeoUpper, "Upper") +
                        $"  → effVol={volHigh:F4} m³" +
                        $"\n  [{mLow.Mh.NodeName}]  zBot={mLow.ZBottom:F3}  zTop={mLow.ZTop:F3}" +
                        SegmentSummary(mLow.Mh.GeoLower, "Lower") +
                        SegmentSummary(mLow.Mh.GeoUpper, "Upper") +
                        $"  → effVol={volLow:F4} m³");
                }
            }

            return lines;
        }

        // ── Segment builders ─────────────────────────────────────────────────

        /// <summary>
        /// Builds GeoLower and GeoUpper for <paramref name="self"/>.
        /// GeoLower spans ZBottom → zTouch (pure raw, no intersection).
        /// GeoUpper spans zTouch  → zTopEff (intersection zone, Inside/Outside split).
        /// </summary>
        private static void BuildGeoSegments(
            MhInfo self,
            MhInfo mHigh, MhInfo mLow,
            double zTouch, double zTopEff,
            List<double[]> halfPlane,
            bool isHigh)
        {
            var mh = self.Mh;

            // ── Lower segment: ZBottom → zTouch ──────────────────────────────
            double hLower = zTouch - self.ZBottom;
            if (hLower > 1e-6)
            {
                double zLowerMid = self.ZBottom + hLower * 0.5;
                mh.GeoLower = new ManholeGeoSegment
                {
                    Bottom = MakeRawLevel(mh.X, mh.Y, self.ZBottom, self.ZBottom, self.RotAngle),
                    Mid    = MakeRawLevel(mh.X, mh.Y, self.ZBottom, zLowerMid,   self.RotAngle),
                    Top    = MakeRawLevel(mh.X, mh.Y, self.ZBottom, zTouch,      self.RotAngle),
                };
            }
            else
            {
                mh.GeoLower = null;  // zTouch == ZBottom, no lower zone
            }

            // ── Upper segment: zTouch → zTopEff ──────────────────────────────
            double hUpper = zTopEff - zTouch;
            if (hUpper > 1e-6)
            {
                double zUpperMid = zTouch + hUpper * 0.5;
                mh.GeoUpper = new ManholeGeoSegment
                {
                    Bottom = MakeUpperLevel(mh, self, mHigh, mLow, zTouch,    halfPlane, isHigh),
                    Mid    = MakeUpperLevel(mh, self, mHigh, mLow, zUpperMid, halfPlane, isHigh),
                    Top    = MakeUpperLevel(mh, self, mHigh, mLow, zTopEff,   halfPlane, isHigh),
                };
            }
        }

        private static ManholeGeoLevel MakeRawLevel(
            double cx, double cy, double zBottom, double z, double rot)
        {
            var raw = ManholeSquareAt(cx, cy, zBottom, z, rot);
            return new ManholeGeoLevel
            {
                Z             = z,
                RawPoly       = raw,
                InsideRegion  = null,
                OutsideRegion = raw != null
                    ? new List<List<double[]>> { raw }
                    : null,
            };
        }

        /// <summary>
        /// Builds one upper-segment level at elevation z with correct Inside/Outside split.
        /// OutsideRegion = Difference(Raw, totalIntersect)  — exclusive zone only.
        /// InsideRegion  = this manhole's claimed share of totalIntersect.
        /// </summary>
        private static ManholeGeoLevel MakeUpperLevel(
            ManholeItem mh,
            MhInfo self, MhInfo mHigh, MhInfo mLow,
            double z,
            List<double[]> halfPlane,
            bool isHigh)
        {
            var raw = ManholeSquareAt(mh.X, mh.Y, self.ZBottom, z, self.RotAngle);
            var level = new ManholeGeoLevel { Z = z, RawPoly = raw };
            if (raw == null) return level;

            // Total 2-D intersection of both manholes at this elevation
            var polyHigh = ManholeSquareAt(
                mHigh.Mh.X, mHigh.Mh.Y, mHigh.ZBottom, z, mHigh.RotAngle);
            var polyLow  = ManholeSquareAt(
                mLow.Mh.X,  mLow.Mh.Y,  mLow.ZBottom,  z, mLow.RotAngle);

            List<List<double[]>> totalIntersect = null;
            if (polyHigh != null && polyLow != null)
                totalIntersect = ClipperGeo.Intersect(polyHigh, polyLow);

            if (totalIntersect == null || totalIntersect.Count == 0 ||
                ClipperGeo.Area(totalIntersect) <= 1e-9)
            {
                level.InsideRegion  = null;
                level.OutsideRegion = new List<List<double[]>> { raw };
                return level;
            }

            // Split totalIntersect using the fixed half-plane:
            // The half-plane is defined by M_high's facing edge at zTouch (fixed XY),
            // but its active length changes at each z as totalIntersect grows.
            //
            // M_high's share = Intersect(totalIntersect, halfPlane)
            // M_low's share  = Difference(totalIntersect, halfPlane)
            List<List<double[]>> insideRegion;
            if (isHigh)
            {
                insideRegion = totalIntersect.Count == 1
                    ? ClipperGeo.Intersect(totalIntersect[0], halfPlane)
                    : IntersectRegionWithRing(totalIntersect, halfPlane);
            }
            else
            {
                insideRegion = ClipperGeo.Difference(
                    totalIntersect,
                    new List<List<double[]>> { halfPlane });
            }

            bool hasInside = insideRegion != null && insideRegion.Count > 0 &&
                             ClipperGeo.Area(insideRegion) > 1e-9;

            level.InsideRegion = hasInside ? insideRegion : null;

            // OutsideRegion = Raw minus the TOTAL intersection (exclusive zone only)
            level.OutsideRegion = ClipperGeo.Difference(
                new List<List<double[]>> { raw },
                totalIntersect);

            return level;
        }

        /// <summary>
        /// Builds a large CCW half-plane polygon on M_high's side of its facing edge
        /// at zTouch. The facing edge is the side of M_high's polygon at zTouch whose
        /// outward normal points most toward M_low. This edge stays fixed in XY for
        /// all elevations above zTouch — only its active intersection length changes.
        /// </summary>
        private static List<double[]> BuildHalfPlane(MhInfo mHigh, MhInfo mLow, double zTouch)
        {
            var poly = ManholeSquareAt(
                mHigh.Mh.X, mHigh.Mh.Y, mHigh.ZBottom, zTouch, mHigh.RotAngle);
            if (poly == null || poly.Count < 3) return null;

            // Unit vector from M_high toward M_low
            double dxDir = mLow.Mh.X - mHigh.Mh.X;
            double dyDir = mLow.Mh.Y - mHigh.Mh.Y;
            double dirLen = Math.Sqrt(dxDir * dxDir + dyDir * dyDir);
            if (dirLen < 1e-9) return null;
            dxDir /= dirLen;
            dyDir /= dirLen;

            // For a CCW polygon, the outward normal of edge k→k+1 is (dy, -dx)
            // Find the edge whose outward normal aligns best with M_high→M_low
            int n = poly.Count;
            int bestEdge = 0;
            double bestDot = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double edgeDx = poly[(k + 1) % n][0] - poly[k][0];
                double edgeDy = poly[(k + 1) % n][1] - poly[k][1];
                double eLen   = Math.Sqrt(edgeDx * edgeDx + edgeDy * edgeDy);
                if (eLen < 1e-9) continue;
                double normX = edgeDy / eLen;   // outward normal X
                double normY = -edgeDx / eLen;  // outward normal Y
                double dot   = normX * dxDir + normY * dyDir;
                if (dot > bestDot) { bestDot = dot; bestEdge = k; }
            }

            // Facing edge: A → B
            double[] edgeA = poly[bestEdge];
            double[] edgeB = poly[(bestEdge + 1) % n];

            // Edge unit direction
            double ex = edgeB[0] - edgeA[0];
            double ey = edgeB[1] - edgeA[1];
            double eL = Math.Sqrt(ex * ex + ey * ey);
            if (eL < 1e-9) return null;
            ex /= eL;
            ey /= eL;

            // Left perpendicular of edge direction (interior of M_high for CCW polygon)
            // = (-ey, ex)
            const double Large = 50000.0;
            double[] p0 = new[] { edgeA[0] - Large * ex,             edgeA[1] - Large * ey             };
            double[] p1 = new[] { edgeB[0] + Large * ex,             edgeB[1] + Large * ey             };
            double[] p2 = new[] { edgeB[0] + Large * ex - Large * ey, edgeB[1] + Large * ey + Large * ex };
            double[] p3 = new[] { edgeA[0] - Large * ex - Large * ey, edgeA[1] - Large * ey + Large * ex };

            // CCW: p0 → p1 (along edge) → p2 (inward) → p3 (inward) → p0
            return new List<double[]> { p0, p1, p2, p3 };
        }

        private static List<List<double[]>> IntersectRegionWithRing(
            List<List<double[]>> region, List<double[]> ring)
        {
            List<List<double[]>> result = null;
            foreach (var poly in region)
            {
                var part = ClipperGeo.Intersect(poly, ring);
                if (part == null || part.Count == 0) continue;
                result = result == null ? part : ClipperGeo.Union(result, part);
            }
            return result;
        }

        // ── Volume helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Effective excavation volume = lower segment (full raw) + upper segment
        /// (exclusive zone + this manhole's Inside share).
        /// </summary>
        private static double EffectiveVolume(ManholeItem mh)
        {
            return SegmentVolume(mh.GeoLower, includeInside: false) +
                   SegmentVolume(mh.GeoUpper, includeInside: true);
        }

        private static double SegmentVolume(ManholeGeoSegment seg, bool includeInside)
        {
            if (seg?.Bottom == null || seg.Mid == null || seg.Top == null) return 0;
            double H = seg.Top.Z - seg.Bottom.Z;
            if (H <= 1e-6) return 0;
            double aBot = LevelEffArea(seg.Bottom, includeInside);
            double aMid = LevelEffArea(seg.Mid,    includeInside);
            double aTop = LevelEffArea(seg.Top,    includeInside);
            return (H / 6.0) * (aBot + 4.0 * aMid + aTop);
        }

        private static double LevelEffArea(ManholeGeoLevel lvl, bool includeInside)
        {
            if (lvl == null) return 0;
            double area = TotalArea(lvl.OutsideRegion);
            if (includeInside) area += TotalArea(lvl.InsideRegion);
            return area;
        }

        private static double TotalArea(List<List<double[]>> region)
        {
            if (region == null || region.Count == 0) return 0;
            return ClipperGeo.Area(region);
        }

        // ── Diagnostic helpers ───────────────────────────────────────────────

        private static string SegmentSummary(ManholeGeoSegment seg, string tag)
        {
            if (seg == null) return $"\n    {tag}: (none)";
            return $"\n    {tag}: " +
                   LevelSummary(seg.Bottom, "Bot") + "  " +
                   LevelSummary(seg.Mid,    "Mid") + "  " +
                   LevelSummary(seg.Top,    "Top");
        }

        private static string LevelSummary(ManholeGeoLevel lvl, string tag)
        {
            if (lvl == null) return $"[{tag}:null]";
            double rawA = lvl.RawPoly != null ? ClipperGeo.Area(lvl.RawPoly) : 0;
            double outA = TotalArea(lvl.OutsideRegion);
            double inA  = TotalArea(lvl.InsideRegion);
            return $"[{tag} z={lvl.Z:F2} raw={rawA:F3} out={outA:F3} in={inA:F3}]";
        }

        // ── Precomputed per-manhole data (used only inside ComputeManholeVsManhole) ──
        private struct MhInfo
        {
            public ManholeItem Mh;
            public double ZTop, ZBottom, RotAngle;
            public double AabbMinX, AabbMaxX, AabbMinY, AabbMaxY;
        }

        /// <summary>
        /// 2-D Clipper intersection area (m²) of two manhole footprints at elevation z.
        /// Each footprint uses its own zBottom and rotAngle.
        /// </summary>
        private static double IntersectTwoManholes(MhInfo a, MhInfo b, double z)
        {
            var polyA = ManholeSquareAt(a.Mh.X, a.Mh.Y, a.ZBottom, z, a.RotAngle);
            var polyB = ManholeSquareAt(b.Mh.X, b.Mh.Y, b.ZBottom, z, b.RotAngle);
            if (polyA == null || polyB == null) return 0;
            return ClipperGeo.Area(ClipperGeo.Intersect(polyA, polyB));
        }

        // =====================================================================
        // Rotation angle
        // =====================================================================

        /// <summary>
        /// Computes the rotation angle (radians) to apply to the manhole square:
        /// • Both inlet and outlet exist → bisector of lowest-inlet and lowest-outlet directions.
        /// • Only outlets → direction of the lowest outlet.
        /// • Only inlets  → direction of the lowest inlet.
        /// • Neither       → 0.
        ///
        /// "Lowest" = pipe with the smallest invert elevation at this manhole.
        /// Directions are unit vectors FROM the manhole outward along each pipe.
        /// </summary>
        private static double ComputeRotationAngle(
            ManholeItem mh,
            Dictionary<string, List<SectionDebugRow>> outlets,
            Dictionary<string, List<SectionDebugRow>> inlets)
        {
            // Lowest outlet (StartNodeName == mh): lowest InvertStart
            double[] outDir = null;
            if (outlets.TryGetValue(mh.NodeName, out var outList) && outList.Count > 0)
            {
                var lowest = outList.OrderBy(s => s.InvertStart).First();
                double dx = lowest.EndX - lowest.StartX;
                double dy = lowest.EndY - lowest.StartY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len >= 1e-6) outDir = new[] { dx / len, dy / len };
            }

            // Lowest inlet (EndNodeName == mh): lowest InvertEnd
            double[] inDir = null;
            if (inlets.TryGetValue(mh.NodeName, out var inList) && inList.Count > 0)
            {
                var lowest = inList.OrderBy(s => s.InvertEnd).First();
                // Direction away from manhole = back toward the pipe's start node
                double dx = lowest.StartX - lowest.EndX;
                double dy = lowest.StartY - lowest.EndY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len >= 1e-6) inDir = new[] { dx / len, dy / len };
            }

            if (outDir != null && inDir != null)
            {
                // Bisector: average the two unit vectors, then take atan2.
                // This correctly handles the circular wrap-around.
                double bx = outDir[0] + inDir[0];
                double by = outDir[1] + inDir[1];
                double bLen = Math.Sqrt(bx * bx + by * by);
                if (bLen < 1e-9)
                {
                    // The two directions are exactly opposite (180° apart) —
                    // fall back to the outlet direction.
                    return Math.Atan2(outDir[1], outDir[0]);
                }
                return Math.Atan2(by / bLen, bx / bLen);
            }

            if (outDir != null) return Math.Atan2(outDir[1], outDir[0]);
            if (inDir  != null) return Math.Atan2(inDir[1],  inDir[0]);
            return 0.0;
        }

        // =====================================================================
        // Area helpers
        // =====================================================================

        private static double AreaOfIntersect(List<double[]> mhPoly, List<double[]> trPoly)
        {
            if (mhPoly == null || trPoly == null) return 0;
            return ClipperGeo.Area(ClipperGeo.Intersect(mhPoly, trPoly));
        }

        private static double AreaOfDiff(List<double[]> mhPoly, List<List<double[]>> trPolys)
        {
            if (mhPoly == null) return 0;
            if (trPolys == null || trPolys.Count == 0) return ClipperGeo.Area(mhPoly);

            // Union all trench polygons, then subtract from manhole polygon
            var region = new List<List<double[]>> { trPolys[0] };
            for (int i = 1; i < trPolys.Count; i++)
                region = ClipperGeo.Union(region, new List<List<double[]>> { trPolys[i] });

            return ClipperGeo.Area(ClipperGeo.Difference(
                new List<List<double[]>> { mhPoly }, region));
        }

        // =====================================================================
        // Polygon builders
        // =====================================================================

        /// <summary>
        /// Rotated square manhole footprint in XY at elevation z.
        /// Half-side = MhBaseHalfSide + (z - zBottom) * MhSlopeH.
        /// The square is centred at (cx, cy) and rotated by <paramref name="rotAngle"/> radians.
        /// </summary>
        private static List<double[]> ManholeSquareAt(
            double cx, double cy, double zBottom, double z, double rotAngle)
        {
            double rise     = Math.Max(0, z - zBottom);
            double halfSide = MhBaseHalfSide + rise * MhSlopeH;
            if (halfSide <= 1e-9) return null;

            double cosA = Math.Cos(rotAngle);
            double sinA = Math.Sin(rotAngle);

            // Axis-aligned offsets, then rotate each corner about (cx, cy)
            var localCorners = new[]
            {
                new[] { -halfSide, -halfSide },  // BL
                new[] {  halfSide, -halfSide },  // BR
                new[] {  halfSide,  halfSide },  // TR
                new[] { -halfSide,  halfSide },  // TL
            };

            var ring = new List<double[]>(4);
            foreach (var lc in localCorners)
            {
                ring.Add(new[]
                {
                    cx + lc[0] * cosA - lc[1] * sinA,
                    cy + lc[0] * sinA + lc[1] * cosA
                });
            }
            return ring;  // CCW preserved by rotation
        }

        /// <summary>
        /// Trench rectangle footprint in XY at elevation z.
        /// Width = baseTrenchWidth + 2*(z - invertAtMh)*slopeRatio, centred on pipe axis.
        /// Starts at the manhole centre and extends 10 m outward along the pipe direction.
        /// </summary>
        private static List<double[]> TrenchRectAt(
            double cx, double cy,
            double z, double invertAtMh,
            double baseTrenchWidth, double slopeRatio,
            double dirX, double dirY)
        {
            double rise  = Math.Max(0, z - invertAtMh);
            double halfW = (baseTrenchWidth + 2.0 * rise * slopeRatio) * 0.5;
            if (halfW <= 1e-9) return null;

            // Perpendicular (90° CCW from dir)
            double perpX = -dirY, perpY = dirX;
            const double Reach = 10.0;  // m — safely beyond any manhole footprint

            // CCW: near-left → far-left → far-right → near-right
            return new List<double[]>
            {
                new[] { cx + perpX * halfW,                cy + perpY * halfW                },
                new[] { cx + dirX * Reach + perpX * halfW, cy + dirY * Reach + perpY * halfW },
                new[] { cx + dirX * Reach - perpX * halfW, cy + dirY * Reach - perpY * halfW },
                new[] { cx - perpX * halfW,                cy - perpY * halfW                },
            };
        }
    }
}
