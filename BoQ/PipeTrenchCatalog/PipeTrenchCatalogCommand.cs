using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.Views;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.PipeTrenchCatalog.PipeTrenchCatalogCommand))]

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog
{
    /// <summary>
    /// Commands
    /// ─────────────────────────────────────────────────────────────
    ///  PIPE_TRENCH_CATALOG   Open the Pipe Trench Rules Catalog window.
    ///                        Re-activates the existing window if already open.
    /// </summary>
    public class PipeTrenchCatalogCommand
    {
        private static PipeTrenchCatalogWindow _window;
        private static PipeTrenchMainVm        _vm;

        [CommandMethod("PIPE_TRENCH_CATALOG")]
        public void OpenPipeTrenchCatalog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                if (_window != null && _window.IsLoaded)
                {
                    _window.Activate();
                    return;
                }

                _vm     = new PipeTrenchMainVm();
                _window = new PipeTrenchCatalogWindow(_vm);
                Application.ShowModelessWindow(_window);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nPIPE_TRENCH_CATALOG hatası: " + ex.Message);
            }
        }
    }
}
