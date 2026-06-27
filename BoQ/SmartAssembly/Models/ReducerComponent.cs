namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>Eccentric or concentric taper cone connecting body shaft to neck.</summary>
    public class ReducerComponent : ManholeComponent
    {
        public ReducerComponent() { Role = ComponentRole.Reducer; }

        /// <summary>Inner clear diameter at the wide (bottom) end (mm).</summary>
        public double BottomInnerDiameterMm { get; set; }

        /// <summary>Inner clear diameter at the narrow (top) end (mm).</summary>
        public double TopInnerDiameterMm    { get; set; }

        /// <summary>Precast wall thickness (mm).</summary>
        public double WallThicknessMm       { get; set; }
    }
}
