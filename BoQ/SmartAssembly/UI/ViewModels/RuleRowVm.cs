using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Flat DataGrid row ViewModel for an <see cref="AssemblyRule"/>.</summary>
    public class RuleRowVm : ViewModelBase
    {
        private readonly AssemblyRule   _rule;
        private readonly Action<RuleRowVm> _resolveBaseName;

        /// <param name="resolveBaseName">
        /// Optional callback — the owning tab VM passes its name-resolution logic so that
        /// <see cref="BaseComponentName"/> (and therefore <see cref="ResultDisplay"/>) refreshes
        /// automatically whenever the user picks a different base from the ComboBox.
        /// </param>
        public RuleRowVm(AssemblyRule rule, Action<RuleRowVm> resolveBaseName = null)
        {
            _rule            = rule ?? new AssemblyRule();
            _resolveBaseName = resolveBaseName;
        }

        public AssemblyRule Rule => _rule;

        public Guid   Id             { get => _rule.Id;                set { _rule.Id                = value; OnPropertyChanged(); } }
        public bool   IsLocalOverride { get => _rule.IsLocalOverride;  set { _rule.IsLocalOverride   = value; OnPropertyChanged(); } }
        public string Notes          { get => _rule.Notes;             set { _rule.Notes             = value; OnPropertyChanged(); } }

        public double MinPipeDiamMm
        {
            get => _rule.MinPipeDiameterMm;
            set
            {
                _rule.MinPipeDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeDiamRangeValid));
            }
        }

        public double MaxPipeDiamMm
        {
            get => _rule.MaxPipeDiameterMm;
            set
            {
                _rule.MaxPipeDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeDiamRangeValid));
            }
        }

        public double MinDepthM
        {
            get => _rule.MinDepthM;
            set { _rule.MinDepthM = value; OnPropertyChanged(); OnPropertyChanged(nameof(DepthRangeDisplay)); }
        }

        public double MaxDepthM
        {
            get => _rule.MaxDepthM;
            set { _rule.MaxDepthM = value; OnPropertyChanged(); OnPropertyChanged(nameof(DepthRangeDisplay)); }
        }

        public Guid SelectedBaseId
        {
            get => _rule.SelectedBaseId;
            set
            {
                _rule.SelectedBaseId = value;
                OnPropertyChanged();
                _resolveBaseName?.Invoke(this);   // keeps BaseComponentName + ResultDisplay in sync
                OnPropertyChanged(nameof(ResultDisplay));
            }
        }

        public bool IsCastInSitu
        {
            get => _rule.IsCastInSitu;
            set
            {
                _rule.IsCastInSitu = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ResultDisplay));
                OnPropertyChanged(nameof(IsBaseSelectable));
            }
        }

        /// <summary>True when the base ComboBox should be active (i.e. not cast-in-situ).</summary>
        public bool IsBaseSelectable => !_rule.IsCastInSitu;

        // ── Resolved base name (set by owning tab VM) ─────────────────────────
        private string _baseComponentName = "";
        public string BaseComponentName
        {
            get => _baseComponentName;
            set { Set(ref _baseComponentName, value); OnPropertyChanged(nameof(ResultDisplay)); }
        }

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>False when MinPipe > MaxPipe (0 = unlimited for MaxPipe).</summary>
        public bool IsPipeDiamRangeValid
            => MaxPipeDiamMm <= 0 || MinPipeDiamMm <= MaxPipeDiamMm;

        // ── Computed display ──────────────────────────────────────────────────
        public string PipeRangeDisplay  => string.Format("{0:0} – {1:0} mm", MinPipeDiamMm, MaxPipeDiamMm);
        public string DepthRangeDisplay => string.Format("{0:0.0} – {1} m",  MinDepthM,
            MaxDepthM <= 0 ? "∞" : MaxDepthM.ToString("0.0"));
        public string ResultDisplay     => IsCastInSitu
            ? "Yerinde Döküm"
            : (string.IsNullOrEmpty(_baseComponentName)
                ? SelectedBaseId.ToString("D").Substring(0, 8) + "…"
                : _baseComponentName);
    }
}
