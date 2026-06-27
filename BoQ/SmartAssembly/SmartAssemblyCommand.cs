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
        private static SmartAssemblyWindow    _window;
        private static SmartAssemblyMainVm    _mainVm;

        /// <summary>Returns the live ViewModel so PIPE_CATALOG_LIVE_EXTRACT can
        /// push catalog updates without re-opening the Smart Assembly window.</summary>
        internal static SmartAssemblyMainVm GetMainVm() =>
            (_window != null && _window.IsLoaded) ? _mainVm : null;

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
                // Re-activate existing window instead of opening a second copy.
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return;
                }

                // Build the shared catalog + load any project templates already in NOD.
                var catalog = new SmartAssemblyMasterCatalog();
                _mainVm = new SmartAssemblyMainVm(catalog, PipeCatalogStore.Current);

                var existingTemplates = ProjectTemplateNodManager.LoadAllTemplates(doc.Database);
                foreach (var t in existingTemplates)
                    _mainVm.ProjectSetupTab.Templates.Add(t);

                _window = new SmartAssemblyWindow(_mainVm);

                // ShowModelessWindow integrates the WPF window into AutoCAD's
                // message pump so keyboard/mouse input reaches it correctly.
                Application.ShowModelessWindow(_window);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nSMART_ASSEMBLY hatası: " + ex.Message);
            }
        }
    }
}
