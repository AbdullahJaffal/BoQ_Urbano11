using UrbanoMetraj.BoQ.SoilCatalog.UI.ViewModels;

namespace UrbanoMetraj.BoQ.SoilCatalog.UI.Views
{
    public partial class SoilCatalogWindow
    {
        public SoilCatalogWindow(SoilCatalogVm vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
