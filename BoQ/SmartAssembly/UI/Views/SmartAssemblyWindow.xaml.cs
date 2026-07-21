using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.Views
{
    /// <summary>
    /// Modeless WPF window for the Smart Manhole Assembly system.
    ///
    /// Opening (from an AutoCAD command):
    /// <code>
    ///   // Keep a static reference so the window survives garbage collection.
    ///   private static SmartAssemblyWindow _win;
    ///
    ///   [CommandMethod("UT_SMART_ASSEMBLY")]
    ///   public void OpenWindow()
    ///   {
    ///       if (_win == null || !_win.IsLoaded)
    ///       {
    ///           _win = new SmartAssemblyWindow();
    ///           // AutoCAD helper: routes WPF input correctly inside the AutoCAD process.
    ///           Bricscad.ApplicationServices.Application.ShowModelessWindow(_win);
    ///       }
    ///       else
    ///       {
    ///           _win.Activate();
    ///       }
    ///   }
    /// </code>
    /// </summary>
    public partial class SmartAssemblyWindow : Window
    {
        public SmartAssemblyWindow()
        {
            InitializeComponent();
            DataContext = new SmartAssemblyMainVm();
        }

        /// <summary>Opens or re-uses the window with a pre-loaded catalog ViewModel.</summary>
        public SmartAssemblyWindow(SmartAssemblyMainVm viewModel)
        {
            InitializeComponent();
            DataContext = viewModel;
        }

        protected override void OnClosed(System.EventArgs e)
        {
            base.OnClosed(e);
            DataContext = null;
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
