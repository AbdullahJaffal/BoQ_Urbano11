using System;
using System.Drawing;
using System.Windows.Forms;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Modal dialog shown by URBANO_SOLIDS before any geometry is drawn. Lets the
    /// engineer choose, independently, how overlapping EXCAVATION and BACKFILL
    /// solids are assigned (lower / upper / split) and which solid layers to draw.
    /// </summary>
    public sealed class SolidsStartupDialog : Form
    {
        private ComboBox _cmbExcav;
        private ComboBox _cmbBackfill;
        private CheckBox _chkExcav;
        private CheckBox _chkBackfill;
        private CheckBox _chkBedding;
        private CheckBox _chkSurround;

        private const int CW = 460, MARGIN = 12;
        private const int GW = CW - MARGIN * 2;

        public SolidsSettings Settings { get; private set; }

        public SolidsStartupDialog()
        {
            Settings = new SolidsSettings();
            BuildForm();
        }

        private void BuildForm()
        {
            Text            = "Urbano 3B Katı Üretimi — Ayarlar";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.WhiteSmoke;
            ClientSize      = new Size(CW + MARGIN * 2, 100);

            int y = MARGIN;

            var banner = new Label
            {
                Text      = "Hafriyat / Geri Dolgu / Yataklama / Gömlekleme — 3B Katı",
                Font      = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 70, 127),
                Left      = MARGIN, Top = y, Width = GW, Height = 24
            };
            Controls.Add(banner);
            y += 32;

            // ── Overlap options ───────────────────────────────────────────────
            var grpOv = new GroupBox
            {
                Text = "Çakışma (overlap) durumunda atama",
                Left = MARGIN, Top = y, Width = GW, Height = 90,
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(grpOv);
            y += grpOv.Height + 8;

            string[] modes =
            {
                "Eşit böl (50/50)",
                "Tamamı ALT (derin) boruya",
                "Tamamı ÜST (sığ) boruya"
            };

            var lblE = new Label { Text = "Hafriyat (Excavation):", Left = 12, Top = 26, Width = 160, TextAlign = ContentAlignment.MiddleLeft };
            _cmbExcav = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 176, Top = 24, Width = 230 };
            _cmbExcav.Items.AddRange(modes);
            _cmbExcav.SelectedIndex = 0;

            var lblB = new Label { Text = "Geri Dolgu (Backfill):", Left = 12, Top = 56, Width = 160, TextAlign = ContentAlignment.MiddleLeft };
            _cmbBackfill = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList, Left = 176, Top = 54, Width = 230 };
            _cmbBackfill.Items.AddRange(modes);
            _cmbBackfill.SelectedIndex = 0;

            grpOv.Controls.AddRange(new Control[] { lblE, _cmbExcav, lblB, _cmbBackfill });

            // ── Which solids to draw ──────────────────────────────────────────
            var grpL = new GroupBox
            {
                Text = "Üretilecek katılar (layer)",
                Left = MARGIN, Top = y, Width = GW, Height = 76,
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(grpL);
            y += grpL.Height + 8;

            _chkExcav    = new CheckBox { Text = "Hafriyat (URB_Hafriyat)",     Checked = true, Left = 12,  Top = 22, Width = 200 };
            _chkBackfill = new CheckBox { Text = "Geri Dolgu (URB_GeriDolgu)",  Checked = true, Left = 220, Top = 22, Width = 210 };
            _chkBedding  = new CheckBox { Text = "Yataklama (URB_Yataklama)",   Checked = true, Left = 12,  Top = 46, Width = 200 };
            _chkSurround = new CheckBox { Text = "Gömlekleme (URB_Gomlekleme)", Checked = true, Left = 220, Top = 46, Width = 210 };
            grpL.Controls.AddRange(new Control[] { _chkExcav, _chkBackfill, _chkBedding, _chkSurround });

            // ── Buttons ───────────────────────────────────────────────────────
            var btnRun = new Button
            {
                Text = "Oluştur", Left = MARGIN + GW - 170, Top = y, Width = 80, Height = 28,
                BackColor = Color.FromArgb(0, 70, 127), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnRun.Click += OnRun;

            var btnCancel = new Button
            {
                Text = "İptal", DialogResult = DialogResult.Cancel,
                Left = MARGIN + GW - 82, Top = y, Width = 80, Height = 28
            };

            Controls.AddRange(new Control[] { btnRun, btnCancel });
            AcceptButton = btnRun;
            CancelButton = btnCancel;

            ClientSize = new Size(CW + MARGIN * 2, y + 28 + MARGIN);
        }

        private void OnRun(object sender, EventArgs e)
        {
            Settings.ExcavationOverlap = (OverlapAssignment)_cmbExcav.SelectedIndex;
            Settings.BackfillOverlap   = (OverlapAssignment)_cmbBackfill.SelectedIndex;
            Settings.DrawExcavation    = _chkExcav.Checked;
            Settings.DrawBackfill      = _chkBackfill.Checked;
            Settings.DrawBedding       = _chkBedding.Checked;
            Settings.DrawSurround      = _chkSurround.Checked;

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
