namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Shared square-frustum excavation volume formula (Phase 7). Used by
    /// ManholeAIService (the real, catalog-driven per-manhole excavation
    /// volume) so it never drifts from the same shape ManholeExcavOverlapService
    /// builds its polygons with — both now read the same per-manhole
    /// ExcavBaseSideM/ExcavSlopeRatio computed once in ManholeAIService.
    /// </summary>
    internal static class ManholeExcavationGeometry
    {
        /// <summary>
        /// Square frustum volume via Simpson's 1/3 rule. baseSideM is the side
        /// length at the pit's bottom (rise = 0); each face grows outward by
        /// slopeRatio metres of run per metre of rise.
        /// </summary>
        internal static double ComputeFrustumVolume(double baseSideM, double heightM, double slopeRatio)
        {
            if (heightM <= 1e-9 || baseSideM <= 0) return 0;

            double sideMid = baseSideM + 2.0 * (heightM * 0.5) * slopeRatio;
            double sideTop = baseSideM + 2.0 * heightM * slopeRatio;

            double aBot = baseSideM * baseSideM;
            double aMid = sideMid * sideMid;
            double aTop = sideTop * sideTop;

            return (heightM / 6.0) * (aBot + 4.0 * aMid + aTop);
        }
    }
}
