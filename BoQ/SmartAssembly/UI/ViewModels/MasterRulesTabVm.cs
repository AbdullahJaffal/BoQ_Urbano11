using System;
using System.Collections.ObjectModel;
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
            set { Set(ref _selectedDepthTier, value); OnPropertyChanged(nameof(IsTierSelected)); }
        }

        public bool IsTierSelected => _selectedDepthTier != null;

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand AddPipeRangeCommand       { get; }
        public ICommand DeletePipeRangeCommand    { get; }
        public ICommand DuplicatePipeRangeCommand { get; }
        public ICommand MoveUpCommand             { get; }
        public ICommand MoveDownCommand           { get; }
        public ICommand AddTierCommand            { get; }
        public ICommand DeleteTierCommand         { get; }

        public ICommand SaveCommand   { get; }   // Kaydet    → fixed AppData path, no dialog
        public ICommand ExportCommand { get; }   // Dışa Aktar → user picks path
        public ICommand ImportCommand { get; }   // İçe Aktar  → user picks file

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
            var vm = new PipeRangeRuleVm(model, _pipeCatalog);
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
                clone.DepthTiers.Add(new DepthTierRule
                {
                    MinDepthM      = t.MinDepthM,
                    MaxDepthM      = t.MaxDepthM,
                    SelectedBaseId = t.SelectedBaseId,
                    IsCastInSitu   = t.IsCastInSitu,
                    Notes          = t.Notes
                });

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
    }
}
