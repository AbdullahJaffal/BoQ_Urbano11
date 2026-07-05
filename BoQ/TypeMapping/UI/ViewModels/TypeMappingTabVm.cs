using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;   // ViewModelBase, RelayCommand
using UrbanoMetraj.BoQ.TypeMapping.Models;

namespace UrbanoMetraj.BoQ.TypeMapping.UI.ViewModels
{
    /// <summary>
    /// One Urbano PIPE catalog item ↔ our PipeCatalog link, editable via a cascading
    /// Aile → Sınıf → Boru ComboBox chain. The Sınıf step exists because two pipes
    /// can share the exact same diameter but differ only in stiffness class (e.g.
    /// SN8 vs SN10) — filtering by Sınıf first prevents picking the wrong one.
    /// </summary>
    public sealed class PipeLinkRowVm : ViewModelBase
    {
        private readonly CatalogItemInfo _urbanoItem;
        private readonly PipeCatalog     _catalog;
        private static readonly ObservableCollection<PipeFamily> _emptyFamilies = new ObservableCollection<PipeFamily>();
        private static readonly ObservableCollection<string>     _emptySinifs   = new ObservableCollection<string>();
        private readonly ObservableCollection<string>          _sinifsInFamily = new ObservableCollection<string>();
        private readonly ObservableCollection<PipeDefinition>  _filteredPipes  = new ObservableCollection<PipeDefinition>();

        private PipeFamily     _selectedFamily;
        private string         _selectedSinif;
        private PipeDefinition _selectedPipe;
        private bool           _isSelected;

        /// <summary>
        /// Row multi-selection state for the "Seçilenlere Uygula" bulk-assign
        /// toolbar — bound two-way to DataGridRow.IsSelected so Ctrl/Shift-click
        /// multi-select (SelectionMode=Extended) is reflected here with zero
        /// code-behind.
        /// </summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public PipeLinkRowVm(CatalogItemInfo urbanoItem, PipeTypeLink existing, PipeCatalog catalog)
        {
            _urbanoItem = urbanoItem;
            _catalog    = catalog;
            _urbanoDiameterMm = ParseUrbanoDiameterMm(urbanoItem?.Name);

            if (existing != null && existing.LinkedPipeDefinitionId != Guid.Empty && catalog != null)
            {
                foreach (var fam in catalog.Families)
                {
                    var p = fam.Pipes.FirstOrDefault(x => x.Id == existing.LinkedPipeDefinitionId);
                    if (p == null) continue;
                    _selectedFamily = fam;
                    _selectedSinif  = p.Sinif;
                    _selectedPipe   = p;
                    break;
                }
                RebuildSinifsInFamily();
                RebuildFilteredPipes();
            }
        }

        public string UrbanoGuid      => _urbanoItem.Guid;
        public string UrbanoName      => _urbanoItem.Name;
        public string UrbanoGroupName => _urbanoItem.GroupName;

        public ObservableCollection<PipeFamily>     AvailableFamilies => _catalog?.Families ?? _emptyFamilies;
        public ObservableCollection<string>         AvailableSinifs   => _sinifsInFamily;
        public ObservableCollection<PipeDefinition> FilteredPipes     => _filteredPipes;

        public PipeFamily SelectedFamily
        {
            get => _selectedFamily;
            set
            {
                if (!Set(ref _selectedFamily, value)) return;
                RebuildSinifsInFamily();
                if (_selectedSinif != null && !_sinifsInFamily.Contains(_selectedSinif))
                    SelectedSinif = null;
                else
                {
                    RebuildFilteredPipes();
                    AutoFillPipeByDiameter();
                }
            }
        }

        public string SelectedSinif
        {
            get => _selectedSinif;
            set
            {
                if (!Set(ref _selectedSinif, value)) return;
                RebuildFilteredPipes();
                AutoFillPipeByDiameter();
            }
        }

        /// <summary>
        /// Auto-fill "Bağlı Boru" once both Aile and Sınıf are picked: search the
        /// filtered pipe list for the one whose NominalDiameter matches Urbano's
        /// OWN drawn diameter (parsed from this catalog item's name) exactly —
        /// same early-gap-detection convention as ManholeLinkRowVm.SelectedFamily
        /// (user directive 2026-07-06: apply the same idea here). Left null
        /// (visibly blank) when no match exists in the filtered Sınıf, surfacing
        /// a catalog gap immediately instead of failing later during Hesapla.
        /// Only fires on a live Aile/Sınıf selection — existing saved links
        /// loaded via the constructor are not retroactively re-matched.
        /// </summary>
        private void AutoFillPipeByDiameter()
        {
            SelectedPipe = _urbanoDiameterMm > 0
                ? _filteredPipes.FirstOrDefault(p => Math.Abs(p.NominalDiameter - _urbanoDiameterMm) < 1e-6)
                : null;
        }

