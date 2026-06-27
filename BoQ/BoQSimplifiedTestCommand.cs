using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.BoQSimplifiedTestCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// BOQ_SIMPLIFIED_TEST — Area-conservation baseline pipeline.
    ///
    /// Invariant: at every intersection station,
    ///   AreaExcav_A_net + AreaExcav_B_net = Area(Union(A_poly, B_poly))
    ///
    /// Mirror injection: within any intersection zone, pipe A and pipe B share
    /// the same set of station offsets (measured from their respective zone
    /// entries), so every segment boundary aligns between the two pipes.
    ///
    /// Per-segment upper/lower: the shallower/deeper determination uses the
    /// average invert elevation over each integration segment, not per-station.
    /// </summary>
    public class BoQSimplifiedTestCommand
    {
        private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);

        private static string       _exportXmlPath;
        private static Editor       _editor;
        private static EventHandler _idleHandler;

        // =====================================================================
        // Command entry point
        // =====================================================================

        [CommandMethod("BOQ_SIMPLIFIED_TEST", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            ed.WriteMessage("\n[SimplifiedBoQ] >>> BOQ_SIMPLIFIED_TEST starting <<<");

            if (_idleHandler != null)
            {
                ed.WriteMessage("\n[SimplifiedBoQ] Already running — wait for completion.\n");
                return;
            }

            _editor        = ed;
            _exportXmlPath = Path.Combine(Path.GetTempPath(), "urbano_simplified_boq.xml");

            try { if (File.Exists(_exportXmlPath)) File.Delete(_exportXmlPath); } catch { }

            var exportService = new UrbanoExportService(ed);
            var cts           = new System.Threading.CancellationTokenSource(DialogTimeout);

            var staThread = new Thread(() => RunAutomation(exportService, cts));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            InputBlocker.Show();
            doc.SendStringToExecute("_ARS_EXPORT_XML\n", true, false, true);

            ed.WriteMessage(
                "\n[SimplifiedBoQ] Dialog automation started. " +
                "Results will appear in the command window when complete.\n");
        }

        // =====================================================================
        // STA automation thread
        // =====================================================================

        private static void RunAutomation(
            IUrbanoExportService                          exportService,
            System.Threading.CancellationTokenSource cts)
        {
            bool success = false;
            try   { success = exportService.WaitAndAutomate(_exportXmlPath, cts.Token); }
            catch (Exception ex)
            { _editor?.WriteMessage($"\n[SimplifiedBoQ] Automation error: {ex.Message}"); }
            finally { cts.Dispose(); }

            _idleHandler = success ? (EventHandler)OnIdleCompute : OnIdleAbort;
            Application.Idle += _idleHandler;
        }

        private static void OnIdleAbort(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;
            InputBlocker.Hide();
            _editor?.WriteMessage("\n[SimplifiedBoQ] Export automation failed.\n");
        }

        // =====================================================================
        // Main computation — AutoCAD main thread (Idle callback)
        // =====================================================================

        private static void OnIdleCompute(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            Editor ed      = _editor;
            string xmlPath = _exportXmlPath;

            try
            {
                // ── Phase 1: Parse ───────────────────────────────────────────────
                ed.WriteMessage("\n[SimplifiedBoQ] Parsing Urbano XML…");
                var parser = new BoQParserService(enableClashDetection: false);
                BoQReport report = parser.Parse(xmlPath, ed);
                var rows = report.SectionDebug;

                ed.WriteMessage($"\n[SimplifiedBoQ] {rows.Count} section(s) parsed.");
                if (rows.Count == 0)
                {
                    ed.WriteMessage("\n[SimplifiedBoQ] No sections — aborting.\n");
                    return;
                }

                // ── Phase 2: Max top half-width per pipe ─────────────────────────
                var maxHW = new double[rows.Count];
                for (int i = 0; i < rows.Count; i++)
                {
                    double twS = rows[i].TrWidth + 2.0 * rows[i].TrueDepthStart * rows[i].SlopeRatio;
                    double twE = rows[i].TrWidth + 2.0 * rows[i].TrueDepthEnd   * rows[i].SlopeRatio;
                    maxHW[i]   = Math.Max(twS, twE) * 0.5;
                }

                // ── Phase 3: Oriented-corridor scan ─────────────────────────────
                // Each pipe's corridor = parallelogram extending ±maxHW perpendicular
                // to the pipe axis (the two red boundary lines in plan view).
                // Two pipes are candidates only if their corridors actually overlap
                // in plan — not just their axis-aligned bounding boxes.
                var corridors = BuildCorridors(rows, maxHW);
                var pairs     = FindOverlapPairs(corridors, rows);

                ed.WriteMessage($"\n[SimplifiedBoQ] Corridor scan: {pairs.Count} overlapping pair(s).");
                foreach (var (ai, bi) in pairs)
                    ed.WriteMessage($"\n   Pair: [{rows[ai].PipeName}] ↔ [{rows[bi].PipeName}]");

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

                // ── Phase 8: Pair scenario processing (area conservation) ─────────
                ProcessPairScenarios(rows, pairZones, allStations, ed);

                // ── Phase 8c: Boundary station duplication ───────────────────────
                // At each zone entry and exit, insert a gross clone at the same
                // position. The clone has OtherInvertZ=NaN so integration treats it
                // as outside the zone (uses full gross area). Because the clone sits
                // at the same world coordinates, dXY=0 for the clone↔original pair,
                // contributing exactly zero volume — the area jumps cleanly.
                //   entry boundary: [gross-clone, SP-station, …zone…, SP-station, gross-clone]
                InjectBoundaryDuplicates(allStations, perPipeZones, rows);

                // ── Phase 9: Volume integration ──────────────────────────────────
                var results = new List<SimplifiedSectionResult>(rows.Count);
                for (int i = 0; i < rows.Count; i++)
                    results.Add(IntegrateVolumes(rows[i], allStations[i]));

                // ── Phase 10: Report ─────────────────────────────────────────────
                PrintReport(ed, results);
                SaveReport(ed, rows, allStations, results);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[SimplifiedBoQ ERROR] {ex.Message}\n{ex.StackTrace}");
            }
            finally
            {
                InputBlocker.Hide();
            }
        }

        // =====================================================================
        // Phase 3 — Oriented-corridor helpers (OBB)
        //
        // A pipe's "corridor" is the parallelogram traced by sweeping a segment of
        // half-width maxHW perpendicular to the pipe axis.  In plan view these are
        // the two boundary lines drawn parallel to the pipe centre-line at distance
        // maxHW on each side.  Two pipes can have overlapping trenches only if their
        // corridors overlap — tested with the Separating-Axis Theorem (SAT).
        // =====================================================================

        // Axis-oriented corridor for a single pipe.
        private struct PipeCorridor
        {
            public double StartX, StartY, EndX, EndY;
            public double Tx, Ty;       // unit tangent  (pipe direction)
            public double Nx, Ny;       // unit normal   (left-hand perpendicular)
            public double Length;
            public double HalfWidth;    // half of the trench top width (= maxHW)
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

        // Returns the 4 world-space corners of a pipe corridor (flat array x0,y0,…,x3,y3).
        // Corner order: left-start, left-end, right-end, right-start
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

        // Projects 4 corners (flat x0,y0,…) onto axis (ax,ay) → returns [min, max].
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

        // SAT test for two corridors — returns true when they overlap in plan view.
        private static bool CorridorsOverlap(PipeCorridor a, PipeCorridor b)
        {
            double[] cA = CorridorCorners(a);
            double[] cB = CorridorCorners(b);

            // 4 candidate separating axes: both tangents and both normals
            double[] axes = { a.Tx, a.Ty,  a.Nx, a.Ny,  b.Tx, b.Ty,  b.Nx, b.Ny };
            for (int ai = 0; ai < 4; ai++)
            {
                double ax = axes[ai * 2], ay = axes[ai * 2 + 1];
                ProjectOntoAxis(cA, ax, ay, out double aMin, out double aMax);
                ProjectOntoAxis(cB, ax, ay, out double bMin, out double bMax);
                if (aMax <= bMin + 1e-9 || bMax <= aMin + 1e-9)
                    return false;   // separated on this axis
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

                // Skip pairs that share a node GUID (connected in the same network).
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
        // Phase 4 — Zone computation (projection onto pipe axis)
        // =====================================================================

        private struct PairZone
        {
            public int    PipeA, PipeB;
            public double EnterA, ExitA;
            public double EnterB, ExitB;
        }

        // For each confirmed pair, find [enterA,exitA] and [enterB,exitB] by projecting
        // the OTHER pipe's corridor corners onto THIS pipe's tangent axis.
        private static List<PairZone> ComputePairZones(
            PipeCorridor[]   corridors,
            List<(int, int)> pairs)
        {
            var result = new List<PairZone>();
            foreach (var (ai, bi) in pairs)
            {
                var cA = corridors[ai];
                var cB = corridors[bi];

                // Zone along A = range where B's corridor has presence on A's axis
                double[] bCorners = CorridorCorners(cB);
                double bOnA_min = double.MaxValue, bOnA_max = double.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    double proj = (bCorners[k * 2]     - cA.StartX) * cA.Tx
                                + (bCorners[k * 2 + 1] - cA.StartY) * cA.Ty;
                    if (proj < bOnA_min) bOnA_min = proj;
                    if (proj > bOnA_max) bOnA_max = proj;
                }
                double enterA = Math.Max(0,         bOnA_min);
                double exitA  = Math.Min(cA.Length, bOnA_max);

                // Zone along B = range where A's corridor has presence on B's axis
                double[] aCorners = CorridorCorners(cA);
                double aOnB_min = double.MaxValue, aOnB_max = double.MinValue;
                for (int k = 0; k < 4; k++)
                {
                    double proj = (aCorners[k * 2]     - cB.StartX) * cB.Tx
                                + (aCorners[k * 2 + 1] - cB.StartY) * cB.Ty;
                    if (proj < aOnB_min) aOnB_min = proj;
                    if (proj > aOnB_max) aOnB_max = proj;
                }
                double enterB = Math.Max(0,         aOnB_min);
                double exitB  = Math.Min(cB.Length, aOnB_max);

                if (exitA > enterA + 1e-6 && exitB > enterB + 1e-6)
                    result.Add(new PairZone
                    {
                        PipeA = ai, PipeB = bi,
                        EnterA = enterA, ExitA = exitA,
                        EnterB = enterB, ExitB = exitB
                    });
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

        private const double IndependentInterval  = 5.0;
        private const double IntersectionInterval = 0.5;

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

            double cursor = 0.0;
            while (cursor < L - 1e-9)
            {
                distSet.Add(cursor);
                double step = InZone(cursor, zones) ? IntersectionInterval : IndependentInterval;
                cursor = Math.Min(cursor + step, L);
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
                var listA = allStations[pz.PipeA];
                var listB = allStations[pz.PipeB];

                double zoneLenA  = pz.ExitA - pz.EnterA;
                double zoneLenB  = pz.ExitB - pz.EnterB;
                double maxOffset = Math.Min(zoneLenA, zoneLenB);

                // Collect all offsets from both A and B within the zone
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

                // Inject missing stations (deduplicate within 1mm)
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
            double hwBed  = row.TopWidthBed  * 0.5;
            double hwSurr = row.TopWidthSurr * 0.5;

            foreach (var st in stations)
            {
                if (st.ExcavPoly != null) continue; // already built (guard for re-entry)

                double f    = Math.Min(st.StationDist / L, 1.0);
                st.WorldX   = row.StartX        + (row.EndX        - row.StartX)        * f;
                st.WorldY   = row.StartY        + (row.EndY        - row.StartY)        * f;
                st.TerrainZ = row.StartTerrainZ + (row.EndTerrainZ - row.StartTerrainZ) * f;
                st.InvertZ  = row.InvertStart   + (row.InvertEnd   - row.InvertStart)   * f;

                // Wall thickness = (OD - ID) / 2; bedding top = outer pipe bottom
                double pipeWall     = (row.PipeOuterDiamM - row.DiameterMm / 1000.0) / 2.0;
                double outerPipeBtm = st.InvertZ - pipeWall;

                double zBot     = outerPipeBtm - row.TrBedHeight;
                double zTop     = st.TerrainZ;
                double trueDepth  = Math.Max(0, zTop - zBot);
                double topWExcav  = row.TrWidth + 2.0 * trueDepth * row.SlopeRatio;
                double hwExcav    = topWExcav * 0.5;
                double zSurrTop   = Math.Min(outerPipeBtm + row.HSurround, zTop);

                st.TrueDepth = trueDepth;
                st.HwExcav   = hwExcav;

                st.ExcavPoly = new List<double[]>
                {
                    new[] { -hwBase,  zBot  }, new[] {  hwBase,  zBot  },
                    new[] {  hwExcav, zTop  }, new[] { -hwExcav, zTop  }
                };
                st.BeddingPoly = new List<double[]>
                {
                    new[] { -hwBase, zBot         }, new[] {  hwBase, zBot         },
                    new[] {  hwBed,  outerPipeBtm }, new[] { -hwBed,  outerPipeBtm }
                };
                st.SurroundPoly = new List<double[]>
                {
                    new[] { -hwBed,  outerPipeBtm }, new[] {  hwBed,  outerPipeBtm },
                    new[] {  hwSurr, zSurrTop     }, new[] { -hwSurr, zSurrTop     }
                };
                st.BackfillPoly = new List<double[]>
                {
                    new[] { -hwSurr,  zSurrTop }, new[] {  hwSurr,  zSurrTop },
                    new[] {  hwExcav, zTop     }, new[] { -hwExcav, zTop     }
                };

                // Derive areas from polygon coordinates via Clipper (not analytic formula).
                // The shape after a Difference may have more than 4 vertices, so coordinate-
                // based area is always correct regardless of polygon complexity.
                // Round to 4 decimal places (= 1 cm²) so all downstream code sees one
                // consistent value and Clipper rounding artefacts don't accumulate.
                st.AreaExcav    = Math.Round(ClipperGeo.Area(st.ExcavPoly),    4);
                st.AreaBedding  = Math.Round(ClipperGeo.Area(st.BeddingPoly),  4);
                st.AreaSurround = Math.Round(Math.Max(0, ClipperGeo.Area(st.SurroundPoly) - row.PipeArea), 4);
                st.AreaBackfill = Math.Round(ClipperGeo.Area(st.BackfillPoly), 4);

                // Initialize deducted/SP to gross; ProcessPairScenarios overwrites
                // these at intersection stations with Clipper Difference results.
                st.AreaExcavDeducted    = st.AreaExcav;
                st.AreaExcavSP          = st.AreaExcav;
                st.AreaBackfillDeducted = st.AreaBackfill;
                st.AreaBackfillSP       = st.AreaBackfill;
            }
        }

        // =====================================================================
        // Phase 8 — Pair scenario processing
        //
        // For each pair (A, B) and each mirrored station pair at offset d:
        //   1. Translate B's polygon to A's (U, Z) frame. Round all coords to 1 mm.
        //   2. A_gross = ClipperGeo.Area(A_poly)   — independent, from coordinates.
        //      B_gross = ClipperGeo.Area(B_poly)   — independent, from coordinates.
        //   3. combined = ClipperGeo.Area(Union(A, B))  via inclusion-exclusion.
        //   4. overlap = A_gross + B_gross - combined.
        //   5. AreaDeducted = ClipperGeo.Area(Difference(this, other))
        //      — the actual polygon after losing all overlap; may be triangle/pentagon.
        //   6. AreaSP = gross - overlap/2  (arithmetic, no geometric equivalent).
        //   7. Verification: each of (gross_A + deducted_B),
        //      (deducted_A + gross_B), (SP_A + SP_B) must equal combined.
        // =====================================================================

        private static void ProcessPairScenarios(
            List<SectionDebugRow>         rows,
            List<PairZone>                pairZones,
            List<List<SimplifiedStation>> allStations,
            Editor                        ed)
        {
            foreach (var pz in pairZones)
            {
                var rowA  = rows[pz.PipeA];
                var rowB  = rows[pz.PipeB];
                var listA = allStations[pz.PipeA];
                var listB = allStations[pz.PipeB];

                double L_A = rowA.Length2D;
                double L_B = rowB.Length2D;
                if (L_A < 1e-9 || L_B < 1e-9) continue;

                // A's unit tangent and left-hand normal (the U axis in A's cross-section frame)
                double txA = (rowA.EndX - rowA.StartX) / L_A;
                double tyA = (rowA.EndY - rowA.StartY) / L_A;
                double nxA = -tyA, nyA = txA;

                double txB = (rowB.EndX - rowB.StartX) / L_B;
                double tyB = (rowB.EndY - rowB.StartY) / L_B;

                double maxOffset = Math.Min(pz.ExitA - pz.EnterA, pz.ExitB - pz.EnterB);

                // Iterate A's stations inside the zone (B has mirror stations at same offsets)
                var zoneStationsA = listA
                    .Where(s => s.StationDist >= pz.EnterA - 1e-6 &&
                                s.StationDist <= pz.EnterA + maxOffset + 1e-6)
                    .OrderBy(s => s.StationDist)
                    .ToList();

                int pairHits = 0;

                foreach (var sA in zoneStationsA)
                {
                    if (sA.ExcavPoly == null) continue; // gross not built yet (shouldn't happen)

                    double offset = sA.StationDist - pz.EnterA;
                    double distB  = Math.Min(pz.ExitB, pz.EnterB + offset);

                    // Find B's mirror station
                    var sB = listB.FirstOrDefault(
                        s => Math.Abs(s.StationDist - distB) < 1e-4);
                    if (sB == null || sB.ExcavPoly == null) continue;

                    // ── Lateral offset of B's centerline from A's (in A's U direction) ──
                    // Project B's closest centerline point onto A's normal.
                    double dx  = sA.WorldX - rowB.StartX;
                    double dy  = sA.WorldY - rowB.StartY;
                    double tB  = Math.Max(0, Math.Min(L_B, dx * txB + dy * tyB));
                    double bCx = rowB.StartX + txB * tB;
                    double bCy = rowB.StartY + tyB * tB;
                    double uBc = (bCx - sA.WorldX) * nxA + (bCy - sA.WorldY) * nyA;

                    // ── Excavation ───────────────────────────────────────────────
                    var aExcav = RoundPolyTo1mm(sA.ExcavPoly);
                    var bExcav = RoundPolyTo1mm(TranslatePoly(sB.ExcavPoly, uBc));

                    // Use the stored (already-rounded-to-1cm²) gross areas so that
                    // IntegrateVolumes and ProcessPairScenarios share one consistent source.
                    double aGrossE   = sA.AreaExcav;
                    double bGrossE   = sB.AreaExcav;
                    double combinedE = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aExcav },
                        new List<List<double[]>> { bExcav }));
                    double overlapE  = Math.Max(0.0, aGrossE + bGrossE - combinedE);

                    if (overlapE > 1e-6)
                    {
                        // KU / KL: derive deducted from the same overlap value so that
                        // grossA + deductedB = grossB + deductedA = combined (exact invariant).
                        // Computing each via a separate Clipper Difference causes asymmetric
                        // integer-rounding that breaks the total-volume invariant.
                        double aDeductedE = Math.Max(0, aGrossE - overlapE);
                        double bDeductedE = Math.Max(0, bGrossE - overlapE);
                        sA.AreaExcavDeducted = aDeductedE;
                        sB.AreaExcavDeducted = bDeductedE;

                        // SP: geometric split preserving upper pipe's flat bottom width.
                        // Split line = vertical line at the edge of the upper pipe's bottom
                        // on the side facing the lower pipe.
                        // Upper pipe keeps the overlap on its own side; lower pipe keeps the rest.
                        // Invariant: aSP + bSP = combined (same as KU/KL).
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

                        var overlapPolyE = ClipperGeo.Intersect(aExcav, bExcav);
                        double ovOnUpperSideE = ClipperGeo.Area(ClipperGeo.Intersect(
                            overlapPolyE, new List<List<double[]>> { upperSideRect }));
                        // Derive lower side from overlapE so that upper+lower = overlapE exactly,
                        // avoiding the Clipper Union vs Intersect rounding asymmetry.
                        double ovOnLowerSideE = overlapE - ovOnUpperSideE;

                        // Upper pipe loses the overlap on the lower pipe's side (outside its base)
                        // Lower pipe loses the overlap on the upper pipe's side
                        double aLoseE = aIsUpper ? ovOnLowerSideE : ovOnUpperSideE;
                        double bLoseE = aIsUpper ? ovOnUpperSideE : ovOnLowerSideE;
                        sA.AreaExcavSP = Math.Max(0, aGrossE - aLoseE);
                        sB.AreaExcavSP = Math.Max(0, bGrossE - bLoseE);

                        double vKU = aGrossE    + bDeductedE;
                        double vKL = aDeductedE + bGrossE;
                        double vSP = sA.AreaExcavSP + sB.AreaExcavSP;
                        ed.WriteMessage(
                            $"\n   [E] combined={combinedE:F4}" +
                            $"  KU={vKU:F4}  KL={vKL:F4}  SP={vSP:F4}" +
                            $"  ov={overlapE:F4}  splitU={splitU:F3}");
                    }

                    // ── Backfill ─────────────────────────────────────────────────
                    var aBf = RoundPolyTo1mm(sA.BackfillPoly);
                    var bBf = RoundPolyTo1mm(TranslatePoly(sB.BackfillPoly, uBc));

                    // Same consistency fix as excavation: use stored gross areas.
                    double aGrossB   = sA.AreaBackfill;
                    double bGrossB   = sB.AreaBackfill;
                    double combinedB = ClipperGeo.Area(ClipperGeo.Union(
                        new List<List<double[]>> { aBf },
                        new List<List<double[]>> { bBf }));
                    double overlapB  = Math.Max(0.0, aGrossB + bGrossB - combinedB);

                    if (overlapB > 1e-6)
                    {
                        // Same symmetric fix as excavation: single overlap → exact invariant.
                        double aDeductedB = Math.Max(0, aGrossB - overlapB);
                        double bDeductedB = Math.Max(0, bGrossB - overlapB);
                        sA.AreaBackfillDeducted = aDeductedB;
                        sB.AreaBackfillDeducted = bDeductedB;

                        // Same geometric split line as excavation (same trench geometry)
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

                        var overlapPolyB = ClipperGeo.Intersect(aBf, bBf);
                        double ovOnUpperSideB = ClipperGeo.Area(ClipperGeo.Intersect(
                            overlapPolyB, new List<List<double[]>> { upperSideRectB }));
                        // Same fix as excavation: derive lower side from overlapB.
                        double ovOnLowerSideB = overlapB - ovOnUpperSideB;

                        double aLoseB = aIsUpperB ? ovOnLowerSideB : ovOnUpperSideB;
                        double bLoseB = aIsUpperB ? ovOnUpperSideB : ovOnLowerSideB;
                        sA.AreaBackfillSP = Math.Max(0, aGrossB - aLoseB);
                        sB.AreaBackfillSP = Math.Max(0, bGrossB - bLoseB);
                    }

                    // Invert of crossing pipe for per-segment upper/lower determination.
                    sA.OtherInvertZ = sB.InvertZ;
                    sB.OtherInvertZ = sA.InvertZ;

                    pairHits++;
                }

                ed.WriteMessage(
                    $"\n[SimplifiedBoQ] Pair [{rowA.PipeName}]↔[{rowB.PipeName}]: " +
                    $"{pairHits} station pair(s) processed.");
            }
        }

        // =====================================================================
        // Phase 8c — Boundary station duplication
        //
        // For each merged zone [enter, exit] on each pipe, two gross clones are
        // inserted: one immediately before the zone-entry station and one
        // immediately after the zone-exit station.
        //
        // Gross clone properties:
        //   • Same StationDist / WorldX / WorldY → dXY = 0 with its neighbour
        //   • OtherInvertZ = NaN → IntegrateVolumes treats it as outside the zone
        //   • All net area fields reset to gross → no deduction applied
        //
        // Zones are processed in reverse order so earlier insertions don't
        // invalidate the indices computed for later zones.
        // =====================================================================

        private static void InjectBoundaryDuplicates(
            List<List<SimplifiedStation>>  allStations,
            List<List<(double, double)>>   perPipeZones,
            List<SectionDebugRow>          rows)
        {
            for (int pi = 0; pi < allStations.Count; pi++)
            {
                var stations = allStations[pi];
                var zones    = perPipeZones[pi];
                if (zones.Count == 0) continue;

                double pipeLen = rows[pi].Length2D;

                // Reverse order: process zones with larger distances first so
                // insertions at lower indices don't shift the positions of
                // already-inserted clones at higher indices.
                for (int zi = zones.Count - 1; zi >= 0; zi--)
                {
                    double enterDist = zones[zi].Item1;
                    double exitDist  = zones[zi].Item2;

                    // ── Exit boundary ────────────────────────────────────────────
                    // Only insert gross clone AFTER exit if zone does NOT end at
                    // the pipe's endpoint (dist < L). If it ends at L, there is no
                    // segment after the zone — no clone needed.
                    if (exitDist < pipeLen - 1e-4)
                    {
                        int exitIdx = stations.FindIndex(
                            s => Math.Abs(s.StationDist - exitDist) < 1e-4);
                        if (exitIdx >= 0)
                            stations.Insert(exitIdx + 1, GrossClone(stations[exitIdx]));
                    }

                    // ── Entry boundary ───────────────────────────────────────────
                    // Only insert gross clone BEFORE entry if zone does NOT start at
                    // the pipe's start (dist > 0). If it starts at 0, there is no
                    // segment before the zone — no clone needed.
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

        // Returns a copy of src with all net area fields reset to gross and
        // OtherInvertZ = NaN, so the clone is treated as an outside-zone station.
        private static SimplifiedStation GrossClone(SimplifiedStation src)
        {
            return new SimplifiedStation
            {
                StationDist          = src.StationDist,
                WorldX               = src.WorldX,
                WorldY               = src.WorldY,
                TerrainZ             = src.TerrainZ,
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
                AreaExcavSP          = src.AreaExcav,
                AreaBackfillDeducted = src.AreaBackfill,
                AreaBackfillSP       = src.AreaBackfill,
                OtherInvertZ         = double.NaN
            };
        }

        // =====================================================================
        // Phase 9 — Trapezoidal integration with per-segment upper/lower
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

                double dXY = Math.Sqrt(
                    (s1.WorldX - s0.WorldX) * (s1.WorldX - s0.WorldX) +
                    (s1.WorldY - s0.WorldY) * (s1.WorldY - s0.WorldY));
                if (dXY < 1e-9) continue;

                // Bedding and surround: no overlap correction needed (below pipe)
                res.VBedding  += (s0.AreaBedding  + s1.AreaBedding)  * 0.5 * dXY;
                res.VSurround += (s0.AreaSurround + s1.AreaSurround) * 0.5 * dXY;

                // Determine if this segment is in an intersection zone
                bool inZone = !double.IsNaN(s0.OtherInvertZ) &&
                              !double.IsNaN(s1.OtherInvertZ);

                if (!inZone)
                {
                    // Outside intersection zone: all scenarios equal gross
                    double ve = (s0.AreaExcav    + s1.AreaExcav)    * 0.5 * dXY;
                    double vb = (s0.AreaBackfill + s1.AreaBackfill) * 0.5 * dXY;
                    res.VExcavKU    += ve; res.VExcavKL    += ve; res.VExcavSP    += ve;
                    res.VBackfillKU += vb; res.VBackfillKL += vb; res.VBackfillSP += vb;
                }
                else
                {
                    // Per-segment upper/lower from average invert over this segment.
                    // Higher invert Z = shallower = upper.
                    bool thisIsUpper =
                        (s0.InvertZ + s1.InvertZ) >= (s0.OtherInvertZ + s1.OtherInvertZ);

                    // ── Excavation ─────────────────────────────────────────────
                    // KU: upper keeps gross,    lower uses Clipper-Difference result.
                    // KL: lower keeps gross,    upper uses Clipper-Difference result.
                    // SP: both use arithmetic split (gross - overlap/2), stored in AreaExcavSP.
                    double eKU_s0 = thisIsUpper ? s0.AreaExcav : s0.AreaExcavDeducted;
                    double eKU_s1 = thisIsUpper ? s1.AreaExcav : s1.AreaExcavDeducted;
                    double eKL_s0 = thisIsUpper ? s0.AreaExcavDeducted : s0.AreaExcav;
                    double eKL_s1 = thisIsUpper ? s1.AreaExcavDeducted : s1.AreaExcav;

                    res.VExcavKU += (eKU_s0 + eKU_s1) * 0.5 * dXY;
                    res.VExcavKL += (eKL_s0 + eKL_s1) * 0.5 * dXY;
                    res.VExcavSP += (s0.AreaExcavSP + s1.AreaExcavSP) * 0.5 * dXY;

                    // ── Backfill ───────────────────────────────────────────────
                    double bKU_s0 = thisIsUpper ? s0.AreaBackfill : s0.AreaBackfillDeducted;
                    double bKU_s1 = thisIsUpper ? s1.AreaBackfill : s1.AreaBackfillDeducted;
                    double bKL_s0 = thisIsUpper ? s0.AreaBackfillDeducted : s0.AreaBackfill;
                    double bKL_s1 = thisIsUpper ? s1.AreaBackfillDeducted : s1.AreaBackfill;

                    res.VBackfillKU += (bKU_s0 + bKU_s1) * 0.5 * dXY;
                    res.VBackfillKL += (bKL_s0 + bKL_s1) * 0.5 * dXY;
                    res.VBackfillSP += (s0.AreaBackfillSP + s1.AreaBackfillSP) * 0.5 * dXY;
                }

                res.SegmentCount++;
            }

            return res;
        }

        // =====================================================================
        // Phase 10 — Report
        // =====================================================================

        private static void PrintReport(
            Editor ed, List<SimplifiedSectionResult> results)
        {
            ed.WriteMessage("\n\n══════════════ BOQ_SIMPLIFIED_TEST RESULTS ══════════════");
            ed.WriteMessage("\n  Area conservation invariant:  A_net + B_net = Area(A∪B)");
            ed.WriteMessage("\n  Per-segment upper/lower:      segment-average invert Z");
            ed.WriteMessage("\n  Symbols: KU=KeepUpper  KL=KeepLower  SP=Split 50/50\n");

            double sumExcavKU = 0, sumExcavKL = 0, sumExcavSP = 0;
            double sumBed     = 0, sumSurr     = 0;
            double sumBfKU    = 0, sumBfKL     = 0, sumBfSP    = 0;

            foreach (var r in results)
            {
                ed.WriteMessage(
                    $"\n── {r.PipeName}  Ø{r.DiameterMm}mm {r.Material}" +
                    $"  L={r.Length2D:F2}m  [{r.SegmentCount} segs]");
                ed.WriteMessage(
                    $"\n   Excavation: KU={r.VExcavKU:F2}  KL={r.VExcavKL:F2}  SP={r.VExcavSP:F2} m³");
                ed.WriteMessage(
                    $"\n   Bedding:    {r.VBedding:F2} m³");
                ed.WriteMessage(
                    $"\n   Surround:   {r.VSurround:F2} m³  (net, pipe void deducted)");
                ed.WriteMessage(
                    $"\n   Backfill:   KU={r.VBackfillKU:F2}  KL={r.VBackfillKL:F2}  SP={r.VBackfillSP:F2} m³");

                sumExcavKU += r.VExcavKU; sumExcavKL += r.VExcavKL; sumExcavSP += r.VExcavSP;
                sumBed     += r.VBedding; sumSurr    += r.VSurround;
                sumBfKU    += r.VBackfillKU; sumBfKL += r.VBackfillKL; sumBfSP += r.VBackfillSP;
            }

            ed.WriteMessage("\n\n── COMBINED TOTALS (all pipes summed) ────────────────────");
            ed.WriteMessage($"\n   Excavation: KU={sumExcavKU:F2}  KL={sumExcavKL:F2}  SP={sumExcavSP:F2} m³");
            ed.WriteMessage($"\n   Bedding:    {sumBed:F2} m³");
            ed.WriteMessage($"\n   Surround:   {sumSurr:F2} m³");
            ed.WriteMessage($"\n   Backfill:   KU={sumBfKU:F2}  KL={sumBfKL:F2}  SP={sumBfSP:F2} m³");

            ed.WriteMessage("\n\n── INVARIANT CHECK (KU + KL difference) ─────────────────");
            ed.WriteMessage($"\n   Δ Excavation (KU - KL): {sumExcavKU - sumExcavKL:F4} m³");
            ed.WriteMessage($"\n   Δ Backfill   (KU - KL): {sumBfKU - sumBfKL:F4} m³");
            ed.WriteMessage("\n   (Should be 0.0000)\n");
            ed.WriteMessage("\n══════════════════════════════════════════════════════════\n");
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
        // Phase 10b — Save to Excel
        // =====================================================================

        private static void SaveReport(
            Editor                        ed,
            List<SectionDebugRow>         rows,
            List<List<SimplifiedStation>> allStations,
            List<SimplifiedSectionResult> results)
        {
            try
            {
                string desktop   = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string path      = Path.Combine(desktop, $"BOQ_Simplified_{timestamp}.xlsx");

                using (var pkg = new ExcelPackage())
                {
                    // ══════════════════════════════════════════════════════════════
                    // Sheet 1 — Per-station detail
                    // ══════════════════════════════════════════════════════════════
                    {
                        var ws = pkg.Workbook.Worksheets.Add("Stations");

                        string[] hdr =
                        {
                            "Pipe Name",
                            "Station (m)", "Dist to Next (m)",
                            "Terrain Z (m)", "Invert Z (m)", "Depth (m)",
                            "Zone",
                            "Excav Gross (m2)", "Excav Deducted (m2)", "Excav SP (m2)",
                            "Bedding (m2)", "Surround (m2)",
                            "Backfill Gross (m2)", "Backfill Deducted (m2)", "Backfill SP (m2)"
                        };

                        for (int c = 0; c < hdr.Length; c++)
                        {
                            var cell = ws.Cells[1, c + 1];
                            cell.Value = hdr[c];
                            cell.Style.Font.Bold = true;
                            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            cell.Style.Fill.BackgroundColor.SetColor(
                                System.Drawing.Color.FromArgb(31, 78, 121));
                            cell.Style.Font.Color.SetColor(System.Drawing.Color.White);
                            cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        }

                        var pipeColours = new[]
                        {
                            System.Drawing.Color.FromArgb(255, 255, 255),
                            System.Drawing.Color.FromArgb(217, 225, 242)
                        };
                        var zoneColour = System.Drawing.Color.FromArgb(255, 230, 153);

                        int row = 2;
                        for (int pi = 0; pi < rows.Count; pi++)
                        {
                            var pipe     = rows[pi];
                            var stations = allStations[pi];
                            var bg       = pipeColours[pi % pipeColours.Length];

                            for (int si = 0; si < stations.Count; si++)
                            {
                                var st = stations[si];
                                if (st.ExcavPoly == null) continue;

                                double distNext = 0;
                                if (si < stations.Count - 1)
                                {
                                    var sn = stations[si + 1];
                                    distNext = Math.Sqrt(
                                        (sn.WorldX - st.WorldX) * (sn.WorldX - st.WorldX) +
                                        (sn.WorldY - st.WorldY) * (sn.WorldY - st.WorldY));
                                }
                                bool inZone = !double.IsNaN(st.OtherInvertZ);

                                ws.Cells[row,  1].Value = pipe.PipeName;
                                ws.Cells[row,  2].Value = st.StationDist;
                                ws.Cells[row,  3].Value = si < stations.Count - 1
                                    ? (object)distNext : null;
                                ws.Cells[row,  4].Value = st.TerrainZ;
                                ws.Cells[row,  5].Value = st.InvertZ;
                                ws.Cells[row,  6].Value = st.TrueDepth;
                                ws.Cells[row,  7].Value = inZone ? "Y" : "";
                                ws.Cells[row,  8].Value = st.AreaExcav;
                                ws.Cells[row,  9].Value = st.AreaExcavDeducted;
                                ws.Cells[row, 10].Value = st.AreaExcavSP;
                                ws.Cells[row, 11].Value = st.AreaBedding;
                                ws.Cells[row, 12].Value = st.AreaSurround;
                                ws.Cells[row, 13].Value = st.AreaBackfill;
                                ws.Cells[row, 14].Value = st.AreaBackfillDeducted;
                                ws.Cells[row, 15].Value = st.AreaBackfillSP;

                                for (int c = 2; c <= 6; c++)
                                    ws.Cells[row, c].Style.Numberformat.Format = "0.000";
                                for (int c = 8; c <= 15; c++)
                                    ws.Cells[row, c].Style.Numberformat.Format = "0.0000";

                                var rowBg = inZone ? zoneColour : bg;
                                for (int c = 1; c <= 15; c++)
                                {
                                    ws.Cells[row, c].Style.Fill.PatternType = ExcelFillStyle.Solid;
                                    ws.Cells[row, c].Style.Fill.BackgroundColor.SetColor(rowBg);
                                }
                                if (inZone)
                                    ws.Cells[row, 7].Style.Font.Bold = true;

                                row++;
                            }
                        }

                        ws.Column(1).Width  = 26;
                        ws.Column(2).Width  = 14;
                        ws.Column(3).Width  = 16;
                        ws.Column(4).Width  = 14;
                        ws.Column(5).Width  = 14;
                        ws.Column(6).Width  = 12;
                        ws.Column(7).Width  =  8;
                        for (int c = 8; c <= 15; c++)
                            ws.Column(c).Width = 19;

                        ws.View.FreezePanes(2, 2);
                    }

                    // ══════════════════════════════════════════════════════════════
                    // Sheet 2 — Volume summary per pipe
                    // ══════════════════════════════════════════════════════════════
                    {
                        var ws = pkg.Workbook.Worksheets.Add("Volumes");

                        string[] headers =
                        {
                            "Pipe Name", "Dia (mm)", "Material", "Length 2D (m)", "Segments",
                            "Excav KU (m3)", "Excav KL (m3)", "Excav SP (m3)",
                            "Bedding (m3)", "Surround (m3)",
                            "Backfill KU (m3)", "Backfill KL (m3)", "Backfill SP (m3)"
                        };

                        for (int c = 0; c < headers.Length; c++)
                        {
                            var cell = ws.Cells[1, c + 1];
                            cell.Value = headers[c];
                            cell.Style.Font.Bold = true;
                            cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                            cell.Style.Fill.BackgroundColor.SetColor(
                                System.Drawing.Color.FromArgb(68, 114, 196));
                            cell.Style.Font.Color.SetColor(System.Drawing.Color.White);
                            cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                        }

                        double sumExcavKU = 0, sumExcavKL = 0, sumExcavSP = 0;
                        double sumBed = 0, sumSurr = 0;
                        double sumBfKU = 0, sumBfKL = 0, sumBfSP = 0;

                        int row = 2;
                        foreach (var r in results)
                        {
                            ws.Cells[row,  1].Value = r.PipeName;
                            ws.Cells[row,  2].Value = r.DiameterMm;
                            ws.Cells[row,  3].Value = r.Material;
                            ws.Cells[row,  4].Value = r.Length2D;
                            ws.Cells[row,  5].Value = r.SegmentCount;
                            ws.Cells[row,  6].Value = r.VExcavKU;
                            ws.Cells[row,  7].Value = r.VExcavKL;
                            ws.Cells[row,  8].Value = r.VExcavSP;
                            ws.Cells[row,  9].Value = r.VBedding;
                            ws.Cells[row, 10].Value = r.VSurround;
                            ws.Cells[row, 11].Value = r.VBackfillKU;
                            ws.Cells[row, 12].Value = r.VBackfillKL;
                            ws.Cells[row, 13].Value = r.VBackfillSP;

                            ws.Cells[row, 4].Style.Numberformat.Format = "0.00";
                            for (int c = 6; c <= 13; c++)
                                ws.Cells[row, c].Style.Numberformat.Format = "0.00";

                            if (row % 2 == 0)
                            {
                                for (int c = 1; c <= 13; c++)
                                {
                                    ws.Cells[row, c].Style.Fill.PatternType = ExcelFillStyle.Solid;
                                    ws.Cells[row, c].Style.Fill.BackgroundColor.SetColor(
                                        System.Drawing.Color.FromArgb(242, 242, 242));
                                }
                            }

                            sumExcavKU += r.VExcavKU; sumExcavKL += r.VExcavKL;
                            sumExcavSP += r.VExcavSP;
                            sumBed     += r.VBedding;  sumSurr    += r.VSurround;
                            sumBfKU    += r.VBackfillKU; sumBfKL  += r.VBackfillKL;
                            sumBfSP    += r.VBackfillSP;
                            row++;
                        }

                        ws.Cells[row,  1].Value = "TOTAL";
                        ws.Cells[row,  6].Value = sumExcavKU;
                        ws.Cells[row,  7].Value = sumExcavKL;
                        ws.Cells[row,  8].Value = sumExcavSP;
                        ws.Cells[row,  9].Value = sumBed;
                        ws.Cells[row, 10].Value = sumSurr;
                        ws.Cells[row, 11].Value = sumBfKU;
                        ws.Cells[row, 12].Value = sumBfKL;
                        ws.Cells[row, 13].Value = sumBfSP;

                        for (int c = 1; c <= 13; c++)
                        {
                            ws.Cells[row, c].Style.Font.Bold = true;
                            ws.Cells[row, c].Style.Fill.PatternType = ExcelFillStyle.Solid;
                            ws.Cells[row, c].Style.Fill.BackgroundColor.SetColor(
                                System.Drawing.Color.FromArgb(221, 235, 247));
                            if (c >= 6)
                                ws.Cells[row, c].Style.Numberformat.Format = "0.00";
                        }

                        ws.Column(1).Width = 28;
                        ws.Column(2).Width = 10;
                        ws.Column(3).Width = 14;
                        ws.Column(4).Width = 15;
                        ws.Column(5).Width = 10;
                        for (int c = 6; c <= 13; c++)
                            ws.Column(c).Width = 16;

                        ws.View.FreezePanes(2, 1);
                    }

                    pkg.SaveAs(new FileInfo(path));
                }

                ed.WriteMessage($"\n[SimplifiedBoQ] Excel saved -> {path}\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[SimplifiedBoQ] Excel save failed: {ex.Message}\n");
            }
        }

        private static double PolyArea(List<double[]> poly)
        {
            if (poly == null || poly.Count < 3) return 0;
            double s = 0;
            int n = poly.Count;
            for (int i = 0; i < n; i++)
            {
                var a = poly[i]; var b = poly[(i + 1) % n];
                s += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(s) * 0.5;
        }

        // =====================================================================
        // Internal data models
        // =====================================================================

        private sealed class SimplifiedStation
        {
            public double StationDist;

            // Interpolated world position and elevations
            public double WorldX, WorldY, TerrainZ, InvertZ, TrueDepth, HwExcav;

            // Gross polygons in local (U, Z) frame — U=0 is this pipe's centerline
            public List<double[]> ExcavPoly, BeddingPoly, SurroundPoly, BackfillPoly;

            // Gross cross-section areas — derived from polygon coordinates via ClipperGeo.Area.
            public double AreaExcav, AreaBedding, AreaSurround, AreaBackfill;

            // Net area when this pipe loses all overlap:
            //   ClipperGeo.Area(Difference(this_poly, other_poly))
            // The result is the actual polygon shape — may be triangle, pentagon, etc.
            // Initialized to gross in BuildGrossProfiles; updated at intersection stations.
            public double AreaExcavDeducted;
            public double AreaBackfillDeducted;

            // Net area for 50/50 split: gross - overlap/2 (arithmetic, no Clipper polygon).
            // Initialized to gross in BuildGrossProfiles; updated at intersection stations.
            public double AreaExcavSP;
            public double AreaBackfillSP;

            // Invert elevation of the crossing pipe (NaN = outside intersection zone).
            public double OtherInvertZ = double.NaN;
        }

        private sealed class SimplifiedSectionResult
        {
            public string PipeName;
            public int    DiameterMm;
            public string Material;
            public double Length2D;
            public int    SegmentCount;

            public double VExcavKU,    VExcavKL,    VExcavSP;
            public double VBedding,    VSurround;
            public double VBackfillKU, VBackfillKL, VBackfillSP;
        }
    }
}
