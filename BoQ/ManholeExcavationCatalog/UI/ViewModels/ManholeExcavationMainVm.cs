using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Services;
using UrbanoMetraj.BoQ.SoilCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;  // ViewModelBase, RelayCommand

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.ViewModels
{
    // =========================================================================
    // SubBaseLayerVm – row VM for one SubBaseLayer
    // =========================================================================

    public sealed class SubBaseLayerVm : ViewModelBase
    {
        private readonly SubBaseLayer _model;

        public SubBaseLayerVm(SubBaseLayer model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public SubBaseLayer Model => _model;

        public string LayerName
        {
            get => _model.LayerName;
            set { _model.LayerName = value; OnPropertyChanged(); }
        }

        public double ThicknessMm
        {
            get => _model.ThicknessMm;
            set { _model.ThicknessMm = value; OnPropertyChanged(); }
        }
    }

    // =========================================================================
    // ManholeBackfillLayerVm – row VM for one ManholeBackfillLayer
    // =========================================================================

    public sealed class ManholeBackfillLayerVm : ViewModelBase
    {
        private readonly ManholeBackfillLayer _model;

        public ManholeBackfillLayerVm(ManholeBackfillLayer model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public ManholeBackfillLayer Model => _model;

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

        /// <summary>Drives IsEnabled on the ThicknessM control — irrelevant when filling to surface.</summary>
        public bool IsThicknessEnabled => !_model.IsFillToSurface;
    }

    // =========================================================================
    // ManholeExcavationDepthTierVm – row VM for one depth tier
    // =========================================================================

    public sealed class ManholeExcavationDepthTierVm : ViewModelBase
    {
        private readonly ManholeExcavationDepthTier _model;
        private SubBaseLayerVm       _selectedSubBaseLayer;
        private ManholeBackfillLayerVm _selectedBackfillLayer;

        public ManholeExcavationDepthTierVm(ManholeExcavationDepthTier model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            SubBaseLayers = new ObservableCollection<SubBaseLayerVm>();
            foreach (var l in model.SubBaseLayers)
                SubBaseLayers.Add(new SubBaseLayerVm(l));

            BackfillLayers = new ObservableCollection<ManholeBackfillLayerVm>();
            foreach (var l in model.BackfillLayers)
                BackfillLayers.Add(new ManholeBackfillLayerVm(l));

            AddSubBaseLayerCommand       = new RelayCommand(OnAddSubBaseLayer);
            DuplicateSubBaseLayerCommand = new RelayCommand(OnDuplicateSubBaseLayer,
                                              _ => _selectedSubBaseLayer != null);
            DeleteSubBaseLayerCommand    = new RelayCommand(OnDeleteSubBaseLayer,
                                              _ => _selectedSubBaseLayer != null);

            AddBackfillLayerCommand       = new RelayCommand(OnAddBackfillLayer);
            DuplicateBackfillLayerCommand = new RelayCommand(OnDuplicateBackfillLayer,
                                               _ => _selectedBackfillLayer != null);
            DeleteBackfillLayerCommand    = new RelayCommand(OnDeleteBackfillLayer,
                                               _ => _selectedBackfillLayer != null);
        }

        public ManholeExcavationDepthTier Model => _model;

        // ── Depth range ───────────────────────────────────────────────────────

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

        /// <summary>False when MinDepth > MaxDepth (MaxDepth 0 = unlimited, always valid).</summary>
        public bool IsDepthRangeValid => _model.MaxDepthM <= 0 || _model.MinDepthM <= _model.MaxDepthM;

        public string DepthRangeDisplay => string.Format("{0:0.0} – {1} m",
            _model.MinDepthM,
            _model.MaxDepthM <= 0 ? "∞" : _model.MaxDepthM.ToString("0.0"));

        // ── Excavation parameters ─────────────────────────────────────────────

        public double WorkingClearanceM
        {
            get => _model.WorkingClearanceM;
            set { _model.WorkingClearanceM = value; OnPropertyChanged(); }
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

        // ── Sub-base layers (below the base slab) ────────────────────────────

        public ObservableCollection<SubBaseLayerVm> SubBaseLayers { get; }

        public SubBaseLayerVm SelectedSubBaseLayer
        {
            get => _selectedSubBaseLayer;
            set { Set(ref _selectedSubBaseLayer, value); }
        }

        public ICommand AddSubBaseLayerCommand       { get; }
        public ICommand DuplicateSubBaseLayerCommand { get; }
        public ICommand DeleteSubBaseLayerCommand    { get; }

        private void OnAddSubBaseLayer(object _)
        {
            var layer = new SubBaseLayer { LayerName = "Yeni Katman", ThicknessMm = 100 };
            _model.SubBaseLayers.Add(layer);
            var vm = new SubBaseLayerVm(layer);
            SubBaseLayers.Add(vm);
            SelectedSubBaseLayer = vm;
        }

        private void OnDuplicateSubBaseLayer(object _)
        {
            if (_selectedSubBaseLayer == null) return;
            var src   = _selectedSubBaseLayer.Model;
            var clone = new SubBaseLayer { LayerName = src.LayerName, ThicknessMm = src.ThicknessMm };
            _model.SubBaseLayers.Add(clone);
            var vm = new SubBaseLayerVm(clone);
            SubBaseLayers.Add(vm);
            SelectedSubBaseLayer = vm;
        }

        private void OnDeleteSubBaseLayer(object _)
        {
            if (_selectedSubBaseLayer == null) return;
            _model.SubBaseLayers.Remove(_selectedSubBaseLayer.Model);
            SubBaseLayers.Remove(_selectedSubBaseLayer);
            SelectedSubBaseLayer = null;
        }

        // ── Backfill layers (around the shaft after installation) ─────────────

        public ObservableCollection<ManholeBackfillLayerVm> BackfillLayers { get; }

        public ManholeBackfillLayerVm SelectedBackfillLayer
        {
            get => _selectedBackfillLayer;
            set { Set(ref _selectedBackfillLayer, value); }
        }

        public ICommand AddBackfillLayerCommand       { get; }
        public ICommand DuplicateBackfillLayerCommand { get; }
        public ICommand DeleteBackfillLayerCommand    { get; }

        private void OnAddBackfillLayer(object _)
        {
            var layer = new ManholeBackfillLayer { LayerName = "Yeni Geri Dolgu", MaterialType = "", ThicknessM = 0.3 };
            _model.BackfillLayers.Add(layer);
            var vm = new ManholeBackfillLayerVm(layer);
            BackfillLayers.Add(vm);
            SelectedBackfillLayer = vm;
        }

        private void OnDuplicateBackfillLayer(object _)
        {
            if (_selectedBackfillLayer == null) return;
            var src   = _selectedBackfillLayer.Model;
            var clone = new ManholeBackfillLayer
            {
                LayerName      = src.LayerName,
                MaterialType   = src.MaterialType,
                ThicknessM     = src.ThicknessM,
                IsFillToSurface = src.IsFillToSurface
            };
            _model.BackfillLayers.Add(clone);
            var vm = new ManholeBackfillLayerVm(clone);
            BackfillLayers.Add(vm);
            SelectedBackfillLayer = vm;
        }

        private void OnDeleteBackfillLayer(object _)
        {
            if (_selectedBackfillLayer == null) return;
            _model.BackfillLayers.Remove(_selectedBackfillLayer.Model);
            BackfillLayers.Remove(_selectedBackfillLayer);
            SelectedBackfillLayer = null;
        }
    }

    // =========================================================================
    // ManholeExcavationRuleVm – one rule (one target base + its depth tiers)
    // Master node in the Master-Detail layout.
    // =========================================================================

    public sealed class ManholeExcavationRuleVm : ViewModelBase
    {
        private readonly ManholeExcavationRule _model;
        private ManholeExcavationDepthTierVm   _selectedTier;

        public ManholeExcavationRuleVm(ManholeExcavationRule model)
        {
            _model = model ?? throw new ArgumentNullException(nameof(model));

            DepthTiers = new ObservableCollection<ManholeExcavationDepthTierVm>();
            foreach (var t in model.DepthTiers)
                DepthTiers.Add(new ManholeExcavationDepthTierVm(t));

            AddTierCommand       = new RelayCommand(OnAddTier);
            DuplicateTierCommand = new RelayCommand(OnDuplicateTier, _ => _selectedTier != null);
            DeleteTierCommand    = new RelayCommand(OnDeleteTier,    _ => _selectedTier != null);

            BuildFamilyFilters();
            RebuildDiameterList();
            BuildSoilFilters();
        }

        public ManholeExcavationRule Model => _model;

        // ── Rule name ─────────────────────────────────────────────────────────

        public string RuleName
        {
            get => _model.RuleName;
            set { _model.RuleName = value; OnPropertyChanged(); }
        }

        // ── Manhole family filter + available base diameters ──────────────────

        public ObservableCollection<ManholeExcavFamilyFilterVm> FamilyFilters { get; }
            = new ObservableCollection<ManholeExcavFamilyFilterVm>();

        private bool _suppressFamilyRebuild;
        private bool _isFamilyDropdownOpen;

        public bool IsFamilyDropdownOpen
        {
            get => _isFamilyDropdownOpen;
            set
            {
                Set(ref _isFamilyDropdownOpen, value);
                // Re-read the catalog each time the dropdown opens so that families/tabans
                // added in SMART_ASSEMBLY during the same session appear immediately.
                if (value) { BuildFamilyFilters(); RebuildDiameterList(); }
            }
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
                foreach (var f in FamilyFilters) f.IsSelected = target;
                _suppressFamilyRebuild = false;
                _model.SelectedFamilyNames.Clear();
                RebuildDiameterList();
                OnPropertyChanged();
                OnPropertyChanged(nameof(SelectedFamiliesDisplay));
            }
        }

        public ObservableCollection<TabanSizeOption> AvailableBaseDiameters { get; }
            = new ObservableCollection<TabanSizeOption>();

        private void BuildFamilyFilters()
        {
            _suppressFamilyRebuild = true;
            FamilyFilters.Clear();
            bool useStored = _model.SelectedFamilyNames.Count > 0;
            foreach (var family in SmartAssemblyCatalogStore.Current.Families)
            {
                bool sel = !useStored || _model.SelectedFamilyNames.Contains(family.Name);
                var vm = new ManholeExcavFamilyFilterVm(family.Name) { OnChanged = OnFamilyFilterChanged };
                vm.IsSelected = sel;
                FamilyFilters.Add(vm);
            }
            _suppressFamilyRebuild = false;
            OnPropertyChanged(nameof(AllFamiliesSelected));
            OnPropertyChanged(nameof(SelectedFamiliesDisplay));
        }

        private void RebuildDiameterList()
        {
            var seen = new System.Collections.Generic.Dictionary<string, TabanSizeOption>();
            var selNames = new HashSet<string>(
                FamilyFilters.Where(f => f.IsSelected).Select(f => f.FamilyName));
            bool useFilter = FamilyFilters.Count > 0 && selNames.Count < FamilyFilters.Count;

            foreach (var family in SmartAssemblyCatalogStore.Current.Families)
            {
                if (useFilter && !selNames.Contains(family.Name)) continue;
                foreach (var comp in family.Components)
                {
                    var bottom = comp as BottomElementComponent;
                    if (bottom == null) continue;

                    TabanSizeOption opt = null;
                    switch (bottom.Footprint.Shape)
                    {
                        case FootprintShape.Circular:
                        {
                            double d = bottom.Footprint.DiameterMm;
                            if (d > 0)
                            {
                                string key = string.Format("C:{0:0}", d);
                                if (!seen.ContainsKey(key))
                                    opt = new TabanSizeOption(d,
                                        string.Format("Ø{0:0} mm (Daire)", d), d, key);
                            }
                            break;
                        }
                        case FootprintShape.Square:
                        {
                            double s = bottom.Footprint.SideMm;
                            if (s > 0)
                            {
                                string key = string.Format("S:{0:0}", s);
                                if (!seen.ContainsKey(key))
                                    opt = new TabanSizeOption(s,
                                        string.Format("{0:0}×{0:0} mm (Kare)", s), s, key);
                            }
                            break;
                        }
                        case FootprintShape.Rectangular:
                        {
                            double l = bottom.Footprint.LengthMm;
                            double w = bottom.Footprint.WidthMm;
                            if (l > 0 && w > 0)
                            {
                                // Normalise: longer side first so L×W and W×L share one key
                                double bigSide   = Math.Max(l, w);
                                double smallSide = Math.Min(l, w);
                                string key = string.Format("R:{0:0}x{1:0}", bigSide, smallSide);
                                if (!seen.ContainsKey(key))
                                    opt = new TabanSizeOption(bigSide,
                                        string.Format("{0:0}×{1:0} mm (Dikdörtgen)", bigSide, smallSide),
                                        bigSide, key);
                            }
                            break;
                        }
                    }
                    if (opt != null) seen[opt.Key] = opt;
                }
            }

            AvailableBaseDiameters.Clear();
            foreach (var opt in seen.Values.OrderBy(o => o.SortKey).ThenBy(o => o.Key))
                AvailableBaseDiameters.Add(opt);
        }

        /// <summary>
        /// Re-reads the Smart Assembly catalog (live VM or disk) and rebuilds the
        /// family filter list + available diameter list. Preserves existing selections.
        /// </summary>
        public void RefreshFromSmartAssembly()
        {
            BuildFamilyFilters();
            RebuildDiameterList();
        }

        private void OnFamilyFilterChanged()
        {
            if (_suppressFamilyRebuild) return;
            _model.SelectedFamilyNames.Clear();
            int selCount = FamilyFilters.Count(f => f.IsSelected);
            if (selCount < FamilyFilters.Count)
                foreach (var f in FamilyFilters.Where(f => f.IsSelected))
                    _model.SelectedFamilyNames.Add(f.FamilyName);
            RebuildDiameterList();
            OnPropertyChanged(nameof(AllFamiliesSelected));
            OnPropertyChanged(nameof(SelectedFamiliesDisplay));
        }

        // ── Zemin-tipi filter ─────────────────────────────────────────────────

        public ObservableCollection<ManholeExcavSoilFilterVm> SoilFilters { get; }
            = new ObservableCollection<ManholeExcavSoilFilterVm>();

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
                foreach (var f in SoilFilters) f.IsSelected = target;
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
                var vm = new ManholeExcavSoilFilterVm(soil.SoilName) { OnChanged = OnSoilFilterChanged };
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

        // ── Diameter range ────────────────────────────────────────────────────

        public double MinBaseDiameterMm
        {
            get => _model.MinBaseDiameterMm;
            set
            {
                _model.MinBaseDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiameterRangeDisplay));
                OnPropertyChanged(nameof(IsDiameterRangeValid));
            }
        }

        public double MaxBaseDiameterMm
        {
            get => _model.MaxBaseDiameterMm;
            set
            {
                if (value > 0 && value < _model.MinBaseDiameterMm)
                {
                    MessageBox.Show(
                        $"Maks taban çapı (Ø{value:0} mm), Min çapından (Ø{_model.MinBaseDiameterMm:0} mm) küçük olamaz.",
                        "Geçersiz Değer", MessageBoxButton.OK, MessageBoxImage.Warning);
                    OnPropertyChanged();
                    return;
                }
                _model.MaxBaseDiameterMm = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(DiameterRangeDisplay));
                OnPropertyChanged(nameof(IsDiameterRangeValid));
            }
        }

        /// <summary>False when Min > Max (Max 0 = unlimited, always valid).</summary>
        public bool IsDiameterRangeValid =>
            _model.MaxBaseDiameterMm <= 0 || _model.MinBaseDiameterMm <= _model.MaxBaseDiameterMm;

        public string DiameterRangeDisplay => string.Format("Ø{0:0} – {1} mm",
            _model.MinBaseDiameterMm,
            _model.MaxBaseDiameterMm <= 0 ? "∞" : ("Ø" + _model.MaxBaseDiameterMm.ToString("0")));

        // ── Depth tiers ───────────────────────────────────────────────────────

        public ObservableCollection<ManholeExcavationDepthTierVm> DepthTiers { get; }

        public ManholeExcavationDepthTierVm SelectedTier
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
        public ICommand DuplicateTierCommand { get; }
        public ICommand DeleteTierCommand    { get; }

        private void OnAddTier(object _)
        {
            double nextMin = DepthTiers.Count > 0
                ? DepthTiers[DepthTiers.Count - 1].MaxDepthM
                : 0;

            var tier = new ManholeExcavationDepthTier
            {
                MinDepthM         = nextMin,
                MaxDepthM         = 0,
                WorkingClearanceM = 0.5,
                SlopeRatio        = 0,
                MaxStepHeightM    = 1.5,
                StepBermWidthM    = 0.5
            };
            _model.DepthTiers.Add(tier);
            var vm = new ManholeExcavationDepthTierVm(tier);
            DepthTiers.Add(vm);
            SelectedTier = vm;
        }

        private void OnDuplicateTier(object _)
        {
            if (_selectedTier == null) return;
            var clone = DeepCloneTier(_selectedTier.Model);
            _model.DepthTiers.Add(clone);
            var vm = new ManholeExcavationDepthTierVm(clone);
            DepthTiers.Add(vm);
            SelectedTier = vm;
        }

        private void OnDeleteTier(object _)
        {
            if (_selectedTier == null) return;
            _model.DepthTiers.Remove(_selectedTier.Model);
            DepthTiers.Remove(_selectedTier);
            SelectedTier = null;
        }

        internal static ManholeExcavationDepthTier DeepCloneTier(ManholeExcavationDepthTier src)
        {
            var clone = new ManholeExcavationDepthTier
            {
                MinDepthM          = src.MinDepthM,
                MaxDepthM          = src.MaxDepthM,
                WorkingClearanceM  = src.WorkingClearanceM,
                SlopeRatio         = src.SlopeRatio,
                RequiresShoring    = src.RequiresShoring,
                IsSteppedExcavation = src.IsSteppedExcavation,
                MaxStepHeightM     = src.MaxStepHeightM,
                StepBermWidthM     = src.StepBermWidthM
            };
            foreach (var l in src.SubBaseLayers)
                clone.SubBaseLayers.Add(new SubBaseLayer { LayerName = l.LayerName, ThicknessMm = l.ThicknessMm });
            foreach (var l in src.BackfillLayers)
                clone.BackfillLayers.Add(new ManholeBackfillLayer
                {
                    LayerName       = l.LayerName,
                    MaterialType    = l.MaterialType,
                    ThicknessM      = l.ThicknessM,
                    IsFillToSurface = l.IsFillToSurface
                });
            return clone;
        }
    }

    // =========================================================================
    // ManholeExcavationMainVm – top-level VM
    // Left panel: Rules list (one entry per target base).
    // Right panel: SelectedRule detail — TargetBase ComboBox + depth tier grid.
    // =========================================================================

    public sealed class ManholeExcavationMainVm : ViewModelBase
    {
        private readonly List<ManholeExcavationRule>   _backingRules;
        private ManholeExcavationRuleVm                _selectedRule;

        public ObservableCollection<ManholeExcavationRuleVm>  Rules { get; }

        // ── Status bar ────────────────────────────────────────────────────────

        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Master selection ──────────────────────────────────────────────────

        public ManholeExcavationRuleVm SelectedRule
        {
            get => _selectedRule;
            set
            {
                Set(ref _selectedRule, value);
                OnPropertyChanged(nameof(IsRuleSelected));
            }
        }

        public bool IsRuleSelected => _selectedRule != null;

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand AddRuleCommand       { get; }
        public ICommand DuplicateRuleCommand { get; }
        public ICommand DeleteRuleCommand    { get; }
        public ICommand SaveCommand          { get; }
        public ICommand ExportCommand        { get; }
        public ICommand ImportCommand        { get; }
        public ICommand RefreshCommand       { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public ManholeExcavationMainVm(IEnumerable<ManholeExcavationRule> existingRules = null)
        {
            _backingRules = new List<ManholeExcavationRule>(existingRules ?? new ManholeExcavationRule[0]);

            // Auto-load from internal path if no rules were injected
            if (_backingRules.Count == 0)
            {
                try
                {
                    _backingRules.AddRange(ManholeExcavationCatalogXmlManager.LoadInternal());
                }
                catch (Exception ex)
                {
                    _statusText = "Otomatik yükleme hatası: " + ex.Message;
                }
            }

            Rules = new ObservableCollection<ManholeExcavationRuleVm>();
            foreach (var r in _backingRules)
                Rules.Add(BuildRuleVm(r));

            if (Rules.Count > 0 && _statusText == "Hazır.")
                StatusText = $"{Rules.Count} kural yüklendi.";

            AddRuleCommand       = new RelayCommand(OnAddRule);
            DuplicateRuleCommand = new RelayCommand(OnDuplicateRule, _ => IsRuleSelected);
            DeleteRuleCommand    = new RelayCommand(OnDeleteRule,    _ => IsRuleSelected);
            SaveCommand          = new RelayCommand(OnSave,    _ => _backingRules.Count > 0);
            ExportCommand        = new RelayCommand(OnExport,  _ => _backingRules.Count > 0);
            ImportCommand        = new RelayCommand(OnImport);
            RefreshCommand       = new RelayCommand(OnRefresh);
        }

        // ── Rule CRUD ─────────────────────────────────────────────────────────

        private void OnAddRule(object _)
        {
            var rule = new ManholeExcavationRule();
            _backingRules.Add(rule);
            var vm = BuildRuleVm(rule);
            Rules.Add(vm);
            SelectedRule = vm;
            StatusText = "Yeni kazı kuralı eklendi.";
        }

        private void OnDuplicateRule(object _)
        {
            if (_selectedRule == null) return;
            var clone = new ManholeExcavationRule
            {
                RuleName          = _selectedRule.Model.RuleName + " (Kopya)",
                MinBaseDiameterMm = _selectedRule.Model.MinBaseDiameterMm,
                MaxBaseDiameterMm = _selectedRule.Model.MaxBaseDiameterMm
            };
            foreach (var name in _selectedRule.Model.SelectedFamilyNames)
                clone.SelectedFamilyNames.Add(name);
            foreach (var name in _selectedRule.Model.SelectedSoilNames)
                clone.SelectedSoilNames.Add(name);
            foreach (var t in _selectedRule.Model.DepthTiers)
                clone.DepthTiers.Add(ManholeExcavationRuleVm.DeepCloneTier(t));
            _backingRules.Add(clone);
            var vm = BuildRuleVm(clone);
            Rules.Add(vm);
            SelectedRule = vm;
            StatusText = "Kural çoğaltıldı.";
        }

        private void OnDeleteRule(object _)
        {
            if (_selectedRule == null) return;
            _backingRules.Remove(_selectedRule.Model);
            Rules.Remove(_selectedRule);
            SelectedRule = null;
            StatusText = "Kazı kuralı silindi.";
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private void OnSave(object _)
        {
            try
            {
                ManholeExcavationCatalogXmlManager.SaveInternal(_backingRules);
                StatusText = $"Kaydedildi → {ManholeExcavationCatalogXmlManager.InternalPath}";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            var dlg = new SaveFileDialog
            {
                Filter   = "Manhole Kazı Kataloğu XML (*.xml)|*.xml",
                Title    = "Kazı Kurallarını Dışa Aktar",
                FileName = "ManholeExcavCatalog.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ManholeExcavationCatalogXmlManager.Export(_backingRules, dlg.FileName);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Manhole Kazı Kataloğu XML (*.xml)|*.xml",
                Title  = "Kazı Kurallarını İçe Aktar"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var loaded = ManholeExcavationCatalogXmlManager.Import(dlg.FileName);
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
                    Rules.Add(BuildRuleVm(r));
                }
                StatusText = $"İçe aktarıldı: {loaded.Count} kural.";
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private ManholeExcavationRuleVm BuildRuleVm(ManholeExcavationRule r)
            => new ManholeExcavationRuleVm(r);

        private static void ShowError(string context, Exception ex)
            => MessageBox.Show(context + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);

        /// <summary>
        /// Re-reads the Smart Assembly catalog for every rule VM.
        /// Invalidates the disk cache first so that components saved in SMART_ASSEMBLY
        /// during the same session are visible even if the SA window is currently closed.
        /// </summary>
        public void RefreshSmartAssemblyData()
        {
            // Force the disk-cached copy to be reloaded on the next access.
            // When SA is open, Current returns the live VM catalog anyway, so
            // Invalidate() has no effect in that case.
            SmartAssemblyCatalogStore.Invalidate();
            foreach (var ruleVm in Rules)
                ruleVm.RefreshFromSmartAssembly();
        }

        private void OnRefresh(object _)
        {
            RefreshSmartAssemblyData();
            StatusText = "Bileşen kataloğu yenilendi.";
        }

        /// <summary>Returns the backing model list for persistence (NOD, XML, etc.).</summary>
        public IReadOnlyList<ManholeExcavationRule> GetRules() => _backingRules;
    }

    // =========================================================================
    // ManholeExcavFamilyFilterVm – one checkbox row in the manhole-family filter
    // =========================================================================

    public sealed class ManholeExcavFamilyFilterVm : ViewModelBase
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

        public ManholeExcavFamilyFilterVm(string name) => FamilyName = name;
    }

    // =========================================================================
    // ManholeExcavSoilFilterVm – one checkbox row in the zemin-tipi filter
    // =========================================================================

    public sealed class ManholeExcavSoilFilterVm : ViewModelBase
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

        public ManholeExcavSoilFilterVm(string name) => SoilName = name;
    }

    // =========================================================================
    // TabanSizeOption – one item in the Min/Max Taban Ø ComboBox
    // Carries both the numeric value (stored in the rule) and a human-readable
    // display string that shows shape type: Daire / Kare / Dikdörtgen.
    // =========================================================================

    public sealed class TabanSizeOption
    {
        /// <summary>Numeric size used for MinBaseDiameterMm / MaxBaseDiameterMm.</summary>
        public double Value   { get; }

        /// <summary>Label shown in the ComboBox, e.g. "Ø1400 mm (Daire)" or "1000×1000 mm (Kare)".</summary>
        public string Display { get; }

        /// <summary>Primary dimension used for sort order.</summary>
        public double SortKey { get; }

        /// <summary>Deduplication key — prevents identical (shape + dimensions) from appearing twice.</summary>
        public string Key     { get; }

        public TabanSizeOption(double value, string display, double sortKey, string key)
        {
            Value   = value;
            Display = display;
            SortKey = sortKey;
            Key     = key;
        }
    }
}
