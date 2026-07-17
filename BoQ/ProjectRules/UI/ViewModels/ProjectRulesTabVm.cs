using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Microsoft.Win32;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.Services;
using UrbanoMetraj.BoQ.ProjectRules.Models;
using UrbanoMetraj.BoQ.ProjectRules.Services;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;   // ViewModelBase, RelayCommand

namespace UrbanoMetraj.BoQ.ProjectRules.UI.ViewModels
{
    /// <summary>Seed passed from the AutoCAD side: the network name + whether it is currently "Aktif".</summary>
    public sealed class NetworkSeed
    {
        public string Name { get; set; } = "";
        public bool   IsActive { get; set; }
    }

    /// <summary>One row in the İstisnalar grid — a flattened view over a per-dimension exception.</summary>
    public sealed class ExceptionRowVm
    {
        public ExceptionRowVm(string dimension, string agGuid, string entityName, string overrideLabel, object model)
        {
            Dimension     = dimension;
            AgGuid        = agGuid ?? "";
            EntityName    = entityName ?? "";
            OverrideLabel = overrideLabel ?? "";
            Model         = model;
        }

        public string Dimension     { get; }
        public string AgGuid        { get; }
        public string EntityName    { get; }
        public string OverrideLabel { get; }
        public object Model         { get; }

        /// <summary>Resolved entity name when known, otherwise a short form of the AG_GUID.</summary>
        public string Display => !string.IsNullOrEmpty(EntityName)
            ? EntityName
            : (AgGuid.Length > 10 ? AgGuid.Substring(0, 8) + "…" : AgGuid);
    }

    /// <summary>
    /// One network row in the new "Proje Kurulumu (DWG)" tab. Wraps a <see cref="NetworkRule"/>
    /// and exposes the pipe Aile→Sınıf and manhole Aile pickers, writing straight back into the
    /// model so a <c>Kaydet</c> persists whatever the grid shows.
    /// </summary>
    public sealed class NetworkRuleRowVm : ViewModelBase
    {
        private readonly PipeCatalog _pipeCatalog;
        private readonly SmartAssemblyMasterCatalog _masterCatalog;
        private readonly ObservableCollection<ComponentFamily> _manholeFamilies;

        private static readonly ObservableCollection<PipeFamily>      _emptyPipeFamilies    = new ObservableCollection<PipeFamily>();
        private static readonly ObservableCollection<ComponentFamily> _emptyManholeFamilies = new ObservableCollection<ComponentFamily>();
        private readonly ObservableCollection<string> _sinifsInFamily = new ObservableCollection<string>();

        public NetworkRule Model { get; }

        public NetworkRuleRowVm(NetworkRule model, PipeCatalog pipeCatalog,
                                SmartAssemblyMasterCatalog masterCatalog, bool isActive)
        {
            Model            = model ?? throw new ArgumentNullException(nameof(model));
            _pipeCatalog     = pipeCatalog;
            _masterCatalog   = masterCatalog;
            _manholeFamilies = masterCatalog?.Families;
            IsActive         = isActive;

            // Resolve the saved GUID/class references back into live catalog objects.
            _selectedPipeFamily = _pipeCatalog?.Families?.FirstOrDefault(f => f.Id == model.PipeFamilyId);
            RebuildSinifs();
            _selectedSinif = !string.IsNullOrEmpty(model.PipeSinif) && _sinifsInFamily.Contains(model.PipeSinif)
                ? model.PipeSinif : null;

            _selectedManholeFamily = _manholeFamilies?.FirstOrDefault(f => f.Id == model.ManholeFamilyId);
            RebuildManholeDiameters();
            RebuildKaziFilters();

            // Connection rules (baca seçim).
            foreach (var r in Model.ConnectionRules)
                ConnectionRules.Add(new ConnRuleVm(r));

            RebuildPieceRows();

            ImportConnRulesCommand = new RelayCommand(_ => ImportConnRules(), _ => _masterCatalog?.MasterPipeRules != null);
            AddConnRuleCommand     = new RelayCommand(_ => AddConnRule());
            DeleteConnRuleCommand  = new RelayCommand(_ => DeleteConnRule(), _ => _selectedConnRule != null);
            AddTierCommand         = new RelayCommand(_ => AddTier(),        _ => _selectedConnRule != null);
            DeleteTierCommand      = new RelayCommand(_ => DeleteTier(),     _ => _selectedTier != null);
            InitPieceCommands();
        }

        public string SystemName => Model.SystemName;

        public bool IsActive { get; }

        /// <summary>Short status shown per row: which pipe/manhole selections are still missing.</summary>
        public string RuleSummary
        {
            get
            {
                var parts = new List<string>();
                parts.Add(_selectedPipeFamily == null
                    ? "Boru: —"
                    : "Boru: " + _selectedPipeFamily.FamilyName +
                      (string.IsNullOrEmpty(_selectedSinif) ? "" : " / " + _selectedSinif));
                parts.Add(_selectedManholeFamily == null
                    ? "Baca: —"
                    : "Baca: " + _selectedManholeFamily.Name);
                return string.Join("   ·   ", parts);
            }
        }

        // ── Pipe Aile → Sınıf ─────────────────────────────────────────────────
        public ObservableCollection<PipeFamily> AvailablePipeFamilies => _pipeCatalog?.Families ?? _emptyPipeFamilies;
        public ObservableCollection<string>     AvailableSinifs       => _sinifsInFamily;

        private PipeFamily _selectedPipeFamily;
        public PipeFamily SelectedPipeFamily
        {
            get => _selectedPipeFamily;
            set
            {
                if (!Set(ref _selectedPipeFamily, value)) return;
                Model.PipeFamilyId = value?.Id ?? Guid.Empty;
                RebuildSinifs();
                if (_selectedSinif != null && !_sinifsInFamily.Contains(_selectedSinif))
                    SelectedSinif = null;
                OnPropertyChanged(nameof(RuleSummary));
            }
        }

        private string _selectedSinif;
        public string SelectedSinif
        {
            get => _selectedSinif;
            set
            {
                if (!Set(ref _selectedSinif, value)) return;
                Model.PipeSinif = value ?? "";
                OnPropertyChanged(nameof(RuleSummary));
            }
        }

        // ── Manhole Aile ──────────────────────────────────────────────────────
        public ObservableCollection<ComponentFamily> AvailableManholeFamilies => _manholeFamilies ?? _emptyManholeFamilies;

        private ComponentFamily _selectedManholeFamily;
        public ComponentFamily SelectedManholeFamily
        {
            get => _selectedManholeFamily;
            set
            {
                if (!Set(ref _selectedManholeFamily, value)) return;
                Model.ManholeFamilyId = value?.Id ?? Guid.Empty;
                RebuildManholeDiameters();
                RebuildPieceRows();
                OnPropertyChanged(nameof(RuleSummary));
            }
        }

