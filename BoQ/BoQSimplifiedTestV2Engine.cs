using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// V2 trench engine (Phases 2–9c of UT_BOQ_HESAPLA): corridors → pair zones → stations →
    /// gross profiles → crossing/parallel clash processing → volume integration → report
    /// stations. Pure geometry, no AutoCAD reference — compiled into Tests/GeoHarness so the
    /// scenario-total invariants (Σ KU = Σ KL = Σ SP) and the trench-prism reference are
    /// tested on the REAL code.
    /// </summary>
    public partial class BoQSimplifiedTestV2Command
    {
        /// <summary>Runs Phases 2–9c on report.SectionDebug in place (V2 volumes + stations).</summary>
        internal static void RunV2Engine(BoQReport report)
        {
                var rows = report.SectionDebug;
                if (rows == null || rows.Count == 0) return;

                // ── Phase 2: Max top half-width per pipe ─────────────────────────
                var maxHW = new double[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    double twS = rows[i].TrWidth + 2.0 * rows[i].TrueDepthStart * rows[i].SlopeRatio;
                    double twE = rows[i].TrWidth + 2.0 * rows[i].TrueDepthEnd   * rows[i].SlopeRatio;
                    maxHW[i]   = Math.Max(twS, twE) * 0.5;
                }

                // ── Phase 3: Oriented-corridor scan ─────────────────────────────
                var corridors = BuildCorridors(rows, maxHW);
                var pairs     = FindOverlapPairs(corridors, rows);

                // ── Phase 4: Per-pair zones + merged per-pipe zones ──────────────
                var pairZones    = ComputePairZones(corridors, pairs);
                var perPipeZones = BuildPerPipeZones(pairZones, rows.Count);

                // ── Phase 5: Adaptive station generation (initial, per pipe) ─────
                var allStations = new List<List<SimplifiedStation>>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                    allStations.Add(GenerateAdaptiveStations(rows[i], perPipeZones[i]));

                // ── Phase 6: Mirror station injection ────────────────────────────
                InjectMirrorStations(allStations, pairZones);

                // ── Phase 7: Gross profiles (interpolate geometry per station) ───
                for (int i = 0; i < rows.Count; i++)
                    BuildGrossProfiles(rows[i], allStations[i]);

                // ── Phase 8a: Bisector-frame processing for crossing pairs ───────
                // Builds scaled bisector stations and stores them in pz.BisStationsA/B.
                // Boundary stations remain OtherInvertZ=NaN (out-of-zone for IntegrateVolumes).
                ProcessBisectorPairs(rows, pairZones, allStations);

                // ── Phase 8: Pair scenario processing (parallel pairs only) ──────
                ProcessPairScenarios(rows, pairZones, allStations);

                // ── Phase 8c: Boundary station duplication (parallel pairs only) ──
                InjectBoundaryDuplicates(allStations, perPipeZones, rows, pairZones);

                // ── Phase 8e: Cross-layer clash losses (sand/pipe vs other layers) ──
                // Applied after 8c so the out-of-zone GrossClone boundary stations stay gross.
                FinalizeCrossLayerLosses(allStations);

                // ── Phase 9: Per-pipe integration (out-of-zone + parallel in-zone)
                // Bisector in-zone segments are skipped (handled in Phase 9b).
                var results = new List<SimplifiedSectionResult>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                    results.Add(IntegrateVolumes(rows[i], allStations[i]));

                // ── Phase 9b: Bisector zone independent integration ──────────────
                // Treats each crossing zone as a separate entity. Per-slice volumes
                // are computed and verified (KU=KL guaranteed structurally).
                IntegrateBisectorZones(pairZones, results);

                // ── Phase 9b.5: Transfer V2 volumes to rows for DWG storage ─────
                // After all integration phases are complete, copy final KU/KL/SP
                // totals into SectionDebugRow so DwgBoQStore can persist them in
                // V2_VOLUMES and BoQScenarioAggregator can use them directly.
                for (int i = 0; i < rows.Count; i++)
                {
                    var r = results[i];
                    rows[i].VExcavKU    = Math.Max(0, r.VExcavKU);
                    rows[i].VExcavKL    = Math.Max(0, r.VExcavKL);
                    rows[i].VExcavSP    = Math.Max(0, r.VExcavSP);
                    rows[i].VBedding    = r.VBedding;
                    rows[i].VSurround   = r.VSurround;
                    rows[i].VBeddingNet    = r.VBedding;
                    rows[i].VSurroundNet   = r.VSurround;
                    rows[i].VBeddingGross  = r.VBeddingGross;
                    rows[i].VSurroundGross = r.VSurroundGross;
                    rows[i].VBackfillKU    = Math.Max(0, r.VBackfillKU);
                    rows[i].VBackfillKL    = Math.Max(0, r.VBackfillKL);
                    rows[i].VBackfillSP    = Math.Max(0, r.VBackfillSP);
                    rows[i].VExcavGross    = r.VExcavGross;
                    rows[i].VBackfillGross = r.VBackfillGross;
                    rows[i].HasV2Volumes = true;
                }

                // ── Phase 9c: Build CrossSectionStation list from V2 data ────────
                // Replaces parser-level stations in report.SectionDebug with V2's
                // computed stations (including correct per-station KU/KL/SP scenario
                // profiles). This is what DwgBoQStore persists and VIEW/SECTIONS read.
                PopulateReportStations(report, rows, allStations, pairZones);

        }

        // =====================================================================
        // Phase 3 — Oriented-corridor helpers (OBB)
        // =====================================================================

        private struct PipeCorridor
        {
            public double StartX, StartY, EndX, EndY;
            public double Tx, Ty;
            public double Nx, Ny;
            public double Length;
            public double HalfWidth;
        }

        private static PipeCorridor[] BuildCorridors(
            List<SectionDebugRow> rows, double[] maxHW)
        {
            var result = new PipeCorridor[rows.Count];
            for (int i = 0; i < rows.Count; i++)
            {
                var r  = rows[i];
                double dx = r.EndX - r.StartX;
                double dy = r.EndY - r.StartY;
                double L  = Math.Sqrt(dx * dx + dy * dy);
                if (L < 1e-9) L = 1e-9;
                result[i] = new PipeCorridor
                {
                    StartX = r.StartX, StartY = r.StartY,
                    EndX   = r.EndX,   EndY   = r.EndY,
                    Tx = dx / L, Ty = dy / L,
                    Nx = -dy / L, Ny = dx / L,
                    Length    = L,
                    HalfWidth = maxHW[i]
                };
            }
            return result;
        }

        private static double[] CorridorCorners(PipeCorridor p)
        {
            double ox = p.Nx * p.HalfWidth, oy = p.Ny * p.HalfWidth;
            return new double[]
            {
                p.StartX + ox, p.StartY + oy,
                p.EndX   + ox, p.EndY   + oy,
                p.EndX   - ox, p.EndY   - oy,
                p.StartX - ox, p.StartY - oy
            };
        }

        private static List<double[]> CorridorRing(PipeCorridor p)
        {
            double ox = p.Nx * p.HalfWidth, oy = p.Ny * p.HalfWidth;
            return new List<double[]> {
                new[] { p.StartX + ox, p.StartY + oy },
                new[] { p.EndX   + ox, p.EndY   + oy },
                new[] { p.EndX   - ox, p.EndY   - oy },
                new[] { p.StartX - ox, p.StartY - oy }
            };
        }

        private static void ProjectOntoAxis(
            double[] corners, double ax, double ay,
            out double min, out double max)
        {
            min = double.MaxValue; max = double.MinValue;
            for (int k = 0; k < 4; k++)
            {
                double p = corners[k * 2] * ax + corners[k * 2 + 1] * ay;
                if (p < min) min = p;
                if (p > max) max = p;
            }
        }

        private static bool CorridorsOverlap(PipeCorridor a, PipeCorridor b)
        {
            double[] cA = CorridorCorners(a);
            double[] cB = CorridorCorners(b);

            double[] axes = { a.Tx, a.Ty,  a.Nx, a.Ny,  b.Tx, b.Ty,  b.Nx, b.Ny };
            for (int ai = 0; ai < 4; ai++)
            {
                double ax = axes[ai * 2], ay = axes[ai * 2 + 1];
                ProjectOntoAxis(cA, ax, ay, out double aMin, out double aMax);
                ProjectOntoAxis(cB, ax, ay, out double bMin, out double bMax);
                if (aMax <= bMin + 1e-9 || bMax <= aMin + 1e-9)
                    return false;
            }
            return true;
        }

        private static List<(int, int)> FindOverlapPairs(
            PipeCorridor[] corridors, List<SectionDebugRow> rows)
        {
            var pairs = new List<(int, int)>();
            for (int i = 0; i < corridors.Length; i++)
            for (int j = i + 1; j < corridors.Length; j++)
            {
                var a = rows[i]; var b = rows[j];

                bool sharedGuid =
                    (!string.IsNullOrEmpty(a.StartNodeGuid) &&
                     (a.StartNodeGuid == b.StartNodeGuid || a.StartNodeGuid == b.EndNodeGuid))
                  || (!string.IsNullOrEmpty(a.EndNodeGuid) &&
                     (a.EndNodeGuid == b.StartNodeGuid || a.EndNodeGuid == b.EndNodeGuid));

                if (!sharedGuid && CorridorsOverlap(corridors[i], corridors[j]))
                    pairs.Add((i, j));
            }
            return pairs;
        }

        // =====================================================================
        // Phase 4 — Zone computation
        // =====================================================================

        private sealed class PairZone
        {
            public int    PipeA, PipeB;
            public double EnterA, ExitA;
            public double EnterB, ExitB;
            // Crossing angle in XY plan: 0° = parallel, 90° = perpendicular
            public double AngleXYDeg;
            // true when AngleXYDeg >= AnglePairThresholdDeg (and <= 45°)
            public bool   UseBisectorFrame;
            public double TanHalfAngle;
            // Paired bisector stations in zone order — populated by ProcessBisectorPairs.
            // BisStationsA[k] and BisStationsB[k] share the same bisector world position.
            public readonly List<SimplifiedStation> BisStationsA = new List<SimplifiedStation>();
            public readonly List<SimplifiedStation> BisStationsB = new List<SimplifiedStation>();
        }

        private static List<PairZone> ComputePairZones(
            PipeCorridor[]   corridors,
            List<(int, int)> pairs)
        {
            var result = new List<PairZone>();
            foreach (var (ai, bi) in pairs)
            {
                var cA = corridors[ai];
                var cB = corridors[bi];

                // Intersect the two corridor rectangles via Clipper to get the actual
                // overlap region. Projecting B's full corridor corners onto A's axis
                // (the previous approach) grossly over-estimated the zone for crossing
                // pipes because the full pipe length contributes to the projection range.
                var interPoly = ClipperGeo.Intersect(CorridorRing(cA), CorridorRing(cB));
                if (interPoly == null || interPoly.Count == 0) continue;

                // Project the intersection polygon's vertices onto each pipe's own axis.
                double enterA = double.MaxValue, exitA = double.MinValue;
                double enterB = double.MaxValue, exitB = double.MinValue;
                foreach (var ring in interPoly)
                {
                    foreach (var v in ring)
                    {
                        double tA = (v[0] - cA.StartX) * cA.Tx + (v[1] - cA.StartY) * cA.Ty;
                        double tB = (v[0] - cB.StartX) * cB.Tx + (v[1] - cB.StartY) * cB.Ty;
                        if (tA < enterA) enterA = tA;
                        if (tA > exitA)  exitA  = tA;
                        if (tB < enterB) enterB = tB;
                        if (tB > exitB)  exitB  = tB;
                    }
                }
                enterA = Math.Round(Math.Max(0,         enterA), 3);
                exitA  = Math.Round(Math.Min(cA.Length, exitA),  3);
                enterB = Math.Round(Math.Max(0,         enterB), 3);
                exitB  = Math.Round(Math.Min(cB.Length, exitB),  3);

                if (exitA > enterA + 1e-6 && exitB > enterB + 1e-6)
                {
                    // Crossing angle: arccos(|dot|) → 0°=parallel, 90°=perpendicular
                    double dotAB        = cA.Tx * cB.Tx + cA.Ty * cB.Ty;
                    double angleXY      = Math.Acos(Math.Min(1.0, Math.Abs(dotAB))) * 180.0 / Math.PI;
                    double angleClamped = Math.Max(0.0, Math.Min(90.0, angleXY));
                    bool   useBisector  = angleClamped >= AnglePairThresholdDeg;
                    double halfRad      = angleClamped * 0.5 * Math.PI / 180.0;

                    result.Add(new PairZone
                    {
                        PipeA  = ai,      PipeB  = bi,
                        EnterA = enterA,  ExitA  = exitA,
                        EnterB = enterB,  ExitB  = exitB,
                        AngleXYDeg       = angleClamped,
                        UseBisectorFrame = useBisector,
                        TanHalfAngle     = Math.Tan(halfRad)
                    });
                }
            }
            return result;
        }

        private static List<List<(double, double)>> BuildPerPipeZones(
            List<PairZone> pairZones, int pipeCount)
        {
            var raw = Enumerable.Range(0, pipeCount)
                .Select(_ => new List<(double, double)>()).ToList();

            foreach (var pz in pairZones)
            {
                raw[pz.PipeA].Add((pz.EnterA, pz.ExitA));
                raw[pz.PipeB].Add((pz.EnterB, pz.ExitB));
            }

            return raw.Select(z => MergeIntervals(z)).ToList();
        }

        private static List<(double, double)> MergeIntervals(List<(double, double)> raw)
        {
            var valid  = raw.Where(iv => iv.Item2 > iv.Item1 + 1e-6)
                            .OrderBy(iv => iv.Item1).ToList();
            var merged = new List<(double, double)>();
            foreach (var iv in valid)
            {
                if (merged.Count > 0 && iv.Item1 <= merged[merged.Count - 1].Item2 + 1e-6)
                    merged[merged.Count - 1] = (
                        merged[merged.Count - 1].Item1,
                        Math.Max(merged[merged.Count - 1].Item2, iv.Item2));
                else
                    merged.Add(iv);
            }
            return merged;
        }

        // =====================================================================
        // Phase 5 — Adaptive station generation
        // =====================================================================

        private const double IndependentInterval    = 5.0;
        private const double IntersectionInterval  = 0.5;
        // Pipes whose XY crossing angle is >= this threshold use the bisector-frame
        // wedge correction. Below threshold they are treated as parallel.
        // Floor = 3.6° (1/cos(1.8°) ≈ 1.000 at 3 dp); no upper ceiling.
        private const double AnglePairThresholdDeg = 3.6;

        private static List<SimplifiedStation> GenerateAdaptiveStations(
            SectionDebugRow               row,
            List<(double Enter, double Exit)> zones)
        {
            double L = row.Length2D;
            if (L < 1e-9) return new List<SimplifiedStation>();

            var distSet = new SortedSet<double>();
            distSet.Add(0.0);
            distSet.Add(L);

            foreach (var (enter, exit) in zones)
            {
                distSet.Add(Math.Max(0, Math.Min(L, enter)));
                distSet.Add(Math.Max(0, Math.Min(L, exit)));
            }

            // Outside-zone: multiples of IndependentInterval that land outside every zone.
            // Ensures clean 5 m grid regardless of where zone transitions occur.
            long nIndMax = (long)Math.Ceiling(L / IndependentInterval);
            for (long n = 0; n <= nIndMax; n++)
            {
                double d = n * IndependentInterval;
                if (d > L) d = L;
                if (!InZone(d, zones)) distSet.Add(d);
            }

            // Inside-zone: multiples of IntersectionInterval within each zone.
            // Mirror-injected stations are additional extras; the grid is independent.
            foreach (var (enter, exit) in zones)
            {
                long nFirst = (long)Math.Ceiling(enter / IntersectionInterval - 1e-9);
                long nLast  = (long)Math.Floor(exit  / IntersectionInterval + 1e-9);
                for (long n = Math.Max(0L, nFirst); n <= nLast; n++)
                {
                    double d = n * IntersectionInterval;
                    if (d >= enter - 1e-9 && d <= exit + 1e-9)
                        distSet.Add(Math.Max(0, Math.Min(L, d)));
                }
            }

            var result = new List<SimplifiedStation>();
            double prev = double.NegativeInfinity;
            foreach (double d in distSet)
            {
                double clamped = Math.Max(0.0, Math.Min(L, d));
                if (clamped - prev < 1e-3) continue;
                prev = clamped;
                result.Add(new SimplifiedStation { StationDist = clamped });
            }
            return result;
        }

        private static bool InZone(double d, List<(double Enter, double Exit)> zones)
        {
            foreach (var (en, ex) in zones)
                if (d >= en - 1e-9 && d < ex - 1e-9) return true;
            return false;
        }

        // =====================================================================
        // Phase 6 — Mirror station injection
        // =====================================================================

        private static void InjectMirrorStations(
            List<List<SimplifiedStation>> allStations,
            List<PairZone>                pairZones)
        {
            foreach (var pz in pairZones)
            {
                if (pz.UseBisectorFrame) continue; // bisector pairs handle their own stations

                var listA = allStations[pz.PipeA];
                var listB = allStations[pz.PipeB];

                double zoneLenA  = pz.ExitA - pz.EnterA;
                double zoneLenB  = pz.ExitB - pz.EnterB;
                double maxOffset = Math.Min(zoneLenA, zoneLenB);

                var offsets = new SortedSet<double>();

                foreach (var s in listA)
                {
                    double off = s.StationDist - pz.EnterA;
                    if (off >= -1e-6 && off <= maxOffset + 1e-6)
                        offsets.Add(Math.Max(0, Math.Min(maxOffset, off)));
                }
                foreach (var s in listB)
                {
                    double off = s.StationDist - pz.EnterB;
                    if (off >= -1e-6 && off <= maxOffset + 1e-6)
                        offsets.Add(Math.Max(0, Math.Min(maxOffset, off)));
                }

                foreach (double off in offsets)
                {
                    double distA = Math.Min(pz.ExitA, pz.EnterA + off);
                    if (!listA.Any(s => Math.Abs(s.StationDist - distA) < 1e-4))
                        listA.Add(new SimplifiedStation { StationDist = distA });

                    double distB = Math.Min(pz.ExitB, pz.EnterB + off);
                    if (!listB.Any(s => Math.Abs(s.StationDist - distB) < 1e-4))
                        listB.Add(new SimplifiedStation { StationDist = distB });
                }

                listA.Sort((x, y) => x.StationDist.CompareTo(y.StationDist));
                listB.Sort((x, y) => x.StationDist.CompareTo(y.StationDist));
            }
        }

        // =====================================================================
        // Phase 7 — Gross 2D trapezoidal cross-sections
        // =====================================================================

        private static void BuildGrossProfiles(
            SectionDebugRow         row,
            List<SimplifiedStation> stations)
        {
            double L = row.Length2D;
            if (L < 1e-9) return;

            double hwBase = row.TrWidth      * 0.5;

            foreach (var st in stations)
            {
                if (st.ExcavPoly != null) continue;

                double f    = Math.Min(st.StationDist / L, 1.0);
                st.WorldX   = row.StartX        + (row.EndX        - row.StartX)        * f;
                st.WorldY   = row.StartY        + (row.EndY        - row.StartY)        * f;
                st.TerrainZ = row.StartTerrainZ + (row.EndTerrainZ - row.StartTerrainZ) * f;   // ZKazi
                st.DolguZ   = row.StartDolguZ   + (row.EndDolguZ   - row.StartDolguZ)   * f;   // ZDolgu
                st.InvertZ  = row.InvertStart   + (row.InvertEnd   - row.InvertStart)   * f;

                double pipeWall     = (row.PipeOuterDiamM - row.DiameterMm / 1000.0) / 2.0;
                double outerPipeBtm = st.InvertZ - pipeWall;

                double zBot     = outerPipeBtm - row.TrBedHeight;
                double zTop     = st.TerrainZ;
                double trueDepth  = Math.Max(0, zTop - zBot);
                double topWExcav  = row.TrWidth + 2.0 * trueDepth * row.SlopeRatio;
                double hwExcav    = topWExcav * 0.5;
                // Sand zones clipped at the excavation top WITH the trench's own width at
                // the clipped level (2026-10-18). Before, a shallow station (ground below
                // the sand top) kept the full-height TopWidthBed/TopWidthSurr at the lower
                // clipped level → the sand polygon stuck out of the trench and Σfill
                // exceeded Σkazı (UT_BOQ_PIPE_DIAG "dejenere", −9.824 m³ on 45 stations).
                double zBedTop    = Math.Max(zBot, Math.Min(outerPipeBtm, zTop));
                double zSurrTop   = Math.Max(zBedTop, Math.Min(outerPipeBtm + row.HSurround, zTop));
                double hwBedTop   = (row.TrWidth + 2.0 * (zBedTop  - zBot) * row.SlopeRatio) * 0.5;
                double hwSurrTop  = (row.TrWidth + 2.0 * (zSurrTop - zBot) * row.SlopeRatio) * 0.5;

                // Backfill's own top reference (ZDolgu), independent of the excavation
                // top (ZKazi) — same trapezoid formula re-run with the new level (user
                // directive 2026-07-06), not sliced off the excavation trapezoid.
                bool   dolguInvalid   = st.DolguZ < st.InvertZ;
                double trueDepthDolgu = dolguInvalid ? 0 : Math.Max(0, st.DolguZ - zBot);
                double topWDolgu      = row.TrWidth + 2.0 * trueDepthDolgu * row.SlopeRatio;
                double hwDolgu        = topWDolgu * 0.5;
                double zTopDolgu      = dolguInvalid ? zBot : st.DolguZ;
                bool   backfillDegenerate = dolguInvalid || zTopDolgu <= zSurrTop;

                st.TrueDepth      = trueDepth;
                st.HwExcav        = hwExcav;
                st.TrueDepthDolgu = trueDepthDolgu;
                st.DolguInvalid   = dolguInvalid;

                // Top clamped to the trench bottom: where the ground is BELOW the trench
                // bottom (pipe above natural ground) the polygon used to flip upside down and
                // its |area| counted as excavation (UT_BOQ_PIPE_DIAG, A21→A22, 2026-10-18).
                double zTopEx = zBot + trueDepth;
                st.ExcavPoly = new List<double[]>
                {
                    new[] { -hwBase,  zBot   }, new[] {  hwBase,  zBot   },
                    new[] {  hwExcav, zTopEx }, new[] { -hwExcav, zTopEx }
                };
                st.BeddingPoly = new List<double[]>
                {
                    new[] { -hwBase,   zBot    }, new[] {  hwBase,   zBot    },
                    new[] {  hwBedTop, zBedTop }, new[] { -hwBedTop, zBedTop }
                };
                st.SurroundPoly = new List<double[]>
                {
                    new[] { -hwBedTop,  zBedTop  }, new[] {  hwBedTop,  zBedTop  },
                    new[] {  hwSurrTop, zSurrTop }, new[] { -hwSurrTop, zSurrTop }
                };
                st.BackfillPoly = backfillDegenerate ? new List<double[]>() : new List<double[]>
                {
                    new[] { -hwSurrTop, zSurrTop  }, new[] {  hwSurrTop, zSurrTop  },
                    new[] {  hwDolgu,   zTopDolgu }, new[] { -hwDolgu,   zTopDolgu }
                };

                st.PipePoly = PipePolyAt(row.PipeOuterDiamM, outerPipeBtm, 1.0);
                st.AxisCut  = st.StationDist;
                st.CosHalf  = 1.0;

                st.AreaExcav    = Math.Round(ClipperGeo.Area(st.ExcavPoly),    3);
                st.AreaBedding  = Math.Round(ClipperGeo.Area(st.BeddingPoly),  3);
                st.AreaSurround = Math.Round(Math.Max(0, ClipperGeo.Area(st.SurroundPoly) - row.PipeArea), 3);
                st.AreaBackfill = backfillDegenerate ? 0.0 : Math.Round(ClipperGeo.Area(st.BackfillPoly), 3);

                st.AreaExcavDeducted    = st.AreaExcav;
                st.AreaExcavDeductedKL  = st.AreaExcav;
                st.AreaExcavSP          = st.AreaExcav;
                st.AreaBackfillDeducted    = st.AreaBackfill;
                st.AreaBackfillDeductedKL  = st.AreaBackfill;
                st.AreaBackfillSP          = st.AreaBackfill;
            }
        }

        // =====================================================================
        // Phase 8a — Bisector-frame processing for crossing pipes
        //
        // For pairs where UseBisectorFrame=true (angle >= AnglePairThresholdDeg),
        // replaces the individual per-pipe in-zone stations with stations that lie
        // on the bisector of the two pipe directions.  Both A and B share the same
        // world position at each bisector station, so IntegrateVolumes uses the
        // same dXY step for both → KU = KL = SP is guaranteed by symmetry.
        //
        // Cross-section half-widths are scaled by cos(θ/2) (bisector projection).
        // Overlap, deductions, and SP split use the same polygon logic as the
        // parallel-pipe path.
        // =====================================================================

        private static void ProcessBisectorPairs(
            List<SectionDebugRow>         rows,
            List<PairZone>                pairZones,
            List<List<SimplifiedStation>> allStations)
        {
            foreach (var pz in pairZones)
            {
                if (!pz.UseBisectorFrame) continue;

                var rowA  = rows[pz.PipeA];  var rowB  = rows[pz.PipeB];
                var listA = allStations[pz.PipeA]; var listB = allStations[pz.PipeB];

                double L_A = rowA.Length2D, L_B = rowB.Length2D;
                if (L_A < 1e-9 || L_B < 1e-9) continue;

                double txA = (rowA.EndX - rowA.StartX) / L_A, tyA = (rowA.EndY - rowA.StartY) / L_A;
                double txB = (rowB.EndX - rowB.StartX) / L_B, tyB = (rowB.EndY - rowB.StartY) / L_B;

                // Bisector unit vector: sum of A and B unit vectors (flip B if obtuse)
                double dotAB  = txA * txB + tyA * tyB;
                double bRawX  = dotAB >= 0 ? txA + txB : txA - txB;
                double bRawY  = dotAB >= 0 ? tyA + tyB : tyA - tyB;
                double bLen   = Math.Sqrt(bRawX * bRawX + bRawY * bRawY);
                if (bLen < 1e-9) continue;
                double bTx = bRawX / bLen, bTy = bRawY / bLen;

                double cosHalf = Math.Cos(pz.AngleXYDeg * 0.5 * Math.PI / 180.0);

                // Crossing point of the two pipe axes (2×2 parametric solve)
                double dx0    = rowB.StartX - rowA.StartX, dy0 = rowB.StartY - rowA.StartY;
                double denom  = txA * tyB - tyA * txB;
                if (Math.Abs(denom) < 1e-9) continue;
                double tA_cr  = (dx0 * tyB - dy0 * txB) / denom;
                double originX = rowA.StartX + txA * tA_cr;
                double originY = rowA.StartY + tyA * tA_cr;

                // Project all 4 zone-boundary world points onto bisector to find extent
                double[] ptx = {
                    rowA.StartX + txA * pz.EnterA, rowA.StartX + txA * pz.ExitA,
                    rowB.StartX + txB * pz.EnterB, rowB.StartX + txB * pz.ExitB };
                double[] pty = {
                    rowA.StartY + tyA * pz.EnterA, rowA.StartY + tyA * pz.ExitA,
                    rowB.StartY + tyB * pz.EnterB, rowB.StartY + tyB * pz.ExitB };
                double bisMin = double.MaxValue, bisMax = double.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    double p = (ptx[k] - originX) * bTx + (pty[k] - originY) * bTy;
                    if (p < bisMin) bisMin = p;
                    if (p > bisMax) bisMax = p;
                }

                long nFirst = (long)Math.Ceiling(bisMin / IntersectionInterval - 1e-9);
                long nLast  = (long)Math.Floor(bisMax  / IntersectionInterval + 1e-9);

                var bStationsA = new List<SimplifiedStation>();
                var bStationsB = new List<SimplifiedStation>();

                for (long n = nFirst; n <= nLast; n++)
                {
                    double bDist = n * IntersectionInterval;
                    double wx    = Math.Round(originX + bTx * bDist, 3);
                    double wy    = Math.Round(originY + bTy * bDist, 3);

                    double rawA = (wx - rowA.StartX) * txA + (wy - rowA.StartY) * tyA;
                    double rawB = (wx - rowB.StartX) * txB + (wy - rowB.StartY) * tyB;
                    double tA   = Math.Round(Math.Max(0, Math.Min(L_A, rawA)), 3);
                    double tB   = Math.Round(Math.Max(0, Math.Min(L_B, rawB)), 3);

                    // Skip if either pipe's unclamped projection is outside its zone
                    if (rawA < pz.EnterA || rawA > pz.ExitA) continue;
                    if (rawB < pz.EnterB || rawB > pz.ExitB) continue;
                    // Skip at zone boundary — transition segment is out-of-zone (gross)
                    if (Math.Abs(tA - pz.EnterA) < 1e-4 || Math.Abs(tA - pz.ExitA) < 1e-4) continue;
                    if (Math.Abs(tB - pz.EnterB) < 1e-4 || Math.Abs(tB - pz.ExitB) < 1e-4) continue;

                    var sA = BuildBisectorStation(rowA, tA, cosHalf, wx, wy, bTx, bTy);
                    var sB = BuildBisectorStation(rowB, tB, cosHalf, wx, wy, bTx, bTy);

                    sA.BisectorDist = n * IntersectionInterval;
                    sB.BisectorDist = n * IntersectionInterval;

                    // Lateral offset of B's centerline from A's, in bisector-normal direction
                    double aCx = rowA.StartX + txA * tA, aCy = rowA.StartY + tyA * tA;
                    double bCx = rowB.StartX + txB * tB, bCy = rowB.StartY + tyB * tB;
                    double uBc = (bCx - aCx) * (-bTy) + (bCy - aCy) * bTx;

                    // ── Excavation ───────────────────────────────────────────────
                    var aExcav = RoundPolyTo1mm(sA.ExcavPoly);
                    var bExcav = RoundPolyTo1mm(TranslatePoly(sB.ExcavPoly, uBc));

                    double aGrossE   = sA.AreaExcav, bGrossE = sB.AreaExcav;
                    double combinedE = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aExcav }, new List<List<double[]>> { bExcav }));
                    double overlapE  = Math.Max(0.0, aGrossE + bGrossE - combinedE);

                    if (overlapE > 1e-6)
                    {
                        var overlapPolyE = ClipperGeo.Intersect(aExcav, bExcav);

                        sA.ExcavDeductPoly     = overlapPolyE;
                        sA.AreaExcavDeducted   = Math.Max(0, aGrossE - ClipperGeo.Area(overlapPolyE));
                        sA.AreaExcavDeductedKL = Math.Max(0, aGrossE - overlapE);

                        var overlapPolyE_B = TranslatePolyList(overlapPolyE, -uBc);
                        sB.ExcavDeductPoly     = overlapPolyE_B;
                        sB.AreaExcavDeducted   = Math.Max(0, bGrossE - ClipperGeo.Area(overlapPolyE_B));
                        sB.AreaExcavDeductedKL = Math.Max(0, bGrossE - overlapE);

                        bool   aIsUpper  = sA.InvertZ >= sB.InvertZ;
                        double upperHW   = aIsUpper ? -sA.ExcavPoly[0][0] : -sB.ExcavPoly[0][0];
                        double upperCtrU = aIsUpper ? 0.0 : uBc;
                        double lowerCtrU = aIsUpper ? uBc : 0.0;
                        bool   lowerRight = lowerCtrU > upperCtrU;
                        double splitU    = lowerRight ? upperCtrU + upperHW : upperCtrU - upperHW;

                        const double LARGE = 1000.0;
                        List<double[]> upperSideRect, lowerSideRect;
                        if (lowerRight)
                        {
                            upperSideRect = new List<double[]> {
                                new[] {-LARGE,-LARGE}, new[] {splitU,-LARGE},
                                new[] {splitU,+LARGE}, new[] {-LARGE,+LARGE} };
                            lowerSideRect = new List<double[]> {
                                new[] {splitU,-LARGE}, new[] {+LARGE,-LARGE},
                                new[] {+LARGE,+LARGE}, new[] {splitU,+LARGE} };
                        }
                        else
                        {
                            upperSideRect = new List<double[]> {
                                new[] {splitU,-LARGE}, new[] {+LARGE,-LARGE},
                                new[] {+LARGE,+LARGE}, new[] {splitU,+LARGE} };
                            lowerSideRect = new List<double[]> {
                                new[] {-LARGE,-LARGE}, new[] {splitU,-LARGE},
                                new[] {splitU,+LARGE}, new[] {-LARGE,+LARGE} };
                        }

                        var aLosePolyE = ClipperGeo.Intersect(overlapPolyE,
                            new List<List<double[]>> { aIsUpper ? lowerSideRect : upperSideRect });
                        sA.ExcavSPLosePoly = aLosePolyE;
                        sA.AreaExcavSP     = Math.Max(0, aGrossE - ClipperGeo.Area(aLosePolyE));

                        var bLosePolyE_inA = ClipperGeo.Intersect(overlapPolyE,
                            new List<List<double[]>> { aIsUpper ? upperSideRect : lowerSideRect });
                        sB.ExcavSPLosePoly = TranslatePolyList(bLosePolyE_inA, -uBc);
                        sB.AreaExcavSP     = Math.Max(0, bGrossE - ClipperGeo.Area(sB.ExcavSPLosePoly));
                    }

                    // ── Backfill ─────────────────────────────────────────────────
                    var aBf = RoundPolyTo1mm(sA.BackfillPoly);
                    var bBf = RoundPolyTo1mm(TranslatePoly(sB.BackfillPoly, uBc));

                    double aGrossB   = sA.AreaBackfill, bGrossB = sB.AreaBackfill;
                    double combinedB = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aBf }, new List<List<double[]>> { bBf }));
                    double overlapB  = Math.Max(0.0, aGrossB + bGrossB - combinedB);

                    if (overlapB > 1e-6)
                    {
                        var overlapPolyB = ClipperGeo.Intersect(aBf, bBf);

                        sA.BackfillDeductPoly      = overlapPolyB;
                        sA.AreaBackfillDeducted    = Math.Max(0, aGrossB - ClipperGeo.Area(overlapPolyB));
                        sA.AreaBackfillDeductedKL  = Math.Max(0, aGrossB - overlapB);

                        var overlapPolyB_B = TranslatePolyList(overlapPolyB, -uBc);
                        sB.BackfillDeductPoly      = overlapPolyB_B;
                        sB.AreaBackfillDeducted    = Math.Max(0, bGrossB - ClipperGeo.Area(overlapPolyB_B));
                        sB.AreaBackfillDeductedKL  = Math.Max(0, bGrossB - overlapB);

                        bool   aIsUpperB  = sA.InvertZ >= sB.InvertZ;
                        double upperHWB   = aIsUpperB ? -sA.ExcavPoly[0][0] : -sB.ExcavPoly[0][0];
                        double upperCtrUB = aIsUpperB ? 0.0 : uBc;
                        double lowerCtrUB = aIsUpperB ? uBc : 0.0;
                        bool   lowerRightB = lowerCtrUB > upperCtrUB;
                        double splitUB    = lowerRightB ? upperCtrUB + upperHWB : upperCtrUB - upperHWB;

                        const double LARGEB = 1000.0;
                        List<double[]> upperSideRectB, lowerSideRectB;
                        if (lowerRightB)
                        {
                            upperSideRectB = new List<double[]> {
                                new[] {-LARGEB,-LARGEB}, new[] {splitUB,-LARGEB},
                                new[] {splitUB,+LARGEB}, new[] {-LARGEB,+LARGEB} };
                            lowerSideRectB = new List<double[]> {
                                new[] {splitUB,-LARGEB}, new[] {+LARGEB,-LARGEB},
                                new[] {+LARGEB,+LARGEB}, new[] {splitUB,+LARGEB} };
                        }
                        else
                        {
                            upperSideRectB = new List<double[]> {
                                new[] {splitUB,-LARGEB}, new[] {+LARGEB,-LARGEB},
                                new[] {+LARGEB,+LARGEB}, new[] {splitUB,+LARGEB} };
                            lowerSideRectB = new List<double[]> {
                                new[] {-LARGEB,-LARGEB}, new[] {splitUB,-LARGEB},
                                new[] {splitUB,+LARGEB}, new[] {-LARGEB,+LARGEB} };
                        }

                        var aLosePolyB = ClipperGeo.Intersect(overlapPolyB,
                            new List<List<double[]>> { aIsUpperB ? lowerSideRectB : upperSideRectB });
                        sA.BackfillSPLosePoly = aLosePolyB;
                        sA.AreaBackfillSP     = Math.Max(0, aGrossB - ClipperGeo.Area(aLosePolyB));

                        var bLosePolyB_inA = ClipperGeo.Intersect(overlapPolyB,
                            new List<List<double[]>> { aIsUpperB ? upperSideRectB : lowerSideRectB });
                        sB.BackfillSPLosePoly = TranslatePolyList(bLosePolyB_inA, -uBc);
                        sB.AreaBackfillSP     = Math.Max(0, bGrossB - ClipperGeo.Area(sB.BackfillSPLosePoly));
                    }

                    ApplyCrossLayerLosses(sA, sB, uBc);

                    sA.OtherInvertZ = sB.InvertZ;
                    sB.OtherInvertZ = sA.InvertZ;

                    bStationsA.Add(sA);
                    bStationsB.Add(sB);
                }

                // Replace in-zone stations (strictly interior) with bisector stations
                listA.RemoveAll(s => s.StationDist > pz.EnterA + 1e-4 && s.StationDist < pz.ExitA - 1e-4);
                listB.RemoveAll(s => s.StationDist > pz.EnterB + 1e-4 && s.StationDist < pz.ExitB - 1e-4);
                listA.AddRange(bStationsA); listA.Sort((x, y) => x.StationDist.CompareTo(y.StationDist));
                listB.AddRange(bStationsB); listB.Sort((x, y) => x.StationDist.CompareTo(y.StationDist));

                // Store paired station lists in the zone for IntegrateBisectorZones
                pz.BisStationsA.Clear(); pz.BisStationsA.AddRange(bStationsA);
                pz.BisStationsB.Clear(); pz.BisStationsB.AddRange(bStationsB);
            }
        }

        // Build a SimplifiedStation for use inside a bisector-frame zone.
        // Half-widths are scaled by cosHalf = cos(θ/2); world position is the
        // bisector point (wx, wy) so IntegrateVolumes uses bisector path length.
        private static SimplifiedStation BuildBisectorStation(
            SectionDebugRow row, double t, double cosHalf, double wx, double wy, double bTx, double bTy)
        {
            double L = row.Length2D;
            double f = L > 1e-9 ? Math.Min(t / L, 1.0) : 0.0;

            double terrainZ     = row.StartTerrainZ + (row.EndTerrainZ - row.StartTerrainZ) * f;   // ZKazi
            double dolguZ       = row.StartDolguZ   + (row.EndDolguZ   - row.StartDolguZ)   * f;   // ZDolgu
            double invertZ      = row.InvertStart   + (row.InvertEnd   - row.InvertStart)   * f;
            double pipeWall     = (row.PipeOuterDiamM - row.DiameterMm / 1000.0) / 2.0;
            double outerPipeBtm = invertZ - pipeWall;
            double zBot         = outerPipeBtm - row.TrBedHeight;
            double zTop         = terrainZ;
            double trueDepth    = Math.Max(0, zTop - zBot);

            double hwBase  = row.TrWidth      * 0.5 / cosHalf;
            double hwExcav = (row.TrWidth + 2.0 * trueDepth * row.SlopeRatio) * 0.5 / cosHalf;
            // Sand zones clipped at the excavation top with the trench width at that level
            // (mirrors BuildGrossProfiles, 2026-10-18).
            double zBedTop   = Math.Max(zBot, Math.Min(outerPipeBtm, zTop));
            double zSurrTop  = Math.Max(zBedTop, Math.Min(outerPipeBtm + row.HSurround, zTop));
            double hwBedTop  = (row.TrWidth + 2.0 * (zBedTop  - zBot) * row.SlopeRatio) * 0.5 / cosHalf;
            double hwSurrTop = (row.TrWidth + 2.0 * (zSurrTop - zBot) * row.SlopeRatio) * 0.5 / cosHalf;

            // Backfill's own top reference (ZDolgu), independent of the excavation
            // top (ZKazi) — mirrors BuildGrossProfiles.
            bool   dolguInvalid   = dolguZ < invertZ;
            double trueDepthDolgu = dolguInvalid ? 0 : Math.Max(0, dolguZ - zBot);
            double hwDolgu        = (row.TrWidth + 2.0 * trueDepthDolgu * row.SlopeRatio) * 0.5 / cosHalf;
            double zTopDolgu      = dolguInvalid ? zBot : dolguZ;
            bool   backfillDegenerate = dolguInvalid || zTopDolgu <= zSurrTop;

            var st = new SimplifiedStation
            {
                StationDist    = t,
                WorldX         = wx,
                WorldY         = wy,
                TerrainZ       = terrainZ,
                DolguZ         = dolguZ,
                InvertZ        = invertZ,
                TrueDepth      = trueDepth,
                TrueDepthDolgu = trueDepthDolgu,
                DolguInvalid   = dolguInvalid,
                HwExcav        = hwExcav
            };

            double zTopEx = zBot + trueDepth;   // clamped: no inverted polygon (mirrors BuildGrossProfiles)
            st.ExcavPoly = new List<double[]> {
                new[] {-hwBase, zBot}, new[] {hwBase, zBot},
                new[] {hwExcav, zTopEx}, new[] {-hwExcav, zTopEx} };
            st.BeddingPoly = new List<double[]> {
                new[] {-hwBase, zBot}, new[] {hwBase, zBot},
                new[] {hwBedTop, zBedTop}, new[] {-hwBedTop, zBedTop} };
            st.SurroundPoly = new List<double[]> {
                new[] {-hwBedTop, zBedTop}, new[] {hwBedTop, zBedTop},
                new[] {hwSurrTop, zSurrTop}, new[] {-hwSurrTop, zSurrTop} };
            st.BackfillPoly = backfillDegenerate ? new List<double[]>() : new List<double[]> {
                new[] {-hwSurrTop, zSurrTop }, new[] {hwSurrTop, zSurrTop },
                new[] {hwDolgu, zTopDolgu}, new[] {-hwDolgu, zTopDolgu} };

            st.PipePoly = PipePolyAt(row.PipeOuterDiamM, outerPipeBtm, cosHalf);

            // Where this station's plane (⊥ b through (wx,wy)) cuts the pipe axis:
            // c = ((W − Start)·b) / (u·b). Sign-invariant in b.
            st.CosHalf = cosHalf;
            st.AxisCut = t;
            if (L > 1e-9)
            {
                double ux = (row.EndX - row.StartX) / L, uy = (row.EndY - row.StartY) / L;
                double ub = ux * bTx + uy * bTy;
                if (Math.Abs(ub) > 1e-9)
                    st.AxisCut = ((wx - row.StartX) * bTx + (wy - row.StartY) * bTy) / ub;
            }

            st.AreaExcav    = Math.Round(ClipperGeo.Area(st.ExcavPoly),    3);
            st.AreaBedding  = Math.Round(ClipperGeo.Area(st.BeddingPoly),  3);
            st.AreaSurround = Math.Round(Math.Max(0, ClipperGeo.Area(st.SurroundPoly) - row.PipeArea), 3);
            st.AreaBackfill = backfillDegenerate ? 0.0 : Math.Round(ClipperGeo.Area(st.BackfillPoly), 3);

            st.AreaExcavDeducted      = st.AreaExcav;
            st.AreaExcavDeductedKL    = st.AreaExcav;
            st.AreaExcavSP            = st.AreaExcav;
            st.AreaBackfillDeducted   = st.AreaBackfill;
            st.AreaBackfillDeductedKL = st.AreaBackfill;
            st.AreaBackfillSP         = st.AreaBackfill;
            st.IsBisectorZone         = true;

            return st;
        }

        // Set OtherInvertZ on the boundary station so that the zone-edge segment
        // is recognised as "in zone" by IntegrateVolumes.
        private static void SetBoundaryOtherInvert(
            List<SimplifiedStation> list,    double boundaryDist,
            SectionDebugRow         otherRow, double otherDist)
        {
            var st = list.FirstOrDefault(s => Math.Abs(s.StationDist - boundaryDist) < 1e-4);
            if (st == null) return;
            double f = otherRow.Length2D > 1e-9
                ? Math.Max(0, Math.Min(1, otherDist / otherRow.Length2D)) : 0.0;
            st.OtherInvertZ = otherRow.InvertStart
                + (otherRow.InvertEnd - otherRow.InvertStart) * f;
        }

        // =====================================================================
        // Phase 8 — Pair scenario processing
        // =====================================================================

        private static void ProcessPairScenarios(
            List<SectionDebugRow>         rows,
            List<PairZone>                pairZones,
            List<List<SimplifiedStation>> allStations)
        {
            foreach (var pz in pairZones)
            {
                if (pz.UseBisectorFrame) continue; // already handled by ProcessBisectorPairs

                var rowA  = rows[pz.PipeA];
                var rowB  = rows[pz.PipeB];
                var listA = allStations[pz.PipeA];
                var listB = allStations[pz.PipeB];

                double L_A = rowA.Length2D;
                double L_B = rowB.Length2D;
                if (L_A < 1e-9 || L_B < 1e-9) continue;

                double txA = (rowA.EndX - rowA.StartX) / L_A;
                double tyA = (rowA.EndY - rowA.StartY) / L_A;
                double nxA = -tyA, nyA = txA;

                double txB = (rowB.EndX - rowB.StartX) / L_B;
                double tyB = (rowB.EndY - rowB.StartY) / L_B;

                double maxOffset = Math.Min(pz.ExitA - pz.EnterA, pz.ExitB - pz.EnterB);

                var zoneStationsA = listA
                    .Where(s => s.StationDist >= pz.EnterA - 1e-6 &&
                                s.StationDist <= pz.EnterA + maxOffset + 1e-6)
                    .OrderBy(s => s.StationDist)
                    .ToList();

                foreach (var sA in zoneStationsA)
                {
                    if (sA.ExcavPoly == null) continue;

                    double offset = sA.StationDist - pz.EnterA;
                    double distB  = Math.Min(pz.ExitB, pz.EnterB + offset);

                    var sB = listB.FirstOrDefault(
                        s => Math.Abs(s.StationDist - distB) < 1e-4);
                    if (sB == null || sB.ExcavPoly == null) continue;

                    double dx  = sA.WorldX - rowB.StartX;
                    double dy  = sA.WorldY - rowB.StartY;
                    double tB  = Math.Max(0, Math.Min(L_B, dx * txB + dy * tyB));
                    double bCx = rowB.StartX + txB * tB;
                    double bCy = rowB.StartY + tyB * tB;
                    double uBc = (bCx - sA.WorldX) * nxA + (bCy - sA.WorldY) * nyA;

                    // ── Excavation ───────────────────────────────────────────────
                    var aExcav = RoundPolyTo1mm(sA.ExcavPoly);
                    var bExcav = RoundPolyTo1mm(TranslatePoly(sB.ExcavPoly, uBc));

                    double aGrossE   = sA.AreaExcav;
                    double bGrossE   = sB.AreaExcav;
                    double combinedE = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aExcav },
                        new List<List<double[]>> { bExcav }));
                    double overlapE  = Math.Max(0.0, aGrossE + bGrossE - combinedE);

                    if (overlapE > 1e-6)
                    {
                        // Polygon-union deduction: accumulate each B-overlap polygon in A's local
                        // frame.  When two B pipes cover the same spatial region the union absorbs
                        // the duplicate, preventing the double-subtraction bug of the old scalar
                        // approach.
                        var overlapPolyE = ClipperGeo.Intersect(aExcav, bExcav);

                        sA.ExcavDeductPoly = sA.ExcavDeductPoly == null
                            ? overlapPolyE
                            : ClipperGeo.Union(sA.ExcavDeductPoly, overlapPolyE);
                        sA.AreaExcavDeducted = Math.Max(0, sA.AreaExcav
                            - ClipperGeo.Area(sA.ExcavDeductPoly));

                        var overlapPolyE_B = TranslatePolyList(overlapPolyE, -uBc);
                        sB.ExcavDeductPoly = sB.ExcavDeductPoly == null
                            ? overlapPolyE_B
                            : ClipperGeo.Union(sB.ExcavDeductPoly, overlapPolyE_B);
                        sB.AreaExcavDeducted = Math.Max(0, sB.AreaExcav
                            - ClipperGeo.Area(sB.ExcavDeductPoly));

                        // KL field: per-pair overwrite (not union-accumulated).
                        // When this station falls in multiple pair zones, AreaExcavDeducted (union)
                        // becomes over-deducted, breaking the KL invariant.  The KL field stores
                        // only this pair's deduction so IntegrateVolumes sees the correct value.
                        sA.AreaExcavDeductedKL = Math.Max(0, sA.AreaExcav - overlapE);
                        sB.AreaExcavDeductedKL = Math.Max(0, sB.AreaExcav - overlapE);

                        bool   aIsUpper   = sA.InvertZ >= sB.InvertZ;
                        double upperHW    = aIsUpper ? (rowA.TrWidth * 0.5) : (rowB.TrWidth * 0.5);
                        double upperCtrU  = aIsUpper ? 0.0 : uBc;
                        double lowerCtrU  = aIsUpper ? uBc : 0.0;
                        bool   lowerRight = lowerCtrU > upperCtrU;
                        double splitU     = lowerRight ? upperCtrU + upperHW : upperCtrU - upperHW;

                        const double LARGE = 1000.0;
                        List<double[]> upperSideRect, lowerSideRect;
                        if (lowerRight)
                        {
                            upperSideRect = new List<double[]> {
                                new[] {-LARGE,-LARGE}, new[] {splitU,-LARGE},
                                new[] {splitU,+LARGE}, new[] {-LARGE,+LARGE} };
                            lowerSideRect = new List<double[]> {
                                new[] {splitU,-LARGE}, new[] {+LARGE,-LARGE},
                                new[] {+LARGE,+LARGE}, new[] {splitU,+LARGE} };
                        }
                        else
                        {
                            upperSideRect = new List<double[]> {
                                new[] {splitU,-LARGE}, new[] {+LARGE,-LARGE},
                                new[] {+LARGE,+LARGE}, new[] {splitU,+LARGE} };
                            lowerSideRect = new List<double[]> {
                                new[] {-LARGE,-LARGE}, new[] {splitU,-LARGE},
                                new[] {splitU,+LARGE}, new[] {-LARGE,+LARGE} };
                        }

                        // SP polygon union — same principle: union of lost-side polys so that
                        // two B pipes on the same side don't cause double SP loss.
                        var aLosePolyE = ClipperGeo.Intersect(overlapPolyE,
                            new List<List<double[]>> { aIsUpper ? lowerSideRect : upperSideRect });
                        sA.ExcavSPLosePoly = sA.ExcavSPLosePoly == null
                            ? aLosePolyE
                            : ClipperGeo.Union(sA.ExcavSPLosePoly, aLosePolyE);
                        sA.AreaExcavSP = Math.Max(0, sA.AreaExcav
                            - ClipperGeo.Area(sA.ExcavSPLosePoly));

                        var bLosePolyE_inA = ClipperGeo.Intersect(overlapPolyE,
                            new List<List<double[]>> { aIsUpper ? upperSideRect : lowerSideRect });
                        var bLosePolyE_inB = TranslatePolyList(bLosePolyE_inA, -uBc);
                        sB.ExcavSPLosePoly = sB.ExcavSPLosePoly == null
                            ? bLosePolyE_inB
                            : ClipperGeo.Union(sB.ExcavSPLosePoly, bLosePolyE_inB);
                        sB.AreaExcavSP = Math.Max(0, sB.AreaExcav
                            - ClipperGeo.Area(sB.ExcavSPLosePoly));

                    }

                    // ── Backfill ─────────────────────────────────────────────────
                    var aBf = RoundPolyTo1mm(sA.BackfillPoly);
                    var bBf = RoundPolyTo1mm(TranslatePoly(sB.BackfillPoly, uBc));

                    double aGrossB   = sA.AreaBackfill;
                    double bGrossB   = sB.AreaBackfill;
                    double combinedB = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aBf },
                        new List<List<double[]>> { bBf }));
                    double overlapB  = Math.Max(0.0, aGrossB + bGrossB - combinedB);

                    if (overlapB > 1e-6)
                    {
                        var overlapPolyB = ClipperGeo.Intersect(aBf, bBf);

                        sA.BackfillDeductPoly = sA.BackfillDeductPoly == null
                            ? overlapPolyB
                            : ClipperGeo.Union(sA.BackfillDeductPoly, overlapPolyB);
                        sA.AreaBackfillDeducted = Math.Max(0, sA.AreaBackfill
                            - ClipperGeo.Area(sA.BackfillDeductPoly));

                        var overlapPolyB_B = TranslatePolyList(overlapPolyB, -uBc);
                        sB.BackfillDeductPoly = sB.BackfillDeductPoly == null
                            ? overlapPolyB_B
                            : ClipperGeo.Union(sB.BackfillDeductPoly, overlapPolyB_B);
                        sB.AreaBackfillDeducted = Math.Max(0, sB.AreaBackfill
                            - ClipperGeo.Area(sB.BackfillDeductPoly));

                        sA.AreaBackfillDeductedKL = Math.Max(0, sA.AreaBackfill - overlapB);
                        sB.AreaBackfillDeductedKL = Math.Max(0, sB.AreaBackfill - overlapB);

                        bool   aIsUpperB  = sA.InvertZ >= sB.InvertZ;
                        double upperHWB   = aIsUpperB ? (rowA.TrWidth * 0.5) : (rowB.TrWidth * 0.5);
                        double upperCtrUB = aIsUpperB ? 0.0 : uBc;
                        double lowerCtrUB = aIsUpperB ? uBc : 0.0;
                        bool   lowerRightB = lowerCtrUB > upperCtrUB;
                        double splitUB    = lowerRightB ? upperCtrUB + upperHWB : upperCtrUB - upperHWB;

                        const double LARGEB = 1000.0;
                        List<double[]> upperSideRectB, lowerSideRectB;
                        if (lowerRightB)
                        {
                            upperSideRectB = new List<double[]> {
                                new[] {-LARGEB,-LARGEB}, new[] {splitUB,-LARGEB},
                                new[] {splitUB,+LARGEB}, new[] {-LARGEB,+LARGEB} };
                            lowerSideRectB = new List<double[]> {
                                new[] {splitUB,-LARGEB}, new[] {+LARGEB,-LARGEB},
                                new[] {+LARGEB,+LARGEB}, new[] {splitUB,+LARGEB} };
                        }
                        else
                        {
                            upperSideRectB = new List<double[]> {
                                new[] {splitUB,-LARGEB}, new[] {+LARGEB,-LARGEB},
                                new[] {+LARGEB,+LARGEB}, new[] {splitUB,+LARGEB} };
                            lowerSideRectB = new List<double[]> {
                                new[] {-LARGEB,-LARGEB}, new[] {splitUB,-LARGEB},
                                new[] {splitUB,+LARGEB}, new[] {-LARGEB,+LARGEB} };
                        }

                        var aLosePolyB = ClipperGeo.Intersect(overlapPolyB,
                            new List<List<double[]>> { aIsUpperB ? lowerSideRectB : upperSideRectB });
                        sA.BackfillSPLosePoly = sA.BackfillSPLosePoly == null
                            ? aLosePolyB
                            : ClipperGeo.Union(sA.BackfillSPLosePoly, aLosePolyB);
                        sA.AreaBackfillSP = Math.Max(0, sA.AreaBackfill
                            - ClipperGeo.Area(sA.BackfillSPLosePoly));

                        var bLosePolyB_inA = ClipperGeo.Intersect(overlapPolyB,
                            new List<List<double[]>> { aIsUpperB ? upperSideRectB : lowerSideRectB });
                        var bLosePolyB_inB = TranslatePolyList(bLosePolyB_inA, -uBc);
                        sB.BackfillSPLosePoly = sB.BackfillSPLosePoly == null
                            ? bLosePolyB_inB
                            : ClipperGeo.Union(sB.BackfillSPLosePoly, bLosePolyB_inB);
                        sB.AreaBackfillSP = Math.Max(0, sB.AreaBackfill
                            - ClipperGeo.Area(sB.BackfillSPLosePoly));
                    }

                    ApplyCrossLayerLosses(sA, sB, uBc);

                    sA.OtherInvertZ = sB.InvertZ;
                    sB.OtherInvertZ = sA.InvertZ;
                }
            }
        }

        // =====================================================================
        // Phase 8c — Boundary station duplication
        // =====================================================================

        private static void InjectBoundaryDuplicates(
            List<List<SimplifiedStation>>  allStations,
            List<List<(double, double)>>   perPipeZones,
            List<SectionDebugRow>          rows,
            List<PairZone>                 pairZones)
        {
            // Build set of zones that belong to bisector pairs — skip GrossClone for these.
            // Bisector transitions are handled naturally by OtherInvertZ=NaN on non-bisector stations.
            var bisZones = new HashSet<(int pi, double enter, double exit)>();
            foreach (var pz in pairZones)
            {
                if (!pz.UseBisectorFrame) continue;
                bisZones.Add((pz.PipeA, pz.EnterA, pz.ExitA));
                bisZones.Add((pz.PipeB, pz.EnterB, pz.ExitB));
            }

            for (int pi = 0; pi < allStations.Count; pi++)
            {
                var stations = allStations[pi];
                var zones    = perPipeZones[pi];
                if (zones.Count == 0) continue;

                double pipeLen = Math.Round(rows[pi].Length2D, 3);

                for (int zi = zones.Count - 1; zi >= 0; zi--)
                {
                    double enterDist = zones[zi].Item1;
                    double exitDist  = zones[zi].Item2;

                    // Bisector-pair zones: no GrossClone needed — transition is OtherInvertZ-driven
                    if (bisZones.Contains((pi, enterDist, exitDist))) continue;

                    if (exitDist < pipeLen - 1e-4)
                    {
                        int exitIdx = stations.FindIndex(
                            s => Math.Abs(s.StationDist - exitDist) < 1e-4);
                        if (exitIdx >= 0)
                            stations.Insert(exitIdx + 1, GrossClone(stations[exitIdx]));
                    }

                    if (enterDist > 1e-4)
                    {
                        int enterIdx = stations.FindIndex(
                            s => Math.Abs(s.StationDist - enterDist) < 1e-4);
                        if (enterIdx >= 0)
                            stations.Insert(enterIdx, GrossClone(stations[enterIdx]));
                    }
                }
            }
        }

        private static SimplifiedStation GrossClone(SimplifiedStation src)
        {
            return new SimplifiedStation
            {
                StationDist          = src.StationDist,
                WorldX               = src.WorldX,
                WorldY               = src.WorldY,
                TerrainZ             = src.TerrainZ,
                DolguZ               = src.DolguZ,
                TrueDepthDolgu       = src.TrueDepthDolgu,
                DolguInvalid         = src.DolguInvalid,
                InvertZ              = src.InvertZ,
                TrueDepth            = src.TrueDepth,
                HwExcav              = src.HwExcav,
                ExcavPoly            = src.ExcavPoly,
                BeddingPoly          = src.BeddingPoly,
                SurroundPoly         = src.SurroundPoly,
                BackfillPoly         = src.BackfillPoly,
                AreaExcav            = src.AreaExcav,
                AreaBedding          = src.AreaBedding,
                AreaSurround         = src.AreaSurround,
                AreaBackfill         = src.AreaBackfill,
                AreaExcavDeducted    = src.AreaExcav,
                AreaExcavDeductedKL  = src.AreaExcav,
                AreaExcavSP          = src.AreaExcav,
                AreaBackfillDeducted    = src.AreaBackfill,
                AreaBackfillDeductedKL  = src.AreaBackfill,
                AreaBackfillSP          = src.AreaBackfill,
                OtherInvertZ         = double.NaN,
                AxisCut              = src.AxisCut,
                CosHalf              = src.CosHalf
            };
        }

        // =====================================================================
        // Phase 8d — Bisector boundary world-position fix
        //
        // After InjectBoundaryDuplicates, each zone boundary has two stations
        // at the same StationDist: a GrossClone (OtherInvertZ=NaN, WorldXY on
        // the pipe axis) and the original inside station (OtherInvertZ set,
        // WorldXY still on the pipe axis from BuildGrossProfiles).
        //
        // To make the boundary→first-bisector-station segment symmetric for A
        // and B, project each inside boundary station's pipe-axis world position
        // onto the bisector LINE.  The GrossClone is left untouched so the
        // outside integration still uses the correct pipe-axis path length.
        // =====================================================================

        private static void UpdateBisectorBoundaryWorldPos(
            List<PairZone>                pairZones,
            List<SectionDebugRow>         rows,
            List<List<SimplifiedStation>> allStations)
        {
            foreach (var pz in pairZones)
            {
                if (!pz.UseBisectorFrame) continue;

                var rowA = rows[pz.PipeA]; var rowB = rows[pz.PipeB];
                var listA = allStations[pz.PipeA]; var listB = allStations[pz.PipeB];

                double L_A = rowA.Length2D, L_B = rowB.Length2D;
                if (L_A < 1e-9 || L_B < 1e-9) continue;

                double txA = (rowA.EndX - rowA.StartX) / L_A, tyA = (rowA.EndY - rowA.StartY) / L_A;
                double txB = (rowB.EndX - rowB.StartX) / L_B, tyB = (rowB.EndY - rowB.StartY) / L_B;

                double dotAB = txA * txB + tyA * tyB;
                double bRawX = dotAB >= 0 ? txA + txB : txA - txB;
                double bRawY = dotAB >= 0 ? tyA + tyB : tyA - tyB;
                double bLen  = Math.Sqrt(bRawX * bRawX + bRawY * bRawY);
                if (bLen < 1e-9) continue;
                double bTx = bRawX / bLen, bTy = bRawY / bLen;

                double dx0   = rowB.StartX - rowA.StartX, dy0 = rowB.StartY - rowA.StartY;
                double denom = txA * tyB - tyA * txB;
                if (Math.Abs(denom) < 1e-9) continue;
                double originX = rowA.StartX + txA * (dx0 * tyB - dy0 * txB) / denom;
                double originY = rowA.StartY + tyA * (dx0 * tyB - dy0 * txB) / denom;

                // Project a pipe-axis boundary position onto the bisector line and
                // update the INSIDE boundary station (the one with OtherInvertZ set).
                void ProjectBoundary(List<SimplifiedStation> list, double dist, double pxw, double pyw)
                {
                    // Prefer the station with OtherInvertZ set (inside), not the GrossClone
                    var st = list.FirstOrDefault(
                        s => Math.Abs(s.StationDist - dist) < 1e-4 && !double.IsNaN(s.OtherInvertZ));
                    if (st == null) return;
                    double d = (pxw - originX) * bTx + (pyw - originY) * bTy;
                    st.WorldX = originX + bTx * d;
                    st.WorldY = originY + bTy * d;
                }

                ProjectBoundary(listA, pz.EnterA,
                    rowA.StartX + txA * pz.EnterA, rowA.StartY + tyA * pz.EnterA);
                ProjectBoundary(listA, pz.ExitA,
                    rowA.StartX + txA * pz.ExitA,  rowA.StartY + tyA * pz.ExitA);
                ProjectBoundary(listB, pz.EnterB,
                    rowB.StartX + txB * pz.EnterB, rowB.StartY + tyB * pz.EnterB);
                ProjectBoundary(listB, pz.ExitB,
                    rowB.StartX + txB * pz.ExitB,  rowB.StartY + tyB * pz.ExitB);
            }
        }

        // =====================================================================
        // Phase 9 — Trapezoidal integration
        // =====================================================================

        private static SimplifiedSectionResult IntegrateVolumes(
            SectionDebugRow         row,
            List<SimplifiedStation> stations)
        {
            var res = new SimplifiedSectionResult
            {
                PipeName   = row.PipeName,
                DiameterMm = row.DiameterMm,
                Material   = row.Material,
                Length2D   = row.Length2D
            };

            if (stations.Count < 2) return res;

            for (int i = 0; i < stations.Count - 1; i++)
            {
                var s0 = stations[i];
                var s1 = stations[i + 1];

                double dAx = s1.AxisCut - s0.AxisCut;           // signed axis distance
                if (Math.Abs(dAx) < 1e-9) continue;
                // Perpendicular-area trapezoid over the axis distance (see SimplifiedStation.AxisCut).
                double Seg(double a0, double a1) => (a0 * s0.CosHalf + a1 * s1.CosHalf) * 0.5 * dAx;

                res.VBedding  += Seg(s0.AreaBedding,  s1.AreaBedding);
                res.VSurround += Seg(s0.AreaSurround, s1.AreaSurround);
                res.VBeddingGross  += Seg(s0.AreaBeddingGross,  s1.AreaBeddingGross);
                res.VSurroundGross += Seg(s0.AreaSurroundGross, s1.AreaSurroundGross);

                // Backfill displaced by the other pipe's sand/pipe body (Phase 8e) —
                // scenario-independent, so it comes off KU/KL/SP alike (never Gross).
                double vBfSand = Seg(s0.BackfillSandLoss, s1.BackfillSandLoss);

                bool inZone = !double.IsNaN(s0.OtherInvertZ) &&
                              !double.IsNaN(s1.OtherInvertZ);

                // Bisector zone: the GROSS volume is integrated here like everywhere else;
                // IntegrateBisectorZones only subtracts the scenario overlap deductions.
                if ((inZone && s0.IsBisectorZone && s1.IsBisectorZone) || !inZone)
                {
                    double ve = Seg(s0.AreaExcav,    s1.AreaExcav);
                    double vb = Seg(s0.AreaBackfill, s1.AreaBackfill);
                    res.VExcavKU    += ve; res.VExcavKL    += ve; res.VExcavSP    += ve;
                    res.VBackfillKU += vb; res.VBackfillKL += vb; res.VBackfillSP += vb;
                    res.VExcavGross += ve; res.VBackfillGross += vb;
                }
                else
                {
                    // Parallel-zone (non-bisector) in-zone: use deducted values from ProcessPairScenarios
                    bool thisIsUpper =
                        (s0.InvertZ + s1.InvertZ) >= (s0.OtherInvertZ + s1.OtherInvertZ);

                    double eKU_s0 = thisIsUpper ? s0.AreaExcav : s0.AreaExcavDeducted;
                    double eKU_s1 = thisIsUpper ? s1.AreaExcav : s1.AreaExcavDeducted;
                    double eKL_s0 = thisIsUpper ? s0.AreaExcavDeductedKL : s0.AreaExcav;
                    double eKL_s1 = thisIsUpper ? s1.AreaExcavDeductedKL : s1.AreaExcav;

                    res.VExcavKU += Seg(eKU_s0, eKU_s1);
                    res.VExcavKL += Seg(eKL_s0, eKL_s1);
                    res.VExcavSP += Seg(s0.AreaExcavSP, s1.AreaExcavSP);
                    res.VExcavGross += Seg(s0.AreaExcav, s1.AreaExcav);

                    double bKU_s0 = thisIsUpper ? s0.AreaBackfill : s0.AreaBackfillDeducted;
                    double bKU_s1 = thisIsUpper ? s1.AreaBackfill : s1.AreaBackfillDeducted;
                    double bKL_s0 = thisIsUpper ? s0.AreaBackfillDeductedKL : s0.AreaBackfill;
                    double bKL_s1 = thisIsUpper ? s1.AreaBackfillDeductedKL : s1.AreaBackfill;

                    res.VBackfillKU += Seg(bKU_s0, bKU_s1);
                    res.VBackfillKL += Seg(bKL_s0, bKL_s1);
                    res.VBackfillSP += Seg(s0.AreaBackfillSP, s1.AreaBackfillSP);
                    res.VBackfillGross += Seg(s0.AreaBackfill, s1.AreaBackfill);
                }

                // No per-segment clamp: a backward segment (signed dAx < 0) legitimately
                // subtracts; the totals are clamped once at the end of the pipe.
                res.VBackfillKU -= vBfSand;
                res.VBackfillKL -= vBfSand;
                res.VBackfillSP -= vBfSand;

                res.SegmentCount++;
            }

            return res;
        }

        // =====================================================================
        // Phase 9b — Bisector zone independent integration
        //
        // Each crossing pair is treated as a self-contained new entity.
        // For each pair of consecutive bisector stations [k, k+1]:
        //   vA      = A gross area × dXY (same bisector dXY for both pipes)
        //   vB      = B gross area × dXY
        //   vOverlap= overlap area × dXY   (overlap = A.AreaExcav - A.AreaExcavDeductedKL)
        //   vUnion  = vA + vB - vOverlap
        //   KU: upper pipe keeps vA/vB gross; lower pipe deducts vOverlap
        //   KL: upper pipe deducts vOverlap; lower pipe keeps vB/vA gross
        //   SP: each pipe deducts half vOverlap
        //
        // Because both pipes share the same dXY, KU_combined = KL_combined = vUnion
        // at every slice — the invariant is structural, not coincidental.
        // =====================================================================

        private static void IntegrateBisectorZones(
            List<PairZone>                pairZones,
            List<SimplifiedSectionResult> results)
        {
            foreach (var pz in pairZones)
            {
                if (!pz.UseBisectorFrame) continue;
                if (pz.BisStationsA.Count < 2) continue;

                var resA = results[pz.PipeA];
                var resB = results[pz.PipeB];

                for (int k = 0; k < pz.BisStationsA.Count - 1; k++)
                {
                    var sA0 = pz.BisStationsA[k];
                    var sA1 = pz.BisStationsA[k + 1];
                    var sB0 = pz.BisStationsB[k];
                    var sB1 = pz.BisStationsB[k + 1];

                    double dXY = Math.Round(Math.Sqrt(
                        (sA1.WorldX - sA0.WorldX) * (sA1.WorldX - sA0.WorldX) +
                        (sA1.WorldY - sA0.WorldY) * (sA1.WorldY - sA0.WorldY)), 3);
                    if (dXY < 1e-9) continue;

                    // Gross volumes (and the sand loss) are integrated per pipe in
                    // IntegrateVolumes on the axis-cut scheme; only the scenario overlap
                    // deductions are taken here. Both pipes share the bisector slices
                    // (parallel planes ⊥ b, spacing dXY), so an overlap AREA × dXY is the
                    // exact overlap volume (Cavalieri) — KU = KL = SP in total by symmetry.
                    double ov0 = sA0.AreaExcav - sA0.AreaExcavDeductedKL;
                    double ov1 = sA1.AreaExcav - sA1.AreaExcavDeductedKL;
                    double vOverlap = Math.Max(0, (ov0 + ov1) * 0.5 * dXY);

                    bool aIsUpper = (sA0.InvertZ + sA1.InvertZ) >= (sB0.InvertZ + sB1.InvertZ);

                    // Excav KU/KL/SP: the losing pipe gives up the overlap.
                    if (aIsUpper) { resB.VExcavKU -= vOverlap; resA.VExcavKL -= vOverlap; }
                    else          { resA.VExcavKU -= vOverlap; resB.VExcavKL -= vOverlap; }
                    resA.VExcavSP -= vOverlap * 0.5;
                    resB.VExcavSP -= vOverlap * 0.5;

                    // Backfill KU/KL/SP
                    double bov0 = sA0.AreaBackfill - sA0.AreaBackfillDeductedKL;
                    double bov1 = sA1.AreaBackfill - sA1.AreaBackfillDeductedKL;
                    double bvOverlap = Math.Max(0, (bov0 + bov1) * 0.5 * dXY);

                    if (aIsUpper) { resB.VBackfillKU -= bvOverlap; resA.VBackfillKL -= bvOverlap; }
                    else          { resA.VBackfillKU -= bvOverlap; resB.VBackfillKL -= bvOverlap; }
                    resA.VBackfillSP -= bvOverlap * 0.5;
                    resB.VBackfillSP -= bvOverlap * 0.5;
                }
            }
        }

        // =====================================================================
        // Phase 9c — Convert V2 stations → CrossSectionStation for DwgBoQStore
        // =====================================================================

        private static void PopulateReportStations(
            BoQReport                      report,
            List<SectionDebugRow>          rows,
            List<List<SimplifiedStation>>  allStations,
            List<PairZone>                 pairZones)
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row    = rows[i];
                var merged = new List<SimplifiedStation>(allStations[i]);

                // For bisector zones: replace interior allStations with pz.BisStations.
                foreach (var pz in pairZones)
                {
                    if (!pz.UseBisectorFrame) continue;

                    List<SimplifiedStation> bisSts;
                    double enterDist, exitDist;
                    if      (pz.PipeA == i) { bisSts = pz.BisStationsA; enterDist = pz.EnterA; exitDist = pz.ExitA; }
                    else if (pz.PipeB == i) { bisSts = pz.BisStationsB; enterDist = pz.EnterB; exitDist = pz.ExitB; }
                    else continue;

                    if (bisSts == null || bisSts.Count == 0) continue;

                    // Remove interior allStations — keep the two boundary stations.
                    merged.RemoveAll(s => s.StationDist > enterDist + 1e-6 && s.StationDist < exitDist - 1e-6);

                    // Map BisectorDist [min..max] → pipe-axis StationDist [enter..exit].
                    double bisMin  = bisSts.Min(s => s.BisectorDist);
                    double bisMax  = bisSts.Max(s => s.BisectorDist);
                    double bisSpan = bisMax - bisMin;

                    foreach (var bs in bisSts)
                    {
                        double t = bisSpan > 1e-9 ? (bs.BisectorDist - bisMin) / bisSpan : 0.5;
                        merged.Add(new SimplifiedStation
                        {
                            StationDist          = Math.Round(enterDist + t * (exitDist - enterDist), 3),
                            WorldX = bs.WorldX,  WorldY = bs.WorldY,
                            TerrainZ = bs.TerrainZ, DolguZ = bs.DolguZ, InvertZ = bs.InvertZ,
                            TrueDepth = bs.TrueDepth, TrueDepthDolgu = bs.TrueDepthDolgu,
                            DolguInvalid = bs.DolguInvalid, HwExcav = bs.HwExcav,
                            ExcavPoly    = bs.ExcavPoly,    BeddingPoly  = bs.BeddingPoly,
                            SurroundPoly = bs.SurroundPoly, BackfillPoly = bs.BackfillPoly,
                            AreaExcav            = bs.AreaExcav,
                            AreaBedding          = bs.AreaBedding,
                            AreaSurround         = bs.AreaSurround,
                            AreaBackfill         = bs.AreaBackfill,
                            AreaExcavDeducted    = bs.AreaExcavDeducted,
                            AreaBackfillDeducted = bs.AreaBackfillDeducted,
                            AreaExcavDeductedKL  = bs.AreaExcavDeductedKL,
                            AreaBackfillDeductedKL = bs.AreaBackfillDeductedKL,
                            AreaExcavSP    = bs.AreaExcavSP,
                            AreaBackfillSP = bs.AreaBackfillSP,
                            OtherInvertZ   = bs.OtherInvertZ,
                            IsBisectorZone = true,
                            BisectorDist   = bs.BisectorDist
                        });
                    }
                }

                merged.Sort((a, b) => a.StationDist.CompareTo(b.StationDist));

                row.Stations.Clear();
                foreach (var st in merged)
                    row.Stations.Add(ToCSS(st));
            }
        }

        private static CrossSectionStation ToCSS(SimplifiedStation st)
        {
            bool inParallelZone = !double.IsNaN(st.OtherInvertZ) && !st.IsBisectorZone;
            bool stIsUpper      = inParallelZone && (st.InvertZ >= st.OtherInvertZ);

            double kuEx = inParallelZone ? (stIsUpper ? st.AreaExcav : st.AreaExcavDeducted)    : st.AreaExcav;
            double klEx = inParallelZone ? (stIsUpper ? st.AreaExcavDeductedKL : st.AreaExcav)  : st.AreaExcav;
            double spEx = inParallelZone ? st.AreaExcavSP    : st.AreaExcav;

            double kuBf = inParallelZone ? (stIsUpper ? st.AreaBackfill : st.AreaBackfillDeducted)    : st.AreaBackfill;
            double klBf = inParallelZone ? (stIsUpper ? st.AreaBackfillDeductedKL : st.AreaBackfill) : st.AreaBackfill;
            double spBf = inParallelZone ? st.AreaBackfillSP : st.AreaBackfill;

            var pu = new ScenarioProfile { Preference = TiePreference.KeepUpper };
            pu.Excavation.NetArea = kuEx;  pu.Backfill.NetArea = kuBf;
            pu.Bedding.NetArea    = st.AreaBedding;  pu.Surround.NetArea = st.AreaSurround;

            var pl = new ScenarioProfile { Preference = TiePreference.KeepLower };
            pl.Excavation.NetArea = klEx;  pl.Backfill.NetArea = klBf;
            pl.Bedding.NetArea    = st.AreaBedding;  pl.Surround.NetArea = st.AreaSurround;

            var ps = new ScenarioProfile { Preference = TiePreference.Split };
            ps.Excavation.NetArea = spEx;  ps.Backfill.NetArea = spBf;
            ps.Bedding.NetArea    = st.AreaBedding;  ps.Surround.NetArea = st.AreaSurround;

            return new CrossSectionStation
            {
                StationDist       = st.StationDist,
                WorldX            = st.WorldX,
                WorldY            = st.WorldY,
                TerrainZ          = st.TerrainZ,
                DolguZ            = st.DolguZ,
                TrueDepthDolgu    = st.TrueDepthDolgu,
                DolguInvalid      = st.DolguInvalid,
                InvertZ           = st.InvertZ,
                TrueDepth         = st.TrueDepth,
                TopWidthExcav     = st.HwExcav * 2.0,
                AreaExcav         = st.AreaExcav,
                AreaBedding       = st.AreaBedding,
                AreaSurround      = st.AreaSurround,
                AreaBackfill      = st.AreaBackfill,
                AreaExcavNet      = kuEx,
                AreaBackfillNet   = kuBf,
                HasOverlap        = !double.IsNaN(st.OtherInvertZ),
                ExcavPoly         = st.ExcavPoly,
                BeddingPoly       = st.BeddingPoly,
                SurroundPoly      = st.SurroundPoly,
                BackfillPoly      = st.BackfillPoly,
                ScenarioKeepUpper = pu,
                ScenarioKeepLower = pl,
                ScenarioSplit     = ps
            };
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        private static List<double[]> TranslatePoly(List<double[]> poly, double uOffset)
        {
            var result = new List<double[]>(poly.Count);
            foreach (var v in poly)
                result.Add(new[] { v[0] + uOffset, v[1] });
            return result;
        }

        private static List<List<double[]>> TranslatePolyList(
            List<List<double[]>> polys, double uOffset)
            => polys.Select(p => TranslatePoly(p, uOffset)).ToList();

        private static List<double[]> RoundPolyTo1mm(List<double[]> poly)
        {
            var result = new List<double[]>(poly.Count);
            foreach (var v in poly)
                result.Add(new[] {
                    Math.Round(v[0] * 1000.0) / 1000.0,
                    Math.Round(v[1] * 1000.0) / 1000.0 });
            return result;
        }

        // =====================================================================
        // Phase 8e — Cross-layer clash losses
        //
        // The pair processing above resolves only SAME-layer overlaps
        // (Excav∩Excav, Backfill∩Backfill). Where one pipe's Yataklama/Gömlekleme
        // or pipe body lies inside the other pipe's trench, that volume was counted
        // twice (as this pipe's sand AND as the other's backfill/sand), so
        // Σfill exceeded Σexcavation (BAKU, 2026-10-13). Rules — every point of the
        // overlap ends up counted once (zero inside a pipe body):
        //   • sand/pipe(other) ∩ backfill(self)  → self's backfill loses it (sand wins)
        //   • sand(upper) ∩ sand/pipe(lower)     → the UPPER pipe's sand loses it
        //   • sand(lower) ∩ pipe(upper)          → the lower pipe's sand loses it
        //   • backfill ∩ backfill                → unchanged (KU/KL/SP scenario)
        // =====================================================================

        private static List<double[]> PipePolyAt(double odM, double outerPipeBtm, double cosHalf)
        {
            var ring = ClipperGeo.PipeBody(odM, outerPipeBtm, 32);
            if (ring == null) return null;
            if (cosHalf > 1e-9 && Math.Abs(cosHalf - 1.0) > 1e-12)
                foreach (var v in ring) v[0] /= cosHalf;   // bisector frame: widths scale by 1/cos(θ/2)
            return ring;
        }

        private static bool HasArea(List<double[]> ring) => ring != null && ring.Count >= 3;

        private static List<List<double[]>> Region(params List<double[]>[] rings)
            => rings.Where(HasArea).ToList();

        private static List<List<double[]>> Accumulate(
            List<List<double[]>> acc, List<List<double[]>> add)
        {
            if (add == null || add.Count == 0 || ClipperGeo.Area(add) <= 1e-9) return acc;
            return acc == null ? add : ClipperGeo.Union(acc, add);
        }

        private static void ApplyCrossLayerLosses(SimplifiedStation sA, SimplifiedStation sB, double uBc)
        {
            if (sA.ExcavPoly == null || sB.ExcavPoly == null) return;

            // Everything in A's frame (B shifted by its lateral offset uBc).
            List<double[]> R(List<double[]> p)  => HasArea(p) ? RoundPolyTo1mm(p) : null;
            List<double[]> RB(List<double[]> p) => HasArea(p) ? RoundPolyTo1mm(TranslatePoly(p, uBc)) : null;

            var aBed = R(sA.BeddingPoly);   var aSur  = R(sA.SurroundPoly);
            var aBf  = R(sA.BackfillPoly);  var aPipe = R(sA.PipePoly);
            var bBed = RB(sB.BeddingPoly);  var bSur  = RB(sB.SurroundPoly);
            var bBf  = RB(sB.BackfillPoly); var bPipe = RB(sB.PipePoly);

            // SurroundPoly spans the outer pipe bottom → OD+sand at full width, so it
            // already contains the pipe body: sand ∪ pipe of one pipe = Bedding ∪ Surround.
            var aSandPipe = Region(aBed, aSur);
            var bSandPipe = Region(bBed, bSur);

            // ── Backfill under the other pipe's sand/pipe → backfill loses it ──
            if (HasArea(aBf) && bSandPipe.Count > 0)
                sA.BackfillSandLossPoly = Accumulate(sA.BackfillSandLossPoly,
                    ClipperGeo.Intersect(Region(aBf), bSandPipe));
            if (HasArea(bBf) && aSandPipe.Count > 0)
                sB.BackfillSandLossPoly = Accumulate(sB.BackfillSandLossPoly,
                    TranslatePolyList(ClipperGeo.Intersect(Region(bBf), aSandPipe), -uBc));

            // ── Sand vs sand / pipe ──
            bool aIsUpper = sA.InvertZ >= sB.InvertZ;
            var upBed  = aIsUpper ? aBed  : bBed;   var upSur = aIsUpper ? aSur : bSur;
            var upPipe = aIsUpper ? aPipe : bPipe;
            var loBed  = aIsUpper ? bBed  : aBed;   var loSur = aIsUpper ? bSur : aSur;
            var loSandPipe   = aIsUpper ? bSandPipe : aSandPipe;
            var upPipeRegion = Region(upPipe);

            // Upper sand inside the lower pipe's sand or body → upper loses it
            // (its own pipe body is not sand, so it is cut out of the lost region).
            List<List<double[]>> UpperLoss(List<double[]> x)
            {
                if (!HasArea(x) || loSandPipe.Count == 0) return null;
                var r = ClipperGeo.Intersect(Region(x), loSandPipe);
                return upPipeRegion.Count > 0 ? ClipperGeo.Difference(r, upPipeRegion) : r;
            }
            // Lower sand occupied by the upper pipe's body → lower loses it.
            List<List<double[]>> LowerLoss(List<double[]> x)
                => HasArea(x) && upPipeRegion.Count > 0 ? ClipperGeo.Intersect(Region(x), upPipeRegion) : null;

            var upBedLoss = UpperLoss(upBed); var upSurLoss = UpperLoss(upSur);
            var loBedLoss = LowerLoss(loBed); var loSurLoss = LowerLoss(loSur);

            var aBedLoss = aIsUpper ? upBedLoss : loBedLoss;
            var aSurLoss = aIsUpper ? upSurLoss : loSurLoss;
            var bBedLoss = aIsUpper ? loBedLoss : upBedLoss;
            var bSurLoss = aIsUpper ? loSurLoss : upSurLoss;

            sA.BeddingLossPoly  = Accumulate(sA.BeddingLossPoly,  aBedLoss);
            sA.SurroundLossPoly = Accumulate(sA.SurroundLossPoly, aSurLoss);
            if (bBedLoss != null)
                sB.BeddingLossPoly  = Accumulate(sB.BeddingLossPoly,  TranslatePolyList(bBedLoss, -uBc));
            if (bSurLoss != null)
                sB.SurroundLossPoly = Accumulate(sB.SurroundLossPoly, TranslatePolyList(bSurLoss, -uBc));
        }

        // Turns the accumulated loss regions into areas: Yataklama/Gömlekleme are
        // reduced in place (they have no KU/KL/SP scenarios); the backfill loss is kept
        // separately and subtracted from every scenario (never Gross) at integration.
        private static void FinalizeCrossLayerLosses(List<List<SimplifiedStation>> allStations)
        {
            var seen = new HashSet<SimplifiedStation>();
            foreach (var list in allStations)
                foreach (var st in list)
                {
                    if (!seen.Add(st)) continue;
                    st.AreaBeddingGross  = st.AreaBedding;    // pre-loss, for the "Ignore" state
                    st.AreaSurroundGross = st.AreaSurround;
                    if (st.BeddingLossPoly != null)
                        st.AreaBedding  = Math.Max(0, st.AreaBedding  - ClipperGeo.Area(st.BeddingLossPoly));
                    if (st.SurroundLossPoly != null)
                        st.AreaSurround = Math.Max(0, st.AreaSurround - ClipperGeo.Area(st.SurroundLossPoly));
                    st.BackfillSandLoss = st.BackfillSandLossPoly != null
                        ? Math.Min(st.AreaBackfill, ClipperGeo.Area(st.BackfillSandLossPoly)) : 0;
                }
        }

        // =====================================================================
        // Internal data models
        // =====================================================================

        private sealed class SimplifiedStation
        {
            public double StationDist;
            public double WorldX, WorldY, TerrainZ, InvertZ, TrueDepth, HwExcav;
            /// <summary>ZDolgu interpolated at this station — backfill's own top reference (independent of TerrainZ/ZKazi).</summary>
            public double DolguZ, TrueDepthDolgu;
            /// <summary>True when DolguZ is below InvertZ at this station — invalid Dolgu Seviyesi; AreaBackfill stays 0 here.</summary>
            public bool   DolguInvalid;
            public List<double[]> ExcavPoly, BeddingPoly, SurroundPoly, BackfillPoly;
            public double AreaExcav, AreaBedding, AreaSurround, AreaBackfill;
            public double AreaExcavDeducted;
            public double AreaBackfillDeducted;
            // Per-pair overwrite (not union-accumulated): used for KL reporting only.
            // Prevents over-deduction when the same pipe is in multiple pair zones simultaneously.
            public double AreaExcavDeductedKL;
            public double AreaBackfillDeductedKL;
            public double AreaExcavSP;
            public double AreaBackfillSP;
            public double OtherInvertZ = double.NaN;
            // True for stations built by ProcessBisectorPairs (bisector frame)
            public bool   IsBisectorZone;
            // Signed distance along bisector from crossing origin (set by ProcessBisectorPairs)
            public double BisectorDist;
            // Accumulated union of all B-overlap polygons in this station's local frame.
            // Null until the first pair-zone interaction; prevents double-deduction when a
            // station falls inside multiple pair zones that cover the same spatial region.
            public List<List<double[]>> ExcavDeductPoly;
            public List<List<double[]>> BackfillDeductPoly;
            public List<List<double[]>> ExcavSPLosePoly;
            public List<List<double[]>> BackfillSPLosePoly;

            // ── Cross-layer clash (Phase 8e) ─────────────────────────────────
            // Pipe body (outer diameter) in this station's frame.
            public List<double[]> PipePoly;
            // Accumulated (union) regions this pipe loses to a clashing pipe, own frame:
            //   BackfillSandLossPoly — own backfill under the other pipe's sand/pipe body;
            //   BeddingLossPoly / SurroundLossPoly — own sand already owned by the other pipe
            //   (sand∩sand → the LOWER pipe keeps it) or occupied by the other pipe's body.
            public List<List<double[]>> BackfillSandLossPoly, BeddingLossPoly, SurroundLossPoly;
            // Area of BackfillSandLossPoly, set by FinalizeCrossLayerLosses.
            public double BackfillSandLoss;
            // Yataklama/Gömlekleme areas BEFORE the cross-layer losses (FinalizeCrossLayerLosses).
            public double AreaBeddingGross, AreaSurroundGross;

            // Axis-cut integration (2026-10-18): where this station's section plane cuts the
            // pipe axis (m from start) and the section's widening factor. On-axis stations:
            // AxisCut = StationDist, CosHalf = 1. Bisector stations: AxisCut = the cut of
            // their plane (⊥ bisector b) with the axis, CosHalf = cos(θ/2). Every volume is
            // Σ ½(A0·CosHalf0 + A1·CosHalf1)·(AxisCut1 − AxisCut0) — perpendicular area ×
            // signed axis distance — so the gross volume telescopes to the pipe's own trench
            // prism whatever the station layout (UT_BOQ_PIPE_DIAG: +750 m³ / +9.8% on crossing
            // pipes when integrated with the world distance between stations).
            public double AxisCut;
            public double CosHalf = 1.0;
        }

        private sealed class SimplifiedSectionResult
        {
            public string PipeName;
            public int    DiameterMm;
            public string Material;
            public double Length2D;
            public int    SegmentCount;

            public double VExcavKU,    VExcavKL,    VExcavSP,    VExcavGross;
            public double VBedding,    VSurround;
            public double VBeddingGross, VSurroundGross;
            public double VBackfillKU, VBackfillKL, VBackfillSP, VBackfillGross;
        }
    }
}
