using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SoilCatalog.SoilCatalogCommand))]

namespace UrbanoMetraj.BoQ.SoilCatalog
{
    /// <summary>
    /// UT_SOIL_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                "Zemin Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class SoilCatalogCommand
    {
        [CommandMethod("UT_SOIL_CATALOG")]
        public void ShowSoilCatalog()
        {
            SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_SOIL_CATALOG);
        }
    }
}
