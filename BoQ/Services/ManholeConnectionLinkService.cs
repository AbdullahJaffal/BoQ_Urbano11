using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Post-processing step for the "Baca Keşif Tablosu" command — links each
    /// ManholeItem to its connected inlet/outlet pipes (neighbor manhole name,
    /// invert elevation, diameter, straight-line distance).
    ///
    /// Deliberately NOT wired into BoQParserService or ManholeAIService: it runs
    /// on an already-loaded BoQReport (report.SectionDebug already carries every
    /// field needed — StartNodeName/EndNodeName/InvertStart/InvertEnd/DiameterMm/
    /// Length2D — so no parser changes or new geometry formulas are required here;
    /// Length2D already IS the straight-line distance between the two manholes).
    /// </summary>
    public static class ManholeConnectionLinkService
    {
        public static void Populate(BoQReport report)
        {
            if (report?.Systems == null) return;

            foreach (var sys in report.Systems)
            {
                if (sys?.Manholes == null || sys.Manholes.Count == 0) continue;

                var lookup = new Dictionary<string, ManholeItem>(StringComparer.OrdinalIgnoreCase);
                foreach (var m in sys.Manholes)
                {
                    if (string.IsNullOrEmpty(m.NodeName)) continue;
                    lookup[m.NodeName] = m;
                    m.Inlets.Clear();
                    m.Outlets.Clear();
                }

                var rows = (report.SectionDebug ?? new List<SectionDebugRow>())
                    .Where(s => string.Equals(s.SystemName, sys.SystemName, StringComparison.Ordinal));

                foreach (var s in rows)
                {
                    if (lookup.TryGetValue(s.EndNodeName ?? "", out var inletMh))
                    {
                        inletMh.Inlets.Add(new ManholeConnectionInfo
                        {
                            NeighborNodeName = s.StartNodeName,
                            InvertElevation  = s.InvertEnd,
                            DiameterMm       = s.DiameterMm,
                            Distance2D       = s.Length2D
                        });
                    }

                    if (lookup.TryGetValue(s.StartNodeName ?? "", out var outletMh))
                    {
                        outletMh.Outlets.Add(new ManholeConnectionInfo
                        {
                            NeighborNodeName = s.EndNodeName,
                            InvertElevation  = s.InvertStart,
                            DiameterMm       = s.DiameterMm,
                            Distance2D       = s.Length2D
                        });
                    }
                }
            }
        }
    }
}