        // ── Zemin Tipi (soil) ─────────────────────────────────────────────────
        private ObservableCollection<string> _availableSoils;
        public ObservableCollection<string> AvailableSoils
        {
            get
            {
                if (_availableSoils == null)
                    _availableSoils = new ObservableCollection<string>(
                        SoilCatalog.Services.SoilCatalogStore.Items
                            .Select(s => s.SoilName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                return _availableSoils;
            }
        }

        public string SelectedSoilName
        {
            get => Model.SoilName;
            set
            {
                if (Model.SoilName == value) return;
                Model.SoilName = value ?? "";
                OnPropertyChanged();
                RebuildKaziFilters();   // soil narrows the available excavation-rule names
            }
        }

        // ── Kazı kuralı filtreleri (rule-name multi-select, soil-narrowed) ────
        public ObservableCollection<RuleNameFilterVm> PipeTrenchFilters   { get; } = new ObservableCollection<RuleNameFilterVm>();
        public ObservableCollection<RuleNameFilterVm> ManholeExcavFilters { get; } = new ObservableCollection<RuleNameFilterVm>();

        private void RebuildKaziFilters()
        {
            string soil = Model.SoilName ?? "";

            PipeTrenchFilters.Clear();
            foreach (var name in PipeTrenchCatalogStore.Current
                         .Where(r => SoilMatches(r.SelectedSoilNames, soil))
                         .Select(r => r.RuleName).Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct().OrderBy(n => n))
                PipeTrenchFilters.Add(new RuleNameFilterVm(
                    name, Model.PipeTrenchRuleNames.Contains(name), SyncTrenchFilter));

            ManholeExcavFilters.Clear();
            foreach (var name in ManholeExcavationCatalogStore.Current
                         .Where(r => SoilMatches(r.SelectedSoilNames, soil))
                         .Select(r => r.RuleName).Where(n => !string.IsNullOrWhiteSpace(n))
                         .Distinct().OrderBy(n => n))
                ManholeExcavFilters.Add(new RuleNameFilterVm(
                    name, Model.ManholeExcavRuleNames.Contains(name), SyncMhExcavFilter));
        }

        private static bool SoilMatches(List<string> ruleSoils, string netSoil)
            => ruleSoils == null || ruleSoils.Count == 0 || string.IsNullOrEmpty(netSoil) || ruleSoils.Contains(netSoil);

        private void SyncTrenchFilter(RuleNameFilterVm f)
        {
            Model.PipeTrenchRuleNames.Remove(f.Name);
            if (f.IsSelected) Model.PipeTrenchRuleNames.Add(f.Name);
        }

        private void SyncMhExcavFilter(RuleNameFilterVm f)
        {
            Model.ManholeExcavRuleNames.Remove(f.Name);
            if (f.IsSelected) Model.ManholeExcavRuleNames.Add(f.Name);
        }

        /// <summary>
        /// Distinct top-opening diameters (mm) of the selected family's Taban pieces — the only
        /// values a connection-rule "Baca Çapı" may take (it's a catalog choice, not a free number).
        /// </summary>
        public ObservableCollection<double> AvailableManholeDiameters { get; } = new ObservableCollection<double>();

        private void RebuildManholeDiameters()
        {
            AvailableManholeDiameters.Clear();
            if (_selectedManholeFamily == null) return;
            var dias = _selectedManholeFamily.Components
                .OfType<BottomElementComponent>()
                .Select(b => b.TopOpeningDiameterMm)
                .Where(d => d > 0)
                .Distinct()
                .OrderBy(d => d);
            foreach (var d in dias) AvailableManholeDiameters.Add(d);
        }

        // ── Piece exclusions ("Kullanımdan parça çıkar") — user-added rows per (family, diameter) ──
        public ObservableCollection<PieceRowVm>      PieceRows            { get; } = new ObservableCollection<PieceRowVm>();
        public ObservableCollection<FamilyDiaOption> AvailablePieceOptions { get; } = new ObservableCollection<FamilyDiaOption>();

        private const double DiaTol = 1e-6;

        private static readonly (ComponentRole Role, string Name)[] _exclRoles =
        {
            (ComponentRole.MiddleElement, "Gövde"),
            (ComponentRole.Reducer,       "Koni"),
            (ComponentRole.Adjuster,      "Boyun"),
            (ComponentRole.Cover,         "Kapak"),
        };

        private FamilyDiaOption _selectedPieceOption;
        public FamilyDiaOption SelectedPieceOption { get => _selectedPieceOption; set => Set(ref _selectedPieceOption, value); }

        public ICommand AddPieceRowCommand    { get; private set; }
        public ICommand DeletePieceRowCommand { get; private set; }

        private void InitPieceCommands()
        {
            AddPieceRowCommand    = new RelayCommand(_ => AddPieceRow(), _ => _selectedPieceOption != null);
            DeletePieceRowCommand = new RelayCommand(DeletePieceRow, r => r is PieceRowVm);
        }

        /// <summary>Manhole families in play for this network: the main family + this network's manhole-exception families.</summary>
        private IEnumerable<ComponentFamily> FamiliesInUse()
        {
            var seen = new HashSet<Guid>();
            if (_selectedManholeFamily != null && seen.Add(_selectedManholeFamily.Id))
                yield return _selectedManholeFamily;
            foreach (var ex in Model.Exceptions?.ManholeFamily ?? new List<ManholeFamilyException>())
            {
                if (ex.ManholeFamilyId == Guid.Empty || !seen.Add(ex.ManholeFamilyId)) continue;
                var fam = _manholeFamilies?.FirstOrDefault(f => f.Id == ex.ManholeFamilyId);
                if (fam != null) yield return fam;
            }
        }

        /// <summary>Rebuilds the exclusion rows (from the model) and the "add" options (families in use ×
        /// their Taban diameters, minus rows already added). Called on family or exception changes.</summary>
        public void RebuildPieceRows()
        {
            PieceRows.Clear();
            foreach (var row in Model.PieceExclusionRows ?? new List<PieceExclusionRow>())
            {
                var fam = _manholeFamilies?.FirstOrDefault(f => f.Id == row.ManholeFamilyId);
                if (fam == null) continue;   // family no longer exists
                var vm = new PieceRowVm(row, fam.Name);
                foreach (var (role, name) in _exclRoles)
                {
                    var heights = HeightsForRole(fam, role, row.ManholeDiameterMm);
                    if (heights.Count == 0) continue;
                    var pe = row.Roles.FirstOrDefault(x => x.Role == role);
                    var allowed = new HashSet<double>(pe?.AllowedHeightsMm ?? Enumerable.Empty<double>());
                    vm.Roles.Add(new RolePieceVm(role, name, heights, allowed, pe != null,
                        rp => SyncPieceRow(row, rp)));
                }
                PieceRows.Add(vm);
            }
            RebuildAvailablePieceOptions();
        }

        private void RebuildAvailablePieceOptions()
        {
            AvailablePieceOptions.Clear();
            var used = new HashSet<string>(
                (Model.PieceExclusionRows ?? new List<PieceExclusionRow>())
                    .Select(r => r.ManholeFamilyId + "|" + r.ManholeDiameterMm.ToString("0.###")));
            foreach (var fam in FamiliesInUse())
            {
                var dias = fam.Components.OfType<BottomElementComponent>()
                    .Select(b => b.TopOpeningDiameterMm).Where(d => d > 0)
                    .Distinct().OrderBy(d => d);
                foreach (var d in dias)
                {
                    if (used.Contains(fam.Id + "|" + d.ToString("0.###"))) continue;
                    AvailablePieceOptions.Add(new FamilyDiaOption
                    { FamilyId = fam.Id, FamilyName = fam.Name, DiameterMm = d });
                }
            }
            SelectedPieceOption = AvailablePieceOptions.FirstOrDefault();
        }

        private void AddPieceRow()
        {
            if (_selectedPieceOption == null) return;
            Model.PieceExclusionRows.Add(new PieceExclusionRow
            {
                ManholeFamilyId   = _selectedPieceOption.FamilyId,
                ManholeDiameterMm = _selectedPieceOption.DiameterMm
            });
            RebuildPieceRows();
        }

        private void DeletePieceRow(object param)
        {
            if (!(param is PieceRowVm row)) return;
            Model.PieceExclusionRows.Remove(row.Model);
            RebuildPieceRows();
        }

        /// <summary>Available (distinct, sorted) heights of a role in <paramref name="fam"/> for a diameter.</summary>
        private List<double> HeightsForRole(ComponentFamily fam, ComponentRole role, double diameterMm)
        {
            IEnumerable<ManholeComponent> comps = fam.Components.Where(c => c.Role == role);

            // Gövde & Koni are keyed to the manhole Ø; Boyun & Kapak sit at the neck → unfiltered.
            if (role == ComponentRole.MiddleElement)
                comps = comps.OfType<MiddleElementComponent>()
                             .Where(c => Math.Abs(c.InnerDiameterMm - diameterMm) < DiaTol);
            else if (role == ComponentRole.Reducer)
                comps = comps.OfType<ReducerComponent>()
                             .Where(c => Math.Abs(c.BottomInnerDiameterMm - diameterMm) < DiaTol);

            return comps.Select(c => c.EffectiveHeight).Where(h => h > 0)
                        .Distinct().OrderBy(h => h).ToList();
        }

        /// <summary>
        /// Presence of a PieceExclusion for a role within a row = "restrict to these heights" (empty =
        /// none). When every height is checked we remove the record → no restriction for that role.
        /// </summary>
        private void SyncPieceRow(PieceExclusionRow row, RolePieceVm rp)
        {
            var allowed = rp.Heights.Where(h => h.IsAllowed).Select(h => h.HeightMm).ToList();
            row.Roles.RemoveAll(x => x.Role == rp.Role);
            if (allowed.Count != rp.Heights.Count)
                row.Roles.Add(new PieceExclusion { Role = rp.Role, AllowedHeightsMm = allowed });
        }

        private void RebuildSinifs()
        {
            _sinifsInFamily.Clear();
            if (_selectedPipeFamily == null) return;
            foreach (var s in _selectedPipeFamily.Pipes.Select(p => p.Sinif ?? "").Distinct().OrderBy(s => s))
                _sinifsInFamily.Add(s);
        }

        // ── Connection rules (baca seçim) ─────────────────────────────────────
        public ObservableCollection<ConnRuleVm> ConnectionRules { get; } = new ObservableCollection<ConnRuleVm>();

        private ConnRuleVm _selectedConnRule;
        public ConnRuleVm SelectedConnRule
        {
            get => _selectedConnRule;
            set { if (Set(ref _selectedConnRule, value)) { OnPropertyChanged(nameof(IsConnRuleSelected)); SelectedTier = null; } }
        }
        public bool IsConnRuleSelected => _selectedConnRule != null;

        private ConnTierVm _selectedTier;
        public ConnTierVm SelectedTier
        {
            get => _selectedTier;
            set { if (Set(ref _selectedTier, value)) RebuildTierConstraints(); }
        }

        /// <summary>Per-role min/max rows for the selected connection-rule tier (reuses the MasterRules pattern).</summary>
        public ObservableCollection<ComponentConstraintVm> TierConstraints { get; } = new ObservableCollection<ComponentConstraintVm>();

        private static readonly (ComponentRole Role, bool ZeroOrOne)[] _constraintRoles =
        {
            (ComponentRole.MiddleElement, false),
            (ComponentRole.Reducer,       true),
            (ComponentRole.Adjuster,      false),
            (ComponentRole.Cover,         true),
        };

        private void RebuildTierConstraints()
        {
            TierConstraints.Clear();
            if (_selectedTier == null || _selectedManholeFamily == null) return;
            var tierModel = _selectedTier.Model;

            var taban = tierModel.GetOrCreateConstraint(ComponentRole.BottomElement);
            taban.MinCount = 1; taban.MaxCount = 1;
            TierConstraints.Add(new ComponentConstraintVm(taban, isReadOnly: true, isZeroOrOne: false));

            foreach (var (role, zeroOrOne) in _constraintRoles)
            {
                if (!_selectedManholeFamily.Components.Any(c => c.Role == role)) continue;
                var cc = tierModel.GetOrCreateConstraint(role);
                if (zeroOrOne && cc.MaxCount == -1) cc.MaxCount = 1;
                TierConstraints.Add(new ComponentConstraintVm(cc, isReadOnly: false, isZeroOrOne: zeroOrOne));
            }
        }

        public ICommand ImportConnRulesCommand { get; }
        public ICommand AddConnRuleCommand     { get; }
        public ICommand DeleteConnRuleCommand  { get; }
        public ICommand AddTierCommand         { get; }
        public ICommand DeleteTierCommand      { get; }

        private void ImportConnRules()
        {
            if (_masterCatalog?.MasterPipeRules == null) return;
            if (Model.ConnectionRules.Count > 0)
            {
                var ans = System.Windows.MessageBox.Show(
                    "Mevcut bağlantı kurallarının üzerine katalog kuralları kopyalansın mı?",
                    "Onay", System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
                if (ans != System.Windows.MessageBoxResult.Yes) return;
            }

            Model.ConnectionRules.Clear();
            foreach (var pr in _masterCatalog.MasterPipeRules)
            {
                var rule = new ConnectionRule { MinPipeMm = pr.MinPipeMm, MaxPipeMm = pr.MaxPipeMm };
                foreach (var dt in pr.DepthTiers ?? new List<DepthTierRule>())
                {
                    // Output = manhole diameter within the network family: seed it from the catalog
                    // base this master tier referenced (its top-opening Ø), then editable per project.
                    double diam = (_masterCatalog.FindById(dt.SelectedBaseId) as BottomElementComponent)?.TopOpeningDiameterMm ?? 0;
                    var ct = new ConnDepthTier
                    {
                        MinDepthM = dt.MinDepthM, MaxDepthM = dt.MaxDepthM,
                        ManholeDiameterMm = diam, IsCastInSitu = dt.IsCastInSitu, Notes = dt.Notes ?? ""
                    };
                    foreach (var cc in dt.ComponentConstraints ?? new List<ComponentTypeConstraint>())
                        ct.ComponentConstraints.Add(new ComponentTypeConstraint
                        { Role = cc.Role, MinCount = cc.MinCount, MaxCount = cc.MaxCount });
                    rule.Tiers.Add(ct);
                }
                Model.ConnectionRules.Add(rule);
            }
            RebuildConnRules();
        }

        private void AddConnRule()
        {
            var rule = new ConnectionRule();
            Model.ConnectionRules.Add(rule);
            var vm = new ConnRuleVm(rule);
            ConnectionRules.Add(vm);
            SelectedConnRule = vm;
        }

        private void DeleteConnRule()
        {
            if (_selectedConnRule == null) return;
            Model.ConnectionRules.Remove(_selectedConnRule.Model);
            ConnectionRules.Remove(_selectedConnRule);
            SelectedConnRule = null;
        }

        private void AddTier()
        {
            if (_selectedConnRule == null) return;
            var t = new ConnDepthTier();
            _selectedConnRule.Model.Tiers.Add(t);
            var tvm = new ConnTierVm(t);
            _selectedConnRule.Tiers.Add(tvm);
            SelectedTier = tvm;
        }

        private void DeleteTier()
        {
            if (_selectedTier == null || _selectedConnRule == null) return;
            _selectedConnRule.Model.Tiers.Remove(_selectedTier.Model);
            _selectedConnRule.Tiers.Remove(_selectedTier);
            SelectedTier = null;
        }

        private void RebuildConnRules()
        {
            ConnectionRules.Clear();
            foreach (var r in Model.ConnectionRules) ConnectionRules.Add(new ConnRuleVm(r));
            SelectedConnRule = ConnectionRules.FirstOrDefault();
        }
    }

    /// <summary>ViewModel over one <see cref="ConnectionRule"/> (pipe-diameter band + its depth tiers).</summary>
    public sealed class ConnRuleVm : ViewModelBase
    {
        public ConnectionRule Model { get; }
        public ObservableCollection<ConnTierVm> Tiers { get; } = new ObservableCollection<ConnTierVm>();

        public ConnRuleVm(ConnectionRule model)
        {
            Model = model ?? throw new ArgumentNullException(nameof(model));
            foreach (var t in Model.Tiers) Tiers.Add(new ConnTierVm(t));
            Tiers.CollectionChanged += (s, e) => OnPropertyChanged(nameof(TierCount));
        }

        public double MinPipeMm
        {
            get => Model.MinPipeMm;
            set { Model.MinPipeMm = value; OnPropertyChanged(); OnPropertyChanged(nameof(RangeDisplay)); }
        }

        public double MaxPipeMm
        {
            get => Model.MaxPipeMm;
            set { Model.MaxPipeMm = value; OnPropertyChanged(); OnPropertyChanged(nameof(RangeDisplay)); }
        }

        public int TierCount => Tiers.Count;

        public bool IsRangeValid => MaxPipeMm <= 0 || MinPipeMm <= MaxPipeMm;

        public string RangeDisplay => string.Format("{0:0}–{1:0} mm", MinPipeMm, MaxPipeMm);
    }

    /// <summary>ViewModel over one <see cref="ConnDepthTier"/> (depth band → manhole diameter).</summary>
    public sealed class ConnTierVm : ViewModelBase
    {
        public ConnDepthTier Model { get; }
        public ConnTierVm(ConnDepthTier model) { Model = model ?? throw new ArgumentNullException(nameof(model)); }

        public double MinDepthM         { get => Model.MinDepthM;         set { Model.MinDepthM = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDepthValid)); } }
        public double MaxDepthM         { get => Model.MaxDepthM;         set { Model.MaxDepthM = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsDepthValid)); } }
        public double ManholeDiameterMm { get => Model.ManholeDiameterMm; set { Model.ManholeDiameterMm = value; OnPropertyChanged(); } }
        public bool   IsCastInSitu      { get => Model.IsCastInSitu;      set { Model.IsCastInSitu = value; OnPropertyChanged(); } }
        public string Notes             { get => Model.Notes;             set { Model.Notes = value; OnPropertyChanged(); } }

