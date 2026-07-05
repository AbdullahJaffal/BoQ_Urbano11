using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.SoilCatalog.Services;
using UrbanoMetraj.BoQ.DolguCatalog.Models;
using UrbanoMetraj.BoQ.DolguCatalog.Services;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.Models;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;  // ViewModelBase, RelayCommand

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.ViewModels
{
    // =========================================================================
    // TrenchLayerVm – row VM for one TrenchLayer (bedding or backfill)
    // =========================================================================

    public sealed class TrenchLayerVm : ViewModelBase
    {
        private readonly TrenchLayer _model;

        public TrenchLayerVm(TrenchLayer model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public TrenchLayer Model => _model;

        /// <summary>Shared dolgu materials list — used as ComboBox source in XAML.</summary>
        public static ObservableCollection<DolguMalzemesi> AvailableMaterials
            => DolguCatalogStore.Items;

        public string LayerName
        {
            get => _model.LayerName;
            set { _model.LayerName = value; OnPropertyChanged(); }
        }

        public string MaterialType
        {
            get => _model.MaterialType;
            set { _model.MaterialType = value; OnPropertyChanged(); }
        }

        public double ThicknessM
        {
            get => _model.ThicknessM;
            set { _model.ThicknessM = value; OnPropertyChanged(); }
        }

        public bool IsFillToSurface
        {
            get => _model.IsFillToSurface;
            set
            {
                _model.IsFillToSurface = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsThicknessEnabled));
            }
        }

        /// <summary>Drives IsEnabled on the ThicknessM cell — irrelevant when filling to surface.</summary>
        public bool IsThicknessEnabled => !_model.IsFillToSurface;
    }

    // =========================================================================
    // GomleklemeLayerVm – row VM for one Gömlekleme (encasement) layer
    // =========================================================================

    public sealed class GomleklemeLayerVm : ViewModelBase
    {
        private readonly GomleklemeLayer _model;

        public GomleklemeLayerVm(GomleklemeLayer model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public GomleklemeLayer Model => _model;

        /// <summary>Static list used as the Position ComboBox source in XAML.</summary>
        public static string[] Positions { get; } = { "boru etrafı", "boru üstü" };

        /// <summary>Shared dolgu materials list — used as ComboBox source in XAML.</summary>
        public static ObservableCollection<DolguMalzemesi> AvailableMaterials
            => DolguCatalogStore.Items;

        public string LayerName
        {
            get => _model.LayerName;
            set { _model.LayerName = value; OnPropertyChanged(); }
        }

        public string MaterialType
        {
            get => _model.MaterialType;
            set { _model.MaterialType = value; OnPropertyChanged(); }
        }

        public string Position
        {
            get => _model.Position;
            set
            {
                _model.Position = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsUpToPipeTopEnabled));
                if (_model.Position != "boru etrafı" && _model.IsUpToPipeTop)
                {
                    _model.IsUpToPipeTop = false;
                    OnPropertyChanged(nameof(IsUpToPipeTop));
                }
            }
        }

        public double ThicknessM
        {
            get => _model.ThicknessM;
            set { _model.ThicknessM = value; OnPropertyChanged(); }
        }

        public bool IsUpToPipeTop
        {
            get => _model.IsUpToPipeTop;
            set
            {
                _model.IsUpToPipeTop = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsThicknessEnabled));
            }
        }

        /// <summary>CheckBox only active when Position == "boru etrafı".</summary>
        public bool IsUpToPipeTopEnabled => _model.Position == "boru etrafı";

        /// <summary>Kalınlık cell is locked when IsUpToPipeTop is true.</summary>
        public bool IsThicknessEnabled => !_model.IsUpToPipeTop;
    }

    // =========================================================================
    // PipeTrenchDepthTierVm – row VM for one depth tier
    // =========================================================================

    public sealed class PipeTrenchDepthTierVm : ViewModelBase
    {
        private readonly PipeTrenchDepthTier _model;
        private TrenchLayerVm     _selectedBeddingLayer;
        private TrenchLayerVm     _selectedBackfillLayer;
        private GomleklemeLayerVm _selectedGomleklemeLayer;

        public PipeTrenchDepthTierVm(PipeTrenchDepthTier model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            BeddingLayers = new ObservableCollection<TrenchLayerVm>();
            foreach (var l in model.BeddingLayers)
                BeddingLayers.Add(new TrenchLayerVm(l));

            BackfillLayers = new ObservableCollection<TrenchLayerVm>();
            foreach (var l in model.BackfillLayers)
            {
                var vm = new TrenchLayerVm(l);
                vm.PropertyChanged += OnBackfillLayerPropertyChanged;
                BackfillLayers.Add(vm);
            }

            GomleklemeLayers = new ObservableCollection<GomleklemeLayerVm>();
            foreach (var l in model.GomleklemeLayers)
                GomleklemeLayers.Add(new GomleklemeLayerVm(l));

            AddBeddingLayerCommand    = new RelayCommand(OnAddBeddingLayer);
            DeleteBeddingLayerCommand = new RelayCommand(OnDeleteBeddingLayer,
                                           _ => _selectedBeddingLayer != null);

            AddBackfillLayerCommand    = new RelayCommand(OnAddBackfillLayer);
            DeleteBackfillLayerCommand = new RelayCommand(OnDeleteBackfillLayer,
                                           _ => _selectedBackfillLayer != null);

            AddGomleklemeLayerCommand    = new RelayCommand(OnAddGomleklemeLayer);
            DeleteGomleklemeLayerCommand = new RelayCommand(OnDeleteGomleklemeLayer,
                                              _ => _selectedGomleklemeLayer != null);
        }

        public PipeTrenchDepthTier Model => _model;

        // ── First-tier lock ───────────────────────────────────────────────────

        private bool _isFirstTier;

        /// <summary>
        /// Set by PipeTrenchRuleVm after any tier list change.
        /// When true, MinDepthM is forced to 0.000 and the cell is read-only.
        /// </summary>
        internal bool IsFirstTier
        {
            get => _isFirstTier;
            set
            {
                if (_isFirstTier == value) return;
                _isFirstTier = value;
                if (_isFirstTier)
                {
                    _model.MinDepthM = 0;
                    OnPropertyChanged(nameof(MinDepthM));
                    OnPropertyChanged(nameof(DepthRangeDisplay));
                }
                OnPropertyChanged(nameof(IsMinDepthEditable));
            }
        }

        /// <summary>False for the first tier — MinDepthM is always 0 and not editable.</summary>
        public bool IsMinDepthEditable => !_isFirstTier;

        // ── Depth range ───────────────────────────────────────────────────────

        public double MinDepthM
        {
            get => _model.MinDepthM;
            set
            {
                if (_isFirstTier) return; // Always locked at 0.000 for the first tier
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

        /// <summary>False when MinDepth > MaxDepth (MaxDepth 0 = unlimited, always valid).</summary>
        public bool IsDepthRangeValid => _model.MaxDepthM <= 0 || _model.MinDepthM <= _model.MaxDepthM;

        public string DepthRangeDisplay => string.Format("{0:0.000} – {1} m",
            _model.MinDepthM,
            _model.MaxDepthM <= 0 ? "∞" : _model.MaxDepthM.ToString("0.000"));

        // ── Excavation parameters ─────────────────────────────────────────────

        public double TrenchWidthClearanceM
        {
            get => _model.TrenchWidthClearanceM;
            set { _model.TrenchWidthClearanceM = value; OnPropertyChanged(); }
        }

        public double SlopeRatio
        {
            get => _model.SlopeRatio;
            set { _model.SlopeRatio = value; OnPropertyChanged(); }
        }

        public bool RequiresShoring
        {
            get => _model.RequiresShoring;
            set { _model.RequiresShoring = value; OnPropertyChanged(); }
        }

        // ── Stepped excavation ────────────────────────────────────────────────

        public bool IsSteppedExcavation
        {
            get => _model.IsSteppedExcavation;
            set
            {
                _model.IsSteppedExcavation = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(AreStepFieldsEnabled));
            }
        }

        /// <summary>Drives IsEnabled on the MaxStepHeight / StepBermWidth controls.</summary>
        public bool AreStepFieldsEnabled => _model.IsSteppedExcavation;

        public double MaxStepHeightM
        {
            get => _model.MaxStepHeightM;
            set { _model.MaxStepHeightM = value; OnPropertyChanged(); }
        }

        public double StepBermWidthM
        {
            get => _model.StepBermWidthM;
            set { _model.StepBermWidthM = value; OnPropertyChanged(); }
        }

        // ── Bedding layers (below / around the pipe) ──────────────────────────

        public ObservableCollection<TrenchLayerVm> BeddingLayers { get; }

        public TrenchLayerVm SelectedBeddingLayer
        {
            get => _selectedBeddingLayer;
            set { Set(ref _selectedBeddingLayer, value); }
        }

        public ICommand AddBeddingLayerCommand    { get; }
        public ICommand DeleteBeddingLayerCommand { get; }

        private void OnAddBeddingLayer(object _)
        {
            var layer = new TrenchLayer { LayerName = "Yataklama Kumu", MaterialType = "Kum", ThicknessM = 0.1 };
            _model.BeddingLayers.Add(layer);
            var vm = new TrenchLayerVm(layer);
            BeddingLayers.Add(vm);
            SelectedBeddingLayer = vm;
        }

        private void OnDeleteBeddingLayer(object _)
        {
            if (_selectedBeddingLayer == null) return;
            _model.BeddingLayers.Remove(_selectedBeddingLayer.Model);
            BeddingLayers.Remove(_selectedBeddingLayer);
            SelectedBeddingLayer = null;
        }

        // ── Backfill layers (above the pipe to surface) ───────────────────────

        public ObservableCollection<TrenchLayerVm> BackfillLayers { get; }

        public TrenchLayerVm SelectedBackfillLayer
        {
            get => _selectedBackfillLayer;
            set { Set(ref _selectedBackfillLayer, value); }
        }

        public ICommand AddBackfillLayerCommand    { get; }
        public ICommand DeleteBackfillLayerCommand { get; }

        private void OnAddBackfillLayer(object _)
        {
            var layer = new TrenchLayer { LayerName = "Geri Dolgu", MaterialType = "Seçme Malzeme", ThicknessM = 0.3 };
            _model.BackfillLayers.Add(layer);
            var vm = new TrenchLayerVm(layer);
            vm.PropertyChanged += OnBackfillLayerPropertyChanged;
            BackfillLayers.Add(vm);
            SelectedBackfillLayer = vm;
        }

        /// <summary>
        /// Only one Geri Dolgu row may be "Yüzeye Doldur" at a time — its real
        /// thickness is resolved as whatever remains of the average excavation-driven
        /// backfill height once every other configured layer is accounted for
        /// (see BoQParserService's Geri Dolgu layer split), which is only
        /// well-defined for a single such row.
        /// </summary>
        private void OnBackfillLayerPropertyChanged(object sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(TrenchLayerVm.IsFillToSurface)) return;
            if (!(sender is TrenchLayerVm changed) || !changed.IsFillToSurface) return;
            foreach (var other in BackfillLayers)
                if (!ReferenceEquals(other, changed) && other.IsFillToSurface)
                    other.IsFillToSurface = false;
        }

        private void OnDeleteBackfillLayer(object _)
        {
            if (_selectedBackfillLayer == null) return;
            _selectedBackfillLayer.PropertyChanged -= OnBackfillLayerPropertyChanged;
            _model.BackfillLayers.Remove(_selectedBackfillLayer.Model);
            BackfillLayers.Remove(_selectedBackfillLayer);
            SelectedBackfillLayer = null;
        }

        // ── Gömlekleme layers (around / above the pipe) ───────────────────────

        public ObservableCollection<GomleklemeLayerVm> GomleklemeLayers { get; }

        public GomleklemeLayerVm SelectedGomleklemeLayer
        {
            get => _selectedGomleklemeLayer;
            set { Set(ref _selectedGomleklemeLayer, value); }
        }

        public ICommand AddGomleklemeLayerCommand    { get; }
        public ICommand DeleteGomleklemeLayerCommand { get; }

        private void OnAddGomleklemeLayer(object _)
        {
            var layer = new GomleklemeLayer
            {
                LayerName    = "Gömlekleme",
                MaterialType = "Kum",
                Position     = "boru etrafı",
                ThicknessM   = 0.1
            };
            _model.GomleklemeLayers.Add(layer);
            var vm = new GomleklemeLayerVm(layer);
            GomleklemeLayers.Add(vm);
            SelectedGomleklemeLayer = vm;
        }

        private void OnDeleteGomleklemeLayer(object _)
        {
            if (_selectedGomleklemeLayer == null) return;
            _model.GomleklemeLayers.Remove(_selectedGomleklemeLayer.Model);
            GomleklemeLayers.Remove(_selectedGomleklemeLayer);
            SelectedGomleklemeLayer = null;
        }
    }

    // =========================================================================
    // PipeTrenchRuleVm – one rule (pipe diameter range + its depth tiers)
    // =========================================================================

    public sealed class PipeTrenchRuleVm : ViewModelBase
    {
        private readonly PipeTrenchRule _model;
        private PipeTrenchDepthTierVm   _selectedTier;

        public PipeTrenchRuleVm(PipeTrenchRule model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            DepthTiers = new ObservableCollection<PipeTrenchDepthTierVm>();
            foreach (var t in model.DepthTiers)
                DepthTiers.Add(new PipeTrenchDepthTierVm(t));

            RefreshFirstTierFlags();

            AddTierCommand       = new RelayCommand(OnAddTier);
            DeleteTierCommand    = new RelayCommand(OnDeleteTier,    _ => _selectedTier != null);
            DuplicateTierCommand = new RelayCommand(OnDuplicateTier, _ => _selectedTier != null);

            BuildFamilyFilters();
            RebuildDiameterList();
            BuildSoilFilters();
        }

        public PipeTrenchRule Model => _model;

        // ── Family filter + available diameters ───────────────────────────────

        public ObservableCollection<FamilyFilterVm> FamilyFilters { get; }
            = new ObservableCollection<FamilyFilterVm>();

        private bool _suppressFamilyRebuild;
        private bool _isFamilyDropdownOpen;

        public bool IsFamilyDropdownOpen
        {
            get => _isFamilyDropdownOpen;
            set { Set(ref _isFamilyDropdownOpen, value); }
        }

        public string SelectedFamiliesDisplay
        {
            get
            {
                if (FamilyFilters.Count == 0) return "Tüm Aileler";
                var sel = FamilyFilters.Where(f => f.IsSelected).ToList();
                if (sel.Count == FamilyFilters.Count) return "Tüm Aileler";
                if (sel.Count == 0) return "(Seçim yok)";
                return string.Join(", ", sel.Select(f => f.FamilyName));
            }
        }

        public bool? AllFamiliesSelected
        {
            get
            {
                if (FamilyFilters.Count == 0) return true;
                int n = FamilyFilters.Count(f => f.IsSelected);
                if (n == FamilyFilters.Count) return true;
                if (n == 0) return false;
                return null;
            }
            set
            {
                bool target = value ?? true;
                _suppressFamilyRebuild = true;
                foreach (var f in FamilyFilters)
                    f.IsSelected = target;
                _suppressFamilyRebuild = false;
                _model.SelectedFamilyNames.Clear();
                // all selected → keep list empty (convention: empty = all)
                RebuildDiameterList();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFamiliesDisplay));
            }
        }

        public ObservableCollection<double> AvailableNominalDiameters { get; }
            = new ObservableCollection<double>();

        private void BuildFamilyFilters()
        {
            _suppressFamilyRebuild = true;
            FamilyFilters.Clear();
            bool useStored = _model.SelectedFamilyNames.Count > 0;
            foreach (var family in PipeCatalogStore.Current.Families)
            {
                bool sel = !useStored || _model.SelectedFamilyNames.Contains(family.FamilyName);
                var vm = new FamilyFilterVm(family.FamilyName) { OnChanged = OnFamilyFilterChanged };
                vm.IsSelected = sel;
                FamilyFilters.Add(vm);
            }
            _suppressFamilyRebuild = false;
            OnPropertyChanged(nameof(AllFamiliesSelected));
            OnPropertyChanged(nameof(SelectedFamiliesDisplay));
        }

        private void RebuildDiameterList()
        {
            var seen = new SortedSet<double>();
            var selNames = new HashSet<string>(
                FamilyFilters.Where(f => f.IsSelected).Select(f => f.FamilyName));
            bool useFilter = FamilyFilters.Count > 0 && selNames.Count < FamilyFilters.Count;
            foreach (var family in PipeCatalogStore.Current.Families)
            {
                if (useFilter && !selNames.Contains(family.FamilyName)) continue;
                foreach (var pipe in family.Pipes)
                    if (pipe.NominalDiameter > 0)
                        seen.Add(pipe.NominalDiameter);
            }
            AvailableNominalDiameters.Clear();
            foreach (var d in seen)
                AvailableNominalDiameters.Add(d);
        }

        private void OnFamilyFilterChanged()
        {
            if (_suppressFamilyRebuild) return;
            _model.SelectedFamilyNames.Clear();
            int selCount = FamilyFilters.Count(f => f.IsSelected);
            if (selCount < FamilyFilters.Count) // not all → store selected names
                foreach (var f in FamilyFilters.Where(f => f.IsSelected))
                    _model.SelectedFamilyNames.Add(f.FamilyName);
            // all selected → keep list empty (convention: empty = all)
            RebuildDiameterList();
            OnPropertyChanged(nameof(AllFamiliesSelected));
            OnPropertyChanged(nameof(SelectedFamiliesDisplay));
        }

        // ── Zemin-tipi filter ─────────────────────────────────────────────────

        public ObservableCollection<SoilFilterVm> SoilFilters { get; }
            = new ObservableCollection<SoilFilterVm>();

        private bool _suppressSoilRebuild;
        private bool _isSoilDropdownOpen;

        public bool IsSoilDropdownOpen
        {
            get => _isSoilDropdownOpen;
            set { Set(ref _isSoilDropdownOpen, value); }
        }

        public string SelectedSoilsDisplay
        {
            get
            {
                if (SoilFilters.Count == 0) return "Tüm Zeminler";
                var sel = SoilFilters.Where(f => f.IsSelected).ToList();
                if (sel.Count == SoilFilters.Count) return "Tüm Zeminler";
                if (sel.Count == 0) return "(Seçim yok)";
                return string.Join(", ", sel.Select(f => f.SoilName));
            }
        }

        public bool? AllSoilsSelected
        {
            get
            {
                if (SoilFilters.Count == 0) return true;
                int n = SoilFilters.Count(f => f.IsSelected);
                if (n == SoilFilters.Count) return true;
                if (n == 0) return false;
                return null;
            }
            set
            {
                bool target = value ?? true;
                _suppressSoilRebuild = true;
                foreach (var f in SoilFilters)
                    f.IsSelected = target;
                _suppressSoilRebuild = false;
                _model.SelectedSoilNames.Clear();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedSoilsDisplay));
            }
        }

        private void BuildSoilFilters()
        {
            _suppressSoilRebuild = true;
            SoilFilters.Clear();
            bool useStored = _model.SelectedSoilNames.Count > 0;
            foreach (var soil in SoilCatalogStore.Items)
            {
                if (string.IsNullOrEmpty(soil.SoilName)) continue;
                bool sel = !useStored || _model.SelectedSoilNames.Contains(soil.SoilName);
                var vm = new SoilFilterVm(soil.SoilName) { OnChanged = OnSoilFilterChanged };
                vm.IsSelected = sel;
                SoilFilters.Add(vm);
            }
            _suppressSoilRebuild = false;
            OnPropertyChanged(nameof(AllSoilsSelected));
            OnPropertyChanged(nameof(SelectedSoilsDisplay));
        }

        private void OnSoilFilterChanged()
        {
            if (_suppressSoilRebuild) return;
            _model.SelectedSoilNames.Clear();
            int selCount = SoilFilters.Count(f => f.IsSelected);
            if (selCount < SoilFilters.Count)
                foreach (var f in SoilFilters.Where(f => f.IsSelected))
                    _model.SelectedSoilNames.Add(f.SoilName);
            OnPropertyChanged(nameof(AllSoilsSelected));
            OnPropertyChanged(nameof(SelectedSoilsDisplay));
        }

        // ── Metadata ─────────────────────────────────────────────────────────

        public string RuleName
        {
            get => _model.RuleName;
            set { _model.RuleName = value; OnPropertyChanged(); }
        }

        // ── Diameter range ────────────────────────────────────────────────────

        public double MinPipeDiameterMm
        {
            get => _model.MinPipeDiameterMm;
            set
            {
                _model.MinPipeDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiameterRangeDisplay));
                OnPropertyChanged(nameof(IsDiameterRangeValid));
            }
        }

        public double MaxPipeDiameterMm
        {
            get => _model.MaxPipeDiameterMm;
            set
            {
                // 0 = unlimited (always valid); otherwise Max must be >= Min
                if (value > 0 && value < _model.MinPipeDiameterMm)
                {
                    MessageBox.Show(
                        $"Maks boru çapı (DN{value:0}), Min çapından (DN{_model.MinPipeDiameterMm:0}) küçük olamaz.",
                        "Geçersiz Değer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OnPropertyChanged(); // Revert ComboBox to current model value
                    return;
                }
                _model.MaxPipeDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiameterRangeDisplay));
                OnPropertyChanged(nameof(IsDiameterRangeValid));
            }
        }

        /// <summary>False when Min > Max (Max 0 = unlimited, always valid).</summary>
        public bool IsDiameterRangeValid =>
            _model.MaxPipeDiameterMm <= 0 || _model.MinPipeDiameterMm <= _model.MaxPipeDiameterMm;

        public string DiameterRangeDisplay => string.Format("Ø{0:0} – {1} mm",
            _model.MinPipeDiameterMm,
            _model.MaxPipeDiameterMm <= 0 ? "∞" : ("Ø" + _model.MaxPipeDiameterMm.ToString("0")));

        // ── Depth tiers ───────────────────────────────────────────────────────

        public ObservableCollection<PipeTrenchDepthTierVm> DepthTiers { get; }

        public PipeTrenchDepthTierVm SelectedTier
        {
            get => _selectedTier;
            set
            {
                Set(ref _selectedTier, value);
                OnPropertyChanged(nameof(IsTierSelected));
            }
        }

        public bool IsTierSelected => _selectedTier != null;

        public ICommand AddTierCommand       { get; }
        public ICommand DeleteTierCommand    { get; }
        public ICommand DuplicateTierCommand { get; }

        // ── Helpers ───────────────────────────────────────────────────────────

        /// <summary>Marks tier[0] as the first (locked-at-zero) tier; clears the flag on all others.</summary>
        private void RefreshFirstTierFlags()
        {
            for (int i = 0; i < DepthTiers.Count; i++)
                DepthTiers[i].IsFirstTier = (i == 0);
        }

        private static double NextMinDepth(ObservableCollection<PipeTrenchDepthTierVm> tiers)
            => tiers.Count > 0
                ? Math.Round(tiers[tiers.Count - 1].MaxDepthM + 0.001, 3)
                : 0.0;

        // ── Tier CRUD ─────────────────────────────────────────────────────────

        private void OnAddTier(object _)
        {
            var tier = new PipeTrenchDepthTier
            {
                MinDepthM             = NextMinDepth(DepthTiers),
                MaxDepthM             = 0,
                TrenchWidthClearanceM = 0.2,
                SlopeRatio            = 0,
                MaxStepHeightM        = 1.5,
                StepBermWidthM        = 0.5
            };
            _model.DepthTiers.Add(tier);
            var vm = new PipeTrenchDepthTierVm(tier);
            DepthTiers.Add(vm);
            RefreshFirstTierFlags();
            SelectedTier = vm;
        }

        private void OnDeleteTier(object _)
        {
            if (_selectedTier == null) return;
            _model.DepthTiers.Remove(_selectedTier.Model);
            DepthTiers.Remove(_selectedTier);
            SelectedTier = null;
            RefreshFirstTierFlags();
        }

        private void OnDuplicateTier(object _)
        {
            if (_selectedTier == null) return;
            var copy = PipeTrenchCloner.CloneTier(_selectedTier.Model);
            // MinDepthM of the copy always follows the current last tier + 1 mm
            copy.MinDepthM = NextMinDepth(DepthTiers);
            _model.DepthTiers.Add(copy);
            var vm = new PipeTrenchDepthTierVm(copy);
            DepthTiers.Add(vm);
            RefreshFirstTierFlags();
            SelectedTier = vm;
        }
    }

    // =========================================================================
    // PipeTrenchMainVm – top-level VM
    // Left panel : Rules list.
    // Right panel: SelectedRule detail — header fields + depth tier grid + layer grids.
    // =========================================================================

    public sealed class PipeTrenchMainVm : ViewModelBase
    {
        private readonly List<PipeTrenchRule> _backingRules;
        private PipeTrenchRuleVm              _selectedRule;

        public ObservableCollection<PipeTrenchRuleVm> Rules { get; }

        // ── Status bar ────────────────────────────────────────────────────────

        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Master selection ──────────────────────────────────────────────────

        public PipeTrenchRuleVm SelectedRule
        {
            get => _selectedRule;
            set
            {
                if (_selectedRule != null)
                {
                    _selectedRule.IsFamilyDropdownOpen = false;
                    _selectedRule.IsSoilDropdownOpen   = false;
                }
                Set(ref _selectedRule, value);
                OnPropertyChanged(nameof(IsRuleSelected));
            }
        }

        public bool IsRuleSelected => _selectedRule != null;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand AddRuleCommand       { get; }
        public ICommand DeleteRuleCommand    { get; }
        public ICommand DuplicateRuleCommand { get; }
        public ICommand SaveCommand          { get; }
        public ICommand ExportCommand        { get; }
        public ICommand ImportCommand        { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public PipeTrenchMainVm(IEnumerable<PipeTrenchRule> existingRules = null)
        {
            _backingRules = new List<PipeTrenchRule>(existingRules ?? new PipeTrenchRule[0]);

            // Auto-load from internal path if no rules were injected
            if (_backingRules.Count == 0)
            {
                try
                {
                    _backingRules.AddRange(PipeTrenchCatalogXmlManager.LoadInternal());
                }
                catch (Exception ex)
                {
                    _statusText = "Otomatik yükleme hatası: " + ex.Message;
                }
            }

            Rules = new ObservableCollection<PipeTrenchRuleVm>();
            foreach (var r in _backingRules)
                Rules.Add(new PipeTrenchRuleVm(r));

            if (Rules.Count > 0 && _statusText == "Hazır.")
                StatusText = $"{Rules.Count} kural yüklendi.";

            AddRuleCommand       = new RelayCommand(OnAddRule);
            DeleteRuleCommand    = new RelayCommand(OnDeleteRule,    _ => IsRuleSelected);
            DuplicateRuleCommand = new RelayCommand(OnDuplicateRule, _ => IsRuleSelected);
            SaveCommand          = new RelayCommand(OnSave,   _ => _backingRules.Count > 0);
            ExportCommand        = new RelayCommand(OnExport, _ => _backingRules.Count > 0);
            ImportCommand        = new RelayCommand(OnImport);
        }

        // ── Validation ────────────────────────────────────────────────────────

        /// <summary>
        /// Returns true if the current rule is complete (has at least one tier with MaxDepthM == 0).
        /// Returns true unconditionally when no rule is selected or the rule has no tiers yet.
        /// Shows a blocking MessageBox and returns false otherwise.
        /// </summary>
        private bool ValidateCurrentRule(string actionName)
        {
            if (_selectedRule == null || _selectedRule.DepthTiers.Count == 0)
                return true;

            foreach (var tier in _selectedRule.DepthTiers)
                if (tier.MaxDepthM <= 0) return true;

            MessageBox.Show(
                $"'{_selectedRule.RuleName}' kuralı tamamlanmamış:\n" +
                "Derinlik kademelerinden en az birinin 'Maks (m)' değeri\n" +
                "0,000 (sınırsız) olmalıdır.\n\n" +
                $"'{actionName}' işlemi iptal edildi.",
                "Kural Doğrulama",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return false;
        }

        // ── Rule CRUD ─────────────────────────────────────────────────────────

        private void OnAddRule(object _)
        {
            if (!ValidateCurrentRule("Kural Ekle")) return;
            var rule = new PipeTrenchRule { RuleName = "Yeni Kural" };
            _backingRules.Add(rule);
            var vm = new PipeTrenchRuleVm(rule);
            Rules.Add(vm);
            SelectedRule = vm;
            StatusText = "Yeni hendek kuralı eklendi.";
        }

        private void OnDeleteRule(object _)
        {
            if (_selectedRule == null) return;
            if (!ValidateCurrentRule("Sil")) return;
            _backingRules.Remove(_selectedRule.Model);
            Rules.Remove(_selectedRule);
            SelectedRule = null;
            StatusText = "Hendek kuralı silindi.";
        }

        private void OnDuplicateRule(object _)
        {
            if (_selectedRule == null) return;
            if (!ValidateCurrentRule("Kopyala")) return;
            var copy = PipeTrenchCloner.CloneRule(_selectedRule.Model);
            _backingRules.Add(copy);
            var vm = new PipeTrenchRuleVm(copy);
            Rules.Add(vm);
            SelectedRule = vm;
            StatusText = "Hendek kuralı kopyalandı.";
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private void OnSave(object _)
        {
            if (!ValidateCurrentRule("Kaydet")) return;
            try
            {
                PipeTrenchCatalogXmlManager.SaveInternal(_backingRules);
                // _backingRules is a private copy (see constructor) — without this,
                // PipeTrenchCatalogStore.Current keeps returning whatever was cached
                // at its first access this session, so BoQParserService's trench
                // geometry would silently use stale rules until AutoCAD restarts.
                PipeTrenchCatalogStore.Current = _backingRules;
                StatusText = $"Kaydedildi → {PipeTrenchCatalogXmlManager.InternalPath}";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            if (!ValidateCurrentRule("Dışa Aktar")) return;
            var dlg = new SaveFileDialog
            {
                Filter   = "Boru Hendek Kataloğu XML (*.xml)|*.xml",
                Title    = "Hendek Kurallarını Dışa Aktar",
                FileName = "PipeTrenchCatalog.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                PipeTrenchCatalogXmlManager.Export(_backingRules, dlg.FileName);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            if (!ValidateCurrentRule("İçe Aktar")) return;
            var dlg = new OpenFileDialog
            {
                Filter = "Boru Hendek Kataloğu XML (*.xml)|*.xml",
                Title  = "Hendek Kurallarını İçe Aktar"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var loaded = PipeTrenchCatalogXmlManager.Import(dlg.FileName);
                if (loaded.Count == 0) { StatusText = "Dosyada kayıt bulunamadı."; return; }

                var ans = MessageBox.Show(
                    $"{loaded.Count} kural bulundu.\nMevcut liste silinsin mi?",
                    "İçe Aktar", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ans == MessageBoxResult.Yes)
                {
                    _backingRules.Clear();
                    Rules.Clear();
                    SelectedRule = null;
                }

                foreach (var r in loaded)
                {
                    _backingRules.Add(r);
                    Rules.Add(new PipeTrenchRuleVm(r));
                }
                StatusText = $"İçe aktarıldı: {loaded.Count} kural.";
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static void ShowError(string context, Exception ex)
            => MessageBox.Show(context + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);

        // ── Persistence bridge ────────────────────────────────────────────────

        /// <summary>Returns the backing model list for persistence (XML, NOD, etc.).</summary>
        public IReadOnlyList<PipeTrenchRule> GetRules() => _backingRules;
    }

    // =========================================================================
    // FamilyFilterVm – one checkbox item in the pipe-family filter bar
    // =========================================================================

    public sealed class FamilyFilterVm : ViewModelBase
    {
        private bool _isSelected = true;

        public string FamilyName { get; }

        internal Action OnChanged { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                OnChanged?.Invoke();
            }
        }

        public FamilyFilterVm(string name) => FamilyName = name;
    }

    // =========================================================================
    // SoilFilterVm – one checkbox item in the zemin-tipi filter dropdown
    // =========================================================================

    public sealed class SoilFilterVm : ViewModelBase
    {
        private bool _isSelected = true;

        public string SoilName { get; }

        internal Action OnChanged { get; set; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value) return;
                _isSelected = value;
                OnPropertyChanged();
                OnChanged?.Invoke();
            }
        }

        public SoilFilterVm(string name) => SoilName = name;
    }

    // =========================================================================
    // PipeTrenchCloner – deep-copy helpers shared by rule and tier VMs
    // =========================================================================

    internal static class PipeTrenchCloner
    {
        internal static PipeTrenchRule CloneRule(PipeTrenchRule src)
        {
            var r = new PipeTrenchRule
            {
                RuleName          = src.RuleName + " (Kopya)",
                MinPipeDiameterMm = src.MinPipeDiameterMm,
                MaxPipeDiameterMm = src.MaxPipeDiameterMm
            };
            foreach (var name in src.SelectedFamilyNames)
                r.SelectedFamilyNames.Add(name);
            foreach (var name in src.SelectedSoilNames)
                r.SelectedSoilNames.Add(name);
            foreach (var t in src.DepthTiers)
                r.DepthTiers.Add(CloneTier(t));
            return r;
        }

        internal static PipeTrenchDepthTier CloneTier(PipeTrenchDepthTier src)
        {
            var t = new PipeTrenchDepthTier
            {
                MinDepthM             = src.MinDepthM,
                MaxDepthM             = src.MaxDepthM,
                TrenchWidthClearanceM = src.TrenchWidthClearanceM,
                SlopeRatio            = src.SlopeRatio,
                RequiresShoring       = src.RequiresShoring,
                IsSteppedExcavation   = src.IsSteppedExcavation,
                MaxStepHeightM        = src.MaxStepHeightM,
                StepBermWidthM        = src.StepBermWidthM
            };
            foreach (var l in src.BeddingLayers)
                t.BeddingLayers.Add(CloneLayer(l));
            foreach (var l in src.BackfillLayers)
                t.BackfillLayers.Add(CloneLayer(l));
            foreach (var l in src.GomleklemeLayers)
                t.GomleklemeLayers.Add(CloneGomleklemeLayer(l));
            return t;
        }

        internal static TrenchLayer CloneLayer(TrenchLayer src)
            => new TrenchLayer
            {
                LayerName       = src.LayerName,
                MaterialType    = src.MaterialType,
                ThicknessM      = src.ThicknessM,
                IsFillToSurface = src.IsFillToSurface
            };

        internal static GomleklemeLayer CloneGomleklemeLayer(GomleklemeLayer src)
            => new GomleklemeLayer
            {
                LayerName     = src.LayerName,
                MaterialType  = src.MaterialType,
                Position      = src.Position,
                ThicknessM    = src.ThicknessM,
                IsUpToPipeTop = src.IsUpToPipeTop
            };
    }
}
