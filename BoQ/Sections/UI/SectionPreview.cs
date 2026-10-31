using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using EntityColor = Autodesk.AutoCAD.Colors.EntityColor;

namespace UrbanoMetraj.BoQ.Sections.UI
{
    /// <summary>
    /// Live preview of one laid-out section (<see cref="SectionLayoutEngine.BuildSlot"/>) —
    /// the same primitives the AutoCAD renderer writes, drawn with the style's colours,
    /// linetypes and hatch tint on a model-space-like dark background.
    /// </summary>
    public sealed class SectionPreview : FrameworkElement
    {
        private SectionDrawing _drawing;
        private SectionStyle   _style;
        private string         _note = "";

        public static readonly Color Background = Color.FromRgb(0x21, 0x28, 0x30);

        public void Show(SectionDrawing drawing, SectionStyle style, string note)
        {
            _drawing = drawing; _style = style; _note = note ?? "";
            InvalidateVisual();
        }

        /// <summary>A whole laid-out sheet (comparison row) as one drawing.</summary>
        public void Show(SectionSheet sheet, SectionStyle style, string note)
        {
            if (sheet == null || sheet.Sections.Count == 0) { Show((SectionDrawing)null, style, note); return; }
            var d = new SectionDrawing
            {
                Cut = sheet.Sections[0].Cut, MinX = sheet.MinX, MinY = sheet.MinY, MaxX = sheet.MaxX, MaxY = sheet.MaxY,
                Prims = sheet.Sections.SelectMany(s => s.Prims).Concat(sheet.SheetPrims).ToList()
            };
            Show(d, style, note);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var bounds = new Rect(0, 0, ActualWidth, ActualHeight);
            dc.DrawRectangle(new SolidColorBrush(Background), null, bounds);
            if (_drawing == null || _style == null || ActualWidth < 20 || ActualHeight < 20)
            {
                DrawMessage(dc, string.IsNullOrEmpty(_note) ? "Önizleme yok" : _note);
                return;
            }

            double w = _drawing.MaxX - _drawing.MinX, h = _drawing.MaxY - _drawing.MinY;
            if (w < 1e-9 || h < 1e-9) return;
            const double pad = 12;
            double k = Math.Min((ActualWidth - 2 * pad) / w, (ActualHeight - 2 * pad) / h);
            double ox = pad + ((ActualWidth - 2 * pad) - w * k) / 2 - _drawing.MinX * k;
            double oy = pad + ((ActualHeight - 2 * pad) - h * k) / 2 + _drawing.MaxY * k;
            Point P(double x, double y) => new Point(ox + x * k, oy - y * k);

            foreach (var p in _drawing.Prims)
            {
                var es = _style.E(p.Element);
                if (!es.Visible) continue;
                var col = EntityColorOf(es, p);
                switch (p.Kind)
                {
                    case DrawPrimKind.Hatch:
                    {
                        var hp = SectionLayoutEngine.HatchOf(_style, p);
                        var hc = hp.ColorAci != 256 && !(p.IsOther && _style.OtherColorMode == OtherNetworkColorMode.PerNetwork) ? Aci(hp.ColorAci) : col;
                        double alpha = (1.0 - Math.Max(0, Math.Min(90, hp.Transparency)) / 100.0) * (hp.Pattern == "SOLID" ? 0.85 : 0.45);
                        var b = new SolidColorBrush(Color.FromArgb((byte)(255 * alpha), hc.R, hc.G, hc.B));
                        dc.DrawGeometry(b, null, Geometry(p.Loops, P, true));
                        break;
                    }
                    case DrawPrimKind.Ring:
                        dc.DrawGeometry(null, Pen(col, es), Geometry(p.Loops, P, true));
                        break;
                    case DrawPrimKind.Polyline:
                        dc.DrawGeometry(null, Pen(col, es), Geometry(new List<List<double[]>> { p.Points }, P, false));
                        break;
                    case DrawPrimKind.Text:
                        DrawText(dc, p, col, k, P);
                        break;
                }
            }
        }

