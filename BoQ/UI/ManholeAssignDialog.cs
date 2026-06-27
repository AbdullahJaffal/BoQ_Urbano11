using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.Windows.Forms;
using UrbanoMetraj.BoQ.Models;

using Font = System.Drawing.Font;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Small dialog shown after the user selects manholes in the DWG.
    /// Displays catalog groups and their sub-template details so the user
    /// can choose which excavation catalog group to assign.
    /// </summary>
    public sealed class ManholeAssignDialog : Form
    {
        private Label    _lblInfo;
        private ListView _lstGroups;
        private GroupBox _gbTemplates;
        private ListView _lstTemplates;
        private Button   _btnOk, _btnCancel;

        public ManholeTemplateGroup SelectedGroup { get; private set; }

        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public ManholeAssignDialog(int manholeCount, IList<ManholeTemplateGroup> groups)
        {
            BuildForm(manholeCount, groups);
        }

        private void BuildForm(int manholeCount, IList<ManholeTemplateGroup> groups)
        {
            Text            = "Kazı Kataloğu Ata";
            ClientSize      = new Size(540, 440);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox     = false;
            MinimizeBox     = false;
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.WhiteSmoke;

            const int M = 10;

            // ── Info label ─────────────────────────────────────────────────────
            bool noManholes = manholeCount == 0;
            _lblInfo = new Label
            {
                Bounds    = new Rectangle(M, M, 520, 38),
                Text      = noManholes
                    ? "⚠  Seçimde hiç baca (AG_GUID veya AG_LAB_ENTITY) bulunamadı.\n" +
                      "   Lütfen Urbano baca entity'lerini seçin."
                    : $"{manholeCount} adet baca seçildi.\n" +
                      "  Uygulanacak kazı kataloğu grubunu seçin, ardından \"Ata\" butonuna basın.",
                ForeColor = noManholes
                    ? Color.Firebrick
                    : Color.FromArgb(0, 70, 127),
                Font = new Font("Segoe UI", 9.5f, noManholes ? FontStyle.Regular : FontStyle.Bold)
            };
            Controls.Add(_lblInfo);

            // ── Separator ──────────────────────────────────────────────────────
            Controls.Add(new Label
            {
                Bounds    = new Rectangle(0, 54, 540, 1),
                BackColor = Color.FromArgb(0, 70, 127)
            });

            // ── Groups list ────────────────────────────────────────────────────
            Controls.Add(new Label
            {
                Text   = "Katalog Grupları",
                Font   = new Font("Segoe UI", 8.5f, FontStyle.Bold),
                Bounds = new Rectangle(M, 60, 200, 16)
            });

            _lstGroups = new ListView
            {
                Bounds        = new Rectangle(M, 78, 520, 148),
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = false,
                GridLines     = true,
                HideSelection = false,
                BorderStyle   = BorderStyle.FixedSingle
            };
            _lstGroups.Columns.Add("Grup Adı",        210);
            _lstGroups.Columns.Add("Mod",              90);
            _lstGroups.Columns.Add("Şablon Sayısı",   110);
            _lstGroups.Columns.Add("ID",               90);

            foreach (var g in groups ?? new List<ManholeTemplateGroup>())
            {
                var item = new ListViewItem(g.Name ?? "(isimsiz)") { Tag = g };
                item.SubItems.Add(g.Mode == "Dynamic" ? "Dinamik" : "Sabit");
                item.SubItems.Add((g.Templates?.Count ?? 0).ToString());
                item.SubItems.Add(g.Id ?? "");
                _lstGroups.Items.Add(item);
            }

            if (_lstGroups.Items.Count > 0)
                _lstGroups.Items[0].Selected = true;

            _lstGroups.SelectedIndexChanged += LstGroups_SelectedIndexChanged;
            _lstGroups.DoubleClick          += (s, e) => TryAccept();
            Controls.Add(_lstGroups);

            // ── Template detail ────────────────────────────────────────────────
            _gbTemplates = new GroupBox
            {
                Text   = "Seçili Grubun Şablonları",
                Bounds = new Rectangle(M, 234, 520, 150),
                Font   = new Font("Segoe UI", 8.5f, FontStyle.Bold)
            };

            _lstTemplates = new ListView
            {
                Bounds        = new Rectangle(6, 18, 508, 120),
                View          = View.Details,
                FullRowSelect = true,
                MultiSelect   = false,
                GridLines     = true,
                HideSelection = false,
                BorderStyle   = BorderStyle.FixedSingle,
                Font          = new Font("Segoe UI", 8.5f)
            };
            _lstTemplates.Columns.Add("Şablon Adı",       160);
            _lstTemplates.Columns.Add("DN (mm)",            70);
            _lstTemplates.Columns.Add("Duvar (mm)",         80);
            _lstTemplates.Columns.Add("Açıklık (m)",        80);
            _lstTemplates.Columns.Add("Min–Maks Boru",     120);

            _gbTemplates.Controls.Add(_lstTemplates);
            Controls.Add(_gbTemplates);

            // ── OK / Cancel ────────────────────────────────────────────────────
            _btnOk = new Button
            {
                Text      = "Ata",
                Bounds    = new Rectangle(340, 400, 90, 30),
                BackColor = Color.FromArgb(0, 70, 127),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                Enabled   = !noManholes && _lstGroups.Items.Count > 0
            };
            _btnOk.Click += (s, e) => TryAccept();
            Controls.Add(_btnOk);

            _btnCancel = new Button
            {
                Text      = "İptal",
                Bounds    = new Rectangle(440, 400, 90, 30),
                FlatStyle = FlatStyle.System
            };
            _btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; Close(); };
            Controls.Add(_btnCancel);

            AcceptButton = _btnOk;
            CancelButton = _btnCancel;

            // Populate template list for the initial selection
            if (_lstGroups.Items.Count > 0)
                RefreshTemplateList(groups?[0]);
        }

        private void LstGroups_SelectedIndexChanged(object sender, EventArgs e)
        {
            var grp = _lstGroups.SelectedItems.Count > 0
                ? _lstGroups.SelectedItems[0].Tag as ManholeTemplateGroup
                : null;
            RefreshTemplateList(grp);
        }

        private void RefreshTemplateList(ManholeTemplateGroup grp)
        {
            _lstTemplates.Items.Clear();
            if (grp?.Templates == null) return;

            bool isDyn = grp.Mode == "Dynamic";
            foreach (var t in grp.Templates)
            {
                string pipeCond = isDyn && (t.MinPipeDiamMm > 0 || t.MaxPipeDiamMm > 0)
                    ? $"{t.MinPipeDiamMm}–{t.MaxPipeDiamMm} mm"
                    : "—";

                var item = new ListViewItem(t.Name ?? "(isimsiz)");
                item.SubItems.Add(t.ManholeDn.ToString());
                item.SubItems.Add(t.WallThicknessMm.ToString());
                item.SubItems.Add(t.ClearanceM.ToString("G", IC));
                item.SubItems.Add(pipeCond);
                _lstTemplates.Items.Add(item);
            }
        }

        private void TryAccept()
        {
            if (_lstGroups.SelectedItems.Count == 0)
            {
                MessageBox.Show("Lütfen bir katalog grubu seçin.", "Uyarı",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            SelectedGroup = _lstGroups.SelectedItems[0].Tag as ManholeTemplateGroup;
            DialogResult  = DialogResult.OK;
            Close();
        }
    }
}
