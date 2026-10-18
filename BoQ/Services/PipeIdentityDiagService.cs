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

                for (int k = 0; k + 1 < st.Count; k++)
                {
                    double dx = st[k + 1].WorldX - st[k].WorldX, dy = st[k + 1].WorldY - st[k].WorldY;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < 1e-9) continue;
                    var a = info[k]; var b = info[k + 1];

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

        private struct StInfo { public double Gap, Clamp, K; public Cat Cat; }

        // ∫ trench area dt over [0, Length2D] — composite Simpson, 40 intervals (the area
        // is quadratic in t except where the depth clamps at 0).
        private static double ExcavReference(SectionDebugRow s)
        {
            double L = s.Length2D;
            if (L <= 1e-9) return 0;
            double wall = Math.Max(0, (s.PipeOuterDiamM - s.DiameterMm / 1000.0) / 2.0);
            double A(double f)
            {
                double zTop = s.StartTerrainZ + (s.EndTerrainZ - s.StartTerrainZ) * f;
                double inv  = s.InvertStart   + (s.InvertEnd   - s.InvertStart)   * f;
                double D    = Math.Max(0, zTop - (inv - wall - s.TrBedHeight));
                return D * (s.TrWidth + D * s.SlopeRatio);
            }
            const int N = 40;
            double acc = 0;
            for (int k = 0; k <= N; k++)
                acc += ((k == 0 || k == N) ? 1 : (k % 2 == 1 ? 4 : 2)) * A((double)k / N);
            return acc * (L / N) / 3.0;
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