        public PipeDefinition SelectedPipe
        {
            get => _selectedPipe;
            set
            {
                Set(ref _selectedPipe, value);
                OnPropertyChanged(nameof(IsLinked));
            }
        }

        /// <summary>False rows are skipped on Save — an unlinked Urbano item stays unlinked, not defaulted.</summary>
        public bool IsLinked => _selectedPipe != null;

        private void RebuildSinifsInFamily()
        {
            _sinifsInFamily.Clear();
            if (_selectedFamily == null) return;
            foreach (var s in _selectedFamily.Pipes.Select(p => p.Sinif ?? "").Distinct().OrderBy(s => s))
                _sinifsInFamily.Add(s);
        }

        private void RebuildFilteredPipes()
        {
            _filteredPipes.Clear();
            if (_selectedFamily == null || _selectedSinif == null) return;
            foreach (var p in _selectedFamily.Pipes.Where(p => (p.Sinif ?? "") == _selectedSinif))
                _filteredPipes.Add(p);
        }

        /// <summary>
        /// Urbano's own drawn diameter (mm), parsed from this catalog item's
        /// name, e.g. "1000 mm KRG" → 1000. Set once in the constructor.
        /// </summary>
        private readonly int _urbanoDiameterMm;

        private static int ParseUrbanoDiameterMm(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(name, @"(\d{2,5})\s*mm");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
        }

        public PipeTypeLink ToModel() => new PipeTypeLink
        {
            UrbanoCatalogItemGuid  = UrbanoGuid,
            UrbanoCatalogItemName  = UrbanoName,
            LinkedPipeDefinitionId = _selectedPipe?.Id ?? Guid.Empty
        };
    }

    /// <summary>
    /// One Urbano MANHOLE catalog item ↔ diameter-resolution mode + a specific
    /// "Taban" (base) piece in our own component catalog, via a cascading
    /// Aile → Taban ComboBox chain (our manhole catalog is assembly-style — every
    /// stack sequences from its base component, there's no flat diameter-keyed
    /// product row the way PipeCatalog has for pipes).
    /// </summary>
    public sealed class ManholeLinkRowVm : ViewModelBase
    {
        private readonly CatalogItemInfo _urbanoItem;
        private readonly ObservableCollection<ComponentFamily> _families;
        private static readonly ObservableCollection<ComponentFamily> _emptyFamilies = new ObservableCollection<ComponentFamily>();
        private readonly ObservableCollection<BottomElementComponent> _bottomsInFamily = new ObservableCollection<BottomElementComponent>();

        private ManholeDiameterMode _mode;
        private ComponentFamily _selectedFamily;
        private BottomElementComponent _selectedBottom;
        private bool _isSelected;

        /// <summary>Row multi-selection state for the "Seçilenlere Uygula" bulk-assign toolbar (see PipeLinkRowVm.IsSelected).</summary>
        public bool IsSelected
        {
            get => _isSelected;
            set => Set(ref _isSelected, value);
        }

        public ManholeLinkRowVm(CatalogItemInfo urbanoItem, ManholeTypeLink existing, ObservableCollection<ComponentFamily> families)
        {
            _urbanoItem = urbanoItem;
            _families   = families;
            _mode = existing?.DiameterMode ?? ManholeDiameterMode.FollowDrawing;
            _urbanoDiameterMm = ParseUrbanoDiameterMm(urbanoItem?.Name);

            if (existing != null && existing.LinkedBottomComponentId != Guid.Empty && families != null)
            {
                foreach (var fam in families)
                {
                    var b = fam.Components.OfType<BottomElementComponent>()
                        .FirstOrDefault(x => x.Id == existing.LinkedBottomComponentId);
                    if (b == null) continue;
                    _selectedFamily = fam;
                    _selectedBottom = b;
                    break;
                }
                RebuildBottomsInFamily();
            }
        }

