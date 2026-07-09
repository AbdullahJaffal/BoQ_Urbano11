using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Persists a <see cref="BoQReport"/> (+ the <see cref="BoQSettings"/>) inside the
    /// active DWG as a HIERARCHICAL Named-Object-Dictionary tree (strict data isolation,
    /// explicit topology, easy debugging / future graph traversal):
    ///
    /// <code>
    /// NOD["URBANO_BOQ"]                       (DBDictionary)
    ///   "META"                                (Xrecord  – timestamp + settings)
    ///   [NetworkName]                         (DBDictionary  e.g. "asu")
    ///     "NETWORK_META"                      (Xrecord  – original name + manholes)
    ///     [Pipe_ID]                           (DBDictionary  e.g. "P_n1_to_n2")
    ///       "METADATA"                        (Xrecord  – StartNode/EndNode + geometry)
    ///       [Station_Chainage]                (DBDictionary  e.g. "STA_0+000")
    ///         "STATION_INFO"                  (Xrecord  – chainage/world coords/gross polys)
    ///         "EXCAVATION_KAZI"               (DBDictionary)   ← natural-ground branch
    ///           "SCENARIO_UPPER|LOWER|50_50"  (Xrecord  – Area + Vertices)
    ///         "BACKFILL_LAYERS"               (DBDictionary)   ← final-grade branch
    ///           "YATAKLAMA"  → SCENARIO_*     (Xrecord  – Area + Vertices)
    ///           "GOMLEKLEME" → SCENARIO_*
    ///           "GERI_DOLGU" → SCENARIO_*
    ///         "PIPE_BODY"                     (Xrecord  – Area + Vertices)
    /// </code>
    ///
    /// Excavation (Kazı) follows the natural ground topography while backfill follows the
    /// final grade — so they are deliberately split into two independent branches, and
    /// every preference scenario (Upper / Lower / 50-50) has its own dedicated XRecord.
    /// </summary>
    public static class DwgBoQStore
    {
        private const string NOD_KEY = "URBANO_BOQ";

        // ── Reserved (non-dictionary) keys at each level ──────────────────────
        private const string K_META         = "META";
        private const string K_NETWORK_META = "NETWORK_META";
        private const string K_METADATA     = "METADATA";
        private const string K_STATION_INFO = "STATION_INFO";
        private const string K_EXCAVATION   = "EXCAVATION_KAZI";
        private const string K_BACKFILL     = "BACKFILL_LAYERS";
        private const string K_PIPE_BODY    = "PIPE_BODY";
        private const string K_YATAKLAMA    = "YATAKLAMA";   // Bedding
        private const string K_GOMLEKLEME   = "GOMLEKLEME";  // Surround
        private const string K_GERI_DOLGU   = "GERI_DOLGU";  // Backfill
        private const string K_SC_UPPER       = "SCENARIO_UPPER";
        private const string K_SC_LOWER       = "SCENARIO_LOWER";
        private const string K_SC_5050        = "SCENARIO_50_50";
        private const string K_MANHOLE_STACKS = "MANHOLE_STACKS";
        private const string K_V2_VOLUMES    = "V2_VOLUMES";
        private const string K_LAYER_SPLITS  = "LAYER_SPLITS";  // Phase 2b — PipeTrenchCatalog per-sub-layer ratios

        // Sub-container that holds all pipe-network data.
        // Other stores (ManholeCatalogStore, ManholeAssignStore) write under
        // KATALOGLAR, completely separate from pipe BoQ data.
        private const string K_NETWORKS = "AGLAR";

        // =====================================================================
        // Save
        // =====================================================================

        public static void Save(Database db, BoQReport report, BoQSettings settings)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                // Reuse or create the shared URBANO_BOQ root.
                // Erase BoQ-owned keys (META + AGLAR) plus any ORPHANED network
                // dictionaries left directly under the root by older builds that
                // stored networks before the AGLAR container existed (e.g. "YSU",
                // "ASU" sitting beside AGLAR). Those orphans hold stale geometry and
                // would shadow the fresh data for any reader that walks the root
                // directly. The KATALOGLAR / MANHOLES_CATALOG / MANHOLE_ASSIGNMENTS
                // branches owned by the manhole stores are explicitly preserved.
                DBDictionary root;
                if (nod.Contains(NOD_KEY))
                {
                    root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForWrite);

                    // Keys owned by OTHER stores — never touch these.
                    var preserve = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    {
                        "KATALOGLAR", "MANHOLES_CATALOG", "MANHOLE_ASSIGNMENTS", "TYPE_MAPPING"
                    };

                    // Collect every direct child that is NOT preserved: META + AGLAR
                    // (rewritten below) and any stale orphan network.
                    var toErase = new List<string>();
                    foreach (DBDictionaryEntry e in root)
                        if (!preserve.Contains(e.Key)) toErase.Add(e.Key);

                    foreach (string k in toErase)
                    {
                        if (!root.Contains(k)) continue;
                        var obj = tr.GetObject(root.GetAt(k), OpenMode.ForWrite);
                        if (obj is DBDictionary sub) EraseTree(tr, sub);
                        obj.Erase();
                    }
                }
                else
                {
                    root = new DBDictionary { TreatElementsAsHard = true };
                    nod.SetAt(NOD_KEY, root);
                    tr.AddNewlyCreatedDBObject(root, true);
                }

                // ── META (settings + timestamp) at root level ─────────────────
                MakeXRecord(tr, root, K_META,
                    Str(report.GeneratedAt.ToString("u")),
                    I16((short)(settings.EnableClashDetection ? 1 : 0)),
                    I16((short)settings.ExcavationOverlap),
                    I16((short)settings.BackfillOverlap),
                    I16((short)settings.ManholeType),
                    I16((short)settings.Language),
                    Str(settings.ManholeConfigPath ?? ""),
                    Dbl(settings.SolidDisplayInterval),
                    Dbl(settings.CrossSectionInterval),
                    // Appended (not inserted) — keeps old-DWG positional reads intact.
                    I16((short)(settings.BacaKaziHesapla ? 1 : 0)),
                    Str(settings.BacaKirmiziKotSurface ?? "Arazi1"),
                    Str(settings.BacaAraziKotuSurface ?? "Arazi1"),
                    Str(settings.BacaTerrasmanKotuSurface ?? "Arazi1"),
                    Str(settings.BacaKirmiziKotC3DSurface ?? ""),
                    Str(settings.BacaAraziKotuC3DSurface ?? ""),
                    Str(settings.BacaTerrasmanKotuC3DSurface ?? ""),
                    Str(settings.KaziSeviyesi ?? "Kırmızı Kot"),
                    Str(settings.DolguSeviyesi ?? "Kırmızı Kot"),
                    Str(settings.BacaKapakSeviyesi ?? "Kırmızı Kot"),
                    I16((short)settings.RingFillMode),
                    I16((short)settings.NetLengthMode),
                    I16((short)(settings.BacaBacaKaziHesapla ? 1 : 0)),
                    I16((short)(settings.BacaAltiParcaEklensin ? 1 : 0)),
                    I16((short)(settings.BacaKaziDisCapKullan ? 1 : 0)),
                    Dbl(settings.MetrajDegiskenParcaBandM));

                // Manhole lookup by system name (first wins on duplicate names).
                var sysByName = new Dictionary<string, SystemBoQ>(StringComparer.Ordinal);
                foreach (var sys in report.Systems ?? Enumerable.Empty<SystemBoQ>())
                    if (!sysByName.ContainsKey(sys.SystemName ?? "")) sysByName[sys.SystemName ?? ""] = sys;

                // ── All pipe networks go under AGLAR ──────────────────────────
                var aglar = MakeSubDict(tr, root, K_NETWORKS);
                var rows = report.SectionDebug ?? new List<SectionDebugRow>();
                foreach (var grp in rows.GroupBy(r => r.SystemName ?? ""))
                {
                    string netName = grp.Key;
                    var netDict = MakeSubDict(tr, aglar, UniqueKey(aglar, SafeKey(netName)));

                    // NETWORK_META: original name + manhole list.
                    sysByName.TryGetValue(netName, out var sysBoQ);
                    WriteNetworkMeta(tr, netDict, netName, sysBoQ);
                    WriteManholeStacks(tr, netDict, sysBoQ);

                    foreach (var sdr in grp)
                    {
                        string pid = UniqueKey(netDict,
                            "P_" + SafeKey(sdr.StartNodeName) + "_to_" + SafeKey(sdr.EndNodeName));
                        var pipeDict = MakeSubDict(tr, netDict, pid);

                        WritePipeMetadata(tr, pipeDict, sdr, netName);
                        if (sdr.HasV2Volumes)
                            WriteV2Volumes(tr, pipeDict, sdr);
                        WriteLayerSplits(tr, pipeDict, sdr);

                        foreach (var st in sdr.Stations ?? new List<CrossSectionStation>())
                        {
                            string sk = UniqueKey(pipeDict, ChainageKey(st.StationDist));
                            var staDict = MakeSubDict(tr, pipeDict, sk);

                            WriteStationInfo(tr, staDict, st);

                            // EXCAVATION_KAZI branch (natural ground).
                            var exDict = MakeSubDict(tr, staDict, K_EXCAVATION);
                            WriteLayerScenarios(tr, exDict, st, TrenchLayerType.Excavation);

                            // BACKFILL_LAYERS branch (final grade).
                            var bfDict = MakeSubDict(tr, staDict, K_BACKFILL);
                            WriteLayerScenarios(tr, MakeSubDict(tr, bfDict, K_YATAKLAMA),  st, TrenchLayerType.Bedding);
                            WriteLayerScenarios(tr, MakeSubDict(tr, bfDict, K_GOMLEKLEME), st, TrenchLayerType.Surround);
                            WriteLayerScenarios(tr, MakeSubDict(tr, bfDict, K_GERI_DOLGU), st, TrenchLayerType.Backfill);

                            // PIPE_BODY.
                            var pb = new List<TypedValue> { Dbl(PolyArea(st.PipeBodyPoly)) };
                            WritePolyVar(pb, st.PipeBodyPoly);
                            MakeXRecord(tr, staDict, K_PIPE_BODY, pb.ToArray());
                        }
                    }
                }

                tr.Commit();
            }
        }

        // =====================================================================
        // UpdateSettings — updates only the META XRecord, leaves all pipe/station data intact
        // =====================================================================

        public static void UpdateSettings(Database db, DateTime generatedAt, BoQSettings settings)
        {
            if (settings == null) return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_KEY)) { tr.Commit(); return; }

                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);
                if (!root.Contains(K_META)) { tr.Commit(); return; }

                // Update the ResultBuffer of the existing META XRecord in-place.
                var rec = tr.GetObject(root.GetAt(K_META), OpenMode.ForWrite) as Xrecord;
                if (rec != null)
                    rec.Data = new ResultBuffer(BuildMetaBuffer(generatedAt, settings));

                tr.Commit();
            }
        }

        // =====================================================================
        // SaveSettings — persists BoQSettings on their own, CREATING the
        // URBANO_BOQ root + META record if the drawing has no BoQ store yet.
        // Lets the "Genel Ayarlar" tab (Proje Ayarları window) edit + save the
        // settings before any Metraj calculation has been run. When a report is
        // already stored, the existing GeneratedAt timestamp is preserved and all
        // pipe/station/network data is left untouched.
        // =====================================================================

        public static void SaveSettings(Database db, BoQSettings settings)
        {
            if (settings == null) return;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                DBDictionary root;
                if (nod.Contains(NOD_KEY))
                {
                    root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForWrite);
                }
                else
                {
                    root = new DBDictionary { TreatElementsAsHard = true };
                    nod.SetAt(NOD_KEY, root);
                    tr.AddNewlyCreatedDBObject(root, true);
                }

                // Preserve an existing report's timestamp; otherwise stamp "now".
                DateTime gen = DateTime.Now;
                if (root.Contains(K_META))
                {
                    var existing = ReadXRecord(tr, root, K_META);
                    if (existing != null && existing.Length > 0
                        && existing[0].Value is string s
                        && DateTime.TryParse(s, CultureInfo.InvariantCulture,
                                             DateTimeStyles.None, out var g))
                        gen = g;

                    var old = tr.GetObject(root.GetAt(K_META), OpenMode.ForWrite);
                    old.Erase();
                }

                MakeXRecord(tr, root, K_META, BuildMetaBuffer(gen, settings));
                tr.Commit();
            }
        }

        // Canonical META field order (append-only) — must stay in sync with the
        // positional reader in Load(). Shared by UpdateSettings + SaveSettings.
        private static TypedValue[] BuildMetaBuffer(DateTime generatedAt, BoQSettings s)
        {
            return new[]
            {
                Str(generatedAt.ToString("u")),
                I16((short)(s.EnableClashDetection ? 1 : 0)),
                I16((short)s.ExcavationOverlap),
                I16((short)s.BackfillOverlap),
                I16((short)s.ManholeType),
                I16((short)s.Language),
                Str(s.ManholeConfigPath ?? ""),
                Dbl(s.SolidDisplayInterval),
                Dbl(s.CrossSectionInterval),
                I16((short)(s.BacaKaziHesapla ? 1 : 0)),
                Str(s.BacaKirmiziKotSurface ?? "Arazi1"),
                Str(s.BacaAraziKotuSurface ?? "Arazi1"),
                Str(s.BacaTerrasmanKotuSurface ?? "Arazi1"),
                Str(s.BacaKirmiziKotC3DSurface ?? ""),
                Str(s.BacaAraziKotuC3DSurface ?? ""),
                Str(s.BacaTerrasmanKotuC3DSurface ?? ""),
                Str(s.KaziSeviyesi ?? "Kırmızı Kot"),
                Str(s.DolguSeviyesi ?? "Kırmızı Kot"),
                Str(s.BacaKapakSeviyesi ?? "Kırmızı Kot"),
                I16((short)s.RingFillMode),
                I16((short)s.NetLengthMode),
                I16((short)(s.BacaBacaKaziHesapla ? 1 : 0)),
                I16((short)(s.BacaAltiParcaEklensin ? 1 : 0)),
                I16((short)(s.BacaKaziDisCapKullan ? 1 : 0)),
                Dbl(s.MetrajDegiskenParcaBandM)
            };
        }

        // =====================================================================
        // HasData / Load
        // =====================================================================

        public static bool HasData(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                bool found = nod.Contains(NOD_KEY);
                tr.Commit();
                return found;
            }
        }

        public static (BoQReport report, BoQSettings settings) Load(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_KEY)) { tr.Commit(); return (null, null); }

                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);

                // ── META ──────────────────────────────────────────────────────
                var meta = ReadXRecord(tr, root, K_META);
                if (meta == null) { tr.Commit(); return (null, null); }
                int mi = 0;
                string genStr = ReadStr(meta, ref mi);
                var settings = new BoQSettings
                {
                    EnableClashDetection = ReadI16(meta, ref mi) != 0,
                    ExcavationOverlap    = (OverlapAssignment)ReadI16(meta, ref mi),
                    BackfillOverlap      = (OverlapAssignment)ReadI16(meta, ref mi),
                    ManholeType          = (ManholeType)ReadI16(meta, ref mi),
                    Language             = (ExportLanguage)ReadI16(meta, ref mi),
                    ManholeConfigPath      = ReadStr(meta, ref mi),
                    SolidDisplayInterval   = mi < meta.Length ? ReadDbl(meta, ref mi) : 5.0,
                    CrossSectionInterval   = mi < meta.Length ? ReadDbl(meta, ref mi) : 5.0,
                    // Appended fields (absent in old DWGs — default gracefully).
                    BacaKaziHesapla          = mi < meta.Length && ReadI16(meta, ref mi) != 0,
                    BacaKirmiziKotSurface    = mi < meta.Length ? ReadStr(meta, ref mi) : "Arazi1",
                    BacaAraziKotuSurface     = mi < meta.Length ? ReadStr(meta, ref mi) : "Arazi1",
                    BacaTerrasmanKotuSurface = mi < meta.Length ? ReadStr(meta, ref mi) : "Arazi1",
                    BacaKirmiziKotC3DSurface    = mi < meta.Length ? ReadStr(meta, ref mi) : "",
                    BacaAraziKotuC3DSurface     = mi < meta.Length ? ReadStr(meta, ref mi) : "",
                    BacaTerrasmanKotuC3DSurface = mi < meta.Length ? ReadStr(meta, ref mi) : "",
                    KaziSeviyesi        = mi < meta.Length ? ReadStr(meta, ref mi) : "Kırmızı Kot",
                    DolguSeviyesi       = mi < meta.Length ? ReadStr(meta, ref mi) : "Kırmızı Kot",
                    BacaKapakSeviyesi   = mi < meta.Length ? ReadStr(meta, ref mi) : "Kırmızı Kot",
                    RingFillMode        = mi < meta.Length ? (RingFillMode)ReadI16(meta, ref mi) : RingFillMode.Greedy,
                    NetLengthMode       = mi < meta.Length ? (NetLengthMode)ReadI16(meta, ref mi) : NetLengthMode.OuterDiameter,
                    BacaBacaKaziHesapla = mi < meta.Length && ReadI16(meta, ref mi) != 0,
                    BacaAltiParcaEklensin = mi < meta.Length && ReadI16(meta, ref mi) != 0,
                    // Absent in older DWGs → default true (Dış Çap), matching BoQSettings.
                    BacaKaziDisCapKullan = mi >= meta.Length || ReadI16(meta, ref mi) != 0,
                    // Metraj variable-piece height band (m). Absent in older DWGs → 0.5.
                    MetrajDegiskenParcaBandM = mi < meta.Length ? ReadDbl(meta, ref mi) : 0.5
                };

                var report = new BoQReport();
                if (DateTime.TryParse(genStr, CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var gen)) report.GeneratedAt = gen;

                // ── Walk the network tree ─────────────────────────────────────
                // New format: networks are under root["AGLAR"].
                // Old-format fallback: networks were directly under root.
                DBDictionary networkContainer = root.Contains(K_NETWORKS)
                    ? (DBDictionary)tr.GetObject(root.GetAt(K_NETWORKS), OpenMode.ForRead)
                    : root;

                foreach (DBDictionaryEntry netEntry in networkContainer)
                {
                    if (netEntry.Key == K_META) continue;
                    var netDict = tr.GetObject(netEntry.Value, OpenMode.ForRead) as DBDictionary;
                    if (netDict == null) continue;

                    var sys = ReadNetworkMeta(tr, netDict, out string netName);
                    if (string.IsNullOrEmpty(netName)) netName = netEntry.Key;
                    ReadManholeStacks(tr, netDict, sys);

                    foreach (DBDictionaryEntry pipeEntry in netDict)
                    {
                        if (pipeEntry.Key == K_NETWORK_META) continue;
                        var pipeDict = tr.GetObject(pipeEntry.Value, OpenMode.ForRead) as DBDictionary;
                        if (pipeDict == null) continue;

                        var sdr = ReadPipeMetadata(tr, pipeDict);
                        sdr.SystemName = netName;   // keep grouping key consistent with the system
                        ReadV2Volumes(tr, pipeDict, sdr);
                        ReadLayerSplits(tr, pipeDict, sdr);

                        foreach (DBDictionaryEntry staEntry in pipeDict)
                        {
                            if (staEntry.Key == K_METADATA) continue;
                            var staDict = tr.GetObject(staEntry.Value, OpenMode.ForRead) as DBDictionary;
                            if (staDict == null) continue;

                            var st = ReadStationInfo(tr, staDict);

                            var pu = new ScenarioProfile { Preference = TiePreference.KeepUpper };
                            var pl = new ScenarioProfile { Preference = TiePreference.KeepLower };
                            var ps = new ScenarioProfile { Preference = TiePreference.Split };

                            var exDict = GetSubDict(tr, staDict, K_EXCAVATION);
                            ReadLayerScenarios(tr, exDict, TrenchLayerType.Excavation, pu, pl, ps);

                            var bfDict = GetSubDict(tr, staDict, K_BACKFILL);
                            if (bfDict != null)
                            {
                                ReadLayerScenarios(tr, GetSubDict(tr, bfDict, K_YATAKLAMA),  TrenchLayerType.Bedding,  pu, pl, ps);
                                ReadLayerScenarios(tr, GetSubDict(tr, bfDict, K_GOMLEKLEME), TrenchLayerType.Surround, pu, pl, ps);
                                ReadLayerScenarios(tr, GetSubDict(tr, bfDict, K_GERI_DOLGU), TrenchLayerType.Backfill, pu, pl, ps);
                            }

                            st.ScenarioKeepUpper = pu;
                            st.ScenarioKeepLower = pl;
                            st.ScenarioSplit     = ps;

                            var pb = ReadXRecord(tr, staDict, K_PIPE_BODY);
                            if (pb != null) { int pi = 0; ReadDbl(pb, ref pi); st.PipeBodyPoly = ReadPolyVar(pb, ref pi); }

                            sdr.Stations.Add(st);
                        }

                        sdr.Stations.Sort((a, b) => a.StationDist.CompareTo(b.StationDist));
                        report.SectionDebug.Add(sdr);
                    }

                    report.Systems.Add(sys);
                }

                // Rebuild per-system pipe aggregates + section volumes from the cache
                // for the saved default preferences (the dialog re-applies on demand).
                BoQScenarioAggregator.Apply(report,
                    BoQScenarioAggregator.Map(settings.ExcavationOverlap),
                    BoQScenarioAggregator.Map(settings.BackfillOverlap));

                tr.Commit();
                return (report, settings);
            }
        }

        // =====================================================================
        // Branch writers / readers
        // =====================================================================

        private static void WriteNetworkMeta(
            Transaction tr, DBDictionary netDict, string netName, SystemBoQ sys)
        {
            var tvs = new List<TypedValue> { Str(netName) };
            var mhs = sys?.Manholes ?? new List<ManholeItem>();
            tvs.Add(I32(mhs.Count));
            foreach (var m in mhs)
            {
                tvs.Add(Str(m.NodeName ?? ""));
                tvs.Add(Str(m.SmartTypeName ?? ""));
                tvs.Add(Dbl(m.X)); tvs.Add(Dbl(m.Y));
                tvs.Add(Dbl(m.TerrainElevation)); tvs.Add(Dbl(m.Depth));
                tvs.Add(I32(m.Diameter));
            }
            // Appended AFTER the fixed-shape repeated group (not interleaved) so old
            // DWGs stay positionally readable — one MhGuid per manhole, same order.
            foreach (var m in mhs) tvs.Add(Str(m.MhGuid ?? ""));

            // Appended AFTER the MhGuid block (same reasoning) — AI/excavation fields
            // that were computed during Process() but previously never persisted, so a
            // Load()-only command (no re-run of ManholeAIService) can still read them.
            foreach (var m in mhs)
            {
                tvs.Add(Dbl(m.ExistingGroundElevation));
                tvs.Add(Dbl(m.ExcavationDepth));
                tvs.Add(Dbl(m.ExcavationVolume));
                tvs.Add(I32(m.ValidInletCount));
                tvs.Add(I32(m.ValidOutletCount));
                tvs.Add(I16(m.HasDropPipe ? (short)1 : (short)0));
            }

            // Appended AFTER the AI/excavation block (Phase 7) — ExcavBaseSideM/
            // ExcavSlopeRatio are required inputs for ManholeExcavOverlapService's
            // ManholeSquareAt to build ANY polygon at all; without persisting them,
            // reopening the results dialog (URBANO_BOQ_VIEW → DwgBoQStore.Load, which
            // re-runs ManholeExcavOverlapService.Compute but NOT ManholeAIService.Process)
            // would silently recompute every manhole-vs-pipe overlap as zero.
            foreach (var m in mhs)
            {
                tvs.Add(Dbl(m.ExcavWorkingClearanceM));
                tvs.Add(Dbl(m.ExcavSlopeRatio));
                tvs.Add(Dbl(m.ExcavBaseSideM));
            }

            // Appended AFTER that (Phase 7) — the matched tier's Geri Dolgu layers,
            // needed by the same reload path so SplitManholeBackfillLayers can
            // rebuild BackfillLayerSplits instead of finding an empty list.
            foreach (var m in mhs)
            {
                var layers = m.ResolvedBackfillLayers ?? new List<ManholeBackfillLayer>();
                tvs.Add(I32(layers.Count));
                foreach (var l in layers)
                {
                    tvs.Add(Str(l.LayerName ?? ""));
                    tvs.Add(Str(l.MaterialType ?? ""));
                    tvs.Add(Dbl(l.ThicknessM));
                    tvs.Add(I16(l.IsFillToSurface ? (short)1 : (short)0));
                }
            }

            // Appended AFTER the backfill-layers block — Urbano's own node
            // rotation (radians, "NR"/"NLR"), needed on the reload path
            // (URBANO_BOQ_VIEW re-runs ManholeExcavOverlapService.Compute but not
            // BoQParserService.Parse) so ComputeRotationAngle can still use it
            // directly instead of silently falling back to the bisector
            // heuristic. Presence flag written separately since 0.0 is itself a
            // valid angle (can't reuse it as a "missing" sentinel).
            foreach (var m in mhs)
            {
                tvs.Add(I16(m.RotationAngleRad.HasValue ? (short)1 : (short)0));
                tvs.Add(Dbl(m.RotationAngleRad ?? 0.0));
            }

            // Appended AFTER the rotation-angle block — Kot/Seviye Ayarları-resolved
            // elevations and the Dolgu-basis pit depth/volume (2026-07-06 feature).
            // Needed on the reload path (URBANO_BOQ_VIEW → DwgBoQStore.Load, which
            // re-runs ManholeExcavOverlapService.Compute but not BoQParserService.Parse
            // or ManholeAIService.Process) so ZKazi/ZDolgu/DolguFinalDepth/
            // DolguBasisVolume are still available instead of silently reading back
            // as 0 (which would zero out BackfillVolume and the excavation-overlap
            // zTop on every reopen).
            foreach (var m in mhs)
            {
                tvs.Add(Dbl(m.ZKazi));
                tvs.Add(Dbl(m.ZDolgu));
                tvs.Add(Dbl(m.ZBacaKapak));
                tvs.Add(Dbl(m.DolguFinalDepth));
                tvs.Add(Dbl(m.DolguBasisVolume));
                tvs.Add(I16(m.DolguInvalid ? (short)1 : (short)0));
            }

            // Appended AFTER the Kot/Seviye Ayarları block — the resolved Taban's
            // Temel Altı Parça (sub-base pieces), needed by the Baca Kesif Tablosu
            // export's Load()-only reload path (BacaKesifTablosuCommand never
            // re-runs ManholeAIService.Process()).
            foreach (var m in mhs)
            {
                var parts = m.ResolvedSubBaseParts ?? new List<TemelAltiParcaComponent>();
                tvs.Add(I32(parts.Count));
                foreach (var p in parts)
                {
                    tvs.Add(Str(p.Name     ?? ""));
                    tvs.Add(Dbl(p.Boy));
                    tvs.Add(Dbl(p.En));
                    tvs.Add(Dbl(p.EffectiveHeight));
                    tvs.Add(Str(p.Aciklama ?? ""));
                }
            }

            // Appended AFTER the Temel Altı Parça block — the matched tier's Alt
            // Temel Katmanları (sub-base preparation layers, e.g. "yataklama kumu"),
            // needed by ManholeExcavOverlapService.Compute (SubBaseVolume) on the
            // same Load()-only reload path.
            foreach (var m in mhs)
            {
                var layers = m.ResolvedSubBaseLayers ?? new List<SubBaseLayer>();
                tvs.Add(I32(layers.Count));
                foreach (var l in layers)
                {
                    tvs.Add(Str(l.LayerName ?? ""));
                    tvs.Add(Str(l.MaterialType ?? ""));
                    tvs.Add(Dbl(l.ThicknessMm));
                }
            }

            MakeXRecord(tr, netDict, K_NETWORK_META, tvs.ToArray());
        }

        private static SystemBoQ ReadNetworkMeta(
            Transaction tr, DBDictionary netDict, out string netName)
        {
            netName = "";
            var sys = new SystemBoQ();
            var tvs = ReadXRecord(tr, netDict, K_NETWORK_META);
            if (tvs == null) { sys.SystemName = ""; return sys; }
            int i = 0;
            netName = ReadStr(tvs, ref i);
            sys.SystemName = netName;
            int mh = ReadI32(tvs, ref i);
            for (int k = 0; k < mh; k++)
            {
                sys.Manholes.Add(new ManholeItem
                {
                    NodeName         = ReadStr(tvs, ref i),
                    SmartTypeName    = ReadStr(tvs, ref i),
                    X                = ReadDbl(tvs, ref i),
                    Y                = ReadDbl(tvs, ref i),
                    TerrainElevation = ReadDbl(tvs, ref i),
                    Depth            = ReadDbl(tvs, ref i),
                    Diameter         = ReadI32(tvs, ref i)
                });
            }
            // Trailing MhGuid block (absent in old DWGs — ReadStr gracefully
            // returns "" past the end of tvs, so each manhole just stays unlinked).
            foreach (var m in sys.Manholes) m.MhGuid = ReadStr(tvs, ref i);

            // Trailing AI/excavation block (absent in old DWGs — Read* gracefully
            // return 0/false past the end of tvs).
            foreach (var m in sys.Manholes)
            {
                m.ExistingGroundElevation = ReadDbl(tvs, ref i);
                m.ExcavationDepth         = ReadDbl(tvs, ref i);
                m.ExcavationVolume        = ReadDbl(tvs, ref i);
                m.ValidInletCount         = ReadI32(tvs, ref i);
                m.ValidOutletCount        = ReadI32(tvs, ref i);
                m.HasDropPipe             = ReadI16(tvs, ref i) != 0;
            }

            // Trailing Phase 7 excavation-geometry block (absent in old DWGs —
            // Read* gracefully return 0, meaning ManholeExcavOverlapService just
            // computes zero overlap for that manhole, same as an unresolved one).
            foreach (var m in sys.Manholes)
            {
                m.ExcavWorkingClearanceM = ReadDbl(tvs, ref i);
                m.ExcavSlopeRatio        = ReadDbl(tvs, ref i);
                m.ExcavBaseSideM         = ReadDbl(tvs, ref i);
            }

            // Trailing Phase 7 backfill-layers block (absent in old DWGs — count
            // reads as 0 past the end of tvs, so the loop below is simply skipped).
            foreach (var m in sys.Manholes)
            {
                int layerCount = ReadI32(tvs, ref i);
                var layers = new List<ManholeBackfillLayer>(layerCount);
                for (int p = 0; p < layerCount && i < tvs.Length; p++)
                {
                    layers.Add(new ManholeBackfillLayer
                    {
                        LayerName       = ReadStr(tvs, ref i),
                        MaterialType    = ReadStr(tvs, ref i),
                        ThicknessM      = ReadDbl(tvs, ref i),
                        IsFillToSurface = ReadI16(tvs, ref i) == 1
                    });
                }
                m.ResolvedBackfillLayers = layers;
            }

            // Trailing rotation-angle block (absent in old DWGs — ReadI16/ReadDbl
            // gracefully return 0 past the end of tvs, so the "has value" flag
            // reads false and RotationAngleRad stays null — ComputeRotationAngle
            // then falls back to its bisector heuristic exactly as before).
            foreach (var m in sys.Manholes)
            {
                bool hasAngle = ReadI16(tvs, ref i) == 1;
                double angle  = ReadDbl(tvs, ref i);
                m.RotationAngleRad = hasAngle ? angle : (double?)null;
            }

            // Trailing Kot/Seviye Ayarları elevation block (absent in old DWGs —
            // Read* gracefully return 0, meaning a reload-only command computes
            // zero overlap/backfill for that manhole until the next full Hesapla).
            foreach (var m in sys.Manholes)
            {
                m.ZKazi           = ReadDbl(tvs, ref i);
                m.ZDolgu          = ReadDbl(tvs, ref i);
                m.ZBacaKapak      = ReadDbl(tvs, ref i);
                m.DolguFinalDepth = ReadDbl(tvs, ref i);
                m.DolguBasisVolume = ReadDbl(tvs, ref i);
                m.DolguInvalid     = ReadI16(tvs, ref i) == 1;
            }

            // Trailing Temel Altı Parça block (absent in old DWGs — count reads as
            // 0 past the end of tvs, so the loop below is simply skipped).
            foreach (var m in sys.Manholes)
            {
                int partCount = ReadI32(tvs, ref i);
                var parts = new List<TemelAltiParcaComponent>(partCount);
                for (int p = 0; p < partCount && i < tvs.Length; p++)
                {
                    parts.Add(new TemelAltiParcaComponent
                    {
                        Name            = ReadStr(tvs, ref i),
                        Boy             = ReadDbl(tvs, ref i),
                        En              = ReadDbl(tvs, ref i),
                        EffectiveHeight = ReadDbl(tvs, ref i),
                        Aciklama        = ReadStr(tvs, ref i)
                    });
                }
                m.ResolvedSubBaseParts = parts;
            }

            // Trailing Alt Temel Katmanları block (absent in old DWGs — count reads
            // as 0 past the end of tvs, so the loop below is simply skipped).
            foreach (var m in sys.Manholes)
            {
                int layerCount = ReadI32(tvs, ref i);
                var layers = new List<SubBaseLayer>(layerCount);
                for (int p = 0; p < layerCount && i < tvs.Length; p++)
                {
                    layers.Add(new SubBaseLayer
                    {
                        LayerName    = ReadStr(tvs, ref i),
                        MaterialType = ReadStr(tvs, ref i),
                        ThicknessMm  = ReadDbl(tvs, ref i)
                    });
                }
                m.ResolvedSubBaseLayers = layers;
            }

            return sys;
        }

        private static void WriteManholeStacks(
            Transaction tr, DBDictionary netDict, SystemBoQ sys)
        {
            var mhs = sys?.Manholes;
            if (mhs == null || mhs.Count == 0) return;

            var tvs = new List<TypedValue> { I32(mhs.Count) };
            foreach (var m in mhs)
            {
                tvs.Add(Str(m.NodeName ?? ""));

                // PreCast stack
                bool hasPc = m.StackPreCast != null;
                tvs.Add(I16(hasPc ? (short)1 : (short)0));
                if (hasPc)
                {
                    tvs.Add(Dbl(m.StackPreCast.ResidualM));
                    tvs.Add(I32(m.StackPreCast.Parts?.Count ?? 0));
                    foreach (var p in m.StackPreCast.Parts ?? new List<StackedPart>())
                    {
                        tvs.Add(Str(p.PartName ?? ""));
                        tvs.Add(Dbl(p.HeightM));
                        tvs.Add(I32(p.Count));
                        tvs.Add(I16(p.IsVariableRing ? (short)1 : (short)0));
                    }
                }

                // CastInPlace stack
                bool hasCip = m.StackCastInPlace != null;
                tvs.Add(I16(hasCip ? (short)1 : (short)0));
                if (hasCip)
                    tvs.Add(Dbl(m.StackCastInPlace.ConcreteDepth));
            }

            // Trailing block (absent in old DWGs — Read* gracefully return ""/0
            // past the end of tvs): PozNo/Aciklama/UnitMaterialVolume/
            // UnitExternalVolume per pre-cast part, in the exact same
            // manhole/part order as the main loop above (only manholes with a
            // stack, only their real part count). These 4 fields were added to
            // StackedPart after this method was first written and never wired
            // in here — they silently vanished on every DWG save/load
            // round-trip even though ManholeAIService computed them correctly.
            foreach (var m in mhs)
            {
                if (m.StackPreCast?.Parts == null) continue;
                foreach (var p in m.StackPreCast.Parts)
                {
                    tvs.Add(Str(p.PozNo ?? ""));
                    tvs.Add(Str(p.Aciklama ?? ""));
                    tvs.Add(Dbl(p.UnitMaterialVolume));
                    tvs.Add(Dbl(p.UnitExternalVolume));
                }
            }

            // Second trailing block (added with the net-pipe-length feature):
            // WallThicknessMm per pre-cast part, same manhole/part order as above.
            // Lets PipeNetLengthService resolve a manhole's outer-shell radius from
            // a reloaded DWG (URBANO_BOQ_VIEW) without re-running ManholeAIService.
            // 0 (missing/old DWG) means "no wall-thickness data" — behaves exactly
            // like null in PipeNetLengthService's reduction formula (adds nothing).
            foreach (var m in mhs)
            {
                if (m.StackPreCast?.Parts == null) continue;
                foreach (var p in m.StackPreCast.Parts)
                    tvs.Add(Dbl(p.WallThicknessMm ?? 0));
            }

            // Third trailing block (added with the net-pipe-length feature): one
            // ResolvedFootprint per manhole (all manholes, not just pre-cast ones),
            // in the same order as sys.Manholes. This is the ACTUAL resolved precast
            // Taban's shape/size — deliberately separate from DrawnShape/Diameter/
            // DrawnLengthM/DrawnWidthM (see ManholeItem.ResolvedFootprint doc) — so
            // PipeNetLengthService gets correct results after a DWG reload too.
            foreach (var m in mhs)
            {
                var fp = m.ResolvedFootprint;
                tvs.Add(I16(fp != null ? (short)1 : (short)0));
                if (fp != null)
                {
                    tvs.Add(I32((int)fp.Shape));
                    tvs.Add(Dbl(fp.DiameterMm));
                    tvs.Add(Dbl(fp.SideMm));
                    tvs.Add(Dbl(fp.LengthMm));
                    tvs.Add(Dbl(fp.WidthMm));
                }
            }

            MakeXRecord(tr, netDict, K_MANHOLE_STACKS, tvs.ToArray());
        }

        private static void ReadManholeStacks(
            Transaction tr, DBDictionary netDict, SystemBoQ sys)
        {
            var tvs = ReadXRecord(tr, netDict, K_MANHOLE_STACKS);
            if (tvs == null || sys?.Manholes == null) return;

            // Build a name-to-manhole lookup for fast matching.
            var lookup = new Dictionary<string, ManholeItem>(StringComparer.OrdinalIgnoreCase);
            foreach (var m in sys.Manholes) if (m.NodeName != null) lookup[m.NodeName] = m;

            int i = 0;
            int count = ReadI32(tvs, ref i);

            // Manholes with a pre-cast stack, in read order — lets the trailing
            // PozNo/Aciklama/volume block (added after this method's original
            // 4-field-per-part shape) be matched back up positionally, same
            // manhole/part order as the write side.
            var pcOrder = new List<ManholeItem>();

            // Every manhole (found or not), in read order — needed to keep the
            // ResolvedFootprint trailing block (one entry per manhole, not just
            // pre-cast ones) positionally aligned with the write side.
            var allOrder = new List<ManholeItem>();

            for (int k = 0; k < count && i < tvs.Length; k++)
            {
                string nodeName = ReadStr(tvs, ref i);
                lookup.TryGetValue(nodeName, out var mh);
                allOrder.Add(mh);

                // PreCast
                short hasPc = ReadI16(tvs, ref i);
                if (hasPc == 1)
                {
                    double residual = ReadDbl(tvs, ref i);
                    int partCount   = ReadI32(tvs, ref i);
                    var parts       = new List<StackedPart>(partCount);
                    for (int p = 0; p < partCount && i < tvs.Length; p++)
                    {
                        parts.Add(new StackedPart
                        {
                            PartName       = ReadStr(tvs, ref i),
                            HeightM        = ReadDbl(tvs, ref i),
                            Count          = ReadI32(tvs, ref i),
                            IsVariableRing = ReadI16(tvs, ref i) == 1
                        });
                    }
                    if (mh != null)
                    {
                        mh.StackPreCast = new ManholeStackResult
                        {
                            NominalDiameter = mh.Diameter,
                            IsPreCast       = true,
                            ResidualM       = residual,
                            Parts           = parts
                        };
                        pcOrder.Add(mh);
                    }
                }

                // CastInPlace
                short hasCip = ReadI16(tvs, ref i);
                if (hasCip == 1)
                {
                    double depth = ReadDbl(tvs, ref i);
                    if (mh != null)
                        mh.StackCastInPlace = new ManholeStackResult
                        {
                            NominalDiameter = mh.Diameter,
                            IsPreCast       = false,
                            ConcreteDepth   = depth
                        };
                }
            }

            // Trailing PozNo/Aciklama/volume block (absent in old DWGs — Read*
            // gracefully return ""/0 past the end of tvs, so parts just stay
            // blank/zero exactly as before this fix).
            foreach (var mh in pcOrder)
                foreach (var p in mh.StackPreCast.Parts)
                {
                    p.PozNo              = ReadStr(tvs, ref i);
                    p.Aciklama           = ReadStr(tvs, ref i);
                    p.UnitMaterialVolume = ReadDbl(tvs, ref i);
                    p.UnitExternalVolume = ReadDbl(tvs, ref i);
                }

            // Second trailing WallThicknessMm block (absent in older DWGs — Read*
            // gracefully returns 0 past the end of tvs, same "no data" meaning as null).
            foreach (var mh in pcOrder)
                foreach (var p in mh.StackPreCast.Parts)
                    p.WallThicknessMm = ReadDbl(tvs, ref i);

            // Third trailing block: one ResolvedFootprint per manhole (absent in
            // older DWGs — simply stops here, leaving ResolvedFootprint null exactly
            // like an unresolved Taban).
            foreach (var mh in allOrder)
            {
                if (i >= tvs.Length) break;
                short hasFp = ReadI16(tvs, ref i);
                if (hasFp == 1)
                {
                    var shape    = (FootprintShape)ReadI32(tvs, ref i);
                    double diaMm  = ReadDbl(tvs, ref i);
                    double sideMm = ReadDbl(tvs, ref i);
                    double lenMm  = ReadDbl(tvs, ref i);
                    double widMm  = ReadDbl(tvs, ref i);
                    if (mh != null)
                        mh.ResolvedFootprint = new Footprint
                        {
                            Shape      = shape,
                            DiameterMm = diaMm,
                            SideMm     = sideMm,
                            LengthMm   = lenMm,
                            WidthMm    = widMm
                        };
                }
            }
        }

        private static void WritePipeMetadata(
            Transaction tr, DBDictionary pipeDict, SectionDebugRow s, string netName)
        {
            var tvs = new List<TypedValue>
            {
                // Required readable topology strings (graph traversal).
                Str(s.StartNodeName ?? ""),
                Str(s.EndNodeName ?? ""),
                Str(s.PipeName ?? ""),
                Str(netName ?? ""),
                I32(s.DiameterMm),
                Str(s.Material ?? ""),
            };
            foreach (var d in new[]
            {
                s.PipeOuterDiamM, s.Length2D,
                s.StartX, s.StartY, s.StartTerrainZ, s.EndX, s.EndY, s.EndTerrainZ,
                s.InvertStart, s.InvertEnd, s.DepthToInvStart, s.DepthToInvEnd,
                s.TrWidth, s.TrBedHeight, s.TrSandOverPipe, s.TrAngleL, s.TrAngleR, s.SlopeRatio,
                s.TopWidthBed, s.ABed, s.HSurround, s.BaseWidthSurr, s.TopWidthSurr,
                s.ASurroundGross, s.PipeArea, s.ASurroundNet,
                s.TrueDepthStart, s.TrueDepthEnd, s.TopWidthExcavS, s.TopWidthExcavE,
                s.AExcavStart, s.AExcavEnd, s.ABackfillStart, s.ABackfillEnd,
            }) tvs.Add(Dbl(d));
            // Appended (not inserted) — keeps old-DWG positional reads intact.
            tvs.Add(Str(s.PozNo    ?? ""));
            tvs.Add(Str(s.Sinif    ?? ""));
            tvs.Add(Str(s.Aciklama ?? ""));
            tvs.Add(Str(s.LinkedPipeFamilyId.ToString()));
            MakeXRecord(tr, pipeDict, K_METADATA, tvs.ToArray());
        }

        private static void WriteV2Volumes(Transaction tr, DBDictionary pipeDict, SectionDebugRow s)
        {
            MakeXRecord(tr, pipeDict, K_V2_VOLUMES,
                I16(1),
                Dbl(s.VExcavKU),      Dbl(s.VExcavKL),      Dbl(s.VExcavSP),    Dbl(s.VExcavGross),
                Dbl(s.VBedding),      Dbl(s.VSurround),
                Dbl(s.VBackfillKU),   Dbl(s.VBackfillKL),   Dbl(s.VBackfillSP), Dbl(s.VBackfillGross));
        }

        private static void ReadV2Volumes(Transaction tr, DBDictionary pipeDict, SectionDebugRow sdr)
        {
            var tvs = ReadXRecord(tr, pipeDict, K_V2_VOLUMES);
            if (tvs == null) return;
            int i = 0;
            if (ReadI16(tvs, ref i) == 0) return;
            sdr.HasV2Volumes    = true;
            sdr.VExcavKU        = ReadDbl(tvs, ref i);
            sdr.VExcavKL        = ReadDbl(tvs, ref i);
            sdr.VExcavSP        = ReadDbl(tvs, ref i);
            sdr.VExcavGross     = i < tvs.Length ? ReadDbl(tvs, ref i) : Math.Max(sdr.VExcavKU, sdr.VExcavKL);
            sdr.VBedding        = ReadDbl(tvs, ref i);
            sdr.VSurround       = ReadDbl(tvs, ref i);
            sdr.VBackfillKU     = ReadDbl(tvs, ref i);
            sdr.VBackfillKL     = ReadDbl(tvs, ref i);
            sdr.VBackfillSP     = ReadDbl(tvs, ref i);
            sdr.VBackfillGross  = i < tvs.Length ? ReadDbl(tvs, ref i) : Math.Max(sdr.VBackfillKU, sdr.VBackfillKL);
        }

        // Phase 2b — PipeTrenchCatalog per-sub-layer ratios (LayerName/MaterialType/
        // Ratio only; Volume is re-derived on load via BoQScenarioAggregator, not
        // persisted). Absent for old DWGs or pipes with no matching catalog rule —
        // each list simply stays empty (SectionDebugRow's own List<> default).
        private static void WriteLayerSplits(Transaction tr, DBDictionary pipeDict, SectionDebugRow s)
        {
            bool any = (s.BeddingLayerSplits?.Count ?? 0) > 0
                    || (s.BoruEtrafiLayerSplits?.Count ?? 0) > 0
                    || (s.BoruUstuLayerSplits?.Count ?? 0) > 0
                    || (s.BackfillLayerSplits?.Count ?? 0) > 0;
            if (!any) return;

            var tvs = new List<TypedValue> { I16(1) };
            WriteSplitGroup(tvs, s.BeddingLayerSplits);
            WriteSplitGroup(tvs, s.BoruEtrafiLayerSplits);
            WriteSplitGroup(tvs, s.BoruUstuLayerSplits);
            WriteSplitGroup(tvs, s.BackfillLayerSplits);
            MakeXRecord(tr, pipeDict, K_LAYER_SPLITS, tvs.ToArray());
        }

        private static void WriteSplitGroup(List<TypedValue> tvs, List<TrenchLayerSplit> group)
        {
            group = group ?? new List<TrenchLayerSplit>();
            tvs.Add(I32(group.Count));
            foreach (var l in group)
            {
                tvs.Add(Str(l.LayerName));
                tvs.Add(Str(l.MaterialType));
                tvs.Add(Dbl(l.Ratio));
            }
        }

        private static void ReadLayerSplits(Transaction tr, DBDictionary pipeDict, SectionDebugRow sdr)
        {
            var tvs = ReadXRecord(tr, pipeDict, K_LAYER_SPLITS);
            if (tvs == null) return;
            int i = 0;
            if (ReadI16(tvs, ref i) == 0) return;
            sdr.BeddingLayerSplits    = ReadSplitGroup(tvs, ref i);
            sdr.BoruEtrafiLayerSplits = ReadSplitGroup(tvs, ref i);
            sdr.BoruUstuLayerSplits   = ReadSplitGroup(tvs, ref i);
            sdr.BackfillLayerSplits   = ReadSplitGroup(tvs, ref i);
        }

        private static List<TrenchLayerSplit> ReadSplitGroup(TypedValue[] tvs, ref int i)
        {
            int count = ReadI32(tvs, ref i);
            var group = new List<TrenchLayerSplit>(count);
            for (int n = 0; n < count; n++)
                group.Add(new TrenchLayerSplit
                {
                    LayerName    = ReadStr(tvs, ref i),
                    MaterialType = ReadStr(tvs, ref i),
                    Ratio        = ReadDbl(tvs, ref i)
                });
            return group;
        }

        private static SectionDebugRow ReadPipeMetadata(Transaction tr, DBDictionary pipeDict)
        {
            var sdr = new SectionDebugRow();
            var tvs = ReadXRecord(tr, pipeDict, K_METADATA);
            if (tvs == null) return sdr;
            int i = 0;
            sdr.StartNodeName = ReadStr(tvs, ref i);
            sdr.EndNodeName   = ReadStr(tvs, ref i);
            sdr.PipeName      = ReadStr(tvs, ref i);
            /* netName  */      ReadStr(tvs, ref i);
            sdr.DiameterMm    = ReadI32(tvs, ref i);
            sdr.Material      = ReadStr(tvs, ref i);
            sdr.PipeOuterDiamM= ReadDbl(tvs, ref i);
            sdr.Length2D      = ReadDbl(tvs, ref i);
            sdr.StartX        = ReadDbl(tvs, ref i); sdr.StartY = ReadDbl(tvs, ref i); sdr.StartTerrainZ = ReadDbl(tvs, ref i);
            sdr.EndX          = ReadDbl(tvs, ref i); sdr.EndY   = ReadDbl(tvs, ref i); sdr.EndTerrainZ   = ReadDbl(tvs, ref i);
            sdr.InvertStart   = ReadDbl(tvs, ref i); sdr.InvertEnd = ReadDbl(tvs, ref i);
            sdr.DepthToInvStart = ReadDbl(tvs, ref i); sdr.DepthToInvEnd = ReadDbl(tvs, ref i);
            sdr.TrWidth       = ReadDbl(tvs, ref i); sdr.TrBedHeight = ReadDbl(tvs, ref i); sdr.TrSandOverPipe = ReadDbl(tvs, ref i);
            sdr.TrAngleL      = ReadDbl(tvs, ref i); sdr.TrAngleR = ReadDbl(tvs, ref i); sdr.SlopeRatio = ReadDbl(tvs, ref i);
            sdr.TopWidthBed   = ReadDbl(tvs, ref i); sdr.ABed = ReadDbl(tvs, ref i); sdr.HSurround = ReadDbl(tvs, ref i);
            sdr.BaseWidthSurr = ReadDbl(tvs, ref i); sdr.TopWidthSurr = ReadDbl(tvs, ref i);
            sdr.ASurroundGross= ReadDbl(tvs, ref i); sdr.PipeArea = ReadDbl(tvs, ref i); sdr.ASurroundNet = ReadDbl(tvs, ref i);
            sdr.TrueDepthStart= ReadDbl(tvs, ref i); sdr.TrueDepthEnd = ReadDbl(tvs, ref i);
            sdr.TopWidthExcavS= ReadDbl(tvs, ref i); sdr.TopWidthExcavE = ReadDbl(tvs, ref i);
            sdr.AExcavStart   = ReadDbl(tvs, ref i); sdr.AExcavEnd = ReadDbl(tvs, ref i);
            sdr.ABackfillStart= ReadDbl(tvs, ref i); sdr.ABackfillEnd = ReadDbl(tvs, ref i);
            // Appended fields (Phase 5) — ReadStr gracefully returns "" for old DWGs
            // that predate them (i beyond tvs.Length).
            sdr.PozNo    = ReadStr(tvs, ref i);
            sdr.Sinif    = ReadStr(tvs, ref i);
            sdr.Aciklama = ReadStr(tvs, ref i);
            Guid famId;
            Guid.TryParse(ReadStr(tvs, ref i), out famId);
            sdr.LinkedPipeFamilyId = famId;
            return sdr;
        }

        private static void WriteStationInfo(
            Transaction tr, DBDictionary staDict, CrossSectionStation st)
        {
            var tvs = new List<TypedValue>();
            foreach (var d in new[]
            {
                st.StationDist, st.WorldX, st.WorldY, st.TerrainZ, st.InvertZ, st.TrueDepth, st.TopWidthExcav,
                st.AreaExcav, st.AreaBedding, st.AreaSurround, st.AreaBackfill, st.AreaExcavNet, st.AreaBackfillNet,
            }) tvs.Add(Dbl(d));
            tvs.Add(I16(st.HasOverlap ? (short)1 : (short)0));
            tvs.Add(I16(st.IsCrossingBoundary ? (short)1 : (short)0));
            WritePoly4(tvs, st.ExcavPoly);
            WritePoly4(tvs, st.BeddingPoly);
            WritePoly4(tvs, st.SurroundPoly);
            WritePoly4(tvs, st.BackfillPoly);
            MakeXRecord(tr, staDict, K_STATION_INFO, tvs.ToArray());
        }

        private static CrossSectionStation ReadStationInfo(Transaction tr, DBDictionary staDict)
        {
            var st = new CrossSectionStation();
            var tvs = ReadXRecord(tr, staDict, K_STATION_INFO);
            if (tvs == null) return st;
            int i = 0;
            st.StationDist   = ReadDbl(tvs, ref i);
            st.WorldX        = ReadDbl(tvs, ref i);
            st.WorldY        = ReadDbl(tvs, ref i);
            st.TerrainZ      = ReadDbl(tvs, ref i);
            st.InvertZ       = ReadDbl(tvs, ref i);
            st.TrueDepth     = ReadDbl(tvs, ref i);
            st.TopWidthExcav = ReadDbl(tvs, ref i);
            st.AreaExcav     = ReadDbl(tvs, ref i);
            st.AreaBedding   = ReadDbl(tvs, ref i);
            st.AreaSurround  = ReadDbl(tvs, ref i);
            st.AreaBackfill  = ReadDbl(tvs, ref i);
            st.AreaExcavNet  = ReadDbl(tvs, ref i);
            st.AreaBackfillNet = ReadDbl(tvs, ref i);
            st.HasOverlap          = ReadI16(tvs, ref i) != 0;
            st.IsCrossingBoundary  = i < tvs.Length && ReadI16(tvs, ref i) != 0;  // backward compat
            st.ExcavPoly           = ReadPoly4(tvs, ref i);
            st.BeddingPoly   = ReadPoly4(tvs, ref i);
            st.SurroundPoly  = ReadPoly4(tvs, ref i);
            st.BackfillPoly  = ReadPoly4(tvs, ref i);
            return st;
        }

        /// <summary>Writes the 3 scenario XRecords (Upper/Lower/50-50) of one layer.</summary>
        private static void WriteLayerScenarios(
            Transaction tr, DBDictionary layerDict, CrossSectionStation st, TrenchLayerType layer)
        {
            WriteScenarioRecord(tr, layerDict, K_SC_UPPER, st.ScenarioKeepUpper?.Layer(layer));
            WriteScenarioRecord(tr, layerDict, K_SC_LOWER, st.ScenarioKeepLower?.Layer(layer));
            WriteScenarioRecord(tr, layerDict, K_SC_5050,  st.ScenarioSplit?.Layer(layer));
        }

        private static void WriteScenarioRecord(
            Transaction tr, DBDictionary dict, string key, LayerNet ln)
        {
            var polygon = SanitizeRegion(ln?.Polygon);
            double area = polygon != null ? ClipperGeo.Area(polygon) : (ln?.NetArea ?? 0);
            var tvs = new List<TypedValue> { Dbl(area) };
            WriteRegion(tvs, polygon);
            MakeXRecord(tr, dict, key, tvs.ToArray());
        }

        /// <summary>
        /// Resolves self-intersections and removes degenerate "needle" artifacts before
        /// persisting scenario polygons.
        ///
        /// Pass 1 — Clipper2 self-union: splits butterfly/figure-8 polygons (produced
        /// when ClipByHalfplane clips a non-convex ring) into valid non-self-intersecting
        /// pieces.
        ///
        /// Pass 2 — needle filter: discards any ring that contains an edge whose length
        /// is at or below the snap grid (0.1 mm). Such an edge means two adjacent vertices
        /// are on neighbouring snap-grid cells — the minimum non-zero Clipper distance —
        /// and the ring is a numerical artifact (a collapsed spike), not real geometry.
        /// No legitimate trench cross-section has an edge shorter than 0.1 mm.
        /// </summary>
        private static List<List<double[]>> SanitizeRegion(List<List<double[]>> region)
        {
            if (region == null || region.Count == 0) return region;

            var union = ClipperGeo.Union(region);
            if (union == null || union.Count == 0) return region;

            const double snapGrid2 = ClipperGeo.SnapGrid * ClipperGeo.SnapGrid;
            var clean = union.Where(ring =>
            {
                for (int i = 0; i < ring.Count; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                    double du = b[0] - a[0], dz = b[1] - a[1];
                    if (du * du + dz * dz <= snapGrid2) return false; // needle edge → reject ring
                }
                return true;
            }).ToList();

            return clean.Count > 0 ? clean : union;
        }

        /// <summary>Reads the 3 scenario records of one layer into the 3 profiles.</summary>
        private static void ReadLayerScenarios(
            Transaction tr, DBDictionary layerDict, TrenchLayerType layer,
            ScenarioProfile upper, ScenarioProfile lower, ScenarioProfile split)
        {
            if (layerDict == null) return;
            FillLayer(ReadScenarioRecord(tr, layerDict, K_SC_UPPER), upper.Layer(layer));
            FillLayer(ReadScenarioRecord(tr, layerDict, K_SC_LOWER), lower.Layer(layer));
            FillLayer(ReadScenarioRecord(tr, layerDict, K_SC_5050),  split.Layer(layer));
        }

        private static void FillLayer((double area, List<List<double[]>> poly)? r, LayerNet target)
        {
            if (r == null || target == null) return;
            target.NetArea = r.Value.area;
            target.Polygon = r.Value.poly;
        }

        private static (double area, List<List<double[]>> poly)? ReadScenarioRecord(
            Transaction tr, DBDictionary dict, string key)
        {
            var tvs = ReadXRecord(tr, dict, key);
            if (tvs == null) return null;
            int i = 0;
            double area = ReadDbl(tvs, ref i);
            var poly = ReadRegion(tvs, ref i);
            return (area, poly);
        }

        // =====================================================================
        // Dictionary tree helpers
        // =====================================================================

        private static DBDictionary MakeSubDict(Transaction tr, DBDictionary parent, string key)
        {
            var d = new DBDictionary { TreatElementsAsHard = true };
            parent.SetAt(key, d);
            tr.AddNewlyCreatedDBObject(d, true);
            return d;
        }

        private static DBDictionary GetSubDict(Transaction tr, DBDictionary parent, string key)
        {
            if (parent == null || !parent.Contains(key)) return null;
            return tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as DBDictionary;
        }

        private static void EraseTree(Transaction tr, DBDictionary dict)
        {
            var ids = new List<ObjectId>();
            foreach (DBDictionaryEntry e in dict) ids.Add(e.Value);
            foreach (var id in ids)
            {
                var obj = tr.GetObject(id, OpenMode.ForWrite);
                if (obj is DBDictionary sub) EraseTree(tr, sub);
                obj.Erase();
            }
        }

        private static Xrecord MakeXRecord(
            Transaction tr, DBDictionary parent, string key, params TypedValue[] tvs)
        {
            var rec = new Xrecord { Data = new ResultBuffer(tvs) };
            parent.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return rec;
        }

        private static TypedValue[] ReadXRecord(Transaction tr, DBDictionary parent, string key)
        {
            if (parent == null || !parent.Contains(key)) return null;
            var rec = tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as Xrecord;
            if (rec?.Data == null) return null;
            var list = new List<TypedValue>();
            foreach (TypedValue tv in rec.Data) list.Add(tv);
            return list.ToArray();
        }

        // ── Key helpers ───────────────────────────────────────────────────────

        private static readonly char[] BadKeyChars =
            { '<', '>', '/', '\\', '"', ':', ';', '?', '*', '|', '=', ',', '`' };

        private static string SafeKey(string s)
        {
            if (string.IsNullOrEmpty(s)) return "_";
            var sb = new StringBuilder(s.Length);
            foreach (char c in s)
                sb.Append((c < 32 || Array.IndexOf(BadKeyChars, c) >= 0) ? '_' : c);
            string r = sb.ToString().Trim();
            if (r.Length == 0) return "_";
            return r.Length > 200 ? r.Substring(0, 200) : r;
        }

        private static string UniqueKey(DBDictionary parent, string baseKey)
        {
            if (!parent.Contains(baseKey)) return baseKey;
            for (int n = 2; ; n++)
            {
                string k = baseKey + "_" + n;
                if (!parent.Contains(k)) return k;
            }
        }

        /// <summary>Chainage key "STA_{km}+{rem:000.###}" (zero-padded, decimals only when needed).</summary>
        private static string ChainageKey(double d)
        {
            if (d < 0) d = 0;
            int km = (int)Math.Floor(d / 1000.0);
            double rem = d - km * 1000.0;
            return "STA_" + km + "+" + rem.ToString("000.###", CultureInfo.InvariantCulture);
        }

        private static double PolyArea(List<double[]> ring)
        {
            if (ring == null || ring.Count < 3) return 0;
            double sum = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum) * 0.5;
        }

        // =====================================================================
        // TypedValue factories / readers
        // =====================================================================

        private static TypedValue Str(string v) => new TypedValue((int)DxfCode.Text,  v ?? "");
        private static TypedValue Dbl(double v) => new TypedValue((int)DxfCode.Real,  v);
        private static TypedValue I32(int v)    => new TypedValue((int)DxfCode.Int32, v);
        private static TypedValue I16(short v)  => new TypedValue((int)DxfCode.Int16, v);

        private static string ReadStr(TypedValue[] tvs, ref int i)
            => (i < tvs.Length) ? (tvs[i++].Value as string ?? "") : "";

        private static double ReadDbl(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            return tv.Value is double d ? d : 0;
        }

        private static int ReadI32(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is int n)   return n;
            if (tv.Value is short s) return s;
            if (tv.Value is long l)  return (int)l;
            return 0;
        }

        private static short ReadI16(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is short s) return s;
            if (tv.Value is int n)   return (short)n;
            return 0;
        }

        // =====================================================================
        // Polygon / region serialisation
        // =====================================================================

        private static void WritePoly4(List<TypedValue> tvs, List<double[]> poly)
        {
            for (int v = 0; v < 4; v++)
            {
                bool ok = poly != null && v < poly.Count && poly[v] != null && poly[v].Length >= 2;
                tvs.Add(Dbl(ok ? poly[v][0] : 0));
                tvs.Add(Dbl(ok ? poly[v][1] : 0));
            }
        }

        private static List<double[]> ReadPoly4(TypedValue[] tvs, ref int i)
        {
            var poly = new List<double[]>(4);
            for (int v = 0; v < 4; v++)
                poly.Add(new[] { ReadDbl(tvs, ref i), ReadDbl(tvs, ref i) });
            return poly;
        }

        // cnt: 0 = null, -1 = empty list, >=3 = real vertex count.
        private static void WritePolyVar(List<TypedValue> tvs, List<double[]> poly)
        {
            if (poly == null) { tvs.Add(I32(0)); return; }
            // Last-line-of-defence cleanup: never persist coincident/spur/collinear
            // vertices, regardless of what the geometry engine produced upstream.
            if (poly.Count >= 4) poly = ClipperGeo.CleanRing(poly);
            if (poly.Count == 0) { tvs.Add(I32(-1)); return; }
            tvs.Add(I32(poly.Count));
            for (int v = 0; v < poly.Count; v++)
            {
                bool ok = poly[v] != null && poly[v].Length >= 2;
                tvs.Add(Dbl(ok ? poly[v][0] : 0));
                tvs.Add(Dbl(ok ? poly[v][1] : 0));
            }
        }

        private static List<double[]> ReadPolyVar(TypedValue[] tvs, ref int i)
        {
            int cnt = ReadI32(tvs, ref i);
            if (cnt == -1) return new List<double[]>();
            if (cnt <= 0) return null;
            var poly = new List<double[]>(cnt);
            for (int v = 0; v < cnt; v++)
                poly.Add(new[] { ReadDbl(tvs, ref i), ReadDbl(tvs, ref i) });
            return poly;
        }

        /// <summary>Region = ring count, then each ring via WritePolyVar.</summary>
        private static void WriteRegion(List<TypedValue> tvs, List<List<double[]>> region)
        {
            if (region == null) { tvs.Add(I32(0)); return; }
            tvs.Add(I32(region.Count));
            foreach (var ring in region) WritePolyVar(tvs, ring);
        }

        private static List<List<double[]>> ReadRegion(TypedValue[] tvs, ref int i)
        {
            int n = ReadI32(tvs, ref i);
            var region = new List<List<double[]>>(n > 0 ? n : 0);
            for (int k = 0; k < n; k++)
            {
                var ring = ReadPolyVar(tvs, ref i);
                if (ring != null && ring.Count >= 3) region.Add(ring);
            }
            return region;
        }
    }
}
