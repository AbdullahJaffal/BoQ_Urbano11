using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Sections.UI
{
    public abstract class Notify : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;
        protected void Raise([CallerMemberName] string n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
        protected bool Set<T>(ref T field, T value, [CallerMemberName] string n = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value; Raise(n); return true;
        }
    }

    public sealed class Choice<T>
    {
        public T      Value { get; set; }
        public string Label { get; set; }
        public override string ToString() => Label;
    }

    // ── Rows ─────────────────────────────────────────────────────────────────

    /// <summary>One drawable element in the Görünüm grid (wraps the style's element so bulk edit + refresh work).</summary>
    public sealed class ElementRowVm : Notify
    {
        public SectionElementStyle E { get; }
        public ElementRowVm(SectionElementStyle e) { E = e; }

        public string Name => SectionElements.DisplayName(E.Key);
        public bool   Visible { get => E.Visible; set { E.Visible = value; Raise(); } }
        public string Layer   { get => E.Layer;   set { E.Layer = value; Raise(); } }

        public short LayerColorAci { get => E.LayerColorAci; set { E.LayerColorAci = value; Raise(); Raise(nameof(LayerColorText)); } }
        public string LayerColorText => E.LayerColorAci.ToString(CultureInfo.InvariantCulture);

        /// <summary>Entity colour: 256 = ByLayer, 1..255 = ACI, or TrueColor (RGB).</summary>
        public short ColorAci { get => E.ColorAci; set { E.ColorAci = value; Raise(); Raise(nameof(ColorText)); } }
        public int   TrueColor { get => E.TrueColor; set { E.TrueColor = value; Raise(); Raise(nameof(ColorText)); } }
        public string ColorText => E.TrueColor >= 0 ? $"RGB {(E.TrueColor >> 16) & 255},{(E.TrueColor >> 8) & 255},{E.TrueColor & 255}"
                                 : E.ColorAci == 256 ? "ByLayer" : E.ColorAci.ToString(CultureInfo.InvariantCulture);

        public string Linetype { get => E.Linetype; set { E.Linetype = string.IsNullOrWhiteSpace(value) ? "ByLayer" : value.Trim(); Raise(); } }

        public string LineWeightText
        {
            get => E.LineWeight < 0 ? "ByLayer" : (E.LineWeight / 100.0).ToString("0.00", CultureInfo.InvariantCulture) + " mm";
            set
            {
                var s = (value ?? "").Replace("mm", "").Trim().Replace(',', '.');
                E.LineWeight = s.Length == 0 || s.Equals("ByLayer", StringComparison.OrdinalIgnoreCase) ? -1
                    : double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var mm) ? (int)Math.Round(mm * 100) : E.LineWeight;
                Raise();
            }
        }

        public string Hatch
        {
            get => string.IsNullOrEmpty(E.Hatch) ? "(yok)" : E.Hatch;
            set { var v = (value ?? "").Trim(); E.Hatch = v.Length == 0 || v == "(yok)" ? "" : v.ToUpperInvariant(); Raise(); }
        }
        public double HatchScale { get => E.HatchScale; set { E.HatchScale = value > 0 ? value : E.HatchScale; Raise(); } }
        public double HatchAngle { get => E.HatchAngle; set { E.HatchAngle = value; Raise(); } }
        public short  HatchColorAci { get => E.HatchColorAci; set { E.HatchColorAci = value; Raise(); Raise(nameof(HatchColorText)); } }
        public string HatchColorText => E.HatchColorAci == 256 ? "Öğe rengi" : E.HatchColorAci.ToString(CultureInfo.InvariantCulture);
        public int    HatchTransparency { get => E.HatchTransparency; set { E.HatchTransparency = Math.Max(0, Math.Min(90, value)); Raise(); } }

        public void RefreshAll() => Raise(string.Empty);
    }

    public sealed class TableRowVm : Notify
    {
        public SectionTableRow R { get; }
        public TableRowVm(SectionTableRow r) { R = r; }
        public string Label => SectionTableRows.Label(R.Key);
        public bool Visible { get => R.Visible; set { R.Visible = value; Raise(); } }
    }

    /// <summary>One catalog sub-layer's own hatch (the style entry is created on first edit).</summary>
    public sealed class SubLayerRowVm : Notify
    {
        private readonly SectionStyle _s;
        public string Name  { get; }
        public string Group { get; }
        public SubLayerRowVm(SectionStyle s, string name, string group) { _s = s; Name = name; Group = group; }

        private SubLayerStyle Get() => _s.SubLayers.TryGetValue(Name, out var v) ? v : null;
        private SubLayerStyle Edit()
        {
            var v = Get();
            if (v == null) _s.SubLayers[Name] = v = new SubLayerStyle { Name = Name };
            return v;
        }

        public string Hatch
        {
            get { var h = Get()?.Hatch; return string.IsNullOrEmpty(h) ? "(grup)" : h; }
            set
            {
                var v = (value ?? "").Trim();
                if (v.Length == 0 || v == "(grup)" || v == "(yok)") { if (Get() != null) _s.SubLayers.Remove(Name); }
                else Edit().Hatch = v.ToUpperInvariant();
                Raise(string.Empty);
            }
        }
        public double HatchScale        { get => Get()?.HatchScale ?? 1.0; set { if (value > 0) Edit().HatchScale = value; Raise(); } }
        public double HatchAngle        { get => Get()?.HatchAngle ?? 0.0; set { Edit().HatchAngle = value; Raise(); } }
        public short  HatchColorAci     { get => Get()?.HatchColorAci ?? 256; set { Edit().HatchColorAci = value; Raise(); Raise(nameof(HatchColorText)); } }
        public string HatchColorText    => HatchColorAci == 256 ? "Grup rengi" : HatchColorAci.ToString(CultureInfo.InvariantCulture);
        public int    HatchTransparency { get => Get()?.HatchTransparency ?? 50; set { Edit().HatchTransparency = Math.Max(0, Math.Min(90, value)); Raise(); } }
    }

    public sealed class NetworkColorVm : Notify
    {
        private readonly SectionStyle _s;
        public string Network { get; }
        public NetworkColorVm(SectionStyle s, string network) { _s = s; Network = network; }
        public short Aci
        {
            get => _s.NetworkColors.TryGetValue(Network, out var a) ? a : (short)9;
            set { _s.NetworkColors[Network] = value; Raise(); Raise(nameof(AciText)); }
        }
        public string AciText => Aci.ToString(CultureInfo.InvariantCulture);
    }

    public sealed class PipeRowVm : Notify
    {
        public int    Index   { get; set; }
        public string Network { get; set; }
        public string Name    { get; set; }
        public string Key     { get; set; }
        public string Kol     { get; set; }
        public int    Diameter { get; set; }
        public double Length  { get; set; }
        public bool   Clash   { get; set; }
        private bool _sel = true;
        public bool   Selected { get => _sel; set => Set(ref _sel, value); }
        private int _count;
        public int    Count   { get => _count; set => Set(ref _count, value); }
        public string LengthText => Length.ToString("0.00", CultureInfo.InvariantCulture);
        public string DiameterText => Diameter > 0 ? "Ø" + Diameter.ToString(CultureInfo.InvariantCulture) : "";
    }

    // ── Main VM ──────────────────────────────────────────────────────────────

    public sealed class CrossSectionsVm : Notify
    {
        public CrossSectionSession Session { get; }

        public CrossSectionsVm(CrossSectionSession session, SectionStyle drawingStyle, SectionRunOptions run)
        {
            Session = session;
            Run = run ?? new SectionRunOptions();
            ReloadTemplates();
            LoadStyle(drawingStyle ?? SectionStyle.Default());

            var rows = session.Capture.Rows;
            var clashPipes = new HashSet<int>(session.Capture.PairZones.SelectMany(z => new[] { z.PipeA, z.PipeB }));
            var selNets  = new HashSet<string>(Run.Networks, StringComparer.OrdinalIgnoreCase);
            var selPipes = new HashSet<string>(Run.Pipes, StringComparer.OrdinalIgnoreCase);
            foreach (int i in session.SelectPipes(new SectionRunOptions()))
            {
                var r = rows[i];
                session.Kol.TryGetValue(i, out var kol);
                string key = SectionRunOptions.PipeKey(r.SystemName, r.StartNodeName, r.EndNodeName);
                Pipes.Add(new PipeRowVm
                {
                    Index = i, Network = r.SystemName, Name = SectionLayoutEngine.Sanitize(r.PipeName), Key = key,
                    Kol = kol?.KolName ?? "", Diameter = r.DiameterMm, Length = r.Length2D, Clash = clashPipes.Contains(i),
                    Selected = (selNets.Count == 0 || selNets.Contains(r.SystemName ?? "")) && (selPipes.Count == 0 || selPipes.Contains(key))
                });
            }
            foreach (var p in Pipes) p.PropertyChanged += (s, e) => { if (e.PropertyName == nameof(PipeRowVm.Selected)) PipesChanged(); };
            PreviewPipe = Pipes.FirstOrDefault(p => p.Selected && p.Clash) ?? Pipes.FirstOrDefault(p => p.Selected) ?? Pipes.FirstOrDefault();
            UpdateCounts();
        }

        // ── Choices (combos / radios) ────────────────────────────────────────
        public IReadOnlyList<Choice<TiePreference>> Scenarios { get; } = new[]
        {
            new Choice<TiePreference> { Value = TiePreference.Split,     Label = "%50 paylaşım (SP)" },
            new Choice<TiePreference> { Value = TiePreference.KeepUpper, Label = "Üst boru korur (KU)" },
            new Choice<TiePreference> { Value = TiePreference.KeepLower, Label = "Alt boru korur (KL)" },
            new Choice<TiePreference> { Value = TiePreference.Ignore,    Label = "Kesişim yok sayılır" },
        };
        public IReadOnlyList<Choice<ChainageFormat>> ChainageFormats { get; } = new[]
        {
            new Choice<ChainageFormat> { Value = ChainageFormat.Station, Label = "0+005.00" },
            new Choice<ChainageFormat> { Value = ChainageFormat.Metric,  Label = "5.00 m" },
        };
        public IReadOnlyList<Choice<AreaDisplay>> AreaDisplays { get; } = new[]
        {
            new Choice<AreaDisplay> { Value = AreaDisplay.Net,   Label = "Net (senaryoya göre)" },
            new Choice<AreaDisplay> { Value = AreaDisplay.Gross, Label = "Brüt" },
            new Choice<AreaDisplay> { Value = AreaDisplay.Both,  Label = "Net / Brüt" },
        };
        public IReadOnlyList<Choice<SectionOrdering>> Orderings { get; } = new[]
        {
            new Choice<SectionOrdering> { Value = SectionOrdering.RowPerPipe, Label = "Her boru yeni satırda" },
            new Choice<SectionOrdering> { Value = SectionOrdering.Continuous, Label = "Sürekli (sütun sayısına göre)" },
        };
        public IReadOnlyList<Choice<SectionGrouping>> Groupings { get; } = new[]
        {
            new Choice<SectionGrouping> { Value = SectionGrouping.Group, Label = "Grup (her kesit bir grup)" },
            new Choice<SectionGrouping> { Value = SectionGrouping.Block, Label = "Blok (her kesit bir blok)" },
            new Choice<SectionGrouping> { Value = SectionGrouping.Free,  Label = "Ayrı nesneler" },
        };
        public IReadOnlyList<string> Linetypes { get; } = new[]
            { "ByLayer", "Continuous", "DASHED", "DASHED2", "HIDDEN", "HIDDEN2", "CENTER", "CENTER2", "DASHDOT", "PHANTOM", "DOT", "DIVIDE", "BORDER" };
        public IReadOnlyList<string> LineWeights { get; } = new[]
            { "ByLayer", "0.00 mm", "0.05 mm", "0.09 mm", "0.13 mm", "0.15 mm", "0.18 mm", "0.20 mm", "0.25 mm", "0.30 mm", "0.35 mm",
              "0.40 mm", "0.50 mm", "0.53 mm", "0.60 mm", "0.70 mm", "0.80 mm", "0.90 mm", "1.00 mm", "1.20 mm", "1.40 mm", "2.00 mm" };
        public IReadOnlyList<string> Hatches { get; } = new[]
            { "(yok)", "SOLID", "ANSI31", "ANSI32", "ANSI33", "ANSI37", "AR-SAND", "AR-CONC", "GRAVEL", "EARTH", "DOTS", "CROSS", "NET", "HONEY", "LINE" };
        /// <summary>Sub-layer hatches — "(grup)" keeps the group element's hatch.</summary>
        public IReadOnlyList<string> SubLayerHatches { get; } = new[]
            { "(grup)", "SOLID", "ANSI31", "ANSI32", "ANSI33", "ANSI37", "AR-SAND", "AR-CONC", "GRAVEL", "EARTH", "DOTS", "CROSS", "NET", "HONEY", "LINE" };

        // ── Run options (per drawing) ────────────────────────────────────────
        public SectionRunOptions Run { get; }

        public bool UseProjectSettings
        {
            get => Run.UseProjectSettings;
            set { Run.UseProjectSettings = value; Raise(); Raise(nameof(CustomEnabled)); Raise(nameof(ProjectSettingsText)); PipesChanged(); }
        }
        public bool CustomEnabled => !Run.UseProjectSettings;
        public string ProjectSettingsText
        {
            get
            {
                var (k, d, iv) = Session.Resolve(new SectionRunOptions { UseProjectSettings = true });
                return $"Proje Ayarları: aralık {iv.ToString("0.0#", CultureInfo.InvariantCulture)} m · Kazı: {SectionLayoutEngine.ScenarioLabel(k)} · Dolgu: {SectionLayoutEngine.ScenarioLabel(d)}";
            }
        }
        public string IntervalText
        {
            get => SectionStationPlanner.SnapInterval(Run.Interval).ToString("0.0", CultureInfo.InvariantCulture);
            set
            {
                if (double.TryParse((value ?? "").Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                    Run.Interval = SectionStationPlanner.SnapInterval(v);
                Raise(); PipesChanged();
            }
        }
        public TiePreference ExcavScenario    { get => Run.ExcavScenario;    set { Run.ExcavScenario = value; Raise(); PipesChanged(); } }
        public TiePreference BackfillScenario { get => Run.BackfillScenario; set { Run.BackfillScenario = value; Raise(); PipesChanged(); } }
        public bool IncludeZoneBoundaries { get => Run.IncludeZoneBoundaries; set { Run.IncludeZoneBoundaries = value; Raise(); PipesChanged(); } }
        public bool OnlyClashZones { get => Run.OnlyClashZones; set { Run.OnlyClashZones = value; Raise(); PipesChanged(); } }
        public string ManualChainages { get => Run.ManualChainages; set { Run.ManualChainages = value ?? ""; Raise(); PipesChanged(); } }
        public double MinGap { get => Run.MinGap; set { Run.MinGap = Math.Max(0, value); Raise(); PipesChanged(); } }

        // ── Scenario comparison ──────────────────────────────────────────────
        public bool CompareScenarios
        {
            get => Run.CompareScenarios;
            set { Run.CompareScenarios = value; Raise(); PipesChanged(); RaiseWarnings(); }
        }
        private bool InSet(TiePreference p) => Run.CompareSet.Contains(p);
        private void Toggle(TiePreference p, bool on, string name)
        {
            if (on && !Run.CompareSet.Contains(p)) Run.CompareSet.Add(p);
            if (!on) Run.CompareSet.Remove(p);
            // Keep the UI order KU, KL, SP, Yok say.
            var order = new[] { TiePreference.KeepUpper, TiePreference.KeepLower, TiePreference.Split, TiePreference.Ignore };
            Run.CompareSet = order.Where(Run.CompareSet.Contains).ToList();
            Raise(name); PipesChanged(); RaiseWarnings();
        }
        public bool CompareKU     { get => InSet(TiePreference.KeepUpper); set => Toggle(TiePreference.KeepUpper, value, nameof(CompareKU)); }
        public bool CompareKL     { get => InSet(TiePreference.KeepLower); set => Toggle(TiePreference.KeepLower, value, nameof(CompareKL)); }
        public bool CompareSP     { get => InSet(TiePreference.Split);     set => Toggle(TiePreference.Split,     value, nameof(CompareSP)); }
        public bool CompareIgnore { get => InSet(TiePreference.Ignore);    set => Toggle(TiePreference.Ignore,    value, nameof(CompareIgnore)); }
        private int ScenarioCount => Session.ScenarioSet(Run).Count;

        private bool _replaceLast;
        /// <summary>Erase the previous run and draw at its insertion point (no pick).</summary>
        public bool ReplaceLast { get => _replaceLast; set => Set(ref _replaceLast, value); }
        public bool HasLastRun { get; set; }

        // ── Scope ────────────────────────────────────────────────────────────
        public ObservableCollection<PipeRowVm> Pipes { get; } = new ObservableCollection<PipeRowVm>();
        private string _filter = "";
        public string Filter { get => _filter; set { if (Set(ref _filter, value ?? "")) Raise(nameof(FilteredPipes)); } }
        public IEnumerable<PipeRowVm> FilteredPipes => Pipes.Where(p => _filter.Length == 0
            || (p.Name ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
            || (p.Network ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
            || (p.Kol ?? "").IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0
            || p.DiameterText.IndexOf(_filter, StringComparison.OrdinalIgnoreCase) >= 0);
        public IEnumerable<string> Networks => Pipes.Select(p => p.Network).Distinct(StringComparer.OrdinalIgnoreCase);

        public void SelectPipes(Func<PipeRowVm, bool> pred, bool value)
        {
            _bulk = true;
            foreach (var p in FilteredPipes.ToList()) if (pred(p)) p.Selected = value;
            _bulk = false;
            PipesChanged();
        }
        private bool _bulk;
        private void PipesChanged()
        {
            if (_bulk) return;
            UpdateCounts();
            Raise(nameof(PreviewPipes));
            RequestPreview?.Invoke();
        }

        private string _summary;
        public string Summary { get => _summary; private set => Set(ref _summary, value); }
        public int SectionCount { get; private set; }

        private void UpdateCounts()
        {
            var (_, _, interval) = Session.Resolve(Run);
            var plan = new SectionStationPlanner.Options
            {
                Interval = interval, IncludeZoneBoundaries = Run.IncludeZoneBoundaries, OnlyClashZones = Run.OnlyClashZones,
                Manual = Run.ParseManual(), MinGap = Run.MinGap
            };
            int total = 0, pipes = 0;
            foreach (var p in Pipes)
            {
                var row = Session.Capture.Rows[p.Index];
                p.Count = row.Length2D < 1e-6 ? 0 : SectionStationPlanner.Plan(row.Length2D, Session.Capture.PerPipeZones[p.Index], plan).Count;
                if (p.Selected) { total += p.Count; if (p.Count > 0) pipes++; }
            }
            int n = ScenarioCount;
            SectionCount = total * n;
            Summary = n > 1 ? $"{total} istasyon × {n} senaryo = {total * n} kesit · {pipes} boru"
                            : $"{total} kesit · {pipes} boru";
        }

        /// <summary>Writes the scope back into the run options (empty lists = everything).</summary>
        public void CommitScope()
        {
            Run.Networks.Clear(); Run.Pipes.Clear();
            if (Pipes.All(p => p.Selected)) return;
            var nets = Pipes.GroupBy(p => p.Network, StringComparer.OrdinalIgnoreCase).ToList();
            bool wholeNetworksOnly = nets.All(g => g.All(p => p.Selected) || g.All(p => !p.Selected));
            if (wholeNetworksOnly) Run.Networks.AddRange(nets.Where(g => g.All(p => p.Selected)).Select(g => g.Key));
            else Run.Pipes.AddRange(Pipes.Where(p => p.Selected).Select(p => p.Key));
        }

        /// <summary>Scope = exactly the pipes picked in the drawing (Çizimden seç).</summary>
        public int ApplySelection(HashSet<int> pipeIndices)
        {
            _bulk = true;
            foreach (var p in Pipes) p.Selected = pipeIndices.Contains(p.Index);
            _bulk = false;
            Filter = "";
            PipesChanged();
            if (PreviewPipe == null || !PreviewPipe.Selected) PreviewPipe = Pipes.FirstOrDefault(p => p.Selected && p.Count > 0);
            return Pipes.Count(p => p.Selected);
        }

        // ── Style / templates ────────────────────────────────────────────────
        private SectionStyle _style;
        public SectionStyle Style { get => _style; private set { _style = value; Raise(); } }
        public ObservableCollection<ElementRowVm>   Elements      { get; } = new ObservableCollection<ElementRowVm>();
        public ObservableCollection<TableRowVm>     TableRows     { get; } = new ObservableCollection<TableRowVm>();
        public ObservableCollection<NetworkColorVm> NetworkColors { get; } = new ObservableCollection<NetworkColorVm>();
        public ObservableCollection<SubLayerRowVm>  SubLayers     { get; } = new ObservableCollection<SubLayerRowVm>();
        public ObservableCollection<string>         Templates     { get; } = new ObservableCollection<string>();
        private List<SectionStyle> _templateStyles = new List<SectionStyle>();

        private string _templateName;
        public string TemplateName
        {
            get => _templateName;
            set
            {
                if (value == null || value == _templateName) return;
                var t = _templateStyles.FirstOrDefault(s => s.Name == value);
                if (t != null) LoadStyle(t.Clone());
                else { _templateName = value; Raise(); }
            }
        }

        public void LoadStyle(SectionStyle s)
        {
            Style = s;
            _templateName = s.Name;
            Raise(nameof(TemplateName));
            Elements.Clear();
            foreach (var key in SectionElements.All())
            {
                var row = new ElementRowVm(s.E(key));
                row.PropertyChanged += (o, e) => RequestPreview?.Invoke();
                Elements.Add(row);
            }
            TableRows.Clear();
            foreach (var r in s.TableRows)
            {
                var row = new TableRowVm(r);
                row.PropertyChanged += (o, e) => RequestPreview?.Invoke();
                TableRows.Add(row);
            }
            NetworkColors.Clear();
            foreach (var n in Session.ComputedNetworks.Concat(Session.UncomputedNetworks)) NetworkColors.Add(new NetworkColorVm(s, n));
            SubLayers.Clear();
            foreach (var (name, group) in SubLayerNames())
            {
                var row = new SubLayerRowVm(s, name, group);
                row.PropertyChanged += (o, e) => RequestPreview?.Invoke();
                SubLayers.Add(row);
            }
            Raise(nameof(CrossingMode)); Raise(nameof(PerNetworkColors));
            RefreshDirty();
            RequestPreview?.Invoke();
        }

        /// <summary>Distinct catalog sub-layers of the computed pipes (name + main group), bottom-up.</summary>
        private IEnumerable<(string Name, string Group)> SubLayerNames()
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var res = new List<(TrenchStackGroup G, string Name)>();
            foreach (var r in Session.Capture.Rows)
                foreach (var l in r.TrenchStack ?? new List<TrenchStackLayer>())
                    if (!string.IsNullOrEmpty(l.LayerName) && seen.Add(l.LayerName)) res.Add((l.Group, l.LayerName));
            string G(TrenchStackGroup g) => g == TrenchStackGroup.Yataklama ? "Yataklama" : g == TrenchStackGroup.GeriDolgu ? "Geri Dolgu" : "Gömlekleme";
            return res.OrderBy(x => (int)x.G).Select(x => (x.Name, G(x.G)));
        }

        /// <summary>Fills the per-network colours (e.g. from the networks' drawing layers).</summary>
        public void ApplyNetworkColors(IDictionary<string, short> colors)
        {
            foreach (var n in NetworkColors)
                if (colors.TryGetValue(n.Network, out var a) && a >= 1 && a <= 255) n.Aci = a;
            if (!PerNetworkColors) PerNetworkColors = true;
            RequestPreview?.Invoke();
        }

        public void ReloadTemplates()
        {
            _templateStyles = SectionStyleStore.LoadTemplates();
            Templates.Clear();
            foreach (var t in _templateStyles) Templates.Add(t.Name);
        }

        private bool _dirty;
        public bool IsDirty { get => _dirty; private set { if (Set(ref _dirty, value)) Raise(nameof(DirtyText)); } }
        public string DirtyText => _dirty ? "• kaydedilmemiş değişiklik var" : "";

        public void RefreshDirty()
        {
            var t = _templateStyles.FirstOrDefault(s => s.Name == Style.Name);
            IsDirty = t == null || !XNode.DeepEquals(t.ToXml(), Style.ToXml());
        }

        public void SaveTemplate(string name = null)
        {
            if (!string.IsNullOrWhiteSpace(name)) Style.Name = name.Trim();
            SectionStyleStore.SaveTemplate(Style);
            ReloadTemplates();
            _templateName = Style.Name; Raise(nameof(TemplateName));
            RefreshDirty();
        }

        public void DeleteTemplate()
        {
            SectionStyleStore.DeleteTemplate(Style.Name);
            ReloadTemplates();
            RefreshDirty();
        }

        public CrossingDisplayMode CrossingMode
        {
            get => Style.Crossing;
            set { Style.Crossing = value; Raise(); RequestPreview?.Invoke(); }
        }
        public bool PerNetworkColors
        {
            get => Style.OtherColorMode == OtherNetworkColorMode.PerNetwork;
            set { Style.OtherColorMode = value ? OtherNetworkColorMode.PerNetwork : OtherNetworkColorMode.Single; Raise(); RequestPreview?.Invoke(); }
        }

        public void MoveTableRow(TableRowVm row, int delta)
        {
            int i = TableRows.IndexOf(row), j = i + delta;
            if (i < 0 || j < 0 || j >= TableRows.Count) return;
            TableRows.Move(i, j);
            Style.TableRows = TableRows.Select(r => r.R).ToList();
            RequestPreview?.Invoke();
        }

        // ── Preview ──────────────────────────────────────────────────────────
        public Action RequestPreview;

        public IEnumerable<PipeRowVm> PreviewPipes => Pipes.Where(p => p.Selected && p.Count > 0);

        private PipeRowVm _previewPipe;
        public PipeRowVm PreviewPipe
        {
            get => _previewPipe;
            set
            {
                if (!Set(ref _previewPipe, value)) return;
                PreviewStationIndex = 0;
                Raise(nameof(PreviewStations));
                RequestPreview?.Invoke();
            }
        }

        public List<double> PreviewStations
        {
            get
            {
                if (_previewPipe == null) return new List<double>();
                var (_, _, interval) = Session.Resolve(Run);
                var row = Session.Capture.Rows[_previewPipe.Index];
                if (row.Length2D < 1e-6) return new List<double>();
                return SectionStationPlanner.Plan(row.Length2D, Session.Capture.PerPipeZones[_previewPipe.Index],
                    new SectionStationPlanner.Options
                    {
                        Interval = interval, IncludeZoneBoundaries = Run.IncludeZoneBoundaries, OnlyClashZones = Run.OnlyClashZones,
                        Manual = Run.ParseManual(), MinGap = Run.MinGap
                    });
            }
        }

        private int _stIdx;
        public int PreviewStationIndex
        {
            get => _stIdx;
            set { if (Set(ref _stIdx, Math.Max(0, value))) { Raise(nameof(PreviewStationText)); RequestPreview?.Invoke(); } }
        }
        public string PreviewStationText
        {
            get
            {
                var st = PreviewStations;
                if (st.Count == 0) return "—";
                int i = Math.Min(_stIdx, st.Count - 1);
                return $"{i + 1} / {st.Count}   ({st[i].ToString("0.00", CultureInfo.InvariantCulture)} m)";
            }
        }

        public void StepStation(int delta)
        {
            var n = PreviewStations.Count;
            if (n == 0) return;
            PreviewStationIndex = Math.Max(0, Math.Min(n - 1, _stIdx + delta));
        }

        /// <summary>
        /// Builds the preview with the current style + run options: one section, or in comparison
        /// mode the whole row of compared scenarios (null when there is nothing to show).
        /// </summary>
        public SectionSheet BuildPreview(out string note)
        {
            note = "";
            var st = PreviewStations;
            if (_previewPipe == null || st.Count == 0) { note = "Önizlenecek kesit yok."; return null; }
            int i = Math.Min(_stIdx, st.Count - 1);
            var cuts = new List<SectionCut>();
            int g = 0;
            Session.AddCuts(_previewPipe.Index, new[] { st[i] }, Run, cuts, ref g);
            if (cuts.Count == 0) { note = "Bu boru için kesit üretilemedi."; return null; }
            if (cuts.Count == 1)
            {
                var sheet = new SectionSheet();
                var d = SectionLayoutEngine.BuildSlot(cuts[0], Style, 0);
                sheet.Sections.Add(d);
                sheet.MinX = d.MinX; sheet.MinY = d.MinY; sheet.MaxX = d.MaxX; sheet.MaxY = d.MaxY;
                return sheet;
            }
            var s = Style.Clone();
            s.ShowRowTitle = false;
            return SectionLayoutEngine.Layout(cuts, s);
        }

        // ── Warnings ─────────────────────────────────────────────────────────
        public string Warnings
        {
            get
            {
                var w = new List<string>();
                if (Session.StalePipes.Count > 0)
                {
                    var x = Session.StalePipes.OrderByDescending(p => p.Diff).First();
                    w.Add($"⚠ {Session.StalePipes.Count} boruda yeniden hesaplanan hacim kayıtlı hacimden farklı (en büyük: " +
                          $"{SectionLayoutEngine.Sanitize(x.Pipe)} {x.Field} Δ={x.Diff.ToString("0.###", CultureInfo.InvariantCulture)} m³) — " +
                          "çizim son hesaptan sonra değişmiş olabilir; UT_BOQ_HESAPLA önerilir.");
                }
                if (Session.NeedsUncomputedWarning(Run))
                    w.Add("⚠ Hesaplanmamış ağlar var (" + string.Join(", ", Session.UncomputedNetworks) +
                          "). Kesitler bu ağlarla kesişimleri içermiyor olabilir — daha doğru sonuç için tüm ağları hesaplayın.");
                int zero = Pipes.Count(p => p.Count == 0);
                if (zero > 0) w.Add($"ℹ {zero} sıfır uzunluklu boru (düşü) kesitsiz geçilir.");
                return string.Join("\n", w);
            }
        }
        public void RaiseWarnings() => Raise(nameof(Warnings));

        public string HeaderText =>
            $"Hesap: {Session.GeneratedAt:dd.MM.yyyy HH:mm} · {Session.ComputedNetworks.Count} ağ · {Pipes.Count} boru";
    }
}
