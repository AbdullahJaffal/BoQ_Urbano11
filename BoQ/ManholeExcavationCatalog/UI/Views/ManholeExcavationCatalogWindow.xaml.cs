using System.Windows;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.ViewModels;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.Views
{
    public partial class ManholeExcavationCatalogWindow : Window
    {
        public ManholeExcavationCatalogWindow(ManholeExcavationMainVm vm)
        {
            InitializeComponent();
            DataContext = vm;
        }
    }
}
