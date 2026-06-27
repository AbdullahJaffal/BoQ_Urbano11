// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoCanarySearch.cs
//
// Command : UrbanoCanarySearch
//
// STRATEGY
// ────────
// Set a unique "canary" value on one pipe in Urbano (e.g. diameter 535.555,
// material KRG) then run this command.  It blasts through the ENTIRE DWG
// database looking for those strings.  Wherever a match appears reveals the
// exact storage location Urbano uses for that property — which tells us the
// schema for ALL pipes.
//
// SCAN COVERAGE
// ─────────────
//   §1  Named Objects Dictionary  — full recursive traversal of every
//       dict/XRecord reachable from the NOD root.
//   §2  Block Table               — extension dicts on the BlockTable object
//       itself, and on every BlockTableRecord (including *Model_Space,
//       *Paper_Space, and every INSERT block definition).
//   §3  Model Space entities      — XData and extension dicts on every
//       entity in *Model_Space.
//
// MATCH OUTPUT (for each hit)
// ───────────────────────────
//   ★★★ CANARY MATCH #N ★★★
//   Path    : NOD → "ARSX_NETWORKTOPOLOGY" → XRecord["XML"] → tv[042] binary
//   Term    : "535.555"
//   Snippet : …<Pipe dia="535.555" mat="KRG" guid="358A1F4…
//
// BINARY DECODE ORDER
// ───────────────────
//   1. GZip (magic 1F 8B) → decompress → UTF-8
//   2. Raw Deflate         → decompress → UTF-8
//   3. UTF-8 (strict)
//   4. UTF-16 LE (null-interleaved)
//   5. ASCII printable
//   In every case the full decoded string is searched, not just a hex preview.
//
// OUTPUT
// ──────
//   Command line  — match banners + path (concise)
//   Desktop file  — UrbanoCanarySearch_<timestamp>.txt  (full verbose log)
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

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoCanarySearch))]

namespace UrbanoMetraj
{
    public class UrbanoCanarySearch
    {
        // ── Search targets ────────────────────────────────────────────────────
        // Edit these or let the command prompt you.  All comparisons are
        // case-insensitive substring matches.
        private static readonly string[] DefaultTerms = { "535.555", "KRG" };

        // ── Tuning ────────────────────────────────────────────────────────────
        private const int MaxDictDepth   = 25;   // recursion guard
        private const int MaxOidFollow   = 3;    // depth to chase ObjectId tvs
        private const int SnippetRadius  = 120;  // chars either side of a match
        private const int MaxBinHexDump  = 64;   // hex preview bytes in log

        // ── Mutable state (reset per command invocation) ──────────────────────
        private string[]            _terms;
        private string[]            _termsLo;    // lower-case for fast compare
        private Editor              _ed;
        private StreamWriter        _log;
        private int                 _matchCount;
        private int                 _objectsScanned;
        private readonly HashSet<long> _visited = new HashSet<long>();

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("UrbanoCanarySearch")]
        public void RunCanarySearch()
        {
            // Reset state
            _visited.Clear();
            _matchCount     = 0;
            _objectsScanned = 0;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            // ── Prompt for canary terms ───────────────────────────────────────
            PromptStringOptions pso = new PromptStringOptions(
                $"\nCanary terms to search (comma-separated) [{string.Join(",", DefaultTerms)}]: ");
            pso.AllowSpaces  = true;
            pso.DefaultValue = string.Join(",", DefaultTerms);
            PromptResult pr = _ed.GetString(pso);

            if (pr.Status != PromptStatus.OK) { _ed.WriteMessage("\nCancelled.\n"); return; }

            string input = pr.StringResult.Trim();
            if (string.IsNullOrEmpty(input)) input = string.Join(",", DefaultTerms);

            _terms   = input.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            _termsLo = new string[_terms.Length];
            for (int i = 0; i < _terms.Length; i++)
            {
                _terms[i]   = _terms[i].Trim();
                _termsLo[i] = _terms[i].ToLowerInvariant();
            }

            // ── Output file ───────────────────────────────────────────────────
            string stamp   = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"UrbanoCanarySearch_{stamp}.txt");

            Say($"\nSearching for: {string.Join(" | ", _terms)}");
            Say($"Log file      : {outPath}");
            Say(Bar('═', 72));

