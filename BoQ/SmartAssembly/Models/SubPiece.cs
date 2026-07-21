namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>One layer in a composite BottomElement (e.g. main slab + leveling ring).</summary>
    public class SubPiece
    {
        public string Name        { get; set; }
        /// <summary>Height contribution of this sub-piece (mm).</summary>
        public double HeightMm    { get; set; }
        public string Description { get; set; }
    }
}
