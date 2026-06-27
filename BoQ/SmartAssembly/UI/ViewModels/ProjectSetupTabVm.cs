using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using AcadAppSvc = Autodesk.AutoCAD.ApplicationServices;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    public class ProjectSetupTabVm : ViewModelBase
    {
        private readonly SmartAssemblyMasterCatalog _masterCatalog;
        private PipeCatalog _pipeCatalog;

        // ── Template list (left panel) ────────────────────────────────────────
        public ObservableCollection<ProjectTemplate> Templates { get; }
            = new ObservableCollection<ProjectTemplate>();

        private ProjectTemplate _selectedTemplate;
        public ProjectTemplate SelectedTemplate
        {
            get => _selectedTemplate;
            set
            {
                Set(ref _selectedTemplate, value);
                OnPropertyChanged(nameof(IsTemplateSelected));
                OnPropertyChanged(nameof(TemplateName));
                OnPropertyChanged(nameof(AssociatedExcavationProfile));
                OnPropertyChanged(nameof(MaterialFamilyOverride));
                LoadLocalPipeRanges();
            }
        }

        public bool IsTemplateSelected => _selectedTemplate != null;

        // ── Template header fields ────────────────────────────────────────────
        public string TemplateName
        {
            get => _selectedTemplate?.TemplateName;
            set { if (_selectedTemplate != null) { _selectedTemplate.TemplateName = value; OnPropertyChanged(); } }
        }

        public string AssociatedExcavationProfile
        {
            get => _selectedTemplate?.AssociatedExcavationProfile;
            set { if (_selectedTemplate != null) { _selectedTemplate.AssociatedExcavationProfile = value; OnPropertyChanged(); } }
        }

        public string MaterialFamilyOverride
        {
            get => _selectedTemplate?.MaterialFamilyOverride;
            set { if (_selectedTemplate != null) { _selectedTemplate.MaterialFamilyOverride = value; OnPropertyChanged(); } }
        }

        // ── Local pipe-range rules (right panel — master list) ────────────────
        public ObservableCollection<PipeRangeRuleVm> LocalPipeRanges { get; }
            = new ObservableCollection<PipeRangeRuleVm>();

        public ObservableCollection<BottomElementVm> AvailableBases { get; }
            = new ObservableCollection<BottomElementVm>();

        private PipeRangeRuleVm _selectedLocalPipeRange;
        public PipeRangeRuleVm SelectedLocalPipeRange
        {
            get => _selectedLocalPipeRange;
            set
            {
                Set(ref _selectedLocalPipeRange, value);
                OnPropertyChanged(nameof(IsLocalPipeRangeSelected));
                SelectedLocalTier = null;
            }
        }

        public bool IsLocalPipeRangeSelected => _selectedLocalPipeRange != null;

        // ── Depth tiers of selected local pipe range ──────────────────────────
        private DepthTierVm _selectedLocalTier;
        public DepthTierVm SelectedLocalTier
        {
            get => _selectedLocalTier;
            set { Set(ref _selectedLocalTier, value); OnPropertyChanged(nameof(IsLocalTierSelected)); }
        }

        public bool IsLocalTierSelected => _selectedLocalTier != null;

        // ── ComboBox sources ──────────────────────────────────────────────────
        public ObservableCollection<string> AvailableExcavationProfiles { get; }
            = new ObservableCollection<string>
            {
                "Standart Hendek",
                "Dar Hendek",
                "Geniş Hendek",
                "Kaya Hafriyatı",
                "Zeminli Dolgu"
            };

        public ObservableCollection<string> AvailableMaterialFamilies { get; }
            = new ObservableCollection<string>();

        public void RefreshMaterialFamilies()
        {
            AvailableMaterialFamilies.Clear();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _masterCatalog.Components)
                if (!string.IsNullOrWhiteSpace(c.FamilyTag) && seen.Add(c.FamilyTag))
                    AvailableMaterialFamilies.Add(c.FamilyTag);
        }

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand NewTemplateCommand            { get; }
        public ICommand DeleteTemplateCommand         { get; }
        public ICommand DuplicateTemplateCommand      { get; }
        public ICommand ImportFromMasterCommand       { get; }
        public ICommand SaveToNodCommand              { get; }
        public ICommand LoadFromNodCommand            { get; }

        public ICommand AddLocalPipeRangeCommand      { get; }
        public ICommand DeleteLocalPipeRangeCommand   { get; }
        public ICommand DuplicateLocalPipeRangeCommand { get; }
        public ICommand AddLocalTierCommand           { get; }
        public ICommand DeleteLocalTierCommand        { get; }

        public ProjectSetupTabVm(SmartAssemblyMasterCatalog masterCatalog, PipeCatalog pipeCatalog = null)
        {
            _masterCatalog = masterCatalog ?? throw new ArgumentNullException(nameof(masterCatalog));
            _pipeCatalog   = pipeCatalog;

            RefreshBasesCombo();
            RefreshMaterialFamilies();

            NewTemplateCommand            = new RelayCommand(OnNewTemplate);
            DeleteTemplateCommand         = new RelayCommand(OnDeleteTemplate,         _ => IsTemplateSelected);
            DuplicateTemplateCommand      = new RelayCommand(OnDuplicateTemplate,      _ => IsTemplateSelected);
            ImportFromMasterCommand       = new RelayCommand(OnImportFromMaster,       _ => IsTemplateSelected);
            SaveToNodCommand              = new RelayCommand(OnSaveToNod,              _ => IsTemplateSelected);
            LoadFromNodCommand            = new RelayCommand(OnLoadFromNod);

            AddLocalPipeRangeCommand      = new RelayCommand(OnAddLocalPipeRange,      _ => IsTemplateSelected);
            DeleteLocalPipeRangeCommand   = new RelayCommand(OnDeleteLocalPipeRange,   _ => IsLocalPipeRangeSelected);
            DuplicateLocalPipeRangeCommand = new RelayCommand(OnDuplicateLocalPipeRange, _ => IsLocalPipeRangeSelected);
            AddLocalTierCommand           = new RelayCommand(OnAddLocalTier,           _ => IsLocalPipeRangeSelected);
            DeleteLocalTierCommand        = new RelayCommand(OnDeleteLocalTier,        _ => IsLocalTierSelected);
        }

        public void RefreshBasesCombo()
        {
            AvailableBases.Clear();
            foreach (var b in _masterCatalog.GetBases())
                AvailableBases.Add(new BottomElementVm(b));

            foreach (var pr in LocalPipeRanges)
                foreach (var tier in pr.DepthTiers)
                    ResolveLocalBaseName(tier);
        }

        // ── Template CRUD ─────────────────────────────────────────────────────

        private void OnNewTemplate(object _)
        {
            var tpl = new ProjectTemplate { TemplateName = "Yeni Şablon " + (Templates.Count + 1) };
            Templates.Add(tpl);
            SelectedTemplate = tpl;
        }

        private void OnDeleteTemplate(object _)
        {
            if (_selectedTemplate == null) return;
            Templates.Remove(_selectedTemplate);
            SelectedTemplate = null;
        }

        private void OnDuplicateTemplate(object _)
        {
            if (_selectedTemplate == null) return;
            var src   = _selectedTemplate;
            var clone = new ProjectTemplate
            {
                TemplateName                = src.TemplateName + " - Kopya",
                AssociatedExcavationProfile = src.AssociatedExcavationProfile,
                MaterialFamilyOverride      = src.MaterialFamilyOverride
            };
            foreach (var pr in src.LocalPipeRules)
            {
                var clonedPr = new PipeRangeRule
                {
                    MinPipeMm            = pr.MinPipeMm,
                    MaxPipeMm            = pr.MaxPipeMm,
                    SelectedPipeFamilyId = pr.SelectedPipeFamilyId,
                    MinPipeId            = pr.MinPipeId,
                    MaxPipeId            = pr.MaxPipeId
                };
                foreach (var t in pr.DepthTiers)
                    clonedPr.DepthTiers.Add(new DepthTierRule
                    {
                        MinDepthM      = t.MinDepthM,
                        MaxDepthM      = t.MaxDepthM,
                        SelectedBaseId = t.SelectedBaseId,
                        IsCastInSitu   = t.IsCastInSitu,
                        Notes          = t.Notes
                    });
                clone.LocalPipeRules.Add(clonedPr);
            }
            Templates.Add(clone);
            SelectedTemplate = clone;
        }

        // ── Import from Master ────────────────────────────────────────────────

        private void OnImportFromMaster(object _)
        {
            if (_selectedTemplate == null) return;

            if (_selectedTemplate.LocalPipeRules.Count > 0)
            {
                var ans = MessageBox.Show(
                    "Mevcut yerel kuralların üzerine Ana kurallar kopyalansın mı?",
                    "Onay", MessageBoxButton.YesNo, MessageBoxImage.Question);
                if (ans != MessageBoxResult.Yes) return;
                _selectedTemplate.LocalPipeRules.Clear();
            }

            foreach (var pr in _masterCatalog.MasterPipeRules)
            {
                var cloned = new PipeRangeRule
                {
                    MinPipeMm            = pr.MinPipeMm,
                    MaxPipeMm            = pr.MaxPipeMm,
                    SelectedPipeFamilyId = pr.SelectedPipeFamilyId,
                    MinPipeId            = pr.MinPipeId,
                    MaxPipeId            = pr.MaxPipeId
                };
                foreach (var t in pr.DepthTiers)
                    cloned.DepthTiers.Add(new DepthTierRule
                    {
                        MinDepthM      = t.MinDepthM,
                        MaxDepthM      = t.MaxDepthM,
                        SelectedBaseId = t.SelectedBaseId,
                        IsCastInSitu   = t.IsCastInSitu,
                        Notes          = t.Notes
                    });
                _selectedTemplate.LocalPipeRules.Add(cloned);
            }

            LoadLocalPipeRanges();
        }

        // ── Local pipe-range CRUD ─────────────────────────────────────────────

        private void OnAddLocalPipeRange(object _)
        {
            if (_selectedTemplate == null) return;
            var rule = new PipeRangeRule();
            _selectedTemplate.LocalPipeRules.Add(rule);
            var vm = BuildLocalVm(rule);
            LocalPipeRanges.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedLocalPipeRange = vm));
        }

        private void OnDeleteLocalPipeRange(object _)
        {
            if (_selectedLocalPipeRange == null || _selectedTemplate == null) return;
            _selectedTemplate.LocalPipeRules.Remove(_selectedLocalPipeRange.Model);
            LocalPipeRanges.Remove(_selectedLocalPipeRange);
            SelectedLocalPipeRange = null;
        }

        private void OnDuplicateLocalPipeRange(object _)
        {
            if (_selectedLocalPipeRange == null || _selectedTemplate == null) return;
            var src   = _selectedLocalPipeRange.Model;
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

            int idx = _selectedTemplate.LocalPipeRules.IndexOf(src);
            _selectedTemplate.LocalPipeRules.Insert(idx + 1, clone);
            var vm = BuildLocalVm(clone);
            LocalPipeRanges.Insert(LocalPipeRanges.IndexOf(_selectedLocalPipeRange) + 1, vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedLocalPipeRange = vm));
        }

        // ── Local depth tier CRUD ─────────────────────────────────────────────

        private void OnAddLocalTier(object _)
        {
            if (_selectedLocalPipeRange == null) return;
            var tier = new DepthTierRule();
            _selectedLocalPipeRange.Model.DepthTiers.Add(tier);
            var vm = new DepthTierVm(tier, ResolveLocalBaseName);
            ResolveLocalBaseName(vm);
            _selectedLocalPipeRange.DepthTiers.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedLocalTier = vm));
        }

        private void OnDeleteLocalTier(object _)
        {
            if (_selectedLocalTier == null || _selectedLocalPipeRange == null) return;
            _selectedLocalPipeRange.Model.DepthTiers.Remove(_selectedLocalTier.Model);
            _selectedLocalPipeRange.DepthTiers.Remove(_selectedLocalTier);
            SelectedLocalTier = null;
        }

        // ── NOD persistence ───────────────────────────────────────────────────

        private void OnSaveToNod(object _)
        {
            if (_selectedTemplate == null) return;
            try
            {
                var db = AcadAppSvc.Application.DocumentManager.MdiActiveDocument?.Database;
                if (db == null)
                {
                    MessageBox.Show("Aktif bir DWG belgesi yok.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                _selectedTemplate.LastModified = DateTime.UtcNow;
                ProjectTemplateNodManager.SaveTemplate(db, _selectedTemplate);
                MessageBox.Show(
                    string.Format("Şablon '{0}' DWG'ye kaydedildi.", _selectedTemplate.TemplateName),
                    "Kayıt Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("NOD kayıt hatası:\n" + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnLoadFromNod(object _)
        {
            try
            {
                var db = AcadAppSvc.Application.DocumentManager.MdiActiveDocument?.Database;
                if (db == null)
                {
                    MessageBox.Show("Aktif bir DWG belgesi yok.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }
                var loaded = ProjectTemplateNodManager.LoadAllTemplates(db);
                Templates.Clear();
                SelectedTemplate = null;
                foreach (var t in loaded) Templates.Add(t);

                MessageBox.Show(
                    loaded.Count > 0
                        ? string.Format("{0} şablon DWG'den yüklendi.", loaded.Count)
                        : "Bu DWG'de kayıtlı şablon bulunamadı.",
                    "Yükleme", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (System.Exception ex)
            {
                MessageBox.Show("NOD yükleme hatası:\n" + ex.Message, "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private void LoadLocalPipeRanges()
        {
            LocalPipeRanges.Clear();
            SelectedLocalPipeRange = null;
            if (_selectedTemplate == null) return;
            foreach (var pr in _selectedTemplate.LocalPipeRules)
                LocalPipeRanges.Add(BuildLocalVm(pr));
        }

        public void SetPipeCatalog(PipeCatalog pipeCatalog)
        {
            _pipeCatalog = pipeCatalog;
            LoadLocalPipeRanges();
        }

        private PipeRangeRuleVm BuildLocalVm(PipeRangeRule model)
        {
            var vm = new PipeRangeRuleVm(model, _pipeCatalog);
            foreach (var t in model.DepthTiers)
            {
                var tvm = new DepthTierVm(t, ResolveLocalBaseName);
                ResolveLocalBaseName(tvm);
                vm.DepthTiers.Add(tvm);
            }
            return vm;
        }

        private void ResolveLocalBaseName(DepthTierVm vm)
        {
            if (vm.IsCastInSitu) { vm.BaseName = "Yerinde Döküm"; return; }
            var comp = _masterCatalog.FindById(vm.SelectedBaseId);
            vm.BaseName = comp != null ? comp.Name : (vm.SelectedBaseId == Guid.Empty ? "(seçilmedi)" : "?");
        }
    }
}
