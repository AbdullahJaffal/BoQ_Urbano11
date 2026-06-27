using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Row ViewModel for a single <see cref="SubPiece"/> inside a composite BottomElement.</summary>
    public class SubPieceRowVm : ViewModelBase
    {
        private readonly SubPiece _model;
        private readonly Action   _onHeightChanged;

        public SubPieceRowVm(SubPiece model, Action onHeightChanged = null)
        {
            _model           = model ?? throw new ArgumentNullException(nameof(model));
            _onHeightChanged = onHeightChanged;
        }

        public SubPiece Model => _model;

        public string Name
        {
            get => _model.Name;
            set { _model.Name = value; OnPropertyChanged(); }
        }

        public double HeightMm
        {
            get => _model.HeightMm;
            set { _model.HeightMm = value; OnPropertyChanged(); _onHeightChanged?.Invoke(); }
        }

        public string Description
        {
            get => _model.Description;
            set { _model.Description = value; OnPropertyChanged(); }
        }
    }
}
