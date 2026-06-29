using System.Windows;
using System.Windows.Controls;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels;

namespace UrbanoMetraj.BoQ.PipeCatalogs.UI.Views
{
    public partial class PipeCatalogWindow : Window
    {
        private readonly PipeCatalogMainVm _vm;

        public PipeCatalogWindow(PipeCatalogMainVm vm)
        {
            InitializeComponent();
            _vm = vm;
            DataContext = vm;

            vm.OpenClassManagerRequested += (s, e) =>
            {
                var dlg = new SinifYoneticisiDialog(_vm.CatalogClasses, this);
                dlg.Show();
            };
        }

        // Normalize the edited pipe after the row is committed.
        private void PipesGrid_RowEditEnding(object sender, DataGridRowEditEndingEventArgs e)
        {
            if (e.EditAction != DataGridEditAction.Commit) return;
            Dispatcher.InvokeAsync(() =>
            {
                if (e.Row.Item is PipeDefinition pipe)
                    pipe.Normalize();
            }, System.Windows.Threading.DispatcherPriority.Background);
        }
    }
}
