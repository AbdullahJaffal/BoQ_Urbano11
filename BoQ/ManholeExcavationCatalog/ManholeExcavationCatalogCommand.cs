using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.Views;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.ManholeExcavationCatalog.ManholeExcavationCatalogCommand))]

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog
{
    /// <summary>
    /// Commands
    /// ─────────────────────────────────────────────────────────────
    ///  MANHOLE_EXCAV_CATALOG   Open the Manhole Excavation Rules Catalog window.
    ///                          Re-activates the existing window if already open.
    /// </summary>
    public class ManholeExcavationCatalogCommand
    {
        private static ManholeExcavationCatalogWindow _window;
        private static ManholeExcavationMainVm        _vm;

        [CommandMethod("MANHOLE_EXCAV_CATALOG")]
        public void OpenExcavationCatalog()
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

                _vm     = new ManholeExcavationMainVm();
                _window = new ManholeExcavationCatalogWindow(_vm);
                Application.ShowModelessWindow(_window);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nMANHOLE_EXCAV_CATALOG hatası: " + ex.Message);
            }
        }
    }
}
