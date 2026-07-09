using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.SoilCatalog.Models;
using UrbanoMetraj.BoQ.SoilCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.SoilCatalog.UI.ViewModels
{
    // =========================================================================
    // SoilClassificationVm – row VM for one SoilClassification
    // =========================================================================

    public sealed class SoilClassificationVm : ViewModelBase
    {
        private readonly SoilClassification _model;

        public SoilClassificationVm(SoilClassification model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public SoilClassification Model => _model;

        public string SoilName
        {
            get => _model.SoilName;
            set { _model.SoilName = value; OnPropertyChanged(); }
        }

        public double BulkingFactor
        {
            get => _model.BulkingFactor;
            set { _model.BulkingFactor = value; OnPropertyChanged(); }
        }

        public string BoqItemCode
        {
            get => _model.BoqItemCode;
            set { _model.BoqItemCode = value; OnPropertyChanged(); }
        }

        public string KaziTipi
        {
            get => _model.KaziTipi;
            set { _model.KaziTipi = value; OnPropertyChanged(); }
        }

        public string Aciklama
        {
            get => _model.Aciklama;
            set { _model.Aciklama = value; OnPropertyChanged(); }
        }
    }

    // =========================================================================
    // SoilCatalogVm – main ViewModel
    // =========================================================================

    public sealed class SoilCatalogVm : ViewModelBase
    {
        private SoilClassificationVm _selected;

        public ObservableCollection<SoilClassificationVm> Items { get; }
            = new ObservableCollection<SoilClassificationVm>();

        // ── Selection ─────────────────────────────────────────────────────────

        public SoilClassificationVm SelectedItem
        {
            get => _selected;
            set
            {
                Set(ref _selected, value);
                OnPropertyChanged(nameof(IsItemSelected));
            }
        }

        public bool IsItemSelected => _selected != null;

        // ── Status bar ────────────────────────────────────────────────────────

        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Commands ──────────────────────────────────────────────────────────

        public ICommand AddCommand       { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand    { get; }
        public ICommand SaveCommand      { get; }
        public ICommand ExportCommand    { get; }
        public ICommand ImportCommand    { get; }

        // ── Constructor ───────────────────────────────────────────────────────

        public SoilCatalogVm()
        {
            AddCommand       = new RelayCommand(OnAdd);
            DuplicateCommand = new RelayCommand(OnDuplicate, _ => IsItemSelected);
            DeleteCommand    = new RelayCommand(OnDelete,    _ => IsItemSelected);
            SaveCommand      = new RelayCommand(OnSave,      _ => Items.Count > 0);
            ExportCommand    = new RelayCommand(OnExport,    _ => Items.Count > 0);
            ImportCommand    = new RelayCommand(OnImport);

            // Auto-load from internal path on construction
            try
            {
                foreach (var s in SoilCatalogXmlManager.LoadInternal())
                    Items.Add(new SoilClassificationVm(s));
                if (Items.Count > 0)
                    StatusText = $"{Items.Count} zemin sınıfı yüklendi.";
            }
            catch (Exception ex)
            {
                StatusText = "Otomatik yükleme hatası: " + ex.Message;
            }
        }

        // ── CRUD ──────────────────────────────────────────────────────────────

        private void OnAdd(object _)
        {
            var item = new SoilClassification { SoilName = "Yeni Zemin", BulkingFactor = 1.20 };
            var vm   = new SoilClassificationVm(item);
            Items.Add(vm);
            SelectedItem = vm;
            StatusText = "Yeni zemin sınıfı eklendi.";
        }

        private void OnDuplicate(object _)
        {
            if (_selected == null) return;
            var src   = _selected.Model;
            var clone = new SoilClassification
            {
                SoilName      = src.SoilName,
                BulkingFactor = src.BulkingFactor,
                BoqItemCode   = src.BoqItemCode,
                KaziTipi      = src.KaziTipi,
                Aciklama      = src.Aciklama
            };
            var vm = new SoilClassificationVm(clone);
            Items.Add(vm);
            SelectedItem = vm;
            StatusText = "Zemin sınıfı çoğaltıldı.";
        }

        private void OnDelete(object _)
        {
            if (_selected == null) return;
            Items.Remove(_selected);
            SelectedItem = null;
            StatusText = "Zemin sınıfı silindi.";
        }

        // ── Persistence ───────────────────────────────────────────────────────

        private void OnSave(object _)
        {
            try
            {
                SoilCatalogXmlManager.SaveInternal(GetModels());
                StatusText = $"Kaydedildi → {SoilCatalogXmlManager.InternalPath}";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            var dlg = new SaveFileDialog
            {
                Filter   = "Zemin Kataloğu XML (*.xml)|*.xml",
                Title    = "Zemin Kataloğunu Dışa Aktar",
                FileName = "SoilCatalog.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                SoilCatalogXmlManager.Export(GetModels(), dlg.FileName);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Zemin Kataloğu XML (*.xml)|*.xml",
                Title  = "Zemin Kataloğu İçe Aktar"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var loaded = SoilCatalogXmlManager.Import(dlg.FileName);
                if (loaded.Count == 0) { StatusText = "Dosyada kayıt bulunamadı."; return; }

                var ans = MessageBox.Show(
                    $"{loaded.Count} kayıt bulundu.\nMevcut liste silinsin mi?",
                    "İçe Aktar", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ans == MessageBoxResult.Yes) Items.Clear();

                foreach (var s in loaded) Items.Add(new SoilClassificationVm(s));
                SelectedItem = null;
                StatusText = $"İçe aktarıldı: {loaded.Count} kayıt.";
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private System.Collections.Generic.IEnumerable<SoilClassification> GetModels()
        {
            foreach (var vm in Items) yield return vm.Model;
        }

        private static void ShowError(string context, Exception ex)
            => MessageBox.Show(context + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
