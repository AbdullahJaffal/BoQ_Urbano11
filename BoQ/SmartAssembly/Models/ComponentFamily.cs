using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// A named group of manhole components sharing the same material.
    /// Mirrors the PipeFamily concept in the Pipe Catalog.
    /// </summary>
    public class ComponentFamily : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        private void Notify([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public Guid Id { get; set; } = Guid.NewGuid();

        private string _name = "";
        public string Name
        {
            get => _name;
            set { if (_name == value) return; _name = value; Notify(); }
        }

        private string _malzeme = "";
        public string Malzeme
        {
            get => _malzeme;
            set { if (_malzeme == value) return; _malzeme = value; Notify(); }
        }

        public ObservableCollection<ManholeComponent> Components { get; set; }
            = new ObservableCollection<ManholeComponent>();
    }
}
