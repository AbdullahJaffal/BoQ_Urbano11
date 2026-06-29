using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.DolguCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.DolguCatalog.UI.Views;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.DolguCatalog.DolguCatalogCommand))]

namespace UrbanoMetraj.BoQ.DolguCatalog
{
    public class DolguCatalogCommand
    {
        private static DolguCatalogWindow _window;
        private static DolguCatalogVm     _vm;

        [CommandMethod("DOLGU_CATALOG")]
        public void ShowDolguCatalog()
        {
            if (_window == null || !_window.IsLoaded)
            {
                _vm     = new DolguCatalogVm();
                _window = new DolguCatalogWindow(_vm);
            }
            AcApp.ShowModelessWindow(_window);
        }
    }
}
