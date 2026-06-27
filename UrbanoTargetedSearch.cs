// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoTargetedSearch.cs
//
// Command : UrbanoTargetedSearch
//
// A focused canary-value locator that avoids the performance sink-holes
// (ACAD_MATERIAL, FBXASSET, AcDbVariableDictionary, …) that make a full DWG
// scan impractical.
//
// SCAN SCOPE
// ──────────
//  §1  Model Space entities  — only AcDbLine, AcDb2dPolyline, AcDb3dPolyline,
//                              AcDbPolyline, AcDbBlockReference
//                              → XData (all RegApps)
//                              → Extension Dictionary (all XRecords, recursive)
//
//  §2  NOD                   — only entries whose names START WITH one of the
//                              Urbano-specific prefixes:
//                                  ARS_   URBANO_   CG_
//                              All other top-level NOD keys are silently skipped.
//
// SEARCH LOGIC (per string/binary value)
// ──────────────────────────────────────
//  • String TypedValues   → direct case-insensitive substring match
//  • Binary TypedValues   → decode as UTF-8 (lenient), then UTF-16 LE,
//                           then try GZip decompression first if magic 1F 8B
//                           → match on decoded text
//
// OUTPUT FORMAT (matches the expected format in the spec)
// ──────────────────────────────────────────────────────
//  [Found KRG]     Entity Handle:2A4 → XData[ARS_PIPE_DATA] tv#3 code=1
//  [Found 535.555] NOD → ARS_NETWORK → XRecord["PIPE_DATA"] tv#7 code=1
//  … plus a snippet of surrounding text so the schema is visible.
//
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoTargetedSearch))]

namespace UrbanoMetraj
{
    public class UrbanoTargetedSearch
    {
        // ── Urbano NOD prefixes — ONLY dictionaries starting with these are entered
        private static readonly string[] UrbanoPrefixes =
        {
            "ARS_", "URBANO_", "CG_"
        };

        // ── Canary values (case-insensitive substring match)
        private static readonly string[] DefaultTerms = { "535.555", "KRG" };

        // ── AutoCAD built-in NOD keys we never enter (performance guard)
        private static readonly HashSet<string> SkippedNodKeys =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ACAD_COLOR",       "ACAD_GROUP",       "ACAD_LAYOUT",
                "ACAD_MATERIAL",    "ACAD_MLEADERSTYLE","ACAD_MLINESTYLE",
                "ACAD_PLOTSETTINGS","ACAD_PLOTSTYLENAME","ACAD_SCALELIST",
                "ACAD_TABLESTYLE",  "ACAD_VISUALSTYLE", "AcDbVariableDictionary",
                "FBXASSET",         "DATALINK",         "ACAD_DETAILVIEWSTYLE",
                "ACAD_SECTIONVIEWSTYLE", "ACAD_FIELD",  "ACAD_PDFDEFINITIONS",
                "ACAD_DWFOBJECT",   "ACAD_DGNOBJDPN",   "ACAD_PDFOBJDPN",
                "ACAD_POINTCLOUDEXOBJECT",
            };

        // ── Recursion / output limits
        private const int MaxDictDepth  = 15;
        private const int SnippetRadius = 100;   // chars either side of hit

