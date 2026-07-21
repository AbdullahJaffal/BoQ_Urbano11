namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>Adjustment ring / neck ring used to fine-tune the assembly to finished road grade.</summary>
    public class AdjusterComponent : ManholeComponent
    {
        public AdjusterComponent() { Role = ComponentRole.Adjuster; }

        /// <summary>Inner clear diameter (mm) — must match top of reducer above it.</summary>
        public double InnerDiameterMm  { get; set; }

        /// <summary>Precast wall thickness (mm).</summary>
        public double WallThicknessMm  { get; set; }
    }
}
