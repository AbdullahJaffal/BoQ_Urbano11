using System;
using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// DWG-scoped project configuration that overrides the Master Catalog for a specific job.
    ///
    /// Created by cloning Master Rules into the DWG NOD so the project engineer can diverge
    /// from company defaults (e.g. force 1000 mm base for pipes up to 600 mm due to site stock).
    ///
    /// Serialized to: NOD["URBANO_BOQ"] → "MANHOLE_TEMPLATES" → [TemplateName]
    /// </summary>
    public class ProjectTemplate
    {
        public Guid   Id                           { get; set; } = Guid.NewGuid();
        public string TemplateName                 { get; set; }

        /// <summary>
        /// String key referencing an entry in the external trench excavation catalog
        /// (e.g. "TrenchProfile_RockyGround"). The excavation engine resolves this at run time.
        /// </summary>
        public string AssociatedExcavationProfile  { get; set; }

        /// <summary>
        /// When set, the assembly algorithm must only select shaft / reducer / adjuster / cover
        /// parts whose <see cref="ManholeComponent.FamilyTag"/> matches this value.
        /// Empty string = no restriction (use Master Catalog defaults).
        /// </summary>
        public string MaterialFamilyOverride       { get; set; }

        /// <summary>
        /// Legacy flat rule list — kept for backward-compatible NOD serialization only.
        /// The UI uses <see cref="LocalPipeRules"/> instead.
        /// </summary>
        public List<AssemblyRule>  LocalRules       { get; set; } = new List<AssemblyRule>();

        /// <summary>
        /// Hierarchical local rules (pipe-range → depth tiers), overriding the Master Catalog
        /// for this specific DWG project.
        /// </summary>
        public List<PipeRangeRule> LocalPipeRules   { get; set; } = new List<PipeRangeRule>();

        public DateTime LastModified               { get; set; } = DateTime.UtcNow;
    }
}
