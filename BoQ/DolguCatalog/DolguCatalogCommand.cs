using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.DolguCatalog.DolguCatalogCommand))]

namespace UrbanoMetraj.BoQ.DolguCatalog
{
    /// <summary>
    /// DOLGU_CATALOG   Opens the shared Akıllı Montaj window on the
    ///                 "Dolgu Kataloğu" tab (formerly a standalone window).
    /// </summary>
    public class DolguCatalogCommand
    {
        [CommandMethod("DOLGU_CATALOG")]
        public void ShowDolguCatalog()
        {
            SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_DOLGU_CATALOG);
        }
    }
}
