using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.SoilCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.SoilCatalog.UI.Views;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SoilCatalog.SoilCatalogCommand))]

namespace UrbanoMetraj.BoQ.SoilCatalog
{
    public class SoilCatalogCommand
    {
        private static SoilCatalogWindow _window;
        private static SoilCatalogVm     _vm;

        [CommandMethod("SOIL_CATALOG")]
        public void ShowSoilCatalog()
        {
            if (_window == null || !_window.IsLoaded)
            {
                _vm     = new SoilCatalogVm();
                _window = new SoilCatalogWindow(_vm);
            }
            AcApp.ShowModelessWindow(_window);
        }
    }
}
