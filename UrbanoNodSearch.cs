// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoNodSearch.cs
//
// Command : SnoopUrbanoNOD
//
// PURPOSE
// ───────
// Targeted NOD inspector built on the confirmed finding that Urbano 11 uses a
// relational model:
//
//   AcDbLine / AcDbCircle  →  XData[TOPOGUID] = "<guid>"
//                                                    │
//                                                    ▼
//                              NOD somewhere: guid as key or value
//                                             ↳ engineering properties live here
//
// This command:
//   1. Prompts for the TOPOGUID string to hunt (paste from SnoopUrbanoData output).
//   2. Scans the complete NOD top-level keys, flagging Urbano-related ones.
//   3. For every Urbano-related dictionary, recurses into ALL sub-entries and
//      checks every string value / decoded binary for the target GUID.
//   4. ALSO scans ALL top-level NOD entries (non-Urbano) for the GUID, because
//      Urbano might store data under a generic AutoCAD key name.
//   5. Prints a MATCH banner with the exact path whenever the GUID is found.
//   6. Writes the full verbose log to Desktop\UrbanoNodSearch_<guid8>.txt
//      while keeping the command-line output concise.
//
// REFERENCES : acdbmgd.dll, acmgd.dll, accoremgd.dll  (no Civil 3D needed)
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

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoNodSearch))]

namespace UrbanoMetraj
{
    public class UrbanoNodSearch
    {
        // ── Urbano keyword fingerprints (case-insensitive substring match) ────
        private static readonly string[] UrbanoKeywords =
        {
            "studio", "ars", "urbano", "network", "topo", "arsx",
            "pipe", "manhole", "sewer", "drainage", "ag_", "drawer",
            "topology", "nod_"
        };

        // ── Limits ────────────────────────────────────────────────────────────
        private const int MaxDepth       = 15;
        private const int MaxOidFollow   = 2;
        private const int MaxBinHex      = 128;   // hex preview bytes
        private const int MaxStringChars = 4000;  // inline string cap

        // ── State shared across recursive calls ───────────────────────────────
        private string          _guid;            // the TOPOGUID we're hunting
        private string          _guidLower;       // normalised for fast contains
        private Editor          _ed;
        private StreamWriter    _log;
        private int             _matchCount;
        private readonly HashSet<long> _seen = new HashSet<long>();

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("SnoopUrbanoNOD")]
        public void SnoopUrbanoNOD()
        {
            _seen.Clear();
            _matchCount = 0;

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            // ── Step 1: ask for the TOPOGUID to hunt ─────────────────────────
            PromptStringOptions pso = new PromptStringOptions(
                "\nEnter TOPOGUID to hunt (paste from SnoopUrbanoData output): ");
            pso.AllowSpaces = true;
            PromptResult pr = _ed.GetString(pso);

            if (pr.Status != PromptStatus.OK || string.IsNullOrWhiteSpace(pr.StringResult))
            {
                _ed.WriteMessage("\nNo GUID entered — command cancelled.\n");
                return;
            }

            _guid      = pr.StringResult.Trim();
            _guidLower = _guid.ToLowerInvariant();

            // Short label for filenames / headlines
            string guidShort = _guid.Length >= 8 ? _guid.Substring(0, 8) : _guid;

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                $"UrbanoNodSearch_{guidShort}.txt");

            _ed.WriteMessage($"\nHunting: {_guid}");
            _ed.WriteMessage($"\nFull log → {outPath}\n");

            using (_log = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    WriteHeader(db);

                    DBDictionary nod =
                        tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead)
                          as DBDictionary;

                    if (nod == null)
                    {
                        Emit("ERROR: Could not open Named Objects Dictionary.");
                        tr.Commit();
                        return;
                    }

                    // ── Step 2: catalogue top-level NOD keys ──────────────────
                    Emit("\n" + Bar('═', 70));
                    Emit("  PHASE 1 — Top-level NOD keys");
                    Emit(Bar('═', 70));

                    var urbanoEntries  = new List<(string key, ObjectId id)>();
                    var genericEntries = new List<(string key, ObjectId id)>();

