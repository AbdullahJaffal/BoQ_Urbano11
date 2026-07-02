using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.PipeTrenchCatalog.PipeTrenchCatalogCommand))]

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog
{
    /// <summary>
    /// Commands
    /// ─────────────────────────────────────────────────────────────
    ///  PIPE_TRENCH_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                        "Hendek Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class PipeTrenchCatalogCommand
    {
        [CommandMethod("PIPE_TRENCH_CATALOG")]
        public void OpenPipeTrenchCatalog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_PIPE_TRENCH);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nPIPE_TRENCH_CATALOG hatası: " + ex.Message);
            }
        }
    }
}
