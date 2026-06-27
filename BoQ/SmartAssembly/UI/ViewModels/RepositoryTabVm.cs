using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    public class RepositoryTabVm : ViewModelBase
    {
        private readonly SmartAssemblyMasterCatalog _catalog;

        // ── Component list (DataGrid) ──────────────────────────────────────────
        public ObservableCollection<ComponentRowVm> Components { get; }
            = new ObservableCollection<ComponentRowVm>();

        private ComponentRowVm _selectedComponent;
        public ComponentRowVm SelectedComponent
        {
            get => _selectedComponent;
            set
            {
                Set(ref _selectedComponent, value);
                OnPropertyChanged(nameof(IsComponentSelected));
                OnPropertyChanged(nameof(IsBottomElementSelected));
                OnPropertyChanged(nameof(IsMiddleOrAdjusterSelected));
                OnPropertyChanged(nameof(IsReducerSelected));
                OnPropertyChanged(nameof(IsCoverSelected));
                LoadSubPieces();
            }
        }

        public bool IsComponentSelected        => _selectedComponent != null;
        public bool IsBottomElementSelected    => _selectedComponent?.Model is BottomElementComponent;
        public bool IsMiddleOrAdjusterSelected => _selectedComponent?.Model is MiddleElementComponent
                                               || _selectedComponent?.Model is AdjusterComponent;
        public bool IsReducerSelected          => _selectedComponent?.Model is ReducerComponent;
        public bool IsCoverSelected            => _selectedComponent?.Model is CoverComponent;

        // ── Role picker (toolbar) ─────────────────────────────────────────────
        public ComponentRole[] AllRoles { get; } = (ComponentRole[])Enum.GetValues(typeof(ComponentRole));

        public FootprintShapeItem[] AllFootprintShapes { get; } =
        {
            new FootprintShapeItem(FootprintShape.Circular,    "Dairesel"),
            new FootprintShapeItem(FootprintShape.Rectangular, "Dikdörtgen"),
            new FootprintShapeItem(FootprintShape.Square,      "Kare"),
        };

        private ComponentRole _newRole = ComponentRole.MiddleElement;
        public ComponentRole NewRole { get => _newRole; set { Set(ref _newRole, value); } }

        // ── Sub-Pieces (composite BottomElement) ──────────────────────────────
        public ObservableCollection<SubPieceRowVm> SubPieces { get; }
            = new ObservableCollection<SubPieceRowVm>();

        private SubPieceRowVm _selectedSubPiece;
        public SubPieceRowVm SelectedSubPiece
        {
            get => _selectedSubPiece;
            set { Set(ref _selectedSubPiece, value); OnPropertyChanged(nameof(IsSubPieceSelected)); }
        }

        public bool IsSubPieceSelected => _selectedSubPiece != null;

        // ── Commands ──────────────────────────────────────────────────────────
        public ICommand AddCommand            { get; }
        public ICommand DeleteCommand         { get; }
        public ICommand DuplicateCommand      { get; }
        public ICommand SaveCommand           { get; }   // Kaydet    → fixed AppData path, no dialog
        public ICommand ExportCommand         { get; }   // Dışa Aktar → user picks path
        public ICommand ImportCommand         { get; }   // İçe Aktar  → user picks file

        // Keep old names as aliases so XAML bindings in Tab 1 still compile
        public ICommand SaveCurrentCommand    => SaveCommand;
        public ICommand SaveXmlCommand        => ExportCommand;
        public ICommand LoadXmlCommand        => ImportCommand;

        public ICommand AddSubPieceCommand    { get; }
        public ICommand DeleteSubPieceCommand { get; }

        private readonly Action _onComponentsChanged;

        public RepositoryTabVm(SmartAssemblyMasterCatalog catalog,
                               Action onComponentsChanged = null)
        {
            _catalog              = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _onComponentsChanged  = onComponentsChanged;

            AddCommand            = new RelayCommand(OnAdd);
            DeleteCommand         = new RelayCommand(OnDelete,      _ => IsComponentSelected);
            DuplicateCommand      = new RelayCommand(OnDuplicate,   _ => IsComponentSelected);
            SaveCommand           = new RelayCommand(OnSave,        _ => _catalog.Components.Count > 0);
            ExportCommand         = new RelayCommand(OnExport,      _ => _catalog.Components.Count > 0);
            ImportCommand         = new RelayCommand(OnImport);
            AddSubPieceCommand    = new RelayCommand(OnAddSubPiece,    _ => IsBottomElementSelected);
            DeleteSubPieceCommand = new RelayCommand(OnDeleteSubPiece, _ => IsSubPieceSelected);

            // Auto-load from fixed AppData path (silent, like PipeCatalog)
            var path = MasterCatalogXmlManager.DefaultComponentsPath;
            if (File.Exists(path))
                TryLoadComponents(path, silent: true);
            else
                Reload();
        }

        public void Reload()
        {
            Components.Clear();
            foreach (var c in _catalog.Components)
                Components.Add(new ComponentRowVm(c));
        }

        // ── Component CRUD ────────────────────────────────────────────────────

        private void OnAdd(object _)
        {
            ManholeComponent comp;
            switch (_newRole)
            {
                case ComponentRole.BottomElement:
                    comp = new BottomElementComponent { Name = "Yeni Taban",        FamilyTag = "Standard-Precast" }; break;
                case ComponentRole.Reducer:
                    comp = new ReducerComponent       { Name = "Yeni Konik",        FamilyTag = "Standard-Precast" }; break;
                case ComponentRole.Adjuster:
                    comp = new AdjusterComponent      { Name = "Yeni Ayar Halkası", FamilyTag = "Standard-Precast" }; break;
                case ComponentRole.Cover:
                    comp = new CoverComponent         { Name = "Yeni Rögar Kapağı", LoadClass = "D400" };             break;
                default:
                    comp = new MiddleElementComponent { Name = "Yeni Halka",        FamilyTag = "Standard-Precast" }; break;
            }

            _catalog.Components.Add(comp);
            var vm = new ComponentRowVm(comp);
            Components.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => SelectedComponent = vm));
        }

        private void OnDelete(object _)
        {
            if (_selectedComponent == null) return;
            _catalog.Components.Remove(_selectedComponent.Model);
            Components.Remove(_selectedComponent);
            SelectedComponent = null;
        }

        private void OnDuplicate(object _)
        {
            if (_selectedComponent == null) return;
            var src = _selectedComponent.Model;

            ManholeComponent clone;
            var b = src as BottomElementComponent;
            if (b != null)
            {
                var cb = new BottomElementComponent
                {
                    Name                 = b.Name + " - Kopya",
                    EffectiveHeight      = b.EffectiveHeight,
                    FamilyTag            = b.FamilyTag,
                    ExternalVolume       = b.ExternalVolume,
                    MaterialVolume       = b.MaterialVolume,
                    WallThicknessMm      = b.WallThicknessMm,
                    TopOpeningDiameterMm = b.TopOpeningDiameterMm,
                    IsComposite          = b.IsComposite,
                    Footprint            = new Footprint
                    {
                        Shape      = b.Footprint?.Shape ?? FootprintShape.Circular,
                        DiameterMm = b.Footprint?.DiameterMm ?? 0,
                        LengthMm   = b.Footprint?.LengthMm   ?? 0,
                        WidthMm    = b.Footprint?.WidthMm    ?? 0
                    }
                };
                foreach (var sp in b.SubPieces)
                    cb.SubPieces.Add(new SubPiece
                        { Name = sp.Name, HeightMm = sp.HeightMm, Description = sp.Description });
                clone = cb;
            }
            else
            {
                var m = src as MiddleElementComponent;
                if (m != null)
                    clone = new MiddleElementComponent
                    {
                        Name = m.Name + " - Kopya", EffectiveHeight = m.EffectiveHeight,
                        FamilyTag = m.FamilyTag, ExternalVolume = m.ExternalVolume,
                        MaterialVolume = m.MaterialVolume, WallThicknessMm = m.WallThicknessMm,
                        InnerDiameterMm = m.InnerDiameterMm
                    };
                else
                {
                    var r = src as ReducerComponent;
                    if (r != null)
                        clone = new ReducerComponent
                        {
                            Name = r.Name + " - Kopya", EffectiveHeight = r.EffectiveHeight,
                            FamilyTag = r.FamilyTag, ExternalVolume = r.ExternalVolume,
                            MaterialVolume = r.MaterialVolume, WallThicknessMm = r.WallThicknessMm,
                            BottomInnerDiameterMm = r.BottomInnerDiameterMm,
                            TopInnerDiameterMm    = r.TopInnerDiameterMm
                        };
                    else
                    {
                        var a = src as AdjusterComponent;
                        if (a != null)
                            clone = new AdjusterComponent
                            {
                                Name = a.Name + " - Kopya", EffectiveHeight = a.EffectiveHeight,
                                FamilyTag = a.FamilyTag, ExternalVolume = a.ExternalVolume,
                                MaterialVolume = a.MaterialVolume, WallThicknessMm = a.WallThicknessMm,
                                InnerDiameterMm = a.InnerDiameterMm
                            };
                        else
                        {
                            var cv = src as CoverComponent;
                            if (cv == null) return;
                            clone = new CoverComponent
                            {
                                Name = cv.Name + " - Kopya", EffectiveHeight = cv.EffectiveHeight,
                                FamilyTag = cv.FamilyTag, ExternalVolume = cv.ExternalVolume,
                                MaterialVolume = cv.MaterialVolume,
                                LoadClass = cv.LoadClass, ClearOpeningMm = cv.ClearOpeningMm
                            };
                        }
                    }
                }
            }

            _catalog.Components.Add(clone);
            var vm = new ComponentRowVm(clone);
            Components.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedComponent = vm));
        }

        // ── Sub-Piece CRUD ────────────────────────────────────────────────────

        private Action SubPieceHeightCallback() =>
            () => _selectedComponent?.RecalcEffectiveHeight();

        private void LoadSubPieces()
        {
            SubPieces.Clear();
            SelectedSubPiece = null;
            var b = _selectedComponent?.Model as BottomElementComponent;
            if (b == null) return;
            var cb = SubPieceHeightCallback();
            foreach (var sp in b.SubPieces)
                SubPieces.Add(new SubPieceRowVm(sp, cb));
        }

        private void OnAddSubPiece(object _)
        {
            var b = _selectedComponent?.Model as BottomElementComponent;
            if (b == null) return;
            var sp = new SubPiece { Name = "Yeni Parça " + (b.SubPieces.Count + 1), HeightMm = 0 };
            b.SubPieces.Add(sp);
            var vm = new SubPieceRowVm(sp, SubPieceHeightCallback());
            SubPieces.Add(vm);
            _selectedComponent.RecalcEffectiveHeight();
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => SelectedSubPiece = vm));
        }

        private void OnDeleteSubPiece(object _)
        {
            if (_selectedSubPiece == null) return;
            var b = _selectedComponent?.Model as BottomElementComponent;
            b?.SubPieces.Remove(_selectedSubPiece.Model);
            SubPieces.Remove(_selectedSubPiece);
            SelectedSubPiece = null;
            _selectedComponent?.RecalcEffectiveHeight();
        }

        // ── XML persistence (PipeCatalog pattern) ────────────────────────────

        private void TryLoadComponents(string path, bool silent)
        {
            try
            {
                var comps = MasterCatalogXmlManager.ImportComponents(path);
                _catalog.Components.Clear();
                foreach (var c in comps) _catalog.Components.Add(c);
                Reload();
                _onComponentsChanged?.Invoke();
                if (!silent)
                    MessageBox.Show(
                        string.Format("{0} bileşen yüklendi.", comps.Count),
                        "İçe Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("Bileşen yükleme hatası:\n" + ex.Message,
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
                else
                    Reload();
            }
        }

        private void OnSave(object _)
        {
            try
            {
                MasterCatalogXmlManager.ExportComponents(_catalog,
                    MasterCatalogXmlManager.DefaultComponentsPath);
                StatusText = "Bileşenler kaydedildi.";
            }
            catch (Exception ex) { ShowError("Kayıt hatası", ex); }
        }

        private void OnExport(object _)
        {
            var path = PickSavePath("Bileşen Kataloğunu Dışa Aktar",
                                   "SmartAssemblyComponents.xml");
            if (path == null) return;
            try
            {
                MasterCatalogXmlManager.ExportComponents(_catalog, path);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(path);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            var path = PickOpenPath("Bileşen Kataloğu İçe Aktar");
            if (path == null) return;
            TryLoadComponents(path, silent: false);
        }

        // ── Status bar text ───────────────────────────────────────────────────
        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Helpers ───────────────────────────────────────────────────────────
        private static string PickOpenPath(string title)
        {
            var dlg = new OpenFileDialog
                { Title = title, Filter = "Smart Assembly XML (*.xml)|*.xml" };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static string PickSavePath(string title, string defaultName)
        {
            var dlg = new SaveFileDialog
                { Title = title, Filter = "Smart Assembly XML (*.xml)|*.xml",
                  DefaultExt = ".xml", FileName = defaultName };
            return dlg.ShowDialog() == true ? dlg.FileName : null;
        }

        private static void ShowError(string ctx, Exception ex)
            => MessageBox.Show(ctx + ":\n" + ex.Message, "Hata",
                               MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Label+value pair for the Plan Şekli ComboBox so display text is in Turkish.</summary>
    public sealed class FootprintShapeItem
    {
        public FootprintShape Shape   { get; }
        public string         Display { get; }
        public FootprintShapeItem(FootprintShape shape, string display) { Shape = shape; Display = display; }
    }
}
