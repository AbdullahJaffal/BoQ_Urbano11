using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using SystemTypeModel = UrbanoMetraj.BoQ.SmartAssembly.Models.SystemType;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>
    /// ViewModel wrapping a <see cref="PipeRangeRule"/>.
    ///
    /// Cascading ComboBox chain (requires a <see cref="PipeCatalog"/> at construction):
    ///   1. <see cref="SelectedPipeFamily"/>  — populates <see cref="PipesInFamily"/>
    ///   2. <see cref="SelectedMinPipe"/>      — drives <see cref="MinPipeMm"/> on the model
    ///   3. <see cref="SelectedMaxPipe"/>      — drives <see cref="MaxPipeMm"/> on the model
    ///
    /// When no catalog is supplied the existing numeric text-edit paths still work
    /// unchanged (backward compatibility for existing serialized rules).
    /// </summary>
    public class PipeRangeRuleVm : ViewModelBase
    {
        private readonly PipeRangeRule _model;
        private readonly PipeCatalog   _catalog;
        private readonly ObservableCollection<SystemTypeModel> _systemTypes;

        // ── Catalog-cascade state ─────────────────────────────────────────────
        private PipeFamily     _selectedFamily;
        private PipeDefinition _selectedMinPipe;
        private PipeDefinition _selectedMaxPipe;
        private SystemTypeModel _selectedSystemType;

        private readonly ObservableCollection<PipeDefinition> _pipesInFamily
            = new ObservableCollection<PipeDefinition>();

        // ── Constructors ──────────────────────────────────────────────────────

        public PipeRangeRuleVm(PipeRangeRule model) : this(model, null, null) { }

        public PipeRangeRuleVm(PipeRangeRule model, PipeCatalog catalog)
            : this(model, catalog, null) { }

        public PipeRangeRuleVm(PipeRangeRule model, PipeCatalog catalog,
                               ObservableCollection<SystemTypeModel> systemTypes)
        {
            _model       = model ?? throw new ArgumentNullException(nameof(model));
            _catalog     = catalog;
            _systemTypes = systemTypes;

            // Restore system-type selection
            if (_systemTypes != null && model.SystemTypeId != Guid.Empty)
                _selectedSystemType = _systemTypes.FirstOrDefault(st => st.Id == model.SystemTypeId);

            // Restore catalog selections persisted in the model
            if (_catalog != null && model.SelectedPipeFamilyId != Guid.Empty)
            {
                _selectedFamily = _catalog.Families
                    .FirstOrDefault(f => f.Id == model.SelectedPipeFamilyId);
                RebuildPipesInFamily();

                if (model.MinPipeId != Guid.Empty)
                    _selectedMinPipe = _pipesInFamily
                        .FirstOrDefault(p => p.Id == model.MinPipeId);

                if (model.MaxPipeId != Guid.Empty)
                    _selectedMaxPipe = _pipesInFamily
                        .FirstOrDefault(p => p.Id == model.MaxPipeId);
            }

            DepthTiers.CollectionChanged += OnDepthTiersChanged;
        }

        public PipeRangeRule Model => _model;

        // ── Numeric fallback (read/write when no catalog is attached) ─────────

        public double MinPipeMm
        {
            get => _model.MinPipeMm;
            set
            {
                _model.MinPipeMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeRangeValid));
            }
        }

        public double MaxPipeMm
        {
            get => _model.MaxPipeMm;
            set
            {
                _model.MaxPipeMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeRangeValid));
            }
        }

        // ── System type ───────────────────────────────────────────────────────

        public SystemTypeModel SelectedSystemType
        {
            get => _selectedSystemType;
            set
            {
                _selectedSystemType  = value;
                _model.SystemTypeId  = value?.Id ?? Guid.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SystemTypeDisplay));
            }
        }

        public string SystemTypeDisplay => _selectedSystemType?.Name ?? "—";

        public void RefreshSystemType(ObservableCollection<SystemTypeModel> types)
        {
            _selectedSystemType = types.FirstOrDefault(st => st.Id == _model.SystemTypeId);
            OnPropertyChanged(nameof(SelectedSystemType));
            OnPropertyChanged(nameof(SystemTypeDisplay));
        }

        // ── Catalog cascade ───────────────────────────────────────────────────

        public bool HasCatalog => _catalog != null && _catalog.Families.Count > 0;

        /// <summary>All families in the shared catalog — ComboBox source for column 1.</summary>
        public ObservableCollection<PipeFamily> AvailableFamilies =>
            _catalog?.Families ?? _emptyFamilies;

        private static readonly ObservableCollection<PipeFamily> _emptyFamilies
            = new ObservableCollection<PipeFamily>();

        /// <summary>Pipes belonging to <see cref="SelectedPipeFamily"/> — ComboBox source for columns 2 &amp; 3.</summary>
        public ObservableCollection<PipeDefinition> PipesInFamily => _pipesInFamily;

        public PipeFamily SelectedPipeFamily
        {
            get => _selectedFamily;
            set
            {
                if (Set(ref _selectedFamily, value))
                {
                    _model.SelectedPipeFamilyId = value?.Id ?? Guid.Empty;
                    RebuildPipesInFamily();

                    // Clear pipe selections if they no longer belong to the new family
                    if (_selectedMinPipe != null && !_pipesInFamily.Contains(_selectedMinPipe))
                        SelectedMinPipe = null;
                    if (_selectedMaxPipe != null && !_pipesInFamily.Contains(_selectedMaxPipe))
                        SelectedMaxPipe = null;
                }
            }
        }

        public PipeDefinition SelectedMinPipe
        {
            get => _selectedMinPipe;
            set
            {
                _selectedMinPipe       = value;
                _model.MinPipeId       = value?.Id ?? Guid.Empty;
                if (value != null) _model.MinPipeMm = value.NominalDiameter;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MinPipeMm));
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeRangeValid));
            }
        }

        public PipeDefinition SelectedMaxPipe
        {
            get => _selectedMaxPipe;
            set
            {
                _selectedMaxPipe       = value;
                _model.MaxPipeId       = value?.Id ?? Guid.Empty;
                if (value != null) _model.MaxPipeMm = value.NominalDiameter;
                OnPropertyChanged();
                OnPropertyChanged(nameof(MaxPipeMm));
                OnPropertyChanged(nameof(PipeRangeDisplay));
                OnPropertyChanged(nameof(IsPipeRangeValid));
            }
        }

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>False when the pipe range is logically inverted (Min > Max).</summary>
        public bool IsPipeRangeValid
        {
            get
            {
                if (_selectedMinPipe != null && _selectedMaxPipe != null)
                    return _selectedMinPipe.NominalDiameter <= _selectedMaxPipe.NominalDiameter;

                return MaxPipeMm <= 0 || MinPipeMm <= MaxPipeMm;
            }
        }

        public string PipeRangeDisplay
        {
            get
            {
                if (_selectedMinPipe != null && _selectedMaxPipe != null)
                    return string.Format("{0} – {1}", _selectedMinPipe.Display, _selectedMaxPipe.Display);

                return string.Format("{0:0} – {1:0} mm", MinPipeMm, MaxPipeMm);
            }
        }

        // ── Depth tiers ───────────────────────────────────────────────────────

        public ObservableCollection<DepthTierVm> DepthTiers { get; }
            = new ObservableCollection<DepthTierVm>();

        public int TierCount => DepthTiers.Count;

        // ── Private helpers ───────────────────────────────────────────────────

        private void RebuildPipesInFamily()
        {
            _pipesInFamily.Clear();
            if (_selectedFamily != null)
                foreach (var p in _selectedFamily.Pipes)
                    _pipesInFamily.Add(p);
        }

        private void OnDepthTiersChanged(object sender, NotifyCollectionChangedEventArgs e)
            => OnPropertyChanged(nameof(TierCount));
    }
}
