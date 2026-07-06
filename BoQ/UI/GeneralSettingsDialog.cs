using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Windows.Forms;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.UI
{
    internal sealed class GeneralSettingsDialog : Form
    {
        private NumericUpDown _nudSolid;
        private NumericUpDown _nudSection;
        private ComboBox      _cmbKazi;
        private ComboBox      _cmbDolgu;
        private ComboBox      _cmbBaca;
        private ComboBox      _cmbBacaKazi;
        private ComboBox      _cmbKirmiziKot;
        private ComboBox      _cmbAraziKotu;
        private ComboBox      _cmbTerrasmanKotu;
        private ComboBox      _cmbKirmiziKotC3D;
        private ComboBox      _cmbAraziKotuC3D;
        private ComboBox      _cmbTerrasmanKotuC3D;
        private ComboBox      _cmbKaziSeviyesi;
        private ComboBox      _cmbDolguSeviyesi;
        private ComboBox      _cmbBacaKapakSeviyesi;
        private ComboBox      _cmbRingFillMode;
        private ComboBox      _cmbNetLengthMode;

        private static readonly object[] AraziOptions =
        {
            "Arazi1", "Arazi2", "Arazi3", "Arazi4", "Arazi5",
            "Arazi6", "Arazi7", "Arazi8", "Arazi9", "Arazi10"
        };

        private static readonly object[] SeviyeOptions =
        {
            "Kırmızı Kot", "Arazi Kotu", "Terrasman Kotu"
        };

        public double             SolidDisplayInterval        => (double)_nudSolid.Value;
        public double             CrossSectionInterval        => (double)_nudSection.Value;
        public OverlapAssignment  ExcavationOverlap           => IndexToOverlap(_cmbKazi.SelectedIndex);
        public OverlapAssignment  BackfillOverlap             => IndexToOverlap(_cmbDolgu.SelectedIndex);
        public ManholeType        SelectedManholeType         => _cmbBaca.SelectedIndex == 1 ? ManholeType.CastInPlace : ManholeType.PreCast;
        public bool                BacaKaziHesapla            => _cmbBacaKazi.SelectedIndex == 0;
        public string             BacaKirmiziKotSurface       => (string)_cmbKirmiziKot.SelectedItem;
        public string             BacaAraziKotuSurface        => (string)_cmbAraziKotu.SelectedItem;
        public string             BacaTerrasmanKotuSurface    => (string)_cmbTerrasmanKotu.SelectedItem;
        public string             BacaKirmiziKotC3DSurface    => _cmbKirmiziKotC3D.SelectedItem as string ?? "";
        public string             BacaAraziKotuC3DSurface     => _cmbAraziKotuC3D.SelectedItem as string ?? "";
        public string             BacaTerrasmanKotuC3DSurface => _cmbTerrasmanKotuC3D.SelectedItem as string ?? "";
        public string             KaziSeviyesi                => (string)_cmbKaziSeviyesi.SelectedItem;
        public string             DolguSeviyesi               => (string)_cmbDolguSeviyesi.SelectedItem;
        public string             BacaKapakSeviyesi           => (string)_cmbBacaKapakSeviyesi.SelectedItem;
        public RingFillMode       RingFillMode                => _cmbRingFillMode.SelectedIndex == 1 ? RingFillMode.BestFit : RingFillMode.Greedy;
        public NetLengthMode      NetLengthMode               => _cmbNetLengthMode.SelectedIndex == 1 ? NetLengthMode.InnerDiameter : NetLengthMode.OuterDiameter;

        public GeneralSettingsDialog(double solidInterval, double sectionInterval,
            OverlapAssignment excavationOverlap, OverlapAssignment backfillOverlap,
            ManholeType manholeType, bool bacaKaziHesapla,
            string kirmiziKotSurface, string araziKotuSurface, string terrasmanKotuSurface,
            List<string> c3dSurfaceNames,
            string kirmiziKotC3DSurface, string araziKotuC3DSurface, string terrasmanKotuC3DSurface,
            string kaziSeviyesi, string dolguSeviyesi, string bacaKapakSeviyesi,
            RingFillMode ringFillMode, NetLengthMode netLengthMode)
        {
            Text            = "Genel Ayarlar";
            ClientSize      = new Size(480, 744);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterParent;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.WhiteSmoke;

            // ── 3B Katı grubu ─────────────────────────────────────────────────
            var grp1 = new GroupBox
            {
                Text = "3B Katı Ayarları",
                Left = 10, Top = 10, Width = 460, Height = 100
            };
            grp1.Controls.AddRange(new Control[]
            {
                new Label
                {
                    Text = "Kesit Aralığı (Görüntü Hassasiyeti):",
                    Left = 8, Top = 26, Width = 215, Height = 20,
                    TextAlign = ContentAlignment.MiddleLeft
                },
                _nudSolid = new NumericUpDown
                {
                    Left = 228, Top = 24, Width = 70,
                    Minimum = 0.5m, Maximum = 50.0m, Increment = 0.5m, DecimalPlaces = 1,
                    Value = (decimal)Math.Max(0.5, Math.Min(50.0, solidInterval))
                },
                new Label { Text = "m", Left = 303, Top = 28, Width = 20,
                    TextAlign = ContentAlignment.MiddleLeft },
                new Label
                {
                    Text = "En küçük değer: 0.5 m  |  Değer küçüldükçe hassasiyet ve işlem yükü artar.",
                    Left = 8, Top = 54, Width = 320, Height = 36,
                    ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5f),
                    TextAlign = ContentAlignment.TopLeft
                }
            });
            Controls.Add(grp1);

            // ── Kesit Çizim grubu ─────────────────────────────────────────────
            var grp2 = new GroupBox
            {
                Text = "Kesit Çizim Ayarları (URBANO_SECTIONS)",
                Left = 10, Top = 118, Width = 460, Height = 100
            };
            grp2.Controls.AddRange(new Control[]
            {
                new Label
                {
                    Text = "Kesitler Arası Mesafe:",
                    Left = 8, Top = 26, Width = 215, Height = 20,
                    TextAlign = ContentAlignment.MiddleLeft
                },
                _nudSection = new NumericUpDown
                {
                    Left = 228, Top = 24, Width = 70,
                    Minimum = 0.5m, Maximum = 100.0m, Increment = 0.5m, DecimalPlaces = 1,
                    Value = (decimal)Math.Max(0.5, Math.Min(100.0, sectionInterval))
                },
                new Label { Text = "m", Left = 303, Top = 28, Width = 20,
                    TextAlign = ContentAlignment.MiddleLeft },
                new Label
                {
                    Text = "Her kaç metrede bir kesit çizileceğini belirler. Çakışma sınırları her zaman eklenir.",
                    Left = 8, Top = 54, Width = 320, Height = 36,
                    ForeColor = Color.Gray, Font = new Font("Segoe UI", 7.5f),
                    TextAlign = ContentAlignment.TopLeft
                }
            });
            Controls.Add(grp2);

            // ── Hesaplama Seçenekleri grubu ───────────────────────────────────
            var grp3 = new GroupBox
            {
                Text = "Hesaplama Seçenekleri",
                Left = 10, Top = 226, Width = 460, Height = 178
            };
            var lblKazi = new Label
            {
                Text = "Kazı:", Left = 8, Top = 30, Width = 38,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbKazi = MakeMethodCombo(46, 26, excavationOverlap);

            var lblDolgu = new Label
            {
                Text = "Dolgu:", Left = 156, Top = 30, Width = 46,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbDolgu = MakeMethodCombo(202, 26, backfillOverlap);

            var lblBaca = new Label
            {
                Text = "Baca:", Left = 8, Top = 62, Width = 40,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbBaca = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 48, Top = 58, Width = 115
            };
            _cmbBaca.Items.AddRange(new object[] { "Prefabrik", "Yerinde Döküm" });
            _cmbBaca.SelectedIndex = manholeType == ManholeType.PreCast ? 0 : 1;

            var lblBacaKazi = new Label
            {
                Text = "Baca Kazısı:", Left = 166, Top = 62, Width = 88,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbBacaKazi = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 256, Top = 58, Width = 74
            };
            _cmbBacaKazi.Items.AddRange(new object[] { "Hesapla", "Yoksay" });
            _cmbBacaKazi.SelectedIndex = bacaKaziHesapla ? 0 : 1;

            var lblRingFillMode = new Label
            {
                Text = "Parça Sayısı Yöntemi:", Left = 8, Top = 94, Width = 130,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbRingFillMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 142, Top = 90, Width = 190
            };
            _cmbRingFillMode.Items.AddRange(new object[] { "Büyükten Küçüğe (Mevcut)", "En İyi Kombinasyon" });
            _cmbRingFillMode.SelectedIndex = ringFillMode == RingFillMode.BestFit ? 1 : 0;

            var lblNetLengthMode = new Label
            {
                Text = "Net Uzunluk Yöntemi:", Left = 8, Top = 126, Width = 130,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbNetLengthMode = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 142, Top = 122, Width = 190
            };
            _cmbNetLengthMode.Items.AddRange(new object[] { "Dış Çap", "İç Çap" });
            _cmbNetLengthMode.SelectedIndex = netLengthMode == NetLengthMode.InnerDiameter ? 1 : 0;

            grp3.Controls.AddRange(new Control[]
            {
                lblKazi, _cmbKazi, lblDolgu, _cmbDolgu, lblBaca, _cmbBaca, lblBacaKazi, _cmbBacaKazi,
                lblRingFillMode, _cmbRingFillMode, lblNetLengthMode, _cmbNetLengthMode
            });
            Controls.Add(grp3);

            // ── Kot Ayarları grubu ────────────────────────────────────────────
            // Two independent pickers per row: a fixed Arazi1…Arazi10 slot label,
            // and the real Civil 3D surface (from the active drawing) it maps to.
            var grp4 = new GroupBox
            {
                Text = "Kot Ayarları",
                Left = 10, Top = 414, Width = 460, Height = 148
            };

            var lblColBaca = new Label
            {
                Text = "Baca", Left = 130, Top = 22, Width = 90,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
            var lblColGenel = new Label
            {
                Text = "Genel", Left = 230, Top = 22, Width = 200,
                Font = new Font("Segoe UI", 8f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };

            var lblKirmiziKot = new Label
            {
                Text = "Kırmızı Kot:", Left = 8, Top = 50, Width = 110,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbKirmiziKot    = MakeAraziCombo(130, 46, kirmiziKotSurface);
            _cmbKirmiziKotC3D = MakeSurfaceCombo(230, 46, c3dSurfaceNames, kirmiziKotC3DSurface);

            var lblAraziKotu = new Label
            {
                Text = "Arazi Kotu:", Left = 8, Top = 82, Width = 110,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbAraziKotu    = MakeAraziCombo(130, 78, araziKotuSurface);
            _cmbAraziKotuC3D = MakeSurfaceCombo(230, 78, c3dSurfaceNames, araziKotuC3DSurface);

            var lblTerrasmanKotu = new Label
            {
                Text = "Terrasman Kotu:", Left = 8, Top = 114, Width = 110,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbTerrasmanKotu    = MakeAraziCombo(130, 110, terrasmanKotuSurface);
            _cmbTerrasmanKotuC3D = MakeSurfaceCombo(230, 110, c3dSurfaceNames, terrasmanKotuC3DSurface);

            grp4.Controls.AddRange(new Control[]
            {
                lblColBaca, lblColGenel,
                lblKirmiziKot, _cmbKirmiziKot, _cmbKirmiziKotC3D,
                lblAraziKotu, _cmbAraziKotu, _cmbAraziKotuC3D,
                lblTerrasmanKotu, _cmbTerrasmanKotu, _cmbTerrasmanKotuC3D
            });
            Controls.Add(grp4);

            // ── Seviye Ayarları grubu ─────────────────────────────────────────
            var grp5 = new GroupBox
            {
                Text = "Seviye Ayarları",
                Left = 10, Top = 572, Width = 460, Height = 120
            };

            var lblKaziSeviyesi = new Label
            {
                Text = "Kazı Seviyesi:", Left = 8, Top = 30, Width = 130,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbKaziSeviyesi = MakeSeviyeCombo(142, 26, kaziSeviyesi);

            var lblDolguSeviyesi = new Label
            {
                Text = "Dolgu Seviyesi:", Left = 8, Top = 62, Width = 130,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbDolguSeviyesi = MakeSeviyeCombo(142, 58, dolguSeviyesi);

            var lblBacaKapakSeviyesi = new Label
            {
                Text = "Baca Kapak Seviyesi:", Left = 8, Top = 94, Width = 130,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbBacaKapakSeviyesi = MakeSeviyeCombo(142, 90, bacaKapakSeviyesi);

            grp5.Controls.AddRange(new Control[]
            {
                lblKaziSeviyesi, _cmbKaziSeviyesi,
                lblDolguSeviyesi, _cmbDolguSeviyesi,
                lblBacaKapakSeviyesi, _cmbBacaKapakSeviyesi
            });
            Controls.Add(grp5);

            // ── Tamam / İptal ─────────────────────────────────────────────────
            var btnOk = new Button
            {
                Text = "Kaydet", DialogResult = DialogResult.OK,
                Left = 308, Top = 700, Width = 76, Height = 28,
                BackColor = Color.FromArgb(0, 70, 127), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            var btnCancel = new Button
            {
                Text = "İptal", DialogResult = DialogResult.Cancel,
                Left = 392, Top = 700, Width = 76, Height = 28,
                FlatStyle = FlatStyle.Flat
            };

            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }

        private static ComboBox MakeAraziCombo(int left, int top, string current)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = left, Top = top, Width = 90
            };
            c.Items.AddRange(AraziOptions);
            c.SelectedIndex = Math.Max(0, Array.IndexOf(AraziOptions, current));
            return c;
        }

        private static ComboBox MakeSeviyeCombo(int left, int top, string current)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = left, Top = top, Width = 140
            };
            c.Items.AddRange(SeviyeOptions);
            c.SelectedIndex = Math.Max(0, Array.IndexOf(SeviyeOptions, current));
            return c;
        }

        private static ComboBox MakeSurfaceCombo(int left, int top, List<string> names, string current)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = left, Top = top, Width = 200
            };
            var items = new List<string>(names ?? Enumerable.Empty<string>());
            if (!string.IsNullOrEmpty(current) && !items.Contains(current))
                items.Insert(0, current);
            c.Items.AddRange(items.ToArray());
            if (items.Count > 0)
            {
                int idx = items.IndexOf(current);
                c.SelectedIndex = idx >= 0 ? idx : 0;
            }
            return c;
        }

        private static ComboBox MakeMethodCombo(int left, int top, OverlapAssignment current)
        {
            var c = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = left, Top = top, Width = 100
            };
            // Index order must match IndexToOverlap below.
            c.Items.AddRange(new object[] { "50 / 50", "Üst hat", "Alt hat", "Yoksay" });
            c.SelectedIndex = OverlapToIndex(current);
            return c;
        }

        private static int OverlapToIndex(OverlapAssignment a)
            => a == OverlapAssignment.UpperPipe ? 1
             : a == OverlapAssignment.LowerPipe ? 2
             : a == OverlapAssignment.Ignore    ? 3
             : 0;

        private static OverlapAssignment IndexToOverlap(int i)
            => i == 1 ? OverlapAssignment.UpperPipe
             : i == 2 ? OverlapAssignment.LowerPipe
             : i == 3 ? OverlapAssignment.Ignore
             : OverlapAssignment.Split;
    }
}
