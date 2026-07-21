using Bricscad.ApplicationServices;
using Teigha.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.PipeTrenchCatalog.PipeTrenchCatalogCommand))]

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog
{
    /// <summary>
    /// Commands
    /// ─────────────────────────────────────────────────────────────
    ///  UT_PIPE_TRENCH_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                        "Hendek Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class PipeTrenchCatalogCommand
    {
        [CommandMethod("UT_PIPE_TRENCH_CATALOG")]
        public void OpenPipeTrenchCatalog()
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
                SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_PIPE_TRENCH);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nUT_PIPE_TRENCH_CATALOG hatası: " + ex.Message);
            }
        }
    }
}
