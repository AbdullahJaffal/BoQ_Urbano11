using System.Windows;
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
    ///   [CommandMethod("SMART_ASSEMBLY")]
    ///   public void OpenWindow()
    ///   {
    ///       if (_win == null || !_win.IsLoaded)
    ///       {
    ///           _win = new SmartAssemblyWindow();
    ///           // AutoCAD helper: routes WPF input correctly inside the AutoCAD process.
    ///           Autodesk.AutoCAD.ApplicationServices.Application.ShowModelessWindow(_win);
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
    }
}
