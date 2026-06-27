// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoNodDeepScan.cs
//
// Command: URBANO_NOD_DEEP
//
// PURPOSE
// ───────
// Exhaustively dump every object reachable from the DWG's Named Objects
// Dictionary (NOD) to a text file on the Desktop.  No model-space entity
// scanning; no filtering on known key names.
//
// WHY THIS IS NECESSARY
// ─────────────────────
// Urbano stores its "single source of truth" (pipe materials, diameters,
// network topology, elevations) as custom ObjectARX objects embedded in the
// NOD object graph.  Visual CAD entities (lines, circles, text labels) are
// merely a generated skin — Urbano redraws them from the hidden data on every
// "Update" or "Regenerate" call.
//
// Previous scans only checked a handful of known ARSX_* keys and stopped when
// they read "nullData" strings.  This scanner:
//   1.  Enumerates the COMPLETE NOD — every key, no filtering.
//   2.  Recurses into all nested DBDictionaries without depth guessing.
//   3.  For every XRecord, dumps each TypedValue with its raw TypeCode + value.
//   4.  Follows ObjectId cross-references (TypeCodes 5, 320, 330–399) to expose
//       the objects they point to.
//   5.  For binary chunks (TypeCode 310/311) tries, in order:
//         a) GZip decompression → UTF-8  (Urbano often gzip-compresses XML)
//         b) UTF-8 string
//         c) UTF-16 LE string
//         d) ASCII printable check
//         e) Raw hex dump (always included)
//   6.  Dumps the Block Table Records' extension dictionaries — Urbano
//       sometimes stores per-pipe/manhole data on the block definition, not on
//       the inserted block reference.
//   7.  Registers the scan results in a Desktop text file that can hold many
//       megabytes without truncation.
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

[assembly: CommandClass(typeof(UrbanoDeepScan.NodDeepScanner))]

namespace UrbanoDeepScan
{
    public class NodDeepScanner
    {
        // ── Tuneable limits ───────────────────────────────────────────────────
        // Increase if output is truncated; decrease if the file is too large.
        private const int MaxRecursionDepth  = 20;    // dictionary nesting guard
        private const int MaxBinaryHexBytes  = 256;   // hex preview per chunk
        private const int MaxStringChars     = 8192;  // inline string preview
        private const int MaxObjectIdFollow  = 3;     // depth we follow OID refs

        // Track visited ObjectIds to avoid infinite reference cycles
        private readonly HashSet<ObjectId> _visited = new HashSet<ObjectId>();

        // ─────────────────────────────────────────────────────────────────────
        // COMMAND ENTRY POINT
        // ─────────────────────────────────────────────────────────────────────
        [CommandMethod("URBANO_NOD_DEEP")]
        public void RunDeepScan()
        {
            _visited.Clear();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            Editor   ed  = doc.Editor;

            string outPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                "Urbano_NOD_Dump.txt");

            ed.WriteMessage($"\nURBANO_NOD_DEEP: scanning — output → {outPath}\n");

