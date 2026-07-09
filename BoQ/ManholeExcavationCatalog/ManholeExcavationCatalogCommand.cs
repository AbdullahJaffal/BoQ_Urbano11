using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.ManholeExcavationCatalog.ManholeExcavationCatalogCommand))]

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog
{
    /// <summary>
    /// Commands
    /// ─────────────────────────────────────────────────────────────
    ///  UT_MANHOLE_EXCAV_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                          "Baca Kazı Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class ManholeExcavationCatalogCommand
    {
        [CommandMethod("UT_MANHOLE_EXCAV_CATALOG")]
        public void OpenExcavationCatalog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_MANHOLE_EXCAV);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nUT_MANHOLE_EXCAV_CATALOG hatası: " + ex.Message);
            }
        }
    }
}
