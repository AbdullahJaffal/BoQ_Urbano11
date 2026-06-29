using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.DolguCatalog.Models;
using UrbanoMetraj.BoQ.DolguCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.DolguCatalog.UI.ViewModels
{
    // =========================================================================
    // DolguMalzemesiVm – row VM
    // =========================================================================

    public sealed class DolguMalzemesiVm : ViewModelBase
    {
        private readonly DolguMalzemesi _model;

        public DolguMalzemesiVm(DolguMalzemesi model)
            => _model = model ?? throw new ArgumentNullException(nameof(model));

        public DolguMalzemesi Model => _model;

        public string BoqItemCode
        {
            get => _model.BoqItemCode;
            set { _model.BoqItemCode = value; OnPropertyChanged(); }
        }

        public string DolguAdi
        {
            get => _model.DolguAdi;
            set { _model.DolguAdi = value; OnPropertyChanged(); }
        }

        public string Aciklama
        {
            get => _model.Aciklama;
            set { _model.Aciklama = value; OnPropertyChanged(); }
        }
    }

    // =========================================================================
    // DolguCatalogVm – main ViewModel
    // =========================================================================

    public sealed class DolguCatalogVm : ViewModelBase
    {
        private DolguMalzemesiVm _selected;

        public ObservableCollection<DolguMalzemesiVm> Items { get; }
            = new ObservableCollection<DolguMalzemesiVm>();

        public DolguMalzemesiVm SelectedItem
        {
            get => _selected;
            set
            {
                Set(ref _selected, value);
                OnPropertyChanged(nameof(IsItemSelected));
            }
        }

        public bool IsItemSelected => _selected != null;

        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        public ICommand AddCommand       { get; }
        public ICommand DuplicateCommand { get; }
        public ICommand DeleteCommand    { get; }
        public ICommand SaveCommand      { get; }
        public ICommand ExportCommand    { get; }
        public ICommand ImportCommand    { get; }

        public DolguCatalogVm()
        {
            AddCommand       = new RelayCommand(OnAdd);
            DuplicateCommand = new RelayCommand(OnDuplicate, _ => IsItemSelected);
            DeleteCommand    = new RelayCommand(OnDelete,    _ => IsItemSelected);
            SaveCommand      = new RelayCommand(OnSave,      _ => Items.Count > 0);
            ExportCommand    = new RelayCommand(OnExport,    _ => Items.Count > 0);
            ImportCommand    = new RelayCommand(OnImport);

            try
            {
                foreach (var d in DolguCatalogXmlManager.LoadInternal())
                    Items.Add(new DolguMalzemesiVm(d));
                if (Items.Count > 0)
                    StatusText = $"{Items.Count} dolgu malzemesi yüklendi.";
            }
            catch (Exception ex)
            {
                StatusText = "Otomatik yükleme hatası: " + ex.Message;
            }

            // Push loaded models into the shared store so PipeTrench ComboBoxes
            // reflect the same objects — edits to existing rows are then live.
            SyncToStore();
        }

        private void OnAdd(object _)
        {
            var item = new DolguMalzemesi { DolguAdi = "Yeni Dolgu" };
            var vm   = new DolguMalzemesiVm(item);
            Items.Add(vm);
            SelectedItem = vm;
            StatusText = "Yeni dolgu malzemesi eklendi.";
            SyncToStore();
        }

        private void OnDuplicate(object _)
        {
            if (_selected == null) return;
            var src   = _selected.Model;
            var clone = new DolguMalzemesi
            {
                BoqItemCode = src.BoqItemCode,
                DolguAdi    = src.DolguAdi,
                Aciklama    = src.Aciklama
            };
            var vm = new DolguMalzemesiVm(clone);
            Items.Add(vm);
            SelectedItem = vm;
            StatusText = "Dolgu malzemesi çoğaltıldı.";
            SyncToStore();
        }

        private void OnDelete(object _)
        {
            if (_selected == null) return;
            Items.Remove(_selected);
            SelectedItem = null;
            StatusText = "Dolgu malzemesi silindi.";
            SyncToStore();
        }

        private void OnSave(object _)
        {
            try
            {
                DolguCatalogXmlManager.SaveInternal(GetModels());
                StatusText = $"Kaydedildi → {DolguCatalogXmlManager.InternalPath}";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            var dlg = new SaveFileDialog
            {
                Filter   = "Dolgu Kataloğu XML (*.xml)|*.xml",
                Title    = "Dolgu Kataloğunu Dışa Aktar",
                FileName = "DolguCatalog.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                DolguCatalogXmlManager.Export(GetModels(), dlg.FileName);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(dlg.FileName);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            var dlg = new OpenFileDialog
            {
                Filter = "Dolgu Kataloğu XML (*.xml)|*.xml",
                Title  = "Dolgu Kataloğu İçe Aktar"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var loaded = DolguCatalogXmlManager.Import(dlg.FileName);
                if (loaded.Count == 0) { StatusText = "Dosyada kayıt bulunamadı."; return; }

                var ans = MessageBox.Show(
                    $"{loaded.Count} kayıt bulundu.\nMevcut liste silinsin mi?",
                    "İçe Aktar", MessageBoxButton.YesNo, MessageBoxImage.Question);

                if (ans == MessageBoxResult.Yes) Items.Clear();

                foreach (var d in loaded) Items.Add(new DolguMalzemesiVm(d));
                SelectedItem = null;
                StatusText = $"İçe aktarıldı: {loaded.Count} kayıt.";
                SyncToStore();
            }
            catch (Exception ex) { ShowError("İçe aktarma hatası", ex); }
        }

        /// <summary>
        /// Mirrors the current items into DolguCatalogStore so that any ComboBox
        /// bound to TrenchLayerVm.AvailableMaterials updates without a restart.
        /// Mutates the store's existing collection in-place (Clear + Add) to
        /// preserve the binding reference held by already-rendered ComboBoxes.
        /// </summary>
        private void SyncToStore()
        {
            var store = DolguCatalogStore.Items;
            store.Clear();
            foreach (var vm in Items)
                store.Add(vm.Model);
        }

        private System.Collections.Generic.IEnumerable<DolguMalzemesi> GetModels()
        {
            foreach (var vm in Items) yield return vm.Model;
        }

        private static void ShowError(string context, Exception ex)
            => MessageBox.Show(context + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
