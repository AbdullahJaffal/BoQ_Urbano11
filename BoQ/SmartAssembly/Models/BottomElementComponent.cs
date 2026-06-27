using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Manhole base slab / chamber floor element.
    ///
    /// Two independent geometry concepts:
    ///   <see cref="Footprint"/>           — plan bounds used by the excavation engine.
    ///   <see cref="TopOpeningDiameterMm"/> — strictly links this base to the MiddleElement above it,
    ///                                        even when the base itself is a large square chamber.
    ///
    /// Composite support: set <see cref="IsComposite"/> = true and fill <see cref="SubPieces"/>
    /// (e.g. main slab + leveling ring). <see cref="ManholeComponent.EffectiveHeight"/> must equal
    /// the sum of all SubPiece heights; the assembly engine reads it directly.
    /// </summary>
    public class BottomElementComponent : ManholeComponent
    {
        public BottomElementComponent() { Role = ComponentRole.BottomElement; }

        /// <summary>Precast wall thickness (mm) — used together with ExternalVolume/MaterialVolume for BOQ.</summary>
        public double        WallThicknessMm       { get; set; }

        /// <summary>
        /// Structural slab thickness at the bottom of the base (mm).
        /// Added to EffectiveHeight to give the full installed height of the base element.
        /// </summary>
        public double        TabanKalinligiMm      { get; set; }

        /// <summary>Plan-view footprint for excavation pit sizing.</summary>
        public Footprint     Footprint             { get; set; } = new Footprint();

        /// <summary>
        /// Inner clear diameter of the circular opening on top of this base (mm).
        /// Must match <see cref="MiddleElementComponent.InnerDiameterMm"/> of the shaft above.
        /// </summary>
        public double        TopOpeningDiameterMm  { get; set; }

        /// <summary>
        /// True when this base is shipped / quoted as multiple pieces whose heights sum to
        /// <see cref="ManholeComponent.EffectiveHeight"/>. The assembly engine counts it as one
        /// "base" slot but the BOQ line-item expander will list each sub-piece separately.
        /// </summary>
        public bool          IsComposite           { get; set; }

        public List<SubPiece> SubPieces            { get; set; } = new List<SubPiece>();
    }
}
