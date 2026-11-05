using System;
using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Groups depth tiers for a single pipe-diameter band.
    ///
    /// Catalog-linked fields (<see cref="SelectedPipeFamilyId"/>, <see cref="MinPipeId"/>,
    /// <see cref="MaxPipeId"/>) hold strong references into the shared PipeCatalog.
    /// When set, the numeric <see cref="MinPipeMm"/>/<see cref="MaxPipeMm"/> fields are
    /// derived from the linked <see cref="UrbanoMetraj.BoQ.PipeCatalog.Models.PipeDefinition"/>
    /// and kept in sync by the ViewModel.  Both fields are persisted for backward
    /// compatibility when no catalog is loaded.
    /// </summary>
    public class PipeRangeRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Lower bound of the connected-pipe nominal diameter (mm, inclusive).
        /// Auto-derived from <see cref="MinPipeId"/> when a catalog is attached.</summary>
        public double MinPipeMm { get; set; }

        /// <summary>Upper bound of the connected-pipe nominal diameter (mm, inclusive).
        /// Auto-derived from <see cref="MaxPipeId"/> when a catalog is attached.</summary>
        public double MaxPipeMm { get; set; }

        // ── Catalog references (Guid.Empty = not set) ─────────────────────────

        /// <summary>The PipeFamily this rule is scoped to.</summary>
        public Guid SelectedPipeFamilyId { get; set; }

        /// <summary>The manhole ComponentFamily ("Baca Aile") of this pipe range: every precast tier's
        /// Taban must come from it (one family per range — decision 2026-09-25). Guid.Empty on rules
        /// saved before the field existed; see SmartAssemblyMasterCatalog.ResolveManholeFamilyId.</summary>
        public Guid ManholeFamilyId { get; set; }

        /// <summary>Specific PipeDefinition that defines the lower diameter bound.</summary>
        public Guid MinPipeId { get; set; }

        /// <summary>Specific PipeDefinition that defines the upper diameter bound.</summary>
        public Guid MaxPipeId { get; set; }

        public List<DepthTierRule> DepthTiers { get; set; } = new List<DepthTierRule>();

        /// <summary>Optional reference to a <see cref="SystemType"/> that scopes this rule to a specific network type.</summary>
        public Guid SystemTypeId { get; set; }
    }
}
