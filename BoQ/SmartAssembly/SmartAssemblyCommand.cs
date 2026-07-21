using System;
using System.Collections.Generic;
using System.Linq;
using Bricscad.ApplicationServices;
using Teigha.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.ProjectRules.Services;
using UrbanoMetraj.BoQ.ProjectRules.UI.ViewModels;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;
using UrbanoMetraj.BoQ.SmartAssembly.UI.Views;
using UrbanoMetraj.BoQ.TypeMapping.Services;
using UrbanoMetraj.BoQ.UI.NetworkPanel;

using Exception = System.Exception;

// AutoCAD must discover this class to register [CommandMethod] attributes.
[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SmartAssembly.SmartAssemblyCommand))]

namespace UrbanoMetraj.BoQ.SmartAssembly
{
    /// <summary>
    /// AutoCAD command entry point for the Smart Manhole Assembly system.
    ///
    /// Commands
    /// ─────────────────────────────────────────────────────────────────────
    ///  UT_SMART_ASSEMBLY   Open the modeless Akıllı Montaj window (catalogs +
    ///                   rules). Re-activates the existing window if already open.
    ///  UT_PROJE_AYARLARI   Open the modeless Proje Ayarları window (Proje Kurulumu
    ///                   + Tür Eşleştirme), sharing the same ViewModel.
    /// </summary>
    public class SmartAssemblyCommand
    {
        // Static reference keeps the modeless window alive across command calls
        // and prevents the GC from collecting it when the command method returns.
        // This is the single shared "Akıllı Montaj" window — every catalog that used
        // to open its own standalone window (Kazı Kuralları, Boru/Hendek/Zemin/Dolgu
        // Katalogları) now lives here as an additional tab.
        private static SmartAssemblyWindow    _window;             // Akıllı Montaj
        private static ProjectSettingsWindow  _projSettingsWindow; // Proje Ayarları
        private static SmartAssemblyMainVm    _mainVm;

        /// <summary>True while either shared window (Akıllı Montaj or Proje Ayarları)
        /// is still open — both bind the same <see cref="_mainVm"/>.</summary>
        private static bool AnyWindowOpen() =>
            (_window             != null && _window.IsLoaded) ||
            (_projSettingsWindow != null && _projSettingsWindow.IsLoaded);

        /// <summary>Returns the live ViewModel so UT_PIPE_CATALOG_LIVE_EXTRACT can
        /// push catalog updates without re-opening either shared window.</summary>
        internal static SmartAssemblyMainVm GetMainVm() =>
            (_mainVm != null && AnyWindowOpen()) ? _mainVm : null;

