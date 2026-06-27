using System;
using System.Drawing;
using System.Windows.Forms;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Modal WinForms dialog that lets the user configure all BoQ export options
    /// before the pipeline starts.
    ///
    /// Show via:
    ///   Application.ShowModalDialog(new BoQStartupDialog(catalogPath))
    ///
    /// After DialogResult.OK, read the <see cref="Settings"/> property.
    /// </summary>
    public sealed class BoQStartupDialog : Form
    {
        // ── Controls ─────────────────────────────────────────────────────────
        private TextBox     _txtCatalogPath;
        private Label       _lblStatus;
        private Button      _btnRun;

        // ── Layout constants ─────────────────────────────────────────────────
        private const int CW     = 494;   // usable client width
        private const int MARGIN = 12;
        private const int GW     = CW - MARGIN * 2;   // group box inner width

        // ── Result ───────────────────────────────────────────────────────────

        /// <summary>Populated when the user clicks Run (DialogResult.OK).</summary>
        public BoQSettings Settings { get; private set; }

        // ── Constructor ───────────────────────────────────────────────────────

        public BoQStartupDialog(string defaultCatalogPath)
        {
            Settings = new BoQSettings { ManholeConfigPath = defaultCatalogPath };
            BuildForm();
            _txtCatalogPath.Text = defaultCatalogPath;
        }

        // =====================================================================
        // UI construction
        // =====================================================================

        private void BuildForm()
        {
            // Form properties
            Text            = "Urbano BoQ Export — Configuration";
            ClientSize      = new Size(CW + MARGIN * 2, 100); // height set at end
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f, FontStyle.Regular);
            BackColor       = Color.WhiteSmoke;

            int y = MARGIN;

            // ── Header banner ─────────────────────────────────────────────
            var banner = new Label
            {
                Text      = "Urbano Network — Bill of Quantities",
                Font      = new Font("Segoe UI", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0, 70, 127),
                Left      = MARGIN, Top = y,
                Width     = GW, Height = 24,
                TextAlign = ContentAlignment.MiddleLeft
            };
            Controls.Add(banner);
            y += 30;

            var sep = new Label
            {
                Left = MARGIN, Top = y, Width = GW, Height = 1,
                BackColor = Color.FromArgb(0, 70, 127)
            };
            Controls.Add(sep);
            y += 8;

            // NOTE: Both the trench-overlap method (Kazı/Dolgu) and the manhole type
            // (Prefabrik/Yerinde Döküm) are now chosen in the Metraj window
            // (URBANO_BOQ_VIEW). The engine caches all scenarios at compute time.

            // ── Section: Manhole catalog ──────────────────────────────────
            var grpCat = AddGroup("Pre-cast Manhole Catalog (.xlsx)", ref y, 58);
            _txtCatalogPath = new TextBox { Left = 10, Top = 22, Width = GW - 110 };
            var btnBrowseCat  = MakeButton("Browse...", GW - 92, 20, 82, BrowseCatalogFile);
            var btnGenCat     = MakeButton("Generate Template", GW - 92 - 130, 20, 124, GenerateCatalogTemplate);
            grpCat.Controls.AddRange(new Control[] { _txtCatalogPath, btnBrowseCat, btnGenCat });

            // ── Status label ──────────────────────────────────────────────
            _lblStatus = new Label
            {
                Left = MARGIN, Top = y + 2, Width = GW, Height = 16,
                ForeColor = Color.Firebrick, Text = ""
            };
            Controls.Add(_lblStatus);
            y += 22;

            // ── Action buttons ────────────────────────────────────────────
            _btnRun = new Button
            {
                Text      = "Run & Save to DWG",
                Left      = MARGIN + GW - 170,
                Top       = y,
                Width     = 80,
                Height    = 28,
                BackColor = Color.FromArgb(0, 70, 127),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            _btnRun.FlatAppearance.BorderColor = Color.FromArgb(0, 50, 100);
            _btnRun.Click += OnRunClicked;

            var btnCancel = new Button
            {
                Text         = "Cancel",
                DialogResult = DialogResult.Cancel,
                Left         = MARGIN + GW - 82,
                Top          = y,
                Width        = 80,
                Height       = 28
            };

            Controls.AddRange(new Control[] { _btnRun, btnCancel });
            AcceptButton = _btnRun;
            CancelButton = btnCancel;

            ClientSize = new Size(CW + MARGIN * 2, y + 28 + MARGIN + 12);
        }

        // ── Layout helpers ────────────────────────────────────────────────────

        private GroupBox AddGroup(string title, ref int y, int height)
        {
            var g = new GroupBox
            {
                Text   = title,
                Left   = MARGIN,
                Top    = y,
                Width  = GW,
                Height = height,
                Font   = new Font("Segoe UI", 8.5f, FontStyle.Regular)
            };
            Controls.Add(g);
            y += height + 6;
            return g;
        }

        private static Button MakeButton(string text, int left, int top, int width, EventHandler handler)
        {
            var b = new Button { Text = text, Left = left, Top = top, Width = width, Height = 24 };
            b.Click += handler;
            return b;
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void BrowseCatalogFile(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog
            {
                Title  = "Select Manhole Catalog Excel File",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx"
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _txtCatalogPath.Text = dlg.FileName;
            }
        }

        private void GenerateCatalogTemplate(object sender, EventArgs e)
        {
            string path = Services.ManholeConfigService.GenerateTemplateInteractive();
            if (!string.IsNullOrEmpty(path))
            {
                _txtCatalogPath.Text = path;
                _lblStatus.ForeColor = Color.DarkGreen;
                _lblStatus.Text = "Catalog template generated: " + path;
            }
        }

        private void OnRunClicked(object sender, EventArgs e)
        {
            _lblStatus.ForeColor = Color.Firebrick;
            _lblStatus.Text = "";

            Settings.ExportFilePath    = "";
            Settings.ManholeConfigPath = _txtCatalogPath.Text.Trim();

            DialogResult = DialogResult.OK;
            Close();
        }
    }
}
