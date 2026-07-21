using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Row ViewModel for a single <see cref="DepthTierRule"/> inside a <see cref="PipeRangeRuleVm"/>.</summary>
    public class DepthTierVm : ViewModelBase
    {
        private readonly DepthTierRule      _model;
        private readonly Action<DepthTierVm> _resolveBaseName;

        public DepthTierVm(DepthTierRule model, Action<DepthTierVm> resolveBaseName = null)
        {
            _model           = model ?? throw new ArgumentNullException(nameof(model));
            _resolveBaseName = resolveBaseName;
        }

        public DepthTierRule Model => _model;

        public double MinDepthM
        {
            get => _model.MinDepthM;
            set
            {
                _model.MinDepthM = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DepthRangeDisplay));
                OnPropertyChanged(nameof(IsDepthRangeValid));
            }
        }

        public double MaxDepthM
        {
            get => _model.MaxDepthM;
            set
            {
                _model.MaxDepthM = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DepthRangeDisplay));
                OnPropertyChanged(nameof(IsDepthRangeValid));
            }
        }

        public bool IsCastInSitu
        {
            get => _model.IsCastInSitu;
            set
            {
                _model.IsCastInSitu = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsBaseSelectable));
                OnPropertyChanged(nameof(ResultDisplay));
            }
        }

        /// <summary>Drives ComboBox IsEnabled — false when cast-in-situ.</summary>
        public bool IsBaseSelectable => !_model.IsCastInSitu;

        public Guid SelectedBaseId
        {
            get => _model.SelectedBaseId;
            set
            {
                _model.SelectedBaseId = value;
                OnPropertyChanged();
                _resolveBaseName?.Invoke(this);
                OnPropertyChanged(nameof(ResultDisplay));
            }
        }

        public string Notes
        {
            get => _model.Notes;
            set { _model.Notes = value; OnPropertyChanged(); }
        }

        // ── Resolved display name (set by owning VM or via callback) ──────────

        private string _baseName = "";
        public string BaseName
        {
            get => _baseName;
            set { Set(ref _baseName, value); OnPropertyChanged(nameof(ResultDisplay)); }
        }

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>False when MinDepth > MaxDepth (0 = unlimited for MaxDepth).</summary>
        public bool IsDepthRangeValid => MaxDepthM <= 0 || MinDepthM <= MaxDepthM;

        // ── Computed display ──────────────────────────────────────────────────

        public string DepthRangeDisplay => string.Format("{0:0.0} – {1} m", MinDepthM,
            MaxDepthM <= 0 ? "∞" : MaxDepthM.ToString("0.0"));

        public string ResultDisplay => IsCastInSitu
            ? "Yerinde Döküm"
            : (string.IsNullOrEmpty(_baseName)
                ? (SelectedBaseId == Guid.Empty ? "(seçilmedi)" : SelectedBaseId.ToString("D").Substring(0, 8) + "…")
                : _baseName);
    }
}