        /// <summary>Builds the shared ViewModel (and its DWG-bound state) once, reusing
        /// it while either window is open and rebuilding it from the active DWG after
        /// both have closed. Does NOT open any window — callers show whichever window
        /// they need (Akıllı Montaj via <see cref="EnsureMainVm"/>, Proje Ayarları via
        /// <see cref="ShowProjectSettings"/>).</summary>
        internal static SmartAssemblyMainVm EnsureVm()
        {
            if (_mainVm != null && AnyWindowOpen()) return _mainVm;

            // Build the shared catalog + load any project templates already in NOD.
            var catalog = new SmartAssemblyMasterCatalog();
            _mainVm = new SmartAssemblyMainVm(catalog, PipeCatalogStore.Current);

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                // Type Mapping data (both the discovered Urbano catalog items AND the
                // user's links) lives in THIS document's DWG NOD (not a shared
                // %APPDATA% file like every other tab, and not the ephemeral %TEMP%
                // export — that way the mapping travels with the DWG to another
                // machine). Load it now and hand the tab a reload delegate + save
                // callback bound to whichever document is active at call-time (kept
                // AutoCAD-agnostic: the ViewModel never touches Autodesk types
                // directly, see TypeMappingTabVm).
                TypeMappingStore.LoadFromDwg(doc.Database);
                _mainVm.TypeMappingTab.Initialize(
                    TypeMappingNodManager.LoadDiscoveredItems(doc.Database),
                    TypeMappingStore.AllPipeLinks.ToList(),
                    TypeMappingStore.AllManholeLinks.ToList(),
                    () =>
                    {
                        var activeDb = Application.DocumentManager.MdiActiveDocument?.Database;
                        return activeDb != null
                            ? TypeMappingNodManager.LoadDiscoveredItems(activeDb)
                            : new List<CatalogItemInfo>();
                    },
                    (pipeLinks, manholeLinks) =>
                    {
                        var activeDoc = Application.DocumentManager.MdiActiveDocument;
                        if (activeDoc == null) return;
                        // The Save button runs from a modeless window's click handler,
                        // outside any active AutoCAD command — writing to the database
                        // needs an explicit document lock here (eLockViolation
                        // otherwise), unlike DwgBoQStore.Save's callers which already
                        // run inside a locked command context.
                        using (activeDoc.LockDocument())
                        {
                            foreach (var l in pipeLinks)    TypeMappingStore.SavePipeLink(activeDoc.Database, l);
                            foreach (var l in manholeLinks) TypeMappingStore.SaveManholeLink(activeDoc.Database, l);
                        }
                    });

                // Project Rules tab (new per-network calc rule source). Networks come from the
                // shared UT_NET_PANEL scrape (URBANO_NETWORKS NOD) and the Active flags from the
                // shared URBANOLOCK_UI_STATE NOD (read directly, so the badge is correct even when
                // the live palette is UrbanoLock's, whose in-memory active set this plugin can't see).
                // The rule set + save callback are bound to this document's DWG.
                Func<List<NetworkSeed>> reloadSeeds = () =>
                {
                    var d = Application.DocumentManager.MdiActiveDocument?.Database;
                    if (d == null) return new List<NetworkSeed>();
                    var nets   = NetworkSessionManager.GetAllNetworks(d);
                    var active = NetworkSessionManager.ResolveActiveFromNod(d, nets);
                    return nets.Select(n => new NetworkSeed { Name = n, IsActive = active.Contains(n) }).ToList();
                };
                _mainVm.ProjectRulesTab.Initialize(
                    reloadSeeds(),
                    ProjectRulesNodManager.Load(doc.Database),
                    ruleSet =>
                    {
                        var activeDoc = Application.DocumentManager.MdiActiveDocument;
                        if (activeDoc == null) return;
                        using (activeDoc.LockDocument())
                            ProjectRulesNodManager.Save(activeDoc.Database, ruleSet);
                    },
                    reloadSeeds,
                    // Interactive exception pick: queued as a modal command so GetSelection runs in a
                    // proper document context (layer-filtered to the network), then the AG_GUIDs come
                    // back to the VM (main thread).
                    (prompt, kind, layerNet, onPicked) => ProjectRuleExceptionPicker.RequestPick(prompt, kind, layerNet, onPicked),
                    // AG_GUID → name map from the last ARS_EXPORT_XML (for the exception name display).
                    () => BoQParserService.BuildEntityNameMap(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "urbano_boq_export.xml")),
                    // "XML Güncelle" — same action as the Metraj window's "Metraj Verisi Güncelle": run
                    // the extract-only command that (re)writes the export XML from Urbano.
                    () => Application.DocumentManager.MdiActiveDocument?
                            .SendStringToExecute("UT_BOQ\n", true, false, true));
            }

            return _mainVm;
        }

        /// <summary>Ensures the shared ViewModel exists AND the Akıllı Montaj window is
        /// shown, returning the ViewModel. External callers (e.g. UT_PIPE_CATALOG
        /// live-extract) rely on this build-and-show contract.</summary>
        internal static SmartAssemblyMainVm EnsureMainVm()
        {
            EnsureVm();
            if (_window == null || !_window.IsLoaded)
            {
                _window = new SmartAssemblyWindow(_mainVm);
                // ShowModelessWindow integrates the WPF window into AutoCAD's
                // message pump so keyboard/mouse input reaches it correctly.
                Application.ShowModelessWindow(_window);
            }
            return _mainVm;
        }

        /// <summary>Opens (or re-activates) the Akıllı Montaj window without changing the active tab.</summary>
        internal static void ShowWindow()
        {
            EnsureMainVm();
            _window.Activate();
        }

        /// <summary>Opens (or re-activates) the Akıllı Montaj window and jumps to the given tab
        /// (see the TAB_* constants on <see cref="SmartAssemblyMainVm"/>).</summary>
        internal static void ShowWindowOnTab(int tabIndex)
        {
            var vm = EnsureMainVm();
            vm.SelectedTabIndex = tabIndex;
            _window.Activate();
        }

        /// <summary>Opens (or re-activates) the Proje Ayarları window (Proje Kurulumu +
        /// Tür Eşleştirme), sharing the same ViewModel as the Akıllı Montaj window.</summary>
        internal static void ShowProjectSettings()
        {
            var vm = EnsureVm();
            if (_projSettingsWindow == null || !_projSettingsWindow.IsLoaded)
            {
                _projSettingsWindow = new ProjectSettingsWindow(vm);
                Application.ShowModelessWindow(_projSettingsWindow);
            }
            else
            {
                _projSettingsWindow.Activate();
            }
        }

        // =====================================================================
        // UT_SMART_ASSEMBLY
        // =====================================================================

        [CommandMethod("UT_SMART_ASSEMBLY", CommandFlags.Modal)]
        public void OpenSmartAssembly()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                ShowWindow();
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nUT_SMART_ASSEMBLY hatası: " + ex.Message);
            }
        }

        // =====================================================================
        // UT_PROJE_AYARLARI
        // =====================================================================

        [CommandMethod("UT_PROJE_AYARLARI", CommandFlags.Modal)]
        public void OpenProjectSettings()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                ShowProjectSettings();
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nUT_PROJE_AYARLARI hatası: " + ex.Message);
            }
        }
    }
}
