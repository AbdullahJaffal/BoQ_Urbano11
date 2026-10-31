using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.ProjectRules.Services;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI.NetworkPanel;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// Everything UT_CROSS_SECTIONS needs from the last UT_BOQ_HESAPLA run, prepared once:
    /// the saved data is loaded, the V2 engine is re-run on it in the original row order
    /// (display capture only), and the recomputed volumes are compared with the saved ones —
    /// a difference means the drawing changed since the calc (stale data).
    /// </summary>
    public sealed class CrossSectionSession
    {
        public BoQReport   Report   { get; private set; }
        public BoQSettings Settings { get; private set; }
        internal BoQSimplifiedTestV2Command.V2SectionCapture Capture { get; private set; }
        public Dictionary<int, KolAssignment> Kol { get; private set; } = new Dictionary<int, KolAssignment>();
        public List<string> ComputedNetworks   { get; private set; } = new List<string>();
        public List<string> UncomputedNetworks { get; private set; } = new List<string>();
        /// <summary>Pipes whose re-run volumes differ from the saved ones (largest |Δ| m³ and which volume).</summary>
        public List<(string Pipe, double Diff, string Field, double Saved, double Now)> StalePipes { get; private set; }
            = new List<(string, double, string, double, double)>();

        private static readonly string[] VolumeNames =
        {
            "Kazı KU", "Kazı KL", "Kazı %50", "Kazı brüt", "Geri Dolgu KU", "Geri Dolgu KL", "Geri Dolgu %50", "Geri Dolgu brüt",
            "Yataklama net", "Gömlekleme net", "Yataklama brüt", "Gömlekleme brüt"
        };

        private static double[] VolumeVector(SectionDebugRow r) => new[]
        {
            r.VExcavKU, r.VExcavKL, r.VExcavSP, r.VExcavGross,
            r.VBackfillKU, r.VBackfillKL, r.VBackfillSP, r.VBackfillGross,
            r.VBeddingNet, r.VSurroundNet, r.VBeddingGross, r.VSurroundGross
        };
        public DateTime GeneratedAt => Report?.GeneratedAt ?? DateTime.MinValue;

        public const double StaleTolAbs = 1e-3;   // m³
        public const double StaleTolRel = 1e-6;

        /// <summary>Loads + re-runs. Returns null with <paramref name="error"/> set when drawing is impossible.</summary>
        public static CrossSectionSession Prepare(Database db, out string error)
        {
            error = null;
            if (!DwgBoQStore.HasData(db))
            {
                error = "Bu çizimde metraj verisi yok. Önce UT_BOQ_HESAPLA ile hesaplayın.";
                return null;
            }
            BoQReport report; BoQSettings settings;
            try { (report, settings) = DwgBoQStore.Load(db); }
            catch (Exception ex) { error = "Metraj verisi okunamadı: " + ex.Message; return null; }
            if (report?.SectionDebug == null || report.SectionDebug.Count == 0)
            {
                error = "Kayıtlı boru kesiti bulunamadı. UT_BOQ_HESAPLA ile yeniden hesaplayın.";
                return null;
            }
            if (report.SectionDebug.Any(r => !r.HasSectionInputs || !r.HasV2Volumes))
            {
                error = "Bu çizimin metraj verisi kesit çizimi gelmeden önce kaydedilmiş (eksik girdi). " +
                        "Lütfen UT_BOQ_HESAPLA'yı bir kez yeniden çalıştırın.";
                return null;
            }

            var s = new CrossSectionSession { Report = report, Settings = settings ?? new BoQSettings() };

            // Same row order as the calc run (pair roles follow it).
            var rows = report.SectionDebug.OrderBy(r => r.CalcOrder).ToList();
            report.SectionDebug.Clear();
            report.SectionDebug.AddRange(rows);

            // The saved V2 volumes as stored. NOT VBedding/VSurround: Load ends with
            // BoQScenarioAggregator.Apply, which overwrites those with the scenario value
            // (gross under "Yok say") — the Net/Gross pairs are left as read.
            var saved = rows.Select(VolumeVector).ToList();

            try { s.Capture = BoQSimplifiedTestV2Command.RunV2EngineCapture(report); }
            catch (Exception ex) { error = "Kesit motoru çalıştırılamadı: " + ex.Message; return null; }
            if (s.Capture == null) { error = "Kesit motoru boş sonuç verdi."; return null; }

            for (int i = 0; i < rows.Count; i++)
            {
                var now = VolumeVector(rows[i]);
                double worst = 0; int worstK = -1;
                for (int k = 0; k < now.Length; k++)
                {
                    double diff = Math.Abs(now[k] - saved[i][k]);
                    if (diff > StaleTolAbs + StaleTolRel * Math.Abs(saved[i][k]) && diff > worst) { worst = diff; worstK = k; }
                }
                if (worstK >= 0) s.StalePipes.Add((rows[i].PipeName, worst, VolumeNames[worstK], saved[i][worstK], now[worstK]));
            }

            // Networks: computed (in the data) vs present in the drawing (Ağ Paneli list).
            s.ComputedNetworks = rows.Select(r => r.SystemName ?? "").Where(n => n.Length > 0)
                                     .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            try
            {
                var all = NetworkSessionManager.GetAllNetworks(db) ?? new List<string>();
                s.UncomputedNetworks = all.Where(n => !s.ComputedNetworks.Contains(n, StringComparer.OrdinalIgnoreCase))
                                          .OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
            }
            catch (Exception) { }

            // Kol chainage — BASINCLI networks stay local-only (decision 10).
            try { ProjectRulesStore.LoadFromDwg(db); } catch (Exception) { }
            s.Kol = KolChainageService.Build(rows, n => { try { return ProjectRulesStore.IsBasincli(n); } catch { return false; } });
            return s;
        }

        /// <summary>Decision 11: warn whenever a network is uncomputed and either scenario considers clashes.</summary>
        public bool NeedsUncomputedWarning(TiePreference kazi, TiePreference dolgu)
            => UncomputedNetworks.Count > 0 && !(kazi == TiePreference.Ignore && dolgu == TiePreference.Ignore);

        /// <summary>Decision 11 for a whole run — in comparison mode any compared scenario that considers clashes counts.</summary>
        public bool NeedsUncomputedWarning(SectionRunOptions o)
            => ScenarioSet(o).Any(sc => NeedsUncomputedWarning(sc.Kazi, sc.Dolgu));

        /// <summary>The (Kazı, Dolgu) pairs each station is drawn with: one normally, the compared set in comparison mode.</summary>
        public List<(TiePreference Kazi, TiePreference Dolgu)> ScenarioSet(SectionRunOptions o)
        {
            if (o != null && o.CompareScenarios && o.CompareSet != null && o.CompareSet.Count > 0)
                return o.CompareSet.Distinct().Select(p => (p, p)).ToList();
            var (k, d, _) = Resolve(o);
            return new List<(TiePreference, TiePreference)> { (k, d) };
        }

        public (TiePreference Kazi, TiePreference Dolgu, double Interval) Resolve(SectionRunOptions o)
        {
            if (o == null || o.UseProjectSettings)
                return (BoQScenarioAggregator.Map(Settings.ExcavationOverlap),
                        BoQScenarioAggregator.Map(Settings.BackfillOverlap),
                        SectionStationPlanner.SnapInterval(Settings.CrossSectionInterval));
            return (o.ExcavScenario, o.BackfillScenario, SectionStationPlanner.SnapInterval(o.Interval));
        }

        /// <summary>Pipe indices to draw, ordered along the kol lines (network → kol → downstream position).</summary>
        public List<int> SelectPipes(SectionRunOptions o)
        {
            var rows = Capture.Rows;
            var nets  = new HashSet<string>(o?.Networks ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            var pipes = new HashSet<string>(o?.Pipes ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
            return Enumerable.Range(0, rows.Count)
                .Where(i => nets.Count == 0 || nets.Contains(rows[i].SystemName ?? ""))
                .Where(i => pipes.Count == 0 || pipes.Contains(SectionRunOptions.PipeKey(rows[i].SystemName, rows[i].StartNodeName, rows[i].EndNodeName)))
                .OrderBy(i => rows[i].SystemName ?? "", StringComparer.OrdinalIgnoreCase)
                .ThenBy(i => Kol.TryGetValue(i, out var k) ? k.KolIndex : int.MaxValue)
                .ThenBy(i => Kol.TryGetValue(i, out var k) ? k.OffsetAtUpstream : 0)
                .ThenBy(i => rows[i].PipeName, StringComparer.Ordinal)
                .ToList();
        }

        public List<SectionCut> BuildCuts(SectionRunOptions o)
        {
            var (_, _, interval) = Resolve(o);
            var cuts = new List<SectionCut>();
            var plan = new SectionStationPlanner.Options
            {
                Interval = interval,
                IncludeZoneBoundaries = o?.IncludeZoneBoundaries ?? true,
                OnlyClashZones = o?.OnlyClashZones ?? false,
                Manual = o?.ParseManual() ?? new List<double>(),
                MinGap = o?.MinGap ?? 0.25
            };
            SkippedPipes.Clear();
            FailedCuts.Clear();
            int group = 0;
            foreach (int pi in SelectPipes(o))
            {
                var row = Capture.Rows[pi];
                if (row.Length2D < 1e-6) { SkippedPipes.Add(row.PipeName); continue; }   // zero-length (drop) pipe
                AddCuts(pi, SectionStationPlanner.Plan(row.Length2D, Capture.PerPipeZones[pi], plan), o, cuts, ref group);
            }
            return cuts;
        }

        /// <summary>Sections of one pipe at the given local chainages, once per scenario of the run.</summary>
        public void AddCuts(int pi, IEnumerable<double> dists, SectionRunOptions o, List<SectionCut> cuts, ref int group)
        {
            var row = Capture.Rows[pi];
            var set = ScenarioSet(o);
            bool compare = set.Count > 1 || (o?.CompareScenarios ?? false);
            Kol.TryGetValue(pi, out var kol);
            foreach (double d in dists)
            {
                bool any = false;
                foreach (var (kazi, dolgu) in set)
                {
                    var cut = BuildOne(pi, d, kazi, dolgu);
                    if (cut == null) continue;
                    if (kol != null)
                    {
                        cut.KolName = kol.KolName;
                        cut.GlobalChainage = KolChainageService.Global(kol, row.Length2D, cut.LocalChainage);
                    }
                    if (compare) cut.CompareGroup = group;
                    cuts.Add(cut);
                    any = true;
                }
                if (any) group++;
            }
        }

        /// <summary>Nearest drawable pipe to a plan point (−1 when none): its snapped local chainage and plan offset.</summary>
        public int NearestPipe(double x, double y, out double chainage, out double offset)
        {
            int best = -1; chainage = 0; offset = double.MaxValue;
            var rows = Capture.Rows;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.Length2D < 1e-6) continue;
                double ch = SectionStationPlanner.ChainageAt(r.StartX, r.StartY, r.EndX, r.EndY, x, y, out double off);
                if (off < offset) { offset = off; chainage = Math.Min(ch, r.Length2D); best = i; }
            }
            return best;
        }

        public const double SelectionNodeTol = 0.2;   // mirrors BoQParserService.ApplySelectionScope

        /// <summary>
        /// Pipes of a drawing selection, matched like the calc's CAD Seçimi: the pipe entity's
        /// endpoints at the pipe's node coordinates, or both of its manholes picked.
        /// </summary>
        public HashSet<int> MatchSelection(SelectionScope sc)
        {
            var res = new HashSet<int>();
            if (sc == null) return res;
            var rows = Capture.Rows;
            double t2 = SelectionNodeTol * SelectionNodeTol;
            bool Near(double ax, double ay, double bx, double by) => (ax - bx) * (ax - bx) + (ay - by) * (ay - by) <= t2;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (!string.IsNullOrEmpty(r.StartNodeGuid) && !string.IsNullOrEmpty(r.EndNodeGuid)
                    && sc.ManholeGuids.Contains(r.StartNodeGuid) && sc.ManholeGuids.Contains(r.EndNodeGuid)) { res.Add(i); continue; }
                foreach (var g in sc.PipeSegments)
                    if ((Near(g[0], g[1], r.StartX, r.StartY) && Near(g[2], g[3], r.EndX, r.EndY))
                     || (Near(g[0], g[1], r.EndX, r.EndY) && Near(g[2], g[3], r.StartX, r.StartY))) { res.Add(i); break; }
            }
            return res;
        }

        /// <summary>Pipes with no drawable section (zero length) in the last <see cref="BuildCuts"/>.</summary>
        public List<string> SkippedPipes { get; } = new List<string>();
        /// <summary>Sections that failed in the last <see cref="BuildCuts"/> — pipe, chainage, reason (incl. code location).</summary>
        public List<(string Pipe, double Dist, string Error)> FailedCuts { get; } = new List<(string, double, string)>();

        /// <summary>One section; a failure is recorded (not thrown) so the rest still draws.</summary>
        public SectionCut BuildOne(int pipe, double dist, TiePreference kazi, TiePreference dolgu)
        {
            try
            {
                var cut = BoQSimplifiedTestV2Command.BuildSectionCut(Capture, pipe, dist, kazi, dolgu);
                if (cut == null && !SkippedPipes.Contains(Capture.Rows[pipe].PipeName)) SkippedPipes.Add(Capture.Rows[pipe].PipeName);
                return cut;
            }
            catch (Exception ex)
            {
                string where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                FailedCuts.Add((Capture.Rows[pipe].PipeName, dist, ex.GetType().Name + ": " + ex.Message + " @ " + where));
                return null;
            }
        }
    }
}
