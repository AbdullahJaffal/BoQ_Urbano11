using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;

namespace GeoHarness
{
    /// <summary>
    /// Command-line verification of ClipperGeo before the Phase 3 orchestrator is
    /// built on top of it. Asserts the geometric invariants the methodology relies
    /// on. Exit code 0 = all pass, 1 = a failure.
    /// </summary>
    internal static class Program
    {
        private static int _pass, _fail;
        private const double Tol = 1e-4;

        private static int Main()
        {
            Console.WriteLine("══ ClipperGeo verification harness ══\n");

            T1_AreaPrimitives();
            T2_BooleanAreas();
            T3_PipeBody();
            T4_Rule1CompleteInclusion();
            T5_ConservationAllScenarios();
            T6_Rule3VerticalSplit();
            T7_ResolverSinglePipe();
            T8_ResolverParallelOverlap();
            T9_ResolverPerpendicularCrossing();
            T10_SlopedSplitDistributesV();
            T11_ConnectedPipesSkipped();
            T12_CollisionStationInjection();
            T13_LowerPipeDeductedParallel();
            T14_SpatialGridEquivalence();

            Console.WriteLine($"\n── {_pass} passed, {_fail} failed ──");
            return _fail == 0 ? 0 : 1;
        }

        // =====================================================================
        // Tests
        // =====================================================================

        private static void T1_AreaPrimitives()
        {
            Section("T1 — area primitives");
            var r = Rect(0, 2, 0, 3);                     // 2 × 3
            Approx("rect area", ClipperGeo.Area(r), 6.0);
        }

