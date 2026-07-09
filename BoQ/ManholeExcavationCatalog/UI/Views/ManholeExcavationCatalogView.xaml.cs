using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.Views
{
    // DataContext is supplied by the hosting TabItem's binding (see SmartAssemblyWindow.xaml),
    // so this view only needs a parameterless constructor for XAML instantiation.
    public partial class ManholeExcavationCatalogView : UserControl
    {
        public ManholeExcavationCatalogView()
        {
            InitializeComponent();
        }

        // Right-click doesn't select a DataGrid row by default; select it first so the
        // row-scoped context-menu commands (Çoğalt / Sil) act on the clicked row.
        private void Grid_SelectRowOnRightClick(object sender, MouseButtonEventArgs e)
        {
            var dep = e.OriginalSource as DependencyObject;
            while (dep != null && !(dep is DataGridRow))
                dep = VisualTreeHelper.GetParent(dep);
            if (dep is DataGridRow row)
                row.IsSelected = true;
        }
    }
}
