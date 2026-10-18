using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// READ-ONLY diagnostic behind UT_BOQ_PIPE_DIAG (KAZI_DOLGU_DOGRULAMA.md §5.3).
    /// Per pipe, with NO clash and NO manhole deduction (the gross V2 volumes), the
    /// identity is: Kazı = Yataklama + Gömlekleme + Geri Dolgu + pipe body. The residual
    /// R = VExcavGross − VBeddingGross − VSurroundGross − VBackfillGross − PipeArea·Length2D
    /// is split, from the persisted station polygons, into:
    ///   ShapeDatum      — Geri Dolgu top (ZDolgu) ≠ Kazı top (ZKazi) at the station
    ///   ShapeDegenerate — no Geri Dolgu polygon although the trench reaches above the sand
    ///   ShapeOther      — any other gap between the excavation trapezoid and its 3 layers
    ///   Clamp           — Gömlekleme net area clamped at 0 (pipe body larger than the zone)
    ///   Path            — Σ PipeArea·(integrated station distance) − PipeArea·Length2D
    ///   Rest            — R − all of the above (area rounding, bisector-zone integration)
    /// Informational: BisectorBody = PipeArea·d·(k − 1) on bisector-frame segments
    /// (k = section widening factor) — the pipe-body volume V2 does not subtract there.
    /// </summary>
    public static class PipeIdentityDiagService
    {
        public sealed class Row
        {
            public string System, Pipe;
            public bool   HasV2;
            public double Length2D, PipeArea;
            public double ExcavGross, BeddingGross, SurroundGross, BackfillGross;
            public double R;
            public double ShapeDatum, ShapeDegenerate, ShapeOther, Clamp, Path, Rest, BisectorBody;
            public int    DatumStations, DegenerateStations, BisectorStations;
            // Exact excavation reference, independent of the station frames: the pipe's own
            // trapezoid (bottom = outer pipe bottom − Yataklama, width TrWidth + 2·D·slope)
            // integrated along the axis from the stored end levels (StartTerrainZ/EndTerrainZ,
            // InvertStart/InvertEnd — linear, exactly what V2 interpolates per station).
            // ExcavError = V2 gross − reference: what the station integration adds/loses.
            public double PathLength;                // Σ station-to-station world distance (info)
            public double ExcavRef, ExcavError;
            // Segment breakdown of the excavation integration. Each station's plane cuts the
            // pipe axis at a coordinate c: on-axis stations → their projection; bisector
            // stations → the cut of their plane (⊥ bisector direction b) with the axis,
            // c = ((W − Start)·b) / (u·b), b taken from the neighbouring bisector stations.
            // V2 = Σ avg(area)·(world distance); Ref = |∫ area dt| between the two cuts.
            //   AA = axis↔axis, TR = axis↔bisector transition, BB = bisector↔bisector.
            public double V2AA, RefAA, V2TR, RefTR, V2BB, RefBB;
            public double Coverage, BackwardLen;     // Σ|Δc| and Σ of backward steps (m)
            public int    BisNoDir;                  // bisector stations without a neighbour (c = projection)
        }

        public static List<Row> Run(BoQReport report)
        {
            var rows = new List<Row>();
            if (report?.SectionDebug == null) return rows;

            foreach (var s in report.SectionDebug)
            {
                var r = new Row
                {
                    System = s.SystemName, Pipe = s.PipeName ?? $"{s.StartNodeName}->{s.EndNodeName}",
                    HasV2 = s.HasV2Volumes, Length2D = s.Length2D, PipeArea = s.PipeArea,
                    ExcavGross = s.VExcavGross, BeddingGross = s.VBeddingGross,
                    SurroundGross = s.VSurroundGross, BackfillGross = s.VBackfillGross,
                };
                r.R = r.ExcavGross - r.BeddingGross - r.SurroundGross - r.BackfillGross - r.PipeArea * r.Length2D;

                var st = (s.Stations ?? new List<CrossSectionStation>())
                         .Where(x => Has(x.ExcavPoly))
                         .ToList();
                var info = st.Select(x => Classify(x, s)).ToList();
                foreach (var i in info)
                {
                    if (i.Cat == Cat.Datum)      r.DatumStations++;
                    if (i.Cat == Cat.Degenerate) r.DegenerateStations++;
                    if (i.K > 1.0005)            r.BisectorStations++;
                }

                double[] cut = AxisCuts(st, info, s, out r.BisNoDir);

                for (int k = 0; k + 1 < st.Count; k++)
                {
                    double dx = st[k + 1].WorldX - st[k].WorldX, dy = st[k + 1].WorldY - st[k].WorldY;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9) continue;
                    var a = info[k]; var b = info[k + 1];

                    // Segment breakdown (see Row).
                    double v2Seg  = (a.Ex + b.Ex) * 0.5 * d;
                    double dc     = cut[k + 1] - cut[k];
                    double refSeg = Math.Abs(RefBetween(s, cut[k], cut[k + 1]));
                    r.Coverage += Math.Abs(dc);
                    if (dc < -1e-6) r.BackwardLen += -dc;
                    bool aBis = a.K > 1.0005, bBis = b.K > 1.0005;
                    if (aBis && bBis)      { r.V2BB += v2Seg; r.RefBB += refSeg; }
                    else if (aBis || bBis) { r.V2TR += v2Seg; r.RefTR += refSeg; }
                    else                   { r.V2AA += v2Seg; r.RefAA += refSeg; }

                    double shape = (a.Gap + b.Gap) * 0.5 * d;
                    Cat cat = a.Cat == b.Cat ? a.Cat
                            : (a.Cat == Cat.Datum || b.Cat == Cat.Datum) ? Cat.Datum
                            : (a.Cat == Cat.Degenerate || b.Cat == Cat.Degenerate) ? Cat.Degenerate : Cat.Other;
                    if      (cat == Cat.Datum)      r.ShapeDatum      += shape;
                    else if (cat == Cat.Degenerate) r.ShapeDegenerate += shape;
                    else                            r.ShapeOther      += shape;

                    r.Clamp += (a.Clamp + b.Clamp) * 0.5 * d;

                    r.PathLength += d;
                    r.Path  += s.PipeArea * d;
                    double kAvg = (a.K + b.K) * 0.5;
                    if (kAvg > 1.0005) r.BisectorBody += s.PipeArea * d * (kAvg - 1.0);
                }
                r.Path -= s.PipeArea * s.Length2D;
                r.ExcavRef   = ExcavReference(s);
                r.ExcavError = r.ExcavGross - r.ExcavRef;
                // R = Σ gap + Path − Clamp + rest   (Gömlekleme net = surround poly − body + clamp)
                r.Rest = r.R - (r.ShapeDatum + r.ShapeDegenerate + r.ShapeOther + r.Path - r.Clamp);
                rows.Add(r);
            }
            return rows;
        }

        private enum Cat { Other, Datum, Degenerate }

        private struct StInfo { public double Gap, Clamp, K, Ex; public Cat Cat; }

        // The pipe's own perpendicular trench area at axis fraction f (0..1): same linear
        // interpolation of ZKazi / invert that V2 uses per station.
        private static double AreaAt(SectionDebugRow s, double f)
        {
            double wall = Math.Max(0, (s.PipeOuterDiamM - s.DiameterMm / 1000.0) / 2.0);
            double zTop = s.StartTerrainZ + (s.EndTerrainZ - s.StartTerrainZ) * f;
            double inv  = s.InvertStart   + (s.InvertEnd   - s.InvertStart)   * f;
            double D    = Math.Max(0, zTop - (inv - wall - s.TrBedHeight));
            return D * (s.TrWidth + D * s.SlopeRatio);
        }

        // ∫ trench area dt over [0, Length2D] — composite Simpson, 40 intervals (the area
        // is quadratic in t except where the depth clamps at 0).
        private static double ExcavReference(SectionDebugRow s) => RefBetween(s, 0, s.Length2D);

        // Signed ∫_{c0}^{c1} area dt (axis metres), clamped to the pipe [0, L].
        private static double RefBetween(SectionDebugRow s, double c0, double c1)
        {
            double L = s.Length2D;
            if (L <= 1e-9) return 0;
            double sign = c1 >= c0 ? 1 : -1;
            double lo = Math.Max(0, Math.Min(c0, c1)), hi = Math.Min(L, Math.Max(c0, c1));
            if (hi - lo <= 1e-9) return 0;
            const int N = 8;
            double h = (hi - lo) / N, acc = 0;
            for (int k = 0; k <= N; k++)
                acc += ((k == 0 || k == N) ? 1 : (k % 2 == 1 ? 4 : 2)) * AreaAt(s, (lo + k * h) / L);
            return sign * acc * h / 3.0;
        }

        // Axis coordinate where each station's section plane cuts the pipe axis (see Row).
        private static double[] AxisCuts(List<CrossSectionStation> st, List<StInfo> info,
                                         SectionDebugRow s, out int noDir)
        {
            noDir = 0;
            var c = new double[st.Count];
            double ux = s.EndX - s.StartX, uy = s.EndY - s.StartY, L = Math.Sqrt(ux * ux + uy * uy);
            if (L < 1e-9) return c;
            ux /= L; uy /= L;
            for (int i = 0; i < st.Count; i++)
            {
                double wx = st[i].WorldX - s.StartX, wy = st[i].WorldY - s.StartY;
                double proj = wx * ux + wy * uy;
                c[i] = proj;
                if (info[i].K <= 1.0005) continue;
                int j = (i + 1 < st.Count && info[i + 1].K > 1.0005) ? i + 1
                      : (i > 0 && info[i - 1].K > 1.0005) ? i - 1 : -1;
                if (j < 0) { noDir++; continue; }
                double bx = st[j].WorldX - st[i].WorldX, by = st[j].WorldY - st[i].WorldY;
                double bl = Math.Sqrt(bx * bx + by * by);
                if (bl < 1e-9) { noDir++; continue; }
                bx /= bl; by /= bl;
                double ub = ux * bx + uy * by;
                if (Math.Abs(ub) < 1e-6) { noDir++; continue; }
                c[i] = (wx * bx + wy * by) / ub;
            }
            return c;
        }

        private static StInfo Classify(CrossSectionStation x, SectionDebugRow s)
        {
            double aEx  = ClipperGeo.Area(x.ExcavPoly);
            double aBed = Has(x.BeddingPoly)  ? ClipperGeo.Area(x.BeddingPoly)  : 0;
            double aSur = Has(x.SurroundPoly) ? ClipperGeo.Area(x.SurroundPoly) : 0;
            double aBf  = Has(x.BackfillPoly) ? ClipperGeo.Area(x.BackfillPoly) : 0;

            var i = new StInfo
            {
                Gap   = aEx - aBed - aSur - aBf,
                Ex    = aEx,
                Clamp = Math.Max(0, s.PipeArea - aSur),
                K     = 1.0,
                Cat   = Cat.Other,
            };
            // Bisector frame: bottom width = TrWidth / cos(θ/2).
            double bottomW = Math.Abs(x.ExcavPoly[1][0] - x.ExcavPoly[0][0]);
            if (s.TrWidth > 1e-9 && bottomW > 1e-9) i.K = bottomW / s.TrWidth;

            double topEx = x.ExcavPoly.Max(v => v[1]);
            if (Has(x.BackfillPoly))
            {
                double topBf = x.BackfillPoly.Max(v => v[1]);
                if (Math.Abs(topEx - topBf) > 1e-3) i.Cat = Cat.Datum;
            }
            else if (Math.Abs(i.Gap) > 1e-4)
                i.Cat = Cat.Degenerate;
            return i;
        }

        // DwgBoQStore persists an empty polygon as 4 zero vertices → treat zero area as "none".
        private static bool Has(List<double[]> p) => p != null && p.Count >= 3 && ClipperGeo.Area(p) > 0;
    }
}
