using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UrbanoMetraj.BoQ.DolguCatalog.UI.Views
{
    // DataContext is supplied by the hosting TabItem's binding (see SmartAssemblyWindow.xaml),
    // so this view only needs a parameterless constructor for XAML instantiation.
    public partial class DolguCatalogView
    {
        public DolguCatalogView()
        {
            InitializeComponent();
        }
    }
}
