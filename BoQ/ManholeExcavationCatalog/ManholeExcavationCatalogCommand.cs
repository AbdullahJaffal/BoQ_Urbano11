using Bricscad.ApplicationServices;
using Teigha.Runtime;
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
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
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
