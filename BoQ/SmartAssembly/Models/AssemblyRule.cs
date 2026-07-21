using System;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// One row in the Rule Engine Matrix.
    /// Selects which <see cref="BottomElementComponent"/> to use based on pipe diameter
    /// and burial depth ranges. First matching rule in the ordered list wins.
    /// </summary>
    public class AssemblyRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        // ── Condition axes ────────────────────────────────────────────────────

        /// <summary>Lower bound of the connected-pipe diameter range (mm, inclusive).</summary>
        public double MinPipeDiameterMm  { get; set; }

        /// <summary>Upper bound of the connected-pipe diameter range (mm, inclusive).</summary>
        public double MaxPipeDiameterMm  { get; set; }

        /// <summary>Lower bound of the manhole burial depth range (m, inclusive).</summary>
        public double MinDepthM          { get; set; }

        /// <summary>Upper bound of the manhole burial depth range (m, inclusive). 0 = unlimited.</summary>
        public double MaxDepthM          { get; set; }

        // ── Result ────────────────────────────────────────────────────────────

        /// <summary>
        /// References a <see cref="BottomElementComponent.Id"/> in the component repository.
        /// Ignored when <see cref="IsCastInSitu"/> = true.
        /// </summary>
        public Guid SelectedBaseId       { get; set; }

        /// <summary>
        /// When true, the assembly bin-packing algorithm is bypassed entirely for this
        /// condition — a cast-in-situ concrete manhole is prescribed instead.
        /// </summary>
        public bool IsCastInSitu         { get; set; }

        public string Notes              { get; set; }

        // ── Provenance ────────────────────────────────────────────────────────

        /// <summary>True when this rule was cloned from the Master Catalog and locally modified.</summary>
        public bool IsLocalOverride      { get; set; }

        /// <summary>Id of the Master Catalog rule this was cloned from (Guid.Empty if original).</summary>
        public Guid SourceMasterRuleId   { get; set; }
    }
}
