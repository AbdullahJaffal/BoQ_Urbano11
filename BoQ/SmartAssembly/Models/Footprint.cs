using System;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Plan-view footprint of a <see cref="BottomElementComponent"/>, used to bound the excavation pit.
    /// A square chamber base can have a rectangular footprint while still exposing a circular top opening.
    /// </summary>
    public class Footprint
    {
        public FootprintShape Shape { get; set; } = FootprintShape.Circular;

        /// <summary>Outer diameter (mm) — used when Shape = Circular.</summary>
        public double DiameterMm { get; set; }

        /// <summary>Long side (mm) — used when Shape = Rectangular.</summary>
        public double LengthMm   { get; set; }

        /// <summary>Short side (mm) — used when Shape = Rectangular.</summary>
        public double WidthMm    { get; set; }

        /// <summary>Side length (mm) — used when Shape = Square.</summary>
        public double SideMm     { get; set; }

        /// <summary>
        /// Orientation of the rectangular base relative to the pipe direction.
        /// One of: "uzun kenar; çıkış boru yönünde" / "uzun kenar; giriş boru yönünde" /
        ///         "kısa kenar; çıkış boru yönünde" / "kısa kenar; giriş boru yönünde".
        /// Only relevant when Shape = Rectangular.
        /// </summary>
        public string RectangularOrientation { get; set; } = "";

        /// <summary>Computed plan area in m².</summary>
        public double ComputedAreaM2 =>
            Shape == FootprintShape.Circular
                ? Math.PI * Math.Pow(DiameterMm / 2000.0, 2)
                : Shape == FootprintShape.Square
                    ? Math.Pow(SideMm / 1000.0, 2)
                    : (LengthMm / 1000.0) * (WidthMm / 1000.0);

        public string DisplayString =>
            Shape == FootprintShape.Circular
                ? string.Format("Ø{0:0} mm", DiameterMm)
                : Shape == FootprintShape.Square
                    ? string.Format("{0:0}×{0:0} mm (kare)", SideMm)
                    : string.Format("{0:0}×{1:0} mm", LengthMm, WidthMm);
    }
}
