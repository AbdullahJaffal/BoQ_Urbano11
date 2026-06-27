using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SectionsDbCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// AutoCAD command: URBANO_SECTIONS_DB  (from-scratch diagnostic plotter)
    ///
    /// Unlike URBANO_SECTIONS, this command does NOT use DwgBoQStore.Load, the
    /// cached BoQReport, the in-memory ScenarioProfile objects, or any of the
    /// resolve/aggregate pipeline. It walks the NOD dictionary tree DIRECTLY —
    /// exactly like the diagnostic LISP — pulling each station's stored polygon
    /// verbatim and drawing it. No fallback to gross/ExcavPoly ever happens:
    /// whatever sits in SCENARIO_50_50 is what gets drawn, period.
    ///
    /// Output goes to dedicated URB_DBG_* layers so the original URB_KS_* drawing
    /// stays on screen for side-by-side comparison.
    /// </summary>
    public class SectionsDbCommand
    {
        // ── NOD keys (kept local so this command is fully self-contained) ─────
        private const string NOD_KEY      = "URBANO_BOQ";
        private const string K_NETWORKS   = "AGLAR";
        private const string K_META       = "META";
        private const string K_NET_META   = "NETWORK_META";
        private const string K_MH_STACKS  = "MANHOLE_STACKS";
        private const string K_PIPE_META  = "METADATA";
        private const string K_STATION    = "STATION_INFO";
        private const string K_EXCAV      = "EXCAVATION_KAZI";
        private const string K_BACKFILL   = "BACKFILL_LAYERS";
        private const string K_PIPE_BODY  = "PIPE_BODY";
        private const string K_YATAKLAMA  = "YATAKLAMA";
        private const string K_GOMLEKLEME = "GOMLEKLEME";
        private const string K_GERI_DOLGU = "GERI_DOLGU";
        private const string K_SC_5050    = "SCENARIO_50_50";

        // ── Dedicated debug layers (distinct from URB_KS_*) ───────────────────
        private const string LExcav = "URB_DBG_Hafriyat";
        private const string LBed   = "URB_DBG_Yataklama";
        private const string LSur   = "URB_DBG_Gomlekleme";
        private const string LBack  = "URB_DBG_GeriDolgu";
        private const string LPipe  = "URB_DBG_Boru";
        private const string LText  = "URB_DBG_Text";

        private const short AciExcav    = 30;
        private const short AciBed      = 2;
        private const short AciSur      = 3;
        private const short AciBack     = 6;
        private const short AciPipe     = 4;
        private const short AciText     = 7;
        private const short AciBoundary = 1;

        // ── Small in-memory model read straight from the dictionary ───────────
        private sealed class DbStation
        {
            public double Dist;
            public double InvertZ;
            public bool   IsBoundary;
            public List<List<double[]>> Excav = new List<List<double[]>>();
            public List<List<double[]>> Bed   = new List<List<double[]>>();
            public List<List<double[]>> Sur   = new List<List<double[]>>();
            public List<List<double[]>> Back  = new List<List<double[]>>();
            public List<double[]>       Pipe;
        }

        private sealed class DbPipe
        {
            public string Label;
            public List<DbStation> Stations = new List<DbStation>();
        }

        // =====================================================================
        [CommandMethod("URBANO_SECTIONS_DB", CommandFlags.Modal)]
        public void DrawSectionsFromDb()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            Database db  = doc.Database;

            // ── Interval ──────────────────────────────────────────────────────
            double interval = 5.0;
            var iOpt = new PromptDoubleOptions("\n[DB Kesit] Örnekleme aralığı (m)")
            { DefaultValue = 5.0, AllowNegative = false, AllowZero = false, UseDefaultValue = true };
            var iRes = ed.GetDouble(iOpt);
            if (iRes.Status == PromptStatus.OK) interval = iRes.Value;

            // ── Insertion point ───────────────────────────────────────────────
            var pRes = ed.GetPoint(
                new PromptPointOptions("\n[DB Kesit] Başlangıç noktasını seçin: ")
                { AllowNone = false });
            if (pRes.Status != PromptStatus.OK) return;
            Point3d insert = pRes.Value;

            List<DbPipe> pipes;
            try
            {
                pipes = ReadAllPipes(db, ed);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[DB Kesit HATA] Okuma: {ex.GetType().Name}: {ex.Message}\n");
                return;
            }

            if (pipes.Count == 0)
            {
                ed.WriteMessage("\n[DB Kesit] Çizilecek boru bulunamadı.\n");
                return;
            }

            try
            {
                using (doc.LockDocument())
                {
                    int n = DrawPipes(db, pipes, insert, interval, ed);
                    ed.WriteMessage($"\n[DB Kesit] Tamamlandı. {n} nesne (URB_DBG_*).\n");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[DB Kesit HATA] Çizim: {ex.GetType().Name}: {ex.Message}\n");
            }
        }

        // =====================================================================
        // Single station — same direct-DB logic, one named station only.
        // =====================================================================
        [CommandMethod("URBANO_SECTION_ONE", CommandFlags.Modal)]
        public void DrawOneSectionFromDb()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            Database db  = doc.Database;

            // ── Network / pipe / station keys ─────────────────────────────────
            string net = AskString(ed, "\n[DB Tek] Ağ adı (ör. YSU): ");
            if (net == null) return;
            string pipe = AskString(ed, "\n[DB Tek] Boru anahtarı (ör. P_N2_to_N3): ");
            if (pipe == null) return;
            string sta = AskString(ed, "\n[DB Tek] İstasyon anahtarı (ör. STA_0+000): ");
            if (sta == null) return;

            // ── Insertion point ───────────────────────────────────────────────
            var pRes = ed.GetPoint(
                new PromptPointOptions("\n[DB Tek] Çizim noktasını seçin: ")
                { AllowNone = false });
            if (pRes.Status != PromptStatus.OK) return;
            Point3d insert = pRes.Value;

            DbStation st;
            try
            {
                st = ReadOneStation(db, ed, net, pipe, sta);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[DB Tek HATA] Okuma: {ex.GetType().Name}: {ex.Message}\n");
                return;
            }
            if (st == null)
            {
                ed.WriteMessage("\n[DB Tek] İstasyon bulunamadı (anahtarları kontrol edin).\n");
                return;
            }

            try
            {
                using (doc.LockDocument())
                {
                    int n = DrawSingle(db, st, insert,
                        net + "/" + pipe + "/" + sta, ed);
                    ed.WriteMessage($"\n[DB Tek] Tamamlandı. {n} nesne (URB_DBG_*).\n");
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[DB Tek HATA] Çizim: {ex.GetType().Name}: {ex.Message}\n");
            }
        }

        // =====================================================================
        // URBANO_SECTION_DUMP — inspect raw DB coordinates without drawing
        // =====================================================================
        [CommandMethod("URBANO_SECTION_DUMP", CommandFlags.Modal)]
        public void DumpOneSectionCoords()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            Database db  = doc.Database;

            string net  = AskString(ed, "\n[DB Dump] Ağ adı (ör. YSU): ");
            if (net == null) return;
            string pipe = AskString(ed, "\n[DB Dump] Boru anahtarı (ör. P_N2_to_N3): ");
            if (pipe == null) return;
            string sta  = AskString(ed, "\n[DB Dump] İstasyon anahtarı (ör. STA_0+000): ");
            if (sta == null) return;

            TypedValue[] rawInfo;
            DbStation st;
            try
            {
                st = ReadOneStationWithRaw(db, ed, net, pipe, sta, out rawInfo);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[DB Dump HATA] {ex.GetType().Name}: {ex.Message}\n");
                return;
            }
            if (st == null)
            {
                ed.WriteMessage("\n[DB Dump] İstasyon bulunamadı.\n");
                return;
            }

            string label = net + "/" + pipe + "/" + sta;
            string text  = BuildDump(rawInfo, st, label);
            string path  = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"urb_dump_{SanitizeFileName(net)}_{SanitizeFileName(pipe)}_{SanitizeFileName(sta)}_{DateTime.Now:HHmmss}.txt");
            File.WriteAllText(path, text, Encoding.UTF8);

            ed.WriteMessage($"\n[DB Dump] → {path}");
            ed.WriteMessage($"\n{BuildSummaryLine(st)}\n");
        }

        private static string AskString(Editor ed, string prompt)
        {
            var opt = new PromptStringOptions(prompt) { AllowSpaces = false };
            var res = ed.GetString(opt);
            if (res.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(res.StringResult))
                return null;
            return res.StringResult.Trim();
        }

        // =====================================================================
        // Reading — walk the NOD tree exactly like the LISP
        // =====================================================================
        private static List<DbPipe> ReadAllPipes(Database db, Editor ed)
        {
            var result = new List<DbPipe>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_KEY))
                {
                    ed.WriteMessage("\n[DB Kesit] URBANO_BOQ yok.");
                    tr.Commit();
                    return result;
                }

                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);

                // Networks live under AGLAR; fall back to root children for old DWGs.
                DBDictionary netParent = root;
                if (root.Contains(K_NETWORKS))
                    netParent = (DBDictionary)tr.GetObject(
                        root.GetAt(K_NETWORKS), OpenMode.ForRead);

                foreach (DBDictionaryEntry netEntry in netParent)
                {
                    string netKey = netEntry.Key;
                    if (netKey == K_META || netKey == K_NET_META) continue;

                    var netDict = tr.GetObject(netEntry.Value, OpenMode.ForRead) as DBDictionary;
                    if (netDict == null) continue;

                    foreach (DBDictionaryEntry pipeEntry in netDict)
                    {
                        string pipeKey = pipeEntry.Key;
                        if (!pipeKey.StartsWith("P_")) continue;   // skip NETWORK_META, MANHOLE_STACKS

                        var pipeDict = tr.GetObject(pipeEntry.Value, OpenMode.ForRead) as DBDictionary;
                        if (pipeDict == null) continue;

                        var pipe = new DbPipe { Label = netKey + "/" + pipeKey };

                        foreach (DBDictionaryEntry staEntry in pipeDict)
                        {
                            if (!staEntry.Key.StartsWith("STA_")) continue;  // skip METADATA
                            var staDict = tr.GetObject(staEntry.Value, OpenMode.ForRead) as DBDictionary;
                            if (staDict == null) continue;

                            var st = ReadStation(tr, staDict);
                            if (st != null) pipe.Stations.Add(st);
                        }

                        if (pipe.Stations.Count > 0)
                        {
                            pipe.Stations.Sort((a, b) => a.Dist.CompareTo(b.Dist));
                            result.Add(pipe);
                        }
                    }
                }

                tr.Commit();
            }

            return result;
        }

        /// <summary>
        /// Navigates URBANO_BOQ → (AGLAR) → net → pipe → station directly and reads
        /// that one station. Key matching is exact-first, then startsWith, then
        /// case-insensitive contains — tolerant of UniqueKey suffixes.
        /// </summary>
        private static DbStation ReadOneStation(
            Database db, Editor ed, string netKey, string pipeKey, string staKey)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_KEY)) { tr.Commit(); return null; }

                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);

                DBDictionary netParent = root;
                if (root.Contains(K_NETWORKS))
                    netParent = (DBDictionary)tr.GetObject(
                        root.GetAt(K_NETWORKS), OpenMode.ForRead);

                var netDict = FindChild(tr, netParent, netKey,
                    new[] { K_META, K_NET_META });
                if (netDict == null)
                {
                    DumpKeys(ed, netParent, "Ağlar", new[] { K_META, K_NET_META });
                    tr.Commit(); return null;
                }

                var pipeDict = FindChild(tr, netDict, pipeKey, null);
                if (pipeDict == null)
                {
                    DumpKeys(ed, netDict, "Borular", new[] { K_NET_META, K_MH_STACKS });
                    tr.Commit(); return null;
                }

                var staDict = FindChild(tr, pipeDict, staKey, new[] { K_PIPE_META });
                if (staDict == null)
                {
                    DumpKeys(ed, pipeDict, "İstasyonlar", new[] { K_PIPE_META });
                    tr.Commit(); return null;
                }

                var st = ReadStation(tr, staDict);
                tr.Commit();
                return st;
            }
        }

        /// <summary>Finds a child dictionary by key (exact → prefix → contains, ci).</summary>
        private static DBDictionary FindChild(
            Transaction tr, DBDictionary parent, string key, string[] skip)
        {
            if (parent == null || string.IsNullOrEmpty(key)) return null;
            bool Skip(string k) => skip != null && skip.Contains(k);

            // exact
            if (parent.Contains(key) && !Skip(key))
            {
                var d = tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as DBDictionary;
                if (d != null) return d;
            }
            // prefix then contains (case-insensitive)
            for (int pass = 0; pass < 2; pass++)
                foreach (DBDictionaryEntry e in parent)
                {
                    if (Skip(e.Key)) continue;
                    bool hit = pass == 0
                        ? e.Key.StartsWith(key, StringComparison.OrdinalIgnoreCase)
                        : e.Key.IndexOf(key, StringComparison.OrdinalIgnoreCase) >= 0;
                    if (!hit) continue;
                    var d = tr.GetObject(e.Value, OpenMode.ForRead) as DBDictionary;
                    if (d != null) return d;
                }
            return null;
        }

        private static void DumpKeys(Editor ed, DBDictionary dict, string label, string[] skip)
        {
            if (ed == null || dict == null) return;
            var keys = new List<string>();
            foreach (DBDictionaryEntry e in dict)
                if (skip == null || !skip.Contains(e.Key)) keys.Add(e.Key);
            ed.WriteMessage($"\n  Mevcut {label}: {string.Join(", ", keys)}");
        }

        private static DbStation ReadStation(Transaction tr, DBDictionary staDict)
        {
            var st = new DbStation();

            // STATION_INFO: dist + invertZ + IsCrossingBoundary flag.
            var si = ReadXrec(tr, staDict, K_STATION);
            if (si == null) return null;
            ReadStationInfo(si, st);

            // EXCAVATION_KAZI → SCENARIO_50_50
            var exDict = GetSub(tr, staDict, K_EXCAV);
            st.Excav = ReadScenarioRegion(tr, exDict, K_SC_5050);

            // BACKFILL_LAYERS → YATAKLAMA / GOMLEKLEME / GERI_DOLGU → SCENARIO_50_50
            var bfDict = GetSub(tr, staDict, K_BACKFILL);
            if (bfDict != null)
            {
                st.Bed  = ReadScenarioRegion(tr, GetSub(tr, bfDict, K_YATAKLAMA),  K_SC_5050);
                st.Sur  = ReadScenarioRegion(tr, GetSub(tr, bfDict, K_GOMLEKLEME), K_SC_5050);
                st.Back = ReadScenarioRegion(tr, GetSub(tr, bfDict, K_GERI_DOLGU), K_SC_5050);
            }

            // PIPE_BODY: [Real area][polyvar]
            var pb = ReadXrec(tr, staDict, K_PIPE_BODY);
            if (pb != null)
            {
                int i = 0;
                ReadReal(pb, ref i);          // skip area
                var rings = ReadRegionVar(pb, ref i, singleRing: true);
                if (rings.Count > 0) st.Pipe = rings[0];
            }

            return st;
        }

        /// <summary>Positional read of STATION_INFO (13 reals, then 2 int16 flags).</summary>
        private static void ReadStationInfo(TypedValue[] tv, DbStation st)
        {
            int i = 0;
            st.Dist = ReadReal(tv, ref i);    // [0] StationDist
            ReadReal(tv, ref i);              // WorldX
            ReadReal(tv, ref i);              // WorldY
            ReadReal(tv, ref i);              // TerrainZ
            st.InvertZ = ReadReal(tv, ref i); // [4] InvertZ
            for (int k = 0; k < 8; k++) ReadReal(tv, ref i);  // TrueDepth..AreaBackfillNet
            ReadI16(tv, ref i);               // HasOverlap
            st.IsBoundary = i < tv.Length && ReadI16(tv, ref i) != 0;
        }

        /// <summary>
        /// Reads a SCENARIO record [Real area][Int32 ringCount]{[Int32 vCount][U,Z]*}*
        /// and returns the region exactly as stored. No fallback, no gross.
        /// </summary>
        private static List<List<double[]>> ReadScenarioRegion(
            Transaction tr, DBDictionary layerDict, string key)
        {
            var empty = new List<List<double[]>>();
            if (layerDict == null) return empty;
            var tv = ReadXrec(tr, layerDict, key);
            if (tv == null) return empty;

            int i = 0;
            ReadReal(tv, ref i);                       // skip stored NetArea
            return ReadRegionVar(tv, ref i, singleRing: false);
        }

        /// <summary>
        /// Variable region reader. When <paramref name="singleRing"/> is true the
        /// stream is a single polyvar (ring count then verts); otherwise it is a
        /// full region (ring-count header, then one polyvar per ring).
        /// </summary>
        private static List<List<double[]>> ReadRegionVar(
            TypedValue[] tv, ref int i, bool singleRing)
        {
            var region = new List<List<double[]>>();

            if (singleRing)
            {
                var ring = ReadPolyVar(tv, ref i);
                if (ring != null && ring.Count >= 3) region.Add(ring);
                return region;
            }

            int nRings = ReadI32(tv, ref i);
            for (int k = 0; k < nRings; k++)
            {
                var ring = ReadPolyVar(tv, ref i);
                if (ring != null && ring.Count >= 3) region.Add(ring);
            }
            return region;
        }

        /// <summary>cnt: 0 = null, -1 = empty, >=1 = that many (U,Z) pairs.</summary>
        private static List<double[]> ReadPolyVar(TypedValue[] tv, ref int i)
        {
            int cnt = ReadI32(tv, ref i);
            if (cnt <= 0) return null;
            var poly = new List<double[]>(cnt);
            for (int v = 0; v < cnt; v++)
                poly.Add(new[] { ReadReal(tv, ref i), ReadReal(tv, ref i) });
            return poly;
        }

        // =====================================================================
        // Drawing — same two-panel layout as URBANO_SECTIONS, raw DB regions
        // =====================================================================
        private static int DrawPipes(
            Database db, List<DbPipe> pipes, Point3d insert, double interval, Editor ed)
        {
            // Global metrics across every station that will be drawn.
            double maxHalfW = 1.0, maxSecH = 2.0;
            foreach (var p in pipes)
                foreach (var st in SelectStations(p.Stations, interval))
                {
                    var ex = st.Excav;
                    if (ex == null || ex.Count == 0) continue;
                    foreach (var ring in ex)
                        foreach (var v in ring)
                            maxHalfW = Math.Max(maxHalfW, Math.Abs(v[0]));
                    double lo = double.MaxValue, hi = double.MinValue;
                    foreach (var ring in ex)
                        foreach (var v in ring) { lo = Math.Min(lo, v[1]); hi = Math.Max(hi, v[1]); }
                    if (hi > lo) maxSecH = Math.Max(maxSecH, hi - lo);
                }

            double panelHalfW = maxHalfW;
            double subGap     = panelHalfW * 0.15;
            double interGap   = panelHalfW * 0.20;
            double colStep    = panelHalfW * 4.0 + subGap + interGap;
            double excavLocalX = panelHalfW;
            double fillLocalX  = panelHalfW * 3.0 + subGap;
            double labelLocalX = panelHalfW * 2.0 + subGap * 0.5;
            double rowStep     = maxSecH * 1.3;
            double textH       = maxSecH * 0.04;

            var plines = new List<(Polyline pl, string layer, short aci)>();
            var texts  = new List<(string txt, double x, double y, double h, string layer, short aci)>();

            double rowOffsetY = 0.0;

            foreach (var pipe in pipes)
            {
                var stations = SelectStations(pipe.Stations, interval);
                if (stations.Count == 0) continue;

                texts.Add((pipe.Label.Replace("→", "->"),
                    insert.X - panelHalfW * 1.5, insert.Y + rowOffsetY,
                    textH * 1.2, LText, AciText));

                double slotX = 0.0;
                foreach (var st in stations)
                {
                    double refZ   = st.InvertZ;
                    double excavX = slotX + excavLocalX;
                    double fillX  = slotX + fillLocalX;

                    // LEFT panel: excavation (the layer where the bug shows).
                    AddRegion(plines, st.Excav, insert, excavX, rowOffsetY, refZ, LExcav, AciExcav);

                    // RIGHT panel: fill layers + pipe body.
                    AddRegion(plines, st.Bed,  insert, fillX, rowOffsetY, refZ, LBed,  AciBed);
                    AddRegion(plines, st.Sur,  insert, fillX, rowOffsetY, refZ, LSur,  AciSur);
                    AddRegion(plines, st.Back, insert, fillX, rowOffsetY, refZ, LBack, AciBack);
                    if (st.Pipe != null && st.Pipe.Count >= 3)
                        AddRegion(plines, new List<List<double[]>> { st.Pipe },
                            insert, fillX, rowOffsetY, refZ, LPipe, AciPipe);

                    // Chainage label.
                    double topZ = refZ + maxSecH;
                    if (st.Excav != null && st.Excav.Count > 0)
                        topZ = st.Excav.SelectMany(r => r).Max(v => v[1]);
                    texts.Add(($"{st.Dist:N1} m",
                        insert.X + slotX + labelLocalX,
                        insert.Y + rowOffsetY + (topZ - refZ) + textH * 0.05,
                        textH, LText, st.IsBoundary ? AciBoundary : AciText));

                    slotX += colStep;
                }

                rowOffsetY -= rowStep;
            }

            // Commit.
            int created = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                foreach (var (pl, layer, aci) in plines)
                {
                    pl.LayerId = EnsureLayer(db, tr, layer, aci);
                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    created++;
                }
                foreach (var (txt, x, y, h, layer, aci) in texts)
                {
                    var t = new DBText
                    {
                        TextString = txt,
                        Height     = Math.Max(0.01, h),
                        Position   = new Point3d(x, y, 0),
                        Normal     = Vector3d.ZAxis,
                        LayerId    = EnsureLayer(db, tr, layer, aci)
                    };
                    btr.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                    created++;
                }
                tr.Commit();
            }

            return created;
        }

        /// <summary>
        /// Draws one station: excavation on the left panel, fill layers + pipe body
        /// on the right panel — same two-panel layout as the multi-station command,
        /// straight from the DB region with no fallback.
        /// </summary>
        private static int DrawSingle(
            Database db, DbStation st, Point3d insert, string label, Editor ed)
        {
            // Panel metrics from this single station's excavation extents.
            double maxHalfW = 1.0, maxSecH = 2.0;
            if (st.Excav != null && st.Excav.Count > 0)
            {
                foreach (var ring in st.Excav)
                    foreach (var v in ring)
                        maxHalfW = Math.Max(maxHalfW, Math.Abs(v[0]));
                double lo = st.Excav.SelectMany(r => r).Min(v => v[1]);
                double hi = st.Excav.SelectMany(r => r).Max(v => v[1]);
                if (hi > lo) maxSecH = Math.Max(maxSecH, hi - lo);
            }

            double panelHalfW = maxHalfW;
            double subGap     = panelHalfW * 0.15;
            double excavX     = panelHalfW;
            double fillX      = panelHalfW * 3.0 + subGap;
            double labelX     = panelHalfW * 2.0 + subGap * 0.5;
            double textH      = maxSecH * 0.04;
            double refZ       = st.InvertZ;

            var plines = new List<(Polyline, string, short)>();
            AddRegion(plines, st.Excav, insert, excavX, 0, refZ, LExcav, AciExcav);
            AddRegion(plines, st.Bed,   insert, fillX,  0, refZ, LBed,   AciBed);
            AddRegion(plines, st.Sur,   insert, fillX,  0, refZ, LSur,   AciSur);
            AddRegion(plines, st.Back,  insert, fillX,  0, refZ, LBack,  AciBack);
            if (st.Pipe != null && st.Pipe.Count >= 3)
                AddRegion(plines, new List<List<double[]>> { st.Pipe },
                    insert, fillX, 0, refZ, LPipe, AciPipe);

            double topZ = (st.Excav != null && st.Excav.Count > 0)
                ? st.Excav.SelectMany(r => r).Max(v => v[1])
                : refZ + maxSecH;

            int created = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                foreach (var (pl, layer, aci) in plines)
                {
                    pl.LayerId = EnsureLayer(db, tr, layer, aci);
                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    created++;
                }

                // Header label + chainage.
                created += AddText(tr, btr, db, label.Replace("→", "->"),
                    insert.X - panelHalfW * 1.5, insert.Y, textH * 1.2,
                    LText, AciText);
                created += AddText(tr, btr, db, $"{st.Dist:N1} m",
                    insert.X + labelX, insert.Y + (topZ - refZ) + textH * 0.05, textH,
                    LText, st.IsBoundary ? AciBoundary : AciText);

                tr.Commit();
            }

            ed?.WriteMessage($"\n  KazıNet={ClipperArea(st.Excav):N4}  Verts={CountVerts(st.Excav)}");
            return created;
        }

        private static int AddText(
            Transaction tr, BlockTableRecord btr, Database db,
            string txt, double x, double y, double h, string layer, short aci)
        {
            var t = new DBText
            {
                TextString = txt,
                Height     = Math.Max(0.01, h),
                Position   = new Point3d(x, y, 0),
                Normal     = Vector3d.ZAxis,
                LayerId    = EnsureLayer(db, tr, layer, aci)
            };
            btr.AppendEntity(t);
            tr.AddNewlyCreatedDBObject(t, true);
            return 1;
        }

        private static int CountVerts(List<List<double[]>> region)
            => region == null ? 0 : region.Sum(r => r?.Count ?? 0);

        /// <summary>Shoelace area of a region (sum over rings). Diagnostic only.</summary>
        private static double ClipperArea(List<List<double[]>> region)
        {
            if (region == null) return 0;
            double total = 0;
            foreach (var ring in region)
            {
                if (ring == null || ring.Count < 3) continue;
                double s = 0;
                int n = ring.Count;
                for (int i = 0; i < n; i++)
                {
                    var a = ring[i]; var b = ring[(i + 1) % n];
                    s += a[0] * b[1] - b[0] * a[1];
                }
                total += Math.Abs(s * 0.5);
            }
            return total;
        }

        /// <summary>Same sampling as URBANO_SECTIONS: step by interval, force boundaries.</summary>
        private static List<DbStation> SelectStations(List<DbStation> stations, double interval)
        {
            var sel = new List<DbStation>();
            if (stations == null || stations.Count == 0) return sel;

            var boundaryDists = new SortedSet<double>(
                stations.Where(s => s.IsBoundary).Select(s => s.Dist));

            int iStart = 0;
            while (true)
            {
                sel.Add(stations[iStart]);
                if (iStart >= stations.Count - 1) break;

                double cur    = stations[iStart].Dist;
                double target = cur + interval;
                var view = boundaryDists.GetViewBetween(cur + 1e-6, target - 1e-6);
                if (view.Count > 0) target = view.Min;

                int j = stations.Count - 1;
                for (int k = iStart + 1; k < stations.Count; k++)
                    if (stations[k].Dist >= target - 1e-6) { j = k; break; }
                iStart = j;
            }
            return sel;
        }

        private static void AddRegion(
            List<(Polyline, string, short)> list, List<List<double[]>> region,
            Point3d origin, double colX, double rowY, double refZ, string layer, short aci)
        {
            if (region == null) return;
            foreach (var ring in region)
            {
                if (ring == null || ring.Count < 3) continue;
                var pl = new Polyline { Closed = true };
                int vi = 0;
                foreach (var v in ring)
                {
                    if (v == null || v.Length < 2) continue;
                    pl.AddVertexAt(vi++,
                        new Point2d(origin.X + colX + v[0],
                                    origin.Y + rowY + (v[1] - refZ)),
                        0, 0, 0);
                }
                if (vi < 3) { pl.Dispose(); continue; }
                list.Add((pl, layer, aci));
            }
        }

        // ── tiny typed-value helpers ──────────────────────────────────────────
        private static double ReadReal(TypedValue[] tv, ref int i)
        {
            if (i >= tv.Length) return 0;
            var val = tv[i++].Value;
            return val == null ? 0 : Convert.ToDouble(val);
        }

        private static int ReadI32(TypedValue[] tv, ref int i)
        {
            if (i >= tv.Length) return 0;
            var val = tv[i++].Value;
            return val == null ? 0 : Convert.ToInt32(val);
        }

        private static short ReadI16(TypedValue[] tv, ref int i)
        {
            if (i >= tv.Length) return 0;
            var val = tv[i++].Value;
            return val == null ? (short)0 : Convert.ToInt16(val);
        }

        private static TypedValue[] ReadXrec(Transaction tr, DBDictionary parent, string key)
        {
            if (parent == null || !parent.Contains(key)) return null;
            var rec = tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as Xrecord;
            return rec?.Data?.AsArray();
        }

        private static DBDictionary GetSub(Transaction tr, DBDictionary parent, string key)
        {
            if (parent == null || !parent.Contains(key)) return null;
            return tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as DBDictionary;
        }

        private static ObjectId EnsureLayer(Database db, Transaction tr, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
            };
            var id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }

        // ── Dump helpers ─────────────────────────────────────────────────────

        /// <summary>
        /// Same navigation as ReadOneStation but also returns the raw STATION_INFO
        /// TypedValue array so the dump can show every scalar before parsing.
        /// </summary>
        private static DbStation ReadOneStationWithRaw(
            Database db, Editor ed,
            string netKey, string pipeKey, string staKey,
            out TypedValue[] rawInfo)
        {
            rawInfo = null;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_KEY)) { tr.Commit(); return null; }

                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);

                DBDictionary netParent = root;
                if (root.Contains(K_NETWORKS))
                    netParent = (DBDictionary)tr.GetObject(root.GetAt(K_NETWORKS), OpenMode.ForRead);

                var netDict = FindChild(tr, netParent, netKey, new[] { K_META, K_NET_META });
                if (netDict == null)
                {
                    DumpKeys(ed, netParent, "Ağlar", new[] { K_META, K_NET_META });
                    tr.Commit(); return null;
                }
                var pipeDict = FindChild(tr, netDict, pipeKey, null);
                if (pipeDict == null)
                {
                    DumpKeys(ed, netDict, "Borular", new[] { K_NET_META, K_MH_STACKS });
                    tr.Commit(); return null;
                }
                var staDict = FindChild(tr, pipeDict, staKey, new[] { K_PIPE_META });
                if (staDict == null)
                {
                    DumpKeys(ed, pipeDict, "İstasyonlar", new[] { K_PIPE_META });
                    tr.Commit(); return null;
                }

                rawInfo = ReadXrec(tr, staDict, K_STATION);
                var st  = ReadStation(tr, staDict);
                tr.Commit();
                return st;
            }
        }

        private static string BuildDump(TypedValue[] rawInfo, DbStation st, string label)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"URBANO_SECTION_DUMP — {label}");
            sb.AppendLine($"Generated : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine(new string('─', 60));

            sb.AppendLine("\n── STATION_INFO (raw) ──");
            string[] fieldNames = {
                "StationDist", "WorldX", "WorldY", "TerrainZ", "InvertZ",
                "[5]", "[6]", "[7]", "[8]", "[9]", "[10]", "[11]", "[12]"
            };
            if (rawInfo != null)
            {
                int i = 0;
                for (int k = 0; k < 13 && i < rawInfo.Length; k++)
                    sb.AppendLine($"  {fieldNames[k],-15} = {Convert.ToDouble(rawInfo[i++].Value):G10}");
                if (i < rawInfo.Length)
                    sb.AppendLine($"  {"HasOverlap",-15} = {Convert.ToInt16(rawInfo[i++].Value)}");
                if (i < rawInfo.Length)
                    sb.AppendLine($"  {"IsBoundary",-15} = {Convert.ToInt16(rawInfo[i++].Value)}");
            }
            else
            {
                sb.AppendLine("  (STATION_INFO Xrecord missing)");
            }
            sb.AppendLine($"  [parsed]  Dist={st.Dist:G6}  InvertZ={st.InvertZ:G6}  IsBoundary={st.IsBoundary}");

            AppendRegionDump(sb, "EXCAVATION_KAZI",  st.Excav);
            AppendRegionDump(sb, "YATAKLAMA",        st.Bed);
            AppendRegionDump(sb, "GOMLEKLEME",       st.Sur);
            AppendRegionDump(sb, "GERI_DOLGU",       st.Back);
            if (st.Pipe != null)
                AppendRegionDump(sb, "PIPE_BODY", new List<List<double[]>> { st.Pipe });

            return sb.ToString();
        }

        private static void AppendRegionDump(
            StringBuilder sb, string name, List<List<double[]>> region)
        {
            int ringCount = region?.Count ?? 0;
            sb.AppendLine($"\n── {name}  ({ringCount} ring(s)) ──");
            if (ringCount == 0) { sb.AppendLine("  (empty)"); return; }

            for (int r = 0; r < region.Count; r++)
            {
                var ring = region[r];
                if (ring == null || ring.Count == 0) { sb.AppendLine($"  Ring {r}: (null)"); continue; }

                double minU = ring.Min(v => v[0]), maxU = ring.Max(v => v[0]);
                double minZ = ring.Min(v => v[1]), maxZ = ring.Max(v => v[1]);
                double area = ClipperArea(new List<List<double[]>> { ring });

                sb.AppendLine($"  Ring {r}:  {ring.Count} verts" +
                              $"  U[{minU:F4} … {maxU:F4}]" +
                              $"  Z[{minZ:F4} … {maxZ:F4}]" +
                              $"  area={area:F6} m²");

                for (int v = 0; v < ring.Count; v++)
                    sb.AppendLine($"    [{v,3}]  U={ring[v][0],12:F6}  Z={ring[v][1],14:F6}");
            }
        }

        private static string BuildSummaryLine(DbStation st)
        {
            string RS(List<List<double[]>> r) =>
                r == null || r.Count == 0 ? "–"
                : $"{r.Count}r/{r.Sum(x => x?.Count ?? 0)}v";

            return $"  Dist={st.Dist:N2}  InvertZ={st.InvertZ:N4}  IsBoundary={st.IsBoundary}" +
                   $"  Excav={RS(st.Excav)}  Bed={RS(st.Bed)}" +
                   $"  Sur={RS(st.Sur)}  Back={RS(st.Back)}" +
                   $"  Pipe={(st.Pipe?.Count ?? 0)}v";
        }

        private static string SanitizeFileName(string s) =>
            new string(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
    }
}
