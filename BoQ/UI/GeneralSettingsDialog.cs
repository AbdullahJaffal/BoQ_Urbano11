using System;
using System.Drawing;
using System.Windows.Forms;

namespace UrbanoMetraj.BoQ.UI
{
    internal sealed class GeneralSettingsDialog : Form
    {
        private NumericUpDown _nudSolid;
        private NumericUpDown _nudSection;

        public double SolidDisplayInterval   => (double)_nudSolid.Value;
        public double CrossSectionInterval   => (double)_nudSection.Value;

        public GeneralSettingsDialog(double solidInterval, double sectionInterval)
        {
            Text            = "Genel Ayarlar";
            ClientSize      = new Size(360, 280);
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
                Left = 10, Top = 10, Width = 336, Height = 100
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
                Left = 10, Top = 118, Width = 336, Height = 100
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

            // ── Tamam / İptal ─────────────────────────────────────────────────
            var btnOk = new Button
            {
                Text = "Kaydet", DialogResult = DialogResult.OK,
                Left = 188, Top = 236, Width = 76, Height = 28,
                BackColor = Color.FromArgb(0, 70, 127), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            var btnCancel = new Button
            {
                Text = "İptal", DialogResult = DialogResult.Cancel,
                Left = 272, Top = 236, Width = 76, Height = 28,
                FlatStyle = FlatStyle.Flat
            };

            Controls.AddRange(new Control[] { btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
        }
    }
}
