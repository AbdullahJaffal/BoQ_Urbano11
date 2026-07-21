using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Phase 3 orchestrator for the "Pre-calculate and Cache" BoQ engine.
    ///
    /// For every pipe station it resolves the four trench layers under ALL THREE
    /// preference scenarios (Keep Upper / Keep Lower / 50-50 Split) and caches the
    /// final polygon coordinates + net area into the station's three
    /// <see cref="ScenarioProfile"/> slots. Downstream commands never re-run Clipper.
    ///
    /// Frame: each pipe A owns a local (U, Z) perpendicular frame (U = 0 = axis).
    /// A neighbouring pipe B is projected into A's frame per station via the affine
    /// map  U_A = uBc + V·proj , where V is B's own cross-section offset, proj =
    /// T_A·T_B, and uBc is B's centreline offset in A's frame. For near-perpendicular
    /// pipes (proj ≈ 0) B's trench is treated as a full-width block over A's section
    /// when A's centreline falls within B's trench — this is the crossing-pipe case
    /// that a naive projection collapses to a zero-width sliver.
    ///
    /// Resolution model (all expressed as Difference against a union → automatic
    /// chunking for multi-layer / multi-pipe clashes):
    ///   net(A, L) = gross(A, L)
    ///               − A's own pipe body            (structural layers only)
    ///               − Union(stronger occupants)    (cross-pipe bodies + higher-rank layers)
    ///               − Union(same-type surrenders)  (per scenario; Phase 3.5 rules)
    /// Excavation is an isolated track: it clashes only with other Excavation and is
    /// never touched by the pipe body or the structural layers.
    /// </summary>
    public static class BoQOverlapResolver
    {
        private const double PerpEps          = 1e-4;   // |proj| below this → perpendicular crossing
        private const int    PipeSegs         = 32;     // pipe-body N-gon resolution
        // 1 µm nudge: at Scale=1e8 this is 100 integer units — more than enough to
        // separate exactly collinear edges without measurably changing any area (error
        // ≈ 1e-6 × trench_height ≈ 3e-6 m² << 1e-4 harness tolerance).
        private const double CollinearBreakDu = 1e-6;

        private static readonly TiePreference[] Scenarios =
            { TiePreference.KeepUpper, TiePreference.KeepLower, TiePreference.Split };

        private static readonly TrenchLayerType[] Layers =
            { TrenchLayerType.Excavation, TrenchLayerType.Bedding,
              TrenchLayerType.Surround,   TrenchLayerType.Backfill };

        // =====================================================================
        // Per-pipe precomputed frame
        // =====================================================================

        private sealed class Frame
        {
            public double Tx, Ty;     // unit axis direction
            public double Nx, Ny;     // unit left-normal
            public double MinX, MaxX, MinY, MaxY;   // footprint AABB (with half-width pad)
            public double AvgInvert;  // (InvertStart + InvertEnd)/2  → upper/lower test
        }

        // =====================================================================
        // Uniform spatial grid over the footprint AABBs
        // =====================================================================

        /// <summary>
        /// A uniform-cell spatial hash over the padded footprint AABBs. Each row is
        /// registered in every cell its AABB covers, so <see cref="Candidates"/>
        /// returns a superset of the rows whose AABB overlaps a query row's AABB.
        /// <para>
        /// Correctness: if two AABBs overlap, their intersection contains a point, and
        /// the grid cell holding that point is covered by BOTH AABBs → both rows are
        /// registered in it → they are mutual candidates. The caller still applies the
        /// exact <see cref="AabbOverlap"/> test, so no overlapping pair is ever missed
        /// and no false positive survives. This replaces the O(n²) all-pairs scan with
        /// a near-linear neighbour query without changing a single resolved pair.
        /// </para>
        /// </summary>
        private sealed class SpatialGrid
        {
            private readonly Frame[]     _frames;
            private readonly List<int>[] _cells;
            private readonly double      _minX, _minY, _cell;
            private readonly int         _cols, _rows;

            public SpatialGrid(Frame[] frames)
            {
                _frames = frames;
                int n = frames.Length;

                double minX = double.MaxValue, minY = double.MaxValue;
                double maxX = double.MinValue, maxY = double.MinValue;
                double sumSpan = 0;
                for (int i = 0; i < n; i++)
                {
                    var f = frames[i];
                    if (f.MinX < minX) minX = f.MinX;
                    if (f.MinY < minY) minY = f.MinY;
                    if (f.MaxX > maxX) maxX = f.MaxX;
                    if (f.MaxY > maxY) maxY = f.MaxY;
                    sumSpan += (f.MaxX - f.MinX) + (f.MaxY - f.MinY);
                }
                double gw = Math.Max(0, maxX - minX);
                double gh = Math.Max(0, maxY - minY);

                // Cell ≈ the average AABB half-perimeter (a typical pipe extent), so a
                // short manhole-to-manhole segment spans O(1) cells.
                double cell = n > 0 ? sumSpan / (2.0 * n) : 0;
                if (cell < 1e-6) cell = Math.Max(Math.Max(gw, gh), 1.0);

                // Keep the total cell count bounded regardless of network span.
                long cap = Math.Max(4096L, (long)n * 8);
                for (int guard = 0; guard < 64; guard++)
                {
                    long c = (long)(gw / cell) + 1;
                    long r = (long)(gh / cell) + 1;
                    if (c * r <= cap) break;
                    cell *= 1.5;
                }

                _minX = minX; _minY = minY; _cell = cell;
                _cols = (int)(gw / cell) + 1;
                _rows = (int)(gh / cell) + 1;
                _cells = new List<int>[_cols * _rows];

                for (int i = 0; i < n; i++)
                {
                    var f = frames[i];
                    int cx0 = Col(f.MinX), cx1 = Col(f.MaxX);
                    int cy0 = Row(f.MinY), cy1 = Row(f.MaxY);
                    for (int cy = cy0; cy <= cy1; cy++)
                        for (int cx = cx0; cx <= cx1; cx++)
                        {
                            int idx = cy * _cols + cx;
                            (_cells[idx] ?? (_cells[idx] = new List<int>())).Add(i);
                        }
                }
            }

            private int Col(double x)
            {
                int c = (int)((x - _minX) / _cell);
                return c < 0 ? 0 : (c >= _cols ? _cols - 1 : c);
            }

            private int Row(double y)
            {
                int r = (int)((y - _minY) / _cell);
                return r < 0 ? 0 : (r >= _rows ? _rows - 1 : r);
            }

            /// <summary>
            /// Row indices sharing at least one cell with <paramref name="i"/>'s AABB
            /// (deduplicated, excluding <paramref name="i"/> itself).
            /// </summary>
            public List<int> Candidates(int i)
            {
                var f = _frames[i];
                var seen   = new HashSet<int>();
                var result = new List<int>();
                int cx0 = Col(f.MinX), cx1 = Col(f.MaxX);
                int cy0 = Row(f.MinY), cy1 = Row(f.MaxY);
                for (int cy = cy0; cy <= cy1; cy++)
                    for (int cx = cx0; cx <= cx1; cx++)
                    {
                        var bucket = _cells[cy * _cols + cx];
                        if (bucket == null) continue;
                        foreach (int j in bucket)
                            if (j != i && seen.Add(j)) result.Add(j);
                    }
                return result;
            }
        }

        // =====================================================================
        // Entry point
        // =====================================================================

        /// <summary>
        /// Resolves and caches the three scenario profiles for every station of
        /// every row. Assumes each row's <c>Stations</c> already carry the gross
        /// layer rings (ExcavPoly/BeddingPoly/SurroundPoly/BackfillPoly) produced by
        /// <c>BoQParserService.ComputeStations</c>.
        /// </summary>
        /// <summary>
        /// Warnings raised during the last <see cref="Resolve"/> — currently only
        /// "two pipes occupy exactly the same location". The caller (BoQ command)
        /// flushes these to the editor after resolving. Cleared on each Resolve.
        /// </summary>
        public static readonly List<string> CoincidentPipeWarnings = new List<string>();

        public static void Resolve(List<SectionDebugRow> rows)
        {
            if (rows == null || rows.Count == 0) return;

            CoincidentPipeWarnings.Clear();

            var frames = new Frame[rows.Count];
            for (int i = 0; i < rows.Count; i++)
                frames[i] = BuildFrame(rows[i]);

            // Spatial grid replaces the O(n²) all-pairs scan with a neighbour query.
            // The exact AabbOverlap gate below still runs, so every resolved pair — and
            // therefore every volume downstream — is identical to the brute-force scan.
            var grid = new SpatialGrid(frames);

            for (int ai = 0; ai < rows.Count; ai++)
            {
                var rowA = rows[ai];
                if (rowA.Stations == null) continue;
                var fa = frames[ai];

                // Candidate neighbours are whole-pipe (station-independent): resolve
                // them ONCE per pipe instead of re-scanning at every station. Pipes
                // meeting at the same manhole (SharesNode) are connected — their
                // trenches overlap at the junction but that is NOT a real clash to
                // deduct, so they are excluded here.
                var neigh = new List<int>();
                foreach (int bi in grid.Candidates(ai))
                {
                    if (SharesNode(rowA, rows[bi])) continue;
                    if (!AabbOverlap(fa, frames[bi])) continue;
                    neigh.Add(bi);
                }

                foreach (var sA in rowA.Stations)
                    ResolveStation(rowA, fa, sA, rows, frames, ai, neigh);
            }
        }

        /// <summary>
        /// Test-only equivalence check: verifies the spatial-grid candidate query
        /// returns EXACTLY the same AABB-overlapping neighbours as a brute-force
        /// all-pairs scan, for every row. Returns <c>null</c> on success or a
        /// human-readable description of the first mismatch. Used by the geometry
        /// harness to guarantee the grid never drops (or invents) a resolved pair.
        /// </summary>
        public static string VerifyGridEquivalence(List<SectionDebugRow> rows)
        {
            if (rows == null || rows.Count == 0) return null;

            var frames = new Frame[rows.Count];
            for (int i = 0; i < rows.Count; i++) frames[i] = BuildFrame(rows[i]);
            var grid = new SpatialGrid(frames);

            for (int ai = 0; ai < rows.Count; ai++)
            {
                var brute = new HashSet<int>();
                for (int bi = 0; bi < rows.Count; bi++)
                    if (bi != ai && AabbOverlap(frames[ai], frames[bi])) brute.Add(bi);

                var viaGrid = new HashSet<int>();
                foreach (int bi in grid.Candidates(ai))
                    if (AabbOverlap(frames[ai], frames[bi])) viaGrid.Add(bi);

                if (!brute.SetEquals(viaGrid))
                    return $"row {ai}: brute-force {brute.Count} vs grid {viaGrid.Count} AABB neighbours differ";
            }
            return null;
        }

        // =====================================================================
        // Crossing-band descriptor  (produced by ComputeInjections)
        // =====================================================================

        /// <summary>
        /// Records one crossing pair: the two row indices and the exact chainage
        /// bounds of the overlap zone on each pipe's axis.
        /// Produced once by <see cref="ComputeInjections"/> and reused by
        /// <see cref="ApplyExcavationAveraging"/> so that crossing detection is
        /// never repeated — eliminating any re-detection failure modes.
        /// </summary>
        public struct CrossingBand
        {
            public int    Ai, Bi;
            public double AMin, AMax, BMin, BMax;
        }

        // =====================================================================
        // Integration-averaging pass
        // =====================================================================

        /// <summary>
        /// Eliminates the integration-axis asymmetry between crossing pipe pairs.
        ///
        /// The trapezoidal rule integrating the same 3-D intersection volume along
        /// Pipe A's axis slices and Pipe B's axis slices yields two slightly different
        /// floating-point numbers (Cavalieri holds in the limit, not at finite h).
        /// This difference makes KeepUpper and KeepLower produce different totals.
        ///
        /// Fix: for each crossing band (already identified by ComputeInjections) we
        /// integrate both perspectives from the cached per-station ScenarioProfile
        /// areas, average them into one TrueIntersectionVol, and store per-section
        /// corrections (<see cref="SectionDebugRow.ExcavAvgAdjKU/KL/SP"/>) that
        /// <see cref="BoQScenarioAggregator.RecomputeRow"/> adds to the raw integrals.
        ///
        /// Passing the pre-computed <paramref name="bands"/> list (from
        /// <see cref="ComputeInjections"/>) instead of re-detecting crossings from
        /// footprints is the key reliability improvement: we reuse the EXACT same
        /// chainage bounds that drove station injection, with zero failure paths.
        ///
        /// Must be called AFTER <see cref="Resolve"/> and BEFORE
        /// <see cref="BoQScenarioAggregator.RecomputeRow"/>.
        /// </summary>
        public static void ApplyExcavationAveraging(
            List<SectionDebugRow> rows, List<CrossingBand> bands)
        {
            foreach (var r in rows)
            { r.ExcavAvgAdjKU = 0; r.ExcavAvgAdjKL = 0; r.ExcavAvgAdjSP = 0; }

            if (bands == null || bands.Count == 0) return;

            foreach (var band in bands)
            {
                var rowA = rows[band.Ai];
                var rowB = rows[band.Bi];
                if (rowA.Stations == null || rowB.Stations == null) continue;

                // Integrate the FULL excavation volume under each scenario for both
                // pipes — the same trapezoidal rule RecomputeRow uses.  No derived
                // intersection-area computation: we use the actual NetArea values
                // written by Resolve, so the result is guaranteed non-zero whenever
                // a real deduction exists.
                double grossA = RawExcav(rowA, TiePreference.Ignore);
                double grossB = RawExcav(rowB, TiePreference.Ignore);
                double kuA    = RawExcav(rowA, TiePreference.KeepUpper);
                double klA    = RawExcav(rowA, TiePreference.KeepLower);
                double spA    = RawExcav(rowA, TiePreference.Split);
                double kuB    = RawExcav(rowB, TiePreference.KeepUpper);
                double klB    = RawExcav(rowB, TiePreference.KeepLower);
                double spB    = RawExcav(rowB, TiePreference.Split);

                // Deduction each pipe carries in each scenario (≥ 0 always).
                double dKuA = grossA - kuA;   // A deducted in KU  (A is lower)
                double dKlA = grossA - klA;   // A deducted in KL  (A is upper)
                double dSpA = grossA - spA;
                double dKuB = grossB - kuB;   // B deducted in KU  (B is lower)
                double dKlB = grossB - klB;   // B deducted in KL  (B is upper)
                double dSpB = grossB - spB;

                // Guard: if neither pipe carries a meaningful deduction in any
                // scenario this band is degenerate — skip it.
                if (dKuA < 1e-9 && dKuB < 1e-9 && dKlA < 1e-9 && dKlB < 1e-9) continue;

                // Identify which pipe is lower: the lower pipe carries the KU deduction.
                // (In KeepUpper the shallower/upper pipe wins → the deeper/lower pipe
                //  is deducted.  In KeepLower the roles reverse.)
                bool aIsLower = dKuA >= dKuB;

                if (aIsLower)
                {
                    // A loses in KU (dKuA ≈ intersection from A's axis)
                    // B loses in KL (dKlB ≈ intersection from B's axis)
                    double trueKU = (dKuA + dKlB) * 0.5;
                    rowA.ExcavAvgAdjKU += (dKuA - trueKU);   // (dKuA − dKlB) / 2
                    rowB.ExcavAvgAdjKL += (dKlB - trueKU);   // (dKlB − dKuA) / 2
                }
                else
                {
                    // B loses in KU (dKuB ≈ intersection from B's axis)
                    // A loses in KL (dKlA ≈ intersection from A's axis)
                    double trueKU = (dKuB + dKlA) * 0.5;
                    rowB.ExcavAvgAdjKU += (dKuB - trueKU);
                    rowA.ExcavAvgAdjKL += (dKlA - trueKU);
                }

                // Split: each pipe deducts the average of both perspectives' half-shares.
                double trueSP = (dSpA + dSpB) * 0.5;
                rowA.ExcavAvgAdjSP += (dSpA - trueSP);
                rowB.ExcavAvgAdjSP += (dSpB - trueSP);
            }
        }

        // Trapezoidal integral of the excavation net area under a given scenario,
        // using the same station grid as RecomputeRow.
        private static double RawExcav(SectionDebugRow sdr, TiePreference kazi)
        {
            var st = sdr.Stations;
            if (st == null || st.Count < 2) return 0;
            double v = 0;
            for (int i = 0; i < st.Count - 1; i++)
            {
                double rdx = st[i + 1].WorldX - st[i].WorldX;
                double rdy = st[i + 1].WorldY - st[i].WorldY;
                double d   = Math.Sqrt(rdx * rdx + rdy * rdy);
                if (d <= 1e-9) continue;
                v += (ExcavNetArea(st[i], kazi) + ExcavNetArea(st[i + 1], kazi)) * 0.5 * d;
            }
            return v;
        }

        private static double ExcavNetArea(CrossSectionStation s, TiePreference kazi)
        {
            if (kazi == TiePreference.Ignore) return s.AreaExcav;
            var prof = s.Scenario(kazi);
            return prof != null ? prof.Excavation.NetArea : s.AreaExcav;
        }

        // =====================================================================
        // Phase 1/2 — collision-station injection
        // =====================================================================

        /// <summary>
        /// For every pair of pipes whose plan-view trench footprints overlap (and
        /// that do NOT share a manhole), returns the collision start/end chainages
        /// projected onto each pipe's centreline.
        /// <para>
        /// <c>allForced[i]</c> — every chainage to inject into rows[i] as a forced
        /// station (boundaries + interior cross-projected grid stations).
        /// </para>
        /// <para>
        /// <c>boundaries[i]</c> — only the aMin/aMax entries for rows[i]: the exact
        /// chainages where a crossing zone begins or ends on that pipe. These are
        /// the stations that should be flagged <see cref="CrossSectionStation.IsCrossingBoundary"/>.
        /// </para>
        /// </summary>
        public static (List<double>[] allForced, List<double>[] boundaries, List<CrossingBand> bands)
            ComputeInjections(List<SectionDebugRow> rows)
        {
            int n = rows?.Count ?? 0;
            var extras     = new List<double>[n];
            var boundaries = new List<double>[n];
            var bands      = new List<CrossingBand>();
            for (int i = 0; i < n; i++)
            {
                extras[i]     = new List<double>();
                boundaries[i] = new List<double>();
            }
            if (n < 2) return (extras, boundaries, bands);

            var frames = new Frame[n];
            var foot   = new List<List<double[]>>[n];
            for (int i = 0; i < n; i++)
            {
                frames[i] = BuildFrame(rows[i]);
                var ring  = Footprint(rows[i], frames[i]);
                foot[i]   = ring != null ? new List<List<double[]>> { ring } : null;
            }

            var grid = new SpatialGrid(frames);
            for (int ai = 0; ai < n; ai++)
            {
                if (foot[ai] == null) continue;
                foreach (int bi in grid.Candidates(ai))
                {
                    if (bi <= ai) continue;   // each unordered pair once (from lower index)
                    if (foot[bi] == null) continue;
                    if (SharesNode(rows[ai], rows[bi])) continue;
                    if (!AabbOverlap(frames[ai], frames[bi])) continue;

                    var inter = ClipperGeo.Intersect(foot[ai], foot[bi]);
                    if (inter.Count == 0) continue;

                    double aMin = double.MaxValue, aMax = double.MinValue;
                    double bMin = double.MaxValue, bMax = double.MinValue;
                    foreach (var ring in inter)
                        foreach (var v in ring)
                        {
                            double tA = Chainage(rows[ai], frames[ai], v[0], v[1]);
                            double tB = Chainage(rows[bi], frames[bi], v[0], v[1]);
                            if (tA < aMin) aMin = tA; if (tA > aMax) aMax = tA;
                            if (tB < bMin) bMin = tB; if (tB > bMax) bMax = tB;
                        }
                    if (aMin >= aMax - 1e-9 || bMin >= bMax - 1e-9) continue;

                    // Record this crossing pair with exact chainage bounds so that
                    // ApplyExcavationAveraging can reuse them without re-detecting.
                    bands.Add(new CrossingBand
                        { Ai = ai, Bi = bi, AMin = aMin, AMax = aMax, BMin = bMin, BMax = bMax });

                    // Boundary stations — flagged as IsCrossingBoundary on each pipe.
                    extras[ai].Add(aMin); extras[ai].Add(aMax);
                    extras[bi].Add(bMin); extras[bi].Add(bMax);
                    boundaries[ai].Add(aMin); boundaries[ai].Add(aMax);
                    boundaries[bi].Add(bMin); boundaries[bi].Add(bMax);

                    // Interior cross-projected grid stations (not boundary-flagged).
                    // 0.1 m density inside the crossing zone ensures the trapezoidal
                    // rule integrates the varying overlap cross-section with sufficient
                    // precision for volumetric conservation across all scenarios.
                    const double GridInterval = 0.1;
                    for (double gA = Math.Ceiling(aMin / GridInterval) * GridInterval;
                         gA < aMax - 1e-9; gA += GridInterval)
                    {
                        if (gA <= aMin + 1e-9) continue;
                        double xP = rows[ai].StartX + gA * frames[ai].Tx;
                        double yP = rows[ai].StartY + gA * frames[ai].Ty;
                        extras[bi].Add(Chainage(rows[bi], frames[bi], xP, yP));
                    }
                    for (double gB = Math.Ceiling(bMin / GridInterval) * GridInterval;
                         gB < bMax - 1e-9; gB += GridInterval)
                    {
                        if (gB <= bMin + 1e-9) continue;
                        double xP = rows[bi].StartX + gB * frames[bi].Tx;
                        double yP = rows[bi].StartY + gB * frames[bi].Ty;
                        extras[ai].Add(Chainage(rows[ai], frames[ai], xP, yP));
                    }
                }
            }
            return (extras, boundaries, bands);
        }

        /// <summary>Plan-view trench-top footprint ring (world XY) for a section.</summary>
        private static List<double[]> Footprint(SectionDebugRow r, Frame f)
        {
            if (r.Length2D < 1e-9) return null;
            double hwS = r.TopWidthExcavS * 0.5, hwE = r.TopWidthExcavE * 0.5;
            return new List<double[]>
            {
                new[] { r.StartX - f.Nx * hwS, r.StartY - f.Ny * hwS },
                new[] { r.EndX   - f.Nx * hwE, r.EndY   - f.Ny * hwE },
                new[] { r.EndX   + f.Nx * hwE, r.EndY   + f.Ny * hwE },
                new[] { r.StartX + f.Nx * hwS, r.StartY + f.Ny * hwS },
            };
        }

        /// <summary>Longitudinal distance of a world (x,y) along a pipe axis, clamped to [0, L].</summary>
        private static double Chainage(SectionDebugRow r, Frame f, double x, double y)
        {
            double t = (x - r.StartX) * f.Tx + (y - r.StartY) * f.Ty;
            return Math.Max(0, Math.Min(r.Length2D, t));
        }


        // =====================================================================
        // One station of pipe A
        // =====================================================================

        private static void ResolveStation(
            SectionDebugRow rowA, Frame fa, CrossSectionStation sA,
            List<SectionDebugRow> rows, Frame[] frames, int ai, List<int> neigh)
        {
            // Gross rings for this station (already computed).
            var gross = new Dictionary<TrenchLayerType, List<double[]>>
            {
                [TrenchLayerType.Excavation] = sA.ExcavPoly,
                [TrenchLayerType.Bedding]    = sA.BeddingPoly,
                [TrenchLayerType.Surround]   = sA.SurroundPoly,
                [TrenchLayerType.Backfill]   = sA.BackfillPoly,
            };

            // A's own pipe body (for the absolute-priority self deduction).
            var bodyA = ClipperGeo.PipeBody(rowA.PipeOuterDiamM, sA.InvertZ, PipeSegs);
            sA.PipeBodyPoly = bodyA;

            double uMinA = gross[TrenchLayerType.Excavation].Min(v => v[0]);
            double uMaxA = gross[TrenchLayerType.Excavation].Max(v => v[0]);

            // Accumulators.
            var stronger = Layers.ToDictionary(L => L, L => new List<List<double[]>>());
            var surrender = Scenarios.ToDictionary(
                p => p, p => Layers.ToDictionary(L => L, L => new List<List<double[]>>()));

            // ── Gather clashes from the pre-filtered neighbour set ────────────
            // `neigh` (built once per pipe in Resolve) already excludes self,
            // manhole-connected pipes, and rows whose footprint AABB cannot overlap A.
            // The real per-station work is the geometric projection below.
            foreach (int bi in neigh)
            {
                var rowB = rows[bi];
                var fb   = frames[bi];

                var nb = ProjectNeighbour(rowA, fa, sA, rowB, fb, uMinA, uMaxA);
                if (nb == null) continue;   // no real overlap at this station

                // Upper/lower is decided PER STATION (not by a whole-pipe average):
                // crossing pipes with different slopes can swap vertical order inside
                // the overlap zone, so each cross-section must use its own invert.
                bool aIsLower = DetermineAIsLower(rowA, sA, ai, rowB, nb, bi);

                foreach (var L in Layers)
                {
                    // Excavation only ever clashes with excavation.
                    // Both pipes independently forward-project the other's geometry
                    // into their own frame and compute the net area. Synchronized
                    // 0.1 m stations in the crossing zone ensure the trapezoidal
                    // integrals converge to the same 3-D intersection volume from
                    // either pipe's perspective (Cavalieri invariant).
                    if (L == TrenchLayerType.Excavation)
                    {
                        AccumulateSameType(surrender, gross[L], nb.Layer[L], L,
                                           aIsLower, nb.CentreU);
                        continue;
                    }

                    // Structural layer: pipe body of B is absolute and always deducts.
                    if (nb.Body != null)
                        stronger[L].Add(nb.Body);

                    foreach (var M in Layers)
                    {
                        if (M == TrenchLayerType.Excavation) continue;  // excav never deducts structural
                        int rL = TrenchLayerPriority.Rank(L);
                        int rM = TrenchLayerPriority.Rank(M);
                        if (rM > rL)
                            stronger[L].Add(nb.Layer[M]);               // stronger neighbour layer
                        else if (rM == rL)
                            AccumulateSameType(surrender, gross[L], nb.Layer[M], L,
                                               aIsLower, nb.CentreU);
                        // rM < rL → A is stronger → A keeps; B loses in B's own pass.
                    }
                }
            }

            // ── Finalise: build the cached scenario profiles ──────────────────
            // The three scenarios differ ONLY through the per-preference `surrender`
            // term; `gross`, `bodyA` and `stronger` are preference-independent. So
            // when no same-type surrender exists at this station — the common case (no
            // clash, or a clash that only involves stronger/pipe-body deductions) — all
            // three profiles are identical: build ONE and share it, skipping two
            // redundant Clipper passes per station. Stations inside a real crossing
            // (surrender non-empty) still get three independent profiles, so instant
            // scenario-switching downstream is unaffected and every net area is
            // byte-identical to computing all three unconditionally.
            bool surrenderDiverges = false;
            foreach (var p in Scenarios)
            {
                foreach (var L in Layers)
                    if (surrender[p][L].Count > 0) { surrenderDiverges = true; break; }
                if (surrenderDiverges) break;
            }

            sA.HasOverlap = stronger.Values.Any(r => r.Count > 0) || surrenderDiverges;

            if (!surrenderDiverges)
            {
                var shared = BuildProfile(TiePreference.KeepUpper, gross, stronger, surrender, bodyA);
                sA.ScenarioKeepUpper = shared;
                sA.ScenarioKeepLower = shared;
                sA.ScenarioSplit     = shared;
            }
            else
            {
                sA.ScenarioKeepUpper = BuildProfile(TiePreference.KeepUpper, gross, stronger, surrender, bodyA);
                sA.ScenarioKeepLower = BuildProfile(TiePreference.KeepLower, gross, stronger, surrender, bodyA);
                sA.ScenarioSplit     = BuildProfile(TiePreference.Split,     gross, stronger, surrender, bodyA);
            }
        }

        /// <summary>
        /// Builds one scenario profile for a station:
        ///   net(L) = gross(L) − pipe body − Union(stronger) − Union(surrender[p]).
        /// Extracted from the finalise step so identical scenarios (no per-preference
        /// surrender) can be computed once and shared across all three slots.
        /// </summary>
        private static ScenarioProfile BuildProfile(
            TiePreference p,
            Dictionary<TrenchLayerType, List<double[]>> gross,
            Dictionary<TrenchLayerType, List<List<double[]>>> stronger,
            Dictionary<TiePreference, Dictionary<TrenchLayerType, List<List<double[]>>>> surrender,
            List<double[]> bodyA)
        {
            var prof = new ScenarioProfile { Preference = p };
            foreach (var L in Layers)
            {
                List<List<double[]>> net = new List<List<double[]>> { gross[L] };

                if (TrenchLayerPriority.PipeBodyDeducts(L) && bodyA != null)
                    net = ClipperGeo.Difference(net, new List<List<double[]>> { bodyA });

                if (stronger[L].Count > 0)
                    net = ClipperGeo.Difference(net, ClipperGeo.Union(stronger[L]));

                if (surrender[p][L].Count > 0)
                    net = ClipperGeo.Difference(net, ClipperGeo.Union(surrender[p][L]));

                var slot = prof.Layer(L);
                slot.Polygon = net;
                slot.NetArea = ClipperGeo.Area(net);
            }
            return prof;
        }

        // =====================================================================
        // Per-station upper/lower decision (replaces the whole-pipe average).
        // =====================================================================

        /// <summary>
        /// Decides whether pipe A is the LOWER (deeper) pipe at THIS station, using
        /// a deterministic cascade that is symmetric between A's and B's passes
        /// (so the deducted A+B volume is conserved):
        ///
        ///   1. Station invert: A's <c>InvertZ</c> vs B's invert at the matching
        ///      point. The smaller (deeper) invert is the lower pipe.
        ///   2. Exact tie → plan coordinates of the two pipes (start+end): the pipe
        ///      further to the RIGHT (larger X) is lower; on equal X the smaller Y
        ///      is lower.
        ///   3. Pipes coincide exactly → raise a user warning and fall back to a
        ///      stable index order so the calculation never fails.
        /// </summary>
        private static bool DetermineAIsLower(
            SectionDebugRow rowA, CrossSectionStation sA, int ai,
            SectionDebugRow rowB, Neighbour nb, int bi)
        {
            const double eps = 1e-9;

            // ── 1. Per-station invert ─────────────────────────────────────────
            double invA = sA.InvertZ;
            double tB   = rowB.Length2D > 1e-9
                ? Math.Max(0.0, Math.Min(1.0, nb.CrossChainageOnNeighbour / rowB.Length2D))
                : 0.0;
            double invB = rowB.InvertStart + (rowB.InvertEnd - rowB.InvertStart) * tB;

            if (invA < invB - eps) return true;    // A deeper → A lower
            if (invA > invB + eps) return false;   // A shallower → A upper

            // ── 2. Plan-coordinate tie-break (start+end of each pipe) ─────────
            // Larger X ⇒ further right ⇒ lower.  Equal X ⇒ smaller Y ⇒ lower.
            double ax = rowA.StartX + rowA.EndX, bx = rowB.StartX + rowB.EndX;
            if (ax > bx + eps) return true;
            if (ax < bx - eps) return false;
            double ay = rowA.StartY + rowA.EndY, by = rowB.StartY + rowB.EndY;
            if (ay < by - eps) return true;
            if (ay > by + eps) return false;

            // ── 3. Pipes occupy the same line → warn, then decide deterministically.
            if (PipesCoincide(rowA, rowB))
                RecordCoincidentPipeWarning(rowA, rowB, sA);
            return ai < bi;
        }

        private static bool PipesCoincide(SectionDebugRow a, SectionDebugRow b)
        {
            const double t = 1e-6;
            bool Near(double u, double v) => Math.Abs(u - v) <= t;
            bool fwd = Near(a.StartX, b.StartX) && Near(a.StartY, b.StartY)
                    && Near(a.EndX,   b.EndX)   && Near(a.EndY,   b.EndY);
            bool rev = Near(a.StartX, b.EndX)   && Near(a.StartY, b.EndY)
                    && Near(a.EndX,   b.StartX) && Near(a.EndY,   b.StartY);
            return fwd || rev;
        }

        private static void RecordCoincidentPipeWarning(
            SectionDebugRow a, SectionDebugRow b, CrossSectionStation s)
        {
            string msg =
                $"İKİ BORU AYNI KONUMDA: {a.StartNodeName}→{a.EndNodeName} ile " +
                $"{b.StartNodeName}→{b.EndNodeName}  (X={s.WorldX:F2}, Y={s.WorldY:F2})";
            if (!CoincidentPipeWarnings.Contains(msg))
                CoincidentPipeWarnings.Add(msg);
        }

        private static void AccumulateSameType(
            Dictionary<TiePreference, Dictionary<TrenchLayerType, List<List<double[]>>>> surrender,
            List<double[]> selfRing, List<double[]> otherRing, TrenchLayerType L,
            bool aIsLower, double otherCentreU)
        {
            if (selfRing == null || otherRing == null) return;

            // Nudge the other ring's U coordinates by CollinearBreakDu before handing
            // it to Clipper. When both trenches have identical slopes (same TrBedHeight
            // + SlopeRatio) their lateral edges are EXACTLY collinear in integer space.
            // Clipper can produce degenerate zero-area output or backward spurs on
            // a perfectly shared edge. The 0.1 mm nudge breaks exact collinearity while
            // being geometrically invisible at engineering precision.
            var nudgedOther = NudgeU(otherRing);

            foreach (var p in Scenarios)
            {
                var ceded = ClipperGeo.SurrenderRegion(
                    selfRing, nudgedOther, p, aIsLower, otherCentreU);
                if (ceded.Count > 0) surrender[p][L].AddRange(ceded);
            }
        }

        /// <summary>
        /// Returns a copy of <paramref name="ring"/> with every U coordinate shifted by
        /// <see cref="CollinearBreakDu"/>. Used to break exact edge collinearity before
        /// same-type Clipper boolean operations.
        /// </summary>
        private static List<double[]> NudgeU(List<double[]> ring)
        {
            var r = new List<double[]>(ring.Count);
            foreach (var v in ring) r.Add(new[] { v[0] + CollinearBreakDu, v[1] });
            return r;
        }

        // =====================================================================
        // Neighbour projection
        // =====================================================================

        private sealed class Neighbour
        {
            public double CentreU;                                  // B's centreline U in A's frame
            public double CrossChainageOnNeighbour;                 // chainage on B at A's cross-section plane
            public double ProjDotProduct;                           // T_A · T_B (signed)
            public bool   IsPerp;                                   // true when |proj| < PerpEps
            public Dictionary<TrenchLayerType, List<double[]>> Layer;
            public List<double[]> Body;
        }

        private static Neighbour ProjectNeighbour(
            SectionDebugRow rowA, Frame fa, CrossSectionStation sA,
            SectionDebugRow rowB, Frame fb, double uMinA, double uMaxA)
        {
            double proj = fa.Tx * fb.Tx + fa.Ty * fb.Ty;           // T_A · T_B
            bool   perp = Math.Abs(proj) < PerpEps;

            double tB, cxB, cyB, tRaw;
            double dCross = 0.0;   // distance along B's axis at A's cross-section plane

            if (perp)
            {
                // Perpendicular: A's cross-section plane is parallel to B's axis —
                // no finite intersection exists. Fall back to closest point.
                ClosestOnSegment(sA.WorldX, sA.WorldY,
                    rowB.StartX, rowB.StartY, rowB.EndX, rowB.EndY,
                    out tB, out cxB, out cyB);
                tRaw = tB;   // Gate 2 is exempt for perp; value unused.
            }
            else
            {
                // Non-perpendicular: find where A's perpendicular cross-section plane
                // intersects B's axis. This is the geometrically correct corresponding
                // point on B for the current station on A — not an approximation.
                //
                // Derivation: the cross-section plane at P_A is { P_A + λ·N_A | λ∈ℝ }.
                // B's axis is { Start_B + d·T_B | d∈[0,L_B] }.
                // Setting equal and solving for d:
                //   d = ((P_A − Start_B) · T_A) / (T_A · T_B)
                double dpx = sA.WorldX - rowB.StartX;
                double dpy = sA.WorldY - rowB.StartY;
                dCross = (dpx * fa.Tx + dpy * fa.Ty) / proj;   // distance along B

                tRaw = rowB.Length2D > 1e-9 ? dCross / rowB.Length2D : 0.0;

                double dClamped = Math.Max(0, Math.Min(rowB.Length2D, dCross));
                tB  = rowB.Length2D > 1e-9 ? dClamped / rowB.Length2D : 0.0;
                cxB = rowB.StartX + dClamped * fb.Tx;
                cyB = rowB.StartY + dClamped * fb.Ty;
            }

            double uBc   = (cxB - sA.WorldX) * fa.Nx + (cyB - sA.WorldY) * fa.Ny;
            double uAinB = (sA.WorldX - cxB) * fb.Nx + (sA.WorldY - cyB) * fb.Ny;

            // ── Gate 1: Lateral distance ──────────────────────────────────────────
            // uAinB is the perpendicular distance from A's station to B's axis.
            // hwB is interpolated at the exact cross-section intersection point on B,
            // so the boundary is tight: a micro-station 1 mm outside the footprint has
            // |uAinB| = hwB_local + ε → rejected here without any width-offset tricks.
            double hwB = (rowB.TopWidthExcavS * (1.0 - tB) + rowB.TopWidthExcavE * tB) * 0.5;
            if (Math.Abs(uAinB) > hwB) return null;

            // ── Gate 2: Longitudinal extent (non-perpendicular only) ─────────────
            // The cross-section intersection parameter tRaw must fall within B's
            // extent [0,1]. This rejects stations of A whose cross-section plane
            // misses B entirely (past either endpoint), which ClosestOnSegment would
            // not catch because it clamps to [0,1].
            // Perpendicular crossings are exempt: Gate 1 already covers them.
            if (!perp && (tRaw < -1e-4 || tRaw > 1.0 + 1e-4)) return null;

            double dB = tB * rowB.Length2D;

            // B's gross layer rings + body in B's own frame, interpolated at the
            // closest point.
            var bLayers = BuildLayers(rowB, dB);
            var bBody   = ClipperGeo.PipeBody(rowB.PipeOuterDiamM, InvertAt(rowB, dB), PipeSegs);

            var nb = new Neighbour
            {
                CentreU                  = uBc,
                CrossChainageOnNeighbour = perp ? tB * rowB.Length2D : dCross,
                ProjDotProduct           = proj,
                IsPerp                   = perp,
                Layer                    = new Dictionary<TrenchLayerType, List<double[]>>(),
                Body                     = ProjectRing(bBody, proj, uBc, perp, uMinA, uMaxA),
            };
            foreach (var kv in bLayers)
                nb.Layer[kv.Key] = ProjectRing(kv.Value, proj, uBc, perp, uMinA, uMaxA);

            return nb;
        }

        /// <summary>
        /// Maps a ring from B's frame into A's frame. Parallel-ish: affine
        /// U = uBc + V·proj. Perpendicular: a full-width block over A's U-range
        /// spanning the ring's Z-band (the crossing punches straight through).
        /// </summary>
        private static List<double[]> ProjectRing(
            List<double[]> ring, double proj, double uBc, bool perp,
            double uMinA, double uMaxA)
        {
            if (ring == null || ring.Count < 3) return null;

            if (!perp)
            {
                var r = new List<double[]>(ring.Count);
                foreach (var v in ring) r.Add(new[] { uBc + v[0] * proj, v[1] });
                return r;
            }

            double zMin = double.MaxValue, zMax = double.MinValue;
            foreach (var v in ring) { if (v[1] < zMin) zMin = v[1]; if (v[1] > zMax) zMax = v[1]; }
            return new List<double[]>
            {
                new[] { uMinA, zMin }, new[] { uMaxA, zMin },
                new[] { uMaxA, zMax }, new[] { uMinA, zMax }
            };
        }

        // =====================================================================
        // Layer ring construction (mirrors BoQParserService.ComputeStations)
        // =====================================================================

        private static Dictionary<TrenchLayerType, List<double[]>> BuildLayers(
            SectionDebugRow row, double t)
        {
            double f        = row.Length2D > 1e-9 ? Math.Min(t / row.Length2D, 1.0) : 0.0;
            double terrainZ = row.StartTerrainZ + (row.EndTerrainZ - row.StartTerrainZ) * f;   // ZKazi
            double dolguZ   = row.StartDolguZ   + (row.EndDolguZ   - row.StartDolguZ)   * f;   // ZDolgu
            double invertZ  = row.InvertStart   + (row.InvertEnd   - row.InvertStart)   * f;

            double depthToInv = Math.Max(0, terrainZ - invertZ);
            double trueDepth  = depthToInv + row.TrBedHeight;
            double hwBase  = row.TrWidth      * 0.5;
            double hwBed   = row.TopWidthBed  * 0.5;
            double hwSurr  = row.TopWidthSurr * 0.5;
            double hwExcav = (row.TrWidth + 2.0 * trueDepth * row.SlopeRatio) * 0.5;

            // Backfill's own top reference (ZDolgu), independent of the excavation
            // top (ZKazi) — mirrors BoQParserService.ComputeStations.
            bool   dolguInvalid   = dolguZ < invertZ;
            double trueDepthDolgu = dolguInvalid ? 0 : Math.Max(0, dolguZ - invertZ) + row.TrBedHeight;
            double hwDolgu        = (row.TrWidth + 2.0 * trueDepthDolgu * row.SlopeRatio) * 0.5;

            double zBot     = invertZ - row.TrBedHeight;
            double zTop     = terrainZ;
            double zTopDolgu = dolguInvalid ? zBot : dolguZ;
            double zSurrTop = Math.Min(invertZ + row.HSurround, zTop);
            bool   backfillDegenerate = dolguInvalid || zTopDolgu <= zSurrTop;

            var result = new Dictionary<TrenchLayerType, List<double[]>>
            {
                [TrenchLayerType.Excavation] = new List<double[]>
                {
                    new[] { -hwBase,  zBot }, new[] { hwBase,  zBot },
                    new[] {  hwExcav, zTop }, new[] { -hwExcav, zTop }
                },
                [TrenchLayerType.Bedding] = new List<double[]>
                {
                    new[] { -hwBase, zBot    }, new[] { hwBase, zBot    },
                    new[] {  hwBed,  invertZ }, new[] { -hwBed, invertZ }
                },
                [TrenchLayerType.Surround] = new List<double[]>
                {
                    new[] { -hwBed,  invertZ  }, new[] { hwBed,  invertZ  },
                    new[] {  hwSurr, zSurrTop }, new[] { -hwSurr, zSurrTop }
                },
                [TrenchLayerType.Backfill] = backfillDegenerate ? new List<double[]>() : new List<double[]>
                {
                    new[] { -hwSurr,  zSurrTop  }, new[] { hwSurr,  zSurrTop  },
                    new[] {  hwDolgu, zTopDolgu }, new[] { -hwDolgu, zTopDolgu }
                },
            };
            return result;
        }

        private static double InvertAt(SectionDebugRow row, double t)
        {
            double f = row.Length2D > 1e-9 ? Math.Min(t / row.Length2D, 1.0) : 0.0;
            return row.InvertStart + (row.InvertEnd - row.InvertStart) * f;
        }

        // =====================================================================
        // Geometry helpers
        // =====================================================================

        private static Frame BuildFrame(SectionDebugRow r)
        {
            double dx = r.EndX - r.StartX, dy = r.EndY - r.StartY;
            double len = Math.Sqrt(dx * dx + dy * dy);
            var f = new Frame();
            if (len < 1e-9) { f.Tx = 1; f.Ty = 0; f.Nx = 0; f.Ny = 1; }
            else { f.Tx = dx / len; f.Ty = dy / len; f.Nx = -dy / len; f.Ny = dx / len; }

            double hw = Math.Max(r.TopWidthExcavS, r.TopWidthExcavE) * 0.5;
            f.MinX = Math.Min(r.StartX, r.EndX) - hw;
            f.MaxX = Math.Max(r.StartX, r.EndX) + hw;
            f.MinY = Math.Min(r.StartY, r.EndY) - hw;
            f.MaxY = Math.Max(r.StartY, r.EndY) + hw;
            f.AvgInvert = (r.InvertStart + r.InvertEnd) * 0.5;
            return f;
        }

        private static bool AabbOverlap(Frame a, Frame b)
            => a.MaxX >= b.MinX && b.MaxX >= a.MinX
            && a.MaxY >= b.MinY && b.MaxY >= a.MinY;

        /// <summary>
        /// True when two sections share a topology node (manhole) — i.e. they are
        /// connected pipes. Compares the start/end node GUIDs in all four
        /// combinations; empty GUIDs never match.
        /// </summary>
        private static bool SharesNode(SectionDebugRow a, SectionDebugRow b)
        {
            return Same(a.StartNodeGuid, b.StartNodeGuid)
                || Same(a.StartNodeGuid, b.EndNodeGuid)
                || Same(a.EndNodeGuid,   b.StartNodeGuid)
                || Same(a.EndNodeGuid,   b.EndNodeGuid);
        }

        private static bool Same(string g1, string g2)
            => !string.IsNullOrEmpty(g1)
            && string.Equals(g1, g2, StringComparison.OrdinalIgnoreCase);

        private static void ClosestOnSegment(
            double px, double py, double ax, double ay, double bx, double by,
            out double t, out double cx, out double cy)
        {
            double dx = bx - ax, dy = by - ay;
            double lenSq = dx * dx + dy * dy;
            if (lenSq < 1e-16) { t = 0; cx = ax; cy = ay; return; }
            t  = Math.Max(0, Math.Min(1, ((px - ax) * dx + (py - ay) * dy) / lenSq));
            cx = ax + t * dx;
            cy = ay + t * dy;
        }

    }
}
