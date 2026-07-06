using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.ExcelImport;
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
                LoadTemelAltiParcalar();
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

        // ── Temel Altı Parçalar ────────────────────────────────────────────────
        public ObservableCollection<TemelAltiParcaRowVm> TemelAltiParcalar { get; }
            = new ObservableCollection<TemelAltiParcaRowVm>();

        private TemelAltiParcaRowVm _selectedTemelAltiParca;
        public TemelAltiParcaRowVm SelectedTemelAltiParca
        {
            get => _selectedTemelAltiParca;
            set { Set(ref _selectedTemelAltiParca, value); OnPropertyChanged(nameof(IsTemelAltiParcaSelected)); }
        }

        public bool IsTemelAltiParcaSelected => _selectedTemelAltiParca != null;

        // ── Commands ───────────────────────────────────────────────────────────
        public ICommand AddFamilyCommand    { get; }
        public ICommand DeleteFamilyCommand { get; }
        public ICommand AddCommand          { get; }
        public ICommand DeleteCommand       { get; }
        public ICommand DuplicateCommand    { get; }
        public ICommand SaveCommand         { get; }
        public ICommand ExportCommand       { get; }
        public ICommand ImportCommand       { get; }
        public ICommand ImportExcelCommand  { get; }
        public ICommand AddSubPieceCommand        { get; }
        public ICommand DeleteSubPieceCommand     { get; }
        public ICommand AddTemelAltiParcaCommand    { get; }
        public ICommand DeleteTemelAltiParcaCommand { get; }

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
            ImportExcelCommand  = new RelayCommand(OnImportExcel, _ => IsFamilySelected);
            AddSubPieceCommand          = new RelayCommand(OnAddSubPiece,           _ => IsBottomElementSelected);
            DeleteSubPieceCommand       = new RelayCommand(OnDeleteSubPiece,        _ => IsSubPieceSelected);
            AddTemelAltiParcaCommand    = new RelayCommand(OnAddTemelAltiParca,    _ => IsBottomElementSelected);
            DeleteTemelAltiParcaCommand = new RelayCommand(OnDeleteTemelAltiParca, _ => IsTemelAltiParcaSelected);

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
                cb.TemelAltiParcaEnabled = b.TemelAltiParcaEnabled;
                foreach (var tap in b.TemelAltiParcalar)
                    cb.TemelAltiParcalar.Add(new TemelAltiParca
                        { Ad = tap.Ad, Boy = tap.Boy, En = tap.En, Kalinlik = tap.Kalinlik, Malzeme = tap.Malzeme });
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

        // ── Temel Altı Parça CRUD ──────────────────────────────────────────────

        private void LoadTemelAltiParcalar()
        {
            TemelAltiParcalar.Clear();
            SelectedTemelAltiParca = null;
            var b = _selectedComponent?.Model as BottomElementComponent;
            if (b == null) return;
            foreach (var tap in b.TemelAltiParcalar)
                TemelAltiParcalar.Add(new TemelAltiParcaRowVm(tap));
        }

        private void OnAddTemelAltiParca(object _)
        {
            var b = _selectedComponent?.Model as BottomElementComponent;
            if (b == null) return;
            var tap = new TemelAltiParca { Ad = "Yeni Parça " + (b.TemelAltiParcalar.Count + 1) };
            b.TemelAltiParcalar.Add(tap);
            var vm = new TemelAltiParcaRowVm(tap);
            TemelAltiParcalar.Add(vm);
            Application.Current?.Dispatcher?.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() => SelectedTemelAltiParca = vm));
        }

        private void OnDeleteTemelAltiParca(object _)
        {
            if (_selectedTemelAltiParca == null) return;
            var b = _selectedComponent?.Model as BottomElementComponent;
            b?.TemelAltiParcalar.Remove(_selectedTemelAltiParca.Model);
            TemelAltiParcalar.Remove(_selectedTemelAltiParca);
            SelectedTemelAltiParca = null;
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

        private void OnImportExcel(object _)
        {
            if (_selectedFamily == null)
            {
                MessageBox.Show("Önce içe aktarılacak bileşenlerin ekleneceği aileyi seçin veya oluşturun.",
                    "Aile Seçilmedi", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var openDlg = new OpenFileDialog
                { Title = "Excel'den Bileşen İçe Aktar", Filter = "Excel (*.xlsx)|*.xlsx" };
            if (openDlg.ShowDialog() != true) return;

            try
            {
                string[] headers;
                System.Collections.Generic.List<string[]> rows;
                ExcelSheetReader.ReadFirstSheet(openDlg.FileName, out headers, out rows);

                if (rows.Count == 0)
                {
                    StatusText = "Excel dosyasında veri satırı bulunamadı.";
                    return;
                }

                var mapDlg = new ColumnMappingDialog(Path.GetFileName(openDlg.FileName), headers, rows,
                    ComponentExcelImportService.Fields, Application.Current?.MainWindow);
                if (mapDlg.ShowDialog() != true) return;

                // Rol column left unmapped -> every imported row uses the currently
                // selected "Yeni tip" role (matches the manual + Ekle workflow).
                var components = ComponentExcelImportService.Build(rows, mapDlg.Result, _newRole);
                if (components.Count == 0)
                {
                    StatusText = "Eşleştirilen sütunlardan hiç bileşen satırı üretilemedi.";
                    return;
                }

                foreach (var comp in components)
                {
                    _selectedFamily.Components.Add(comp);
                    Components.Add(new ComponentRowVm(comp));
                }

                _onComponentsChanged?.Invoke();
                StatusText = string.Format("Excel'den {0} bileşen \"{1}\" ailesine eklendi — kaydetmek için Kaydet'e basın.",
                                           components.Count, _selectedFamily.Name);
            }
            catch (Exception ex) { ShowError("Excel içe aktarma hatası", ex); }
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
