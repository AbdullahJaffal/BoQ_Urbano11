using System;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Abstract base for all typed manhole precast components.
    /// <see cref="EffectiveHeight"/> is in millimetres.
    /// </summary>
    public abstract class ManholeComponent
    {
        public Guid          Id              { get; set; } = Guid.NewGuid();
        /// <summary>Catalogue position number (e.g. "B.01", "Ø1200-H500"). Free text — no uniqueness enforced.</summary>
        public string        PozNo           { get; set; }
        public string        Name            { get; set; }
        /// <summary>Stacking height contribution of this component (mm).</summary>
        public double        EffectiveHeight { get; set; }
        /// <summary>Material / product family — e.g. "Standard-Precast", "HDPE", "Heavy-Duty".</summary>
        public string        FamilyTag       { get; set; }
        public ComponentRole Role            { get; set; }

        /// <summary>Total spatial displacement of this component (m³) — used for excavation and bedding volumes.</summary>
        public double ExternalVolume  { get; set; }

        /// <summary>Net concrete / HDPE volume of this component (m³) — used for material quantity take-off.</summary>
        public double MaterialVolume  { get; set; }

        /// <summary>
        /// When true the assembly engine treats this component's height as variable (site-fitted).
        /// The UI disables manual height entry and, for BottomElement, disables composite sub-pieces.
        /// </summary>
        public bool   IsVariable       { get; set; }

        /// <summary>Marks this component as mandatory in every assembly (non-optional). Not applicable to BottomElement.</summary>
        public bool   ZorunluParca     { get; set; }

        /// <summary>Marks this ring as a height-adjustment (yükseltme) piece. Only meaningful for MiddleElement and Adjuster.</summary>
        public bool   YukseltmeParcasi { get; set; }

        /// <summary>Free-text description / note for this component.</summary>
        public string Aciklama         { get; set; }
    }
}
