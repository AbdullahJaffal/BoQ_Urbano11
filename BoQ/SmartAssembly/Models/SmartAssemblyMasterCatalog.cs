using System;
using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Company-wide component repository and master rule matrix.
    /// Serialized exclusively to an external .xml file — never written to the DWG NOD.
    /// DWG projects clone and override rules into <see cref="ProjectTemplate"/> instances.
    /// </summary>
    public class SmartAssemblyMasterCatalog
    {
        public Guid     CatalogId    { get; set; } = Guid.NewGuid();
        public string   Version      { get; set; } = "1.0";
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>All registered components across all roles.</summary>
        public List<ManholeComponent> Components  { get; set; } = new List<ManholeComponent>();

        /// <summary>
        /// Legacy flat rule list — kept for backward-compatible XML serialization only.
        /// The UI and assembly engine use <see cref="MasterPipeRules"/> instead.
        /// </summary>
        public List<AssemblyRule>     MasterRules     { get; set; } = new List<AssemblyRule>();

        /// <summary>
        /// Hierarchical rule matrix (pipe-range → depth tiers). Evaluated in list order;
        /// first matching pipe range wins, then first matching depth tier within it.
        /// </summary>
        public List<PipeRangeRule>    MasterPipeRules { get; set; } = new List<PipeRangeRule>();

        // ── Convenience accessors ──────────────────────────────────────────────

        public IEnumerable<BottomElementComponent> GetBases()
        {
            foreach (var c in Components)
            {
                var b = c as BottomElementComponent;
                if (b != null) yield return b;
            }
        }

        public IEnumerable<MiddleElementComponent> GetShafts()
        {
            foreach (var c in Components)
            {
                var m = c as MiddleElementComponent;
                if (m != null) yield return m;
            }
        }

        public ManholeComponent FindById(Guid id)
        {
            foreach (var c in Components)
                if (c.Id == id) return c;
            return null;
        }
    }
}
