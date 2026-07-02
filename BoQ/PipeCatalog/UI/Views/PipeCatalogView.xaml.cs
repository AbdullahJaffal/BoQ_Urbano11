using System.Windows;
using System.Windows.Controls;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels;

namespace UrbanoMetraj.BoQ.PipeCatalogs.UI.Views
{
    // DataContext is supplied by the hosting TabItem's binding (see SmartAssemblyWindow.xaml),
    // so this view only needs a parameterless constructor for XAML instantiation.
    public partial class PipeCatalogView : UserControl
    {
        private bool _classManagerWired;

        public PipeCatalogView()
        {
            InitializeComponent();
            DataContextChanged += OnDataContextChanged;
        }

        private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (_classManagerWired || !(e.NewValue is PipeCatalogMainVm vm)) return;
            _classManagerWired = true;

            vm.OpenClassManagerRequested += (s, ev) =>
            {
                var dlg = new SinifYoneticisiDialog(vm.CatalogClasses, Window.GetWindow(this));
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
