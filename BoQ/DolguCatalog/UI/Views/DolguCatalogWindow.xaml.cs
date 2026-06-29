using UrbanoMetraj.BoQ.DolguCatalog.UI.ViewModels;

namespace UrbanoMetraj.BoQ.DolguCatalog.UI.Views
{
    public partial class DolguCatalogWindow
    {
        public DolguCatalogWindow(DolguCatalogVm vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
