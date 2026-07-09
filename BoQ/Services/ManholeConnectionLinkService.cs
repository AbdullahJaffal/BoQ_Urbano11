using System;
using System.Collections.Generic;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Post-processing step for the "Baca Keşif Tablosu" command — links each
    /// ManholeItem to its connected inlet/outlet pipes (neighbor manhole name,
    /// invert elevation, diameter, straight-line distance).
    ///
    /// Matches against the FULL report.SectionDebug — NOT scoped to the manhole's
    /// own SystemName — because a manhole can sit at a junction between two
    /// networks (e.g. a manhole listed under "ASU" can have a connecting pipe
    /// whose SectionDebugRow.SystemName is "YSU"); NodeName is the only reliable
    /// join key. This mirrors ManholeAIService.ProcessManhole's own inlet/outlet
    /// resolution, which likewise matches against the full section list rather
    /// than filtering by system (BoQ/Services/ManholeAIService.cs, ~line 159/224-229).
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

            var lookup = new Dictionary<string, ManholeItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var sys in report.Systems)
            {
                if (sys?.Manholes == null) continue;
                foreach (var m in sys.Manholes)
                {
                    if (string.IsNullOrEmpty(m.NodeName)) continue;
                    lookup[m.NodeName] = m;
                    m.Inlets.Clear();
                    m.Outlets.Clear();
                    m.TotalPipeExcavOverlap = 0;
                }
            }
            if (lookup.Count == 0) return;

            foreach (var s in report.SectionDebug ?? new List<SectionDebugRow>())
            {
                if (lookup.TryGetValue(s.EndNodeName ?? "", out var inletMh))
                {
                    inletMh.Inlets.Add(new ManholeConnectionInfo
                    {
                        NeighborNodeName = s.StartNodeName,
                        InvertElevation  = s.InvertEnd,
                        DiameterMm       = s.DiameterMm,
                        Distance2D       = s.Length2D,
                        NetLength        = s.NetLength,
                        SystemName       = s.SystemName
                    });
                    inletMh.TotalPipeExcavOverlap += s.ManholeExcavDeducted;
                }

                if (lookup.TryGetValue(s.StartNodeName ?? "", out var outletMh))
                {
                    outletMh.Outlets.Add(new ManholeConnectionInfo
                    {
                        NeighborNodeName = s.EndNodeName,
                        InvertElevation  = s.InvertStart,
                        DiameterMm       = s.DiameterMm,
                        Distance2D       = s.Length2D,
                        NetLength        = s.NetLength,
                        SystemName       = s.SystemName
                    });
                    outletMh.TotalPipeExcavOverlap += s.ManholeExcavDeducted;
                }
            }
        }
    }
}
