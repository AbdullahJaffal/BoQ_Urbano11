// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoXmlExtractor.cs
//
// Command : ExtractUrbanoXML
//
// PURPOSE
// ───────
// Urbano 11 serialises its entire network database (pipe diameters, materials,
// invert levels, excavation volumes, manhole data) as XML and stores it inside
// XRecords named "XML" in several NOD dictionaries:
//
//   NOD["ARSX_NETWORKTOPOLOGY"]["XML"]  ← main network data  (pipes + manholes)
//   NOD["ARSX_AUXTOPOLOGY"]["XML"]      ← auxiliary topology
//   NOD["ARSX_LSINSTANCES"]["XML"]      ← layer-set instances
//   NOD["ARSX_DCT_PIPE"]["XML"]         ← pipe catalogue
//   NOD["ARSX_DCT_MANHOLE"]["XML"]      ← manhole catalogue
//   NOD["ARSX_DCT_TRENCH"]["XML"]       ← trench catalogue
//   … plus any other NOD dict that has a child named "XML"   ← auto-discovered
//
// Each XRecord stores the payload as either:
//   a) One or more DxfCode.BinaryChunk (310) entries  → reassemble, then GZip-decode
//   b) One or more DxfCode.Text (1) strings           → concatenate directly
//   c) A mix of both
//
// WHAT THIS COMMAND DOES
// ──────────────────────
//   1. Scans every top-level NOD entry for an "XML" child.
//   2. Extracts, reassembles, and decompresses each payload found.
//   3. Saves each XML to Desktop\Urbano_<DictName>.xml  (pretty-printed).
//   4. Performs a structural analysis of ARSX_NETWORKTOPOLOGY:
//        - Prints the root element name and namespace.
//        - Lists the top-3 child element types and their attributes.
//        - Searches the XML for the TOPOGUID the user supplies.
//        - If found, prints every attribute on that element → this is the
//          BoQ property map we need.
//
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoXmlExtractor))]

namespace UrbanoMetraj
{
    public class UrbanoXmlExtractor
    {
        // Known Urbano NOD keys – scanned first (in priority order), then the
        // auto-discovery pass picks up anything else with an "XML" child.
        private static readonly string[] KnownUrbanoKeys =
        {
            "ARSX_NETWORKTOPOLOGY",
            "ARSX_AUXTOPOLOGY",
            "ARSX_LSINSTANCES",
            "ARSX_DCT_PIPE",
            "ARSX_DCT_MANHOLE",
            "ARSX_DCT_TRENCH",
            "__SA__.LD",
        };

        private Editor _ed;

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("ExtractUrbanoXML")]
        public void ExtractUrbanoXML()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            // Optional: ask for a TOPOGUID to look up in the extracted XML
            PromptStringOptions pso = new PromptStringOptions(
                "\nEnter TOPOGUID to look up in extracted XML (or press Enter to skip): ");
            pso.AllowSpaces  = true;
            pso.DefaultValue = "";
            PromptResult pr = _ed.GetString(pso);

            string searchGuid = (pr.Status == PromptStatus.OK)
                                ? pr.StringResult.Trim()
                                : "";

