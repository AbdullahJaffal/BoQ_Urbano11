namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// A single sub-base piece placed physically below a <see cref="BottomElementComponent"/>.
    /// Contributes to excavation / backfill depth calculation but does NOT affect
    /// ring distribution (ComputeMinDepthM uses EffectiveHeight only).
    /// </summary>
    public class TemelAltiParca
    {
        public string Ad       { get; set; } = "";
        public double Boy      { get; set; }  // length mm
        public double En       { get; set; }  // width mm
        public double Kalinlik { get; set; }  // thickness mm
        public string Malzeme  { get; set; } = "";
    }
}
