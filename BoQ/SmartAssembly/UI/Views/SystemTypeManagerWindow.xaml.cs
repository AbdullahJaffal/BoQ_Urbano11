using System.Collections.ObjectModel;
using System.Windows;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.Views
{
    public partial class SystemTypeManagerWindow : Window
    {
        private readonly SystemTypeManagerVm        _vm;
        private readonly ObservableCollection<SystemType> _target;

        public SystemTypeManagerWindow(ObservableCollection<SystemType> systemTypes)
        {
            InitializeComponent();
            _target     = systemTypes;
            _vm         = new SystemTypeManagerVm(systemTypes);
            DataContext = _vm;
        }

        private void OnSave(object sender, RoutedEventArgs e)
        {
            _vm.CommitTo(_target);
            DialogResult = true;
        }

        private void OnCancel(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
