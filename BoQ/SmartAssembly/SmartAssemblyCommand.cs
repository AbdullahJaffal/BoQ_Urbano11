using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;
using UrbanoMetraj.BoQ.SmartAssembly.UI.Views;

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
    ///  SMART_ASSEMBLY   Open the modeless Smart Assembly window.
    ///                   Re-activates the existing window if already open.
    /// </summary>
    public class SmartAssemblyCommand
    {
        // Static reference keeps the modeless window alive across command calls
        // and prevents the GC from collecting it when the command method returns.
        // This is the single shared "Akıllı Montaj" window — every catalog that used
        // to open its own standalone window (Kazı Kuralları, Boru/Hendek/Zemin/Dolgu
        // Katalogları) now lives here as an additional tab.
        private static SmartAssemblyWindow    _window;
        private static SmartAssemblyMainVm    _mainVm;

        /// <summary>Returns the live ViewModel so PIPE_CATALOG_LIVE_EXTRACT can
        /// push catalog updates without re-opening the Akıllı Montaj window.</summary>
        internal static SmartAssemblyMainVm GetMainVm() =>
            (_window != null && _window.IsLoaded) ? _mainVm : null;

        /// <summary>Ensures the shared window exists (building it if necessary) and returns its ViewModel.</summary>
        internal static SmartAssemblyMainVm EnsureMainVm()
        {
            if (_window != null && _window.IsLoaded) return _mainVm;

            // Build the shared catalog + load any project templates already in NOD.
            var catalog = new SmartAssemblyMasterCatalog();
            _mainVm = new SmartAssemblyMainVm(catalog, PipeCatalogStore.Current);

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc != null)
            {
                var existingTemplates = ProjectTemplateNodManager.LoadAllTemplates(doc.Database);
                foreach (var t in existingTemplates)
                    _mainVm.ProjectSetupTab.Templates.Add(t);
            }

            _window = new SmartAssemblyWindow(_mainVm);

            // ShowModelessWindow integrates the WPF window into AutoCAD's
            // message pump so keyboard/mouse input reaches it correctly.
            Application.ShowModelessWindow(_window);
            return _mainVm;
        }

        /// <summary>Opens (or re-activates) the shared window without changing the active tab.</summary>
        internal static void ShowWindow()
        {
            EnsureMainVm();
            _window.Activate();
        }

        /// <summary>Opens (or re-activates) the shared window and jumps to the given tab
        /// (see the TAB_* constants on <see cref="SmartAssemblyMainVm"/>).</summary>
        internal static void ShowWindowOnTab(int tabIndex)
        {
            var vm = EnsureMainVm();
            vm.SelectedTabIndex = tabIndex;
            _window.Activate();
        }

        // =====================================================================
        // SMART_ASSEMBLY
        // =====================================================================

        [CommandMethod("SMART_ASSEMBLY", CommandFlags.Modal)]
        public void OpenSmartAssembly()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                ShowWindow();
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nSMART_ASSEMBLY hatası: " + ex.Message);
            }
        }
    }
}
