using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Generates 3-D lofted Solid3d objects for each trench layer by reading
    /// the polygon coordinates stored in <see cref="CrossSectionStation"/>.
    ///
    /// One Solid3d is created per consecutive station pair (i → i+1), so each
    /// solid spans exactly one 5 m interval.  This prevents lofting from
    /// interpolating between profiles of different shapes (e.g. where an overlap
    /// zone starts or ends), which would otherwise produce unintended curves.
    ///
    /// Polygon selection per layer:
    ///   Excavation → ExcavPolyModified   (if HasOverlap and non-null) else ExcavPoly
    ///   Backfill   → BackfillPolyModified (if HasOverlap and non-null) else BackfillPoly
    ///   Bedding    → BeddingPoly
    ///   Surround   → SurroundPoly
    ///
    /// World conversion:  X = WorldX + N.x * U,  Y = WorldY + N.y * U,  Z = Z
    /// where N = unit left-normal of the pipe axis direction.
    /// </summary>
    public static class SolidBuilderService
    {
        private const string LayerExcavation = "URB_Hafriyat";
        private const string LayerBackfill   = "URB_GeriDolgu";
        private const string LayerBedding    = "URB_Yataklama";
        private const string LayerSurround   = "URB_Gomlekleme";

        private const short ColorExcavation = 30;
        private const short ColorBackfill   = 92;
        private const short ColorBedding    = 40;
        private const short ColorSurround   = 150;

        private enum LayerMode { Excavation, Backfill, Bedding, Surround }

        // ─────────────────────────────────────────────────────────────────────
        public static int Build(
            BoQReport report, Document doc, SolidsSettings cfg,
            double displayInterval = 5.0, Editor ed = null)
        {
            if (report?.SectionDebug == null || doc == null) return 0;
            if (cfg == null) cfg = new SolidsSettings();
            double interval = Math.Max(0.5, displayInterval);

            var solids = new List<(Solid3d s, string layer)>();

            TiePreference kazi  = BoQScenarioAggregator.Map(cfg.ExcavationOverlap);
            TiePreference dolgu = BoQScenarioAggregator.Map(cfg.BackfillOverlap);

            foreach (var section in report.SectionDebug)
            {
                if (cfg.DrawExcavation)
                    foreach (var s in BuildLayerSegments(section, LayerMode.Excavation, kazi, dolgu, interval))
                        solids.Add((s, LayerExcavation));

                if (cfg.DrawBackfill)
                    foreach (var s in BuildLayerSegments(section, LayerMode.Backfill, kazi, dolgu, interval))
                        solids.Add((s, LayerBackfill));

                if (cfg.DrawBedding)
                    foreach (var s in BuildLayerSegments(section, LayerMode.Bedding, kazi, dolgu, interval))
                        solids.Add((s, LayerBedding));

                if (cfg.DrawSurround)
                    foreach (var s in BuildLayerSegments(section, LayerMode.Surround, kazi, dolgu, interval))
                        solids.Add((s, LayerSurround));
            }

            int created = 0;
            using (doc.LockDocument())
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);

                foreach (var (solid, layerName) in solids)
                {
                    solid.LayerId = EnsureLayer(doc.Database, tr, layerName, LayerAci(layerName));
                    btr.AppendEntity(solid);
                    tr.AddNewlyCreatedDBObject(solid, true);
                    created++;
                }
                tr.Commit();
            }

            ed?.WriteMessage($"\n[Solids] {created} solid segment(s) created.");
            return created;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Build one extruded prism per consecutive station pair (i → i+1).
        // ─────────────────────────────────────────────────────────────────────
        private static List<Solid3d> BuildLayerSegments(
            SectionDebugRow sec, LayerMode mode, TiePreference kazi, TiePreference dolgu,
            double displayInterval)
        {
            var result = new List<Solid3d>();

            var stations = sec.Stations;
            if (stations == null || stations.Count < 2) return result;

            double dx = sec.EndX - sec.StartX, dy = sec.EndY - sec.StartY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            if (len < 1e-9) return result;
            double nx = -dy / len, ny = dx / len;
            double tx =  dx / len, ty = dy / len;

            // Crossing-boundary chainages sorted for fast range queries.
            // These act as mandatory segment breaks in addition to the user interval.
            var boundaryDists = new SortedSet<double>(
                stations.Where(s => s.IsCrossingBoundary).Select(s => s.StationDist));

            // Group stations by displayInterval, honouring crossing boundaries as hard stops:
            // From station[i], the next stop is min(current + interval, nextCrossingBoundary).
            // This ensures crossing-zone edges always land on a segment boundary, preventing
            // a single prism from straddling a crossing entry or exit point.
            int iStart = 0;
            while (iStart < stations.Count - 1)
            {
                double currentDist = stations[iStart].StationDist;
                double targetDist  = currentDist + displayInterval;

                // Override targetDist if a crossing boundary falls before the interval end.
                var view = boundaryDists.GetViewBetween(currentDist + 1e-6, targetDist - 1e-6);
                if (view.Count > 0)
                    targetDist = view.Min;

                // Find j: first index where StationDist >= targetDist, or last index.
                int j = stations.Count - 1;
                for (int k = iStart + 1; k < stations.Count; k++)
                {
                    if (stations[k].StationDist >= targetDist - 1e-6)
                    {
                        j = k;
                        break;
                    }
                }

                double segLen = stations[j].StationDist - stations[iStart].StationDist;
                if (segLen > 1e-4)
                {
                    var stA    = stations[iStart];
                    var region = SelectRegion(stA, mode, kazi, dolgu);
                    if (region != null)
                    {
                        List<Solid3d> seg;
                        if (mode == LayerMode.Surround)
                        {
                            seg = BuildSurroundSegment(region, stA, nx, ny, tx, ty, segLen);
                        }
                        else
                        {
                            seg = ExtrudeRegion(region, stA, nx, ny, tx, ty, segLen);
                            if (seg.Count == 0)
                            {
                                var fb = FallbackRegion(stA, mode);
                                if (fb != null) seg = ExtrudeRegion(fb, stA, nx, ny, tx, ty, segLen);
                            }
                        }
                        result.AddRange(seg);
                    }
                }

                iStart = j;
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Surround: full prism MINUS a 3-D pipe solid (Boolean subtract)
        // ─────────────────────────────────────────────────────────────────────
        private static List<Solid3d> BuildSurroundSegment(
            List<List<double[]>> region, CrossSectionStation st,
            double nx, double ny, double tx, double ty, double height)
        {
            var solids = new List<Solid3d>();

            // Outer surround shape: the overlap-clipped net outer ring if it builds,
            // otherwise the clean gross trapezoid (guaranteed to extrude).
            Solid3d surround = ExtrudeSingleRing(LargestRing(region), st, nx, ny, tx, ty, height)
                            ?? ExtrudeSingleRing(st.SurroundPoly, st, nx, ny, tx, ty, height);
            if (surround == null) return solids;

            // Subtract the pipe as a 3-D solid. If the boolean fails, keep the full
            // surround prism (better a complete Gömlekleme than a missing one).
            if (st.PipeBodyPoly != null && st.PipeBodyPoly.Count >= 3)
            {
                var pipe = ExtrudeSingleRing(st.PipeBodyPoly, st, nx, ny, tx, ty, height);
                if (pipe != null)
                {
                    try { surround.BooleanOperation(BooleanOperationType.BoolSubtract, pipe); }
                    catch { }
                    pipe.Dispose();   // consumed by the boolean (or discarded on failure)
                }
            }

            solids.Add(surround);
            return solids;
        }

        /// <summary>Extrudes one clean (U,Z) ring into a solid prism, or null on failure.</summary>
        private static Solid3d ExtrudeSingleRing(
            List<double[]> ring, CrossSectionStation st,
            double nx, double ny, double tx, double ty, double height)
        {
            if (ring == null || ring.Count < 3 || PolyArea2D(ring) < 1e-6) return null;

            Region reg = null;
            try
            {
                reg = MakeRegion(ring, st, nx, ny);
                if (reg == null) return null;

                double h = reg.Normal.DotProduct(new Vector3d(tx, ty, 0)) >= 0 ? height : -height;
                var solid = new Solid3d();
                try { solid.Extrude(reg, h, 0.0); return solid; }
                catch { solid.Dispose(); return null; }
            }
            finally { reg?.Dispose(); }
        }

        /// <summary>Largest-area ring of a region, or null if none.</summary>
        private static List<double[]> LargestRing(List<List<double[]>> region)
        {
            if (region == null) return null;
            List<double[]> best = null;
            double bestArea = -1;
            foreach (var r in region)
            {
                if (r == null || r.Count < 3) continue;
                double a = PolyArea2D(r);
                if (a > bestArea) { bestArea = a; best = r; }
            }
            return best;
        }

        /// <summary>
        /// Clean fallback geometry for a layer: the gross trapezoid, minus the pipe
        /// body for the surround (where the pipe sits). Built from canonical rings —
        /// not Clipper output — so Region construction never hits a degenerate
        /// self-touching boundary.
        /// </summary>
        private static List<List<double[]>> FallbackRegion(CrossSectionStation st, LayerMode mode)
        {
            List<double[]> gross;
            switch (mode)
            {
                case LayerMode.Excavation: gross = st.ExcavPoly;    break;
                case LayerMode.Backfill:   gross = st.BackfillPoly; break;
                case LayerMode.Bedding:    gross = st.BeddingPoly;  break;
                case LayerMode.Surround:   gross = st.SurroundPoly; break;
                default: return null;
            }
            if (gross == null || gross.Count < 3) return null;

            var region = new List<List<double[]>> { gross };
            if (mode == LayerMode.Surround &&
                st.PipeBodyPoly != null && st.PipeBodyPoly.Count >= 3)
                region.Add(st.PipeBodyPoly);   // pipe void (only the surround contains the pipe)
            return region;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Select the net cross-section region (U,Z) for the chosen scenario
        // ─────────────────────────────────────────────────────────────────────
        private static List<List<double[]>> SelectRegion(
            CrossSectionStation st, LayerMode mode, TiePreference kazi, TiePreference dolgu)
        {
            TiePreference   pref;
            List<double[]>  legacy;
            switch (mode)
            {
                case LayerMode.Excavation: pref = kazi;  legacy = st.ExcavPoly;    break;
                case LayerMode.Backfill:   pref = dolgu; legacy = st.BackfillPoly; break;
                case LayerMode.Bedding:    pref = dolgu; legacy = st.BeddingPoly;  break;
                case LayerMode.Surround:   pref = dolgu; legacy = st.SurroundPoly; break;
                default: return null;
            }

            // Ignore mode: skip Clipper scenario, use full gross polygon.
            if (pref == TiePreference.Ignore)
                return legacy != null && legacy.Count >= 3
                    ? new List<List<double[]>> { legacy } : null;

            TrenchLayerType layer;
            switch (mode)
            {
                case LayerMode.Excavation: layer = TrenchLayerType.Excavation; break;
                case LayerMode.Backfill:   layer = TrenchLayerType.Backfill;   break;
                case LayerMode.Bedding:    layer = TrenchLayerType.Bedding;    break;
                default:                   layer = TrenchLayerType.Surround;   break;
            }

            var prof = st.Scenario(pref);
            if (prof != null)
            {
                var poly = prof.Layer(layer).Polygon;
                if (poly != null && poly.Count > 0) return poly;
            }

            return null;
        }

        // ─────────────────────────────────────────────────────────────────────
        // REMOVED: BuildFrustumMesh (caused distortion at 4↔6 vertex transitions)
        // ─────────────────────────────────────────────────────────────────────
        private static List<Entity> BuildFrustumMesh_UNUSED(
            List<List<double[]>> regionA, List<List<double[]>> regionB,
            CrossSectionStation stA, CrossSectionStation stB,
            double nx, double ny, ref int meshOk, ref int meshFail)
        {
            var result = new List<Entity>();

            var ringA = LargestRing(regionA);
            var ringB = LargestRing(regionB);
            if (ringA == null || ringB == null) return result;
            if (PolyArea2D(ringA) < 1e-6 || PolyArea2D(ringB) < 1e-6) return result;

            List<double[]> sampA = ringA.Count < ringB.Count ? UpsampleRing(ringA, ringB) : ringA;
            List<double[]> sampB = ringB.Count < ringA.Count ? UpsampleRing(ringB, ringA) : ringB;
            if (sampA.Count != sampB.Count) { meshFail++; return result; }

            // Normalize winding to CCW so both caps face outward consistently.
            if (PolySignedArea2D(sampA) < 0) sampA = new List<double[]>(Enumerable.Reverse(sampA));
            if (PolySignedArea2D(sampB) < 0) sampB = new List<double[]>(Enumerable.Reverse(sampB));

            int n = sampA.Count;

            // Convert rings to 3D world points
            var ptsA = new Point3d[n];
            var ptsB = new Point3d[n];
            for (int i = 0; i < n; i++)
            {
                ptsA[i] = new Point3d(stA.WorldX + nx * sampA[i][0], stA.WorldY + ny * sampA[i][0], sampA[i][1]);
                ptsB[i] = new Point3d(stB.WorldX + nx * sampB[i][0], stB.WorldY + ny * sampB[i][0], sampB[i][1]);
            }

            try
            {
                // Side quads: one Face per pair of adjacent vertices
                for (int i = 0; i < n; i++)
                {
                    int j = (i + 1) % n;
                    result.Add(new Face(ptsA[i], ptsA[j], ptsB[j], ptsB[i],
                                        true, true, true, true));
                }

                // Cap A (triangulated fan, facing inward = reversed winding)
                for (int i = 1; i < n - 1; i++)
                    result.Add(new Face(ptsA[0], ptsA[i + 1], ptsA[i], ptsA[i],
                                        true, true, true, false));

                // Cap B (triangulated fan, facing outward)
                for (int i = 1; i < n - 1; i++)
                    result.Add(new Face(ptsB[0], ptsB[i], ptsB[i + 1], ptsB[i + 1],
                                        true, true, true, false));

                meshOk++;
            }
            catch { meshFail++; }

            return result;
        }

        /// <summary>
        /// Inserts vertices from ringB into ringA so both have the same count.
        /// Extra vertices in ringB are assumed to lie on edges of ringA
        /// (guaranteed when ringB is a Clipper-clipped version of ringA).
        /// </summary>
        private static List<double[]> UpsampleRing(List<double[]> ringA, List<double[]> ringB)
        {
            const double snapTol = 1e-3;
            var result = new List<double[]>(ringA);

            foreach (var vB in ringB)
            {
                // Skip if vB is already present in result.
                bool exists = false;
                foreach (var vA in result)
                    if (Math.Abs(vA[0] - vB[0]) < snapTol && Math.Abs(vA[1] - vB[1]) < snapTol)
                    { exists = true; break; }
                if (exists) continue;

                // Find the edge of result on which vB lies (minimum perpendicular distance).
                int bestEdge = -1;
                double bestDist = double.MaxValue;
                double bestT = 0;

                for (int i = 0; i < result.Count; i++)
                {
                    var a = result[i];
                    var b = result[(i + 1) % result.Count];
                    double ex = b[0] - a[0], ey = b[1] - a[1];
                    double len2 = ex * ex + ey * ey;
                    if (len2 < 1e-12) continue;

                    double t = ((vB[0] - a[0]) * ex + (vB[1] - a[1]) * ey) / len2;
                    t = Math.Max(0.0, Math.Min(1.0, t));
                    double px = a[0] + t * ex, py = a[1] + t * ey;
                    double dist = Math.Sqrt((vB[0] - px) * (vB[0] - px) +
                                           (vB[1] - py) * (vB[1] - py));
                    if (dist < bestDist) { bestDist = dist; bestEdge = i; bestT = t; }
                }

                // Insert only if the vertex genuinely lies on the edge.
                if (bestEdge >= 0 && bestDist < snapTol * 10)
                    result.Insert(bestEdge + 1, vB);
            }

            return result;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Extrude a net region (handling holes + disjoint chunks) into prisms
        // ─────────────────────────────────────────────────────────────────────
        private static List<Solid3d> ExtrudeRegion(
            List<List<double[]>> region, CrossSectionStation st,
            double nx, double ny, double tx, double ty, double height)
        {
            var solids = new List<Solid3d>();

            var rings = new List<List<double[]>>();
            foreach (var r in region)
                if (r != null && r.Count >= 3 && PolyArea2D(r) > 1e-6) rings.Add(r);
            if (rings.Count == 0) return solids;

            // Classify: a ring is a HOLE when its centroid lies inside another,
            // smaller-enclosing ring; otherwise it is an OUTER face.
            int n = rings.Count;
            var isHole = new bool[n];
            var parent = new int[n];
            for (int i = 0; i < n; i++)
            {
                parent[i] = -1;
                double[] c = Centroid(rings[i]);
                double bestArea = double.MaxValue;
                for (int j = 0; j < n; j++)
                {
                    if (j == i) continue;
                    if (ClipperGeo.PointStrictlyInside(c, rings[j]))
                    {
                        double aj = PolyArea2D(rings[j]);
                        if (aj < bestArea) { bestArea = aj; parent[i] = j; }
                    }
                }
                isHole[i] = parent[i] >= 0;
            }

            var axis = new Vector3d(tx, ty, 0);

            for (int i = 0; i < n; i++)
            {
                if (isHole[i]) continue;

                Region outer = null;
                try
                {
                    outer = MakeRegion(rings[i], st, nx, ny);
                    if (outer == null) continue;

                    for (int h = 0; h < n; h++)
                    {
                        if (!isHole[h] || parent[h] != i) continue;
                        var hole = MakeRegion(rings[h], st, nx, ny);
                        if (hole != null)
                        {
                            try { outer.BooleanOperation(BooleanOperationType.BoolSubtract, hole); }
                            catch { }
                            hole.Dispose();
                        }
                    }

                    double h2 = outer.Normal.DotProduct(axis) >= 0 ? height : -height;
                    var solid = new Solid3d();
                    try { solid.Extrude(outer, h2, 0.0); solids.Add(solid); }
                    catch { solid.Dispose(); }
                }
                catch { }
                finally { outer?.Dispose(); }
            }

            return solids;
        }

        /// <summary>Builds a planar Region from one (U,Z) ring in the cross-section plane.</summary>
        private static Region MakeRegion(
            List<double[]> ring, CrossSectionStation st, double nx, double ny)
        {
            var pts = new Point3dCollection();
            foreach (var p in ring)
                pts.Add(new Point3d(st.WorldX + nx * p[0], st.WorldY + ny * p[0], p[1]));

            Polyline3d pl = null;
            try
            {
                pl = new Polyline3d(Poly3dType.SimplePoly, pts, true);
                var curves = new DBObjectCollection { pl };
                var regions = Region.CreateFromCurves(curves);
                if (regions == null || regions.Count == 0) return null;

                var reg = regions[0] as Region;
                for (int i = 1; i < regions.Count; i++)
                    (regions[i] as DBObject)?.Dispose();
                return reg;
            }
            catch { return null; }
            finally { pl?.Dispose(); }
        }

        private static double[] Centroid(List<double[]> ring)
        {
            double sx = 0, sy = 0;
            foreach (var p in ring) { sx += p[0]; sy += p[1]; }
            return new[] { sx / ring.Count, sy / ring.Count };
        }

        // ─────────────────────────────────────────────────────────────────────
        private static double PolyArea2D(List<double[]> poly)
            => Math.Abs(PolySignedArea2D(poly));

        private static double PolySignedArea2D(List<double[]> poly)
        {
            double sum = 0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                double[] a = poly[i], b = poly[(i + 1) % n];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return sum * 0.5;
        }

        // ─────────────────────────────────────────────────────────────────────
        private static short LayerAci(string name)
        {
            switch (name)
            {
                case LayerExcavation: return ColorExcavation;
                case LayerBackfill:   return ColorBackfill;
                case LayerBedding:    return ColorBedding;
                default:              return ColorSurround;
            }
        }

        private static ObjectId EnsureLayer(Database db, Transaction tr, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
            };
            var id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
