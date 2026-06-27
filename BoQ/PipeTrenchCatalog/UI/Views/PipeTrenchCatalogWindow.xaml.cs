using System.Windows;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.ViewModels;

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.Views
{
    public partial class PipeTrenchCatalogWindow : Window
    {
        public PipeTrenchCatalogWindow(PipeTrenchMainVm vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
