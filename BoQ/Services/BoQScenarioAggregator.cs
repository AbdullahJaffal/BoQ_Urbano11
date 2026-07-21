using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Aggregates cached per-station scenario geometry into pipe volumes for a
    /// chosen preference combination — WITHOUT re-running any Clipper boolean ops.
    ///
    /// Two independent preferences drive the result, matching the two BoQ_VIEW
    /// drop-downs:
    ///   • Kazı  → applies to the Excavation layer only.
    ///   • Dolgu → applies to the Bedding, Surround and Backfill layers together.
    /// Excavation is an isolated overlap track, so the two dimensions never
    /// interact and any (Kazı, Dolgu) combination is a pure cache lookup.
    /// </summary>
    public static class BoQScenarioAggregator
    {
        /// <summary>Maps the stored <see cref="OverlapAssignment"/> to a scenario key.</summary>
        public static TiePreference Map(OverlapAssignment a)
        {
            switch (a)
            {
                case OverlapAssignment.LowerPipe: return TiePreference.KeepLower;
                case OverlapAssignment.UpperPipe: return TiePreference.KeepUpper;
                case OverlapAssignment.Ignore:    return TiePreference.Ignore;
                default:                          return TiePreference.Split;
            }
        }

        /// <summary>
        /// Recomputes one section's layer volumes (prismatoid, average end-area)
        /// from the cached scenario areas under the given preferences, writing them
        /// back into the row. Also records the overlap deducted vs the gross areas.
        /// </summary>
        public static void RecomputeRow(SectionDebugRow sdr, TiePreference kazi, TiePreference dolgu)
        {
            // ── Fast path: V2 pre-computed volumes (stored in DWG) ────────────
            // kazı and dolgu are fully independent — each picks its own scenario.
            if (sdr.HasV2Volumes)
            {
                // Excavation
                if (kazi == TiePreference.Ignore)
                {
                    sdr.VExcav               = sdr.VExcavGross;
                    sdr.OverlapExcavDeducted = 0;
                }
                else
                {
                    sdr.VExcav = kazi == TiePreference.KeepUpper ? sdr.VExcavKU
                               : kazi == TiePreference.KeepLower ? sdr.VExcavKL
                               :                                   sdr.VExcavSP;
                    sdr.OverlapExcavDeducted = System.Math.Max(0, sdr.VExcavGross - sdr.VExcav);
                }

                // Dolgu (bedding / surround / backfill)
                if (dolgu == TiePreference.Ignore)
                {
                    sdr.VBackfill               = sdr.VBackfillGross;
                    sdr.OverlapBackfillDeducted = 0;
                }
                else
                {
                    sdr.VBackfill = dolgu == TiePreference.KeepUpper ? sdr.VBackfillKU
                                  : dolgu == TiePreference.KeepLower ? sdr.VBackfillKL
                                  :                                    sdr.VBackfillSP;
                    sdr.OverlapBackfillDeducted = System.Math.Max(0, sdr.VBackfillGross - sdr.VBackfill);
                }
                ApplyLayerSplits(sdr);
                return;
            }

            // ── Slow path: station-by-station integration ─────────────────────
            // NetArea(Ignore) returns the gross area, so vEx/vBa will equal gEx/gBa
            // when Ignore is selected → deduction becomes 0 automatically.
            var st = sdr.Stations;
            if (st == null || st.Count < 2)
            {
                sdr.VExcav = sdr.VBedding = sdr.VSurround = sdr.VBackfill = 0;
                sdr.OverlapExcavDeducted    = 0;
                sdr.OverlapBackfillDeducted = 0;
                return;
            }

            double vEx = 0, vBe = 0, vSu = 0, vBa = 0;
            double gEx = 0, gBe = 0, gSu = 0, gBa = 0;

            for (int i = 0; i < st.Count - 1; i++)
            {
                double ddx = st[i + 1].WorldX - st[i].WorldX;
                double ddy = st[i + 1].WorldY - st[i].WorldY;
                double d   = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (d <= 1e-9) continue;

                vEx += Avg(NetArea(st[i],     kazi,  TrenchLayerType.Excavation),
                           NetArea(st[i + 1], kazi,  TrenchLayerType.Excavation)) * d;
                vBe += Avg(NetArea(st[i],     dolgu, TrenchLayerType.Bedding),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Bedding))    * d;
                vSu += Avg(NetArea(st[i],     dolgu, TrenchLayerType.Surround),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Surround))   * d;
                vBa += Avg(NetArea(st[i],     dolgu, TrenchLayerType.Backfill),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Backfill))   * d;

                gEx += Avg(st[i].AreaExcav,    st[i + 1].AreaExcav)    * d;
                gBe += Avg(st[i].AreaBedding,  st[i + 1].AreaBedding)  * d;
                gSu += Avg(st[i].AreaSurround, st[i + 1].AreaSurround) * d;
                gBa += Avg(st[i].AreaBackfill, st[i + 1].AreaBackfill) * d;
            }

            // Excavation averaging correction (only applies to non-Ignore scenarios)
            if (kazi != TiePreference.Ignore)
            {
                double adj = 0;
                switch (kazi)
                {
                    case TiePreference.KeepUpper: adj = sdr.ExcavAvgAdjKU; break;
                    case TiePreference.KeepLower: adj = sdr.ExcavAvgAdjKL; break;
                    case TiePreference.Split:     adj = sdr.ExcavAvgAdjSP; break;
                }
                vEx = System.Math.Max(0, vEx + adj);
            }

            sdr.VExcav    = System.Math.Max(0, vEx);
            sdr.VBedding  = System.Math.Max(0, vBe);
            sdr.VSurround = System.Math.Max(0, vSu);
            sdr.VBackfill = System.Math.Max(0, vBa);

            // Deductions: 0 when Ignore (vEx == gEx in that case anyway, but be explicit)
            sdr.OverlapExcavDeducted    = kazi  == TiePreference.Ignore ? 0
                : System.Math.Max(0, gEx - sdr.VExcav);
            sdr.OverlapBackfillDeducted = dolgu == TiePreference.Ignore ? 0
                : System.Math.Max(0, (gBe - vBe) + (gSu - vSu) + (gBa - vBa));

            ApplyLayerSplits(sdr);
        }

        /// <summary>
        /// Splits the final (post-clash) combined layer totals — VBedding, VSurround,
        /// VBackfill — into per-catalog-sub-layer volumes using each split list's fixed
        /// area-ratio (computed once at parse time from the matched PipeTrenchCatalog
        /// tier's geometry). Pure post-processing: never re-runs any Clipper boolean op.
        /// Gömlekleme (Surround) needs an extra add-back/split/re-deduct sequence since
        /// the pipe body is already subtracted from the COMBINED Boru-Etrafı+Boru-Üstü
        /// total, but only Boru Etrafı physically contains the pipe. See project memory
        /// "Trench layer splitting design" for the full derivation.
        /// </summary>
        private static void ApplyLayerSplits(SectionDebugRow sdr)
        {
            ApplyRatios(sdr.BeddingLayerSplits,  sdr.VBedding);
            ApplyRatios(sdr.BackfillLayerSplits, sdr.VBackfill);

            double vPipe  = sdr.PipeArea * sdr.Length2D;
            double sPrime = sdr.VSurround + vPipe;

            // Etrafı/Üstü area split, from fields already resolved in ParseSections
            // (no new inputs needed): width-at-height-h = TopWidthBed + 2×h×SlopeRatio.
            double wAtOd      = sdr.TopWidthBed + 2.0 * sdr.PipeOuterDiamM * sdr.SlopeRatio;
            double areaEtrafi = (sdr.TopWidthBed + wAtOd) / 2.0 * sdr.PipeOuterDiamM;
            double areaUstu   = (wAtOd + sdr.TopWidthSurr) / 2.0 * sdr.TrSandOverPipe;
            double areaTotal  = areaEtrafi + areaUstu;

            double sEtrafiPrime = areaTotal > 1e-12 ? sPrime * (areaEtrafi / areaTotal) : sPrime;
            double sUstuPrime   = sPrime - sEtrafiPrime;

            double vEtrafiFinal = System.Math.Max(0, sEtrafiPrime - vPipe);
            double vUstuFinal   = System.Math.Max(0, sUstuPrime);

            ApplyRatios(sdr.BoruEtrafiLayerSplits, vEtrafiFinal);
            ApplyRatios(sdr.BoruUstuLayerSplits,   vUstuFinal);
        }

        private static void ApplyRatios(List<TrenchLayerSplit> splits, double groupTotalVolume)
        {
            foreach (var s in splits)
                s.Volume = s.Ratio * groupTotalVolume;
        }

        /// <summary>
        /// Recomputes every section's volumes and rebuilds the per-system pipe
        /// aggregates for the given preferences. Manholes are left untouched.
        /// This is what the BoQ_VIEW "Hesapla" button calls after the user changes
        /// a drop-down.
        /// </summary>
        public static void Apply(BoQReport report, TiePreference kazi, TiePreference dolgu,
                                  bool applyManholeDeduction = false)
        {
            if (report?.SectionDebug == null) return;

            foreach (var sdr in report.SectionDebug)
            {
                RecomputeRow(sdr, kazi, dolgu);
                if (applyManholeDeduction)
                {
                    sdr.VExcav    = System.Math.Max(0, sdr.VExcav    - sdr.ManholeExcavDeducted);
                    sdr.VBackfill = System.Math.Max(0, sdr.VBackfill - sdr.ManholeBackfillDeducted);
                }
            }

            foreach (var sys in report.Systems ?? Enumerable.Empty<SystemBoQ>())
            {
                var rows = report.SectionDebug.Where(r => r.SystemName == sys.SystemName);
                sys.Pipes = rows
                    .GroupBy(r => new { r.DiameterMm, Mat = r.Material ?? "" })
                    .OrderBy(g => g.Key.DiameterMm)
                    .Select(g => new PipeItem
                    {
                        Diameter              = g.Key.DiameterMm,
                        Material              = g.Key.Mat,
                        TotalLength           = g.Sum(r => r.Length2D),
                        TotalExcavationVolume = g.Sum(r => r.VExcav),
                        TotalBeddingVolume    = g.Sum(r => r.VBedding),
                        TotalSurroundVolume   = g.Sum(r => r.VSurround),
                        TotalBackfillVolume   = g.Sum(r => r.VBackfill),
                        OverlapExcavDeducted    = g.Sum(r => r.OverlapExcavDeducted),
                        OverlapBackfillDeducted = g.Sum(r => r.OverlapBackfillDeducted),
                    })
                    .ToList();
            }
        }

        // ── helpers ───────────────────────────────────────────────────────────

        private static double Avg(double a, double b) => (a + b) * 0.5;

        /// <summary>
        /// Cached net area of a layer under a preference; falls back to the gross
        /// station area when no scenario is present (e.g. data from an old DWG).
        /// </summary>
        private static double NetArea(CrossSectionStation s, TiePreference p, TrenchLayerType layer)
        {
            // Ignore mode: skip scenario lookup, return full gross area (no deduction).
            if (p == TiePreference.Ignore)
            {
                switch (layer)
                {
                    case TrenchLayerType.Excavation: return s.AreaExcav;
                    case TrenchLayerType.Bedding:    return s.AreaBedding;
                    case TrenchLayerType.Surround:   return s.AreaSurround;
                    default:                         return s.AreaBackfill;
                }
            }

            var prof = s.Scenario(p);
            if (prof != null) return prof.Layer(layer).NetArea;

            switch (layer)   // legacy fallback (old DWG without scenario data)
            {
                case TrenchLayerType.Excavation: return s.AreaExcav;
                case TrenchLayerType.Bedding:    return s.AreaBedding;
                case TrenchLayerType.Surround:   return s.AreaSurround;
                default:                         return s.AreaBackfill;
            }
        }
    }
}
