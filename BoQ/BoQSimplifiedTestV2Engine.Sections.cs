using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Sections;
using UrbanoMetraj.BoQ.Services;

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// V2 engine — cross-section (En Kesit) extraction for UT_CROSS_SECTIONS.
    ///
    /// Display only: nothing here feeds the volume integration or the ledger. The
    /// drawn section is always one the engine computed (decision 1):
    ///   • an engine station at that chainage, or
    ///   • a clash-free chainage → the gross section from BuildGrossProfiles, or
    ///   • inside a crossing zone off the grid → the nearest engine station.
    /// Scenario resolution mirrors the ledger per clash pair: KU → the lower pipe loses
    /// the overlap, KL → the upper, SP → each its side of the split line; the Phase 8e
    /// sand/pipe losses apply to every scenario except "Yok say" (gross).
    /// </summary>
    public partial class BoQSimplifiedTestV2Command
    {
        /// <summary>Engine state kept after a run, for building sections.</summary>
        internal sealed class V2SectionCapture
        {
            internal List<SectionDebugRow>         Rows;
            internal List<List<SimplifiedStation>> Stations;
            internal List<List<(double, double)>>  PerPipeZones;
            internal List<PairZone>                PairZones;
        }

        /// <summary>One clash pair as seen from one station, in that pipe's own left-normal frame.</summary>
        internal sealed class StationClash
        {
            public int               OtherPipe;
            public SimplifiedStation Other;
            public double            OtherU;              // other pipe's axis offset in this frame
            public bool              Mirror;              // pair frame was mirrored to this pipe's left normal
            public bool              IsUpper;
            public double            SplitU = double.NaN;
            public bool              IsBisector;
            public double            AngleDeg;
            public List<List<double[]>> OvE, LoseSPE, OvB, LoseSPB, BfSand, BedLoss, SurLoss;
        }

        // ── Recording (called from the pair passes right after RecordSlice) ───────

        /// <summary>
        /// Stores the clash shapes of one slice on both stations (display only). Same
        /// polygons as RecordSlice / ComputeCrossLoss; B's copy is moved into B's frame and
        /// mirrored when the pair frame runs against B's direction (<paramref name="mirrorB"/>).
        /// </summary>
        private static void RecordClash(PairZone pz, SimplifiedStation sA, SimplifiedStation sB, double uBc, bool mirrorB)
        {
            if (sA.ExcavPoly == null || sB.ExcavPoly == null) return;
            List<double[]> R(List<double[]> p)  => HasArea(p) ? RoundPolyTo1mm(p) : null;
            List<double[]> RB(List<double[]> p) => HasArea(p) ? RoundPolyTo1mm(TranslatePoly(p, uBc)) : null;

            bool aIsUpper = sA.InvertZ >= sB.InvertZ;
            double upperHW   = aIsUpper ? -sA.ExcavPoly[0][0] : -sB.ExcavPoly[0][0];
            double upperCtrU = aIsUpper ? 0.0 : uBc;
            double lowerCtrU = aIsUpper ? uBc : 0.0;
            bool   lowerRight = lowerCtrU > upperCtrU;
            double splitU     = lowerRight ? upperCtrU + upperHW : upperCtrU - upperHW;
            const double BIG = 1000.0;
            var leftRect  = new List<double[]> { new[] { -BIG, -BIG }, new[] { splitU, -BIG }, new[] { splitU, BIG }, new[] { -BIG, BIG } };
            var rightRect = new List<double[]> { new[] { splitU, -BIG }, new[] { BIG, -BIG }, new[] { BIG, BIG }, new[] { splitU, BIG } };
            var upperSide = lowerRight ? leftRect : rightRect;
            var lowerSide = lowerRight ? rightRect : leftRect;
            var aLoseSide = new List<List<double[]>> { aIsUpper ? lowerSide : upperSide };
            var bLoseSide = new List<List<double[]>> { aIsUpper ? upperSide : lowerSide };

            List<List<double[]>> ovE = null, loseAE = null, loseBE = null;
            var aEx = R(sA.ExcavPoly); var bEx = RB(sB.ExcavPoly);
            if (aEx != null && bEx != null)
            {
                ovE = ClipperGeo.Intersect(aEx, bEx);
                if (ovE.Count > 0)
                {
                    loseAE = ClipperGeo.Intersect(ovE, aLoseSide);
                    loseBE = ClipperGeo.Intersect(ovE, bLoseSide);
                }
            }
            List<List<double[]>> ovB = null, loseAB = null, loseBB = null;
            var aBf = R(sA.BackfillPoly); var bBf = RB(sB.BackfillPoly);
            if (aBf != null && bBf != null)
            {
                ovB = ClipperGeo.Intersect(aBf, bBf);
                if (ovB.Count > 0)
                {
                    loseAB = ClipperGeo.Intersect(ovB, aLoseSide);
                    loseBB = ClipperGeo.Intersect(ovB, bLoseSide);
                }
            }
            var c = ComputeCrossLoss(sA, sB, uBc);
            bool hasOverlap = (ovE?.Count ?? 0) > 0 || (ovB?.Count ?? 0) > 0;

            (sA.Clashes ?? (sA.Clashes = new List<StationClash>())).Add(new StationClash
            {
                OtherPipe = pz.PipeB, Other = sB, OtherU = uBc, Mirror = false, IsUpper = aIsUpper,
                SplitU = hasOverlap ? splitU : double.NaN,
                IsBisector = pz.UseBisectorFrame, AngleDeg = pz.AngleXYDeg,
                OvE = ovE, LoseSPE = loseAE, OvB = ovB, LoseSPB = loseAB,
                BfSand = c?.ABf, BedLoss = c?.ABed, SurLoss = c?.ASur
            });

            // B side — recomputed natively in B's frame (B's own polygons unshifted, A shifted by
            // −uBc), so every lost edge lying on B's own outline is exactly B's (1 mm snapped)
            // edge: shifting A-frame results back by a non-mm offset left 0.5 mm slivers after
            // the Difference. Same split line and tie rule as A's side; then mirrored onto B's
            // own left normal when the pair frame runs against B's direction.
            List<double[]> RA(List<double[]> p) => HasArea(p) ? RoundPolyTo1mm(TranslatePoly(p, -uBc)) : null;
            double splitB = splitU - uBc;
            var leftB  = new List<double[]> { new[] { -BIG, -BIG }, new[] { splitB, -BIG }, new[] { splitB, BIG }, new[] { -BIG, BIG } };
            var rightB = new List<double[]> { new[] { splitB, -BIG }, new[] { BIG, -BIG }, new[] { BIG, BIG }, new[] { splitB, BIG } };
            var bLoseSideB = new List<List<double[]>> { (aIsUpper ? lowerRight : !lowerRight) ? leftB : rightB };

            List<List<double[]>> ovEB = null, loseEB = null, ovBB = null, loseBBB = null;
            var bExB = R(sB.ExcavPoly); var aExB = RA(sA.ExcavPoly);
            if (bExB != null && aExB != null)
            {
                ovEB = ClipperGeo.Intersect(bExB, aExB);
                if (ovEB.Count > 0) loseEB = ClipperGeo.Intersect(ovEB, bLoseSideB);
            }
            var bBfB = R(sB.BackfillPoly); var aBfB = RA(sA.BackfillPoly);
            if (bBfB != null && aBfB != null)
            {
                ovBB = ClipperGeo.Intersect(bBfB, aBfB);
                if (ovBB.Count > 0) loseBBB = ClipperGeo.Intersect(ovBB, bLoseSideB);
            }
            var cB = ComputeCrossLoss(sB, sA, -uBc, !aIsUpper);

            (sB.Clashes ?? (sB.Clashes = new List<StationClash>())).Add(new StationClash
            {
                OtherPipe = pz.PipeA, Other = sA, Mirror = mirrorB, IsUpper = !aIsUpper,
                OtherU = mirrorB ? uBc : -uBc,
                SplitU = hasOverlap ? (mirrorB ? -splitB : splitB) : double.NaN,
                IsBisector = pz.UseBisectorFrame, AngleDeg = pz.AngleXYDeg,
                OvE = MirrorRegion(ovEB, mirrorB), LoseSPE = MirrorRegion(loseEB, mirrorB),
                OvB = MirrorRegion(ovBB, mirrorB), LoseSPB = MirrorRegion(loseBBB, mirrorB),
                BfSand  = MirrorRegion(cB?.ABf,  mirrorB),
                BedLoss = MirrorRegion(cB?.ABed, mirrorB),
                SurLoss = MirrorRegion(cB?.ASur, mirrorB)
            });
        }

        private static List<List<double[]>> MirrorRegion(List<List<double[]>> region, bool mirror)
        {
            if (region == null || !mirror) return region;
            var res = new List<List<double[]>>(region.Count);
            foreach (var ring in region)
            {
                var m = new List<double[]>(ring.Count);
                for (int k = ring.Count - 1; k >= 0; k--) m.Add(new[] { -ring[k][0], ring[k][1] });   // reflect + keep orientation
                res.Add(m);
            }
            return res;
        }

        // ── Section building ─────────────────────────────────────────────────────

        /// <summary>
        /// Builds the drawable section of pipe <paramref name="pipe"/> at local chainage
        /// <paramref name="requested"/> under the given Kazı / Dolgu scenarios.
        /// </summary>
        internal static SectionCut BuildSectionCut(
            V2SectionCapture cap, int pipe, double requested, TiePreference kazi, TiePreference dolgu)
        {
            var row = cap.Rows[pipe];
            double L = row.Length2D;
            double d = Math.Max(0, Math.Min(L, requested));
            var zones = cap.PerPipeZones[pipe];
            var list  = cap.Stations[pipe];

            SimplifiedStation st;
            SectionStationKind kind;
            var atD = list.Where(s => s.ExcavPoly != null && Math.Abs(s.StationDist - d) < 1e-4).ToList();
            if (atD.Count > 0)
            {
                // Zone boundaries carry a gross clone next to the in-zone station: draw the clash one.
                st   = atD.FirstOrDefault(s => (s.Clashes?.Count ?? 0) > 0) ?? atD[0];
                kind = SectionStationKind.EngineStation;
            }
            else if (!zones.Any(z => d > z.Item1 + 1e-6 && d < z.Item2 - 1e-6))
            {
                st = new SimplifiedStation { StationDist = d };
                BuildGrossProfiles(row, new List<SimplifiedStation> { st });
                kind = SectionStationKind.GrossAtChainage;
            }
            else
            {
                var z = zones.First(zz => d > zz.Item1 + 1e-6 && d < zz.Item2 - 1e-6);
                st = list.Where(s => s.ExcavPoly != null && s.StationDist >= z.Item1 - 1e-4 && s.StationDist <= z.Item2 + 1e-4)
                         .OrderBy(s => Math.Abs(s.StationDist - d))
                         .ThenByDescending(s => s.Clashes?.Count ?? 0)
                         .FirstOrDefault();
                if (st == null)
                {
                    st = new SimplifiedStation { StationDist = d };
                    BuildGrossProfiles(row, new List<SimplifiedStation> { st });
                    kind = SectionStationKind.GrossAtChainage;
                }
                else kind = SectionStationKind.SnappedInClash;
            }

            // Zero-length (e.g. a vertical drop) / degenerate pipe: the engine has no section for it.
            if (st.ExcavPoly == null || st.ExcavPoly.Count < 4) return null;
            return ResolveCut(cap, pipe, row, st, d, kind, kazi, dolgu);
        }

        /// <summary>Section of one specific engine station (its partner in a crossing shares the same cut).</summary>
        internal static SectionCut BuildSectionCutAtStation(
            V2SectionCapture cap, int pipe, SimplifiedStation st, TiePreference kazi, TiePreference dolgu)
            => ResolveCut(cap, pipe, cap.Rows[pipe], st, st.StationDist, SectionStationKind.EngineStation, kazi, dolgu);

        private static SectionCut ResolveCut(
            V2SectionCapture cap, int pipe, SectionDebugRow row, SimplifiedStation st, double requested,
            SectionStationKind kind, TiePreference kazi, TiePreference dolgu)
        {
            var clashes = st.Clashes ?? new List<StationClash>();
            bool bisector = st.IsBisectorZone;
            var bisClash  = clashes.FirstOrDefault(c => c.IsBisector);

            var cut = new SectionCut
            {
                PipeIndex = pipe, PipeName = row.PipeName, SystemName = row.SystemName,
                StartNodeName = row.StartNodeName, EndNodeName = row.EndNodeName,
                DiameterMm = row.DiameterMm, Material = row.Material, OuterDiamM = row.PipeOuterDiamM,
                RequestedDist = requested, LocalChainage = st.StationDist, Kind = kind,
                WorldX = st.WorldX, WorldY = st.WorldY,
                IsPipeStart = st.StationDist < 1e-4, IsPipeEnd = st.StationDist > row.Length2D - 1e-4,
                IsZoneBoundary = cap.PerPipeZones[pipe].Any(z => Math.Abs(z.Item1 - st.StationDist) < 1e-4
                                                              || Math.Abs(z.Item2 - st.StationDist) < 1e-4),
                KaziGroundZ = st.TerrainZ, DolguGroundZ = st.DolguZ, DolguInvalid = st.DolguInvalid,
                InvertZ = st.InvertZ, TrueDepth = st.TrueDepth,
                BottomWidth = st.ExcavPoly[1][0] - st.ExcavPoly[0][0],
                TopWidth    = st.ExcavPoly[2][0] - st.ExcavPoly[3][0],
                SlopeX      = row.SlopeRatio > 1e-12 ? 1.0 / row.SlopeRatio : 0.0,
                IsBisectorFrame  = bisector,
                CrossingAngleDeg = bisector && bisClash != null ? bisClash.AngleDeg : 0.0,
                ExcavScenario = kazi, BackfillScenario = dolgu
            };
            cut.TrenchBottomZ = st.ExcavPoly[0][1];

            // ── Gross ────────────────────────────────────────────────────────────
            var grossEx  = Region(st.ExcavPoly);
            var grossBed = Region(st.BeddingPoly);
            var grossSur = Region(st.SurroundPoly);
            var grossBf  = Region(st.BackfillPoly);
            var pipeBody = Region(st.PipePoly);

            // ── Losses per scenario (mirrors ApplyPairLedger + aggregator) ───────
            List<List<double[]>> lostEx = null, lostBf = null, lostBed = null, lostSur = null;
            foreach (var c in clashes)
            {
                lostEx = Accumulate(lostEx, ScenarioLoss(kazi,  c, c.OvE, c.LoseSPE));
                lostBf = Accumulate(lostBf, ScenarioLoss(dolgu, c, c.OvB, c.LoseSPB));
                if (dolgu != TiePreference.Ignore)
                {
                    lostBf  = Accumulate(lostBf,  c.BfSand);
                    lostBed = Accumulate(lostBed, c.BedLoss);
                    lostSur = Accumulate(lostSur, c.SurLoss);
                }
            }

            var ownEx  = Minus(grossEx,  lostEx);
            var ownBed = Minus(grossBed, lostBed);
            var ownSur = Minus(Minus(grossSur, lostSur), pipeBody);
            var ownBf  = Minus(grossBf,  lostBf);

            void Add(SectionLayer layer, SectionRegionRole role, List<List<double[]>> rings, StationClash c = null)
            {
                if (rings == null || rings.Count == 0) return;
                var rg = new SectionRegion { Layer = layer, Role = role, Rings = rings };
                if (c != null)
                {
                    var other = cap.Rows[c.OtherPipe];
                    rg.OtherPipeIndex = c.OtherPipe; rg.OtherPipeName = other.PipeName; rg.OtherSystemName = other.SystemName;
                    rg.OtherDiameterMm = other.DiameterMm;
                }
                cut.Regions.Add(rg);
            }

            Add(SectionLayer.Kazi,        SectionRegionRole.GrossOutline, grossEx);
            Add(SectionLayer.Kazi,        SectionRegionRole.Owned,        ownEx);
            Add(SectionLayer.Yataklama,   SectionRegionRole.Owned,        ownBed);
            Add(SectionLayer.Gomlekleme,  SectionRegionRole.Owned,        ownSur);
            Add(SectionLayer.GeriDolgu,   SectionRegionRole.Owned,        ownBf);
            Add(SectionLayer.BoruGovdesi, SectionRegionRole.Owned,        pipeBody);

            foreach (var c in clashes)
            {
                var other = cap.Rows[c.OtherPipe];
                cut.Clashes.Add(new SectionClashInfo
                {
                    OtherPipeIndex = c.OtherPipe, OtherPipeName = other.PipeName, OtherSystemName = other.SystemName,
                    ThisIsUpper = c.IsUpper, OtherOffsetU = c.OtherU, SplitU = c.SplitU
                });

                var lostE = ScenarioLoss(kazi, c, c.OvE, c.LoseSPE);
                if (lostE != null && lostE.Count > 0) Add(SectionLayer.Kazi, SectionRegionRole.LostToOther, lostE, c);
                var lostB = ScenarioLoss(dolgu, c, c.OvB, c.LoseSPB);
                if (dolgu != TiePreference.Ignore) lostB = Accumulate(lostB, c.BfSand);
                if (lostB != null && lostB.Count > 0) Add(SectionLayer.GeriDolgu, SectionRegionRole.LostToOther, lostB, c);

                // The clashing pipe's own section in this cut, outside this pipe's share.
                var o = c.Other;
                List<List<double[]>> Shift(List<double[]> p) => HasArea(p) ? new List<List<double[]>> { TranslatePoly(p, c.OtherU) } : null;
                Add(SectionLayer.Kazi,        SectionRegionRole.OtherTrench, Minus(Shift(o.ExcavPoly),    ownEx), c);
                Add(SectionLayer.Yataklama,   SectionRegionRole.OtherTrench, Minus(Shift(o.BeddingPoly),  ownEx), c);
                Add(SectionLayer.Gomlekleme,  SectionRegionRole.OtherTrench, Minus(Minus(Shift(o.SurroundPoly), ownEx), Shift(o.PipePoly)), c);
                Add(SectionLayer.GeriDolgu,   SectionRegionRole.OtherTrench, Minus(Shift(o.BackfillPoly), ownEx), c);
                Add(SectionLayer.BoruGovdesi, SectionRegionRole.OtherTrench, Shift(o.PipePoly), c);
                Add(SectionLayer.Kazi,        SectionRegionRole.GrossOutline, Shift(o.ExcavPoly), c);   // its whole outline (ghost mode)
            }

            // ── Areas (measured on the drawn regions) ────────────────────────────
            cut.AreaKaziGross        = ClipperGeo.AreaRaw(grossEx);
            cut.AreaKaziNet          = ClipperGeo.AreaRaw(ownEx);
            cut.AreaYataklamaGross   = ClipperGeo.AreaRaw(grossBed);
            cut.AreaYataklamaNet     = ClipperGeo.AreaRaw(ownBed);
            cut.AreaGomleklemeGross  = ClipperGeo.AreaRaw(Minus(grossSur, pipeBody));
            cut.AreaGomleklemeNet    = ClipperGeo.AreaRaw(ownSur);
            cut.AreaGeriDolguGross   = ClipperGeo.AreaRaw(grossBf);
            cut.AreaGeriDolguNet     = ClipperGeo.AreaRaw(ownBf);

            BuildSubBands(cut, row, st, ownBed, ownSur, ownBf);
            SetExtent(cut);
            return cut;
        }

        /// <summary>The overlap this pipe gives up in one clash pair under scenario <paramref name="p"/>.</summary>
        private static List<List<double[]>> ScenarioLoss(
            TiePreference p, StationClash c, List<List<double[]>> overlap, List<List<double[]>> spLose)
        {
            switch (p)
            {
                case TiePreference.KeepUpper: return c.IsUpper ? null : overlap;   // upper keeps → lower loses
                case TiePreference.KeepLower: return c.IsUpper ? overlap : null;   // lower keeps → upper loses
                case TiePreference.Split:     return spLose;
                default:                      return null;                         // Ignore: gross
            }
        }

        private static List<List<double[]>> Minus(List<List<double[]>> a, List<List<double[]>> b)
        {
            if (a == null || a.Count == 0) return a ?? new List<List<double[]>>();
            if (b == null || b.Count == 0) return a;
            return ClipperGeo.Difference(a, b);
        }

        // ── Sub-layer bands (decision 4) ─────────────────────────────────────────

        private static void BuildSubBands(SectionCut cut, SectionDebugRow row, SimplifiedStation st,
            List<List<double[]>> ownBed, List<List<double[]>> ownSur, List<List<double[]>> ownBf)
        {
            var stack = row.TrenchStack;
            if (stack == null || stack.Count == 0) return;

            // Levels — mirror BuildGrossProfiles.
            double pipeWall  = (row.PipeOuterDiamM - row.DiameterMm / 1000.0) / 2.0;
            double outerBtm  = st.InvertZ - pipeWall;
            double zBot      = outerBtm - row.TrBedHeight;
            double zTop      = st.TerrainZ;
            double zBedTop   = Math.Max(zBot, Math.Min(outerBtm, zTop));
            double zSurrTop  = Math.Max(zBedTop, Math.Min(outerBtm + row.HSurround, zTop));
            double zTopDolgu = st.DolguInvalid ? zBot : st.DolguZ;
            double zOD       = Math.Min(outerBtm + row.PipeOuterDiamM, zSurrTop);

            // The group regions are snapped to 1 mm while these levels are not: the outermost
            // band edge of a main group is opened by EdgeTol so the bands tile the region (the
            // region itself still bounds them). Internal edges stay exact (shared by two bands).
            const double EdgeTol = 0.002;
            void Group(TrenchStackGroup g, SectionLayer layer, List<List<double[]>> owned, double z0, double z1, double nominalH,
                       bool openLow, bool openHigh)
            {
                var layers = stack.Where(l => l.Group == g).ToList();
                if (layers.Count == 0 || owned == null || owned.Count == 0 || z1 <= z0 + 1e-6) return;
                foreach (var (name, mat, lo0, hi0) in ResolveStack(layers, z0, z1, nominalH))
                {
                    // Edges on the regions' 1 mm grid: a pipe top at 77.292 must not be crossed by an
                    // edge at 77.2917 (a 0.3 mm cap the 1 mm ring clean-up then distorts).
                    double lo = Math.Round(lo0 * 1000.0) / 1000.0, hi = Math.Round(hi0 * 1000.0) / 1000.0;
                    if (hi <= lo) continue;
                    double cLo = openLow  && lo0 <= z0 + 1e-9 ? lo - EdgeTol : lo;
                    double cHi = openHigh && hi0 >= z1 - 1e-9 ? hi + EdgeTol : hi;
                    var rect = new List<List<double[]>> { Rect(-1000, cLo, 1000, cHi) };
                    var band = ClipperGeo.Intersect(owned, rect);
                    if (band.Count == 0) continue;
                    cut.SubBands.Add(new SectionSubBand
                    {
                        Group = g, Layer = layer, LayerName = name, MaterialType = mat, ZLow = lo, ZHigh = hi, Rings = band,
                        Area = ClipperGeo.IntersectAreaRaw(owned, rect)
                    });
                }
            }

            Group(TrenchStackGroup.Yataklama,  SectionLayer.Yataklama,  ownBed, zBot,      zBedTop,   zBedTop - zBot,       true,  true);
            Group(TrenchStackGroup.BoruEtrafi, SectionLayer.Gomlekleme, ownSur, outerBtm,  zOD,       row.PipeOuterDiamM,   true,  false);
            Group(TrenchStackGroup.BoruUstu,   SectionLayer.Gomlekleme, ownSur, zOD,       zSurrTop,  zSurrTop - zOD,       false, true);
            Group(TrenchStackGroup.GeriDolgu,  SectionLayer.GeriDolgu,  ownBf,  zSurrTop,  zTopDolgu, zTopDolgu - zSurrTop, true,  true);

            // Division lines between consecutive bands of the same main group.
            foreach (var layer in new[] { SectionLayer.Yataklama, SectionLayer.Gomlekleme, SectionLayer.GeriDolgu })
            {
                var owned = layer == SectionLayer.Yataklama ? ownBed : layer == SectionLayer.Gomlekleme ? ownSur : ownBf;
                var bands = cut.SubBands.Where(b => b.Layer == layer).OrderBy(b => b.ZLow).ToList();
                for (int k = 0; k + 1 < bands.Count; k++)
                {
                    double z = bands[k].ZHigh;
                    var segs = ScanLine(owned, z);
                    if (segs.Count > 0) cut.DivisionLines.Add(new SectionDivisionLine { Layer = layer, Z = z, Segments = segs });
                }
            }
        }

        /// <summary>
        /// Bottom-up heights of one group's sub-layers inside [z0, z1] — same resolution as
        /// the parser's ratio split: the first flexible layer (UpToPipeTop / FillToSurface)
        /// takes the group height minus every fixed layer, further flexible layers take 0.
        /// </summary>
        internal static List<(string Name, string Material, double Lo, double Hi)> ResolveStack(
            List<TrenchStackLayer> layers, double z0, double z1, double nominalH)
        {
            double fixedSum = layers.Where(l => l.Kind == TrenchStackKind.Fixed).Sum(l => l.ThicknessM);
            bool flexUsed = false;
            var res = new List<(string, string, double, double)>();
            double z = z0;
            foreach (var l in layers)
            {
                double th = l.ThicknessM;
                if (l.Kind != TrenchStackKind.Fixed)
                {
                    th = flexUsed ? 0.0 : Math.Max(0, nominalH - fixedSum);
                    flexUsed = true;
                }
                double lo = z, hi = Math.Min(z1, z + Math.Max(0, th));
                z += Math.Max(0, th);
                if (hi > lo + 1e-6) res.Add((l.LayerName, l.MaterialType, lo, hi));
                if (z >= z1 - 1e-9) break;
            }
            return res;
        }

        private static List<double[]> Rect(double u0, double z0, double u1, double z1)
            => new List<double[]> { new[] { u0, z0 }, new[] { u1, z0 }, new[] { u1, z1 }, new[] { u0, z1 } };

        /// <summary>Horizontal line at <paramref name="z"/> clipped to a region (even-odd), as {u0,u1} segments.</summary>
        internal static List<double[]> ScanLine(List<List<double[]>> region, double z)
        {
            var xs = new List<double>();
            foreach (var ring in region ?? new List<List<double[]>>())
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    if ((a[1] > z) == (b[1] > z)) continue;              // edge does not straddle z
                    xs.Add(a[0] + (z - a[1]) * (b[0] - a[0]) / (b[1] - a[1]));
                }
            xs.Sort();
            var segs = new List<double[]>();
            for (int k = 0; k + 1 < xs.Count; k += 2)
                if (xs[k + 1] - xs[k] > 1e-4) segs.Add(new[] { xs[k], xs[k + 1] });
            return segs;
        }

        private static void SetExtent(SectionCut cut)
        {
            double minU = double.MaxValue, maxU = double.MinValue, minZ = double.MaxValue, maxZ = double.MinValue;
            foreach (var rg in cut.Regions)
                foreach (var ring in rg.Rings)
                    foreach (var v in ring)
                    {
                        if (v[0] < minU) minU = v[0]; if (v[0] > maxU) maxU = v[0];
                        if (v[1] < minZ) minZ = v[1]; if (v[1] > maxZ) maxZ = v[1];
                    }
            if (minU > maxU) { minU = -0.5; maxU = 0.5; minZ = cut.InvertZ - 0.5; maxZ = cut.InvertZ + 0.5; }
            maxZ = Math.Max(maxZ, Math.Max(cut.KaziGroundZ, cut.DolguInvalid ? maxZ : cut.DolguGroundZ));
            cut.MinU = minU; cut.MaxU = maxU; cut.MinZ = minZ; cut.MaxZ = maxZ;
        }
    }
}