        private static void T2_BooleanAreas()
        {
            Section("T2 — Difference / Intersect / Union areas");
            var a = Rect(0, 4, 0, 2);                     // area 8
            var b = Rect(2, 6, 0, 2);                     // area 8, overlap [2,4]×[0,2]=4
            Approx("A∩B", ClipperGeo.Area(ClipperGeo.Intersect(a, b)), 4.0);
            Approx("A−B", ClipperGeo.Area(ClipperGeo.Difference(a, b)), 4.0);
            Approx("A∪B", ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { a }, new List<List<double[]>> { b })), 12.0);
        }

        private static void T3_PipeBody()
        {
            Section("T3 — pipe body N-gon");
            double od = 1.0, invert = 100.0;
            var body = ClipperGeo.PipeBody(od, invert, 32);
            double r = od / 2.0;
            // 32-gon inscribed in radius r underestimates πr² slightly (~0.1%).
            ApproxRel("pipe area ≈ πr²", ClipperGeo.Area(body), Math.PI * r * r, 0.01);
            // Centre elevation = invert + r → topmost vertex near invert+2r=crown.
            double maxZ = double.MinValue;
            foreach (var v in body) if (v[1] > maxZ) maxZ = v[1];
            ApproxRel("crown ≈ invert+OD", maxZ, invert + od, 0.01);
        }

        private static void T4_Rule1CompleteInclusion()
        {
            Section("T4 — Rule 1 complete inclusion (override)");
            var outer = Rect(0, 10, 0, 10);              // area 100
            var inner = Rect(3, 5, 3, 5);                // area 4, fully inside

            Check("inner swallowed by outer", ClipperGeo.IsSwallowedBy(inner, outer));
            Check("outer NOT swallowed by inner", !ClipperGeo.IsSwallowedBy(outer, inner));

            // self = inner → kept intact regardless of preference
            // (force a preference that would otherwise make inner LOSE)
            var netInner = ClipperGeo.ResolveSameLayer(
                inner, outer, TiePreference.KeepUpper, selfIsLower: true, otherCentreU: 0);
            Approx("inner intact (override)", ClipperGeo.Area(netInner), 4.0);

            // self = outer → outer minus inner (the hole)
            var netOuter = ClipperGeo.ResolveSameLayer(
                outer, inner, TiePreference.KeepLower, selfIsLower: false, otherCentreU: 0);
            Approx("outer − inner", ClipperGeo.Area(netOuter), 96.0);
        }

        private static void T5_ConservationAllScenarios()
        {
            Section("T5 — area conservation (netA + netB == A∪B) for all 3 scenarios");
            // Two overlapping trapezoids in a shared frame.
            var A = Trap(-2, 2, 0, -3, 3, 4);            // deeper (invert z=0)
            var B = Trap( 1, 4, 1,  0, 5, 5);            // shallower (invert z=1)
            double union = ClipperGeo.Area(ClipperGeo.Union(
                new List<List<double[]>> { A }, new List<List<double[]>> { B }));

            foreach (var pref in new[] { TiePreference.KeepUpper,
                                         TiePreference.KeepLower,
                                         TiePreference.Split })
            {
                // A is the deeper (lower) pipe → selfIsLower true for A's call.
                var netA = ClipperGeo.ResolveSameLayer(A, B, pref, true,  +2.5);
                var netB = ClipperGeo.ResolveSameLayer(B, A, pref, false, -2.5);
                double sum = ClipperGeo.Area(netA) + ClipperGeo.Area(netB);
                Approx($"{pref}: netA+netB == union", sum, union);
            }
        }

        private static void T6_Rule3VerticalSplit()
        {
            Section("T6 — Rule 3 split point + vertical wall");
            // lower = wide deep trench centred at 0; upper = narrow shallow trench
            // whose lowest-left corner (1.5, 2) sits strictly inside the lower one.
            var lower = Trap(-2, 2, 0, -3, 3, 4);
            var upper = Trap(1.5, 2.5, 2, 1, 3, 4);

            double xSplit = ClipperGeo.FindSplitX(upper, lower, otherCentreU: 2.0, selfIsLower: true);
            Approx("xSplit = lowest inside upper node U", xSplit, 1.5);

            // Vertical split of the overlap must keep a clean wall at xSplit.
            var overlap = ClipperGeo.Intersect(lower, upper);
            var (left, right) = ClipperGeo.VerticalSplit(overlap, xSplit);
            double leftMaxU = MaxU(left), rightMinU = MinU(right);
            Check("left half ends at wall",  leftMaxU  <= xSplit + Tol);
            Check("right half starts at wall", rightMinU >= xSplit - Tol);
            Approx("left+right == overlap",
                ClipperGeo.Area(left) + ClipperGeo.Area(right), ClipperGeo.Area(overlap));
        }

        // =====================================================================
        // Orchestrator (BoQOverlapResolver) tests — synthetic pipes
        // =====================================================================
        // Vertical-walled trenches (SlopeRatio = 0) so every layer is a width-2
        // rectangle and areas are hand-checkable. OD = 0.5 → pipe area = πr².

        private static void T7_ResolverSinglePipe()
        {
            Section("T7 — resolver, single pipe (pipe-body rule, no neighbour)");
            var a = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);
            BoQOverlapResolver.Resolve(new List<SectionDebugRow> { a });

            var st = a.Stations[1];                      // mid station
            double rArea = Math.PI * 0.25 * 0.25;
            foreach (var p in AllPrefs())
            {
                var prof = st.Scenario(p);
                Approx($"{p}: excav == gross (no body)", prof.Excavation.NetArea, 2.0 * 3.2);
                Approx($"{p}: bedding == gross",         prof.Bedding.NetArea,    2.0 * 0.2);
                ApproxRel($"{p}: surround == gross−πr²", prof.Surround.NetArea, 1.2 - rArea, 0.01);
                Approx($"{p}: backfill == gross",        prof.Backfill.NetArea,   2.0 * 2.4);
            }
        }

        private static void T8_ResolverParallelOverlap()
        {
            Section("T8 — resolver, parallel overlapping pipes (excavation scenarios)");
            // A deeper (invert 2.0), B shallower (invert 2.5), offset 1 m in Y.
            var a = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);
            var b = MakeRow(0, 1, 10, 1, invert: 2.5, terrain: 5.0, od: 0.5);
            BoQOverlapResolver.Resolve(new List<SectionDebugRow> { a, b });

            var sa = MidStation(a);
            var sb = MidStation(b);
            double grossA = 2.0 * 3.2, grossB = 2.0 * 2.7, overlap = 1.0 * 2.7;

            // Keep Lower → A (deeper) keeps all, B loses the overlap.
            Approx("KeepLower netA", sa.Scenario(Pref("KeepLower")).Excavation.NetArea, grossA);
            Approx("KeepLower netB", sb.Scenario(Pref("KeepLower")).Excavation.NetArea, grossB - overlap);
            // Keep Upper → B (shallower) keeps all, A loses.
            Approx("KeepUpper netA", sa.Scenario(Pref("KeepUpper")).Excavation.NetArea, grossA - overlap);
            Approx("KeepUpper netB", sb.Scenario(Pref("KeepUpper")).Excavation.NetArea, grossB);
            // Split → literal Rule 3: the wall lands at the upper trench's bottom-edge
            // node (U=0 here), so for SIDE-BY-SIDE pipes the strip goes to the upper
            // pipe rather than halving. The robust invariant is conservation (no
            // double-count / no gap). [See note to user re: parallel-pipe split point.]
            double splitSum = sa.Scenario(Pref("Split")).Excavation.NetArea
                            + sb.Scenario(Pref("Split")).Excavation.NetArea;
            Approx("Split conserves (netA+netB==union)", splitSum, grossA + grossB - overlap);
        }

        private static void T9_ResolverPerpendicularCrossing()
        {
            Section("T9 — resolver, perpendicular crossing (full-width, not a sliver)");
            var a = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);   // along X, deeper
            var b = MakeRow(5, -5, 5, 5, invert: 2.5, terrain: 5.0, od: 0.5);   // along Y, crosses at x=5
            BoQOverlapResolver.Resolve(new List<SectionDebugRow> { a, b });

            var sa = StationAtX(a, 5.0);
            double grossA = 2.0 * 3.2;
            double overlap = 2.0 * 2.7;     // full A width × B's excav Z-band → NOT a sliver

            // A is deeper: KeepLower → A keeps full; KeepUpper → A loses the full-width block.
            Approx("crossing KeepLower netA", sa.Scenario(Pref("KeepLower")).Excavation.NetArea, grossA);
            Approx("crossing KeepUpper netA", sa.Scenario(Pref("KeepUpper")).Excavation.NetArea, grossA - overlap);
            Check("crossing detected (overlap flagged)", sa.HasOverlap);
        }

        private static void T10_SlopedSplitDistributesV()
        {
            Section("T10 — sloped equal-depth trenches: split slices the V (interior base node)");

            // (a) FindSplitX must pick the interior BASE node, not the midpoint.
            //     lower centred at 0, upper centred at +1.5; equal depth; bases overlap.
            //     upper's left base corner (0.5, 0) sits ON lower's base edge → it is
            //     the interior base node. Old strict test fell back to midpoint 0.75.
            var lower = Trap(-1, 1, 0, -2.6, 2.6, 3.2);
            var upper = Trap(0.5, 2.5, 0, -1.1, 4.1, 3.2);
            double xSplit = ClipperGeo.FindSplitX(upper, lower, otherCentreU: 1.5, selfIsLower: true);
            Approx("xSplit = interior base node (not midpoint)", xSplit, 0.5);

            // (b) End-to-end: two sloped equal-depth pipes, bases overlapping.
            //     Split must DISTRIBUTE the overlap — A keeps strictly between the
            //     Keep-Upper (loses all) and Keep-Lower (keeps all) extremes.
            var a = MakeRow(0, 0,   10, 0,   invert: 2.0, terrain: 5.0, od: 0.5, slope: 0.5);
            var b = MakeRow(0, 1.5, 10, 1.5, invert: 2.0, terrain: 5.0, od: 0.5, slope: 0.5);
            BoQOverlapResolver.Resolve(new List<SectionDebugRow> { a, b });

            var sa = MidStation(a);
            double keepAll  = sa.Scenario(Pref("KeepLower")).Excavation.NetArea;  // A keeps everything
            double loseAll  = sa.Scenario(Pref("KeepUpper")).Excavation.NetArea;  // A cedes whole overlap
            double splitNet = sa.Scenario(Pref("Split")).Excavation.NetArea;
            Check("Split is partial: loseAll < splitNet", splitNet > loseAll + Tol);
            Check("Split is partial: splitNet < keepAll", splitNet < keepAll - Tol);
            Check("overlap exists (loseAll < keepAll)",    loseAll < keepAll - Tol);
        }

        private static void T11_ConnectedPipesSkipped()
        {
            Section("T11 — pipes sharing a manhole are NOT clashed");
            // Same geometry as T8 (which DID deduct), but now the two pipes share a
            // topology node → the overlap must be ignored entirely.
            var a = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);
            var b = MakeRow(0, 1, 10, 1, invert: 2.5, terrain: 5.0, od: 0.5);
            a.EndNodeGuid   = "MH-7";   // A ends at manhole 7
            b.StartNodeGuid = "MH-7";   // B starts at manhole 7  → connected
            BoQOverlapResolver.Resolve(new List<SectionDebugRow> { a, b });

            var sa = MidStation(a);
            double grossA = 2.0 * 3.2;
            foreach (var p in AllPrefs())
                Approx($"{p}: connected → no deduction",
                       sa.Scenario(p).Excavation.NetArea, grossA);
            Check("no overlap flagged on connected pair", !sa.HasOverlap);
        }

        private static void T12_CollisionStationInjection()
        {
            Section("T12 — collision-station injection (footprint → chainages)");
            // A along +X (footprint Y∈[-1,1]); B along +Y crossing at x=5 (footprint X∈[4,6]).
            // Intersection footprint = X[4,6]×Y[-1,1].
            var a = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);
            var b = MakeRow(5, -5, 5, 5, invert: 2.5, terrain: 5.0, od: 0.5);
            var inj = BoQOverlapResolver.ComputeInjections(new List<SectionDebugRow> { a, b });

            // On A (chainage = x): collision spans 4..6.
            Check("A station near 4", inj.allForced[0].Any(t => Math.Abs(t - 4) < 0.05));
            Check("A station near 6", inj.allForced[0].Any(t => Math.Abs(t - 6) < 0.05));
            // On B (axis +Y from y=-5: chainage = y+5): intersection Y[-1,1] → 4..6.
            Check("B station near 4", inj.allForced[1].Any(t => Math.Abs(t - 4) < 0.05));
            Check("B station near 6", inj.allForced[1].Any(t => Math.Abs(t - 6) < 0.05));

            // Connected pipes must get NO injected stations.
            var c = MakeRow(0, 0, 10, 0, invert: 2.0, terrain: 5.0, od: 0.5);
            var d = MakeRow(5, -5, 5, 5, invert: 2.5, terrain: 5.0, od: 0.5);
            c.EndNodeGuid = "MH-9"; d.StartNodeGuid = "MH-9";
            var inj2 = BoQOverlapResolver.ComputeInjections(new List<SectionDebugRow> { c, d });
            Check("connected pair → no injection", inj2.allForced[0].Count == 0 && inj2.allForced[1].Count == 0);
        }

        private static void T13_LowerPipeDeductedParallel()
        {
            Section("T13 — lower pipe excavation deduction (parallel offset — reported bug)");
            // Deep wide LOWER centred at U=0; shallow narrow UPPER offset left (centre -1.5),
            // base higher (z=1.5). Mirrors the screenshots: they overlap on the upper-left.
            var lower = Trap(-1, 1, 0, -2.6, 2.6, 3.2);
            var upper = Trap(-2.5, -0.5, 1.5, -3.5, 0.5, 3.2);

            double ovl = ClipperGeo.Area(ClipperGeo.Intersect(lower, upper));
            Console.WriteLine($"   overlap area = {ovl:F4}");

            // Split: both should cede part.
            var cedeLowerS = ClipperGeo.SurrenderRegion(lower, upper, TiePreference.Split, true,  -1.5);
            var cedeUpperS = ClipperGeo.SurrenderRegion(upper, lower, TiePreference.Split, false, +1.5);
            Console.WriteLine($"   Split:     lower cedes {ClipperGeo.Area(cedeLowerS):F4}, upper cedes {ClipperGeo.Area(cedeUpperS):F4}");
            Check("Split: lower cedes > 0", ClipperGeo.Area(cedeLowerS) > 1e-4);
            Check("Split: upper cedes > 0", ClipperGeo.Area(cedeUpperS) > 1e-4);

            // KeepUpper: lower loses the whole overlap, upper keeps all.
            var cedeLowerKU = ClipperGeo.SurrenderRegion(lower, upper, TiePreference.KeepUpper, true,  -1.5);
            var cedeUpperKU = ClipperGeo.SurrenderRegion(upper, lower, TiePreference.KeepUpper, false, +1.5);
            Console.WriteLine($"   KeepUpper: lower cedes {ClipperGeo.Area(cedeLowerKU):F4}, upper cedes {ClipperGeo.Area(cedeUpperKU):F4}");
            Check("KeepUpper: lower cedes overlap", ClipperGeo.Area(cedeLowerKU) > 1e-4);
            Check("KeepUpper: upper cedes nothing", ClipperGeo.Area(cedeUpperKU) < 1e-4);
        }

        private static void T14_SpatialGridEquivalence()
        {
            Section("T14 — spatial grid returns identical AABB pairs to brute force");

            // (a) Random cloud: a realistic mix of overlapping and disjoint pipes.
            //     If the grid ever dropped a truly-overlapping pair, a real clash
            //     would silently vanish from the volume calc — so assert exact parity
            //     against the O(n²) scan the grid replaces.
            var rnd = new Random(12345);
            var cloud = new List<SectionDebugRow>();
            for (int i = 0; i < 300; i++)
            {
                double sx = rnd.NextDouble() * 200.0;
                double sy = rnd.NextDouble() * 200.0;
                double ang = rnd.NextDouble() * Math.PI;
                double len = 3.0 + rnd.NextDouble() * 40.0;
                cloud.Add(MakeRow(sx, sy, sx + len * Math.Cos(ang), sy + len * Math.Sin(ang),
                                  invert: 2.0, terrain: 5.0, od: 0.5));
            }
            Check("random cloud (300 pipes) ≡ brute force",
                  BoQOverlapResolver.VerifyGridEquivalence(cloud) == null);

            // (b) One long pipe spanning the whole extent crossing many short pipes:
            //     the long pipe registers in many grid cells — verify de-duplication
            //     and full coverage still match brute force exactly.
            var spanning = new List<SectionDebugRow>();
            spanning.Add(MakeRow(0, 100, 200, 100, invert: 2.0, terrain: 5.0, od: 0.5));
            for (int i = 0; i < 60; i++)
                spanning.Add(MakeRow(i * 3.0, 90, i * 3.0, 110, invert: 2.5, terrain: 5.0, od: 0.5));
            Check("long spanning pipe ≡ brute force",
                  BoQOverlapResolver.VerifyGridEquivalence(spanning) == null);

            // (c) Degenerate cluster: 40 pipes stacked at the exact same location —
            //     every pair overlaps; the grid must return the complete set.
            var cluster = new List<SectionDebugRow>();
            for (int i = 0; i < 40; i++)
                cluster.Add(MakeRow(10, 10, 20, 10, invert: 2.0, terrain: 5.0, od: 0.5));
            Check("coincident cluster ≡ brute force",
                  BoQOverlapResolver.VerifyGridEquivalence(cluster) == null);

            // (d) Fully disjoint: pipes far apart on a diagonal — zero overlapping
            //     pairs; the grid must not invent any.
            var disjoint = new List<SectionDebugRow>();
            for (int i = 0; i < 50; i++)
                disjoint.Add(MakeRow(i * 100.0, i * 100.0, i * 100.0 + 5, i * 100.0,
                                     invert: 2.0, terrain: 5.0, od: 0.5));
            Check("disjoint spread ≡ brute force",
                  BoQOverlapResolver.VerifyGridEquivalence(disjoint) == null);
        }

        // ── synthetic row/station construction ────────────────────────────────

        private static SectionDebugRow MakeRow(
            double sx, double sy, double ex, double ey, double invert, double terrain, double od,
            double slope = 0.0)
        {
            double bed = 0.2, hSurr = od + 0.1;
            double trueDepth = Math.Max(0, terrain - invert) + bed;
            var r = new SectionDebugRow
            {
                StartX = sx, StartY = sy, EndX = ex, EndY = ey,
                StartTerrainZ = terrain, EndTerrainZ = terrain,
                InvertStart = invert, InvertEnd = invert,
                PipeOuterDiamM = od,
                TrWidth = 2.0, TrBedHeight = bed, SlopeRatio = slope,
                TopWidthBed  = 2.0 + 2.0 * bed * slope,
                TopWidthSurr = 2.0 + 2.0 * bed * slope + 2.0 * hSurr * slope,
                HSurround = hSurr,
                TopWidthExcavS = 2.0 + 2.0 * trueDepth * slope,
                TopWidthExcavE = 2.0 + 2.0 * trueDepth * slope,
            };
            r.Length2D = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
            Populate(r, 5.0);
            return r;
        }

        private static void Populate(SectionDebugRow r, double interval)
        {
            r.Stations = new List<CrossSectionStation>();
            double t = 0;
            while (true)
            {
                double f = Math.Min(t / r.Length2D, 1.0);
                double x = r.StartX + (r.EndX - r.StartX) * f;
                double y = r.StartY + (r.EndY - r.StartY) * f;
                double terrainZ = r.StartTerrainZ + (r.EndTerrainZ - r.StartTerrainZ) * f;
                double invertZ  = r.InvertStart   + (r.InvertEnd   - r.InvertStart)   * f;
                double trueDepth = Math.Max(0, terrainZ - invertZ) + r.TrBedHeight;
                double hwB = r.TrWidth * 0.5, hwBed = r.TopWidthBed * 0.5, hwSurr = r.TopWidthSurr * 0.5;
                double hwEx = (r.TrWidth + 2.0 * trueDepth * r.SlopeRatio) * 0.5;
                double zBot = invertZ - r.TrBedHeight, zTop = terrainZ;
                double zSurr = Math.Min(invertZ + r.HSurround, zTop);
                r.Stations.Add(new CrossSectionStation
                {
                    StationDist = t, WorldX = x, WorldY = y,
                    TerrainZ = terrainZ, InvertZ = invertZ, TrueDepth = trueDepth,
                    ExcavPoly    = Quad(-hwB, zBot, hwB, zBot, hwEx, zTop, -hwEx, zTop),
                    BeddingPoly  = Quad(-hwB, zBot, hwB, zBot, hwBed, invertZ, -hwBed, invertZ),
                    SurroundPoly = Quad(-hwBed, invertZ, hwBed, invertZ, hwSurr, zSurr, -hwSurr, zSurr),
                    BackfillPoly = Quad(-hwSurr, zSurr, hwSurr, zSurr, hwEx, zTop, -hwEx, zTop),
                });
                if (t >= r.Length2D - 1e-9) break;
                t = Math.Min(t + interval, r.Length2D);
            }
        }

        private static List<double[]> Quad(
            double u0, double z0, double u1, double z1, double u2, double z2, double u3, double z3)
            => new List<double[]>
            { new[] { u0, z0 }, new[] { u1, z1 }, new[] { u2, z2 }, new[] { u3, z3 } };

        private static CrossSectionStation MidStation(SectionDebugRow r)
            => r.Stations[r.Stations.Count / 2];

        private static CrossSectionStation StationAtX(SectionDebugRow r, double x)
        {
            CrossSectionStation best = r.Stations[0];
            foreach (var s in r.Stations)
                if (Math.Abs(s.WorldX - x) < Math.Abs(best.WorldX - x)) best = s;
            return best;
        }

        private static IEnumerable<TiePreference> AllPrefs()
            => new[] { TiePreference.KeepUpper, TiePreference.KeepLower, TiePreference.Split };

        private static TiePreference Pref(string n)
            => n == "KeepUpper" ? TiePreference.KeepUpper
             : n == "KeepLower" ? TiePreference.KeepLower
             : TiePreference.Split;

        // =====================================================================
        // Geometry + assert helpers
        // =====================================================================

        private static List<double[]> Rect(double u0, double u1, double z0, double z1)
            => new List<double[]>
            {
                new[] { u0, z0 }, new[] { u1, z0 }, new[] { u1, z1 }, new[] { u0, z1 }
            };

        private static List<double[]> Trap(
            double baseL, double baseR, double zBot, double topL, double topR, double zTop)
            => new List<double[]>
            {
                new[] { baseL, zBot }, new[] { baseR, zBot },
                new[] { topR,  zTop }, new[] { topL,  zTop }
            };

        private static double MaxU(IEnumerable<List<double[]>> region)
        {
            double m = double.MinValue;
            foreach (var ring in region) foreach (var v in ring) if (v[0] > m) m = v[0];
            return m;
        }

        private static double MinU(IEnumerable<List<double[]>> region)
        {
            double m = double.MaxValue;
            foreach (var ring in region) foreach (var v in ring) if (v[0] < m) m = v[0];
            return m;
        }

        private static void Section(string name) => Console.WriteLine($"\n{name}");

        private static void Approx(string label, double actual, double expected)
        {
            bool ok = Math.Abs(actual - expected) <= Tol;
            Report(ok, label, $"got {actual:F6}, expected {expected:F6}");
        }

        private static void ApproxRel(string label, double actual, double expected, double rel)
        {
            bool ok = Math.Abs(actual - expected) <= Math.Abs(expected) * rel + Tol;
            Report(ok, label, $"got {actual:F6}, expected {expected:F6} (±{rel:P0})");
        }

        private static void Check(string label, bool ok) => Report(ok, label, "");

        private static void Report(bool ok, string label, string detail)
        {
            if (ok) { _pass++; Console.WriteLine($"  [PASS] {label}"); }
            else    { _fail++; Console.WriteLine($"  [FAIL] {label}  — {detail}"); }
        }
    }
}
