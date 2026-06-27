using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
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
                Set(ref _selectedFamily, value);
                OnPropertyChanged(nameof(IsFamilySelected));
                OnPropertyChanged(nameof(PipesInFamily));
                OnPropertyChanged(nameof(SelectedFamilyName));
                OnPropertyChanged(nameof(SelectedFamilyMaterial));
            }
        }

        public bool IsFamilySelected => _selectedFamily != null;

        // ── Selected-family proxy properties (bound to right-panel TextBoxes) ─
        public string SelectedFamilyName
        {
            get => _selectedFamily?.FamilyName ?? "";
            set
            {
                if (_selectedFamily == null || _selectedFamily.FamilyName == value) return;
                _selectedFamily.FamilyName = value;
                OnPropertyChanged();
                UpdateStore();
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
                UpdateStore();
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

        public ICommand SaveCommand          { get; }
        public ICommand ExportCommand        { get; }
        public ICommand ImportCommand        { get; }

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
            _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

            AddFamilyCommand     = new RelayCommand(OnAddFamily);
            DeleteFamilyCommand  = new RelayCommand(OnDeleteFamily,   _ => IsFamilySelected);
            AddPipeCommand       = new RelayCommand(OnAddPipe,        _ => IsFamilySelected);
            DeletePipeCommand    = new RelayCommand(OnDeletePipe,     _ => IsPipeSelected);
            DuplicatePipeCommand = new RelayCommand(OnDuplicatePipe,  _ => IsPipeSelected);

            SaveCommand   = new RelayCommand(OnSave,   _ => Families.Count > 0);
            ExportCommand = new RelayCommand(OnExport, _ => Families.Count > 0);
            ImportCommand = new RelayCommand(OnImport);
        }

        // ── Family CRUD ───────────────────────────────────────────────────────

        private void OnAddFamily(object _)
        {
            var fam = new PipeFamily { FamilyName = GenerateDefaultFamilyName() };
            _catalog.Families.Add(fam);
            SelectedFamily = fam;
            UpdateStore();
            StatusText = $"'{fam.FamilyName}' ailesi eklendi.";
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
            UpdateStore();
        }

        // ── Pipe CRUD ─────────────────────────────────────────────────────────

        private void OnAddPipe(object _)
        {
            if (_selectedFamily == null) return;
            var pipe = new PipeDefinition { NominalDiameter = 100 };
            _selectedFamily.Pipes.Add(pipe);
            SelectedPipe = pipe;
            UpdateStore();
        }

        private void OnDeletePipe(object _)
        {
            if (_selectedPipe == null || _selectedFamily == null) return;
            _selectedFamily.Pipes.Remove(_selectedPipe);
            SelectedPipe = null;
            UpdateStore();
        }

        private void OnDuplicatePipe(object _)
        {
            if (_selectedPipe == null || _selectedFamily == null) return;
            var clone = new PipeDefinition
            {
                NominalDiameter = _selectedPipe.NominalDiameter,
                OuterDiameter   = _selectedPipe.OuterDiameter,
                InnerDiameter   = _selectedPipe.InnerDiameter,
                WallThickness   = _selectedPipe.WallThickness
            };
            _selectedFamily.Pipes.Add(clone);
            SelectedPipe = clone;
            UpdateStore();
        }

        // ── Save / Export / Import ────────────────────────────────────────────

        private void OnSave(object _)
        {
            if (!ConfirmIfInconsistent()) return;
            try
            {
                string path = DefaultSavePath;
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                PipeCatalogImportService.ExportNativeXml(_catalog, path);
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
                UpdateStore();
                StatusText = string.Format("{0} aile yüklendi.", _catalog.Families.Count);
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
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
            UpdateStore();
            StatusText = string.Format("DWG'den {0} aile çıkarıldı.", _catalog.Families.Count);
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static string DefaultSavePath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UrbanoMetraj", "PipeCatalog.xml");

        private void UpdateStore() => PipeCatalogStore.Current = _catalog;

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
