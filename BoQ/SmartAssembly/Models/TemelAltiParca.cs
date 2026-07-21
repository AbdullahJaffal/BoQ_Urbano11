namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// A sub-base element placed physically below a manhole (e.g. gravel bed, lean concrete pad).
    /// Contributes to excavation / backfill depth.
    /// IsVariable is always false — height is a fixed catalog value.
    /// <see cref="BaglandiTabanCapiMm"/> links this piece to the correct
    /// <see cref="BottomElementComponent"/> via its TopOpeningDiameterMm.
    /// </summary>
    public class TemelAltiParcaComponent : ManholeComponent
    {
        public TemelAltiParcaComponent() { Role = ComponentRole.TemelAltiParca; }

        /// <summary>Plan length of the sub-base slab / pad (mm).</summary>
        public double Boy              { get; set; }

        /// <summary>Plan width of the sub-base slab / pad (mm).</summary>
        public double En               { get; set; }

        /// <summary>
        /// TopOpeningDiameterMm of the Taban this sub-base belongs to (mm).
        /// Used to find the matching base when assembling the manhole.
        /// </summary>
        public double BaglandiTabanCapiMm { get; set; }
    }
}