                    foreach (DBDictionaryEntry entry in nod)
                    {
                        bool isUrbano = IsUrbanoKey(entry.Key);
                        string tag    = isUrbano ? "  [URBANO]" : "         ";
                        Emit($"{tag}  \"{entry.Key}\"");

                        if (isUrbano) urbanoEntries.Add((entry.Key, entry.Value));
                        else          genericEntries.Add((entry.Key, entry.Value));
                    }

                    Emit($"\n  Total NOD entries : {urbanoEntries.Count + genericEntries.Count}");
                    Emit($"  Urbano-flagged    : {urbanoEntries.Count}");
                    Emit($"  Generic           : {genericEntries.Count}");

                    // ── Step 3: deep-scan Urbano-flagged entries ──────────────
                    Emit("\n" + Bar('═', 70));
                    Emit("  PHASE 2 — Deep scan of Urbano-flagged NOD entries");
                    Emit(Bar('═', 70));

                    foreach (var (key, id) in urbanoEntries)
                    {
                        Emit($"\n{Bar('─', 60)}");
                        Emit($"  NOD[\"{ key}\"]");
                        Emit(Bar('─', 60));
                        ScanObject(tr, id, "  ", 0);
                    }

                    // ── Step 4: scan generic entries for the GUID ─────────────
                    Emit("\n" + Bar('═', 70));
                    Emit("  PHASE 3 — GUID hunt inside generic NOD entries");
                    Emit(Bar('═', 70));

                    int genericHits = 0;
                    foreach (var (key, id) in genericEntries)
                    {
                        // Quick pre-scan: only recurse if the entry might contain strings
                        // (all entries are scanned but we only emit detail on GUID hits)
                        if (!id.IsNull && !_seen.Contains(id.OldIdPtr.ToInt64()))
                        {
                            bool hit = ScanObjectForGuid(tr, id, $"NOD[\"{key}\"]", "  ", 0);
                            if (hit) genericHits++;
                        }
                    }

                    if (genericHits == 0)
                        Emit("  (GUID not found in any generic NOD entry)");

                    // ── Summary ───────────────────────────────────────────────
                    Emit("\n" + Bar('═', 70));
                    Emit($"  SEARCH COMPLETE");
                    Emit($"  GUID hunted : {_guid}");
                    Emit($"  Total MATCH banners printed : {_matchCount}");
                    if (_matchCount == 0)
                        Emit("  ⚠  GUID NOT FOUND in NOD — see Phase 4 suggestions below.");
                    Emit(Bar('═', 70));

