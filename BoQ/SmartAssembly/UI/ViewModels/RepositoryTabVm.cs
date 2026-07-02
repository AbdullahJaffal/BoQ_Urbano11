using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;
using UrbanoMetraj.BoQ.SmartAssembly.Services;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    public class RepositoryTabVm : ViewModelBase
    {
        private readonly SmartAssemblyMasterCatalog _catalog;
        private readonly Action _onComponentsChanged;

        // ── Left panel — family list ───────────────────────────────────────────
        public ObservableCollection<ComponentFamily> Families => _catalog.Families;

        private ComponentFamily _selectedFamily;
        public ComponentFamily SelectedFamily
        {
            get => _selectedFamily;
            set
            {
                Set(ref _selectedFamily, value);
                OnPropertyChanged(nameof(IsFamilySelected));
                ReloadComponents();
            }
        }

        public bool IsFamilySelected => _selectedFamily != null;

        // ── Middle panel — component list ──────────────────────────────────────
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

        // ── Role picker ────────────────────────────────────────────────────────
        public ComponentRoleItem[] AllRoles { get; } =
        {
            new ComponentRoleItem(ComponentRole.BottomElement, "Taban"),
            new ComponentRoleItem(ComponentRole.MiddleElement, "Gövde Halkası"),
            new ComponentRoleItem(ComponentRole.Reducer,       "Konik"),
            new ComponentRoleItem(ComponentRole.Adjuster,      "Boyun bileziği"),
            new ComponentRoleItem(ComponentRole.Cover,         "Rögar Kapağı"),
        };

        private ComponentRole _newRole = ComponentRole.MiddleElement;
        public ComponentRole NewRole { get => _newRole; set { Set(ref _newRole, value); } }

        // ── Sub-Pieces ─────────────────────────────────────────────────────────
        public ObservableCollection<SubPieceRowVm> SubPieces { get; }
            = new ObservableCollection<SubPieceRowVm>();

        private SubPieceRowVm _selectedSubPiece;
        public SubPieceRowVm SelectedSubPiece
        {
            get => _selectedSubPiece;
            set { Set(ref _selectedSubPiece, value); OnPropertyChanged(nameof(IsSubPieceSelected)); }
        }

        public bool IsSubPieceSelected => _selectedSubPiece != null;

        // ── Commands ───────────────────────────────────────────────────────────
        public ICommand AddFamilyCommand    { get; }
        public ICommand DeleteFamilyCommand { get; }
        public ICommand AddCommand          { get; }
        public ICommand DeleteCommand       { get; }
        public ICommand DuplicateCommand    { get; }
        public ICommand SaveCommand         { get; }
        public ICommand ExportCommand       { get; }
        public ICommand ImportCommand       { get; }
        public ICommand AddSubPieceCommand    { get; }
        public ICommand DeleteSubPieceCommand { get; }

        // Aliases so Tab 1 XAML bindings that still use old names keep compiling
        public ICommand SaveCurrentCommand => SaveCommand;
        public ICommand SaveXmlCommand     => ExportCommand;
        public ICommand LoadXmlCommand     => ImportCommand;

        // ── Footprint shape picker ─────────────────────────────────────────────
        public FootprintShapeItem[] AllFootprintShapes { get; } =
        {
            new FootprintShapeItem(FootprintShape.Circular,    "Dairesel"),
            new FootprintShapeItem(FootprintShape.Rectangular, "Dikdörtgen"),
            new FootprintShapeItem(FootprintShape.Square,      "Kare"),
        };

        public string[] FootprintOrientationOptions { get; } =
        {
            "uzun kenar; çıkış boru yönünde",
            "uzun kenar; giriş boru yönünde",
            "kısa kenar; çıkış boru yönünde",
            "kısa kenar; giriş boru yönünde"
        };

        // ── Constructor ────────────────────────────────────────────────────────
        public RepositoryTabVm(SmartAssemblyMasterCatalog catalog,
                               Action onComponentsChanged = null)
        {
            _catalog             = catalog ?? throw new ArgumentNullException(nameof(catalog));
            _onComponentsChanged = onComponentsChanged;

            AddFamilyCommand    = new RelayCommand(OnAddFamily);
            DeleteFamilyCommand = new RelayCommand(OnDeleteFamily, _ => IsFamilySelected);
            AddCommand          = new RelayCommand(OnAdd,       _ => IsFamilySelected);
            DeleteCommand       = new RelayCommand(OnDelete,    _ => IsComponentSelected);
            DuplicateCommand    = new RelayCommand(OnDuplicate, _ => IsComponentSelected);
            SaveCommand         = new RelayCommand(OnSave);
            ExportCommand       = new RelayCommand(OnExport);
            ImportCommand       = new RelayCommand(OnImport);
            AddSubPieceCommand    = new RelayCommand(OnAddSubPiece,    _ => IsBottomElementSelected);
            DeleteSubPieceCommand = new RelayCommand(OnDeleteSubPiece, _ => IsSubPieceSelected);

            // Auto-load from fixed AppData path (silent, like PipeCatalog)
            var path = MasterCatalogXmlManager.DefaultComponentsPath;
            if (File.Exists(path))
                TryLoadFamilies(path, silent: true);
        }

        // ── Rebuild component VM list from selected family ─────────────────────
        private void ReloadComponents()
        {
            Components.Clear();
            SelectedComponent = null;
            if (_selectedFamily == null) return;
            foreach (var c in _selectedFamily.Components)
                Components.Add(new ComponentRowVm(c));
        }

        // ── Family CRUD ────────────────────────────────────────────────────────

        private void OnAddFamily(object _)
        {
            var family = new ComponentFamily { Name = "Yeni Aile" };
            _catalog.Families.Add(family);
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => SelectedFamily = family));
        }

        private void OnDeleteFamily(object _)
        {
            if (_selectedFamily == null) return;
            _catalog.Families.Remove(_selectedFamily);
            SelectedFamily = null;
            _onComponentsChanged?.Invoke();
        }

        // ── Component CRUD ─────────────────────────────────────────────────────

        private void OnAdd(object _)
        {
            if (_selectedFamily == null) return;
            ManholeComponent comp;
            switch (_newRole)
            {
                case ComponentRole.BottomElement:
                    comp = new BottomElementComponent { Name = "Yeni Taban",        FamilyTag = "Standard-Precast" }; break;
                case ComponentRole.Reducer:
                    comp = new ReducerComponent       { Name = "Yeni Konik",        FamilyTag = "Standard-Precast", ZorunluParca = true }; break;
                case ComponentRole.Adjuster:
                    comp = new AdjusterComponent      { Name = "Yeni Ayar Halkası", FamilyTag = "Standard-Precast", YukseltmeParcasi = true }; break;
                case ComponentRole.Cover:
                    comp = new CoverComponent         { Name = "Yeni Rögar Kapağı", LoadClass = "D400",             ZorunluParca = true };    break;
                default: // MiddleElement
                    comp = new MiddleElementComponent { Name = "Yeni Halka",        FamilyTag = "Standard-Precast", YukseltmeParcasi = true }; break;
            }

            _selectedFamily.Components.Add(comp);
            var vm = new ComponentRowVm(comp);
            Components.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => SelectedComponent = vm));
        }

        private void OnDelete(object _)
        {
            if (_selectedComponent == null || _selectedFamily == null) return;
            _selectedFamily.Components.Remove(_selectedComponent.Model);
            Components.Remove(_selectedComponent);
            SelectedComponent = null;
        }

        private void OnDuplicate(object _)
        {
            if (_selectedComponent == null || _selectedFamily == null) return;
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
                    TabanKalinligiMm     = b.TabanKalinligiMm,
                    IsVariable           = b.IsVariable,
                    ZorunluParca         = b.ZorunluParca,
                    YukseltmeParcasi     = b.YukseltmeParcasi,
                    Footprint = new Footprint
                    {
                        Shape                  = b.Footprint?.Shape ?? FootprintShape.Circular,
                        DiameterMm             = b.Footprint?.DiameterMm ?? 0,
                        SideMm                 = b.Footprint?.SideMm    ?? 0,
                        LengthMm               = b.Footprint?.LengthMm  ?? 0,
                        WidthMm                = b.Footprint?.WidthMm   ?? 0,
                        RectangularOrientation = b.Footprint?.RectangularOrientation ?? ""
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
                        InnerDiameterMm = m.InnerDiameterMm,
                        IsVariable = m.IsVariable, ZorunluParca = m.ZorunluParca,
                        YukseltmeParcasi = m.YukseltmeParcasi
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
                            TopInnerDiameterMm    = r.TopInnerDiameterMm,
                            IsVariable = r.IsVariable, ZorunluParca = r.ZorunluParca
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
                                InnerDiameterMm = a.InnerDiameterMm,
                                IsVariable = a.IsVariable, ZorunluParca = a.ZorunluParca,
                                YukseltmeParcasi = a.YukseltmeParcasi
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
                                LoadClass = cv.LoadClass, ClearOpeningMm = cv.ClearOpeningMm,
                                IsVariable = cv.IsVariable, ZorunluParca = cv.ZorunluParca
                            };
                        }
                    }
                }
            }

            _selectedFamily.Components.Add(clone);
            var vm = new ComponentRowVm(clone);
            Components.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(DispatcherPriority.Background,
                new Action(() => SelectedComponent = vm));
        }

        // ── Sub-Piece CRUD ─────────────────────────────────────────────────────

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

        // ── XML persistence (PipeCatalog pattern) ──────────────────────────────

        private void TryLoadFamilies(string path, bool silent)
        {
            try
            {
                var families = MasterCatalogXmlManager.ImportFamilies(path);
                _catalog.Families.Clear();
                foreach (var f in families) _catalog.Families.Add(f);
                SelectedFamily = null;
                _onComponentsChanged?.Invoke();
                if (!silent)
                    MessageBox.Show(
                        string.Format("{0} aile yüklendi.", families.Count),
                        "İçe Aktarma Başarılı", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            catch (Exception ex)
            {
                if (!silent)
                    MessageBox.Show("Aile yükleme hatası:\n" + ex.Message,
                        "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void OnSave(object _)
        {
            try
            {
                MasterCatalogXmlManager.ExportFamilies(_catalog,
                    MasterCatalogXmlManager.DefaultComponentsPath);
                // Invalidate the shared disk cache so the next consumer that reads
                // SmartAssemblyCatalogStore.Current (e.g. MANHOLE_EXCAV_CATALOG)
                // reloads from disk and sees the freshly saved families/components.
                SmartAssemblyCatalogStore.Invalidate();
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
                MasterCatalogXmlManager.ExportFamilies(_catalog, path);
                StatusText = "Dışa aktarıldı: " + Path.GetFileName(path);
            }
            catch (Exception ex) { ShowError("Dışa aktarma hatası", ex); }
        }

        private void OnImport(object _)
        {
            var path = PickOpenPath("Bileşen Kataloğu İçe Aktar");
            if (path == null) return;
            TryLoadFamilies(path, silent: false);
        }

        // ── Status bar ─────────────────────────────────────────────────────────
        private string _statusText = "Hazır.";
        public string StatusText
        {
            get => _statusText;
            set { Set(ref _statusText, value); }
        }

        // ── Helpers ────────────────────────────────────────────────────────────
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

    /// <summary>Label+value pair for the Yeni Tip ComboBox so role names are displayed in Turkish.</summary>
    public sealed class ComponentRoleItem
    {
        public ComponentRole Role    { get; }
        public string        Display { get; }
        public ComponentRoleItem(ComponentRole role, string display) { Role = role; Display = display; }
    }
}