        public string UrbanoGuid      => _urbanoItem.Guid;
        public string UrbanoName      => _urbanoItem.Name;
        public string UrbanoGroupName => _urbanoItem.GroupName;

        public ManholeDiameterMode DiameterMode
        {
            get => _mode;
            set => Set(ref _mode, value);
        }

        public static ManholeDiameterMode[] AvailableModes { get; } =
            (ManholeDiameterMode[])Enum.GetValues(typeof(ManholeDiameterMode));

        public ObservableCollection<ComponentFamily>        AvailableFamilies => _families ?? _emptyFamilies;
        public ObservableCollection<BottomElementComponent> BottomsInFamily   => _bottomsInFamily;

        public ComponentFamily SelectedFamily
        {
            get => _selectedFamily;
            set
            {
                if (!Set(ref _selectedFamily, value)) return;
                RebuildBottomsInFamily();

                // Auto-fill Taban Parçası: search the newly-picked family for a Taban
                // matching Urbano's OWN drawn shape/dimensions exactly — same
                // hard-match convention ManholeAIService.ResolveFamilyAndTaban uses
                // at calculation time (shape-aware since 2026-07-06: circular by
                // diameter, square by side, rectangular by L/W with swap allowed).
                // Left null (visibly blank) when no match exists, which is the
                // point: it surfaces a catalog gap immediately instead of only
                // failing much later during Hesapla (user directive 2026-07-05).
                BottomElementComponent autoMatch = null;
                var shape = _urbanoItem?.Shape ?? FootprintShape.Circular;
                if (shape == FootprintShape.Circular && _urbanoDiameterMm > 0)
                {
                    autoMatch = _bottomsInFamily.FirstOrDefault(b =>
                        b.Footprint?.Shape == FootprintShape.Circular &&
                        Math.Abs(b.Footprint.DiameterMm - _urbanoDiameterMm) < 1e-6);
                }
                else if (shape == FootprintShape.Square && _urbanoItem?.LengthM > 0)
                {
                    double sideMm = _urbanoItem.LengthM * 1000.0;
                    autoMatch = _bottomsInFamily.FirstOrDefault(b =>
                        b.Footprint?.Shape == FootprintShape.Square &&
                        Math.Abs(b.Footprint.SideMm - sideMm) < 1e-3);
                }
                else if (shape == FootprintShape.Rectangular && _urbanoItem?.LengthM > 0 && _urbanoItem?.WidthM > 0)
                {
                    double lMm = _urbanoItem.LengthM * 1000.0, wMm = _urbanoItem.WidthM * 1000.0;
                    autoMatch = _bottomsInFamily.FirstOrDefault(b =>
                        b.Footprint?.Shape == FootprintShape.Rectangular &&
                        ((Math.Abs(b.Footprint.LengthMm - lMm) < 1e-3 && Math.Abs(b.Footprint.WidthMm - wMm) < 1e-3) ||
                         (Math.Abs(b.Footprint.LengthMm - wMm) < 1e-3 && Math.Abs(b.Footprint.WidthMm - lMm) < 1e-3)));
                }

                SelectedBottom = autoMatch;
            }
        }

        public BottomElementComponent SelectedBottom
        {
            get => _selectedBottom;
            set => Set(ref _selectedBottom, value);
        }

        private void RebuildBottomsInFamily()
        {
            _bottomsInFamily.Clear();
            if (_selectedFamily == null) return;
            foreach (var b in _selectedFamily.Components.OfType<BottomElementComponent>())
                _bottomsInFamily.Add(b);
        }

        /// <summary>
        /// Urbano's own drawn diameter (mm), parsed from the first "Ø####" in this
        /// catalog item's (already-cleaned) name — same "first Φ = the manhole's
        /// own diameter" convention as BoQParserService.ExtractManholeNominalDiam,
        /// just applied to the display name instead of the raw XML property.
        /// </summary>
        private readonly int _urbanoDiameterMm;

        private static int ParseUrbanoDiameterMm(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var m = System.Text.RegularExpressions.Regex.Match(name, "Ø(\\d{2,5})");
            return m.Success && int.TryParse(m.Groups[1].Value, out int v) ? v : 0;
        }

        public ManholeTypeLink ToModel() => new ManholeTypeLink
        {
            UrbanoCatalogItemGuid   = UrbanoGuid,
            UrbanoCatalogItemName   = UrbanoName,
            DiameterMode            = _mode,
            LinkedFamilyId          = _selectedFamily?.Id ?? Guid.Empty,
            LinkedBottomComponentId = _selectedBottom?.Id ?? Guid.Empty
        };
    }

