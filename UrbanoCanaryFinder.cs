// ============================================================================
// UrbanoCanaryFinder.cs
//
// Command : URBANO_FIND
//
// Targeted canary-value search scoped to:
//   §1  Model Space — Lines, Polylines, BlockReferences
//         • XData (all registered app names)
//         • Extension Dictionaries / XRecords (recursive)
//   §2  NOD — ONLY top-level keys whose names begin with:
//         ARS_  |  URBANO_  |  CG_
//         All standard AutoCAD dictionaries are hard-skipped.
//
// Output examples:
//   [Found KRG]     -> Entity Handle:2A4 -> XData[ARS_PIPE_DATA] tv#003 code=1
//   [Found 535.555] -> NOD -> ARS_NETWORK -> XRecord["PIPE_DATA"] tv#007 code=40
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoCanaryFinder))]

namespace UrbanoMetraj
{
    public class UrbanoCanaryFinder
    {
        // ── Search terms (case-insensitive substring match) ──────────────────
        private static readonly string SearchString  = "KRG";
        private static readonly double SearchDouble  = 535.555;
        private const  double          DoubleTolerance = 1e-3;

        // ── Urbano NOD prefixes — the ONLY top-level NOD keys we enter ───────
        private static readonly string[] UrbanoPrefixes =
            { "ARS_", "URBANO_", "CG_" };

        // ── Standard AutoCAD keys we explicitly skip for performance ─────────
        private static readonly HashSet<string> SkipNodKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ACAD_COLOR",          "ACAD_GROUP",         "ACAD_LAYOUT",
                "ACAD_MATERIAL",       "ACAD_MLEADERSTYLE",  "ACAD_MLINESTYLE",
                "ACAD_PLOTSETTINGS",   "ACAD_PLOTSTYLENAME", "ACAD_SCALELIST",
                "ACAD_TABLESTYLE",     "ACAD_VISUALSTYLE",   "AcDbVariableDictionary",
                "FBXASSET",            "DATALINK",           "ACAD_DETAILVIEWSTYLE",
                "ACAD_SECTIONVIEWSTYLE","ACAD_FIELD",        "ACAD_PDFDEFINITIONS",
                "ACAD_DWFOBJECT",      "ACAD_DGNOBJDPN",     "ACAD_PDFOBJDPN",
                "ACAD_POINTCLOUDEXOBJECT",
            };

        private const int MaxRecursionDepth = 12;

        // ── Per-run state ────────────────────────────────────────────────────
        private Editor       _ed;
        private StreamWriter _log;
        private int          _hits;
        private int          _scanned;
        private readonly HashSet<long> _visited = new HashSet<long>();

        // ====================================================================
        // COMMAND ENTRY POINT
        // ====================================================================

        [CommandMethod("URBANO_FIND")]
        public void Run()
        {
            _visited.Clear();
            _hits    = 0;
            _scanned = 0;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"UrbanoCanaryFinder_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            _ed.WriteMessage($"\n{'═',72}");
            _ed.WriteMessage($"\nURBANO_FIND — searching for \"{SearchString}\" and {SearchDouble}");
            _ed.WriteMessage($"\nLog: {logPath}");
            _ed.WriteMessage($"\n{'═',72}\n");

            using (_log = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            {
                _log.WriteLine($"URBANO_FIND  DWG: {db.Filename}  Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _log.WriteLine($"Search: \"{SearchString}\"  and  {SearchDouble} (±{DoubleTolerance})");
                _log.WriteLine(new string('═', 80));

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        ScanModelSpace(tr, db);
                        ScanNod(tr, db);
                    }
                    catch (System.Exception ex)
                    {
                        Report($"[FATAL] {ex.GetType().Name}: {ex.Message}");
                    }
                    finally { tr.Commit(); }
                }

                _log.WriteLine(new string('═', 80));
                _log.WriteLine($"Scanned: {_scanned}  Hits: {_hits}");
            }

