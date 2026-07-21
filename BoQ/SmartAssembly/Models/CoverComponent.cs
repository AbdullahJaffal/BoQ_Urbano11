namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>Manhole frame and cover (grate or solid lid).</summary>
    public class CoverComponent : ManholeComponent
    {
        public CoverComponent() { Role = ComponentRole.Cover; }

        /// <summary>Load class per EN 124 — e.g. "D400", "B125", "E600".</summary>
        public string LoadClass      { get; set; }

        /// <summary>Clear opening diameter / dimension (mm).</summary>
        public double ClearOpeningMm { get; set; }
    }
}
