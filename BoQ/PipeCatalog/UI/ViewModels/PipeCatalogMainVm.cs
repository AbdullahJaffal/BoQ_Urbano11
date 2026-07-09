using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.ExcelImport;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels
{
    public class PipeCatalogMainVm : ViewModelBase
    {
        private readonly PipeCatalog _catalog;

        // ── Family list ───────────────────────────────────────────────────────
        public ObservableCollection<PipeFamily> Families => _catalog.Families;

        private PipeFamily _selectedFamily;
        public PipeFamily SelectedFamily
        {
            get => _selectedFamily;
            set
            {
                // Unsubscribe from old family's pipe changes before switching.
                UnwirePipes(_selectedFamily);
                Set(ref _selectedFamily, value);
                // Subscribe to new family's pipe changes.
                WirePipes(_selectedFamily);
                OnPropertyChanged(nameof(IsFamilySelected));
                OnPropertyChanged(nameof(PipesInFamily));
                OnPropertyChanged(nameof(SelectedFamilyName));
                OnPropertyChanged(nameof(SelectedFamilyMaterial));
            }
        }

        public bool IsFamilySelected => _selectedFamily != null;

        // Catalog-wide shared class list — bound as ItemsSource of the Sınıf ComboBox.
        public System.Collections.ObjectModel.ObservableCollection<string> CatalogClasses
            => _catalog.Classes;

        // ── Selected-family proxy properties (bound to right-panel TextBoxes) ─
        public string SelectedFamilyName
        {
            get => _selectedFamily?.FamilyName ?? "";
            set
            {
                if (_selectedFamily == null || _selectedFamily.FamilyName == value) return;
                _selectedFamily.FamilyName = value;
                OnPropertyChanged();
                MarkDirty();
            }
        }

        public string SelectedFamilyMaterial
        {
            get => _selectedFamily?.Material ?? "";
            set
            {
                if (_selectedFamily == null || _selectedFamily.Material == value) return;
                _selectedFamily.Material = value;
                OnPropertyChanged();
                MarkDirty();
            }
        }

        public ObservableCollection<PipeDefinition> PipesInFamily =>
            _selectedFamily?.Pipes ?? _emptyPipes;

        private static readonly ObservableCollection<PipeDefinition> _emptyPipes
            = new ObservableCollection<PipeDefinition>();

        // ── Status bar ────────────────────────────────────────────────────────
        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand AddFamilyCommand     { get; }
        public ICommand DeleteFamilyCommand  { get; }
        public ICommand AddPipeCommand       { get; }
        public ICommand DeletePipeCommand    { get; }
        public ICommand DuplicatePipeCommand { get; }

        public ICommand SaveCommand            { get; }
        public ICommand ExportCommand          { get; }
        public ICommand ExportExcelCommand     { get; }
        public ICommand ImportCommand          { get; }
        public ICommand ImportExcelCommand     { get; }
        public ICommand ManageClassesCommand   { get; }

        // ── Selected pipe ─────────────────────────────────────────────────────
        private PipeDefinition _selectedPipe;
        public PipeDefinition SelectedPipe
        {
            get => _selectedPipe;
            set { Set(ref _selectedPipe, value); OnPropertyChanged(nameof(IsPipeSelected)); }
        }

        public bool IsPipeSelected => _selectedPipe != null;

        // ── Constructor ───────────────────────────────────────────────────────
        public PipeCatalogMainVm(PipeCatalog catalog)
        {
            // Work on a DEEP COPY — changes stay local until the user clicks Kaydet.
            _catalog = DeepClone(catalog ?? new PipeCatalog());

            // Track family-level property changes (FamilyName / Material edited in the left grid).
            _catalog.Families.CollectionChanged += OnFamiliesChanged;
            foreach (var f in _catalog.Families)
                f.PropertyChanged += OnModelChanged;

            AddFamilyCommand     = new RelayCommand(OnAddFamily);
            DeleteFamilyCommand  = new RelayCommand(OnDeleteFamily,   _ => IsFamilySelected);
            AddPipeCommand       = new RelayCommand(OnAddPipe,        _ => IsFamilySelected);
            DeletePipeCommand    = new RelayCommand(OnDeletePipe,     _ => IsPipeSelected);
            DuplicatePipeCommand = new RelayCommand(OnDuplicatePipe,  _ => IsPipeSelected);

            SaveCommand          = new RelayCommand(OnSave,          _ => _isDirty);
            ExportCommand        = new RelayCommand(OnExport,        _ => Families.Count > 0);
            ExportExcelCommand   = new RelayCommand(OnExportExcel,   _ => Families.Count > 0);
            ImportCommand        = new RelayCommand(OnImport);
            ImportExcelCommand   = new RelayCommand(OnImportExcel,    _ => IsFamilySelected);
            ManageClassesCommand = new RelayCommand(OnManageClasses);
        }

        // ── Family CRUD ───────────────────────────────────────────────────────

        private void OnAddFamily(object _)
        {
            var fam = new PipeFamily { FamilyName = GenerateDefaultFamilyName() };
            _catalog.Families.Add(fam);
            SelectedFamily = fam;
            MarkDirty();
        }

        private string GenerateDefaultFamilyName()
        {
            int i = 1;
            while (_catalog.Families.Any(f => f.FamilyName == "Yeni Aile " + i))
                i++;
            return "Yeni Aile " + i;
        }

        private void OnDeleteFamily(object _)
        {
            if (_selectedFamily == null) return;
            _catalog.Families.Remove(_selectedFamily);
            SelectedFamily = null;
            MarkDirty();
        }

        // ── Pipe CRUD ─────────────────────────────────────────────────────────

        private void OnAddPipe(object _)
        {
            if (_selectedFamily == null) return;
            var pipe = new PipeDefinition { NominalDiameter = 100 };
            _selectedFamily.Pipes.Add(pipe);
            SelectedPipe = pipe;
            MarkDirty();
        }

        private void OnDeletePipe(object _)
        {
            if (_selectedPipe == null || _selectedFamily == null) return;
            _selectedFamily.Pipes.Remove(_selectedPipe);
            SelectedPipe = null;
            MarkDirty();
        }

        private void OnDuplicatePipe(object _)
        {
            if (_selectedPipe == null || _selectedFamily == null) return;
            var clone = new PipeDefinition
            {
                PozNo           = _selectedPipe.PozNo,
                NominalDiameter = _selectedPipe.NominalDiameter,
                OuterDiameter   = _selectedPipe.OuterDiameter,
                InnerDiameter   = _selectedPipe.InnerDiameter,
                WallThickness   = _selectedPipe.WallThickness,
                Sinif           = _selectedPipe.Sinif,
                Aciklama        = _selectedPipe.Aciklama
            };
            _selectedFamily.Pipes.Add(clone);
            SelectedPipe = clone;
            MarkDirty();
        }

        // ── Class manager ─────────────────────────────────────────────────────

        // Raised so the View can open the dialog (VM stays UI-agnostic for the Window ref).
        public event System.EventHandler OpenClassManagerRequested;

        private void OnManageClasses(object _)
            => OpenClassManagerRequested?.Invoke(this, System.EventArgs.Empty);

        // ── Save / Export / Import ────────────────────────────────────────────

        private void OnSave(object _)
        {
            if (!ConfirmIfInconsistent()) return;
            try
            {
                string path = DefaultSavePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                PipeCatalogImportService.ExportNativeXml(_catalog, path);
                UpdateStore();   // ← only here: commit working copy to live store
                _isDirty = false;
                StatusText = "Katalog kaydedildi.";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            if (!ConfirmIfInconsistent()) return;
            string path = SaveFile("PipeCatalog XML (*.xml)|*.xml",
                                   "Kataloğu Dışa Aktar", "PipeCatalog.xml");
            if (path == null) return;
            try
            {
                PipeCatalogImportService.ExportNativeXml(_catalog, path);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(path);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnExportExcel(object _)
        {
            if (!ConfirmIfInconsistent()) return;
            string path = SaveFile("Excel (*.xlsx)|*.xlsx",
                                   "Kataloğu Excel'e Aktar", "BoruKatalogu.xlsx");
            if (path == null) return;
            try
            {
                PipeCatalogExcelExportService.Export(_catalog, path);
                StatusText = "Excel'e aktarıldı: " + Path.GetFileName(path);
            }
            catch (Exception ex) { ShowError("Excel'e aktarma hatası", ex); }
        }

        // Returns true if it is safe to proceed (no inconsistencies, or user confirmed).
        private bool ConfirmIfInconsistent()
        {
            int count = 0;
            foreach (var fam in _catalog.Families)
                foreach (var pipe in fam.Pipes)
                    if (!pipe.IsConsistent) count++;

            if (count == 0) return true;

            var ans = MessageBox.Show(
                string.Format(
                    "{0} boru satırında OD ≠ ID + 2×Et tutarsızlığı var " +
                    "(kırmızı satırlar).\n\nYine de devam etmek istiyor musunuz?",
                    count),
                "Tutarsız Boru Verileri",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            return ans == MessageBoxResult.Yes;
        }

        private void OnImport(object _)
        {
            string path = PickFile("PipeCatalog XML (*.xml)|*.xml", "Katalog İçe Aktar");
            if (path == null) return;
            try
            {
                // Auto-detect format: try native schema first, fall back to Urbano schema
                PipeCatalog loaded;
                try   { loaded = PipeCatalogImportService.ImportNativeXml(path); }
                catch { loaded = PipeCatalogImportService.ImportUrbanoXml(path); }

                if (loaded.Families.Count == 0)
                {
                    StatusText = "İçe aktarılan katalog boş.";
                    return;
                }

                var ans = MessageBox.Show(
                    string.Format("{0} aile bulundu.\n\nMevcut kataloğun üzerine yazılsın mı?",
                                  loaded.Families.Count),
                    "İçe Aktar", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ans != MessageBoxResult.Yes) return;

                _catalog.Families.Clear();
                foreach (var f in loaded.Families)
                    _catalog.Families.Add(f);

                SelectedFamily = null;
                MarkDirty();
                StatusText = string.Format("{0} aile yüklendi — kaydetmek için Kaydet'e basın.",
                                           _catalog.Families.Count);
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
        }

        private void OnImportExcel(object _)
        {
            if (_selectedFamily == null)
            {
                MessageBox.Show("Önce içe aktarılacak boruların ekleneceği aileyi seçin veya oluşturun.",
                    "Aile Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            string path = PickFile("Excel (*.xlsx)|*.xlsx", "Excel'den Boru İçe Aktar");
            if (path == null) return;

            try
            {
                string[] headers;
                System.Collections.Generic.List<string[]> rows;
                ExcelSheetReader.ReadFirstSheet(path, out headers, out rows);

                if (rows.Count == 0)
                {
                    StatusText = "Excel dosyasında veri satırı bulunamadı.";
                    return;
                }

                var dlg = new ColumnMappingDialog(Path.GetFileName(path), headers, rows,
                    PipeCatalogExcelImportService.Fields, Application.Current?.MainWindow);
                if (dlg.ShowDialog() != true) return;

                var pipes = PipeCatalogExcelImportService.Build(rows, dlg.Result);
                if (pipes.Count == 0)
                {
                    StatusText = "Eşleştirilen sütunlardan hiç boru satırı üretilemedi.";
                    return;
                }

                foreach (var p in pipes)
                    _selectedFamily.Pipes.Add(p);

                MarkDirty();
                StatusText = string.Format("Excel'den {0} boru \"{1}\" ailesine eklendi — kaydetmek için Kaydet'e basın.",
                                           pipes.Count, _selectedFamily.FamilyName);
            }
            catch (Exception ex) { ShowError("Excel içe aktarma hatası", ex); }
        }

        // ── Called by PipeCatalogCommand after live extraction completes ──────
        public void ApplyExtractedCatalog(PipeCatalog extracted)
        {
            if (extracted.Families.Count == 0)
            {
                StatusText = "Canlı çıkarma: katalog boş.";
                return;
            }
            _catalog.Families.Clear();
            foreach (var f in extracted.Families)
                _catalog.Families.Add(f);
            SelectedFamily = null;
            MarkDirty();
            StatusText = string.Format("DWG'den {0} aile çıkarıldı — kaydetmek için Kaydet'e basın.",
                                       _catalog.Families.Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private bool _isDirty;

        private void MarkDirty()
        {
            _isDirty   = true;
            StatusText = "● Kaydedilmemiş değişiklikler var — Kaydet tuşuna basın.";
        }

        // ── Change-tracking subscriptions ────────────────────────────────────

        // Fired when families are added/removed from the catalog.
        private void OnFamiliesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (PipeFamily f in e.NewItems) f.PropertyChanged += OnModelChanged;
            if (e.OldItems != null)
                foreach (PipeFamily f in e.OldItems) f.PropertyChanged -= OnModelChanged;
        }

        // Fired when pipes are added/removed from the selected family.
        private void OnPipesChanged(object sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems != null)
                foreach (PipeDefinition p in e.NewItems) p.PropertyChanged += OnModelChanged;
            if (e.OldItems != null)
                foreach (PipeDefinition p in e.OldItems) p.PropertyChanged -= OnModelChanged;
        }

        // Fired when any tracked model property changes — enables Kaydet immediately.
        private void OnModelChanged(object sender, PropertyChangedEventArgs e) => MarkDirty();

        private void WirePipes(PipeFamily family)
        {
            if (family == null) return;
            family.Pipes.CollectionChanged += OnPipesChanged;
            foreach (var p in family.Pipes) p.PropertyChanged += OnModelChanged;
        }

        private void UnwirePipes(PipeFamily family)
        {
            if (family == null) return;
            family.Pipes.CollectionChanged -= OnPipesChanged;
            foreach (var p in family.Pipes) p.PropertyChanged -= OnModelChanged;
        }

        // ─────────────────────────────────────────────────────────────────────

        private static string DefaultSavePath => PipeCatalogStore.DefaultSavePath;

        private void UpdateStore()
        {
            PipeCatalogStore.Current = _catalog;
            // Refresh sibling tabs (rules, project setup) without touching PipeCatalogTab
            // itself — passing the same object to ApplyExtractedCatalog would Clear() and
            // re-iterate the same collection, wiping all families.
            SmartAssemblyCommand.GetMainVm()?.RefreshPipeCatalog(_catalog, updatePipeCatalogTab: false);
        }

        private static PipeCatalog DeepClone(PipeCatalog src)
        {
            var dst = new PipeCatalog { LastModified = src.LastModified };
            foreach (var cls in src.Classes)
                dst.Classes.Add(cls);
            foreach (var fam in src.Families)
            {
                var fc = new PipeFamily
                {
                    Id         = fam.Id,
                    FamilyName = fam.FamilyName,
                    Material   = fam.Material
                };
                foreach (var p in fam.Pipes)
                    fc.Pipes.Add(new PipeDefinition
                    {
                        Id              = p.Id,
                        PozNo           = p.PozNo,
                        NominalDiameter = p.NominalDiameter,
                        OuterDiameter   = p.OuterDiameter,
                        InnerDiameter   = p.InnerDiameter,
                        WallThickness   = p.WallThickness,
                        Sinif           = p.Sinif,
                        Aciklama        = p.Aciklama
                    });
                dst.Families.Add(fc);
            }
            return dst;
        }

        private static string PickFile(string filter, string title)
        {
            var dlg = new OpenFileDialog { Filter = filter, Title = title };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string SaveFile(string filter, string title, string defaultName)
        {
            var dlg = new SaveFileDialog { Filter = filter, Title = title, FileName = defaultName };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static void ShowError(string context, Exception ex)
            => MessageBox.Show(context + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
