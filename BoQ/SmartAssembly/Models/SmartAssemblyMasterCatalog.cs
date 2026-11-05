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

        /// <summary>The family that owns <paramref name="componentId"/>, or null.</summary>
        public ComponentFamily FindFamilyOf(Guid componentId)
        {
            if (componentId == Guid.Empty) return null;
            foreach (var f in Families)
                foreach (var c in f.Components)
                    if (c.Id == componentId) return f;
            return null;
        }

        /// <summary>
        /// "Baca Aile" of a master pipe range: its explicit <see cref="PipeRangeRule.ManholeFamilyId"/>,
        /// else — for ranges saved before that field existed — the single family owning every precast
        /// tier's Taban. Guid.Empty when the tiers mix families, or none has a Taban (all cast-in-situ).
        /// </summary>
        public Guid ResolveManholeFamilyId(PipeRangeRule rule)
        {
            if (rule == null) return Guid.Empty;
            if (rule.ManholeFamilyId != Guid.Empty) return rule.ManholeFamilyId;
            Guid found = Guid.Empty;
            foreach (var t in rule.DepthTiers ?? new List<DepthTierRule>())
            {
                if (t.IsCastInSitu) continue;
                var fam = FindFamilyOf(t.SelectedBaseId);
                if (fam == null) continue;
                if (found == Guid.Empty) found = fam.Id;
                else if (found != fam.Id) return Guid.Empty;   // mixed families
            }
            return found;
        }
    }
}