            Say("\n" + Bar('═', 70));
            Say("  ExtractUrbanoXML");
            Say(Bar('═', 70));
            if (!string.IsNullOrEmpty(searchGuid))
                Say($"  GUID to look up : {searchGuid}");
            Say($"  DWG             : {db.Filename}");
            Say($"  Output folder   : {Desktop}");
            Say(Bar('═', 70));

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    DBDictionary nod =
                        tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead)
                          as DBDictionary;

                    if (nod == null)
                    {
                        Say("ERROR: Cannot open Named Objects Dictionary.");
                        tr.Commit();
                        return;
                    }

                    // ── Pass 1: process known keys in priority order ───────────
                    var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    XDocument mainXml = null;   // ARSX_NETWORKTOPOLOGY result

                    foreach (string key in KnownUrbanoKeys)
                    {
                        if (!nod.Contains(key)) continue;
                        processed.Add(key);

                        Say($"\n{Bar('─', 60)}");
                        Say($"  Processing known key: \"{key}\"");
                        Say(Bar('─', 60));

                        XDocument xdoc = ExtractFromNodDict(tr, nod, key);

                        if (xdoc != null)
                        {
                            string path = SaveXml(xdoc, key);
                            Say($"  ✓ Saved → {path}");

                            if (key.Equals("ARSX_NETWORKTOPOLOGY",
                                           StringComparison.OrdinalIgnoreCase))
                                mainXml = xdoc;

                            AnalyseXml(xdoc, key, searchGuid);
                        }
                    }

                    // ── Pass 2: auto-discover any other dict with an "XML" child ─
                    Say($"\n{Bar('─', 60)}");
                    Say("  Auto-discovery: scanning remaining NOD entries for \"XML\" children");
                    Say(Bar('─', 60));

                    int discovered = 0;
                    foreach (DBDictionaryEntry entry in nod)
                    {
                        if (processed.Contains(entry.Key)) continue;
                        if (entry.Value.IsNull) continue;

                        try
                        {
                            DBDictionary subDict =
                                tr.GetObject(entry.Value, OpenMode.ForRead) as DBDictionary;
                            if (subDict == null || !subDict.Contains("XML")) continue;

                            Say($"\n  Found \"XML\" in NOD[\"{entry.Key}\"]");
                            processed.Add(entry.Key);
                            discovered++;

                            XDocument xdoc = ExtractFromNodDict(tr, nod, entry.Key);
                            if (xdoc != null)
                            {
                                string path = SaveXml(xdoc, entry.Key);
                                Say($"  ✓ Saved → {path}");
                                AnalyseXml(xdoc, entry.Key, searchGuid);
                            }
                        }
                        catch { /* not a dict or not readable — skip */ }
                    }

                    if (discovered == 0)
                        Say("  (No additional dictionaries with \"XML\" entries found)");

                    // ── Final summary ─────────────────────────────────────────
                    Say("\n" + Bar('═', 70));
                    Say($"  EXTRACTION COMPLETE — {processed.Count} dictionary/ies processed");
                    Say($"  Files saved to: {Desktop}");
                    if (!string.IsNullOrEmpty(searchGuid) && mainXml == null)
                        Say("  NOTE: ARSX_NETWORKTOPOLOGY was not found or empty.");
                    Say(Bar('═', 70));
                }
                catch (System.Exception ex)
                {
                    Say($"\n[FATAL] {ex.GetType().Name}: {ex.Message}");
                    Say(ex.StackTrace);
                }
                finally
                {
                    tr.Commit();
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Core extraction: NOD dict → XDocument
        // ═════════════════════════════════════════════════════════════════════

        private XDocument ExtractFromNodDict(Transaction tr,
                                              DBDictionary nod,
                                              string dictKey)
        {
            // Step 1: open the named sub-dictionary
            if (!nod.Contains(dictKey))
            {
                Say($"  [{dictKey}] not found in NOD.");
                return null;
            }
            ObjectId dictId = nod.GetAt(dictKey);
            if (dictId.IsNull)
            {
                Say($"  [{dictKey}] entry has null ObjectId.");
                return null;
            }

            DBDictionary dict;
            try { dict = tr.GetObject(dictId, OpenMode.ForRead) as DBDictionary; }
            catch (System.Exception ex)
            {
                Say($"  [{dictKey}] cannot open: {ex.Message}");
                return null;
            }

            if (dict == null)
            {
                Say($"  [{dictKey}] is not a DBDictionary.");
                return null;
            }

            Say($"  [{dictKey}] opened — {dict.Count} entries");

            // List all entries so the user can see what's inside
            foreach (DBDictionaryEntry e in dict)
                Say($"    sub-key: \"{e.Key}\"");

            // Step 2: locate the "XML" XRecord
            if (!dict.Contains("XML"))
            {
                Say($"  [{dictKey}] has no \"XML\" entry.");
                return null;
            }
            ObjectId xrecId = dict.GetAt("XML");
            if (xrecId.IsNull)
            {
                Say($"  [{dictKey}][\"XML\"] entry has null ObjectId.");
                return null;
            }

            Xrecord xrec;
            try { xrec = tr.GetObject(xrecId, OpenMode.ForRead) as Xrecord; }
            catch (System.Exception ex)
            {
                Say($"  [{dictKey}][\"XML\"] cannot open XRecord: {ex.Message}");
                return null;
            }

            if (xrec == null)
            {
                Say($"  [{dictKey}][\"XML\"] is not an XRecord.");
                return null;
            }

            if (xrec.Data == null)
            {
                Say($"  [{dictKey}][\"XML\"] XRecord has null Data.");
                return null;
            }

            // Step 3: reassemble the payload from TypedValues
            return ReassembleAndDecode(xrec.Data, dictKey);
        }

        // ═════════════════════════════════════════════════════════════════════
        // Reassemble chunks → decompress → parse XML
        // ═════════════════════════════════════════════════════════════════════

        private XDocument ReassembleAndDecode(ResultBuffer rb, string label)
        {
            var    binChunks  = new List<byte[]>();   // DxfCode 310 chunks
            var    strParts   = new List<string>();   // DxfCode 1 / 3 strings
            int    tvCount    = 0;
            long   totalBin   = 0;
            long   totalStr   = 0;

            foreach (TypedValue tv in rb)
            {
                int code = tv.TypeCode;
                tvCount++;

                if (tv.Value is byte[] bytes)
                {
                    binChunks.Add(bytes);
                    totalBin += bytes.Length;
                    Say($"  chunk [{tvCount:D3}] code={code}  BINARY {bytes.Length}B  " +
                        $"Hex:{HexPreview(bytes, 20)}");
                }
                else if (tv.Value != null)
                {
                    string s = tv.Value.ToString();
                    strParts.Add(s);
                    totalStr += s.Length;
                    // Only preview first 120 chars of string chunks
                    string preview = s.Length > 120 ? s.Substring(0, 117) + "…" : s;
                    Say($"  chunk [{tvCount:D3}] code={code}  STRING {s.Length}ch  \"{preview}\"");
                }
            }

            Say($"  TypedValues: {tvCount}  |  Binary total: {totalBin}B  |  String total: {totalStr}ch");

            // ── Binary path ───────────────────────────────────────────────────
            if (binChunks.Count > 0)
            {
                byte[] raw = Reassemble(binChunks);
                Say($"  Reassembled binary: {raw.Length}B");
                Say($"  Magic bytes: {raw[0]:X2} {(raw.Length > 1 ? raw[1].ToString("X2") : "?")}");

                byte[] payload = TryDecompress(raw, label);
                string xml     = TryDecodeToString(payload ?? raw);

                if (xml == null)
                {
                    Say($"  [{label}] Could not decode binary payload to readable text.");
                    // Save raw bytes for manual inspection
                    string rawPath = Path.Combine(Desktop, $"Urbano_{SanitizeKey(label)}_RAW.bin");
                    File.WriteAllBytes(rawPath, raw);
                    Say($"  Raw bytes saved → {rawPath}");
                    return null;
                }

                Say($"  Decoded string length: {xml.Length} chars");
                return TryParseXml(xml, label);
            }

            // ── String path ───────────────────────────────────────────────────
            if (strParts.Count > 0)
            {
                string combined = string.Concat(strParts);
                Say($"  Combined string: {combined.Length} chars");

                // Sometimes strings are still GZip-encoded as base64
                byte[] fromBase64 = TryBase64Decode(combined);
                if (fromBase64 != null)
                {
                    Say($"  Decoded base64 → {fromBase64.Length}B");
                    byte[] payload = TryDecompress(fromBase64, label);
                    string xml2    = TryDecodeToString(payload ?? fromBase64);
                    if (xml2 != null) return TryParseXml(xml2, label);
                }

                return TryParseXml(combined, label);
            }

            Say($"  [{label}] XRecord contained no recognisable data.");
            return null;
        }

        // ═════════════════════════════════════════════════════════════════════
        // Byte-level operations
        // ═════════════════════════════════════════════════════════════════════

        private static byte[] Reassemble(List<byte[]> chunks)
        {
            long total = 0;
            foreach (byte[] c in chunks) total += c.Length;
            byte[] buf = new byte[total];
            int    pos = 0;
            foreach (byte[] c in chunks) { Buffer.BlockCopy(c, 0, buf, pos, c.Length); pos += c.Length; }
            return buf;
        }

        private byte[] TryDecompress(byte[] data, string label)
        {
            // ── GZip (magic 1F 8B) ────────────────────────────────────────────
            if (data.Length > 2 && data[0] == 0x1F && data[1] == 0x8B)
            {
                Say($"  [{label}] GZip magic detected → decompressing…");
                try
                {
                    using (var ms  = new MemoryStream(data))
                    using (var gz  = new GZipStream(ms, CompressionMode.Decompress))
                    using (var out2 = new MemoryStream())
                    {
                        gz.CopyTo(out2);
                        byte[] result = out2.ToArray();
                        Say($"  [{label}] GZip decompressed: {data.Length}B → {result.Length}B");
                        return result;
                    }
                }
                catch (System.Exception ex)
                {
                    Say($"  [{label}] GZip decompression FAILED: {ex.Message}");
                }
            }

            // ── Deflate (raw, no header) ──────────────────────────────────────
            try
            {
                using (var ms  = new MemoryStream(data))
                using (var df  = new DeflateStream(ms, CompressionMode.Decompress))
                using (var out2 = new MemoryStream())
                {
                    df.CopyTo(out2);
                    byte[] result = out2.ToArray();
                    if (result.Length > data.Length)
                    {
                        Say($"  [{label}] Deflate decompressed: {data.Length}B → {result.Length}B");
                        return result;
                    }
                }
            }
            catch { }

            Say($"  [{label}] Data is not compressed (or unknown compression). Using as-is.");
            return null;   // caller uses original
        }

        private static string TryDecodeToString(byte[] data)
        {
            if (data == null || data.Length == 0) return null;

            // UTF-8 (most common for XML)
            try
            {
                string s = new UTF8Encoding(false, true).GetString(data);
                if (s.TrimStart().StartsWith("<")) return s;
            }
            catch { }

            // UTF-16 LE
            if (data.Length % 2 == 0)
            {
                try
                {
                    // Check for BOM or null-interleaved pattern
                    bool hasNulls = false;
                    for (int i = 1; i < Math.Min(data.Length, 20); i += 2)
                        if (data[i] == 0) { hasNulls = true; break; }

                    if (hasNulls || (data[0] == 0xFF && data[1] == 0xFE))
                    {
                        string s = Encoding.Unicode.GetString(data);
                        if (s.TrimStart().StartsWith("<")) return s;
                    }
                }
                catch { }
            }

            // UTF-8 without strict validation (last resort)
            try
            {
                string s = Encoding.UTF8.GetString(data);
                if (ContainsPrintable(s, 0.80)) return s;
            }
            catch { }

            return null;
        }

        private static byte[] TryBase64Decode(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            string cleaned = s.Trim();
            // Pad if needed
            int mod = cleaned.Length % 4;
            if (mod == 2) cleaned += "==";
            else if (mod == 3) cleaned += "=";
            try   { return Convert.FromBase64String(cleaned); }
            catch { return null; }
        }

        // ═════════════════════════════════════════════════════════════════════
        // XML parsing and pretty-printing
        // ═════════════════════════════════════════════════════════════════════

        private XDocument TryParseXml(string xml, string label)
        {
            // Strip BOM if present
            if (xml.Length > 0 && xml[0] == '﻿') xml = xml.Substring(1);

            try
            {
                XDocument doc = XDocument.Parse(xml, LoadOptions.None);
                Say($"  [{label}] XML parsed successfully.");
                return doc;
            }
            catch (System.Exception ex)
            {
                Say($"  [{label}] XML parse failed: {ex.Message}");
                Say($"  First 300 chars: {(xml.Length > 300 ? xml.Substring(0, 300) : xml)}");

                // Save raw string for manual inspection
                string rawPath = Path.Combine(Desktop, $"Urbano_{SanitizeKey(label)}_RAW.txt");
                try
                {
                    File.WriteAllText(rawPath, xml, Encoding.UTF8);
                    Say($"  Raw string saved → {rawPath}");
                }
                catch { }
                return null;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Save XML to Desktop
        // ═════════════════════════════════════════════════════════════════════

        private string SaveXml(XDocument doc, string dictKey)
        {
            string fileName = $"Urbano_{SanitizeKey(dictKey)}.xml";
            string path     = Path.Combine(Desktop, fileName);

            var settings = new XmlWriterSettings
            {
                Indent             = true,
                IndentChars        = "  ",
                NewLineOnAttributes = false,
                Encoding           = new UTF8Encoding(false),   // UTF-8 without BOM
                OmitXmlDeclaration = false,
            };

            using (var writer = XmlWriter.Create(path, settings))
                doc.Save(writer);

            long bytes = new FileInfo(path).Length;
            Say($"  Saved {bytes:#,0} bytes → {path}");
            return path;
        }

        // ═════════════════════════════════════════════════════════════════════
        // XML structural analysis + GUID lookup
        // ═════════════════════════════════════════════════════════════════════

        private void AnalyseXml(XDocument doc, string label, string searchGuid)
        {
            if (doc?.Root == null) return;

            Say($"\n  ── XML Analysis: [{label}] ──────────────────────────────");

            XElement root = doc.Root;
            Say($"  Root element   : <{root.Name.LocalName}>");
            if (!string.IsNullOrEmpty(root.Name.NamespaceName))
                Say($"  Namespace      : {root.Name.NamespaceName}");

            // Top-level attributes on root
            foreach (XAttribute a in root.Attributes())
                Say($"  Root attr      : {a.Name.LocalName}=\"{TruncStr(a.Value, 80)}\"");

            // Count direct child element types
            var childCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement child in root.Elements())
            {
                string n = child.Name.LocalName;
                if (!childCounts.ContainsKey(n)) childCounts[n] = 0;
                childCounts[n]++;
            }

            Say($"  Direct children: {new List<int>(childCounts.Values).Count} unique element type(s)");
            foreach (var kv in childCounts)
                Say($"    <{kv.Key}>  ×{kv.Value}");

            // Show attributes of the first child of each type (schema discovery)
            var shown = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (XElement child in root.Elements())
            {
                if (shown.Contains(child.Name.LocalName)) continue;
                shown.Add(child.Name.LocalName);

                Say($"\n  Sample <{child.Name.LocalName}> attributes:");
                bool hasAttrs = false;
                foreach (XAttribute a in child.Attributes())
                {
                    Say($"    {a.Name.LocalName,-35} = \"{TruncStr(a.Value, 60)}\"");
                    hasAttrs = true;
                }
                if (!hasAttrs) Say("    (no attributes — check child elements)");

                // Check first-level grandchildren too
                foreach (XElement gc in child.Elements())
                {
                    Say($"    child <{gc.Name.LocalName}>:");
                    foreach (XAttribute a in gc.Attributes())
                        Say($"      {a.Name.LocalName,-33} = \"{TruncStr(a.Value, 60)}\"");
                }

                if (shown.Count >= 3) break;   // limit schema preview to 3 types
            }

            // ── GUID lookup ───────────────────────────────────────────────────
            if (string.IsNullOrEmpty(searchGuid)) return;

            Say($"\n  ── GUID Lookup: {searchGuid} ──────────────────────────");

            bool foundAny = false;
            string guidLo = searchGuid.ToLowerInvariant();

            // Walk every element in the document, check all attributes and text
            foreach (XElement el in doc.Descendants())
            {
                bool hit = false;

                // Check attributes
                foreach (XAttribute a in el.Attributes())
                {
                    if (a.Value.IndexOf(guidLo, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        hit = true; break;
                    }
                }

                // Check element text content
                if (!hit && el.Value != null &&
                    el.Value.IndexOf(guidLo, StringComparison.OrdinalIgnoreCase) >= 0)
                    hit = true;

                if (!hit) continue;

                foundAny = true;
                Say($"\n  *** GUID MATCH *** in [{label}]");
                Say($"  Element : <{el.Name.LocalName}>");
                Say($"  XPath   : {GetPath(el)}");
                Say($"  ALL ATTRIBUTES ON THIS ELEMENT (this is your BoQ property map):");

                foreach (XAttribute a in el.Attributes())
                {
                    string marker = a.Value.IndexOf(guidLo, StringComparison.OrdinalIgnoreCase) >= 0
                                    ? "  ◄ GUID HERE"
                                    : "";
                    Say($"    {a.Name.LocalName,-40} = \"{TruncStr(a.Value, 80)}\"{marker}");
                }

                // Sibling attributes that look like quantities
                Say($"\n  SIBLING ELEMENTS (same parent):");
                if (el.Parent != null)
                {
                    foreach (XElement sib in el.Parent.Elements())
                    {
                        Say($"    <{sib.Name.LocalName}>");
                        foreach (XAttribute a in sib.Attributes())
                            Say($"      {a.Name.LocalName,-38} = \"{TruncStr(a.Value, 60)}\"");
                    }
                }

                Say($"\n  CHILD ELEMENTS:");
                foreach (XElement child in el.Elements())
                {
                    Say($"    <{child.Name.LocalName}>");
                    foreach (XAttribute a in child.Attributes())
                        Say($"      {a.Name.LocalName,-38} = \"{TruncStr(a.Value, 60)}\"");
                }

                break;   // one detailed hit is enough; user can search the file for more
            }

            if (!foundAny)
            {
                Say($"  GUID \"{searchGuid}\" NOT found in [{label}].");
                Say("  Possible reasons:");
                Say("    • The GUID is in a different dictionary (check ARSX_AUXTOPOLOGY).");
                Say("    • The GUID uses a different attribute name (e.g. 'id', 'guid', 'topo').");
                Say("    • Run: grep -i \"" + searchGuid.Substring(0, 8) + "\" Urbano_ARSX_NETWORKTOPOLOGY.xml");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Utilities
        // ═════════════════════════════════════════════════════════════════════

        private static string GetPath(XElement el)
        {
            var parts = new List<string>();
            XElement cur = el;
            while (cur != null && cur.Parent != null)
            {
                // Count same-name siblings before this one for an index
                int idx = 1;
                foreach (XElement sib in cur.Parent.Elements(cur.Name))
                {
                    if (sib == cur) break;
                    idx++;
                }
                parts.Add($"{cur.Name.LocalName}[{idx}]");
                cur = cur.Parent;
            }
            if (cur != null) parts.Add(cur.Name.LocalName);
            parts.Reverse();
            return "/" + string.Join("/", parts);
        }

        private static string HexPreview(byte[] b, int n)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < Math.Min(b.Length, n); i++)
                sb.Append(b[i].ToString("X2")).Append(' ');
            if (b.Length > n) sb.Append($"(+{b.Length - n}B)");
            return sb.ToString().TrimEnd();
        }

        private static bool ContainsPrintable(string s, double threshold)
        {
            if (string.IsNullOrEmpty(s)) return false;
            int good = 0;
            foreach (char c in s)
                if (c >= 0x20 || c == '\t' || c == '\r' || c == '\n') good++;
            return (double)good / s.Length >= threshold;
        }

        private static string SanitizeKey(string key)
            => key.Replace('/', '_').Replace('\\', '_').Replace(':', '_')
                  .Replace('*', '_').Replace('?', '_').Replace('"', '_')
                  .Replace('<', '_').Replace('>', '_').Replace('|', '_');

        private static string TruncStr(string s, int max)
            => s != null && s.Length > max ? s.Substring(0, max) + "…" : s ?? "";

        private static string Bar(char c, int n) => new string(c, n);

        private static string Desktop
            => Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        private void Say(string msg)
        {
            string cl = msg.Length > 200 ? msg.Substring(0, 197) + "…" : msg;
            _ed.WriteMessage("\n" + cl);
        }
    }
}