            using (_log = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            {
                LogLine("UrbanoCanarySearch — Full DWG canary scan");
                LogLine($"DWG  : {db.Filename}");
                LogLine($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                LogLine($"Terms: {string.Join(" | ", _terms)}");
                LogLine(Bar('═', 72));

                using (Transaction tr = db.TransactionManager.StartTransaction())
                {
                    try
                    {
                        // §1 Named Objects Dictionary
                        SectionHeader("§1  Named Objects Dictionary (full recursive)");
                        DBDictionary nod = tr.GetObject(
                            db.NamedObjectsDictionaryId, OpenMode.ForRead) as DBDictionary;
                        ScanDictionary(tr, nod, "NOD", 0);

                        // §2 Block Table + all BlockTableRecords
                        SectionHeader("§2  Block Table & BlockTableRecord extension dicts");
                        ScanBlockTable(tr, db);

                        // §3 Model Space entities
                        SectionHeader("§3  Model Space entity XData & extension dicts");
                        ScanModelSpace(tr, db);
                    }
                    catch (System.Exception ex)
                    {
                        string msg = $"[FATAL] {ex.GetType().Name}: {ex.Message}";
                        Say(msg); LogLine(msg);
                        LogLine(ex.StackTrace);
                    }
                    finally
                    {
                        tr.Commit();
                    }
                }

                // ── Summary ───────────────────────────────────────────────────
                LogLine("");
                LogLine(Bar('═', 72));
                LogLine($"SCAN COMPLETE");
                LogLine($"Objects scanned : {_objectsScanned}");
                LogLine($"Canary matches  : {_matchCount}");
                LogLine(Bar('═', 72));
            }

            Say(Bar('═', 72));
            Say($"DONE — {_matchCount} match(es) in {_objectsScanned} objects.");
            Say($"Full log: {outPath}");
            if (_matchCount == 0)
            {
                Say("");
                Say("No matches found. Try:");
                Say("  1. Run ARS_LABEL_N / ARS_LABEL_S first (Urbano regenerate).");
                Say("  2. Ensure the canary value is committed (not just in the UI).");
                Say("  3. Check that the DWG is saved after setting the canary.");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // §1 helper — recursive dictionary scan
        // ═════════════════════════════════════════════════════════════════════

        private void ScanDictionary(Transaction tr, DBDictionary dict,
                                     string path, int depth)
        {
            if (dict == null) return;
            if (depth > MaxDictDepth)
            {
                LogLine($"{path} → [MAX DEPTH {MaxDictDepth} — stopped]");
                return;
            }

            _objectsScanned++;
            MarkVisited(dict.ObjectId);

            foreach (DBDictionaryEntry entry in dict)
            {
                string childPath = $"{path} → \"{entry.Key}\"";

                // The KEY itself might be a canary string (unlikely but check)
                CheckString(entry.Key, childPath, "dictionary key");

                if (entry.Value.IsNull || IsVisited(entry.Value)) continue;

                DBObject child;
                try { child = tr.GetObject(entry.Value, OpenMode.ForRead); }
                catch (System.Exception ex)
                {
                    LogLine($"{childPath} → [open error: {ex.Message}]");
                    continue;
                }

                MarkVisited(entry.Value);
                _objectsScanned++;

                switch (child)
                {
                    case DBDictionary subDict:
                        ScanDictionary(tr, subDict, childPath, depth + 1);
                        break;

                    case Xrecord xrec:
                        ScanXRecord(tr, xrec, childPath + " → XRecord", depth, 0);
                        break;

                    default:
                        // Could be a proxy / custom ARX object — scan its
                        // XData and extension dict too
                        ScanGenericObject(tr, child, childPath, depth);
                        break;
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // §2 Block Table
        // ═════════════════════════════════════════════════════════════════════

        private void ScanBlockTable(Transaction tr, Database db)
        {
            BlockTable bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
            if (bt == null) return;

            // Extension dict on the BlockTable object itself
            ScanExtDict(tr, bt, "BlockTable");

            int btrIdx = 0;
            foreach (ObjectId btrId in bt)
            {
                if (IsVisited(btrId)) continue;
                MarkVisited(btrId);
                _objectsScanned++;

                BlockTableRecord btr;
                try { btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord; }
                catch { continue; }
                if (btr == null) continue;

                string btrPath = $"BTR[\"{btr.Name}\"]";

                // XData on the BTR
                ScanXData(btr, btrPath + ".XData");

                // Extension dict on the BTR
                ScanExtDict(tr, btr, btrPath);

                btrIdx++;
            }

            LogLine($"Block Table: {btrIdx} BTRs scanned");
        }

        // ═════════════════════════════════════════════════════════════════════
        // §3 Model Space entities
        // ═════════════════════════════════════════════════════════════════════

        private void ScanModelSpace(Transaction tr, Database db)
        {
            BlockTableRecord ms;
            try
            {
                ms = tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead)
                        as BlockTableRecord;
            }
            catch (System.Exception ex)
            {
                LogLine($"[Model Space open error: {ex.Message}]");
                return;
            }

            if (ms == null) { LogLine("[Model Space is null]"); return; }

            int count = 0;
            foreach (ObjectId entId in ms)
            {
                if (IsVisited(entId)) continue;

                DBObject obj;
                try { obj = tr.GetObject(entId, OpenMode.ForRead); }
                catch { continue; }

                MarkVisited(entId);
                _objectsScanned++;
                count++;

                string entPath = $"ModelSpace[{count}]<{obj.GetRXClass()?.Name ?? "?"}>" +
                                 $"Handle:{obj.Handle}";

                // XData
                ScanXData(obj, entPath + ".XData");

                // Extension dict
                ScanExtDict(tr, obj, entPath);
            }

            LogLine($"Model Space: {count} entities scanned");
        }

        // ═════════════════════════════════════════════════════════════════════
        // XRecord — the core of the search
        // ═════════════════════════════════════════════════════════════════════

        private void ScanXRecord(Transaction tr, Xrecord xrec,
                                  string path, int dictDepth, int oidDepth)
        {
            if (xrec?.Data == null) return;

            // Accumulate all binary chunks for consolidated decode
            var binAcc = new List<byte[]>();
            int idx    = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                string tvPath = $"{path}[tv{idx:D3},code={code}]";

                if (val is byte[] bytes)
                {
                    binAcc.Add(bytes);
                    // Decode this chunk individually
                    DecodeAndSearch(bytes, tvPath + " binary-chunk");

                    LogLine($"{tvPath}  BINARY {bytes.Length}B  " +
                            $"hex:{HexDump(bytes, MaxBinHexDump)}");
                }
                else if (IsOidCode(code))
                {
                    if (val is ObjectId oid && !oid.IsNull
                        && oidDepth < MaxOidFollow && !IsVisited(oid))
                    {
                        MarkVisited(oid);
                        _objectsScanned++;
                        try
                        {
                            DBObject refObj = tr.GetObject(oid, OpenMode.ForRead);
                            string refPath  = $"{tvPath}→OID({refObj.GetRXClass()?.Name})";
                            LogLine(refPath);
                            switch (refObj)
                            {
                                case DBDictionary d:
                                    ScanDictionary(tr, d, refPath, dictDepth + 1);
                                    break;
                                case Xrecord xr:
                                    ScanXRecord(tr, xr, refPath + "→XRecord",
                                                dictDepth, oidDepth + 1);
                                    break;
                                default:
                                    ScanGenericObject(tr, refObj, refPath, dictDepth);
                                    break;
                            }
                        }
                        catch (System.Exception ex)
                        {
                            LogLine($"{tvPath} → OID follow failed: {ex.Message}");
                        }
                    }
                }
                else
                {
                    string strVal = val?.ToString() ?? "";
                    CheckString(strVal, tvPath, "xrecord-string");
                    // Limit log verbosity for long strings
                    string preview = strVal.Length > 200
                                     ? strVal.Substring(0, 197) + "…"
                                     : strVal;
                    LogLine($"{tvPath}  \"{preview}\"");
                }

                idx++;
            }

            // Consolidated multi-chunk decode (Urbano often splits a single
            // compressed payload across dozens of 127-byte chunks)
            if (binAcc.Count > 1)
            {
                byte[] all = MergeChunks(binAcc);
                DecodeAndSearch(all, path + $" [consolidated {all.Length}B from {binAcc.Count} chunks]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // XData (ResultBuffer) scanner
        // ═════════════════════════════════════════════════════════════════════

        private void ScanXData(DBObject obj, string path)
        {
            using (ResultBuffer rb = obj.XData)
            {
                if (rb == null) return;

                var binAcc     = new List<byte[]>();
                int idx        = 0;
                string curApp  = "(unknown)";

                foreach (TypedValue tv in rb)
                {
                    int    code = tv.TypeCode;
                    object val  = tv.Value;

                    if (code == (int)DxfCode.ExtendedDataRegAppName)
                    {
                        curApp = val?.ToString() ?? "";
                        LogLine($"{path}  RegApp:\"{curApp}\"");
                        idx = 0;
                        continue;
                    }

                    string tvPath = $"{path}[app={curApp},tv{idx:D3},code={code}]";

                    if (val is byte[] bytes)
                    {
                        binAcc.Add(bytes);
                        DecodeAndSearch(bytes, tvPath + " binary");
                        LogLine($"{tvPath}  BINARY {bytes.Length}B  hex:{HexDump(bytes, 32)}");
                    }
                    else
                    {
                        string strVal = val?.ToString() ?? "";
                        CheckString(strVal, tvPath, "xdata-string");
                    }

                    idx++;
                }

                if (binAcc.Count > 1)
                {
                    byte[] all = MergeChunks(binAcc);
                    DecodeAndSearch(all, path + $" [xdata-consolidated {all.Length}B]");
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Extension dictionary helper
        // ═════════════════════════════════════════════════════════════════════

        private void ScanExtDict(Transaction tr, DBObject obj, string path)
        {
            if (obj.ExtensionDictionary.IsNull) return;
            if (IsVisited(obj.ExtensionDictionary)) return;
            MarkVisited(obj.ExtensionDictionary);

            try
            {
                DBDictionary extDict =
                    tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead)
                      as DBDictionary;
                ScanDictionary(tr, extDict, path + ".ExtDict", 0);
            }
            catch (System.Exception ex)
            {
                LogLine($"{path}.ExtDict  [open error: {ex.Message}]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Generic / proxy object fallback
        // ═════════════════════════════════════════════════════════════════════

        private void ScanGenericObject(Transaction tr, DBObject obj,
                                        string path, int depth)
        {
            string cls = obj.GetRXClass()?.Name ?? "?";
            LogLine($"{path}  <{cls}> Handle:{obj.Handle}");

            ScanXData(obj, path + ".XData");
            ScanExtDict(tr, obj, path);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Binary decode → search
        // ═════════════════════════════════════════════════════════════════════

        private void DecodeAndSearch(byte[] bytes, string path)
        {
            if (bytes == null || bytes.Length == 0) return;

            // Try each decode strategy; search every successful result.
            // We deliberately try ALL strategies (not early-exit) because a
            // chunk might be valid ASCII AND valid UTF-8 — both should be checked.

            TryStrategyAndSearch(bytes, "raw-UTF8",
                b => new UTF8Encoding(false, false).GetString(b), path);

            // GZip (1F 8B)
            if (bytes.Length > 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
            {
                string gz = TryGZip(bytes);
                if (gz != null)
                {
                    CheckString(gz, path + " [GZip→UTF8]", "gzip-decoded");
                    // Also write the decoded text to the log if it looks like XML
                    if (gz.TrimStart().StartsWith("<"))
                        LogLine($"{path} [GZip→UTF8 first 400ch]: " +
                                (gz.Length > 400 ? gz.Substring(0, 400) + "…" : gz));
                    return;   // if GZip succeeded, other strategies are irrelevant
                }
            }

            // Raw Deflate
            string deflated = TryDeflate(bytes);
            if (deflated != null)
            {
                CheckString(deflated, path + " [Deflate→UTF8]", "deflate-decoded");
                return;
            }

            // UTF-16 LE
            if (bytes.Length % 2 == 0)
            {
                bool maybeUtf16 = false;
                for (int i = 1; i < Math.Min(bytes.Length, 20); i += 2)
                    if (bytes[i] == 0) { maybeUtf16 = true; break; }

                if (maybeUtf16)
                    TryStrategyAndSearch(bytes, "UTF16LE",
                        b => Encoding.Unicode.GetString(b), path);
            }

            // ASCII printable
            if (IsAscii(bytes))
                TryStrategyAndSearch(bytes, "ASCII",
                    b => Encoding.ASCII.GetString(b), path);
        }

        private void TryStrategyAndSearch(byte[] bytes, string strategyLabel,
                                           Func<byte[], string> decode, string path)
        {
            try
            {
                string s = decode(bytes);
                if (!string.IsNullOrEmpty(s))
                    CheckString(s, $"{path} [{strategyLabel}]", strategyLabel);
            }
            catch { }
        }

        // ═════════════════════════════════════════════════════════════════════
        // String canary check → match banner
        // ═════════════════════════════════════════════════════════════════════

        private void CheckString(string value, string path, string context)
        {
            if (string.IsNullOrEmpty(value)) return;

            string valueLo = value.ToLowerInvariant();

            for (int t = 0; t < _termsLo.Length; t++)
            {
                string termLo = _termsLo[t];
                int    pos    = valueLo.IndexOf(termLo, StringComparison.Ordinal);
                if (pos < 0) continue;

                _matchCount++;

                // Extract a snippet centred on the match
                int    snipStart = Math.Max(0, pos - SnippetRadius);
                int    snipEnd   = Math.Min(value.Length, pos + termLo.Length + SnippetRadius);
                string snippet   = value.Substring(snipStart, snipEnd - snipStart);
                if (snipStart > 0)   snippet = "…" + snippet;
                if (snipEnd < value.Length) snippet = snippet + "…";

                string banner =
                    "\n" + Bar('★', 72) + "\n" +
                    $"  ★★★  CANARY MATCH #{_matchCount}  ★★★\n" +
                    $"  Term    : \"{_terms[t]}\"\n" +
                    $"  Path    : {path}\n" +
                    $"  Context : {context}\n" +
                    $"  Snippet : {snippet}\n" +
                    Bar('★', 72);

                // Always echo full banner to command line
                _ed.WriteMessage(banner);

                // Write to log file too
                LogLine(banner);
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
                using (var out2 = new MemoryStream())
                {
                    gz.CopyTo(out2);
                    byte[] dec = out2.ToArray();
                    // Try UTF-8 on decompressed bytes
                    try { return new UTF8Encoding(false, true).GetString(dec); }
                    catch { }
                    // Try UTF-16 LE
                    if (dec.Length % 2 == 0)
                        try { return Encoding.Unicode.GetString(dec); }
                        catch { }
                    // Fallback: lenient UTF-8
                    return Encoding.UTF8.GetString(dec);
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
                using (var out2 = new MemoryStream())
                {
                    df.CopyTo(out2);
                    byte[] dec = out2.ToArray();
                    if (dec.Length <= b.Length) return null;   // no expansion = not deflate
                    return new UTF8Encoding(false, false).GetString(dec);
                }
            }
            catch { return null; }
        }

        private static bool IsAscii(byte[] b)
        {
            foreach (byte by in b)
                if (by < 0x09 || (by > 0x0D && by < 0x20) || by > 0x7E) return false;
            return true;
        }

        private static byte[] MergeChunks(List<byte[]> chunks)
        {
            long total = 0;
            foreach (byte[] c in chunks) total += c.Length;
            byte[] buf = new byte[total];
            int    pos = 0;
            foreach (byte[] c in chunks) { Buffer.BlockCopy(c, 0, buf, pos, c.Length); pos += c.Length; }
            return buf;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Visited-object guard (prevents infinite loops on cyclic object graphs)
        // ═════════════════════════════════════════════════════════════════════

        private bool IsVisited(ObjectId id)
        {
            if (id.IsNull) return true;
            return _visited.Contains(id.OldIdPtr.ToInt64());
        }

        private void MarkVisited(ObjectId id)
        {
            if (!id.IsNull) _visited.Add(id.OldIdPtr.ToInt64());
        }

        // ═════════════════════════════════════════════════════════════════════
        // Small utilities
        // ═════════════════════════════════════════════════════════════════════

        private static bool IsOidCode(int code)
            => code == 5 || code == 320 || (code >= 330 && code <= 399);

        private static string HexDump(byte[] b, int maxBytes)
        {
            int    n  = Math.Min(b.Length, maxBytes);
            var    sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2")).Append(' ');
            }
            if (b.Length > maxBytes) sb.Append($"(+{b.Length - maxBytes}B)");
            return sb.ToString().TrimEnd();
        }

        private static string Bar(char c, int n) => new string(c, n);

        // Write to log file only (verbose detail that would flood the command line)
        private void LogLine(string s) => _log?.WriteLine(s);

        // Write to both command line and log file
        private void Say(string s)
        {
            _log?.WriteLine(s);
            string cl = s.Length > 200 ? s.Substring(0, 197) + "…" : s;
            _ed.WriteMessage("\n" + cl);
        }

        private void SectionHeader(string title)
        {
            string bar = Bar('─', 72);
            Say(""); Say(bar); Say("  " + title); Say(bar);
        }
    }
}