        // ── Per-run state
        private string[]     _terms;
        private string[]     _termsLo;
        private Editor       _ed;
        private StreamWriter _log;
        private int          _hits;
        private int          _scanned;
        private readonly HashSet<long> _visited = new HashSet<long>();

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("UrbanoTargetedSearch")]
        public void Run()
        {
            _visited.Clear();
            _hits    = 0;
            _scanned = 0;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            // ── Terms prompt ──────────────────────────────────────────────────
            var pso = new PromptStringOptions(
                $"\nSearch terms (comma-separated) [{string.Join(",", DefaultTerms)}]: ")
            {
                AllowSpaces  = true,
                DefaultValue = string.Join(",", DefaultTerms)
            };
            PromptResult pr = _ed.GetString(pso);
            if (pr.Status != PromptStatus.OK) { _ed.WriteMessage("\nCancelled.\n"); return; }

            string raw = pr.StringResult.Trim();
            if (string.IsNullOrEmpty(raw)) raw = string.Join(",", DefaultTerms);

            string[] parts = raw.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            _terms   = new string[parts.Length];
            _termsLo = new string[parts.Length];
            for (int i = 0; i < parts.Length; i++)
            {
                _terms[i]   = parts[i].Trim();
                _termsLo[i] = _terms[i].ToLowerInvariant();
            }

            // ── Log file ──────────────────────────────────────────────────────
            string logPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"UrbanoTargetedSearch_{DateTime.Now:yyyyMMdd_HHmmss}.txt");

            Print($"\nTerms  : {string.Join(" | ", _terms)}");
            Print($"Scope  : Model Space (Line/Polyline/BlockRef) + NOD[ARS_/URBANO_/CG_]");
            Print($"Log    : {logPath}");
            Print(Ruler());

            using (_log = new StreamWriter(logPath, false, new UTF8Encoding(true)))
            {
                _log.WriteLine("UrbanoTargetedSearch");
                _log.WriteLine($"DWG  : {db.Filename}");
                _log.WriteLine($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                _log.WriteLine($"Terms: {string.Join(" | ", _terms)}");
                _log.WriteLine(Ruler());

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        ScanEntities(tr, db);
                        ScanNod(tr, db);
                    }
                    catch (System.Exception ex)
                    {
                        Print($"[FATAL] {ex.GetType().Name}: {ex.Message}");
                        _log.WriteLine(ex.StackTrace);
                    }
                    finally { tr.Commit(); }
                }

                _log.WriteLine(Ruler());
                _log.WriteLine($"Scanned : {_scanned} objects");
                _log.WriteLine($"Matches : {_hits}");
            }

            Print(Ruler());
            Print($"Scanned {_scanned} objects — {_hits} match(es) found.");
            if (_hits == 0)
                PrintNoMatchAdvice();
            else
                Print($"Full log saved → {logPath}");
        }

        // ═════════════════════════════════════════════════════════════════════
        // §1  MODEL SPACE ENTITIES
        // ═════════════════════════════════════════════════════════════════════

        private void ScanEntities(Transaction tr, Database db)
        {
            Section("§1  Model Space — Line / Polyline / BlockReference");

            BlockTableRecord ms;
            try
            {
                ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                       as BlockTableRecord;
            }
            catch (System.Exception ex)
            {
                Print($"  [Cannot open Model Space: {ex.Message}]");
                return;
            }

            int entityCount = 0;
            int eligibleCount = 0;

            foreach (ObjectId entId in ms)
            {
                entityCount++;

                // ── Type filter ───────────────────────────────────────────────
                // We open ForRead only once; if it's not an eligible type we
                // still need to dispose the object, hence the using block.
                DBObject rawObj;
                try { rawObj = tr.GetObject(entId, OpenMode.ForRead); }
                catch { continue; }

                if (!IsEligibleEntity(rawObj))
                    continue;

                eligibleCount++;
                _scanned++;

                Entity ent  = (Entity)rawObj;
                string stem = $"Entity Handle:{ent.Handle} <{ent.GetRXClass()?.DxfName ?? "?"}>";

                // ── XData ─────────────────────────────────────────────────────
                ScanXData(ent, stem);

                // ── Extension Dictionary ──────────────────────────────────────
                if (!ent.ExtensionDictionary.IsNull)
                    ScanExtDict(tr, ent.ExtensionDictionary, stem + " → ExtDict", 0);
            }

            _log.WriteLine($"  Entities in space: {entityCount}  Eligible: {eligibleCount}");
        }

        private static bool IsEligibleEntity(DBObject obj)
            => obj is Line
            || obj is Polyline
            || obj is Polyline2d
            || obj is Polyline3d
            || obj is BlockReference;

        // ═════════════════════════════════════════════════════════════════════
        // §2  NOD — Urbano-prefix filter
        // ═════════════════════════════════════════════════════════════════════

        private void ScanNod(Transaction tr, Database db)
        {
            Section("§2  NOD — ARS_ / URBANO_ / CG_ prefixes only");

            DBDictionary nod;
            try
            {
                nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead)
                        as DBDictionary;
            }
            catch (System.Exception ex)
            {
                Print($"  [Cannot open NOD: {ex.Message}]");
                return;
            }

            int entered  = 0;
            int skipped  = 0;

            foreach (DBDictionaryEntry entry in nod)
            {
                if (SkippedNodKeys.Contains(entry.Key))      { skipped++; continue; }
                if (!HasUrbanoPrefix(entry.Key))              { skipped++; continue; }
                if (entry.Value.IsNull || Visit(entry.Value)) { skipped++; continue; }

                entered++;
                _log.WriteLine($"  Entering NOD[\"{entry.Key}\"]");

                DBObject child;
                try { child = tr.GetObject(entry.Value, OpenMode.ForRead); }
                catch (System.Exception ex)
                {
                    _log.WriteLine($"    [open error: {ex.Message}]");
                    continue;
                }

                string nodPath = $"NOD → \"{entry.Key}\"";

                switch (child)
                {
                    case DBDictionary subDict:
                        ScanDict(tr, subDict, nodPath, 0);
                        break;
                    case Xrecord xrec:
                        ScanXRecord(xrec, nodPath + " → XRecord[root]", 0);
                        break;
                    default:
                        ScanGeneric(tr, child, nodPath, 0);
                        break;
                }
            }

            _log.WriteLine($"  NOD: entered {entered}, skipped {skipped}");
        }

        private static bool HasUrbanoPrefix(string key)
        {
            foreach (string pfx in UrbanoPrefixes)
                if (key.StartsWith(pfx, StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Recursive dictionary scanner
        // ═════════════════════════════════════════════════════════════════════

        private void ScanDict(Transaction tr, DBDictionary dict,
                               string path, int depth)
        {
            if (dict == null || depth > MaxDictDepth) return;
            _scanned++;

            foreach (DBDictionaryEntry entry in dict)
            {
                string childPath = $"{path} → \"{entry.Key}\"";

                // Check the key name itself
                Match(entry.Key, childPath, "dict-key", entry.Key);

                if (entry.Value.IsNull || Visit(entry.Value)) continue;

                DBObject child;
                try { child = tr.GetObject(entry.Value, OpenMode.ForRead); }
                catch (System.Exception ex)
                {
                    _log.WriteLine($"{childPath} [open error: {ex.Message}]");
                    continue;
                }

                _scanned++;

                switch (child)
                {
                    case DBDictionary sub:
                        ScanDict(tr, sub, childPath, depth + 1);
                        break;
                    case Xrecord xrec:
                        ScanXRecord(xrec, childPath + " → XRecord", depth);
                        break;
                    default:
                        ScanGeneric(tr, child, childPath, depth);
                        break;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // XRecord scanner — the workhorse
        // ═════════════════════════════════════════════════════════════════════

        private void ScanXRecord(Xrecord xrec, string path, int depth)
        {
            if (xrec?.Data == null) return;
            _scanned++;

            var    binChunks = new List<byte[]>();
            int    idx       = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                int    code   = tv.TypeCode;
                string tvPath = $"{path} tv#{idx:D3} code={code}";

                if (tv.Value is byte[] bytes)
                {
                    binChunks.Add(bytes);
                    SearchBytes(bytes, tvPath);
                }
                else if (tv.Value != null)
                {
                    string s = tv.Value.ToString();
                    Match(s, tvPath, "xrecord-string", s);
                }

                idx++;
            }

            // Consolidated chunk search (GZip split across 127-byte chunks)
            if (binChunks.Count > 1)
            {
                byte[] all = Merge(binChunks);
                SearchBytes(all, $"{path} [consolidated {all.Length}B×{binChunks.Count}chunks]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // XData scanner
        // ═════════════════════════════════════════════════════════════════════

        private void ScanXData(DBObject obj, string stem)
        {
            using (ResultBuffer rb = obj.XData)
            {
                if (rb == null) return;

                string  curApp   = "?";
                int     idx      = 0;
                var     binChunks = new List<byte[]>();

                foreach (TypedValue tv in rb)
                {
                    int code = tv.TypeCode;

                    if (code == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        // Flush accumulated binary for previous app
                        if (binChunks.Count > 1)
                            SearchBytes(Merge(binChunks),
                                        $"{stem} → XData[{curApp}] consolidated");
                        binChunks.Clear();

                        curApp = tv.Value?.ToString() ?? "?";
                        idx    = 0;
                        continue;
                    }

                    string tvPath = $"{stem} → XData[{curApp}] tv#{idx:D3} code={code}";

                    if (tv.Value is byte[] bytes)
                    {
                        binChunks.Add(bytes);
                        SearchBytes(bytes, tvPath);
                    }
                    else if (tv.Value != null)
                    {
                        string s = tv.Value.ToString();
                        Match(s, tvPath, "xdata-string", s);
                    }

                    idx++;
                }

                // Final flush
                if (binChunks.Count > 1)
                    SearchBytes(Merge(binChunks),
                                $"{stem} → XData[{curApp}] consolidated");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Extension dictionary (entity-level)
        // ═════════════════════════════════════════════════════════════════════

        private void ScanExtDict(Transaction tr, ObjectId extDictId,
                                  string path, int depth)
        {
            if (extDictId.IsNull || Visit(extDictId)) return;

            try
            {
                DBDictionary d = tr.GetObject(extDictId, OpenMode.ForRead) as DBDictionary;
                ScanDict(tr, d, path, depth);
            }
            catch (System.Exception ex)
            {
                _log.WriteLine($"{path} [ExtDict open error: {ex.Message}]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Generic / proxy object fallback
        // ═════════════════════════════════════════════════════════════════════

        private void ScanGeneric(Transaction tr, DBObject obj,
                                  string path, int depth)
        {
            _scanned++;
            _log.WriteLine($"{path} <{obj.GetRXClass()?.Name ?? "?"}> Handle:{obj.Handle}");

            ScanXData(obj, path);

            if (!obj.ExtensionDictionary.IsNull)
                ScanExtDict(tr, obj.ExtensionDictionary, path + " → ExtDict", depth + 1);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Binary search — try every decode strategy
        // ═════════════════════════════════════════════════════════════════════

        private void SearchBytes(byte[] bytes, string path)
        {
            if (bytes == null || bytes.Length == 0) return;

            // ── GZip first (Urbano's primary encoding) ────────────────────────
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                string gz = TryGZip(bytes);
                if (gz != null)
                {
                    Match(gz, path + " [GZip→UTF-8]", "gzip", gz);
                    return;   // GZip succeeded; no need to try other strategies
                }
            }

            // ── Raw Deflate ───────────────────────────────────────────────────
            string deflated = TryDeflate(bytes);
            if (deflated != null)
            {
                Match(deflated, path + " [Deflate→UTF-8]", "deflate", deflated);
                return;
            }

            // ── UTF-8 (lenient) ───────────────────────────────────────────────
            try
            {
                string s = Encoding.UTF8.GetString(bytes);
                Match(s, path + " [UTF-8]", "utf8", s);
            }
            catch { }

            // ── UTF-16 LE (null-interleaved pattern) ─────────────────────────
            if (bytes.Length >= 4 && bytes.Length % 2 == 0)
            {
                bool nullInterleaved = false;
                for (int i = 1; i < Math.Min(bytes.Length, 16); i += 2)
                    if (bytes[i] == 0) { nullInterleaved = true; break; }

                if (nullInterleaved)
                {
                    try
                    {
                        string s = Encoding.Unicode.GetString(bytes);
                        Match(s, path + " [UTF-16LE]", "utf16", s);
                    }
                    catch { }
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Core match function — checks every term against the decoded string
        // ═════════════════════════════════════════════════════════════════════

        private void Match(string value, string path, string context, string fullText)
        {
            if (string.IsNullOrEmpty(value)) return;

            string valueLo = value.ToLowerInvariant();

            for (int t = 0; t < _termsLo.Length; t++)
            {
                int pos = valueLo.IndexOf(_termsLo[t], StringComparison.Ordinal);
                if (pos < 0) continue;

                _hits++;

                // ── Snippet: SnippetRadius chars around the hit ────────────
                int    s0   = Math.Max(0, pos - SnippetRadius);
                int    s1   = Math.Min(fullText.Length, pos + _terms[t].Length + SnippetRadius);
                string snip = fullText.Substring(s0, s1 - s0);
                if (s0 > 0) snip = "…" + snip;
                if (s1 < fullText.Length) snip += "…";
                // Collapse whitespace/newlines so it fits on one line
                snip = System.Text.RegularExpressions.Regex.Replace(snip, @"\s+", " ").Trim();

                // ── Format matches the spec ───────────────────────────────
                string line1 = $"[Found {_terms[t]}] → {path}";
                string line2 = $"  Snippet: {snip}";
                string bar   = new string('─', 72);

                // Print to command line
                _ed.WriteMessage($"\n{bar}");
                _ed.WriteMessage($"\n{line1}");
                _ed.WriteMessage($"\n{line2}");
                _ed.WriteMessage($"\n{bar}");

                // Write to log file (no length clipping)
                _log.WriteLine(bar);
                _log.WriteLine(line1);
                _log.WriteLine($"  Context: {context}");
                _log.WriteLine(line2);
                _log.WriteLine(bar);
                _log.WriteLine();
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Decode helpers
        // ═════════════════════════════════════════════════════════════════════

        private static string TryGZip(byte[] b)
        {
            try
            {
                using (var ms  = new MemoryStream(b))
                using (var gz  = new GZipStream(ms, CompressionMode.Decompress))
                using (var o   = new MemoryStream())
                {
                    gz.CopyTo(o);
                    byte[] dec = o.ToArray();
                    try   { return new UTF8Encoding(false, true).GetString(dec); }
                    catch { return Encoding.UTF8.GetString(dec); }
                }
            }
            catch { return null; }
        }

        private static string TryDeflate(byte[] b)
        {
            try
            {
                using (var ms  = new MemoryStream(b))
                using (var df  = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o   = new MemoryStream())
                {
                    df.CopyTo(o);
                    byte[] dec = o.ToArray();
                    return dec.Length > b.Length
                           ? new UTF8Encoding(false, false).GetString(dec)
                           : null;
                }
            }
            catch { return null; }
        }

        private static byte[] Merge(List<byte[]> chunks)
        {
            long total = 0;
            foreach (byte[] c in chunks) total += c.Length;
            byte[] buf = new byte[total];
            int    pos = 0;
            foreach (byte[] c in chunks)
            {
                Buffer.BlockCopy(c, 0, buf, pos, c.Length);
                pos += c.Length;
            }
            return buf;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Visited-object guard
        // ═════════════════════════════════════════════════════════════════════

        // Returns true if already visited (and therefore should be skipped).
        // Also marks the id as visited on first call.
        private bool Visit(ObjectId id)
        {
            if (id.IsNull) return true;
            long key = id.OldIdPtr.ToInt64();
            if (_visited.Contains(key)) return true;
            _visited.Add(key);
            return false;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Formatting
        // ═════════════════════════════════════════════════════════════════════

        private void Section(string title)
        {
            Print(""); Print(Ruler()); Print("  " + title); Print(Ruler());
        }

        private static string Ruler() => new string('═', 72);

        private void Print(string s)
        {
            _log?.WriteLine(s);
            string cl = s.Length > 200 ? s.Substring(0, 197) + "…" : s;
            _ed.WriteMessage("\n" + cl);
        }

        private void PrintNoMatchAdvice()
        {
            Print("");
            Print("  No matches found — checklist:");
            Print("  1. Did you SAVE the DWG in Urbano after setting the canary values?");
            Print("  2. Run ARS_LABEL_N / ARS_LABEL_S first so Urbano commits the data.");
            Print("  3. Canary may be in a compressed NOD binary. Run ExtractUrbanoXML");
            Print("     and open the Desktop XML files, then Ctrl+F for '535.555'.");
            Print("  4. Check that the NOD actually contains ARS_* keys by running");
            Print("     SnoopUrbanoNOD first to see what top-level keys exist.");
        }
    }
}
