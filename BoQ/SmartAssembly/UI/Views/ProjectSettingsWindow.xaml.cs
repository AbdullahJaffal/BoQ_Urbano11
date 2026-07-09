using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Autodesk.AutoCAD.ApplicationServices;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.Views
{
    /// <summary>
    /// Modeless WPF window hosting the DWG-specific "Proje Ayarları" tabs:
    /// Proje Kurulumu (DWG), Tür Eşleştirme and Genel Ayarlar. These used to be tabs
    /// inside the Akıllı Montaj window; they were split out into their own window but
    /// still share the <b>same</b> <see cref="SmartAssemblyMainVm"/> instance, so catalog
    /// and DWG-bound state (templates, type mapping) stay in sync between the two windows.
    ///
    /// The "Genel Ayarlar" tab (moved here 2026-07-09 from the Metraj window's ⚙ Ayarlar
    /// button) hosts the WinForms <see cref="GeneralSettingsControl"/> via WindowsFormsHost
    /// and reads/writes <see cref="BoQSettings"/> straight to the active DWG.
    ///
    /// Opened via UT_PROJE_AYARLARI (see SmartAssemblyCommand.ShowProjectSettings).
    /// </summary>
    public partial class ProjectSettingsWindow : Window
    {
        // The BoQSettings currently loaded into the Genel Ayarlar tab. Kept so that
        // fields the panel does NOT expose (Language, EnableClashDetection,
        // ManholeConfigPath, ExportFilePath) are preserved on save.
        private BoQSettings _currentSettings;
        private GeneralSettingsControl _settingsControl;

        public ProjectSettingsWindow()
        {
            InitializeComponent();
            DataContext = new SmartAssemblyMainVm();
            BuildGeneralSettingsTab();
        }

        /// <summary>Opens the window bound to an already-built shared ViewModel.</summary>
        public ProjectSettingsWindow(SmartAssemblyMainVm viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
            BuildGeneralSettingsTab();
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            // Detach this window's DataContext only — the shared ViewModel object
            // itself stays alive as long as SmartAssemblyCommand holds it (and the
            // Akıllı Montaj window may still be bound to it).
            DataContext = null;
        }

        // ── Genel Ayarlar tab ──────────────────────────────────────────────────

        private void BuildGeneralSettingsTab()
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;

            BoQSettings s = null;
            var surfaces = new List<string>();
            if (doc != null)
            {
                try
                {
                    var (_, loaded) = DwgBoQStore.Load(doc.Database);
                    s = loaded;
                }
                catch { /* no BoQ store yet — fall through to defaults */ }

                try { surfaces = Civil3DSurfaceService.GetSurfaceNames(doc); }
                catch { surfaces = new List<string>(); }
            }

            _currentSettings = s ?? new BoQSettings();

            _settingsControl = new GeneralSettingsControl(
                _currentSettings.SolidDisplayInterval, _currentSettings.CrossSectionInterval,
                _currentSettings.ExcavationOverlap, _currentSettings.BackfillOverlap,
                _currentSettings.ManholeType,
                _currentSettings.BacaKaziHesapla, _currentSettings.BacaBacaKaziHesapla,
                _currentSettings.BacaAltiParcaEklensin, _currentSettings.BacaKaziDisCapKullan,
                _currentSettings.BacaKirmiziKotSurface, _currentSettings.BacaAraziKotuSurface,
                _currentSettings.BacaTerrasmanKotuSurface,
                surfaces,
                _currentSettings.BacaKirmiziKotC3DSurface, _currentSettings.BacaAraziKotuC3DSurface,
                _currentSettings.BacaTerrasmanKotuC3DSurface,
                _currentSettings.KaziSeviyesi, _currentSettings.DolguSeviyesi,
                _currentSettings.BacaKapakSeviyesi,
                _currentSettings.RingFillMode, _currentSettings.NetLengthMode,
                _currentSettings.MetrajDegiskenParcaBandM);

            _settingsHost.Child = _settingsControl;

            if (doc == null)
                _settingsStatus.Text = "Aktif çizim yok — kaydetmek için bir DWG açın.";
        }

        private void GeneralSettings_Save_Click(object sender, RoutedEventArgs e)
        {
            var doc = AcadApp.DocumentManager.MdiActiveDocument;
            if (doc == null || _settingsControl == null)
            {
                _settingsStatus.Text = "Aktif çizim yok — kaydedilemedi.";
                return;
            }

            // Overwrite only the fields the panel edits; preserve the rest.
            _currentSettings.SolidDisplayInterval        = _settingsControl.SolidDisplayInterval;
            _currentSettings.CrossSectionInterval        = _settingsControl.CrossSectionInterval;
            _currentSettings.ExcavationOverlap           = _settingsControl.ExcavationOverlap;
            _currentSettings.BackfillOverlap             = _settingsControl.BackfillOverlap;
            _currentSettings.ManholeType                 = _settingsControl.SelectedManholeType;
            _currentSettings.BacaKaziHesapla             = _settingsControl.BacaKaziHesapla;
            _currentSettings.BacaBacaKaziHesapla         = _settingsControl.BacaBacaKaziHesapla;
            _currentSettings.BacaAltiParcaEklensin       = _settingsControl.BacaAltiParcaEklensin;
            _currentSettings.BacaKaziDisCapKullan        = _settingsControl.BacaKaziDisCapKullan;
            _currentSettings.BacaKirmiziKotSurface       = _settingsControl.BacaKirmiziKotSurface;
            _currentSettings.BacaAraziKotuSurface        = _settingsControl.BacaAraziKotuSurface;
            _currentSettings.BacaTerrasmanKotuSurface    = _settingsControl.BacaTerrasmanKotuSurface;
            _currentSettings.BacaKirmiziKotC3DSurface    = _settingsControl.BacaKirmiziKotC3DSurface;
            _currentSettings.BacaAraziKotuC3DSurface     = _settingsControl.BacaAraziKotuC3DSurface;
            _currentSettings.BacaTerrasmanKotuC3DSurface = _settingsControl.BacaTerrasmanKotuC3DSurface;
            _currentSettings.KaziSeviyesi                = _settingsControl.KaziSeviyesi;
            _currentSettings.DolguSeviyesi               = _settingsControl.DolguSeviyesi;
            _currentSettings.BacaKapakSeviyesi           = _settingsControl.BacaKapakSeviyesi;
            _currentSettings.RingFillMode                = _settingsControl.RingFillMode;
            _currentSettings.NetLengthMode               = _settingsControl.NetLengthMode;
            _currentSettings.MetrajDegiskenParcaBandM    = _settingsControl.MetrajDegiskenParcaBandM;

            try
            {
                using (doc.LockDocument())
                {
                    DwgBoQStore.SaveSettings(doc.Database, _currentSettings);
                }
                _settingsStatus.Text = "Ayarlar çizime kaydedildi — Metraj'da Hesapla ile uygulayın.";
            }
            catch (System.Exception ex)
            {
                _settingsStatus.Text = "Kaydetme hatası: " + ex.Message;
            }
        }

        // Right-click doesn't select a ListBox item by default; select it first so the
        // row-scoped context-menu commands (Çoğalt / Sil) act on the clicked template.
        private void List_SelectItemOnRightClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is ListBoxItem))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is ListBoxItem item)
                item.IsSelected = true;
        }
    }
}
