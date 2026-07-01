using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    public class MasterRulesTabVm : ViewModelBase
    {
        private readonly SmartAssemblyMasterCatalog _catalog;
        private PipeCatalog _pipeCatalog;

        // ── Left panel — pipe-range list ──────────────────────────────────────
        public ObservableCollection<PipeRangeRuleVm> PipeRanges    { get; } = new ObservableCollection<PipeRangeRuleVm>();
        public ObservableCollection<BottomElementVm> AvailableBases { get; } = new ObservableCollection<BottomElementVm>();

        private PipeRangeRuleVm _selectedPipeRange;
        public PipeRangeRuleVm SelectedPipeRange
        {
            get => _selectedPipeRange;
            set
            {
                Set(ref _selectedPipeRange, value);
                OnPropertyChanged(nameof(IsPipeRangeSelected));
                SelectedDepthTier = null;
            }
        }

        public bool IsPipeRangeSelected => _selectedPipeRange != null;

        // ── Right panel — depth tiers of selected pipe range ──────────────────
        private DepthTierVm _selectedDepthTier;
        public DepthTierVm SelectedDepthTier
        {
            get => _selectedDepthTier;
            set
            {
                Set(ref _selectedDepthTier, value);
                OnPropertyChanged(nameof(IsTierSelected));
                RebuildTierConstraints();
            }
        }

        public bool IsTierSelected => _selectedDepthTier != null;

        /// <summary>Constraint rows shown in the right-side panel for the selected depth tier.</summary>
        public ObservableCollection<ComponentConstraintVm> TierConstraints { get; }
            = new ObservableCollection<ComponentConstraintVm>();

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand AddPipeRangeCommand       { get; }
        public ICommand DeletePipeRangeCommand    { get; }
        public ICommand DuplicatePipeRangeCommand { get; }
        public ICommand MoveUpCommand             { get; }
        public ICommand MoveDownCommand           { get; }
        public ICommand AddTierCommand            { get; }
        public ICommand DeleteTierCommand         { get; }

        public ICommand SaveCommand               { get; }
        public ICommand ExportCommand            { get; }
        public ICommand ImportCommand            { get; }
        public ICommand ManageSystemTypesCommand { get; }

        // Sistem tipleri ComboBox kaynağı
        public ObservableCollection<SystemType> AvailableSystemTypes => _catalog.SystemTypes;

        public MasterRulesTabVm(SmartAssemblyMasterCatalog catalog, PipeCatalog pipeCatalog = null)
        {
            _catalog     = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _pipeCatalog = pipeCatalog;

            AddPipeRangeCommand       = new RelayCommand(OnAddPipeRange);
            DeletePipeRangeCommand    = new RelayCommand(OnDeletePipeRange,    _ => IsPipeRangeSelected);
            DuplicatePipeRangeCommand = new RelayCommand(OnDuplicatePipeRange, _ => IsPipeRangeSelected);
            MoveUpCommand             = new RelayCommand(OnMoveUp,   _ => IsPipeRangeSelected && PipeRanges.IndexOf(_selectedPipeRange) > 0);
            MoveDownCommand           = new RelayCommand(OnMoveDown, _ => IsPipeRangeSelected && PipeRanges.IndexOf(_selectedPipeRange) < PipeRanges.Count - 1);
            AddTierCommand            = new RelayCommand(OnAddTier,    _ => IsPipeRangeSelected);
            DeleteTierCommand         = new RelayCommand(OnDeleteTier, _ => IsTierSelected);
            SaveCommand               = new RelayCommand(OnSave);
            ExportCommand             = new RelayCommand(OnExport);
            ImportCommand             = new RelayCommand(OnImport);
            ManageSystemTypesCommand  = new RelayCommand(OnManageSystemTypes);

            // Auto-load from fixed AppData path (silent, like PipeCatalog)
            var path = MasterCatalogXmlManager.DefaultRulesPath;
            if (File.Exists(path))
                TryLoadRules(path, silent: true);
            else
                Reload();
        }

        public void Reload()
        {
            PipeRanges.Clear();
            foreach (var pr in _catalog.MasterPipeRules)
                PipeRanges.Add(BuildVm(pr));
            RefreshBasesCombo();
        }

        /// <summary>Called whenever components change in the Repository tab.</summary>
        public void RefreshBasesCombo()
        {
            AvailableBases.Clear();
            foreach (var b in _catalog.GetBases())
                AvailableBases.Add(new BottomElementVm(b));

            foreach (var pr in PipeRanges)
                foreach (var tier in pr.DepthTiers)
                    ResolveBaseName(tier);
        }

        /// <summary>Replaces the pipe catalog; rebuilds all PipeRangeRule VMs so their
        /// cascading ComboBoxes reflect the new families list.</summary>
        public void SetPipeCatalog(PipeCatalog pipeCatalog)
        {
            _pipeCatalog = pipeCatalog;
            Reload();
        }

        // ── VM factory ────────────────────────────────────────────────────────

        private PipeRangeRuleVm BuildVm(PipeRangeRule model)
        {
            var vm = new PipeRangeRuleVm(model, _pipeCatalog, _catalog.SystemTypes);
            foreach (var t in model.DepthTiers)
            {
                var tvm = new DepthTierVm(t, ResolveBaseName);
                ResolveBaseName(tvm);
                vm.DepthTiers.Add(tvm);
            }
            return vm;
        }

        private void ResolveBaseName(DepthTierVm vm)
        {
            if (vm.IsCastInSitu) { vm.BaseName = "Yerinde Döküm"; return; }
            var comp = _catalog.FindById(vm.SelectedBaseId);
            vm.BaseName = comp != null ? comp.Name : (vm.SelectedBaseId == Guid.Empty ? "(seçilmedi)" : "?");
            if (vm == _selectedDepthTier)
                RebuildTierConstraints();
        }

        private void RebuildTierConstraints()
        {
            // Unsubscribe from old rows
            foreach (var old in TierConstraints)
                old.PropertyChanged -= OnConstraintChanged;
            TierConstraints.Clear();

            var tier = _selectedDepthTier;
            if (tier == null) return;

            // Taban is always present, always 1/1, read-only
            var tabanC = tier.Model.GetOrCreateConstraint(ComponentRole.BottomElement);
            tabanC.MinCount = 1;
            tabanC.MaxCount = 1;
            TierConstraints.Add(new ComponentConstraintVm(tabanC, isReadOnly: true, isZeroOrOne: false));

            // Find the family that owns the selected base component
            var baseComp   = _catalog.FindById(tier.SelectedBaseId);
            var baseFamily = FindFamilyForComponent(baseComp);

            if (baseFamily != null)
            {
                // Add remaining roles in display order, only if the family has at least one component of that type
                var orderedRoles = new[]
                {
                    new { Role = ComponentRole.MiddleElement, IsZeroOrOne = false },
                    new { Role = ComponentRole.Reducer,       IsZeroOrOne = true  },
                    new { Role = ComponentRole.Adjuster,      IsZeroOrOne = false },
                    new { Role = ComponentRole.Cover,         IsZeroOrOne = true  },
                };
                foreach (var entry in orderedRoles)
                {
                    bool hasRole = false;
                    foreach (var c in baseFamily.Components)
                        if (c.Role == entry.Role) { hasRole = true; break; }
                    if (!hasRole) continue;

                    var constraint = tier.Model.GetOrCreateConstraint(entry.Role);
                    // ZeroOrOne types cannot be unlimited; clamp default -1 to 1
                    if (entry.IsZeroOrOne && constraint.MaxCount == -1)
                        constraint.MaxCount = 1;
                    TierConstraints.Add(new ComponentConstraintVm(constraint, isReadOnly: false, isZeroOrOne: entry.IsZeroOrOne));
                }
            }

            // Subscribe to MinCount changes on every new row, then recalculate
            foreach (var vm in TierConstraints)
                vm.PropertyChanged += OnConstraintChanged;
            RecalcMinDepth();
        }

        private void OnConstraintChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ComponentConstraintVm.MinCount))
                RecalcMinDepth();
        }

        private void RecalcMinDepth()
        {
            if (_selectedDepthTier == null) return;
            double computed = ComputeMinDepthM(_selectedDepthTier);
            if (Math.Abs(_selectedDepthTier.MinDepthM - computed) > 1e-6)
                _selectedDepthTier.MinDepthM = computed;
        }

        // ── Pipe range CRUD ───────────────────────────────────────────────────

        private void OnAddPipeRange(object _)
        {
            var rule = new PipeRangeRule();
            _catalog.MasterPipeRules.Add(rule);
            var vm = BuildVm(rule);
            PipeRanges.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedPipeRange = vm));
        }

        private void OnDeletePipeRange(object _)
        {
            if (_selectedPipeRange == null) return;
            _catalog.MasterPipeRules.Remove(_selectedPipeRange.Model);
            PipeRanges.Remove(_selectedPipeRange);
            SelectedPipeRange = null;
        }

        private void OnDuplicatePipeRange(object _)
        {
            if (_selectedPipeRange == null) return;
            var src   = _selectedPipeRange.Model;
            var clone = new PipeRangeRule
            {
                MinPipeMm            = src.MinPipeMm,
                MaxPipeMm            = src.MaxPipeMm,
                SelectedPipeFamilyId = src.SelectedPipeFamilyId,
                MinPipeId            = src.MinPipeId,
                MaxPipeId            = src.MaxPipeId
            };
            foreach (var t in src.DepthTiers)
            {
                var clonedTier = new DepthTierRule
                {
                    MinDepthM      = t.MinDepthM,
                    MaxDepthM      = t.MaxDepthM,
                    SelectedBaseId = t.SelectedBaseId,
                    IsCastInSitu   = t.IsCastInSitu,
                    Notes          = t.Notes
                };
                foreach (var cc in t.ComponentConstraints)
                    clonedTier.ComponentConstraints.Add(new ComponentTypeConstraint
                    {
                        Role = cc.Role, MinCount = cc.MinCount, MaxCount = cc.MaxCount
                    });
                clone.DepthTiers.Add(clonedTier);
            }

            int idx = _catalog.MasterPipeRules.IndexOf(src);
            _catalog.MasterPipeRules.Insert(idx + 1, clone);
            var vm = BuildVm(clone);
            PipeRanges.Insert(PipeRanges.IndexOf(_selectedPipeRange) + 1, vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedPipeRange = vm));
        }

        private void OnMoveUp(object _)
        {
            int idx = PipeRanges.IndexOf(_selectedPipeRange);
            if (idx <= 0) return;
            Swap(idx, idx - 1);
        }

        private void OnMoveDown(object _)
        {
            int idx = PipeRanges.IndexOf(_selectedPipeRange);
            if (idx < 0 || idx >= PipeRanges.Count - 1) return;
            Swap(idx, idx + 1);
        }

        private void Swap(int a, int b)
        {
            var tmp = PipeRanges[a]; PipeRanges[a] = PipeRanges[b]; PipeRanges[b] = tmp;
            var mTmp = _catalog.MasterPipeRules[a];
            _catalog.MasterPipeRules[a] = _catalog.MasterPipeRules[b];
            _catalog.MasterPipeRules[b] = mTmp;
        }

        // ── XML persistence (PipeCatalog pattern) ────────────────────────────

        private void TryLoadRules(string path, bool silent)
        {
            try
            {
                // Load system types first so BuildVm can resolve references
                var systemTypes = MasterCatalogXmlManager.ImportSystemTypes(path);
                _catalog.SystemTypes.Clear();
                foreach (var st in systemTypes) _catalog.SystemTypes.Add(st);

                var rules = MasterCatalogXmlManager.ImportPipeRules(path);
                _catalog.MasterPipeRules.Clear();
                foreach (var r in rules) _catalog.MasterPipeRules.Add(r);
                Reload();
                if (!silent)
                    MessageBox.Show(
                        string.Format("{0} kural yüklendi.", rules.Count),
                        "İçe Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("Kural yükleme hatası:\n" + ex.Message,
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                else
                    Reload();
            }
        }

        private void OnSave(object _)
        {
            try
            {
                MasterCatalogXmlManager.ExportPipeRules(_catalog,
                    MasterCatalogXmlManager.DefaultRulesPath);
                MessageBox.Show("Kurallar kaydedildi.", "Kayıt Başarılı",
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Kayıt hatası:\n" + ex.Message, "Hata",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnExport(object _)
        {
            var dlg = new SaveFileDialog
            {
                Title      = "Kural Matrisini Dışa Aktar",
                Filter     = "Smart Assembly XML (*.xml)|*.xml",
                DefaultExt = ".xml",
                FileName   = "SmartAssemblyRules.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                MasterCatalogXmlManager.ExportPipeRules(_catalog, dlg.FileName);
                MessageBox.Show("Dışa aktarıldı:\n" + dlg.FileName,
                    "Dışa Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show("Dışa aktarma hatası:\n" + ex.Message,
                    "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnImport(object _)
        {
            var dlg = new OpenFileDialog
                { Title = "Kural Matrisi İçe Aktar", Filter = "Smart Assembly XML (*.xml)|*.xml" };
            if (dlg.ShowDialog() != true) return;
            TryLoadRules(dlg.FileName, silent: false);
        }

        // ── Depth tier CRUD ───────────────────────────────────────────────────

        private void OnAddTier(object _)
        {
            if (_selectedPipeRange == null) return;
            var tier = new DepthTierRule();

            // MinDepthM = previous tier's MaxDepthM + 1 mm
            var tiers = _selectedPipeRange.DepthTiers;
            if (tiers.Count > 0)
                tier.MinDepthM = tiers[tiers.Count - 1].Model.MaxDepthM + 0.001;

            _selectedPipeRange.Model.DepthTiers.Add(tier);
            var vm = new DepthTierVm(tier, ResolveBaseName);
            ResolveBaseName(vm);
            _selectedPipeRange.DepthTiers.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedDepthTier = vm));
        }

        private void OnDeleteTier(object _)
        {
            if (_selectedDepthTier == null || _selectedPipeRange == null) return;
            _selectedPipeRange.Model.DepthTiers.Remove(_selectedDepthTier.Model);
            _selectedPipeRange.DepthTiers.Remove(_selectedDepthTier);
            SelectedDepthTier = null;
        }

        // ── Min depth auto-calculation ────────────────────────────────────────

        /// <summary>
        /// Computes the theoretical minimum burial depth for a tier based on the
        /// MinCount constraints and the effective heights of matching components.
        /// Diameter chain: Taban.TopOpeningDiameterMm drives subsequent pieces;
        /// Konik.TopInnerDiameterMm changes the diameter for pieces above the reducer.
        /// </summary>
        private double ComputeMinDepthM(DepthTierVm tier)
        {
            if (tier == null || tier.IsCastInSitu || tier.SelectedBaseId == Guid.Empty)
                return tier != null ? tier.MinDepthM : 0;

            var taban = _catalog.FindById(tier.SelectedBaseId) as BottomElementComponent;
            if (taban == null) return tier.MinDepthM;

            var family = FindFamilyForComponent(taban);
            if (family == null) return tier.MinDepthM;

            double minMm  = 0;
            double diam   = taban.TopOpeningDiameterMm;
            var    model  = tier.Model;

            // Taban — always exactly 1
            minMm += taban.EffectiveHeight;

            // Gövde Halkası
            int minGovde = GetConstraintMin(model, ComponentRole.MiddleElement);
            if (minGovde > 0)
            {
                double h = MinHeightInFamily<MiddleElementComponent>(
                    family, c => Math.Abs(c.InnerDiameterMm - diam) < 0.5);
                minMm += minGovde * h;
                // diam unchanged after Govde
            }

            // Konik — also updates currentDiam to its TopInnerDiameterMm
            int minKonik = GetConstraintMin(model, ComponentRole.Reducer);
            if (minKonik > 0)
            {
                ReducerComponent bestKonik = null;
                foreach (var c in family.Components)
                {
                    var r = c as ReducerComponent;
                    if (r == null || Math.Abs(r.BottomInnerDiameterMm - diam) >= 0.5) continue;
                    if (bestKonik == null || r.EffectiveHeight < bestKonik.EffectiveHeight)
                        bestKonik = r;
                }
                if (bestKonik != null)
                {
                    minMm += minKonik * bestKonik.EffectiveHeight;
                    diam   = bestKonik.TopInnerDiameterMm;  // ← diameter narrows here
                }
            }

            // Boyun bileziği
            int minBoyun = GetConstraintMin(model, ComponentRole.Adjuster);
            if (minBoyun > 0)
            {
                double h = MinHeightInFamily<AdjusterComponent>(
                    family, c => Math.Abs(c.InnerDiameterMm - diam) < 0.5);
                minMm += minBoyun * h;
            }

            // Rögar Kapağı
            int minCover = GetConstraintMin(model, ComponentRole.Cover);
            if (minCover > 0)
            {
                double h = MinHeightInFamily<CoverComponent>(
                    family, c => Math.Abs(c.ClearOpeningMm - diam) < 0.5);
                minMm += minCover * h;
            }

            return minMm / 1000.0;
        }

        private ComponentFamily FindFamilyForComponent(ManholeComponent comp)
        {
            if (comp == null) return null;
            foreach (var f in _catalog.Families)
                foreach (var c in f.Components)
                    if (c.Id == comp.Id) return f;
            return null;
        }

        private static int GetConstraintMin(DepthTierRule model, ComponentRole role)
        {
            foreach (var cc in model.ComponentConstraints)
                if (cc.Role == role) return cc.MinCount;
            return 0;
        }

        private static double MinHeightInFamily<T>(ComponentFamily family, Func<T, bool> match)
            where T : ManholeComponent
        {
            double best = double.MaxValue;
            foreach (var c in family.Components)
            {
                var t = c as T;
                if (t != null && match(t) && t.EffectiveHeight < best)
                    best = t.EffectiveHeight;
            }
            return best == double.MaxValue ? 0 : best;
        }

        // ── System type management ─────────────────────────────────────────────

        private void OnManageSystemTypes(object _)
        {
            var win = new UrbanoMetraj.BoQ.SmartAssembly.UI.Views.SystemTypeManagerWindow(
                          _catalog.SystemTypes);
            win.ShowDialog();
            // Refresh display names on all existing pipe-range VMs
            foreach (var pr in PipeRanges)
                pr.RefreshSystemType(_catalog.SystemTypes);
        }
    }
}