            using (var w   = new StreamWriter(outPath, false, new UTF8Encoding(true)))
            using (var tr  = db.TransactionManager.StartTransaction())
            {
                // ── File header ───────────────────────────────────────────────
                w.WriteLine("╔═══════════════════════════════════════════════════════════════╗");
                w.WriteLine("║              URBANO NOD DEEP SCAN — FULL DUMP                 ║");
                w.WriteLine("╚═══════════════════════════════════════════════════════════════╝");
                w.WriteLine($"DWG  : {db.Filename}");
                w.WriteLine($"Date : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine();

                // ════════════════════════════════════════════════════════════════
                // PART 1 ── Complete Named Objects Dictionary (NOD) dump
                // ════════════════════════════════════════════════════════════════
                Section(w, "PART 1 — Named Objects Dictionary (complete, recursive)");

                var nod = tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead)
                            as DBDictionary;
                DumpDictionary(tr, nod, w, "", 0);

                // ════════════════════════════════════════════════════════════════
                // PART 2 ── Block Table Records' Extension Dictionaries
                //
                // Urbano stores per-block-definition data here.  A manhole block
                // called "URBANO_MH_1000" might carry its diameter in the block
                // record's extension dict rather than on any entity.
                // ════════════════════════════════════════════════════════════════
                Section(w, "PART 2 — Block Table Records → extension dictionaries");

                var bt = tr.GetObject(db.BlockTableId, OpenMode.ForRead) as BlockTable;
                int btrCount = 0;
                foreach (ObjectId btrId in bt)
                {
                    try
                    {
                        var btr = tr.GetObject(btrId, OpenMode.ForRead) as BlockTableRecord;
                        if (btr == null || btr.ExtensionDictionary.IsNull) continue;

                        w.WriteLine($"  BTR: {btr.Name}  Handle:{btr.Handle}");
                        var extDict = tr.GetObject(btr.ExtensionDictionary, OpenMode.ForRead)
                                       as DBDictionary;
                        if (extDict != null)
                            DumpDictionary(tr, extDict, w, "    ", 0);
                        btrCount++;
                    }
                    catch (System.Exception ex)
                    {
                        w.WriteLine($"    ERROR reading BTR: {ex.Message}");
                    }
                }
                w.WriteLine($"  (scanned {btrCount} BTRs that have extension dicts)");

                // ════════════════════════════════════════════════════════════════
                // PART 3 ── Group Dictionary
                // ════════════════════════════════════════════════════════════════
                Section(w, "PART 3 — Group Dictionary");

                if (!db.GroupDictionaryId.IsNull)
                {
                    var gd = tr.GetObject(db.GroupDictionaryId, OpenMode.ForRead) as DBDictionary;
                    if (gd != null)
                        DumpDictionary(tr, gd, w, "  ", 0);
                    else
                        w.WriteLine("  (not a DBDictionary)");
                }
                else
                {
                    w.WriteLine("  (GroupDictionaryId is null)");
                }

                // ════════════════════════════════════════════════════════════════
                // PART 4 ── Registered Application Table
                //
                // Urbano registers XData application names here.
                // Listing them reveals what XData groups may exist on entities.
                // ════════════════════════════════════════════════════════════════
                Section(w, "PART 4 — Registered Application Table (all app names)");

                var rat = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
                var appNames = new List<string>();
                foreach (ObjectId id in rat)
                {
                    try
                    {
                        var rec = tr.GetObject(id, OpenMode.ForRead) as RegAppTableRecord;
                        if (rec != null) appNames.Add(rec.Name);
                    }
                    catch { }
                }
                appNames.Sort(StringComparer.OrdinalIgnoreCase);
                foreach (string name in appNames)
                    w.WriteLine($"  {name}");
                w.WriteLine($"  ({appNames.Count} registered apps total)");

                // ════════════════════════════════════════════════════════════════
                // PART 5 ── Summary of visited objects
                // ════════════════════════════════════════════════════════════════
                w.WriteLine();
                w.WriteLine($"══════ SCAN COMPLETE — {_visited.Count} unique objects visited ══════");

                tr.Commit();
            }

            ed.WriteMessage($"DONE — {outPath}\n");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Dictionary traversal
        // ═════════════════════════════════════════════════════════════════════