    /// <summary>
    /// Root ViewModel for the "Tür Eşleştirme" (Type Mapping) tab. Deliberately
    /// AutoCAD-agnostic (no Autodesk.* usings), matching every other Akıllı Montaj
    /// tab — the DWG-bound load/save work is injected by the caller
    /// (SmartAssemblyCommand) via <see cref="Initialize"/>, since Type Mapping data
    /// lives in the active DWG's NOD rather than a shared %APPDATA% XML file.
    /// </summary>
    public sealed class TypeMappingTabVm : ViewModelBase
    {
        private readonly PipeCatalog _pipeCatalog;
        private readonly ObservableCollection<ComponentFamily> _manholeFamilies;
        private Action<List<PipeTypeLink>, List<ManholeTypeLink>> _saveAction;
        private Func<List<CatalogItemInfo>> _reloadItemsFunc;
        private Dictionary<string, PipeTypeLink>    _existingPipeLinks    = new Dictionary<string, PipeTypeLink>(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, ManholeTypeLink> _existingManholeLinks = new Dictionary<string, ManholeTypeLink>(StringComparer.OrdinalIgnoreCase);

        public ObservableCollection<PipeLinkRowVm>    PipeLinks    { get; } = new ObservableCollection<PipeLinkRowVm>();
        public ObservableCollection<ManholeLinkRowVm> ManholeLinks { get; } = new ObservableCollection<ManholeLinkRowVm>();

        private string _statusText = "";
        public string StatusText
        {
            get => _statusText;
            set => Set(ref _statusText, value);
        }

        public ICommand RefreshCommand { get; }
        public ICommand SaveCommand    { get; }

        // ── Bulk-assign toolbar (user directive 2026-07-06): pick Aile (+ Sınıf
        // for pipes) once, apply it to every currently multi-selected row instead
        // of repeating the same cascade one row at a time. Reuses each row's own
        // SelectedFamily/SelectedSinif setters — same auto-fill-by-diameter logic
        // that already runs for a single-row edit fires per row here too. ──
        private static readonly ObservableCollection<PipeFamily>      _emptyPipeFamilies    = new ObservableCollection<PipeFamily>();
        private static readonly ObservableCollection<ComponentFamily> _emptyManholeFamilies = new ObservableCollection<ComponentFamily>();
        private readonly ObservableCollection<string> _bulkPipeSinifs = new ObservableCollection<string>();

        public ObservableCollection<PipeFamily>      AvailablePipeFamilies    => _pipeCatalog?.Families ?? _emptyPipeFamilies;
        public ObservableCollection<ComponentFamily> AvailableManholeFamilies => _manholeFamilies ?? _emptyManholeFamilies;
        public ObservableCollection<string>          BulkPipeSinifs           => _bulkPipeSinifs;

        private PipeFamily _bulkPipeFamily;
        public PipeFamily BulkPipeFamily
        {
            get => _bulkPipeFamily;
            set
            {
                if (!Set(ref _bulkPipeFamily, value)) return;
                _bulkPipeSinifs.Clear();
                if (_bulkPipeFamily != null)
                    foreach (var s in _bulkPipeFamily.Pipes.Select(p => p.Sinif ?? "").Distinct().OrderBy(s => s))
                        _bulkPipeSinifs.Add(s);
                BulkPipeSinif = null;
            }
        }

        private string _bulkPipeSinif;
        public string BulkPipeSinif
        {
            get => _bulkPipeSinif;
            set => Set(ref _bulkPipeSinif, value);
        }

        private ComponentFamily _bulkManholeFamily;
        public ComponentFamily BulkManholeFamily
        {
            get => _bulkManholeFamily;
            set => Set(ref _bulkManholeFamily, value);
        }

        public ICommand ApplyBulkPipeCommand    { get; }
        public ICommand ApplyBulkManholeCommand { get; }

        public TypeMappingTabVm(PipeCatalog pipeCatalog, ObservableCollection<ComponentFamily> manholeFamilies)
        {
            _pipeCatalog     = pipeCatalog;
            _manholeFamilies = manholeFamilies;
            RefreshCommand = new RelayCommand(_ => Refresh());
            SaveCommand    = new RelayCommand(_ => Save(), _ => _saveAction != null);
            ApplyBulkPipeCommand = new RelayCommand(
                _ => ApplyBulkPipe(),
                _ => BulkPipeFamily != null && BulkPipeSinif != null && PipeLinks.Any(r => r.IsSelected));
            ApplyBulkManholeCommand = new RelayCommand(
                _ => ApplyBulkManhole(),
                _ => BulkManholeFamily != null && ManholeLinks.Any(r => r.IsSelected));
        }

        private void ApplyBulkPipe()
        {
            foreach (var row in PipeLinks.Where(r => r.IsSelected))
            {
                row.SelectedFamily = BulkPipeFamily;
                row.SelectedSinif  = BulkPipeSinif;
            }
        }

        private void ApplyBulkManhole()
        {
            foreach (var row in ManholeLinks.Where(r => r.IsSelected))
                row.SelectedFamily = BulkManholeFamily;
        }

        /// <summary>
        /// Supplies this tab with everything that needs a Database/transaction — the
        /// discovered Urbano catalog items and links already saved in the active DWG
        /// (loaded once externally), a reload delegate ("Yenile" re-reads the DWG,
        /// not a temp file — works even after DwgBoQStore.Save's data has moved to
        /// another machine), and a save callback bound to that same DWG. This
        /// ViewModel deliberately never touches Autodesk.* types itself; called by
        /// SmartAssemblyCommand right after the window/document is known.
        /// </summary>
        public void Initialize(
            List<CatalogItemInfo> discoveredItems,
            List<PipeTypeLink> existingPipeLinks,
            List<ManholeTypeLink> existingManholeLinks,
            Func<List<CatalogItemInfo>> reloadItemsFunc,
            Action<List<PipeTypeLink>, List<ManholeTypeLink>> saveAction)
        {
            _existingPipeLinks = (existingPipeLinks ?? new List<PipeTypeLink>())
                .ToDictionary(l => l.UrbanoCatalogItemGuid, StringComparer.OrdinalIgnoreCase);
            _existingManholeLinks = (existingManholeLinks ?? new List<ManholeTypeLink>())
                .ToDictionary(l => l.UrbanoCatalogItemGuid, StringComparer.OrdinalIgnoreCase);
            _reloadItemsFunc = reloadItemsFunc;
            _saveAction = saveAction;
            CommandManager.InvalidateRequerySuggested();
            RefreshWithItems(discoveredItems ?? new List<CatalogItemInfo>());
        }

        /// <summary>Re-invokes the reload delegate (DWG-bound) and rebuilds the grids.</summary>
        private void Refresh()
            => RefreshWithItems(_reloadItemsFunc?.Invoke() ?? new List<CatalogItemInfo>());

        /// <summary>
        /// Rebuilds the grids from a discovered-items list — existing links are
        /// pre-selected, newly-discovered catalog items (not linked yet) show up blank.
        /// </summary>
        private void RefreshWithItems(List<CatalogItemInfo> items)
        {
            PipeLinks.Clear();
            foreach (var item in items.Where(i => i.Reference == "PIPE"))
            {
                _existingPipeLinks.TryGetValue(item.Guid, out var existing);
                PipeLinks.Add(new PipeLinkRowVm(item, existing, _pipeCatalog));
            }

            ManholeLinks.Clear();
            foreach (var item in items.Where(i => i.Reference == "MANHOLE"))
            {
                _existingManholeLinks.TryGetValue(item.Guid, out var existing);
                ManholeLinks.Add(new ManholeLinkRowVm(item, existing, _manholeFamilies));
            }

            StatusText = items.Count == 0
                ? "Urbano kataloğ öğesi bulunamadı — önce Metraj Verisi Güncelle çalıştırın."
                : $"{PipeLinks.Count} boru, {ManholeLinks.Count} baca kataloğu öğesi bulundu.";
        }

        private void Save()
        {
            var pipeLinks = PipeLinks
                .Where(r => r.IsLinked)
                .Select(r => r.ToModel())
                .ToList();
            var manholeLinks = ManholeLinks
                .Select(r => r.ToModel())
                .ToList();

            _saveAction?.Invoke(pipeLinks, manholeLinks);
            StatusText = $"Kaydedildi: {pipeLinks.Count} boru bağlantısı, {manholeLinks.Count} baca bağlantısı.";
        }
    }
}