        private Color EntityColorOf(SectionElementStyle es, DrawPrim p)
        {
            if (p.IsOther && _style.OtherColorMode == OtherNetworkColorMode.PerNetwork
                && p.Network != null && _style.NetworkColors.TryGetValue(p.Network, out var nc)) return Aci(nc);
            if (es.TrueColor >= 0) return Color.FromRgb((byte)((es.TrueColor >> 16) & 255), (byte)((es.TrueColor >> 8) & 255), (byte)(es.TrueColor & 255));
            return Aci(es.ColorAci != 256 ? es.ColorAci : es.LayerColorAci);
        }

        /// <summary>ACI → RGB from AutoCAD's own table (7 = white on the dark background).</summary>
        public static Color Aci(short aci)
        {
            if (aci < 1 || aci > 255) aci = 7;
            if (aci == 7) return Colors.White;
            int rgb = EntityColor.LookUpRgb((byte)aci);
            return Color.FromRgb((byte)((rgb >> 16) & 255), (byte)((rgb >> 8) & 255), (byte)(rgb & 255));
        }

        private static Pen Pen(Color c, SectionElementStyle es)
        {
            double t = es.LineWeight > 0 ? Math.Max(1.0, Math.Min(4.0, es.LineWeight / 25.0)) : 1.0;
            var pen = new Pen(new SolidColorBrush(c), t);
            string lt = (es.Linetype ?? "").ToUpperInvariant();
            if (lt.StartsWith("DASHDOT") || lt.StartsWith("CENTER") || lt.StartsWith("PHANTOM"))
                pen.DashStyle = new DashStyle(new double[] { 8, 3, 1, 3 }, 0);
            else if (lt.StartsWith("DASH") || lt.StartsWith("HIDDEN"))
                pen.DashStyle = new DashStyle(new double[] { 5, 3 }, 0);
            else if (lt.StartsWith("DOT"))
                pen.DashStyle = new DashStyle(new double[] { 1, 3 }, 0);
            pen.Freeze();
            return pen;
        }

        private static Geometry Geometry(List<List<double[]>> loops, Func<double, double, Point> P, bool closed)
        {
            var g = new StreamGeometry { FillRule = FillRule.EvenOdd };
            using (var ctx = g.Open())
                foreach (var l in loops ?? new List<List<double[]>>())
                {
                    if (l == null || l.Count < 2) continue;
                    ctx.BeginFigure(P(l[0][0], l[0][1]), closed, closed);
                    ctx.PolyLineTo(l.Skip(1).Select(v => P(v[0], v[1])).ToList(), true, false);
                }
            g.Freeze();
            return g;
        }

        private void DrawText(DrawingContext dc, DrawPrim p, Color c, double k, Func<double, double, Point> P)
        {
            double size = p.Height * k * 1.35;   // cap height → em size
            if (size < 3.5) return;              // too small to read at this zoom
            var ft = new FormattedText(p.Text ?? "", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                       new Typeface("Arial"), size, new SolidColorBrush(c), 1.0);
            var a = P(p.X, p.Y);
            string an = p.Anchor.ToString();
            double dx = an.EndsWith("Left") ? 0 : an.EndsWith("Right") ? -ft.Width : -ft.Width / 2;
            double dy = an.StartsWith("Top") ? 0 : an.StartsWith("Middle") ? -ft.Height / 2 : -ft.Baseline;
            if (Math.Abs(p.Rotation) > 1e-6)
            {
                dc.PushTransform(new RotateTransform(-p.Rotation * 180 / Math.PI, a.X, a.Y));
                dc.DrawText(ft, new Point(a.X + dx, a.Y + dy));
                dc.Pop();
            }
            else dc.DrawText(ft, new Point(a.X + dx, a.Y + dy));
        }

        private void DrawMessage(DrawingContext dc, string text)
        {
            var ft = new FormattedText(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                                       new Typeface("Segoe UI"), 13, Brushes.Gray, 1.0);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
        }
    }
}
