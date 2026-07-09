using System;
using System.Linq;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Computes SectionDebugRow.NetLength = Length2D minus each connected manhole's
    /// half-width. In OuterDiameter mode (default, BoQSettings.NetLengthMode) that's
    /// inner half-width + wall thickness of the precast ring occupying this pipe's
    /// invert elevation; in InnerDiameter mode it's the inner half-width only.
    ///
    /// Must run after ManholeAIService.Process() (needs ManholeItem.ExcavationDepth
    /// and StackPreCast/StackCastInPlace to be resolved). Runtime-only — NetLength is
    /// never persisted to the DWG.
    /// </summary>
    public static class PipeNetLengthService
    {
        public static void Compute(BoQReport report, NetLengthMode mode = NetLengthMode.OuterDiameter)
        {
            // Global lookup across ALL systems — not scoped per system — so a pipe whose
            // two ends belong to manholes in different networks (cross-system connection)
            // still gets both ends reduced, not just the one sharing the pipe's own system.
            var byName = report.Systems
                .SelectMany(s => s.Manholes)
                .GroupBy(m => m.NodeName)
                .ToDictionary(g => g.Key, g => g.First());

            foreach (var row in report.SectionDebug)
            {
                byName.TryGetValue(row.StartNodeName, out var startMh);
                byName.TryGetValue(row.EndNodeName, out var endMh);

                double redStart = Reduction(startMh, row.InvertStart, mode);
                double redEnd   = Reduction(endMh, row.InvertEnd, mode);
                row.NetLength = Math.Max(0, row.Length2D - redStart - redEnd);
            }
        }

        /// <summary>Half-width (m) to deduct for one connected manhole end. Uses
        /// ManholeItem.ResolvedFootprint — the ACTUAL resolved precast Taban's shape/size
        /// — never DrawnShape/Diameter/DrawnLengthM/DrawnWidthM, which reflect how the manhole
        /// was drawn in Urbano and can disagree with the real linked precast family (e.g. drawn
        /// as a circle placeholder but built from a square Taban). Wall thickness of the ring
        /// at the pipe's invert is added only in OuterDiameter mode.</summary>
        private static double Reduction(ManholeItem mh, double invertZ, NetLengthMode mode)
        {
            var fp = mh?.ResolvedFootprint;
            if (fp == null) return 0; // Taban not resolved — no reduction (raw length)

            double innerHalfM;
            switch (fp.Shape)
            {
                case FootprintShape.Circular:
                    if (fp.DiameterMm <= 0) return 0;
                    innerHalfM = fp.DiameterMm / 2000.0;
                    break;
                case FootprintShape.Square:
                    if (fp.SideMm <= 0) return 0;
                    innerHalfM = fp.SideMm / 2000.0;
                    break;
                default: // Rectangular — shorter side, regardless of approach direction
                    double shortSideMm = Math.Min(fp.LengthMm, fp.WidthMm);
                    if (shortSideMm <= 0) return 0;
                    innerHalfM = shortSideMm / 2000.0;
                    break;
            }

            if (mode == NetLengthMode.InnerDiameter)
                return innerHalfM; // user chose inner-only — never add wall thickness

            var stack = mh.StackPreCast ?? mh.StackCastInPlace;
            if (stack != null && stack.Parts.Count > 0)
            {
                // Parts are stacked strictly bottom-to-top starting at the Taban's
                // physical bottom face (mh.ExcavationDepth already includes the Taban
                // slab + sub-base layers below it — see ManholeExcavOverlapService's
                // own zBottom = ZKazi - ExcavationDepth).
                double cum = mh.ZKazi - mh.ExcavationDepth;
                foreach (var part in stack.Parts)
                {
                    double top = cum + part.HeightM * part.Count;
                    if (invertZ >= cum - 1e-6 && invertZ <= top + 1e-6)
                    {
                        return part.WallThicknessMm.HasValue
                            ? innerHalfM + part.WallThicknessMm.Value / 1000.0
                            : innerHalfM;
                    }
                    cum = top;
                }
            }

            return innerHalfM; // no stack / ring not found — inner half-width only
        }
    }
}
