using System.Windows;
using System.Windows.Controls;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels;

namespace UrbanoMetraj.BoQ.PipeCatalogs.UI.Views
{
    public partial class PipeCatalogWindow : Window
    {
        public PipeCatalogWindow(PipeCatalogMainVm vm)
        {
            InitializeComponent();
            DataContext = vm;
        }

        // Normalize the edited pipe after the row is committed.
        // Runs after the DataGrid has written all cell values back to the model,
        // so Normalize() sees the final values and can derive the missing dimension.
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