        public bool IsDepthValid => MaxDepthM <= 0 || MinDepthM <= MaxDepthM;
    }

    /// <summary>A "family — Ø" pick option for adding a piece-exclusion row.</summary>
    public sealed class FamilyDiaOption
    {
        public Guid   FamilyId   { get; set; }
        public string FamilyName { get; set; } = "";
        public double DiameterMm { get; set; }
        public string Display => string.Format("{0} — Ø{1:0} mm", FamilyName, DiameterMm);
    }

    /// <summary>One "remove pieces from use" row: a (family, diameter) with its roles.</summary>
    public sealed class PieceRowVm
    {
        public PieceRowVm(PieceExclusionRow model, string familyName)
        {
            Model      = model;
            FamilyName = familyName ?? "";
        }

        public PieceExclusionRow Model { get; }
        public string FamilyName { get; }
        public string Header => string.Format("{0} — Ø{1:0} mm", FamilyName, Model.ManholeDiameterMm);
        public ObservableCollection<RolePieceVm> Roles { get; } = new ObservableCollection<RolePieceVm>();
    }

    /// <summary>One manhole role (Gövde/Boyun/…) with a checkbox per available height.</summary>
    public sealed class RolePieceVm
    {
        public RolePieceVm(ComponentRole role, string roleName, IEnumerable<double> availableHeights,
                           HashSet<double> allowedSet, bool hasRestriction, Action<RolePieceVm> onChanged)
        {
            Role     = role;
            RoleName = roleName;
            foreach (var h in availableHeights)
            {
                bool isAllowed = !hasRestriction || allowedSet.Contains(h);
                Heights.Add(new HeightOptVm(h, isAllowed, _ => onChanged?.Invoke(this)));
            }
        }

        public ComponentRole Role     { get; }
        public string        RoleName { get; }
        public ObservableCollection<HeightOptVm> Heights { get; } = new ObservableCollection<HeightOptVm>();
    }

    /// <summary>One selectable excavation-rule name (a checkbox) used as a per-network filter.</summary>
    public sealed class RuleNameFilterVm : ViewModelBase
    {
        private readonly Action<RuleNameFilterVm> _onChanged;

        public RuleNameFilterVm(string name, bool isSelected, Action<RuleNameFilterVm> onChanged)
        {
            Name       = name ?? "";
            _isSelected = isSelected;
            _onChanged = onChanged;
        }

        public string Name { get; }

        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set { if (Set(ref _isSelected, value)) _onChanged?.Invoke(this); }
        }
    }

    /// <summary>One selectable height (a checkbox) inside a <see cref="RolePieceVm"/>.</summary>
    public sealed class HeightOptVm : ViewModelBase
    {
        private readonly Action<HeightOptVm> _onChanged;

        public HeightOptVm(double heightMm, bool isAllowed, Action<HeightOptVm> onChanged)
        {
            HeightMm    = heightMm;
            _isAllowed  = isAllowed;
            _onChanged  = onChanged;
        }

        public double HeightMm { get; }
        public string Label => HeightMm.ToString("0");

        private bool _isAllowed;
        public bool IsAllowed
        {
            get => _isAllowed;
            set { if (Set(ref _isAllowed, value)) _onChanged?.Invoke(this); }
        }
    }

    /// <summary>
    /// Root ViewModel for the redesigned "Proje Kurulumu (DWG)" tab — the per-network project rule
    /// source. AutoCAD-agnostic like the other Proje Ayarları tabs: the network list, the loaded
    /// <see cref="ProjectRuleSet"/> and a save-to-DWG callback are injected via <see cref="Initialize"/>.
    /// Step 2 covers the mode switch + per-network pipe family/class + manhole family; connection
    /// rules, piece exclusions and exceptions arrive in later phases.
    /// </summary>
    public sealed class ProjectRulesTabVm : ViewModelBase
    {
        private readonly PipeCatalog _pipeCatalog;
        private readonly SmartAssemblyMasterCatalog _masterCatalog;
        private readonly ObservableCollection<ComponentFamily> _manholeFamilies;

        private ProjectRuleSet _ruleSet = new ProjectRuleSet();
        private Action<ProjectRuleSet> _saveToDwg;
        private Func<List<NetworkSeed>> _reloadSeeds;
        private Action<string, ExceptionEntityKind, string, Action<List<string>>> _pickEntities;
        private Func<Dictionary<string, string>> _loadEntityNames;
        private Action _runExtractXml;
        private Dictionary<string, string> _entityNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private List<NetworkSeed> _seeds = new List<NetworkSeed>();

        public ObservableCollection<NetworkRuleRowVm> Networks { get; } = new ObservableCollection<NetworkRuleRowVm>();

        private NetworkRuleRowVm _selectedNetwork;
        public NetworkRuleRowVm SelectedNetwork
        {
            get => _selectedNetwork;
            set
            {
                Set(ref _selectedNetwork, value);
                OnPropertyChanged(nameof(IsNetworkSelected));
                RebuildExceptions();   // exceptions are per-network — refresh the list for this network
            }
        }

        public bool IsNetworkSelected => _selectedNetwork != null;

        /// <summary>Exceptions of the currently selected network (null when none selected).</summary>
        private ProjectExceptions CurExc => _selectedNetwork?.Model.Exceptions;

        // ── Exclusive mode switch ─────────────────────────────────────────────
        public bool IsRulesMode
        {
            get => _ruleSet.CalcMode == CalcMode.Rules;
            set
            {
                if (value && _ruleSet.CalcMode != CalcMode.Rules)
                {
                    _ruleSet.CalcMode = CalcMode.Rules;
                    RaiseModeChanged();
                }
            }
        }

        public bool IsTypeMappingMode
        {
            get => _ruleSet.CalcMode == CalcMode.TypeMapping;
            set
            {
                if (value && _ruleSet.CalcMode != CalcMode.TypeMapping)
                {
                    _ruleSet.CalcMode = CalcMode.TypeMapping;
                    RaiseModeChanged();
                }
            }
        }

        public string ModeSummary => _ruleSet.CalcMode == CalcMode.Rules
            ? "Aktif mod: BU SAYFANIN KURALLARI. Tür Eşleştirme yok sayılır."
            : "Aktif mod: TÜR EŞLEŞTİRME. Bu sayfanın kuralları yok sayılır.";

        private string _statusText = "";
        public string StatusText { get => _statusText; set => Set(ref _statusText, value); }

        public ICommand SaveCommand      { get; }
        public ICommand RefreshCommand   { get; }
        public ICommand ExportXmlCommand { get; }
        public ICommand ImportXmlCommand { get; }

        // ── Exceptions ────────────────────────────────────────────────────────
        private static readonly ObservableCollection<PipeFamily>      _emptyPipeFamilies    = new ObservableCollection<PipeFamily>();
        private static readonly ObservableCollection<ComponentFamily> _emptyManholeFamilies = new ObservableCollection<ComponentFamily>();
        private readonly ObservableCollection<string> _excPipeSinifs = new ObservableCollection<string>();

        public ObservableCollection<ExceptionRowVm> Exceptions { get; } = new ObservableCollection<ExceptionRowVm>();

        public ObservableCollection<PipeFamily>      AvailableExcPipeFamilies    => _pipeCatalog?.Families ?? _emptyPipeFamilies;
        public ObservableCollection<string>          ExcPipeSinifs               => _excPipeSinifs;
        public ObservableCollection<ComponentFamily> AvailableExcManholeFamilies => _manholeFamilies ?? _emptyManholeFamilies;

        private PipeFamily _excPipeFamily;
        public PipeFamily ExcPipeFamily
        {
            get => _excPipeFamily;
            set
            {
                if (!Set(ref _excPipeFamily, value)) return;
                _excPipeSinifs.Clear();
                if (value != null)
                    foreach (var s in value.Pipes.Select(p => p.Sinif ?? "").Distinct().OrderBy(s => s))
                        _excPipeSinifs.Add(s);
                ExcPipeSinif = null;
            }
        }

        private string _excPipeSinif;
        public string ExcPipeSinif { get => _excPipeSinif; set => Set(ref _excPipeSinif, value); }

        private readonly ObservableCollection<double> _excManholeDiameters = new ObservableCollection<double>();
        public ObservableCollection<double> AvailableExcManholeDiameters => _excManholeDiameters;

        private ComponentFamily _excManholeFamily;
        public ComponentFamily ExcManholeFamily
        {
            get => _excManholeFamily;
            set
            {
                if (!Set(ref _excManholeFamily, value)) return;
                _excManholeDiameters.Clear();
                if (value != null)
                    foreach (var d in value.Components.OfType<BottomElementComponent>()
                                 .Select(b => b.TopOpeningDiameterMm).Where(d => d > 0)
                                 .Distinct().OrderBy(d => d))
                        _excManholeDiameters.Add(d);
                ExcManholeDiameter = _excManholeDiameters.FirstOrDefault();
            }
        }

        private double _excManholeDiameter;
        public double ExcManholeDiameter { get => _excManholeDiameter; set => Set(ref _excManholeDiameter, value); }

        // ── Excavation exceptions (Zemin Tipi + Kural Adı override) ───────────
        private const string AllRulesSentinel = "(Tümü)";

        private ObservableCollection<string> _availableExcSoils;
        public ObservableCollection<string> AvailableExcSoils
        {
            get
            {
                if (_availableExcSoils == null)
                    _availableExcSoils = new ObservableCollection<string>(
                        SoilCatalog.Services.SoilCatalogStore.Items
                            .Select(s => s.SoilName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct());
                return _availableExcSoils;
            }
        }

        public ObservableCollection<string> ExcavPipeRuleOptions { get; } = new ObservableCollection<string>();
        public ObservableCollection<string> ExcavMhRuleOptions   { get; } = new ObservableCollection<string>();

        private string _excExcavPipeSoil;
        public string ExcExcavPipeSoil
        {
            get => _excExcavPipeSoil;
            set { if (Set(ref _excExcavPipeSoil, value)) RebuildExcavPipeRuleOptions(); }
        }
        private string _excExcavPipeRule = AllRulesSentinel;
        public string ExcExcavPipeRule { get => _excExcavPipeRule; set => Set(ref _excExcavPipeRule, value); }

        private string _excExcavMhSoil;
        public string ExcExcavMhSoil
        {
            get => _excExcavMhSoil;
            set { if (Set(ref _excExcavMhSoil, value)) RebuildExcavMhRuleOptions(); }
        }
        private string _excExcavMhRule = AllRulesSentinel;
        public string ExcExcavMhRule { get => _excExcavMhRule; set => Set(ref _excExcavMhRule, value); }

        private void RebuildExcavPipeRuleOptions()
        {
            ExcavPipeRuleOptions.Clear();
            ExcavPipeRuleOptions.Add(AllRulesSentinel);
            foreach (var n in PipeTrenchCatalogStore.Current
                         .Where(r => SoilMatchesFilter(r.SelectedSoilNames, _excExcavPipeSoil))
                         .Select(r => r.RuleName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n))
                ExcavPipeRuleOptions.Add(n);
            ExcExcavPipeRule = AllRulesSentinel;
        }

        private void RebuildExcavMhRuleOptions()
        {
            ExcavMhRuleOptions.Clear();
            ExcavMhRuleOptions.Add(AllRulesSentinel);
            foreach (var n in ManholeExcavationCatalogStore.Current
                         .Where(r => SoilMatchesFilter(r.SelectedSoilNames, _excExcavMhSoil))
                         .Select(r => r.RuleName).Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().OrderBy(n => n))
                ExcavMhRuleOptions.Add(n);
            ExcExcavMhRule = AllRulesSentinel;
        }

        private static bool SoilMatchesFilter(List<string> ruleSoils, string soil)
            => ruleSoils == null || ruleSoils.Count == 0 || string.IsNullOrEmpty(soil) || ruleSoils.Contains(soil);

        public ICommand AddPipeExceptionCommand    { get; }
        public ICommand AddManholeExceptionCommand { get; }
        public ICommand AddPipeExcavExceptionCommand    { get; }
        public ICommand AddManholeExcavExceptionCommand { get; }
        public ICommand DeleteExceptionCommand     { get; }
        public ICommand RefreshExceptionNamesCommand { get; }
        public ICommand ExtractXmlCommand            { get; }

        public ProjectRulesTabVm(PipeCatalog pipeCatalog, SmartAssemblyMasterCatalog masterCatalog)
        {
            _pipeCatalog     = pipeCatalog;
            _masterCatalog   = masterCatalog;
            _manholeFamilies = masterCatalog?.Families;

            SaveCommand      = new RelayCommand(_ => Save(),      _ => _saveToDwg != null);
            RefreshCommand   = new RelayCommand(_ => Refresh(),   _ => _reloadSeeds != null);
            ExportXmlCommand = new RelayCommand(_ => ExportXml(), _ => Networks.Count > 0);
            ImportXmlCommand = new RelayCommand(_ => ImportXml());

            AddPipeExceptionCommand    = new RelayCommand(_ => AddPipeException(),    _ => _pickEntities != null && _excPipeFamily != null && _selectedNetwork != null);
            AddManholeExceptionCommand = new RelayCommand(_ => AddManholeException(), _ => _pickEntities != null && _excManholeFamily != null && _excManholeDiameter > 0 && _selectedNetwork != null);
            AddPipeExcavExceptionCommand    = new RelayCommand(_ => AddPipeExcavException(),    _ => _pickEntities != null && !string.IsNullOrEmpty(_excExcavPipeSoil) && _selectedNetwork != null);
            AddManholeExcavExceptionCommand = new RelayCommand(_ => AddManholeExcavException(), _ => _pickEntities != null && !string.IsNullOrEmpty(_excExcavMhSoil)   && _selectedNetwork != null);
            DeleteExceptionCommand     = new RelayCommand(DeleteException, r => r is ExceptionRowVm);
            RefreshExceptionNamesCommand = new RelayCommand(_ => RefreshExceptionNames(), _ => _loadEntityNames != null && _selectedNetwork != null);
            ExtractXmlCommand            = new RelayCommand(_ => _runExtractXml?.Invoke(), _ => _runExtractXml != null);
        }

        /// <summary>
        /// Supplies the DWG-bound pieces: the discovered networks (name + active flag), the loaded
        /// rule set, a save callback that writes to the active DWG NOD, and a reload delegate the
        /// "Yenile" button uses to re-read the network list + live active state from the DWG.
        /// </summary>
        public void Initialize(List<NetworkSeed> seeds, ProjectRuleSet ruleSet,
                               Action<ProjectRuleSet> saveToDwg, Func<List<NetworkSeed>> reloadSeeds = null,
                               Action<string, ExceptionEntityKind, string, Action<List<string>>> pickEntities = null,
                               Func<Dictionary<string, string>> loadEntityNames = null,
                               Action runExtractXml = null)
        {
            _seeds           = seeds ?? new List<NetworkSeed>();
            _ruleSet         = ruleSet ?? new ProjectRuleSet();
            _saveToDwg       = saveToDwg;
            _reloadSeeds     = reloadSeeds;
            _pickEntities    = pickEntities;
            _loadEntityNames = loadEntityNames;
            _runExtractXml   = runExtractXml;
            _entityNames     = _loadEntityNames?.Invoke() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            RebuildNetworks();
            RebuildExceptions();
            RaiseModeChanged();
            CommandManager.InvalidateRequerySuggested();
        }

        /// <summary>Re-reads the network list + live active flags from the DWG (Yenile button).</summary>
        private void Refresh()
        {
            if (_reloadSeeds == null) return;
            _seeds = _reloadSeeds() ?? new List<NetworkSeed>();
            RebuildNetworks();
        }

        private void RebuildNetworks()
        {
            // Preserve the user's current selection across a rebuild (Yenile / import).
            string keepSelected = _selectedNetwork?.SystemName;

            Networks.Clear();

            // Seed each discovered network with its existing rule (or a fresh one). When the network
            // scrape is momentarily empty we leave _ruleSet.NetworkRules untouched so a later Save
            // can't wipe previously-saved rules.
            if (_seeds.Count > 0)
            {
                var resolved = new List<NetworkRule>();
                foreach (var seed in _seeds)
                {
                    var rule = _ruleSet.FindNetwork(seed.Name) ?? new NetworkRule { SystemName = seed.Name };
                    resolved.Add(rule);
                    Networks.Add(new NetworkRuleRowVm(rule, _pipeCatalog, _masterCatalog, seed.IsActive));
                }
                _ruleSet.NetworkRules = resolved;
            }

            SelectedNetwork = (keepSelected != null
                ? Networks.FirstOrDefault(n => string.Equals(n.SystemName, keepSelected, StringComparison.Ordinal))
                : null) ?? Networks.FirstOrDefault();

            int activeCount = _seeds.Count(s => s.IsActive);
            StatusText = Networks.Count == 0
                ? "Ağ bulunamadı — önce Ağ panelinde (UT_NET_PANEL) ağları tarayın/yenileyin."
                : string.Format("{0} ağ yüklendi ({1} aktif).", Networks.Count, activeCount);
        }

        private void RaiseModeChanged()
        {
            OnPropertyChanged(nameof(IsRulesMode));
            OnPropertyChanged(nameof(IsTypeMappingMode));
            OnPropertyChanged(nameof(ModeSummary));
        }

        private void Save()
        {
            _saveToDwg?.Invoke(_ruleSet);
            int excCount = _ruleSet.NetworkRules.Sum(
                n => (n.Exceptions?.PipeFamily.Count ?? 0) + (n.Exceptions?.ManholeFamily.Count ?? 0));
            StatusText = string.Format("Kaydedildi: {0} ağ, {1} istisna, mod = {2}.",
                _ruleSet.NetworkRules.Count, excCount, _ruleSet.CalcMode);
        }

        // ── Exceptions: add via drawing pick, conflict-checked per dimension ───

        private void AddPipeException()
        {
            if (_pickEntities == null || _excPipeFamily == null || _selectedNetwork == null) return;
            Guid   famId = _excPipeFamily.Id;
            string sinif = _excPipeSinif ?? "";
            string label = _excPipeFamily.FamilyName + (string.IsNullOrEmpty(sinif) ? "" : " / " + sinif);
            _pickEntities("\nBoru istisnası uygulanacak boruları seçin (yalnızca çizgi/polyline): ",
                          ExceptionEntityKind.Pipe, _selectedNetwork.SystemName,
                          guids => ApplyPipeException(guids, famId, sinif, label));
        }

        private void ApplyPipeException(List<string> guids, Guid famId, string sinif, string label)
        {
            if (guids == null || guids.Count == 0) { StatusText = "İstisna: hiç öğe seçilmedi."; return; }

            var exc = CurExc;
            if (exc == null) return;
            bool replaceConflicts = ResolveConflicts(guids, g => exc.FindPipe(g) != null, "BORU");

            int added = 0, updated = 0, skipped = 0;
            foreach (var g in guids)
            {
                var existing = exc.FindPipe(g);
                if (existing != null)
                {
                    if (!replaceConflicts) { skipped++; continue; }
                    existing.PipeFamilyId = famId; existing.PipeSinif = sinif; existing.OverrideLabel = label;
                    updated++;
                }
                else
                {
                    exc.PipeFamily.Add(new PipeFamilyException
                    {
                        AgGuid = g, PipeFamilyId = famId, PipeSinif = sinif, OverrideLabel = label,
                        EntityName = LookupName(g)
                    });
                    added++;
                }
            }
            RebuildExceptions();
            StatusText = string.Format("Boru istisnası: {0} eklendi, {1} güncellendi, {2} atlandı. (Kaydet ile DWG'ye yazın)",
                added, updated, skipped);
        }

        private void AddManholeException()
        {
            if (_pickEntities == null || _excManholeFamily == null || _excManholeDiameter <= 0 || _selectedNetwork == null) return;
            Guid   famId = _excManholeFamily.Id;
            double dia   = _excManholeDiameter;
            string label = string.Format("{0} — Ø{1:0} mm", _excManholeFamily.Name, dia);
            _pickEntities("\nBaca istisnası uygulanacak bacaları seçin (yalnızca blok/daire): ",
                          ExceptionEntityKind.Manhole, _selectedNetwork.SystemName,
                          guids => ApplyManholeException(guids, famId, dia, label));
        }

        private void ApplyManholeException(List<string> guids, Guid famId, double dia, string label)
        {
            if (guids == null || guids.Count == 0) { StatusText = "İstisna: hiç öğe seçilmedi."; return; }

            var exc = CurExc;
            if (exc == null) return;
            bool replaceConflicts = ResolveConflicts(guids, g => exc.FindManhole(g) != null, "BACA");

            int added = 0, updated = 0, skipped = 0;
            foreach (var g in guids)
            {
                var existing = exc.FindManhole(g);
                if (existing != null)
                {
                    if (!replaceConflicts) { skipped++; continue; }
                    existing.ManholeFamilyId = famId; existing.ManholeDiameterMm = dia; existing.OverrideLabel = label;
                    updated++;
                }
                else
                {
                    exc.ManholeFamily.Add(new ManholeFamilyException
                    { AgGuid = g, ManholeFamilyId = famId, ManholeDiameterMm = dia, OverrideLabel = label, EntityName = LookupName(g) });
                    added++;
                }
            }
            RebuildExceptions();
            _selectedNetwork?.RebuildPieceRows();   // exception families feed the piece-exclusion options
            StatusText = string.Format("Baca istisnası: {0} eklendi, {1} güncellendi, {2} atlandı. (Kaydet ile DWG'ye yazın)",
                added, updated, skipped);
        }

        // ── Excavation exceptions (soil + rule-name) ──────────────────────────

        private void AddPipeExcavException()
        {
            if (_pickEntities == null || string.IsNullOrEmpty(_excExcavPipeSoil) || _selectedNetwork == null) return;
            string soil  = _excExcavPipeSoil;
            var    names = (string.IsNullOrEmpty(_excExcavPipeRule) || _excExcavPipeRule == AllRulesSentinel)
                ? new List<string>() : new List<string> { _excExcavPipeRule };
            string label = soil + " / " + (names.Count > 0 ? names[0] : AllRulesSentinel);
            _pickEntities("\nBoru kazı istisnası uygulanacak boruları seçin (yalnızca çizgi/polyline): ",
                          ExceptionEntityKind.Pipe, _selectedNetwork.SystemName,
                          guids => ApplyExcavException(guids, CurExc?.PipeExcav, g => CurExc?.FindPipeExcav(g), soil, names, label, "BORU KAZI"));
        }

        private void AddManholeExcavException()
        {
            if (_pickEntities == null || string.IsNullOrEmpty(_excExcavMhSoil) || _selectedNetwork == null) return;
            string soil  = _excExcavMhSoil;
            var    names = (string.IsNullOrEmpty(_excExcavMhRule) || _excExcavMhRule == AllRulesSentinel)
                ? new List<string>() : new List<string> { _excExcavMhRule };
            string label = soil + " / " + (names.Count > 0 ? names[0] : AllRulesSentinel);
            _pickEntities("\nBaca kazı istisnası uygulanacak bacaları seçin (yalnızca blok/daire): ",
                          ExceptionEntityKind.Manhole, _selectedNetwork.SystemName,
                          guids => ApplyExcavException(guids, CurExc?.ManholeExcav, g => CurExc?.FindManholeExcav(g), soil, names, label, "BACA KAZI"));
        }

        private void ApplyExcavException(List<string> guids, List<ExcavException> list,
            Func<string, ExcavException> find, string soil, List<string> names, string label, string dimTr)
        {
            if (list == null) return;
            if (guids == null || guids.Count == 0) { StatusText = "İstisna: hiç öğe seçilmedi."; return; }

            bool replaceConflicts = ResolveConflicts(guids, g => find(g) != null, dimTr);
            int added = 0, updated = 0, skipped = 0;
            foreach (var g in guids)
            {
                var existing = find(g);
                if (existing != null)
                {
                    if (!replaceConflicts) { skipped++; continue; }
                    existing.SoilName = soil; existing.RuleNames = new List<string>(names); existing.OverrideLabel = label;
                    updated++;
                }
                else
                {
                    list.Add(new ExcavException
                    { AgGuid = g, SoilName = soil, RuleNames = new List<string>(names), OverrideLabel = label, EntityName = LookupName(g) });
                    added++;
                }
            }
            RebuildExceptions();
            StatusText = string.Format("{0} istisnası: {1} eklendi, {2} güncellendi, {3} atlandı. (Kaydet ile DWG'ye yazın)",
                dimTr, added, updated, skipped);
        }

        /// <summary>
        /// Per-dimension conflict prompt (decision 3): if any picked entity already has an exception
        /// in THIS dimension, ask once whether to replace them all or keep the old ones. Returns true
        /// to replace conflicting entries, false to keep the old ones (skip those entities).
        /// </summary>
        private static bool ResolveConflicts(List<string> guids, Func<string, bool> hasExisting, string dimTr)
        {
            int conflicts = guids.Count(hasExisting);
            if (conflicts == 0) return true;
            var r = System.Windows.MessageBox.Show(
                string.Format("Seçilen öğelerden {0} tanesinde zaten bir {1} istisnası var.\n\n" +
                              "Evet = yenisiyle değiştir\nHayır = eskisini koru (bu öğeleri atla)",
                              conflicts, dimTr),
                "Çakışan istisna", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question);
            return r == System.Windows.MessageBoxResult.Yes;
        }

        private void DeleteException(object param)
        {
            if (!(param is ExceptionRowVm row)) return;
            var exc = CurExc;
            if (exc == null) return;
            if (row.Model is PipeFamilyException pe)    exc.PipeFamily.Remove(pe);
            if (row.Model is ManholeFamilyException me) exc.ManholeFamily.Remove(me);
            if (row.Model is ExcavException ee) { exc.PipeExcav.Remove(ee); exc.ManholeExcav.Remove(ee); }
            RebuildExceptions();
            _selectedNetwork?.RebuildPieceRows();
            StatusText = "İstisna silindi. (Kaydet ile DWG'ye yazın)";
        }

        private string LookupName(string agGuid)
            => agGuid != null && _entityNames.TryGetValue(agGuid, out var n) ? n : "";

        /// <summary>
        /// Re-reads the exported XML and refreshes each exception's cached name. Entities that no
        /// longer exist in the XML are removed automatically (user directive). No-op with a hint if
        /// no XML has been extracted yet.
        /// </summary>
        private void RefreshExceptionNames()
        {
            if (_loadEntityNames == null) return;
            var map = _loadEntityNames() ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (map.Count == 0)
            {
                StatusText = "İsim güncelleme: XML bulunamadı — önce Metraj Verisi çıkarın (UT_BOQ).";
                return;
            }
            _entityNames = map;

            var exc = CurExc;
            if (exc == null) return;
            int resolved = 0, removed = 0;

            var pipeKeep = new List<PipeFamilyException>();
            foreach (var e in exc.PipeFamily)
            {
                if (map.TryGetValue(e.AgGuid ?? "", out var name)) { e.EntityName = name; pipeKeep.Add(e); resolved++; }
                else removed++;
            }
            exc.PipeFamily = pipeKeep;

            var mhKeep = new List<ManholeFamilyException>();
            foreach (var e in exc.ManholeFamily)
            {
                if (map.TryGetValue(e.AgGuid ?? "", out var name)) { e.EntityName = name; mhKeep.Add(e); resolved++; }
                else removed++;
            }
            exc.ManholeFamily = mhKeep;

            exc.PipeExcav    = PruneExcav(exc.PipeExcav,    map, ref resolved, ref removed);
            exc.ManholeExcav = PruneExcav(exc.ManholeExcav, map, ref resolved, ref removed);

            RebuildExceptions();
            _selectedNetwork?.RebuildPieceRows();
            StatusText = string.Format("İsimler güncellendi: {0} çözüldü, {1} silindi (XML'de yok). (Kaydet ile DWG'ye yazın)",
                resolved, removed);
        }

        private static List<ExcavException> PruneExcav(
            List<ExcavException> list, Dictionary<string, string> map, ref int resolved, ref int removed)
        {
            var keep = new List<ExcavException>();
            foreach (var e in list)
            {
                if (map.TryGetValue(e.AgGuid ?? "", out var name)) { e.EntityName = name; keep.Add(e); resolved++; }
                else removed++;
            }
            return keep;
        }

        private void RebuildExceptions()
        {
            Exceptions.Clear();
            var exc = CurExc;
            if (exc == null) return;
            foreach (var e in exc.PipeFamily)
                Exceptions.Add(new ExceptionRowVm("Boru", e.AgGuid, e.EntityName, e.OverrideLabel, e));
            foreach (var e in exc.ManholeFamily)
                Exceptions.Add(new ExceptionRowVm("Baca", e.AgGuid, e.EntityName, e.OverrideLabel, e));
            foreach (var e in exc.PipeExcav)
                Exceptions.Add(new ExceptionRowVm("Boru Kazı", e.AgGuid, e.EntityName, e.OverrideLabel, e));
            foreach (var e in exc.ManholeExcav)
                Exceptions.Add(new ExceptionRowVm("Baca Kazı", e.AgGuid, e.EntityName, e.OverrideLabel, e));
        }

        private void ExportXml()
        {
            var dlg = new SaveFileDialog
            {
                Title    = "Proje Kurallarını Dışa Aktar",
                Filter   = "XML dosyası (*.xml)|*.xml",
                FileName = "ProjeKurallari.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                ProjectRulesXmlManager.Export(_ruleSet, dlg.FileName);
                StatusText = "Dışa aktarıldı: " + dlg.FileName;
            }
            catch (Exception ex) { StatusText = "Dışa aktarma hatası: " + ex.Message; }
        }

        private void ImportXml()
        {
            var dlg = new OpenFileDialog
            {
                Title  = "Proje Kurallarını İçe Aktar",
                Filter = "XML dosyası (*.xml)|*.xml"
            };
            if (dlg.ShowDialog() != true) return;
            try
            {
                var imported = ProjectRulesXmlManager.Import(dlg.FileName);
                // Merge imported per-network defaults into the current rule set by SystemName; the
                // network list itself still comes from the DWG (imported networks not in this
                // drawing are ignored). Mode is adopted from the file.
                _ruleSet.CalcMode = imported.CalcMode;
                foreach (var impNet in imported.NetworkRules)
                {
                    var target = _ruleSet.FindNetwork(impNet.SystemName);
                    if (target == null) continue;
                    target.PipeFamilyId    = impNet.PipeFamilyId;
                    target.PipeSinif       = impNet.PipeSinif;
                    target.ManholeFamilyId = impNet.ManholeFamilyId;
                }
                RebuildNetworks();
                RaiseModeChanged();
                StatusText = "İçe aktarıldı: " + dlg.FileName;
            }
            catch (Exception ex) { StatusText = "İçe aktarma hatası: " + ex.Message; }
        }
    }
}
