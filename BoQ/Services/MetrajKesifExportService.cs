using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.DolguCatalog.Services;
using UrbanoMetraj.BoQ.ProjectRules.Services;
using UrbanoMetraj.BoQ.SoilCatalog.Services;
using UrbanoMetraj.BoQ.SmartAssembly.Services;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Generates the "Metraj Keşif Tablosu" workbook — a paged, deeply-grouped
    /// bill-of-quantities modeled on the client "birim fiyat / imalat analizi" reference.
    ///
    /// Sheet 1 = project info (manual). Then one sheet per active network, laid out as a
    /// collapsible Excel outline with Turkish titles and a "merge-by-material" backfill
    /// breakdown:
    ///
    ///   {AĞ} İŞLERİ
    ///     KAZI-DOLGU İŞLERİ
    ///       KAZI İŞLERİ      → per soil (poz from Zemin Kataloğu): total → BACA / BORU
    ///       DOLGU İŞLERİ     → per MATERIAL (poz from Dolgu Kataloğu). Layers sharing a
    ///                          material merge into one group titled by the layer names
    ///                          (e.g. "GÖMLEKLEME / YATAKLAMA" for "kum"). Each group:
    ///                          TOTAL first, then breakdown BACA / BORU → (within BORU)
    ///                          YATAKLAMA / GÖMLEKLEME → (within GÖMLEKLEME) BORU ETRAFI /
    ///                          BORU ÜSTÜ, with a total-first row at every multi-child level.
    ///     MUAYENE BACALARI İŞLERİ → per manhole diameter → precast pieces
    ///     BORU İŞLERİ            → per pipe type → pipe lengths by diameter
    ///
    /// 17 columns: 1 SIRA NO (hierarchical code AS.01.01…) | 2 POZ NO (catalog code) |
    /// 3 MALZEME KODU (manual) | 4 POZ KODU (network prefix, e.g. AS) | 5 İMALAT AÇIKLAMASI |
    /// 6 BİRİM | 7 MİKTARI | 8-11 *BF (manual) | 12 BİRİM FİYATI(=ΣBF) |
    /// 13-16 *TUTARI | 17 TOPLAM TUTAR.
    ///
    /// Row kinds: Band (colored category header — title, money = Σ children), Label
    /// (italic breakdown-dimension header BACA/BORU/… — transparent to numbering), Content
    /// (poz + açıklama + miktar; money = Σ children when it aggregates, else MİKTAR×BF).
    /// Detail leaves are the only rows the user types *BF into; every aggregate money cell
    /// is a SUM of its DIRECT children, so nothing is double-counted. Same poz/açıklama is
    /// repeated on every row of a material group (user request).
    ///
    /// NO engineering calculation — only aggregation of already-computed BoQ values plus
    /// price×quantity formulas. Reuses ExcelExportService's EPPlus 4.5.3.3 primitives.
    /// </summary>
    public static class MetrajKesifExportService
    {
        private const int ColCount = 17;
        private static readonly int[] MoneyCols = { 13, 14, 15, 16, 17 };

        // Per-row formula column letters.
        private const string ColMiktar = "G", ColMalzBf = "H", ColIsciBf = "I",
                             ColGgBf = "J", ColKarBf = "K",
                             ColMalzT = "M", ColIsciT = "N", ColGgT = "O", ColKarT = "P";

        private static readonly Color BandDark  = Color.FromArgb(0,  70, 127);
        private static readonly Color BandMid   = Color.FromArgb(0,  90, 160);
        private static readonly Color BandBlue  = Color.FromArgb(68, 114, 196);
        private static readonly Color LabelFill = Color.FromArgb(219, 229, 244);
        private static readonly Color LabelText = Color.FromArgb(0, 50, 100);
        private static readonly Color White     = Color.White;

        private static readonly Dictionary<ExportLanguage, string[]> HeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "Item No", "Poz No", "Material Code", "Poz Code", "Work Description", "Unit", "Quantity",
                    "Material UP", "Labour UP", "Overhead UP", "Profit UP", "Unit Price",
                    "Material Amount", "Labour Amount", "Overhead Amount", "Profit Amount", "Total Amount"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "SIRA NO", "POZ NO", "MALZEME KODU", "POZ KODU", "İMALAT AÇIKLAMASI", "BİRİM", "MİKTARI",
                    "MALZEME BF", "İŞÇİLİK BF", "GENEL GİDER BF", "KAR BF", "BİRİM FİYATI",
                    "MALZEME TUTARI", "İŞÇİLİK TUTARI", "GENEL GİDER TUTARI", "KAR TUTARI", "TOPLAM TUTAR"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "№", "Поз №", "Код материала", "Код позиции", "Описание работ", "Ед.", "Кол-во",
                    "Материал ЕЦ", "Труд ЕЦ", "Накл. ЕЦ", "Прибыль ЕЦ", "Цена",
                    "Сумма материала", "Сумма труда", "Сумма накл.", "Сумма прибыли", "Итого"
                }
            };

        private static readonly Dictionary<ExportLanguage, string> AdetUnitMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Pcs",
                [ExportLanguage.Turkish] = "Adet",
                [ExportLanguage.Russian] = "шт"
            };

        // Breakdown-dimension labels (Turkish, always).
        private const string S_Baca = "BACA", S_Boru = "BORU";
        private const string L_GeriDolgu = "GERİ DOLGU", L_Gomlek = "GÖMLEKLEME", L_Yataklama = "YATAKLAMA";
        private const string P_Etrafi = "BORU ETRAFI", P_Ustu = "BORU ÜSTÜ";

        private const string FmtVolume = "#,##0.000";
        private const string FmtLength = "#,##0.00";
        private const string FmtCount  = "#,##0";
        private const string FmtPrice  = "#,##0.00";

        // Ordering rank for breakdown labels (source, layer, position share one map).
        private static readonly Dictionary<string, int> LabelRank = new Dictionary<string, int>
        {
            [S_Baca] = 0, [S_Boru] = 1,
            [L_Yataklama] = 0, [L_Gomlek] = 1, [L_GeriDolgu] = 2,
            [P_Etrafi] = 0, [P_Ustu] = 1
        };

        // =====================================================================
        // Row tree
        // =====================================================================

        private enum Kind { Band, Label, Content }

        private sealed class Row
        {
            public Kind   Kind;
            public string Title;      // Band/Label caption
            public string Poz;        // Content
            public string Ack;        // Content description
            public bool   HasMiktar;
            public double Miktar;
            public string Birim;
            public string MiktarFmt;
            public readonly List<Row> Children = new List<Row>();

            public int    ExcelRow;
            public int    Depth;
            public string SiraNo = "";
            public string PozKodu = "";

            public bool HasChildren => Children.Count > 0;
            public bool HasContent  => Children.Count > 0;

            public static Row Band(string title)  => new Row { Kind = Kind.Band,  Title = title };
            public static Row Label(string title) => new Row { Kind = Kind.Label, Title = title };
            public static Row Content(string poz, string ack, double miktar, string birim, string fmt)
                => new Row { Kind = Kind.Content, Poz = poz ?? "", Ack = ack ?? "",
                             HasMiktar = true, Miktar = miktar, Birim = birim, MiktarFmt = fmt };

            public Row Add(Row c) { if (c != null) Children.Add(c); return this; }
        }

        // One atomic volume contribution inside a material group.
        private sealed class Contrib
        {
            public string   Material;
            public string[] Path;     // e.g. { BORU, GÖMLEKLEME, BORU ETRAFI }
            public double   Vol;
        }

        // LIVE catalog Açıklama lookups, built once per export. The stored
        // StackedPart.Aciklama / SectionDebugRow.Aciklama only carry what the catalog held
        // at the last HESAPLA (stale if the description was edited afterward). Like the
        // KAZI/DOLGU rows (resolved live from Soil/Dolgu stores), pieces & pipes resolve
        // their İMALAT AÇIKLAMASI live from the Baca Parça / Boru catalogs here, falling
        // back to the stored value, then a composed name.
        private sealed class CatalogAck
        {
            private readonly Dictionary<string, string> _piece   = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<string, string> _pipeDnMat = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            private readonly Dictionary<int, string>    _pipeDn    = new Dictionary<int, string>();

            /// <summary>Live piece Açıklama by component Name (= StackedPart.PartName).</summary>
            public string ForPiece(string partName)
                => partName != null && _piece.TryGetValue(partName, out var a) ? a : null;

            /// <summary>Live pipe Açıklama by diameter (+ material when it disambiguates).</summary>
            public string ForPipe(int dn, string material)
            {
                if (!string.IsNullOrEmpty(material) && _pipeDnMat.TryGetValue(dn + "|" + material, out var a))
                    return a;
                return _pipeDn.TryGetValue(dn, out var b) ? b : null;
            }

            public static CatalogAck Build()
            {
                var r = new CatalogAck();
                try
                {
                    var mc = SmartAssemblyCatalogStore.Current;
                    if (mc?.Components != null)
                        foreach (var c in mc.Components)
                            if (!string.IsNullOrWhiteSpace(c.Name) && !string.IsNullOrWhiteSpace(c.Aciklama)
                                && !r._piece.ContainsKey(c.Name))
                                r._piece[c.Name] = c.Aciklama;
                }
                catch { }
                try
                {
                    var pc = PipeCatalogStore.Current;
                    if (pc?.Families != null)
                        foreach (var fam in pc.Families)
                            foreach (var p in fam.Pipes ?? Enumerable.Empty<PipeDefinition>())
                            {
                                if (string.IsNullOrWhiteSpace(p.Aciklama)) continue;
                                int dn = (int)Math.Round(p.NominalDiameter);
                                string k = dn + "|" + (fam.Material ?? "");
                                if (!r._pipeDnMat.ContainsKey(k)) r._pipeDnMat[k] = p.Aciklama;
                                if (!r._pipeDn.ContainsKey(dn))    r._pipeDn[dn]   = p.Aciklama;
                            }
                }
                catch { }
                return r;
            }
        }

        // =====================================================================
        // Entry point
        // =====================================================================

        public static void Export(BoQReport report, BoQSettings settings, string path)
        {
            // The Metraj Keşif Tablosu is an inherently Turkish form (section titles are
            // always Turkish, catalog descriptions are Turkish) — so the column headers,
            // units and sheet names are forced to Turkish regardless of settings.Language.
            ExportLanguage lang = ExportLanguage.Turkish;
            var ack = CatalogAck.Build();
            using (var pkg = new ExcelPackage())
            {
                WriteProjectInfoSheet(pkg, lang);
                foreach (var sys in report.Systems ?? new List<SystemBoQ>())
                {
                    Row root = BuildNetwork(report, sys, settings, ack);
                    if (root.HasContent) WriteNetworkSheet(pkg, sys, root, lang);
                }
                ExcelExportService.SavePackage(pkg, path);
            }
        }

        // =====================================================================
        // Tree builder
        // =====================================================================

        private static Row BuildNetwork(BoQReport report, SystemBoQ sys, BoQSettings settings, CatalogAck ack)
        {
            var sections = (report.SectionDebug ?? new List<SectionDebugRow>())
                .Where(r => string.Equals(r.SystemName, sys.SystemName, StringComparison.Ordinal))
                .ToList();

            var root = Row.Band(sys.SystemName + " İŞLERİ");

            // ── KAZI-DOLGU İŞLERİ ─────────────────────────────────────────────
            var kaziDolgu = Row.Band("KAZI-DOLGU İŞLERİ");
            var kazi  = Row.Band("KAZI İŞLERİ");   BuildKazi(kazi, sys);
            if (kazi.HasContent) kaziDolgu.Add(kazi);
            var dolgu = Row.Band("DOLGU İŞLERİ");  BuildDolgu(dolgu, sys, sections);
            if (dolgu.HasContent) kaziDolgu.Add(dolgu);
            if (kaziDolgu.HasContent) root.Add(kaziDolgu);

            // ── MUAYENE BACALARI İŞLERİ (pieces, grouped by manhole diameter) ─
            var bacalar = Row.Band("MUAYENE BACALARI İŞLERİ");
            BuildManholes(bacalar, sys, settings.MetrajDegiskenParcaBandM, ack);
            if (bacalar.HasContent) root.Add(bacalar);

            // ── BORU İŞLERİ (lengths, grouped by pipe type) ───────────────────
            var borular = Row.Band("BORU İŞLERİ");
            BuildPipes(borular, sections, ack);
            if (borular.HasContent) root.Add(borular);

            return root;
        }

        // ── KAZI: one material group per soil (single soil now) ───────────────
        private static void BuildKazi(Row kazi, SystemBoQ sys)
        {
            var (poz, desc) = SoilPozDesc(sys.SystemName);
            double mh   = sys.Manholes.Sum(m => m.ExcavationVolume);
            double pipe = sys.Pipes.Sum(p => p.TotalExcavationVolume);

            var contribs = new List<Contrib>();
            if (mh   > 1e-9) contribs.Add(new Contrib { Material = poz, Path = new[] { S_Baca }, Vol = mh });
            if (pipe > 1e-9) contribs.Add(new Contrib { Material = poz, Path = new[] { S_Boru }, Vol = pipe });
            if (contribs.Count == 0) return;

            string title = !string.IsNullOrWhiteSpace(desc) ? desc : "KAZI";
            var band = Row.Band(title);
            band.Add(BuildAgg(contribs, 0, poz, desc));
            kazi.Add(band);
        }

        // ── DOLGU: group all backfill/bedding contributions by material ───────
        private static void BuildDolgu(Row dolgu, SystemBoQ sys, List<SectionDebugRow> sections)
        {
            var all = new List<Contrib>();
            void AddMap(Dictionary<string, double> map, string[] pathTail, string source)
            {
                foreach (var kv in map)
                    if (kv.Value > 1e-9)
                        all.Add(new Contrib { Material = kv.Key,
                                              Path = new[] { source }.Concat(pathTail).ToArray(),
                                              Vol = kv.Value });
            }

            // Geri Dolgu
            AddMap(ManholeGeriDolguByMaterial(sys), new[] { L_GeriDolgu }, S_Baca);
            var boruGd = PipeSplitsByMaterial(sections, s => s.BackfillLayerSplits);
            if (boruGd.Count == 0) AddIfPos(boruGd, "", sys.Pipes.Sum(p => p.TotalBackfillVolume));
            AddMap(boruGd, new[] { L_GeriDolgu }, S_Boru);

            // Yataklama
            AddMap(ManholeYataklamaByMaterial(sys), new[] { L_Yataklama }, S_Baca);
            var boruYa = PipeSplitsByMaterial(sections, s => s.BeddingLayerSplits);
            if (boruYa.Count == 0) AddIfPos(boruYa, "", sys.Pipes.Sum(p => p.TotalBeddingVolume));
            AddMap(boruYa, new[] { L_Yataklama }, S_Boru);

            // Gömlekleme (boru etrafı + boru üstü). Fallback: whole surround under Etrafı.
            var boruGoE = PipeSplitsByMaterial(sections, s => s.BoruEtrafiLayerSplits);
            var boruGoU = PipeSplitsByMaterial(sections, s => s.BoruUstuLayerSplits);
            if (boruGoE.Count == 0 && boruGoU.Count == 0)
                AddIfPos(boruGoE, "", sys.Pipes.Sum(p => p.TotalSurroundVolume));
            AddMap(boruGoE, new[] { L_Gomlek, P_Etrafi }, S_Boru);
            AddMap(boruGoU, new[] { L_Gomlek, P_Ustu },  S_Boru);

            if (all.Count == 0) return;

            // One group per material.
            foreach (var grp in all.GroupBy(c => c.Material ?? "")
                                    .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
            {
                var contribs = grp.ToList();
                var (poz, desc) = DolguPozDesc(grp.Key);

                // Title = the distinct layer names present (path[1]) joined in rank order.
                var layers = contribs.Select(c => c.Path.Length > 1 ? c.Path[1] : "")
                                     .Where(s => !string.IsNullOrEmpty(s))
                                     .Distinct()
                                     .OrderBy(Rank).ToList();
                string title = layers.Count > 0 ? string.Join(" / ", layers)
                             : (!string.IsNullOrEmpty(desc) ? desc : "DOLGU");

                var band = Row.Band(title);
                band.Add(BuildAgg(contribs, 0, poz, desc));
                dolgu.Add(band);
            }
        }

        // Recursively builds the total-first breakdown for a set of contributions.
        // Returns a single Content leaf (one contribution) or a Content total-row that
        // parents the split into Label bands.
        private static Row BuildAgg(List<Contrib> contribs, int depth, string poz, string desc)
        {
            if (contribs.Count == 1)
                return Row.Content(poz, DetailAck(desc, contribs[0]), contribs[0].Vol, "m³", FmtVolume);

            var total = Row.Content(poz, string.IsNullOrEmpty(desc) ? "Toplam" : desc,
                                    contribs.Sum(c => c.Vol), "m³", FmtVolume);

            // Advance to the next path index where the contributions actually differ.
            int d = depth;
            while (contribs.Select(c => LabelAt(c, d)).Distinct().Count() <= 1
                   && contribs.Any(c => c.Path.Length > d + 1))
                d++;

            foreach (var g in contribs.GroupBy(c => LabelAt(c, d)).OrderBy(g => Rank(g.Key)))
            {
                var label = Row.Label(g.Key);
                label.Add(BuildAgg(g.ToList(), d + 1, poz, desc));
                total.Add(label);
            }
            return total;
        }

        private static string DetailAck(string desc, Contrib c) => desc ?? "";
        private static string LabelAt(Contrib c, int d) => d < c.Path.Length ? c.Path[d] : "";
        private static int Rank(string label) => LabelRank.TryGetValue(label, out int r) ? r : 99;

        // ── MUAYENE: pieces aggregated per manhole nominal diameter ───────────
        // Fixed pieces (Taban/Konik/Kapak…) get one row per exact height. VARIABLE
        // rings (değişken — Gövde/Boyun) are summed into height BANDS of width bandM
        // (a DWG-saved setting, default 0.5 m): every ring whose height falls in the
        // same [n·band, (n+1)·band) interval is one line, with that interval appended to
        // the catalog Açıklama (the piece's own description, per user request).
        private static void BuildManholes(Row parent, SystemBoQ sys, double bandM, CatalogAck ack)
        {
            var withStack = sys.Manholes.Where(m => m.StackPreCast != null
                                                 && m.StackPreCast.Parts != null
                                                 && m.StackPreCast.Parts.Count > 0).ToList();
            if (withStack.Count == 0) return;

            foreach (var diaGrp in withStack.GroupBy(m => m.Diameter).OrderBy(g => g.Key))
            {
                string title = diaGrp.Key > 0 ? $"Ø{diaGrp.Key} MM İÇ ÇAPLI BACALAR" : "MUAYENE BACALARI";
                var band = Row.Band(title);

                var agg = new List<(string key, string poz, string ack, int count)>();
                foreach (var m in diaGrp)
                    foreach (var p in m.StackPreCast.Parts)
                    {
                        bool isVar = p.IsVariableRing && bandM > 1e-6;
                        // Live catalog Açıklama first, then the stored value, then a composed name.
                        string catAck = ack.ForPiece(p.PartName)
                                     ?? (!string.IsNullOrWhiteSpace(p.Aciklama) ? p.Aciklama : null);
                        string baseAck = !string.IsNullOrWhiteSpace(catAck)
                            ? catAck
                            : (isVar ? p.PartName : $"{p.PartName} {p.HeightM:0.00}m");

                        string desc, hkey;
                        if (isVar)
                        {
                            double lo = Math.Floor(p.HeightM / bandM) * bandM;
                            double hi = lo + bandM;
                            desc = $"{baseAck} ({lo:0.00}-{hi:0.00} m)";
                            hkey = "V" + lo.ToString("0.000");
                        }
                        else
                        {
                            desc = baseAck;
                            hkey = "F" + p.HeightM.ToString("0.000");
                        }

                        string key = p.PartName + "|" + (p.PozNo ?? "") + "|" + hkey;
                        int i = agg.FindIndex(x => x.key == key);
                        if (i < 0) agg.Add((key, p.PozNo ?? "", desc, p.Count));
                        else       agg[i] = (agg[i].key, agg[i].poz, agg[i].ack, agg[i].count + p.Count);
                    }

                foreach (var a in agg)
                    band.Add(Row.Content(a.poz, a.ack, a.count, "Adet", FmtCount));
                if (band.HasContent) parent.Add(band);
            }
        }

        // ── BORU: lengths grouped by pipe type, then by diameter ──────────────
        private static void BuildPipes(Row parent, List<SectionDebugRow> sections, CatalogAck ack)
        {
            if (sections.Count == 0) return;

            foreach (var typeGrp in sections
                        .GroupBy(s => (Sinif: s.Sinif ?? "", Mat: s.Material ?? ""))
                        .OrderBy(g => g.Key.Mat, StringComparer.OrdinalIgnoreCase))
            {
                string title = (typeGrp.Key.Sinif + " " + typeGrp.Key.Mat).Trim();
                if (string.IsNullOrEmpty(title)) title = "BORULAR";
                var band = Row.Band(title);

                foreach (var diaGrp in typeGrp.GroupBy(s => s.DiameterMm).OrderBy(g => g.Key))
                {
                    var withPoz = diaGrp.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.PozNo)) ?? diaGrp.First();
                    // Live catalog Açıklama first, then the stored value, then a composed name.
                    string catAck = ack.ForPipe(diaGrp.Key, withPoz.Material)
                                 ?? (!string.IsNullOrWhiteSpace(withPoz.Aciklama) ? withPoz.Aciklama : null);
                    string desc = !string.IsNullOrWhiteSpace(catAck)
                        ? catAck : $"Ø{diaGrp.Key} {withPoz.Material} boru";
                    band.Add(Row.Content(withPoz.PozNo ?? "", desc, diaGrp.Sum(r => r.Length2D), "m", FmtLength));
                }
                if (band.HasContent) parent.Add(band);
            }
        }

        // =====================================================================
        // Aggregation helpers
        // =====================================================================

        private static Dictionary<string, double> PipeSplitsByMaterial(
            List<SectionDebugRow> sections, Func<SectionDebugRow, IEnumerable<TrenchLayerSplit>> pick)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var s in sections)
                foreach (var l in pick(s) ?? Enumerable.Empty<TrenchLayerSplit>())
                {
                    if (l.Volume <= 1e-9) continue;
                    string mat = l.MaterialType ?? "";
                    d.TryGetValue(mat, out double v);
                    d[mat] = v + l.Volume;
                }
            return d;
        }

        private static Dictionary<string, double> ManholeGeriDolguByMaterial(SystemBoQ sys)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sys.Manholes)
            {
                var splits = m.BackfillLayerSplits ?? new List<TrenchLayerSplit>();
                double sum = splits.Sum(l => l.Volume);
                if (sum > 1e-9)
                    foreach (var l in splits) { if (l.Volume <= 1e-9) continue;
                        string mat = l.MaterialType ?? ""; d.TryGetValue(mat, out double v); d[mat] = v + l.Volume; }
                else if (m.BackfillVolume > 1e-9)
                    { d.TryGetValue("", out double v); d[""] = v + m.BackfillVolume; }
            }
            return d;
        }

        private static Dictionary<string, double> ManholeYataklamaByMaterial(SystemBoQ sys)
        {
            var d = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sys.Manholes)
            {
                if (m.SubBaseVolume <= 1e-9) continue;
                string mat = m.ResolvedSubBaseLayers?.FirstOrDefault()?.MaterialType ?? "";
                d.TryGetValue(mat, out double v);
                d[mat] = v + m.SubBaseVolume;
            }
            return d;
        }

        private static void AddIfPos(Dictionary<string, double> d, string key, double v)
        {
            if (v > 1e-9) { d.TryGetValue(key, out double e); d[key] = e + v; }
        }

        // =====================================================================
        // Layout (two-phase) + writing
        // =====================================================================

        private sealed class Counter { public int Value; }

        private static void WriteNetworkSheet(ExcelPackage pkg, SystemBoQ sys, Row root, ExportLanguage lang)
        {
            var ws = pkg.Workbook.Worksheets.Add(ExcelExportService.Truncate(
                ExcelExportService.SanitizeSheetName(sys.SystemName), 31));

            ExcelExportService.WriteTitle(ws, sys.SystemName + " — Metraj Keşif Tablosu", ColCount, 1);
            const int hdrRow = 3;
            ExcelExportService.WriteHeaders(ws, hdrRow, HeaderMap[lang], ColCount);
            ws.View.FreezePanes(hdrRow + 1, 1);
            ws.OutLineSummaryBelow = false;

            string prefix = NetworkPrefix(sys.SystemName);

            // Phase 1 — assign rows + hierarchical numbering. The network root is coded
            // as the bare prefix; Label rows are transparent to numbering (their coded
            // children continue the parent Content/Band's own numbering counter).
            int rowCounter = hdrRow + 1;
            root.Depth = 0; root.ExcelRow = rowCounter++; root.SiraNo = prefix; root.PozKodu = "";
            var c0 = new Counter();
            foreach (var child in root.Children)
                AssignLayout(child, 1, new List<int>(), c0, prefix, ref rowCounter);

            // Phase 2 — write cells.
            WriteNode(ws, root);

            SetColumnWidthsAndGroups(ws);
        }

        private static void AssignLayout(Row n, int depth, List<int> basePath, Counter counter,
                                         string prefix, ref int rowCounter)
        {
            n.Depth = depth;
            n.ExcelRow = rowCounter++;

            if (n.Kind == Kind.Label)
            {
                n.SiraNo = ""; n.PozKodu = "";
                foreach (var c in n.Children)
                    AssignLayout(c, depth + 1, basePath, counter, prefix, ref rowCounter);   // transparent
            }
            else
            {
                int idx = ++counter.Value;
                var myPath = new List<int>(basePath) { idx };
                n.SiraNo  = prefix + "." + string.Join(".", myPath.Select(x => x.ToString("00")));
                n.PozKodu = n.Kind == Kind.Content ? prefix : "";
                var myCounter = new Counter();
                foreach (var c in n.Children)
                    AssignLayout(c, depth + 1, myPath, myCounter, prefix, ref rowCounter);
            }
        }

        private static void WriteNode(ExcelWorksheet ws, Row n)
        {
            int r = n.ExcelRow;
            ws.Cells[r, 1].Value = n.SiraNo;
            ws.Cells[r, 4].Value = n.PozKodu;
            ws.Cells[r, 5].Value = n.Kind == Kind.Content ? Norm(n.Ack) : n.Title;
            ws.Cells[r, 5].Style.Indent = Math.Min(n.Depth, 8);
            // Multi-line catalog descriptions keep their line breaks and wrap in the cell
            // (Excel auto-fits the row height on open — no manual height set on content rows).
            if (n.Kind == Kind.Content)
            {
                ws.Cells[r, 5].Style.WrapText = true;
                ws.Cells[r, 5].Style.VerticalAlignment = ExcelVerticalAlignment.Top;
            }

            if (n.Kind == Kind.Content)
            {
                ws.Cells[r, 2].Value = n.Poz;
                ws.Cells[r, 6].Value = n.Birim;
                if (n.HasMiktar) ws.Cells[r, 7].Value = n.Miktar;

                if (n.HasChildren)
                {
                    foreach (int col in MoneyCols) ws.Cells[r, col].Formula = SumChildren(n, col);
                }
                else
                {
                    ws.Cells[r, 12].Formula = $"{ColMalzBf}{r}+{ColIsciBf}{r}+{ColGgBf}{r}+{ColKarBf}{r}";
                    ws.Cells[r, 13].Formula = $"{ColMiktar}{r}*{ColMalzBf}{r}";
                    ws.Cells[r, 14].Formula = $"{ColMiktar}{r}*{ColIsciBf}{r}";
                    ws.Cells[r, 15].Formula = $"{ColMiktar}{r}*{ColGgBf}{r}";
                    ws.Cells[r, 16].Formula = $"{ColMiktar}{r}*{ColKarBf}{r}";
                    ws.Cells[r, 17].Formula = $"{ColMalzT}{r}+{ColIsciT}{r}+{ColGgT}{r}+{ColKarT}{r}";
                }

                ExcelExportService.ApplyDataRowStyle(ws, r, ColCount, false);
                ws.Cells[r, 6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                if (n.HasMiktar) ExcelExportService.SetNumericFormat(ws, r, 7, 7, n.MiktarFmt);
                ExcelExportService.SetNumericFormat(ws, r, 8, ColCount, FmtPrice);
                // Aggregate content rows read as bold totals.
                if (n.HasChildren) ws.Cells[r, 5].Style.Font.Bold = true;
            }
            else
            {
                foreach (int col in MoneyCols)
                {
                    ws.Cells[r, col].Formula = SumChildren(n, col);
                    ws.Cells[r, col].Style.Numberformat.Format = FmtPrice;
                }
                StyleBand(ws, r, n);
            }

            ws.Row(r).OutlineLevel = OutlineFor(n.Depth);
            foreach (var c in n.Children) WriteNode(ws, c);
        }

        /// <summary>
        /// Collapses the logical tree depth onto the few meaningful outline "stops"
        /// (user request — the intermediate band-only / BACA-BORU-only / source-only views
        /// were noise). Kept collapse states, in order:
        ///   0 network · 1 sections · 2 subsections+categories ·
        ///   3 material bands + their poz totals (one line per material — the summary view) ·
        ///   4 the full detail breakdown.
        /// So logical depths 3&amp;4 share level 3 (band shows with its total, never alone) and
        /// everything below the material total (BACA/BORU/layers/positions/details) shares
        /// level 4 (expands in one step). Numbering/indent still use the full depth.
        /// </summary>
        private static int OutlineFor(int depth)
        {
            if (depth <= 2) return depth;
            if (depth <= 4) return 3;
            return 4;
        }

        private static string Norm(string s) => s?.Replace("\r\n", "\n").Replace("\r", "\n");

        private static string SumChildren(Row n, int col)
        {
            if (!n.HasChildren) return null;
            return string.Join("+", n.Children.Select(c => Col(col) + c.ExcelRow));
        }

        private static void StyleBand(ExcelWorksheet ws, int r, Row n)
        {
            bool isLabel = n.Kind == Kind.Label;
            Color fill = isLabel ? LabelFill
                       : n.Depth <= 1 ? BandDark
                       : n.Depth == 2 ? BandMid : BandBlue;
            Color text = isLabel ? LabelText : White;
            for (int c = 1; c <= ColCount; c++)
            {
                var cell = ws.Cells[r, c];
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(fill);
                cell.Style.Font.Bold = !isLabel;
                cell.Style.Font.Italic = isLabel;
                cell.Style.Font.Color.SetColor(text);
                cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            }
            ws.Row(r).Height = 17;
        }

        private static void SetColumnWidthsAndGroups(ExcelWorksheet ws)
        {
            double[] w = { 20, 14, 13, 11, 52, 7, 12, 11, 11, 12, 11, 13, 14, 14, 15, 13, 15 };
            for (int c = 1; c <= ColCount; c++) ws.Column(c).Width = w[c - 1];

            ws.OutLineSummaryRight = true;
            for (int c = 8;  c <= 11; c++) ws.Column(c).OutlineLevel = 1;   // *BF → BİRİM FİYATI
            for (int c = 13; c <= 16; c++) ws.Column(c).OutlineLevel = 1;   // *TUTARI → TOPLAM TUTAR
        }

        // =====================================================================
        // Project-info sheet
        // =====================================================================

        private static void WriteProjectInfoSheet(ExcelPackage pkg, ExportLanguage lang)
        {
            var ws = pkg.Workbook.Worksheets.Add(SheetName(lang, "Proje Bilgileri", "Project Info", "Проект"));

            string title = lang == ExportLanguage.English ? "Bill of Quantities — Project Information"
                         : lang == ExportLanguage.Russian ? "Ведомость объёмов — Сведения о проекте"
                         : "Metraj Keşif Tablosu — Proje Bilgileri";
            ExcelExportService.WriteTitle(ws, title, 2, 1);

            string[] fields = lang == ExportLanguage.English
                ? new[] { "Project Name", "Location", "Employer", "Contractor", "Work Definition",
                          "Section", "Date", "Year", "Revision", "Prepared By" }
                : lang == ExportLanguage.Russian
                ? new[] { "Наименование", "Местоположение", "Заказчик", "Подрядчик", "Описание работ",
                          "Раздел", "Дата", "Год", "Ревизия", "Подготовил" }
                : new[] { "Proje Adı", "Proje Yeri", "İşveren", "Yüklenici", "İşin Tanımı",
                          "Bölüm", "Tarih", "Yıl", "Revizyon", "Hazırlayan" };

            int row = 4;
            foreach (var f in fields)
            {
                var lbl = ws.Cells[row, 1];
                lbl.Value = f + " :";
                lbl.Style.Font.Bold = true;
                lbl.Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                lbl.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;
                ws.Cells[row, 2].Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                ws.Cells[row, 2].Style.Border.Bottom.Color.SetColor(Color.FromArgb(180, 198, 231));
                ws.Row(row).Height = 20;
                row++;
            }
            ws.Column(1).Width = 22;
            ws.Column(2).Width = 48;
        }

        // =====================================================================
        // Catalog lookups + utilities
        // =====================================================================

        private static (string poz, string desc) SoilPozDesc(string sysName = null)
        {
            SoilCatalog.Models.SoilClassification soil = null;

            // RULES mode: price this network's excavation with ITS chosen Zemin Tipi (not the first soil).
            if (ProjectRulesStore.IsRulesMode && !string.IsNullOrEmpty(sysName))
            {
                var net = ProjectRulesStore.FindNetwork(sysName);
                if (net != null && !string.IsNullOrWhiteSpace(net.SoilName))
                    soil = SoilCatalogStore.Items?.FirstOrDefault(
                        s => string.Equals(s.SoilName, net.SoilName, System.StringComparison.OrdinalIgnoreCase));
            }

            if (soil == null) soil = SoilCatalogStore.Items?.FirstOrDefault();
            if (soil == null) return ("", "");
            string desc = !string.IsNullOrWhiteSpace(soil.Aciklama) ? soil.Aciklama
                        : !string.IsNullOrWhiteSpace(soil.KaziTipi) ? soil.KaziTipi
                        : soil.SoilName;
            return (soil.BoqItemCode ?? "", desc ?? "");
        }

        private static (string poz, string desc) DolguPozDesc(string material)
        {
            if (string.IsNullOrWhiteSpace(material)) return ("", "");
            var d = DolguCatalogStore.Items?.FirstOrDefault(x =>
                string.Equals(x.DolguAdi, material, StringComparison.OrdinalIgnoreCase));
            if (d == null) return ("", material);
            string desc = !string.IsNullOrWhiteSpace(d.Aciklama) ? d.Aciklama : d.DolguAdi;
            return (d.BoqItemCode ?? "", desc ?? material);
        }

        private static string NetworkPrefix(string name)
        {
            string letters = new string((name ?? "").Where(char.IsLetter).ToArray()).ToUpperInvariant();
            return letters.Length == 0 ? "N" : letters.Substring(0, Math.Min(2, letters.Length));
        }

        private static string SheetName(ExportLanguage lang, string tr, string en, string ru)
            => lang == ExportLanguage.English ? en : lang == ExportLanguage.Russian ? ru : tr;

        private static string Col(int index) => ((char)('A' + index - 1)).ToString();
    }
}
