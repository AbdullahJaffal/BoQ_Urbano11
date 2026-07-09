using System.Windows;
using System.Windows.Media;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Ribbon butonları için koda gömülü, temaya bağımsız vektör ikonlar.
    ///
    /// Diskteki PNG dosyalarına ihtiyaç duymaz; her zaman görünürler ve her
    /// çözünürlükte keskin kalırlar. Renkli yuvarlak "tile" + beyaz glif düzeni
    /// hem açık hem koyu ribbon temasında okunur. 32×32 koordinat uzayında
    /// çizilir (Large buton için). RibbonSetup, önce icons\ klasöründeki PNG'yi
    /// dener; dosya yoksa buradaki vektör ikon devreye girer.
    /// </summary>
    internal static class RibbonIcons
    {
        // ── Aksan renkleri (koyu-doygun → beyaz glif her iki temada okunur) ──
        private static readonly Color Blue   = Color.FromRgb(0x1E, 0x6F, 0xBA);
        private static readonly Color Orange = Color.FromRgb(0xE6, 0x7E, 0x22);
        private static readonly Color Teal   = Color.FromRgb(0x16, 0xA0, 0x85);
        private static readonly Color Purple = Color.FromRgb(0x8E, 0x44, 0xAD);
        private static readonly Color Red    = Color.FromRgb(0xC0, 0x39, 0x2B);
        private static readonly Color Green  = Color.FromRgb(0x27, 0xAE, 0x60);
        private static readonly Color Slate  = Color.FromRgb(0x5D, 0x6D, 0x7E);

        // ── Metraj (BoQ) — köşesi kıvrık belge + satırlar ─────────────────────
        public static ImageSource Metraj()
        {
            var g = Canvas(Blue);
            g.Children.Add(Fill("M10,6 L20,6 L24,10 L24,26 L10,26 Z", Colors.White)); // sayfa
            g.Children.Add(Fill("M20,6 L20,10 L24,10 Z", Blue));                       // kıvrık köşe
            g.Children.Add(Stroke("M13,14 H21 M13,18 H21 M13,22 H19", Blue, 1.7));     // satırlar
            return Finish(g);
        }

        // ── Katalog Ata — baca (eş merkezli halkalar) + "ekle" rozeti ─────────
        public static ImageSource KatalogAta()
        {
            var g = Canvas(Orange);
            g.Children.Add(StrokeGeo(new EllipseGeometry(new Point(14, 18), 7.5, 7.5), Colors.White, 2.3));
            g.Children.Add(FillGeo(new EllipseGeometry(new Point(14, 18), 3, 3), Colors.White));
            g.Children.Add(FillGeo(new EllipseGeometry(new Point(24, 8), 5, 5), Colors.White)); // rozet
            g.Children.Add(Stroke("M24,5.5 V10.5 M21.5,8 H26.5", Orange, 1.8));                 // "+"
            return Finish(g);
        }

        // ── Baca Keşif Tablosu — baca kesiti (U) + derinlik oku ───────────────
        public static ImageSource BacaKesif()
        {
            var g = Canvas(Teal);
            g.Children.Add(Stroke("M9,7 V23 H19 V7", Colors.White, 2.4));       // baca kesiti
            g.Children.Add(Stroke("M24,8 V22", Colors.White, 1.7));             // derinlik şaftı
            g.Children.Add(Fill("M24,6.5 L21.8,10.5 L26.2,10.5 Z", Colors.White));  // üst ok başı
            g.Children.Add(Fill("M24,23.5 L21.8,19.5 L26.2,19.5 Z", Colors.White)); // alt ok başı
            return Finish(g);
        }

        // ── Metraj Keşif Tablosu — hesap tablosu ızgarası ─────────────────────
        public static ImageSource MetrajKesif()
        {
            var g = Canvas(Purple);
            g.Children.Add(FillGeo(new RectangleGeometry(new Rect(8, 6, 16, 20), 2, 2), Colors.White));
            g.Children.Add(Stroke("M8,12 H24 M16,12 V26 M8,19 H24", Purple, 1.5)); // başlık + ızgara
            return Finish(g);
        }

        // ── Akıllı Montaj — dişli (katalog/kural motoru) ──────────────────────
        public static ImageSource AkilliMontaj()
        {
            var g = Canvas(Red);
            var gear = new GeometryGroup { FillRule = FillRule.Nonzero };
            gear.Children.Add(new EllipseGeometry(new Point(16, 16), 7, 7));
            for (int i = 0; i < 8; i++)
            {
                var tooth = new RectangleGeometry(new Rect(14.6, 3.4, 2.8, 6.4))
                {
                    Transform = new RotateTransform(i * 45, 16, 16)
                };
                gear.Children.Add(tooth);
            }
            g.Children.Add(new GeometryDrawing(Brushes.White, null, gear));
            g.Children.Add(FillGeo(new EllipseGeometry(new Point(16, 16), 3, 3), Red)); // merkez delik
            return Finish(g);
        }

        // ── Proje Ayarları — üç kaydırıcı (ayar/konfigürasyon) ────────────────
        public static ImageSource ProjeAyarlari()
        {
            var g = Canvas(Slate);
            // three horizontal slider tracks
            g.Children.Add(Stroke("M7,10 H25 M7,16 H25 M7,22 H25", Colors.White, 1.8));
            // knobs: white bump on the track + tile-coloured centre → reads as a dial
            foreach (var c in new[] { new Point(12, 10), new Point(20, 16), new Point(15, 22) })
            {
                g.Children.Add(FillGeo(new EllipseGeometry(c, 3.0, 3.0), Colors.White));
                g.Children.Add(FillGeo(new EllipseGeometry(c, 1.3, 1.3), Slate));
            }
            return Finish(g);
        }

        // ── Ağ Paneli — düğüm/kenar ağı ───────────────────────────────────────
        public static ImageSource AgPaneli()
        {
            var g = Canvas(Green);
            g.Children.Add(Stroke("M16,16 L16,7 M16,16 L8,24 M16,16 L24,24 M8,24 L24,24",
                                  Colors.White, 1.8)); // kenarlar
            var nodes = new GeometryGroup { FillRule = FillRule.Nonzero };
            nodes.Children.Add(new EllipseGeometry(new Point(16, 16), 3.4, 3.4));
            nodes.Children.Add(new EllipseGeometry(new Point(16, 7), 2.9, 2.9));
            nodes.Children.Add(new EllipseGeometry(new Point(8, 24), 2.9, 2.9));
            nodes.Children.Add(new EllipseGeometry(new Point(24, 24), 2.9, 2.9));
            g.Children.Add(new GeometryDrawing(Brushes.White, null, nodes));
            return Finish(g);
        }

        // ── Çizim yardımcıları ────────────────────────────────────────────────

        // Şeffaf 32×32 zemin (sınırları sabitler) + renkli yuvarlak tile.
        private static DrawingGroup Canvas(Color tile)
        {
            var g = new DrawingGroup();
            g.Children.Add(new GeometryDrawing(
                Brushes.Transparent, null, new RectangleGeometry(new Rect(0, 0, 32, 32))));
            g.Children.Add(new GeometryDrawing(
                new SolidColorBrush(tile), null, new RectangleGeometry(new Rect(1, 1, 30, 30), 6.5, 6.5)));
            return g;
        }

        private static GeometryDrawing Fill(string path, Color c)
            => new GeometryDrawing(new SolidColorBrush(c), null, Geometry.Parse(path));

        private static GeometryDrawing FillGeo(Geometry geo, Color c)
            => new GeometryDrawing(new SolidColorBrush(c), null, geo);

        private static GeometryDrawing Stroke(string path, Color c, double thick)
            => new GeometryDrawing(null, MakePen(c, thick), Geometry.Parse(path));

        private static GeometryDrawing StrokeGeo(Geometry geo, Color c, double thick)
            => new GeometryDrawing(null, MakePen(c, thick), geo);

        private static Pen MakePen(Color c, double thick)
            => new Pen(new SolidColorBrush(c), thick)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap   = PenLineCap.Round,
                LineJoin     = PenLineJoin.Round
            };

        private static ImageSource Finish(DrawingGroup g)
        {
            var img = new DrawingImage(g);
            img.Freeze();
            return img;
        }
    }
}
