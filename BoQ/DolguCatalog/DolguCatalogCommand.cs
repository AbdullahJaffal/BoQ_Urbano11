using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.DolguCatalog.DolguCatalogCommand))]

namespace UrbanoMetraj.BoQ.DolguCatalog
{
    /// <summary>
    /// UT_DOLGU_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                 "Dolgu Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class DolguCatalogCommand
    {
        [CommandMethod("UT_DOLGU_CATALOG")]
        public void ShowDolguCatalog()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_DOLGU_CATALOG);
        }
    }
}