        private void DumpDictionary(Transaction tr, DBDictionary dict,
                                     StreamWriter w, string indent, int depth)
        {
            if (dict == null) return;
            if (depth > MaxRecursionDepth)
            { w.WriteLine($"{indent}⚠ MAX RECURSION DEPTH"); return; }

            w.WriteLine($"{indent}Dictionary  Handle:{dict.Handle}  " +
                        $"Count:{dict.Count}  " +
                        $"TreatElementsAsHard:{dict.TreatElementsAsHard}");

            foreach (DBDictionaryEntry entry in dict)
            {
                w.WriteLine($"{indent}├─ \"{entry.Key}\"");
                try
                {
                    if (entry.Value.IsNull)
                    { w.WriteLine($"{indent}│   (null ObjectId)"); continue; }

                    if (_visited.Contains(entry.Value))
                    { w.WriteLine($"{indent}│   (already visited — cycle guard)"); continue; }

                    _visited.Add(entry.Value);

                    DBObject obj = tr.GetObject(entry.Value, OpenMode.ForRead);
                    DumpAnyObject(tr, obj, w, indent + "│   ", depth + 1, MaxObjectIdFollow);
                }
                catch (System.Exception ex)
                {
                    w.WriteLine($"{indent}│   !! {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Generic object dispatcher
        // ═════════════════════════════════════════════════════════════════════

        private void DumpAnyObject(Transaction tr, DBObject obj, StreamWriter w,
                                    string indent, int dictDepth, int oidFollowBudget)
        {
            if (obj == null) { w.WriteLine($"{indent}(null)"); return; }

            string cls = obj.GetRXClass()?.Name ?? "?";
            w.WriteLine($"{indent}Type   : {cls}");
            w.WriteLine($"{indent}Handle : {obj.Handle}");

            switch (obj)
            {
                case DBDictionary subDict:
                    DumpDictionary(tr, subDict, w, indent, dictDepth);
                    break;

                case Xrecord xrec:
                    DumpXRecord(tr, xrec, w, indent, dictDepth, oidFollowBudget);
                    break;

                default:
                    // Custom / proxy object — dump everything accessible
                    DumpForeignObject(tr, obj, cls, w, indent, dictDepth, oidFollowBudget);
                    break;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // XRecord — core data extraction
        // ═════════════════════════════════════════════════════════════════════

        private void DumpXRecord(Transaction tr, Xrecord xrec, StreamWriter w,
                                  string indent, int dictDepth, int oidBudget)
        {
            if (xrec.Data == null) { w.WriteLine($"{indent}(empty XRecord)"); return; }

            // Collect all binary bytes for a consolidated decode attempt
            var accumBytes = new List<byte>();
            int tvIndex = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (val is byte[] bytes)
                {
                    // ── Binary chunk ─────────────────────────────────────────
                    accumBytes.AddRange(bytes);
                    w.WriteLine($"{indent}  [{tvIndex:D3}] TypeCode:{code,4}  " +
                                $"BINARY  {bytes.Length} bytes");
                    w.WriteLine($"{indent}         Hex : {HexPreview(bytes)}");

                    string decoded = TryDecodeBytes(bytes);
                    if (decoded != null)
                    {
                        // Show first MaxStringChars, but write the full thing
                        string preview = decoded.Length > MaxStringChars
                            ? decoded.Substring(0, MaxStringChars) + $"\n{indent}         … ({decoded.Length - MaxStringChars} more chars)"
                            : decoded;
                        w.WriteLine($"{indent}         Dec : {preview}");
                    }
                }
                else if (IsObjectIdCode(code))
                {
                    // ── ObjectId cross-reference — follow it ─────────────────
                    w.WriteLine($"{indent}  [{tvIndex:D3}] TypeCode:{code,4}  " +
                                $"ObjectId = {val}");

                    if (oidBudget > 0 && val is ObjectId oid && !oid.IsNull
                        && !_visited.Contains(oid))
                    {
                        _visited.Add(oid);
                        try
                        {
                            var refObj = tr.GetObject(oid, OpenMode.ForRead);
                            w.WriteLine($"{indent}         ↳ following reference...");
                            DumpAnyObject(tr, refObj, w, indent + "           ",
                                          dictDepth, oidBudget - 1);
                        }
                        catch (System.Exception ex)
                        {
                            w.WriteLine($"{indent}         ↳ FAILED: {ex.Message}");
                        }
                    }
                    else if (oidBudget <= 0)
                    {
                        w.WriteLine($"{indent}         (OID budget exhausted — not following)");
                    }
                }
                else
                {
                    // ── Scalar / string value ─────────────────────────────────
                    string strVal = val?.ToString() ?? "(null)";
                    if (strVal.Length > MaxStringChars)
                        strVal = strVal.Substring(0, MaxStringChars) +
                                 $"… ({strVal.Length - MaxStringChars} more chars)";

                    w.WriteLine($"{indent}  [{tvIndex:D3}] TypeCode:{code,4}  {strVal}");

                    // If it looks like it could be XML, try to parse and pretty-print first tag
                    if (strVal.TrimStart().StartsWith("<") && strVal.Contains(">"))
                        w.WriteLine($"{indent}         (looks like XML/fragment — {strVal.Length} chars)");
                }

                tvIndex++;
            }

            // ── Consolidated binary interpretation ────────────────────────────
            // If multiple chunks together form a larger structure (common in Urbano)
            // try decoding the concatenated bytes as well.
            if (accumBytes.Count > 0 && tvIndex > 1)
            {
                w.WriteLine($"{indent}  [consolidated {accumBytes.Count} bytes from {tvIndex} chunks]");
                string consolidated = TryDecodeBytes(accumBytes.ToArray());
                if (consolidated != null)
                {
                    string preview = consolidated.Length > MaxStringChars
                        ? consolidated.Substring(0, MaxStringChars) + "…"
                        : consolidated;
                    w.WriteLine($"{indent}  Consolidated decode: {preview}");
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Foreign / proxy object — dump what we can via reflection and APIs
        // ═════════════════════════════════════════════════════════════════════

        private void DumpForeignObject(Transaction tr, DBObject obj, string cls,
                                        StreamWriter w, string indent,
                                        int dictDepth, int oidBudget)
        {
            // DXF name (may differ from RX class name for proxy objects)
            string dxf = obj.GetRXClass()?.DxfName ?? "?";
            w.WriteLine($"{indent}DxfName  : {dxf}");

            bool isProxy = cls.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0
                        || dxf.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0;
            if (isProxy)
                w.WriteLine($"{indent}⚠ PROXY OBJECT — data accessible only via ObjectARX (unmanaged)");

            // XData
            using (ResultBuffer rb = obj.XData)
            {
                if (rb != null)
                {
                    w.WriteLine($"{indent}XData:");
                    DumpResultBufferRaw(tr, rb, w, indent + "  ", dictDepth, oidBudget);
                }
            }

            // Extension dictionary
            if (!obj.ExtensionDictionary.IsNull)
            {
                w.WriteLine($"{indent}ExtensionDictionary:");
                try
                {
                    var extD = tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead)
                                 as DBDictionary;
                    if (extD != null)
                        DumpDictionary(tr, extD, w, indent + "  ", dictDepth);
                }
                catch (System.Exception ex)
                {
                    w.WriteLine($"{indent}  !! {ex.Message}");
                }
            }
        }

        // Used for XData ResultBuffers (same logic as XRecord but sourced differently)
        private void DumpResultBufferRaw(Transaction tr, ResultBuffer rb, StreamWriter w,
                                          string indent, int dictDepth, int oidBudget)
        {
            int i = 0;
            foreach (TypedValue tv in rb)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (val is byte[] bytes)
                {
                    w.WriteLine($"{indent}  [{i:D3}] ({code,4}) BINARY {bytes.Length}B  " +
                                $"Hex:{HexPreview(bytes)}");
                    string dec = TryDecodeBytes(bytes);
                    if (dec != null) w.WriteLine($"{indent}         Dec:{Truncate(dec, 400)}");
                }
                else if (IsObjectIdCode(code) && val is ObjectId oid
                         && !oid.IsNull && oidBudget > 0
                         && !_visited.Contains(oid))
                {
                    w.WriteLine($"{indent}  [{i:D3}] ({code,4}) OID → following...");
                    _visited.Add(oid);
                    try
                    {
                        var refObj = tr.GetObject(oid, OpenMode.ForRead);
                        DumpAnyObject(tr, refObj, w, indent + "    ",
                                      dictDepth, oidBudget - 1);
                    }
                    catch (System.Exception ex)
                    {
                        w.WriteLine($"{indent}    FAILED: {ex.Message}");
                    }
                }
                else
                {
                    w.WriteLine($"{indent}  [{i:D3}] ({code,4}) {Truncate(val?.ToString() ?? "(null)", 300)}");
                }
                i++;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Binary decode strategies
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Tries to interpret a byte array as meaningful text using multiple strategies.
        /// Returns the best interpretation prefixed with the strategy name, or null
        /// if none produced readable output.
        /// </summary>
        private static string TryDecodeBytes(byte[] b)
        {
            if (b == null || b.Length == 0) return null;

            // ── Strategy 1: GZip (magic bytes 1F 8B) ─────────────────────────
            if (b.Length > 2 && b[0] == 0x1F && b[1] == 0x8B)
            {
                try
                {
                    using (var ms  = new MemoryStream(b))
                    using (var gz  = new GZipStream(ms, CompressionMode.Decompress))
                    using (var out2 = new MemoryStream())
                    {
                        gz.CopyTo(out2);
                        byte[] decompressed = out2.ToArray();

                        // Try UTF-8 on decompressed bytes
                        string xml = SafeUtf8(decompressed);
                        if (xml != null) return "[GZip→UTF8] " + xml;

                        // Try UTF-16 LE on decompressed bytes
                        if (decompressed.Length % 2 == 0)
                        {
                            string s = Encoding.Unicode.GetString(decompressed);
                            if (IsPrintable(s)) return "[GZip→UTF16LE] " + s;
                        }
                    }
                }
                catch { /* not valid gzip */ }
            }

            // ── Strategy 2: Deflate (no header — try anyway) ─────────────────
            // Some libraries write raw deflate without gzip wrapper
            try
            {
                using (var ms  = new MemoryStream(b))
                using (var df  = new DeflateStream(ms, CompressionMode.Decompress))
                using (var out2 = new MemoryStream())
                {
                    df.CopyTo(out2);
                    byte[] decompressed = out2.ToArray();
                    if (decompressed.Length > b.Length)   // successful expansion
                    {
                        string s = SafeUtf8(decompressed);
                        if (s != null) return "[Deflate→UTF8] " + s;
                    }
                }
            }
            catch { }

            // ── Strategy 3: UTF-8 ─────────────────────────────────────────────
            {
                string s = SafeUtf8(b);
                if (s != null && IsPrintable(s)) return "[UTF8] " + s;
            }

            // ── Strategy 4: UTF-16 LE ─────────────────────────────────────────
            if (b.Length % 2 == 0)
            {
                try
                {
                    // Check BOM or null-interleaved pattern (common for UTF-16)
                    bool hasNulls = false;
                    for (int i = 1; i < Math.Min(b.Length, 20); i += 2)
                        if (b[i] == 0) { hasNulls = true; break; }

                    if (hasNulls)
                    {
                        string s = Encoding.Unicode.GetString(b);
                        if (IsPrintable(s)) return "[UTF16LE] " + s;
                    }
                }
                catch { }
            }

            // ── Strategy 5: Pure ASCII printable ─────────────────────────────
            {
                bool ok = true;
                foreach (byte by in b)
                    if (by < 0x09 || (by > 0x0D && by < 0x20) || by > 0x7E)
                    { ok = false; break; }
                if (ok) return "[ASCII] " + Encoding.ASCII.GetString(b);
            }

            return null;   // none of the strategies produced readable text
        }

        // ═════════════════════════════════════════════════════════════════════
        // Small utilities
        // ═════════════════════════════════════════════════════════════════════

        private static string SafeUtf8(byte[] b)
        {
            try
            {
                // throwOnInvalidBytes = true so we know it's valid
                var enc = new UTF8Encoding(false, true);
                return enc.GetString(b);
            }
            catch { return null; }
        }

        private static bool IsPrintable(string s)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int good = 0;
            foreach (char c in s)
                if (c >= 0x20 || c == '\t' || c == '\r' || c == '\n') good++;
            return (double)good / s.Length >= 0.85;
        }

        private static bool IsObjectIdCode(int code)
            => code == 5
            || code == 320
            || (code >= 330 && code <= 399);

        private static string HexPreview(byte[] b)
        {
            int len = Math.Min(b.Length, MaxBinaryHexBytes);
            var sb = new StringBuilder(len * 3);
            for (int i = 0; i < len; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2")).Append(' ');
            }
            if (b.Length > MaxBinaryHexBytes)
                sb.Append($"(+{b.Length - MaxBinaryHexBytes}B)");
            return sb.ToString().TrimEnd();
        }

        private static string Truncate(string s, int max)
            => s != null && s.Length > max ? s.Substring(0, max) + "…" : s ?? "";

        private static void Section(StreamWriter w, string title)
        {
            w.WriteLine();
            w.WriteLine($"╔═══════════════════════════════════════════════════════════════╗");
            w.WriteLine($"║  {title.PadRight(62)}║");
            w.WriteLine($"╚═══════════════════════════════════════════════════════════════╝");
            w.WriteLine();
        }
    }
}
