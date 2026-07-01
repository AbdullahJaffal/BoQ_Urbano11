using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Company-wide component repository and master rule matrix.
    /// Components are grouped into <see cref="ComponentFamily"/> objects (mirror of PipeFamily).
    /// Serialized exclusively to an external .xml file — never written to the DWG NOD.
    /// </summary>
    public class SmartAssemblyMasterCatalog
    {
        public Guid     CatalogId    { get; set; } = Guid.NewGuid();
        public string   Version      { get; set; } = "1.0";
        public DateTime LastModified { get; set; } = DateTime.UtcNow;

        /// <summary>All registered component families (each family groups components of the same material).</summary>
        public ObservableCollection<ComponentFamily> Families { get; set; }
            = new ObservableCollection<ComponentFamily>();

        /// <summary>
        /// Flat enumeration of all components across all families.
        /// Used by <see cref="GetBases"/>, <see cref="FindById"/>, and the rules engine.
        /// </summary>
        public IEnumerable<ManholeComponent> Components
        {
            get
            {
                foreach (var f in Families)
                    foreach (var c in f.Components)
                        yield return c;
            }
        }

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

        /// <summary>User-defined network/system types (e.g. "Yağmur Suyu", "Atık Su") used to tag pipe-range rules.</summary>
        public ObservableCollection<SystemType> SystemTypes { get; set; } = new ObservableCollection<SystemType>();

        // ── Convenience accessors ──────────────────────────────────────────────

        public IEnumerable<BottomElementComponent> GetBases()
        {
            foreach (var f in Families)
                foreach (var c in f.Components)
                {
                    var b = c as BottomElementComponent;
                    if (b != null) yield return b;
                }
        }

        public IEnumerable<MiddleElementComponent> GetShafts()
        {
            foreach (var f in Families)
                foreach (var c in f.Components)
                {
                    var m = c as MiddleElementComponent;
                    if (m != null) yield return m;
                }
        }

        public ManholeComponent FindById(Guid id)
        {
            foreach (var f in Families)
                foreach (var c in f.Components)
                    if (c.Id == id) return c;
            return null;
        }
    }
}