                    if (_matchCount == 0)
                        EmitNotFoundAdvice();
                }
                catch (System.Exception ex)
                {
                    Emit($"\n[FATAL] {ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    tr.Commit();
                }
            }

            _ed.WriteMessage($"\nDone — {_matchCount} match(es) found.  Log: {outPath}\n");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Recursive object scanner (full verbose dump)
        // Used for the Urbano-flagged entries where we want every detail.
        // ═════════════════════════════════════════════════════════════════════

        private void ScanObject(Transaction tr, ObjectId id,
                                 string indent, int depth)
        {
            if (id.IsNull || depth > MaxDepth) return;

            long key = id.OldIdPtr.ToInt64();
            if (_seen.Contains(key)) { Emit($"{indent}(already visited)"); return; }
            _seen.Add(key);

            DBObject obj;
            try { obj = tr.GetObject(id, OpenMode.ForRead); }
            catch (System.Exception ex)
            {
                Emit($"{indent}[ERROR opening object: {ex.Message}]");
                return;
            }

            string cls = obj.GetRXClass()?.Name ?? "?";
            string dxf = obj.GetRXClass()?.DxfName ?? "?";
            Emit($"{indent}Type: {cls}  (DXF:{dxf})  Handle:{obj.Handle}");

            switch (obj)
            {
                case DBDictionary dict:
                    ScanDictionary(tr, dict, indent, depth);
                    break;

                case Xrecord xrec:
                    ScanXRecord(tr, xrec, indent, depth);
                    break;

                default:
                    ScanGenericObject(tr, obj, indent, depth);
                    break;
            }
        }

        private void ScanDictionary(Transaction tr, DBDictionary dict,
                                     string indent, int depth)
        {
            Emit($"{indent}Dictionary: {dict.Count} entries");

            foreach (DBDictionaryEntry entry in dict)
            {
                Emit($"{indent}  ┌─ \"{entry.Key}\"");

                // Check the KEY itself for the GUID
                if (ContainsGuid(entry.Key))
                    MatchBanner($"Dictionary KEY \"{entry.Key}\"", indent + "  ");

                if (entry.Value.IsNull)
                {
                    Emit($"{indent}  │  (null id)");
                }
                else
                {
                    long k = entry.Value.OldIdPtr.ToInt64();
                    if (_seen.Contains(k))
                        Emit($"{indent}  │  (already visited)");
                    else
                    {
                        _seen.Add(k);
                        try
                        {
                            DBObject child = tr.GetObject(entry.Value, OpenMode.ForRead);
                            ScanChildObject(tr, child, indent + "  │  ", depth + 1);
                        }
                        catch (System.Exception ex)
                        {
                            Emit($"{indent}  │  [ERROR: {ex.Message}]");
                        }
                    }
                }

                Emit($"{indent}  └─");
            }
        }

        private void ScanChildObject(Transaction tr, DBObject obj,
                                      string indent, int depth)
        {
            if (depth > MaxDepth) { Emit($"{indent}(max depth)"); return; }

            string cls = obj.GetRXClass()?.Name ?? "?";
            Emit($"{indent}Type: {cls}  Handle:{obj.Handle}");

            switch (obj)
            {
                case DBDictionary sub:
                    ScanDictionary(tr, sub, indent, depth);
                    break;
                case Xrecord xrec:
                    ScanXRecord(tr, xrec, indent, depth);
                    break;
                default:
                    ScanGenericObject(tr, obj, indent, depth);
                    break;
            }
        }

        private void ScanXRecord(Transaction tr, Xrecord xrec,
                                  string indent, int depth)
        {
            if (xrec.Data == null) { Emit($"{indent}(empty XRecord)"); return; }

            var accumBin = new List<byte>();
            int idx      = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (val is byte[] bytes)
                {
                    accumBin.AddRange(bytes);
                    Emit($"{indent}  [{idx:D3}] code={code,5}  BINARY {bytes.Length}B  Hex:{HexPreview(bytes)}");

                    string decoded = TryDecode(bytes);
                    if (decoded != null)
                    {
                        string preview = Truncate(decoded, MaxStringChars);
                        Emit($"{indent}         Dec: {preview}");
                        if (ContainsGuid(decoded))
                            MatchBanner($"XRecord[{idx}] binary decoded value", indent);
                    }
                }
                else if (IsOidCode(code))
                {
                    Emit($"{indent}  [{idx:D3}] code={code,5}  ObjectId={val}");

                    if (val is ObjectId oid && !oid.IsNull && depth < MaxOidFollow)
                    {
                        long k = oid.OldIdPtr.ToInt64();
                        if (!_seen.Contains(k))
                        {
                            _seen.Add(k);
                            try
                            {
                                DBObject refObj = tr.GetObject(oid, OpenMode.ForRead);
                                Emit($"{indent}         ↳ OID ref → {refObj.GetRXClass()?.Name}  Handle:{refObj.Handle}");
                                ScanChildObject(tr, refObj, indent + "           ", depth + 1);
                            }
                            catch (System.Exception ex)
                            {
                                Emit($"{indent}         ↳ FAILED: {ex.Message}");
                            }
                        }
                    }
                }
                else
                {
                    string strVal = val?.ToString() ?? "(null)";
                    string dxfLbl = SafeDxf(code);
                    Emit($"{indent}  [{idx:D3}] code={code,5}  {dxfLbl,-26}  {Truncate(strVal, MaxStringChars)}");

                    if (ContainsGuid(strVal))
                        MatchBanner($"XRecord[{idx}] string value \"{Truncate(strVal, 80)}\"", indent);
                }

                idx++;
            }

            // Consolidated binary decode (chunks may split a single payload)
            if (accumBin.Count > 0 && idx > 1)
            {
                string consol = TryDecode(accumBin.ToArray());
                if (consol != null)
                {
                    Emit($"{indent}  [consolidated {accumBin.Count}B from {idx} chunks]");
                    Emit($"{indent}  Dec: {Truncate(consol, MaxStringChars)}");
                    if (ContainsGuid(consol))
                        MatchBanner("XRecord consolidated binary decode", indent);
                }
            }
        }

        private void ScanGenericObject(Transaction tr, DBObject obj,
                                        string indent, int depth)
        {
            // Dump XData if any
            using (ResultBuffer rb = obj.XData)
            {
                if (rb != null)
                {
                    Emit($"{indent}XData present:");
                    DumpResultBuffer(tr, rb, indent + "  ", depth);
                }
            }

            // Extension dictionary
            if (!obj.ExtensionDictionary.IsNull)
            {
                long k = obj.ExtensionDictionary.OldIdPtr.ToInt64();
                if (!_seen.Contains(k))
                {
                    _seen.Add(k);
                    try
                    {
                        DBDictionary extD =
                            tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                        if (extD != null)
                        {
                            Emit($"{indent}ExtDict:");
                            ScanDictionary(tr, extD, indent + "  ", depth + 1);
                        }
                    }
                    catch (System.Exception ex)
                    {
                        Emit($"{indent}  [ExtDict ERROR: {ex.Message}]");
                    }
                }
            }
        }

        private void DumpResultBuffer(Transaction tr, ResultBuffer rb,
                                       string indent, int depth)
        {
            int idx = 0;
            foreach (TypedValue tv in rb)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (val is byte[] bytes)
                {
                    Emit($"{indent}  [{idx:D3}] ({code}) BINARY {bytes.Length}B  Hex:{HexPreview(bytes)}");
                    string dec = TryDecode(bytes);
                    if (dec != null)
                    {
                        Emit($"{indent}        Dec:{Truncate(dec, MaxStringChars)}");
                        if (ContainsGuid(dec)) MatchBanner($"ResultBuffer[{idx}] binary", indent);
                    }
                }
                else
                {
                    string strVal = val?.ToString() ?? "(null)";
                    Emit($"{indent}  [{idx:D3}] ({code}) {SafeDxf(code),-24}  {Truncate(strVal, 300)}");
                    if (ContainsGuid(strVal)) MatchBanner($"ResultBuffer[{idx}] value", indent);
                }
                idx++;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Lightweight GUID-only scan (Phase 3 — generic entries)
        // Returns true if ANY match was found anywhere in this object's subtree.
        // Only emits output when it finds a hit.
        // ═════════════════════════════════════════════════════════════════════

        private bool ScanObjectForGuid(Transaction tr, ObjectId id,
                                        string path, string indent, int depth)
        {
            if (id.IsNull || depth > MaxDepth) return false;
            long k = id.OldIdPtr.ToInt64();
            if (_seen.Contains(k)) return false;
            _seen.Add(k);

            DBObject obj;
            try { obj = tr.GetObject(id, OpenMode.ForRead); }
            catch { return false; }

            bool found = false;

            switch (obj)
            {
                case DBDictionary dict:
                    foreach (DBDictionaryEntry entry in dict)
                    {
                        string childPath = $"{path}[\"{entry.Key}\"]";
                        if (ContainsGuid(entry.Key))
                        {
                            MatchBanner($"Dictionary KEY at {childPath}", indent);
                            found = true;
                        }
                        if (!entry.Value.IsNull)
                            found |= ScanObjectForGuid(tr, entry.Value, childPath, indent, depth + 1);
                    }
                    break;

                case Xrecord xrec:
                    found |= XRecordContainsGuid(tr, xrec, path, indent, depth);
                    break;

                default:
                    // Check XData on this object
                    using (ResultBuffer rb = obj.XData)
                    {
                        if (rb != null)
                        {
                            int i = 0;
                            foreach (TypedValue tv in rb)
                            {
                                string s = tv.Value?.ToString() ?? "";
                                if (ContainsGuid(s))
                                {
                                    MatchBanner($"XData[{i}] on object at {path}", indent);
                                    found = true;
                                }
                                if (tv.Value is byte[] bytes)
                                {
                                    string dec = TryDecode(bytes);
                                    if (dec != null && ContainsGuid(dec))
                                    {
                                        MatchBanner($"XData[{i}] binary decode at {path}", indent);
                                        found = true;
                                    }
                                }
                                i++;
                            }
                        }
                    }
                    // Extension dict
                    if (!obj.ExtensionDictionary.IsNull)
                        found |= ScanObjectForGuid(tr, obj.ExtensionDictionary,
                                                   $"{path}.ExtDict", indent, depth + 1);
                    break;
            }

            return found;
        }

        private bool XRecordContainsGuid(Transaction tr, Xrecord xrec,
                                          string path, string indent, int depth)
        {
            if (xrec.Data == null) return false;
            bool found       = false;
            var  accumBin    = new List<byte>();
            int  idx         = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                if (tv.Value is byte[] bytes)
                {
                    accumBin.AddRange(bytes);
                    string dec = TryDecode(bytes);
                    if (dec != null && ContainsGuid(dec))
                    {
                        MatchBanner($"XRecord[{idx}] binary at {path}", indent);
                        found = true;
                    }
                }
                else if (IsOidCode(tv.TypeCode) && tv.Value is ObjectId oid
                         && !oid.IsNull && depth < MaxOidFollow)
                {
                    found |= ScanObjectForGuid(tr, oid, $"{path}→OID[{idx}]", indent, depth + 1);
                }
                else
                {
                    string s = tv.Value?.ToString() ?? "";
                    if (ContainsGuid(s))
                    {
                        MatchBanner($"XRecord[{idx}] string at {path}: \"{Truncate(s,80)}\"", indent);
                        found = true;
                    }
                }
                idx++;
            }

            if (accumBin.Count > 0)
            {
                string consol = TryDecode(accumBin.ToArray());
                if (consol != null && ContainsGuid(consol))
                {
                    MatchBanner($"XRecord consolidated binary at {path}", indent);
                    found = true;
                }
            }

            return found;
        }

        // ═════════════════════════════════════════════════════════════════════
        // MATCH BANNER — printed prominently whenever the GUID is found
        // ═════════════════════════════════════════════════════════════════════

        private void MatchBanner(string context, string indent)
        {
            _matchCount++;
            string bar = new string('*', 65);
            Emit("");
            Emit(indent + bar);
            Emit(indent + $"  *** GUID MATCH #{_matchCount} ***");
            Emit(indent + $"  GUID    : {_guid}");
            Emit(indent + $"  Found in: {context}");
            Emit(indent + bar);
            Emit("");

            // Also echo loudly to the command line regardless of log buffering
            _ed.WriteMessage($"\n*** MATCH #{_matchCount}: {context}\n");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Advice if GUID was not found anywhere
        // ═════════════════════════════════════════════════════════════════════

        private void EmitNotFoundAdvice()
        {
            Emit("\n" + Bar('─', 70));
            Emit("  GUID NOT FOUND — possible reasons and next steps:");
            Emit(Bar('─', 70));
            Emit("");
            Emit("  1. The GUID is stored COMPRESSED.");
            Emit("     Urbano frequently GZip-compresses its XML payloads.");
            Emit("     Run URBANO_NOD_DEEP and inspect the decoded binary columns");
            Emit("     in the output file — look for your GUID in the 'Dec:' lines.");
            Emit("");
            Emit("  2. The GUID is stored in a PROXY OBJECT.");
            Emit("     If Urbano's ARX objects are not registered (Civil 3D not loaded),");
            Emit("     AutoCAD reads them as AcDbProxyObject whose data is opaque.");
            Emit("     ➜ Ensure Urbano 11 plugin is fully loaded, then re-run.");
            Emit("");
            Emit("  3. The TOPOGUID XData key uses a DIFFERENT DICTIONARY KEY.");
            Emit("     Some versions store the GUID as a handle index, not as the key");
            Emit("     directly. Try running URBANO_NOD_DEEP and searching the");
            Emit("     output file for the first 8 chars of your GUID:");
            Emit($"     {(_guid.Length >= 8 ? _guid.Substring(0, 8) : _guid)}");
            Emit("");
            Emit("  4. Data lives in the ENTITY'S OWN EXTENSION DICTIONARY, not NOD.");
            Emit("     Run SnoopUrbanoData on the pipe → check § 3 Extension Dictionary.");
            Emit("     If that is also empty, try running ARS_LABEL_N / ARS_LABEL_S");
            Emit("     first (Urbano commands that regenerate the network state).");
            Emit("");
            Emit("  5. Data is in a BLOCK TABLE RECORD extension dict.");
            Emit("     URBANO_NOD_DEEP Part 2 scans those. Check its output.");
            Emit(Bar('─', 70));
        }

        // ═════════════════════════════════════════════════════════════════════
        // Binary decode (GZip → UTF-8 → UTF-16 → ASCII)
        // ═════════════════════════════════════════════════════════════════════

        private static string TryDecode(byte[] b)
        {
            if (b == null || b.Length == 0) return null;

            // GZip
            if (b.Length > 2 && b[0] == 0x1F && b[1] == 0x8B)
            {
                try
                {
                    using (var ms = new MemoryStream(b))
                    using (var gz = new GZipStream(ms, CompressionMode.Decompress))
                    using (var o  = new MemoryStream())
                    {
                        gz.CopyTo(o);
                        byte[] dec = o.ToArray();
                        string s   = SafeUtf8(dec);
                        if (s != null) return "[GZip→UTF8] " + s;
                        if (dec.Length % 2 == 0)
                        {
                            string u = Encoding.Unicode.GetString(dec);
                            if (IsPrintable(u)) return "[GZip→UTF16LE] " + u;
                        }
                    }
                }
                catch { }
            }

            // Raw Deflate
            try
            {
                using (var ms = new MemoryStream(b))
                using (var df = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o  = new MemoryStream())
                {
                    df.CopyTo(o);
                    byte[] dec = o.ToArray();
                    if (dec.Length > b.Length)
                    {
                        string s = SafeUtf8(dec);
                        if (s != null) return "[Deflate→UTF8] " + s;
                    }
                }
            }
            catch { }

            // Plain UTF-8
            { string s = SafeUtf8(b); if (s != null && IsPrintable(s)) return "[UTF8] " + s; }

            // UTF-16 LE (look for null-interleaved pattern)
            if (b.Length % 2 == 0)
            {
                bool hasNulls = false;
                for (int i = 1; i < Math.Min(b.Length, 20); i += 2)
                    if (b[i] == 0) { hasNulls = true; break; }
                if (hasNulls)
                {
                    try
                    {
                        string s = Encoding.Unicode.GetString(b);
                        if (IsPrintable(s)) return "[UTF16LE] " + s;
                    }
                    catch { }
                }
            }

            // Pure ASCII
            bool ok = true;
            foreach (byte by in b)
                if (by < 0x09 || (by > 0x0D && by < 0x20) || by > 0x7E) { ok = false; break; }
            if (ok) return "[ASCII] " + Encoding.ASCII.GetString(b);

            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        private bool ContainsGuid(string s)
            => s != null && s.IndexOf(_guidLower, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool IsUrbanoKey(string key)
        {
            if (key == null) return false;
            string lo = key.ToLowerInvariant();
            foreach (string kw in UrbanoKeywords)
                if (lo.Contains(kw)) return true;
            return false;
        }

        private static bool IsOidCode(int code)
            => code == 5 || code == 320 || (code >= 330 && code <= 399);

        private static string SafeUtf8(byte[] b)
        {
            try   { return new UTF8Encoding(false, true).GetString(b); }
            catch { return null; }
        }

        private static bool IsPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int good = 0;
            foreach (char c in s)
                if (c >= 0x20 || c == '\t' || c == '\r' || c == '\n') good++;
            return (double)good / s.Length >= 0.82;
        }

        private static string HexPreview(byte[] b)
        {
            int n  = Math.Min(b.Length, MaxBinHex);
            var sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2")).Append(' ');
            }
            if (b.Length > MaxBinHex) sb.Append($"(+{b.Length - MaxBinHex}B)");
            return sb.ToString().TrimEnd();
        }

        private static string SafeDxf(int code)
        {
            try   { return ((DxfCode)code).ToString(); }
            catch { return $"code{code}"; }
        }

        private static string Truncate(string s, int max)
            => s != null && s.Length > max
               ? s.Substring(0, max) + $"… (+{s.Length - max} chars)"
               : s ?? "";

        private static string Bar(char c, int len)
            => new string(c, len);

        private void WriteHeader(Database db)
        {
            Emit(Bar('═', 70));
            Emit("  URBANO NOD SEARCH — targeted GUID hunt");
            Emit(Bar('═', 70));
            Emit($"  DWG  : {db.Filename}");
            Emit($"  Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Emit($"  GUID : {_guid}");
            Emit(Bar('═', 70));
        }

        // Writes to both the log file and the command line (trimmed to 180 chars)
        private void Emit(string line)
        {
            _log?.WriteLine(line);
            string cl = line.Length > 180 ? line.Substring(0, 177) + "…" : line;
            _ed.WriteMessage("\n" + cl);
        }
    }
}
