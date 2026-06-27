namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>Precast shaft ring — the repeating body element stacked between base and cone.</summary>
    public class MiddleElementComponent : ManholeComponent
    {
        public MiddleElementComponent() { Role = ComponentRole.MiddleElement; }

        /// <summary>Inner clear diameter of the ring (mm). Links up to BottomElement.TopOpeningDiameterMm.</summary>
        public double InnerDiameterMm  { get; set; }

        /// <summary>Precast wall thickness (mm).</summary>
        public double WallThicknessMm  { get; set; }
    }
}
