using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Bricscad.ApplicationServices;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// The Metraj (BoQ) window. Reads the per-station scenario data cached in the
    /// DWG and lets the user:
    ///   • choose the Kazı (excavation) and Dolgu (bedding+surround+backfill)
    ///     calculation method (50/50 · Üst · Alt) from two drop-downs,
    ///   • press "Sonuçları Göster" to re-aggregate the tables from the cache
    ///     (no Clipper, no re-save — just a display refresh for the chosen
    ///     Kazı/Dolgu/Baca Kazısı preference),
    ///   • "Metraj Verisi Güncelle" (UT_BOQ) to pull fresh data from Urbano
    ///     and refresh the discovered-catalog-item list for Tür Eşleştirme —
    ///     no geometry/Manhole AI runs here, so this is safe to do before
    ///     linking is complete,
    ///   • "Hesapla" (UT_BOQ_HESAPLA) to run the actual geometry + Manhole
    ///     AI calculation and save it — meant to be pressed after Tür
    ///     Eşleştirme / Baca-Boru Bağlantı Kuralları links are set up,
    ///   • build the 3-D solids for the selected method,
    ///   • export to Excel.
    /// </summary>
    public sealed class BoQResultsDialog : Form
    {
        private readonly BoQReport   _report;
        private readonly BoQSettings _savedSettings;
        private readonly Document    _doc;

        private TabControl _tabs;
        private ComboBox   _cmbLanguage;
        private Button     _btnHesapla;    // disabled until a Kapsam (scope) is active
        private Button     _btnScopeMain;  // split button main face — runs the last-used scope mode
        private Button     _btnScopeArrow; // split button arrow — opens the scope-mode menu
        private ToolTip    _scopeTip;
        private ToolStripMenuItem _scopeSonItem;   // "Son Seçilen" — disabled until a CAD pick exists
        private ScopeMode  _scopeMode;
        private TextBox    _txtExportPath;
        private Label      _lblInfo;

        private OverlapAssignment _excavationOverlap;
        private OverlapAssignment _backfillOverlap;
        private ManholeType       _manholeTypeSetting;
        private bool              _bacaKaziHesapla;
        private bool              _bacaBacaKaziHesapla;
        private bool              _bacaAltiParcaEklensin;
        private bool              _bacaKaziDisCapKullan;
        private string            _bacaKirmiziKotSurface;
        private string            _bacaAraziKotuSurface;
        private string            _bacaTerrasmanKotuSurface;
        private string            _bacaKirmiziKotC3DSurface;
        private string            _bacaAraziKotuC3DSurface;
        private string            _bacaTerrasmanKotuC3DSurface;
        private string            _kaziSeviyesi;
        private string            _dolguSeviyesi;
        private string            _bacaKapakSeviyesi;
        private RingFillMode      _ringFillMode;
        private NetLengthMode     _netLengthMode;

        private OverlapAssignment SelectedExcavationOverlap => _excavationOverlap;
        private OverlapAssignment SelectedBackfillOverlap   => _backfillOverlap;
        private bool              BacaKaziHesapla           => _bacaKaziHesapla;
        private bool              BacaBacaKaziHesapla       => _bacaBacaKaziHesapla;

        private const int CW     = 820;
        private const int MARGIN = 10;
        private const int GW     = CW - MARGIN * 2;
        private const int TABS_TOP = 74;

        public BoQResultsDialog(BoQReport report, BoQSettings savedSettings, Document doc)
        {
            _report        = report;
            _savedSettings = savedSettings;
            _doc           = doc;
            _excavationOverlap  = _savedSettings?.ExcavationOverlap ?? OverlapAssignment.Split;
            _backfillOverlap    = _savedSettings?.BackfillOverlap ?? OverlapAssignment.Split;
            _manholeTypeSetting = _savedSettings?.ManholeType ?? ManholeType.PreCast;
            _bacaKaziHesapla    = _savedSettings?.BacaKaziHesapla ?? false;
            _bacaBacaKaziHesapla = _savedSettings?.BacaBacaKaziHesapla ?? false;
            _bacaAltiParcaEklensin = _savedSettings?.BacaAltiParcaEklensin ?? false;
            _bacaKaziDisCapKullan = _savedSettings?.BacaKaziDisCapKullan ?? true;
            _bacaKirmiziKotSurface    = _savedSettings?.BacaKirmiziKotSurface ?? "Arazi1";
            _bacaAraziKotuSurface     = _savedSettings?.BacaAraziKotuSurface ?? "Arazi1";
            _bacaTerrasmanKotuSurface = _savedSettings?.BacaTerrasmanKotuSurface ?? "Arazi1";
            _bacaKirmiziKotC3DSurface    = _savedSettings?.BacaKirmiziKotC3DSurface ?? "";
            _bacaAraziKotuC3DSurface     = _savedSettings?.BacaAraziKotuC3DSurface ?? "";
            _bacaTerrasmanKotuC3DSurface = _savedSettings?.BacaTerrasmanKotuC3DSurface ?? "";
            _kaziSeviyesi       = _savedSettings?.KaziSeviyesi ?? "Kırmızı Kot";
            _dolguSeviyesi      = _savedSettings?.DolguSeviyesi ?? "Kırmızı Kot";
            _bacaKapakSeviyesi  = _savedSettings?.BacaKapakSeviyesi ?? "Kırmızı Kot";
            _ringFillMode       = _savedSettings?.RingFillMode ?? RingFillMode.Greedy;
            _netLengthMode      = _savedSettings?.NetLengthMode ?? NetLengthMode.OuterDiameter;
            BuildForm();
            // Pre-compute pipe–manhole overlap volumes so BacaKazi deduction is available.
            ManholeExcavOverlapService.Compute(_report);
            // Pre-compute manhole-vs-manhole overlap volumes (Bacalar Arası Kazı
            // Çakışması), needed regardless of the toggle since it only affects
            // whether the computed deduction is applied to displayed values.
            ManholeExcavOverlapService.ComputeManholeVsManhole(_report);
            // Show the tables for the saved default method on first open.
            Recalculate();
        }

        private void BuildForm()
        {
            Text            = "Urbano — Metraj (BoQ)";
            ClientSize      = new Size(CW + MARGIN * 2, 620);
            FormBorderStyle = FormBorderStyle.Sizable;
            MinimumSize     = new Size(760, 480);
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);
            BackColor       = Color.WhiteSmoke;

            int y = MARGIN;

            // ── Info banner ───────────────────────────────────────────────────
            _lblInfo = new Label
            {
                Left = MARGIN, Top = y, Width = GW, Height = 20,
                ForeColor = Color.FromArgb(0, 70, 127),
                Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Text = BuildInfoLine()
            };
            Controls.Add(_lblInfo);

            // ── Calculation-method toolbar ────────────────────────────────────
            BuildOptionsBar();

            // ── Tab control ───────────────────────────────────────────────────
            _tabs = new TabControl
            {
                Left = MARGIN, Top = TABS_TOP, Width = GW, Height = 420,
                Anchor = AnchorStyles.Top | AnchorStyles.Left |
                         AnchorStyles.Right | AnchorStyles.Bottom
            };
            Controls.Add(_tabs);

            // ── Export section ────────────────────────────────────────────────
            // Leave 40px below grpExport (height=80) for the Close button + margin.
            int ey = ClientSize.Height - 130;
            var grpExport = new GroupBox
            {
                Text = "Excel'e Aktar",
                Left = MARGIN, Top = ey, Width = GW, Height = 80,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Segoe UI", 8.5f)
            };
            Controls.Add(grpExport);

            var lblLang = new Label
            {
                Text = "Dil:", Left = 8, Top = 26, Width = 70,
                TextAlign = ContentAlignment.MiddleLeft
            };
            _cmbLanguage = new ComboBox
            {
                DropDownStyle = ComboBoxStyle.DropDownList,
                Left = 82, Top = 24, Width = 130
            };
            _cmbLanguage.Items.AddRange(new object[] { "English", "Turkish", "Russian" });
            _cmbLanguage.SelectedIndex = (int)(_savedSettings?.Language ?? ExportLanguage.English);

            _txtExportPath = new TextBox
            {
                Left = 222, Top = 24, Width = GW - 390,
                Text = BuildDefaultExportPath()
            };
            var btnBrowse = new Button
            {
                Text = "Gözat…", Left = GW - 162, Top = 22, Width = 70, Height = 24
            };
            btnBrowse.Click += BrowseExportPath;

            var btnExport = new Button
            {
                Text = "Excel'e Aktar",
                Left = GW - 86, Top = 22, Width = 86, Height = 24,
                BackColor = Color.FromArgb(0, 70, 127),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                Font = new Font("Segoe UI", 9f, FontStyle.Bold)
            };
            btnExport.Click += ExportToExcel;

            grpExport.Controls.AddRange(new Control[]
            {
                lblLang, _cmbLanguage, _txtExportPath, btnBrowse, btnExport
            });

            // ── Close button ──────────────────────────────────────────────────
            var btnClose = new Button
            {
                Text = "Kapat",
                Left = MARGIN, Top = ey + 88, Width = 80, Height = 26,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left
            };
            btnClose.Click += (s, e) => Close();
            Controls.Add(btnClose);
            CancelButton = btnClose;

            Resize += (s, e) =>
            {
                int usableW = ClientSize.Width - MARGIN * 2;
                _tabs.Width = usableW;
                grpExport.Width = usableW;
                grpExport.Top = ClientSize.Height - 130;
                btnClose.Top = ClientSize.Height - 42;
                _tabs.Height = ClientSize.Height - _tabs.Top - 140;
                _txtExportPath.Width = usableW - 390;
                btnBrowse.Left = usableW - 162;
                btnExport.Left = usableW - 86;
                _lblInfo.Width = usableW;
            };
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            base.OnFormClosed(e);
            if (_savedSettings == null || _doc == null) return;
            // _doc.Database is null once the drawing has been closed while this
            // dialog was still open (the Document object itself outlives the
            // close) — must check separately from _doc itself, or HasData
            // throws NullReferenceException on db.TransactionManager.
            var db = _doc.Database;
            if (db == null) return;
            if (_report?.GeneratedAt == default || !DwgBoQStore.HasData(db)) return;
            try
            {
                using (_doc.LockDocument())
                    DwgBoQStore.UpdateSettings(db, _report.GeneratedAt, _savedSettings);
            }
            catch (System.Exception ex)
            {
                // Settings persistence is best-effort on close, but a silent loss of the
                // user's chosen method/language is worth a diagnostic trail.
                System.Diagnostics.Trace.WriteLine(
                    $"[UrbanoMetraj] Ayarlar kaydedilemedi: {ex.Message}");
            }
        }

        // ── Calculation-method toolbar ────────────────────────────────────────

        private void BuildOptionsBar()
        {
            // ── Action buttons ────────────────────────────────────────────────
            int row1 = 36;

            var btnRefresh = new Button
            {
                Text = "Metraj Verisi Güncelle", Left = MARGIN, Top = row1 - 1,
                Width = 168, Height = 28, FlatStyle = FlatStyle.Flat
            };
            btnRefresh.Click += (s, e) =>
            {
                BoQSimplifiedTestV2Command.RequestReopenView();
                _doc.SendStringToExecute("UT_BOQ\n", true, false, true);
                Close();
            };

            // HOLD (2026-07): "3B Katı" (UT_SOLIDS) hidden from the UI — feature not
            // fully validated yet. Command still works from the command line.
            // Restore by un-commenting this block and re-adding btnSolids to Controls.AddRange.
            // var btnSolids = new Button
            // {
            //     Text = "3B Katı", Left = MARGIN + 176, Top = row1 - 1, Width = 86, Height = 28,
            //     FlatStyle = FlatStyle.Flat
            // };
            // btnSolids.Click += (s, e) =>
            // {
            //     SolidsCommand.OverrideExcavation      = SelectedExcavationOverlap;
            //     SolidsCommand.OverrideBackfill        = SelectedBackfillOverlap;
            //     SolidsCommand.OverrideDisplayInterval = _savedSettings?.SolidDisplayInterval ?? 5.0;
            //     _doc.SendStringToExecute("UT_SOLIDS\nUT_BOQ_VIEW\n", true, false, true);
            //     Close();
            // };

            // ⚙ Ayarlar taşındı → Proje Ayarları penceresi ("Genel Ayarlar" sekmesi,
            // UT_PROJE_AYARLARI). Ayarlar orada düzenlenip DWG'ye kaydedilir; bu pencere
            // onları açılışta DWG'den okur ve Hesapla ile uygular.

            // HOLD (2026-07): "Kesit Çiz" (UT_SECTIONS) hidden from the UI — feature not
            // fully validated yet. Command still works from the command line.
            // Restore by un-commenting this block and re-adding btnSections to Controls.AddRange.
            // var btnSections = new Button
            // {
            //     Text = "Kesit Çiz", Left = MARGIN + 270, Top = row1 - 1, Width = 90, Height = 28,
            //     FlatStyle = FlatStyle.Flat
            // };
            // btnSections.Click += (s, e) =>
            // {
            //     SectionsCommand.OverrideExcavation           = SelectedExcavationOverlap;
            //     SectionsCommand.OverrideBackfill             = SelectedBackfillOverlap;
            //     SectionsCommand.OverrideCrossSectionInterval = _savedSettings?.CrossSectionInterval ?? 5.0;
            //     _doc.SendStringToExecute("UT_SECTIONS\n", true, false, true);
            // };

            // The real calculation (geometry + Manhole AI + save), meant to be run
            // after Tür Eşleştirme / Baca-Boru Bağlantı Kuralları links are set up.
            // Takes over the prominent styling the old "Hesapla" button had, since
            // this is the action that actually deserves it now.
            _btnHesapla = new Button
            {
                Text = "Hesapla", Left = MARGIN + 176, Top = row1 - 1, Width = 90, Height = 28,
                BackColor = Color.FromArgb(0, 70, 127), ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 9f, FontStyle.Bold),
                Enabled = false     // enabled only once a Kapsam (scope) is active
            };
            _btnHesapla.Click += (s, e) =>
            {
                // Scope is already applied by ActivateScope: PendingSelectionScope is
                // null for "Tüm Aktif Ağlar", set for "CAD Seçimi" / "Son Seçilen".
                BoQSimplifiedTestV2Command.RequestReopenView();
                _doc.SendStringToExecute("UT_BOQ_HESAPLA\n", true, false, true);
                Close();
            };

            // ── Kapsam (calculation scope) — compact split button ────────────────
            // Main face runs the last-used mode directly; the ▾ arrow opens the menu
            // to switch mode. Hesapla stays disabled until a mode resolves a scope.
            _scopeMode = BoQSimplifiedTestV2Command.LastScopeMode;
            _scopeTip  = new ToolTip();

            _btnScopeMain = new Button
            {
                Left = MARGIN + 272, Top = row1 - 1, Width = 30, Height = 28,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Symbol", 10f),
                Text = ScopeGlyph(_scopeMode)
            };
            _btnScopeArrow = new Button
            {
                Left = MARGIN + 302, Top = row1 - 1, Width = 18, Height = 28,
                FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI", 7f), Text = "▼"
            };

            var scopeMenu = new ContextMenuStrip();
            scopeMenu.Items.Add(ScopeMenuItem("▦  Tüm Aktif Ağlar", ScopeMode.Full));
            scopeMenu.Items.Add(ScopeMenuItem("✛  CAD Seçimi",       ScopeMode.Cad));
            _scopeSonItem = ScopeMenuItem("↻  Son Seçilen",          ScopeMode.Son);
            _scopeSonItem.Enabled = BoQSimplifiedTestV2Command.LastCadScope != null;
            scopeMenu.Items.Add(_scopeSonItem);

            _btnScopeArrow.Click += (s, e) =>
                scopeMenu.Show(_btnScopeArrow, new Point(0, _btnScopeArrow.Height));
            _btnScopeMain.Click += (s, e) =>
            {
                if (_scopeMode == ScopeMode.None)
                    scopeMenu.Show(_btnScopeMain, new Point(0, _btnScopeMain.Height));
                else
                    ActivateScope(_scopeMode);
            };

            Controls.AddRange(new Control[]
            {
                btnRefresh, _btnHesapla,          // HOLD: btnSolids (3B Katı) + btnSections (Kesit Çiz) removed
                _btnScopeMain, _btnScopeArrow
            });

            // Reflect the remembered mode + any pending selection on (re)open — without
            // re-triggering a pick (ActivateScope only runs on user interaction).
            UpdateScopeButton();
            bool pendingReady = BoQSimplifiedTestV2Command.PendingSelectionScope != null
                             && !BoQSimplifiedTestV2Command.PendingSelectionScope.IsEmpty;
            _btnHesapla.Enabled = _scopeMode == ScopeMode.Full || pendingReady;
        }

        // ── Scope split-button plumbing ───────────────────────────────────────

        private ToolStripMenuItem ScopeMenuItem(string text, ScopeMode mode)
        {
            var mi = new ToolStripMenuItem(text);
            mi.Click += (s, e) => ActivateScope(mode);
            return mi;
        }

        private static string ScopeGlyph(ScopeMode mode)
        {
            switch (mode)
            {
                case ScopeMode.Full: return "▦";   // ▦ grid — all active networks
                case ScopeMode.Cad:  return "✛";   // ✛ crosshair — pick from CAD
                case ScopeMode.Son:  return "↻";   // ↻ repeat — reuse last selection
                default:             return "▾";   // ▾ nothing chosen yet
            }
        }

        private static string ScopeName(ScopeMode mode)
        {
            switch (mode)
            {
                case ScopeMode.Full: return "Tüm Aktif Ağlar";
                case ScopeMode.Cad:  return "CAD Seçimi";
                case ScopeMode.Son:  return "Son Seçilen";
                default:             return "Kapsam seçin (▾)";
            }
        }

        private void UpdateScopeButton()
        {
            _btnScopeMain.Text = ScopeGlyph(_scopeMode);
            _scopeTip.SetToolTip(_btnScopeMain,  "Hesaplama kapsamı: " + ScopeName(_scopeMode));
            _scopeTip.SetToolTip(_btnScopeArrow, "Kapsam yöntemini değiştir");
            _lblInfo.Text = BuildInfoLine();
        }

        // Applies a scope mode: "Tüm Aktif Ağlar" clears any selection; "CAD Seçimi"
        // launches a fresh pick; "Son Seçilen" reuses the last CAD pick (this session).
        private void ActivateScope(ScopeMode mode)
        {
            _scopeMode = mode;
            BoQSimplifiedTestV2Command.LastScopeMode = mode;

            switch (mode)
            {
                case ScopeMode.Full:
                    BoQSimplifiedTestV2Command.PendingSelectionScope   = null;
                    BoQSimplifiedTestV2Command.PendingSelectionSummary = null;
                    UpdateScopeButton();
                    _btnHesapla.Enabled = true;
                    break;

                case ScopeMode.Cad:
                    BoQSimplifiedTestV2Command.RequestReopenView();
                    _doc.SendStringToExecute("UT_BOQ_CADSELECT\n", true, false, true);
                    Close();
                    break;

                case ScopeMode.Son:
                    if (BoQSimplifiedTestV2Command.LastCadScope != null
                        && !BoQSimplifiedTestV2Command.LastCadScope.IsEmpty)
                    {
                        BoQSimplifiedTestV2Command.PendingSelectionScope   =
                            BoQSimplifiedTestV2Command.LastCadScope;
                        BoQSimplifiedTestV2Command.PendingSelectionSummary =
                            BoQSimplifiedTestV2Command.LastCadSummary;
                        UpdateScopeButton();
                        _btnHesapla.Enabled = true;
                    }
                    else
                    {
                        MessageBox.Show(this,
                            "Henüz bir CAD seçimi yapılmadı.\nÖnce 'CAD Seçimi' ile bir seçim yapın.",
                            "Son Seçilen", MessageBoxButtons.OK, MessageBoxIcon.Information);
                        _scopeMode = ScopeMode.None;
                        UpdateScopeButton();
                    }
                    break;
            }
        }

        // ── Recalculate tables from the cache for the selected methods ─────────

        private ManholeType SelectedManholeType => _manholeTypeSetting;

        private void Recalculate()
        {
            TiePreference kazi  = BoQScenarioAggregator.Map(SelectedExcavationOverlap);
            TiePreference dolgu = BoQScenarioAggregator.Map(SelectedBackfillOverlap);
            BoQScenarioAggregator.Apply(_report, kazi, dolgu, BacaKaziHesapla);

            if (_savedSettings != null)
            {
                _savedSettings.ExcavationOverlap = SelectedExcavationOverlap;
                _savedSettings.BackfillOverlap   = SelectedBackfillOverlap;
                _savedSettings.ManholeType       = SelectedManholeType;
                _savedSettings.BacaKaziHesapla   = BacaKaziHesapla;
                _savedSettings.BacaBacaKaziHesapla = BacaBacaKaziHesapla;
                _savedSettings.BacaAltiParcaEklensin = _bacaAltiParcaEklensin;
                _savedSettings.BacaKaziDisCapKullan = _bacaKaziDisCapKullan;
            }

            RebuildTabs();
            _lblInfo.Text = BuildInfoLine();
        }

        private void RebuildTabs()
        {
            int keep = _tabs.TabPages.Count > 0 ? _tabs.SelectedIndex : 0;
            _tabs.SuspendLayout();
            _tabs.TabPages.Clear();

            AddSummaryTab();
            foreach (var sys in _report.Systems ?? Enumerable.Empty<SystemBoQ>())
            {
                AddPipesTab(sys);
                AddManholesTab(sys);
            }
            AddBomTab(SelectedManholeType);
            if (SelectedManholeType == ManholeType.PreCast) AddBomDetailTab();

            if (keep >= 0 && keep < _tabs.TabPages.Count) _tabs.SelectedIndex = keep;
            _tabs.ResumeLayout();
        }

        // ── Tab builders ──────────────────────────────────────────────────────

        private void AddSummaryTab()
        {
            var page = new TabPage("Özet");
            var grid = MakeGrid();
            grid.Columns.Add("Item",  "Kalem");
            grid.Columns.Add("Value", "Değer");
            grid.Columns[0].Width = 280;
            grid.Columns[1].Width = 160;

            Add(grid, "Toplam boru uzunluğu (m)",  $"{_report.TotalPipeLength:N2}");
            Add(grid, "Toplam baca",                $"{_report.TotalManholeCount}");
            Add(grid, "Toplam kazı hacmi (m³)",     $"{_report.TotalExcavationVolume:N3}");
            Add(grid, "Toplam yataklama (m³)",      $"{_report.TotalBeddingVolume:N3}");
            Add(grid, "Toplam gömlekleme (m³)",     $"{_report.TotalSurroundVolume:N3}");
            Add(grid, "Toplam geri dolgu (m³)",     $"{_report.TotalBackfillVolume:N3}");
            double netManholeExcav    = BacaBacaKaziHesapla
                ? Math.Max(0, _report.TotalManholeExcavationVolume - _report.TotalManholeVsManholeExcavDeducted)
                : _report.TotalManholeExcavationVolume;
            double netManholeBackfill = BacaBacaKaziHesapla
                ? Math.Max(0, _report.TotalManholeBackfillVolume - _report.TotalManholeVsManholeBackfillDeducted)
                : _report.TotalManholeBackfillVolume;

            Add(grid, "Toplam baca kazısı (m³)",
                BacaKaziHesapla ? $"{netManholeExcav:N3}" : "0,000");
            Add(grid, "Toplam baca yatakalama (m³)",
                BacaKaziHesapla ? $"{_report.TotalManholeSubBaseVolume:N3}" : "0,000");
            Add(grid, "Toplam baca geri dolgu (m³)",
                BacaKaziHesapla ? $"{netManholeBackfill:N3}" : "0,000");
            Add(grid, "Toplam bacalar arası kazı çakışması (m³)",
                (BacaKaziHesapla && BacaBacaKaziHesapla) ? $"{_report.TotalManholeVsManholeExcavDeducted:N3}" : "0,000");
            Add(grid, "Toplam bacalar arası geri dolgu çakışması (m³)",
                (BacaKaziHesapla && BacaBacaKaziHesapla) ? $"{_report.TotalManholeVsManholeBackfillDeducted:N3}" : "0,000");
            Add(grid, "Çakışmadan düşülen kazı (m³)",  $"{_report.TotalOverlapExcavDeducted:N3}");
            Add(grid, "Çakışmadan düşülen dolgu (m³)", $"{_report.TotalOverlapBackfillDeducted:N3}");
            Add(grid, "İstasyon verisi",
                $"{_report.SectionDebug?.Sum(s => s.Stations?.Count ?? 0) ?? 0} istasyon / " +
                $"{_report.SectionDebug?.Count ?? 0} kesit");

            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        private void AddPipesTab(SystemBoQ sys)
        {
            var page = new TabPage($"{sys.SystemName} – Borular");
            var grid = MakeGrid();
            foreach (string col in new[]
            {
                "Boru Hattı", "Çap (mm)", "Malzeme", "Uzunluk (m)", "Net Uzunluk (m)",
                "Kazı (m³)", "Yataklama (m³)", "Gömlekleme (m³)",
                "Geri dolgu (m³)", "Düşülen (m³)"
            })
                grid.Columns.Add(col, col);
            grid.Columns[0].Width = 120;
            grid.Columns[1].Width = 75;
            grid.Columns[2].Width = 70;
            for (int i = 3; i < grid.Columns.Count; i++) grid.Columns[i].Width = 95;

            var sections = _report.SectionDebug?
                .Where(r => r.SystemName == sys.SystemName)
                .OrderBy(r => r.DiameterMm)
                .ThenBy(r => r.PipeName)
                .ToList();

            if (sections != null && sections.Count > 0)
            {
                double gtLen = 0, gtNetLen = 0, gtEx = 0, gtBe = 0, gtSu = 0, gtBa = 0, gtOv = 0;

                var groups = sections.GroupBy(r => r.DiameterMm);
                foreach (var grp in groups)
                {
                    double stLen = 0, stNetLen = 0, stEx = 0, stBe = 0, stSu = 0, stBa = 0, stOv = 0;

                    foreach (var s in grp)
                    {
                        grid.Rows.Add(
                            s.PipeName,
                            s.DiameterMm,
                            s.Material,
                            s.Length2D.ToString("N2"),
                            s.NetLength.ToString("N2"),
                            s.VExcav.ToString("N3"),
                            s.VBedding.ToString("N3"),
                            s.VSurround.ToString("N3"),
                            s.VBackfill.ToString("N3"),
                            (s.OverlapExcavDeducted + s.OverlapBackfillDeducted).ToString("N3"));

                        stLen += s.Length2D;  stNetLen += s.NetLength;  stEx += s.VExcav;
                        stBe  += s.VBedding;  stSu += s.VSurround;
                        stBa  += s.VBackfill;
                        stOv  += s.OverlapExcavDeducted + s.OverlapBackfillDeducted;
                    }

                    // Subtotal row per diameter
                    int sr = grid.Rows.Add(
                        $"Ara Toplam Ø{grp.Key} mm", "", "",
                        stLen.ToString("N2"),
                        stNetLen.ToString("N2"),
                        stEx.ToString("N3"),
                        stBe.ToString("N3"),
                        stSu.ToString("N3"),
                        stBa.ToString("N3"),
                        stOv.ToString("N3"));
                    foreach (DataGridViewCell c in grid.Rows[sr].Cells)
                    {
                        c.Style.BackColor = Color.FromArgb(0, 90, 160);
                        c.Style.ForeColor = Color.White;
                        c.Style.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    }

                    gtLen += stLen; gtNetLen += stNetLen; gtEx += stEx; gtBe += stBe;
                    gtSu  += stSu;  gtBa += stBa; gtOv += stOv;
                }

                // Grand total row
                int tr = grid.Rows.Add(
                    "GENEL TOPLAM", "", "",
                    gtLen.ToString("N2"),
                    gtNetLen.ToString("N2"),
                    gtEx.ToString("N3"),
                    gtBe.ToString("N3"),
                    gtSu.ToString("N3"),
                    gtBa.ToString("N3"),
                    gtOv.ToString("N3"));
                foreach (DataGridViewCell c in grid.Rows[tr].Cells)
                {
                    c.Style.BackColor = Color.FromArgb(0, 70, 127);
                    c.Style.ForeColor = Color.White;
                    c.Style.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                }
            }
            else
            {
                // Fallback: old DWG without section debug — aggregated view
                // Net Uzunluk left blank: PipeItem is a diameter+material aggregate with
                // no per-pipe start/end manhole link to attribute a reduction to.
                foreach (var p in sys.Pipes)
                {
                    grid.Rows.Add(
                        "—", p.Diameter, p.Material,
                        p.TotalLength.ToString("N2"),
                        "",
                        p.TotalExcavationVolume.ToString("N3"),
                        p.TotalBeddingVolume.ToString("N3"),
                        p.TotalSurroundVolume.ToString("N3"),
                        p.TotalBackfillVolume.ToString("N3"),
                        (p.OverlapExcavDeducted + p.OverlapBackfillDeducted).ToString("N3"));
                }

                if (sys.Pipes.Count > 0)
                {
                    int tr = grid.Rows.Add(
                        "GENEL TOPLAM", "", "",
                        sys.Pipes.Sum(p => p.TotalLength).ToString("N2"),
                        "",
                        sys.Pipes.Sum(p => p.TotalExcavationVolume).ToString("N3"),
                        sys.Pipes.Sum(p => p.TotalBeddingVolume).ToString("N3"),
                        sys.Pipes.Sum(p => p.TotalSurroundVolume).ToString("N3"),
                        sys.Pipes.Sum(p => p.TotalBackfillVolume).ToString("N3"),
                        sys.Pipes.Sum(p => p.OverlapExcavDeducted + p.OverlapBackfillDeducted).ToString("N3"));
                    foreach (DataGridViewCell c in grid.Rows[tr].Cells)
                    {
                        c.Style.BackColor = Color.FromArgb(0, 70, 127);
                        c.Style.ForeColor = Color.White;
                        c.Style.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    }
                }
            }

            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        private void AddManholesTab(SystemBoQ sys)
        {
            var page = new TabPage($"{sys.SystemName} – Bacalar");
            var grid = MakeGrid();
            foreach (string col in new[]
            {
                "Ad", "Tip", "Çap (mm)", "Derinlik (m)",
                "Kazı Derin. (m)", "Kazı Hacmi (m³)", "Geri Dolgu (m³)",
                "Baca Yatakalama (m³)", "B-B Kazı Çak. (m³)", "B-B G.Dolgu Çak. (m³)",
                "X (m)", "Y (m)", "Zemin Z (m)"
            })
                grid.Columns.Add(col, col);
            grid.Columns[0].Width = 70;
            grid.Columns[1].Width = 180;
            grid.Columns[2].Width = 70;
            grid.Columns[3].Width = 90;
            grid.Columns[4].Width = 100;
            grid.Columns[5].Width = 105;
            grid.Columns[6].Width = 105;
            grid.Columns[7].Width = 125;
            grid.Columns[8].Width = 115;
            grid.Columns[9].Width = 130;
            for (int i = 10; i < grid.Columns.Count; i++) grid.Columns[i].Width = 90;

            bool showMhExcav   = BacaKaziHesapla;
            bool showMhOverlap = BacaBacaKaziHesapla;
            foreach (var m in sys.Manholes)
            {
                double netExcav    = showMhOverlap ? Math.Max(0, m.ExcavationVolume - m.ManholeVsManholeExcavDeducted)    : m.ExcavationVolume;
                double netBackfill = showMhOverlap ? Math.Max(0, m.BackfillVolume   - m.ManholeVsManholeBackfillDeducted) : m.BackfillVolume;
                grid.Rows.Add(
                    m.NodeName, m.SmartTypeName ?? "", m.DiameterDisplay,
                    m.Depth.ToString("N2"),
                    showMhExcav ? m.ExcavationDepth.ToString("N3") : "0,000",
                    showMhExcav ? netExcav.ToString("N3")          : "0,000",
                    showMhExcav ? netBackfill.ToString("N3")       : "0,000",
                    showMhExcav ? m.SubBaseVolume.ToString("N3")   : "0,000",
                    (showMhExcav && showMhOverlap) ? m.ManholeVsManholeExcavDeducted.ToString("N3")    : "0,000",
                    (showMhExcav && showMhOverlap) ? m.ManholeVsManholeBackfillDeducted.ToString("N3") : "0,000",
                    m.X.ToString("N3"), m.Y.ToString("N3"),
                    m.TerrainElevation.ToString("N3"));
            }

            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        private void AddBomTab(ManholeType manholeType)
        {
            bool isPreCast = manholeType == ManholeType.PreCast;
            string title   = isPreCast ? "Baca BOM — Prefabrik" : "Baca BOM — Yerinde Döküm";
            var page = new TabPage(title);
            var grid = MakeGrid();

            if (isPreCast)
            {
                grid.Columns.Add("Part",     "Parça / Malzeme");
                grid.Columns.Add("Qty",      "Adet");
                grid.Columns.Add("Unit",     "Birim");
                grid.Columns.Add("PozNo",    "Poz No");
                grid.Columns.Add("Aciklama", "Açıklama");
                grid.Columns.Add("MatVol",   "Malzeme Hacmi (m³)");
                grid.Columns.Add("ExtVol",   "Dış Hacim (m³)");
                grid.Columns[0].Width = 220;
                grid.Columns[1].Width = 60;
                grid.Columns[2].Width = 60;
                grid.Columns[3].Width = 90;
                grid.Columns[4].Width = 160;
                grid.Columns[5].Width = 110;
                grid.Columns[6].Width = 110;

                var bomGroups = ManholeAIService.BuildBomBySystem(_report,
                    new BoQSettings { ManholeType = ManholeType.PreCast });
                foreach (var grp in bomGroups)
                {
                    if (bomGroups.Count > 1)
                    {
                        int r = grid.Rows.Add($"── {grp.SystemName} ──", "", "", "", "", "", "");
                        grid.Rows[r].DefaultCellStyle.BackColor = Color.FromArgb(220, 230, 245);
                        grid.Rows[r].DefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    }
                    foreach (var line in grp.Lines)
                        grid.Rows.Add(line.Description, line.Quantity, line.Unit,
                            line.PozNo, line.Aciklama,
                            line.TotalMaterialVolume.ToString("N4"),
                            line.TotalExternalVolume.ToString("N4"));
                }
            }
            else
            {
                grid.Columns.Add("Desc",  "Baca Tipi");
                grid.Columns.Add("Qty",   "Adet");
                grid.Columns.Add("Unit",  "Birim");
                grid.Columns.Add("Depth", "Toplam Beton Derinliği (m)");
                grid.Columns[0].Width = 200;
                grid.Columns[1].Width = 60;
                grid.Columns[2].Width = 60;
                grid.Columns[3].Width = 180;

                var bomGroups = ManholeAIService.BuildBomBySystem(_report,
                    new BoQSettings { ManholeType = ManholeType.CastInPlace });
                foreach (var grp in bomGroups)
                {
                    if (bomGroups.Count > 1)
                    {
                        int r = grid.Rows.Add($"── {grp.SystemName} ──", "", "", "");
                        grid.Rows[r].DefaultCellStyle.BackColor = Color.FromArgb(220, 230, 245);
                        grid.Rows[r].DefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
                    }
                    foreach (var line in grp.Lines)
                        grid.Rows.Add(line.Description, line.Quantity, line.Unit,
                            line.TotalDepthM > 0 ? line.TotalDepthM.ToString("N2") : "");
                }
            }

            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        /// <summary>
        /// One row per (baca, parça) — not an aggregate across every manhole
        /// (user directive 2026-07-06: "Baca BOM — Prefabrik" only shows a
        /// combined total; this shows exactly which parts/quantities each
        /// SPECIFIC baca uses).
        /// </summary>
        private void AddBomDetailTab()
        {
            var page = new TabPage("Baca BOM — Detaylı");
            var grid = MakeGrid();

            grid.Columns.Add("Sistem",   "Sistem");
            grid.Columns.Add("Baca",     "Baca Adı");
            grid.Columns.Add("Diam",     "Çap (mm)");
            grid.Columns.Add("Part",     "Parça Adı");
            grid.Columns.Add("Height",   "Yükseklik (mm)");
            grid.Columns.Add("Qty",      "Adet");
            grid.Columns.Add("PozNo",    "Poz No");
            grid.Columns.Add("Aciklama", "Açıklama");
            grid.Columns.Add("MatVol",   "Malzeme Hacmi (m³)");
            grid.Columns.Add("ExtVol",   "Dış Hacim (m³)");
            grid.Columns[0].Width = 90;
            grid.Columns[1].Width = 90;
            grid.Columns[2].Width = 70;
            grid.Columns[3].Width = 180;
            grid.Columns[4].Width = 100;
            grid.Columns[5].Width = 55;
            grid.Columns[6].Width = 80;
            grid.Columns[7].Width = 140;
            grid.Columns[8].Width = 100;
            grid.Columns[9].Width = 100;

            foreach (var sys in _report.Systems ?? Enumerable.Empty<SystemBoQ>())
            {
                foreach (var mh in sys.Manholes.OrderBy(m => m.NodeName, StringComparer.OrdinalIgnoreCase))
                {
                    if (mh.StackPreCast == null || mh.StackPreCast.Parts.Count == 0)
                    {
                        int r = grid.Rows.Add(sys.SystemName, mh.NodeName, mh.DiameterDisplay,
                            "(Prefabrik eşleşme bulunamadı)", "", "", "", "", "", "");
                        grid.Rows[r].DefaultCellStyle.ForeColor = Color.Gray;
                        grid.Rows[r].DefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Italic);
                        continue;
                    }

                    foreach (var part in mh.StackPreCast.Parts)
                        grid.Rows.Add(sys.SystemName, mh.NodeName, mh.DiameterDisplay,
                            part.PartName, (part.HeightM * 1000.0).ToString("N1"),
                            part.Count, part.PozNo, part.Aciklama,
                            (part.UnitMaterialVolume * part.Count).ToString("N4"),
                            (part.UnitExternalVolume * part.Count).ToString("N4"));
                }
            }

            page.Controls.Add(grid);
            _tabs.TabPages.Add(page);
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private void BrowseExportPath(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog
            {
                Title = "Excel Raporunu Kaydet",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                FileName = "Urbano_BoQ_Export.xlsx",
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog(this) == DialogResult.OK)
                    _txtExportPath.Text = dlg.FileName;
            }
        }

        private void ExportToExcel(object sender, EventArgs e)
        {
            string path = _txtExportPath.Text.Trim();
            if (string.IsNullOrWhiteSpace(path))
            {
                MessageBox.Show(this, "Lütfen bir Excel çıktı yolu seçin.",
                    "Aktar", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            try
            {
                var exportSettings = new BoQSettings
                {
                    Language       = (ExportLanguage)_cmbLanguage.SelectedIndex,
                    ExportFilePath = path,
                    ManholeType    = SelectedManholeType
                };
                ExcelExportService.Export(_report, exportSettings);

                MessageBox.Show(this, $"Excel raporu kaydedildi:\n{path}",
                    "Aktarım Tamam", MessageBoxButtons.OK, MessageBoxIcon.Information);

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path, UseShellExecute = true
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Aktarım başarısız:\n{ex.Message}",
                    "Aktarım Hatası", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static DataGridView MakeGrid()
        {
            var g = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                BackgroundColor = Color.White,
                BorderStyle = BorderStyle.None,
                ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
                ColumnHeadersHeight = 26,
                Font = new Font("Segoe UI", 8.5f)
            };
            g.ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(0, 70, 127);
            g.ColumnHeadersDefaultCellStyle.ForeColor = Color.White;
            g.ColumnHeadersDefaultCellStyle.Font = new Font("Segoe UI", 8.5f, FontStyle.Bold);
            g.EnableHeadersVisualStyles = false;
            g.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(235, 241, 250);
            g.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.None;
            g.RowTemplate.Height = 22;
            return g;
        }

        private static void Add(DataGridView g, string item, string value)
            => g.Rows.Add(item, value);

        private string BuildInfoLine()
        {
            int sections = _report.SectionDebug?.Count ?? 0;
            int stations = _report.SectionDebug?.Sum(s => s.Stations?.Count ?? 0) ?? 0;
            int systems  = _report.Systems?.Count ?? 0;
            string line = $"Metraj — {systems} sistem  |  {sections} kesit  |  {stations} istasyon" +
                          $"  |  Oluşturma: {_report.GeneratedAt:g}";

            // Surface an active CAD selection so the scope of the shown/next calc is clear.
            string sel = BoQSimplifiedTestV2Command.PendingSelectionSummary;
            if ((_scopeMode == ScopeMode.Cad || _scopeMode == ScopeMode.Son)
                && !string.IsNullOrEmpty(sel))
                line += $"  |  {ScopeName(_scopeMode)}: {sel}";
            return line;
        }

        private static string BuildDefaultExportPath()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            return Path.Combine(desktop, $"Urbano_BoQ_{DateTime.Now:yyyyMMdd_HHmm}.xlsx");
        }
    }
}
