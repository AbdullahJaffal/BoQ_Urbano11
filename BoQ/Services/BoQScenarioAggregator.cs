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
            var st = sdr.Stations;
            if (st == null || st.Count < 2)
            {
                sdr.VExcav = sdr.VBedding = sdr.VSurround = sdr.VBackfill = 0;
                sdr.OverlapVolumeDeducted = 0;
                return;
            }

            double vEx = 0, vBe = 0, vSu = 0, vBa = 0;
            double gEx = 0, gBe = 0, gSu = 0, gBa = 0;   // gross (no deduction) totals

            for (int i = 0; i < st.Count - 1; i++)
            {
                double ddx = st[i + 1].WorldX - st[i].WorldX;
                double ddy = st[i + 1].WorldY - st[i].WorldY;
                double d   = Math.Sqrt(ddx * ddx + ddy * ddy);
                if (d <= 1e-9) continue;

                vEx += Avg(NetArea(st[i], kazi,  TrenchLayerType.Excavation),
                           NetArea(st[i + 1], kazi,  TrenchLayerType.Excavation)) * d;
                vBe += Avg(NetArea(st[i], dolgu, TrenchLayerType.Bedding),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Bedding)) * d;
                vSu += Avg(NetArea(st[i], dolgu, TrenchLayerType.Surround),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Surround)) * d;
                vBa += Avg(NetArea(st[i], dolgu, TrenchLayerType.Backfill),
                           NetArea(st[i + 1], dolgu, TrenchLayerType.Backfill)) * d;

                gEx += Avg(st[i].AreaExcav,    st[i + 1].AreaExcav)    * d;
                gBe += Avg(st[i].AreaBedding,  st[i + 1].AreaBedding)  * d;
                gSu += Avg(st[i].AreaSurround, st[i + 1].AreaSurround) * d;
                gBa += Avg(st[i].AreaBackfill, st[i + 1].AreaBackfill) * d;
            }

            // Apply the integration-averaging correction so that KeepUpper, KeepLower
            // and Split all produce exactly the same total excavation volume.
            double adj = 0;
            switch (kazi)
            {
                case TiePreference.KeepUpper: adj = sdr.ExcavAvgAdjKU; break;
                case TiePreference.KeepLower: adj = sdr.ExcavAvgAdjKL; break;
                case TiePreference.Split:     adj = sdr.ExcavAvgAdjSP; break;
            }
            vEx = System.Math.Max(0, vEx + adj);

            sdr.VExcav    = vEx;
            sdr.VBedding  = vBe;
            sdr.VSurround = vSu;
            sdr.VBackfill = vBa;
            sdr.OverlapVolumeDeducted =
                System.Math.Max(0, (gEx - vEx) + (gBe - vBe) + (gSu - vSu) + (gBa - vBa));
        }

        /// <summary>
        /// Recomputes every section's volumes and rebuilds the per-system pipe
        /// aggregates for the given preferences. Manholes are left untouched.
        /// This is what the BoQ_VIEW "Hesapla" button calls after the user changes
        /// a drop-down.
        /// </summary>
        public static void Apply(BoQReport report, TiePreference kazi, TiePreference dolgu)
        {
            if (report?.SectionDebug == null) return;

            foreach (var sdr in report.SectionDebug)
                RecomputeRow(sdr, kazi, dolgu);

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
                        OverlapVolumeDeducted = g.Sum(r => r.OverlapVolumeDeducted),
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
