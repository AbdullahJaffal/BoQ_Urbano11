using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Row ViewModel for a single <see cref="TemelAltiParca"/> in the sub-base DataGrid.</summary>
    public class TemelAltiParcaRowVm : ViewModelBase
    {
        private readonly TemelAltiParca _model;

        public TemelAltiParcaRowVm(TemelAltiParca model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));
        }

        public TemelAltiParca Model => _model;

        public string Ad
        {
            get => _model.Ad;
            set { _model.Ad = value; OnPropertyChanged(); }
        }

        public double Boy
        {
            get => _model.Boy;
            set { _model.Boy = value; OnPropertyChanged(); }
        }

        public double En
        {
            get => _model.En;
            set { _model.En = value; OnPropertyChanged(); }
        }

        public double Kalinlik
        {
            get => _model.Kalinlik;
            set { _model.Kalinlik = value; OnPropertyChanged(); }
        }

        public string Malzeme
        {
            get => _model.Malzeme;
            set { _model.Malzeme = value; OnPropertyChanged(); }
        }
    }
}