            _ed.WriteMessage($"\n{'═',72}");
            _ed.WriteMessage($"\nScanned {_scanned} objects. Found {_hits} match(es).");
            _ed.WriteMessage($"\nLog saved → {logPath}\n");
        }

        // ====================================================================
        // §1  MODEL SPACE — Lines, Polylines, BlockReferences
        // ====================================================================

        private void ScanModelSpace(Transaction tr, Database db)
        {
            _log.WriteLine("\n── §1  Model Space Entities ──────────────────────────────────────────");

            BlockTableRecord ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                                    as BlockTableRecord;
            if (ms == null) { _log.WriteLine("  [Model Space unavailable]"); return; }

            foreach (ObjectId id in ms)
            {
                DBObject obj;
                try { obj = tr.GetObject(id, OpenMode.ForRead); }
                catch { continue; }

                // Type filter — only the requested entity types
                if (!IsTargetEntity(obj)) continue;

                _scanned++;
                Entity ent    = (Entity)obj;
                string handle = ent.Handle.ToString();
                string dxf    = ent.GetRXClass()?.DxfName ?? obj.GetType().Name;
                string stem   = $"Entity Handle:{handle} <{dxf}>";

                // ── XData ─────────────────────────────────────────────────────
                ScanXData(ent, stem);

                // ── Extension Dictionary → XRecords ───────────────────────────
                if (!ent.ExtensionDictionary.IsNull)
                    ScanDictById(tr, ent.ExtensionDictionary, stem + " -> ExtDict", 0);
            }
        }

        private static bool IsTargetEntity(DBObject obj)
            => obj is Line
            || obj is Polyline
            || obj is Polyline2d
            || obj is Polyline3d
            || obj is BlockReference;

        // ====================================================================
        // §2  NOD — Urbano prefixes only
        // ====================================================================

        private void ScanNod(Transaction tr, Database db)
        {
            _log.WriteLine("\n── §2  NOD (ARS_ / URBANO_ / CG_ only) ──────────────────────────────");

            DBDictionary nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead)
                                  as DBDictionary;
            if (nod == null) { _log.WriteLine("  [NOD unavailable]"); return; }

            int entered = 0, skipped = 0;

            foreach (DBDictionaryEntry entry in nod)
            {
                // Hard-skip standard AutoCAD dicts
                if (SkipNodKeys.Contains(entry.Key)  ||
                    !HasUrbanoPrefix(entry.Key)       ||
                    entry.Value.IsNull                ||
                    MarkVisited(entry.Value))
                {
                    skipped++;
                    continue;
                }

                entered++;
                _log.WriteLine($"  Entering NOD[\"{entry.Key}\"]");

                DBObject child;
                try { child = tr.GetObject(entry.Value, OpenMode.ForRead); }
                catch (System.Exception ex)
                {
                    _log.WriteLine($"    [open error: {ex.Message}]");
                    continue;
                }

                _scanned++;
                string nodPath = $"NOD -> \"{entry.Key}\"";

                if      (child is DBDictionary dict) ScanDict(tr, dict, nodPath, 0);
                else if (child is Xrecord xrec)      ScanXRecord(xrec, nodPath + " -> XRecord[root]");
                else                                  ScanXData(child, nodPath);
            }

            _log.WriteLine($"  NOD: entered {entered}, skipped {skipped}");
        }

        private static bool HasUrbanoPrefix(string key)
        {
            foreach (string p in UrbanoPrefixes)
                if (key.StartsWith(p, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ====================================================================
        // Recursive Dictionary walker
        // ====================================================================

        private void ScanDictById(Transaction tr, ObjectId dictId,
                                   string path, int depth)
        {
            if (dictId.IsNull || MarkVisited(dictId)) return;
            try
            {
                DBDictionary d = tr.GetObject(dictId, OpenMode.ForRead) as DBDictionary;
                ScanDict(tr, d, path, depth);
            }
            catch (System.Exception ex) { _log.WriteLine($"{path} [dict open error: {ex.Message}]"); }
        }

        private void ScanDict(Transaction tr, DBDictionary dict,
                               string path, int depth)
        {
            if (dict == null || depth > MaxRecursionDepth) return;
            _scanned++;

            foreach (DBDictionaryEntry entry in dict)
            {
                string childPath = $"{path} -> \"{entry.Key}\"";

                // The key name itself might contain a string canary
                CheckString(entry.Key, childPath, "dict-key");

                if (entry.Value.IsNull || MarkVisited(entry.Value)) continue;

                DBObject child;
                try { child = tr.GetObject(entry.Value, OpenMode.ForRead); }
                catch (System.Exception ex)
                {
                    _log.WriteLine($"{childPath} [open error: {ex.Message}]");
                    continue;
                }

                _scanned++;

                if      (child is DBDictionary sub)  ScanDict(tr, sub, childPath, depth + 1);
                else if (child is Xrecord xrec)      ScanXRecord(xrec, childPath + " -> XRecord");
                else
                {
                    // Unknown object — at minimum scan its XData and ExtDict
                    ScanXData(child, childPath);
                    if (!child.ExtensionDictionary.IsNull)
                        ScanDictById(tr, child.ExtensionDictionary,
                                     childPath + " -> ExtDict", depth + 1);
                }
            }
        }

        // ====================================================================
        // XRecord scanner
        // ====================================================================

        private void ScanXRecord(Xrecord xrec, string path)
        {
            if (xrec?.Data == null) return;
            _scanned++;

            var binChunks = new List<byte[]>();
            int idx       = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                string tvPath = $"{path} tv#{idx:D3} code={tv.TypeCode}";

                if (tv.Value is byte[] bytes)
                {
                    binChunks.Add(bytes);
                    SearchBinary(bytes, tvPath);
                }
                else if (tv.Value != null)
                {
                    string s = tv.Value.ToString();
                    CheckString(s, tvPath, "xrecord-tv");
                    CheckNumeric(tv, tvPath);
                }

                idx++;
            }

            // GZip payload is often split across multiple 127-byte chunks;
            // try the reassembled blob too.
            if (binChunks.Count > 1)
                SearchBinary(Merge(binChunks),
                             $"{path} [consolidated {binChunks.Count} chunks]");
        }

        // ====================================================================
        // XData scanner
        // ====================================================================

        private void ScanXData(DBObject obj, string stem)
        {
            using (ResultBuffer rb = obj.XData)
            {
                if (rb == null) return;

                string       curApp    = "?";
                int          idx       = 0;
                var          binChunks = new List<byte[]>();

                foreach (TypedValue tv in rb)
                {
                    int code = tv.TypeCode;

                    if (code == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        FlushBinChunks(binChunks, $"{stem} -> XData[{curApp}] consolidated");
                        binChunks.Clear();
                        curApp = tv.Value?.ToString() ?? "?";
                        idx    = 0;
                        continue;
                    }

                    string tvPath = $"{stem} -> XData[{curApp}] tv#{idx:D3} code={code}";

                    if (tv.Value is byte[] bytes)
                    {
                        binChunks.Add(bytes);
                        SearchBinary(bytes, tvPath);
                    }
                    else if (tv.Value != null)
                    {
                        string s = tv.Value.ToString();
                        CheckString(s, tvPath, "xdata-tv");
                        CheckNumeric(tv, tvPath);
                    }

                    idx++;
                }

                FlushBinChunks(binChunks, $"{stem} -> XData[{curApp}] consolidated");
            }
        }

        private void FlushBinChunks(List<byte[]> chunks, string path)
        {
            if (chunks.Count > 1)
                SearchBinary(Merge(chunks), path);
        }

        // ====================================================================
        // Numeric TypedValue check
        // ====================================================================

        private void CheckNumeric(TypedValue tv, string path)
        {
            double d;
            try
            {
                if      (tv.Value is double dv) d = dv;
                else if (tv.Value is float  fv) d = fv;
                else return;
            }
            catch { return; }

            if (Math.Abs(d - SearchDouble) <= DoubleTolerance)
                Hit($"[Found {SearchDouble}] -> {path}  value={d:G10}", path);
        }

        // ====================================================================
        // String check
        // ====================================================================

        private void CheckString(string value, string path, string context)
        {
            if (string.IsNullOrEmpty(value)) return;
            if (value.IndexOf(SearchString, StringComparison.OrdinalIgnoreCase) >= 0)
                Hit($"[Found {SearchString}] -> {path}  value=\"{Truncate(value, 120)}\"", path);
        }

        // ====================================================================
        // Binary decode: GZip → Deflate → UTF-8 → UTF-16 LE
        // ====================================================================

        private void SearchBinary(byte[] bytes, string path)
        {
            if (bytes == null || bytes.Length == 0) return;

            // GZip: magic bytes 0x1F 0x8B
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                string gz = TryGZip(bytes);
                if (gz != null) { CheckString(gz, path + " [GZip]", "gzip"); return; }
            }

            // Raw Deflate
            string deflated = TryDeflate(bytes);
            if (deflated != null) { CheckString(deflated, path + " [Deflate]", "deflate"); return; }

            // UTF-8 (lenient — best effort for mixed binary)
            try
            {
                string s = Encoding.UTF8.GetString(bytes);
                CheckString(s, path + " [UTF-8]", "utf8");
            }
            catch { }

            // UTF-16 LE (heuristic: even length, null bytes at odd positions)
            if (bytes.Length >= 4 && bytes.Length % 2 == 0 && bytes[1] == 0)
            {
                try
                {
                    string s = Encoding.Unicode.GetString(bytes);
                    CheckString(s, path + " [UTF-16LE]", "utf16");
                }
                catch { }
            }
        }

        // ====================================================================
        // Hit reporter
        // ====================================================================

        private void Hit(string line, string path)
        {
            _hits++;
            string bar = new string('─', 80);
            _ed.WriteMessage($"\n{bar}\n{line}\n{bar}");
            _log.WriteLine(bar);
            _log.WriteLine(line);
            _log.WriteLine(bar);
            _log.WriteLine();
        }

        private void Report(string s)
        {
            _ed.WriteMessage("\n" + s);
            _log?.WriteLine(s);
        }

        // ====================================================================
        // Visited-object guard (prevents infinite loops in circular dicts)
        // ====================================================================

        // Returns false the first time an id is seen (marks it); true thereafter.
        private bool MarkVisited(ObjectId id)
        {
            if (id.IsNull) return true;
            // Use the database handle (stable numeric id) as the de-dup key.
            long key = (long)id.Handle.Value;
            if (_visited.Contains(key)) return true;
            _visited.Add(key);
            return false;
        }

        // ====================================================================
        // Helpers
        // ====================================================================

        private static string TryGZip(byte[] b)
        {
            try
            {
                using (var ms = new MemoryStream(b))
                using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                using (var o  = new MemoryStream())
                {
                    gz.CopyTo(o);
                    return Encoding.UTF8.GetString(o.ToArray());
                }
            }
            catch { return null; }
        }

        private static string TryDeflate(byte[] b)
        {
            try
            {
                using (var ms = new MemoryStream(b))
                using (var df = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o  = new MemoryStream())
                {
                    df.CopyTo(o);
                    byte[] dec = o.ToArray();
                    // Only accept if decompression actually expanded the data
                    return dec.Length > b.Length ? Encoding.UTF8.GetString(dec) : null;
                }
            }
            catch { return null; }
        }

        private static byte[] Merge(List<byte[]> chunks)
        {
            int total = 0;
            foreach (var c in chunks) total += c.Length;
            byte[] buf = new byte[total];
            int    pos = 0;
            foreach (var c in chunks) { Buffer.BlockCopy(c, 0, buf, pos, c.Length); pos += c.Length; }
            return buf;
        }

        private static string Truncate(string s, int max)
            => s.Length <= max ? s : s.Substring(0, max) + "…";
    }
}
