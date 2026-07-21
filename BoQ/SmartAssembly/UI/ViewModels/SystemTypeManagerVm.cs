using System.Collections.ObjectModel;
using System.Windows.Input;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    public class SystemTypeManagerVm : ViewModelBase
    {
        public ObservableCollection<SystemType> SystemTypes { get; }
            = new ObservableCollection<SystemType>();

        private SystemType _selected;
        public SystemType SelectedItem
        {
            get => _selected;
            set { Set(ref _selected, value); OnPropertyChanged(nameof(IsItemSelected)); }
        }

        public bool IsItemSelected => _selected != null;

        public ICommand AddCommand    { get; }
        public ICommand DeleteCommand { get; }

        public SystemTypeManagerVm(ObservableCollection<SystemType> existing)
        {
            foreach (var st in existing)
                SystemTypes.Add(new SystemType { Id = st.Id, Name = st.Name });

            AddCommand = new RelayCommand(_ =>
            {
                var st = new SystemType { Name = "Yeni Sistem Tipi" };
                SystemTypes.Add(st);
                SelectedItem = st;
            });

            DeleteCommand = new RelayCommand(
                _ => { if (_selected != null) SystemTypes.Remove(_selected); },
                _ => IsItemSelected);
        }

        public void CommitTo(ObservableCollection<SystemType> target)
        {
            target.Clear();
            foreach (var st in SystemTypes)
                target.Add(st);
        }
    }
}
