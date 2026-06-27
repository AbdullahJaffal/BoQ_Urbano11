// ═══════════════════════════════════════════════════════════════════════════════
// UrbanoEntitySnoop.cs
//
// Command : SnoopUrbanoData
//
// PURPOSE
// ───────
// Interactive single-entity data snooper.  Select any entity (pipe, manhole,
// label, block reference …) and this command exhaustively dumps:
//
//   § 1  Basic metadata        — .NET type, DXF class, handle, layer, colour …
//   § 2  XData                 — every registered-app group, raw TypedValues,
//                                binary chunks decoded (GZip→UTF-8, UTF-8, UTF-16)
//   § 3  Extension Dictionary  — fully recursive; XRecords decoded as § 2
//   § 4  Civil 3D Property Sets— via Autodesk.Aec.PropertyData (AecBaseMgd.dll)
//   § 5  .NET Reflection dump  — all readable public properties on the entity class
//
// Output: concise summary to the AutoCAD command line PLUS a full verbose report
// written to Desktop\UrbanoSnoop_<handle>.txt  (no AutoCAD line-length limit).
//
// REFERENCES USED
//   acdbmgd.dll      – Database, Transaction, XRecord, DBDictionary, TypedValue …
//   acmgd.dll        – Application, Document
//   accoremgd.dll    – Editor, PromptEntityOptions
//   AecBaseMgd.dll   – PropertyDataServices, PropertySet (Civil 3D standard)
//
// NOTE: AecBaseMgd usage is wrapped in try/catch so the command still works when
// loaded into plain AutoCAD (no Civil 3D) — the property-set section is simply
// skipped.
// ═══════════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;

// Register this class so AutoCAD picks up [CommandMethod] attributes.
[assembly: CommandClass(typeof(UrbanoMetraj.UrbanoEntitySnoop))]

namespace UrbanoMetraj
{
    public class UrbanoEntitySnoop
    {
        // ── Tuning ────────────────────────────────────────────────────────────
        private const int MaxDictDepth   = 12;    // dictionary recursion guard
        private const int MaxOidFollow   = 3;     // depth to chase ObjectId refs
        private const int MaxHexBytes    = 256;   // hex preview length per binary chunk
        private const int MaxStringChars = 6000;  // inline string/XML preview
        private const int MaxReflProps   = 80;    // max .NET properties printed

        // ObjectIds already visited (cycle / re-entry guard)
        private readonly HashSet<long> _seen = new HashSet<long>();

        // Shared output: both the command-line writer and the file writer
        private Editor       _ed;
        private StreamWriter _file;

        // ═════════════════════════════════════════════════════════════════════
        // COMMAND ENTRY POINT
        // ═════════════════════════════════════════════════════════════════════

        [CommandMethod("SnoopUrbanoData")]
        public void SnoopUrbanoData()
        {
            _seen.Clear();

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Database db  = doc.Database;
            _ed          = doc.Editor;

            // ── Entity selection ──────────────────────────────────────────────
            var peo = new PromptEntityOptions(
                "\nSnoopUrbanoData — Select an entity (pipe, manhole, label, block…): ");
            peo.AllowNone = false;
            PromptEntityResult per = _ed.GetEntity(peo);

            if (per.Status != PromptStatus.OK)
            {
                _ed.WriteMessage("\nNo entity selected — command cancelled.\n");
                return;
            }

            using (Transaction tr = db.TransactionManager.StartTransaction())
            {
                try
                {
                    Entity ent = tr.GetObject(per.ObjectId, OpenMode.ForRead) as Entity;
                    if (ent == null)
                    {
                        _ed.WriteMessage("\nCould not open entity — aborted.\n");
                        tr.Commit();
                        return;
                    }

                    // ── Output file ───────────────────────────────────────────
                    string outPath = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                        $"UrbanoSnoop_{ent.Handle}.txt");

                    using (_file = new StreamWriter(outPath, false, new UTF8Encoding(true)))
                    {
                        _seen.Add(ent.ObjectId.OldIdPtr.ToInt64());

                        FileHeader(db, ent);

                        DumpSection("§ 1  BASIC ENTITY METADATA",       () => DumpBasicInfo(tr, ent));
                        DumpSection("§ 2  XDATA  (all registered apps)", () => DumpXData(db, tr, ent));
                        DumpSection("§ 3  EXTENSION DICTIONARY",         () => DumpExtDict(tr, ent));
                        DumpSection("§ 4  CIVIL 3D PROPERTY SETS",       () => DumpPropertySets(tr, ent));
                        DumpSection("§ 5  .NET REFLECTION  (entity class properties)",
                                                                         () => DumpReflection(ent));

                        Footer();
                    }

                    _ed.WriteMessage($"\nFull report → {outPath}\n");
                }
                catch (System.Exception ex)
                {
                    _ed.WriteMessage($"\n[FATAL] {ex.GetType().Name}: {ex.Message}\n");
                }
                finally
                {
                    tr.Commit();
                }
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // § 1  BASIC INFO
        // ═════════════════════════════════════════════════════════════════════

        private void DumpBasicInfo(Transaction tr, Entity ent)
        {
            RXClass rx = ent.GetRXClass();

            Print($"  .NET Type      : {ent.GetType().FullName}");
            Print($"  Assembly       : {ent.GetType().Assembly.GetName().Name}");
            Print($"  RXClass Name   : {rx?.Name ?? "?"}");
            Print($"  DXF Class Name : {rx?.DxfName ?? "?"}");
            Print($"  Handle         : {ent.Handle}");
            Print($"  ObjectId       : {ent.ObjectId}");
            Print($"  Layer          : {ent.Layer}");
            Print($"  Color          : {ent.ColorIndex}");
            Print($"  Linetype       : {ent.Linetype}");
            Print($"  LineWeight     : {ent.LineWeight}");
            Print($"  IsProxy        : {(rx?.Name?.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0 ? "YES ⚠" : "no")}");
            Print($"  HasXData       : {ent.XData != null}");
            Print($"  HasExtDict     : {ent.ExtensionDictionary != ObjectId.Null}");

            // Owner chain (useful for block references)
            try
            {
                ObjectId ownerId = ent.OwnerId;
                if (!ownerId.IsNull)
                {
                    DBObject owner = tr.GetObject(ownerId, OpenMode.ForRead);
                    Print($"  Owner Type     : {owner.GetRXClass()?.Name ?? "?"}  Handle:{owner.Handle}");
                }
            }
            catch { }

            // Extra info for specific well-known types
            if (ent is BlockReference br)
            {
                Print($"  Block Name     : {br.Name}");
                Print($"  Position       : {br.Position}");
                Print($"  Scale          : {br.ScaleFactors}");
                Print($"  Rotation (rad) : {br.Rotation:F6}");
                Print($"  IsDynamic      : {br.IsDynamicBlock}");
                if (br.IsDynamicBlock)
                    Print($"  DynBlockDef    : {br.DynamicBlockTableRecord}");
            }
            else if (ent is DBText txt)
            {
                Print($"  TextString     : {txt.TextString}");
                Print($"  Position       : {txt.Position}");
                Print($"  Height         : {txt.Height:F4}");
            }
            else if (ent is MText mt)
            {
                Print($"  MText Contents : {mt.Contents}");
                Print($"  Location       : {mt.Location}");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // § 2  XDATA
        // ═════════════════════════════════════════════════════════════════════

        private void DumpXData(Database db, Transaction tr, Entity ent)
        {
            // Collect all registered app names in the DWG
            var appNames = new List<string>();
            var rat = tr.GetObject(db.RegAppTableId, OpenMode.ForRead) as RegAppTable;
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

            Print($"  Total registered apps in DWG: {appNames.Count}");

            // --- Strategy A: per-app query (most reliable)
            int appCount = 0;
            foreach (string app in appNames)
            {
                using (ResultBuffer rb = ent.GetXDataForApplication(app))
                {
                    if (rb == null) continue;
                    appCount++;
                    PrintBlank();
                    Print($"  ┌─ RegApp: \"{app}\"");
                    DumpResultBuffer(tr, rb, "  │  ", MaxOidFollow);
                    Print($"  └─ (end of \"{app}\")");
                }
            }

            // --- Strategy B: full XData blob (catches anything Strategy A misses)
            using (ResultBuffer allRb = ent.XData)
            {
                if (allRb != null && appCount == 0)
                {
                    Print("  (No per-app match — dumping raw combined XData blob)");
                    DumpResultBuffer(tr, allRb, "    ", MaxOidFollow);
                }
            }

            if (appCount == 0)
                Print("  (No XData found on this entity)");
            else
                Print($"\n  Total XData RegApp groups found: {appCount}");
        }

        // ═════════════════════════════════════════════════════════════════════
        // § 3  EXTENSION DICTIONARY
        // ═════════════════════════════════════════════════════════════════════

        private void DumpExtDict(Transaction tr, Entity ent)
        {
            if (ent.ExtensionDictionary == ObjectId.Null)
            {
                Print("  (Entity has no extension dictionary)");
                return;
            }

            try
            {
                DBDictionary extDict =
                    tr.GetObject(ent.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;

                if (extDict == null)
                {
                    Print("  (ExtensionDictionary ObjectId exists but object is null)");
                    return;
                }

                Print($"  Root extension dict — Handle:{extDict.Handle}  Count:{extDict.Count}");
                DumpDictionary(tr, extDict, "  ", 0);
            }
            catch (System.Exception ex)
            {
                Print($"  [ERROR opening extension dictionary: {ex.Message}]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // § 4  CIVIL 3D PROPERTY SETS  (AecBaseMgd — conditional)
        // ═════════════════════════════════════════════════════════════════════

        private void DumpPropertySets(Transaction tr, Entity ent)
        {
            // We use full reflection so this compiles even without AecBaseMgd
            // and gracefully skips if the assembly is not loaded.
            try
            {
                Type pdsType = Type.GetType(
                    "Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices, AecBaseMgd",
                    false, false);

                if (pdsType == null)
                {
                    // Try loaded assemblies (Civil 3D may already be in the AppDomain)
                    foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name.IndexOf("AecBase", StringComparison.OrdinalIgnoreCase) < 0)
                            continue;
                        pdsType = asm.GetType(
                            "Autodesk.Aec.PropertyData.DatabaseServices.PropertyDataServices",
                            false, false);
                        if (pdsType != null) break;
                    }
                }

                if (pdsType == null)
                {
                    Print("  (AecBaseMgd not loaded — Civil 3D Property Sets not available)");
                    Print("  Tip: Run from within Civil 3D, not plain AutoCAD.");
                    return;
                }

                // PropertyDataServices.GetPropertySets(ObjectId) → ObjectIdCollection
                MethodInfo getPS = pdsType.GetMethod("GetPropertySets",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(ObjectId) },
                    null);

                if (getPS == null)
                {
                    Print("  (Could not find PropertyDataServices.GetPropertySets method)");
                    return;
                }

                var psIds = getPS.Invoke(null, new object[] { ent.ObjectId })
                            as System.Collections.IEnumerable;

                if (psIds == null)
                {
                    Print("  (GetPropertySets returned null)");
                    return;
                }

                int psCount = 0;
                foreach (ObjectId psId in psIds)
                {
                    psCount++;
                    try
                    {
                        DBObject psObj = tr.GetObject(psId, OpenMode.ForRead);
                        Type psType   = psObj.GetType();

                        // PropertySet.PropertySetDefinitionName
                        string defName = psType.GetProperty("PropertySetDefinitionName")
                                                ?.GetValue(psObj)?.ToString() ?? "?";

                        PrintBlank();
                        Print($"  ┌─ PropertySet #{psCount}  Definition: \"{defName}\"");
                        Print($"  │  Handle  : {psObj.Handle}");
                        Print($"  │  .NET Type: {psType.FullName}");

                        // PropertySet.PropertyNameList → IList<string>
                        var nameList = psType.GetProperty("PropertyNameList")
                                             ?.GetValue(psObj) as System.Collections.IEnumerable;

                        if (nameList != null)
                        {
                            // PropertySet.PropertyNameToId(name) → int
                            MethodInfo nameToId = psType.GetMethod("PropertyNameToId");
                            // PropertySet.GetAt(int) → object
                            MethodInfo getAt = psType.GetMethod("GetAt", new[] { typeof(int) });

                            foreach (string propName in nameList)
                            {
                                try
                                {
                                    string val = "?";
                                    if (nameToId != null && getAt != null)
                                    {
                                        int pid = (int)nameToId.Invoke(psObj, new object[] { propName });
                                        object v = getAt.Invoke(psObj, new object[] { pid });
                                        val = v?.ToString() ?? "(null)";
                                    }
                                    Print($"  │    {propName,-40} = {val}");
                                }
                                catch (System.Exception ex2)
                                {
                                    string em = ex2.InnerException != null ? ex2.InnerException.Message : ex2.Message;
                                    Print("  │    " + propName.PadRight(40) + " = [error: " + em + "]");
                                }
                            }
                        }
                        else
                        {
                            Print("  │  (PropertyNameList not accessible)");
                        }

                        Print($"  └─ (end of PropertySet \"{defName}\")");
                    }
                    catch (System.Exception ex)
                    {
                        Print($"  [ERROR reading PropertySet #{psCount}: {ex.Message}]");
                    }
                }

                if (psCount == 0)
                    Print("  (No Civil 3D Property Sets attached to this entity)");
                else
                    Print($"\n  Total Property Sets found: {psCount}");
            }
            catch (System.Exception ex)
            {
                Print($"  [ERROR in DumpPropertySets: {ex.Message}]");
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // § 5  .NET REFLECTION
        // ═════════════════════════════════════════════════════════════════════

        private void DumpReflection(Entity ent)
        {
            Type type = ent.GetType();
            Print($"  Type : {type.FullName}");
            Print($"  Asm  : {type.Assembly.Location}");

            PropertyInfo[] props = type.GetProperties(
                BindingFlags.Public | BindingFlags.Instance);

            Print($"  Scanning {props.Length} public properties…");
            PrintBlank();

            int printed = 0;
            foreach (PropertyInfo pi in props)
            {
                if (printed >= MaxReflProps) { Print("  … (limit reached — increase MaxReflProps)"); break; }
                if (!pi.CanRead)                      continue;
                if (pi.GetIndexParameters().Length > 0) continue;

                // Only print primitive-ish types; skip ObjectId, collections, etc.
                Type pt = pi.PropertyType;
                bool interesting =
                    pt == typeof(string)  || pt == typeof(double) || pt == typeof(float) ||
                    pt == typeof(int)     || pt == typeof(long)   || pt == typeof(bool)  ||
                    pt == typeof(short)   || pt == typeof(byte)   || pt.IsEnum           ||
                    pt.FullName?.StartsWith("Autodesk.AutoCAD.Geometry") == true         ||
                    pt.FullName?.StartsWith("Autodesk.AutoCAD.Colors")   == true;

                if (!interesting) continue;

                try
                {
                    object val = pi.GetValue(ent);
                    if (val == null) continue;
                    string s = val.ToString();
                    if (s.Length > 200) s = s.Substring(0, 197) + "…";
                    Print($"  {pi.Name,-42} = {s}");
                    printed++;
                }
                catch { /* some properties throw; skip them */ }
            }

            if (printed == 0)
                Print("  (No primitive-typed public properties returned values)");
        }

        // ═════════════════════════════════════════════════════════════════════
        // Dictionary traversal
        // ═════════════════════════════════════════════════════════════════════

        private void DumpDictionary(Transaction tr, DBDictionary dict,
                                     string indent, int depth)
        {
            if (dict == null) return;
            if (depth > MaxDictDepth)
            {
                Print($"{indent}⚠ MAX RECURSION DEPTH ({MaxDictDepth}) — stopping");
                return;
            }

            foreach (DBDictionaryEntry entry in dict)
            {
                PrintBlank();
                Print($"{indent}┌─ Key: \"{entry.Key}\"");

                if (entry.Value.IsNull)
                {
                    Print($"{indent}│  (null ObjectId)");
                    Print($"{indent}└─");
                    continue;
                }

                long oidInt = entry.Value.OldIdPtr.ToInt64();
                if (_seen.Contains(oidInt))
                {
                    Print($"{indent}│  (already visited — cycle guard)");
                    Print($"{indent}└─");
                    continue;
                }
                _seen.Add(oidInt);

                try
                {
                    DBObject obj = tr.GetObject(entry.Value, OpenMode.ForRead);
                    DumpDbObject(tr, obj, indent + "│  ", depth + 1);
                }
                catch (System.Exception ex)
                {
                    Print($"{indent}│  [ERROR: {ex.GetType().Name}: {ex.Message}]");
                }

                Print($"{indent}└─");
            }
        }

        private void DumpDbObject(Transaction tr, DBObject obj,
                                   string indent, int depth)
        {
            if (obj == null) { Print($"{indent}(null object)"); return; }

            string cls = obj.GetRXClass()?.Name ?? "?";
            string dxf = obj.GetRXClass()?.DxfName ?? "?";
            bool isProxy = cls.IndexOf("Proxy", StringComparison.OrdinalIgnoreCase) >= 0;

            Print($"{indent}Type   : {cls}  (DXF: {dxf}){(isProxy ? "  ⚠ PROXY" : "")}");
            Print($"{indent}Handle : {obj.Handle}");

            switch (obj)
            {
                case DBDictionary subDict:
                    Print($"{indent}(Nested dictionary — {subDict.Count} entries)");
                    DumpDictionary(tr, subDict, indent, depth);
                    break;

                case Xrecord xrec:
                    DumpXRecord(tr, xrec, indent, MaxOidFollow);
                    break;

                default:
                    // Unknown type — dump its XData and ExtDict if present
                    DumpGenericObject(tr, obj, indent, depth);
                    break;
            }
        }

        private void DumpXRecord(Transaction tr, Xrecord xrec,
                                  string indent, int oidBudget)
        {
            if (xrec.Data == null) { Print($"{indent}(empty XRecord)"); return; }

            var accumBin = new List<byte>();
            int idx      = 0;

            foreach (TypedValue tv in xrec.Data)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (val is byte[] bytes)
                {
                    accumBin.AddRange(bytes);
                    Print($"{indent}  [{idx:D3}] code={code,5}  BINARY  {bytes.Length} bytes");
                    Print($"{indent}         Hex : {HexPreview(bytes)}");
                    string dec = TryDecode(bytes);
                    if (dec != null)
                        Print($"{indent}         Dec : {Truncate(dec, MaxStringChars)}");
                }
                else if (IsOidCode(code))
                {
                    Print($"{indent}  [{idx:D3}] code={code,5}  ObjectId = {val}");
                    if (oidBudget > 0 && val is ObjectId oid && !oid.IsNull)
                    {
                        long k = oid.OldIdPtr.ToInt64();
                        if (!_seen.Contains(k))
                        {
                            _seen.Add(k);
                            try
                            {
                                DBObject refObj = tr.GetObject(oid, OpenMode.ForRead);
                                Print($"{indent}         ↳ following OID ref:");
                                DumpDbObject(tr, refObj, indent + "           ", 0);
                            }
                            catch (System.Exception ex)
                            {
                                Print($"{indent}         ↳ FAILED: {ex.Message}");
                            }
                        }
                        else
                        {
                            Print($"{indent}         (already visited)");
                        }
                    }
                }
                else
                {
                    string strVal = Truncate(val?.ToString() ?? "(null)", MaxStringChars);
                    string dxfLabel = SafeDxfLabel(code);
                    Print($"{indent}  [{idx:D3}] code={code,5}  {dxfLabel,-28}  {strVal}");

                    if (strVal.TrimStart().StartsWith("<") && strVal.Contains(">"))
                        Print($"{indent}         ^ looks like XML ({strVal.Length} chars)");
                }
                idx++;
            }

            // If we saw multiple binary chunks, try concatenated decode
            if (accumBin.Count > 0 && idx > 1)
            {
                string consol = TryDecode(accumBin.ToArray());
                if (consol != null)
                {
                    Print($"{indent}  [consolidated {accumBin.Count}B from {idx} chunks]");
                    Print($"{indent}  Dec : {Truncate(consol, MaxStringChars)}");
                }
            }
        }

        private void DumpGenericObject(Transaction tr, DBObject obj,
                                        string indent, int depth)
        {
            // XData on the object
            using (ResultBuffer rb = obj.XData)
            {
                if (rb != null)
                {
                    Print($"{indent}XData:");
                    DumpResultBuffer(tr, rb, indent + "  ", MaxOidFollow - 1);
                }
            }

            // Extension dictionary on the object
            if (!obj.ExtensionDictionary.IsNull)
            {
                try
                {
                    Print($"{indent}ExtensionDictionary:");
                    var ed2 = tr.GetObject(obj.ExtensionDictionary, OpenMode.ForRead) as DBDictionary;
                    if (ed2 != null)
                        DumpDictionary(tr, ed2, indent + "  ", depth);
                }
                catch (System.Exception ex)
                {
                    Print($"{indent}  [ERROR: {ex.Message}]");
                }
            }
        }

        // Used for ResultBuffers (XData blobs, not XRecords)
        private void DumpResultBuffer(Transaction tr, ResultBuffer rb,
                                       string indent, int oidBudget)
        {
            int idx = 0;
            string currentApp = null;

            foreach (TypedValue tv in rb)
            {
                int    code = tv.TypeCode;
                object val  = tv.Value;

                if (code == (int)DxfCode.ExtendedDataRegAppName)
                {
                    currentApp = val?.ToString();
                    Print($"{indent}[RegApp: \"{currentApp}\"]");
                    idx = 0;
                    continue;
                }

                string pfx = $"{indent}  [{idx:D3}]";

                if (val is byte[] bytes)
                {
                    Print($"{pfx} code={code,5}  BINARY {bytes.Length}B  Hex:{HexPreview(bytes)}");
                    string dec = TryDecode(bytes);
                    if (dec != null) Print($"{indent}        Dec:{Truncate(dec, MaxStringChars)}");
                }
                else if (IsOidCode(code) && val is ObjectId oid && !oid.IsNull
                         && oidBudget > 0)
                {
                    long k = oid.OldIdPtr.ToInt64();
                    Print($"{pfx} code={code,5}  ObjectId = {val}");
                    if (!_seen.Contains(k))
                    {
                        _seen.Add(k);
                        try
                        {
                            DBObject refObj = tr.GetObject(oid, OpenMode.ForRead);
                            DumpDbObject(tr, refObj, indent + "        ", 0);
                        }
                        catch (System.Exception ex) { Print($"{indent}        FAILED: {ex.Message}"); }
                    }
                }
                else
                {
                    Print($"{pfx} code={code,5}  {SafeDxfLabel(code),-26}  {Truncate(val?.ToString() ?? "(null)", 300)}");
                }
                idx++;
            }
        }

        // ═════════════════════════════════════════════════════════════════════
        // Helpers
        // ═════════════════════════════════════════════════════════════════════

        /// <summary>Multi-strategy binary decode: GZip→UTF8, Deflate→UTF8, UTF8, UTF16-LE, ASCII.</summary>
        private static string TryDecode(byte[] b)
        {
            if (b == null || b.Length == 0) return null;

            // GZip (magic 1F 8B)
            if (b.Length > 2 && b[0] == 0x1F && b[1] == 0x8B)
            {
                try
                {
                    using (var ms  = new MemoryStream(b))
                    using (var gz  = new GZipStream(ms, CompressionMode.Decompress))
                    using (var o   = new MemoryStream())
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
                using (var ms  = new MemoryStream(b))
                using (var df  = new DeflateStream(ms, CompressionMode.Decompress))
                using (var o   = new MemoryStream())
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

            // UTF-8
            { string s = SafeUtf8(b); if (s != null && IsPrintable(s)) return "[UTF8] " + s; }

            // UTF-16 LE (look for null-interleaved bytes)
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

        private static bool IsOidCode(int code)
            => code == 5 || code == 320 || (code >= 330 && code <= 399);

        private static string HexPreview(byte[] b)
        {
            int   n  = Math.Min(b.Length, MaxHexBytes);
            var   sb = new StringBuilder(n * 3);
            for (int i = 0; i < n; i++)
            {
                if (i > 0 && i % 16 == 0) sb.Append(' ');
                sb.Append(b[i].ToString("X2")).Append(' ');
            }
            if (b.Length > MaxHexBytes) sb.Append($"(+{b.Length - MaxHexBytes}B more)");
            return sb.ToString().TrimEnd();
        }

        private static string SafeDxfLabel(int code)
        {
            try   { return ((DxfCode)code).ToString(); }
            catch { return $"(code {code})"; }
        }

        private static string Truncate(string s, int max)
            => s != null && s.Length > max
               ? s.Substring(0, max) + $"… (+{s.Length - max} chars)"
               : s ?? "";

        // ── Output helpers ────────────────────────────────────────────────────

        // Writes to both the command line (trimmed) and the file (verbatim).
        private void Print(string line)
        {
            _file?.WriteLine(line);

            // AutoCAD command line: keep lines short and sensible
            string clLine = line.Length > 180 ? line.Substring(0, 177) + "…" : line;
            _ed.WriteMessage("\n" + clLine);
        }

        private void PrintBlank() => Print("");

        private void DumpSection(string title, Action body)
        {
            string bar = new string('═', 70);
            Print("");
            Print(bar);
            Print($"  {title}");
            Print(bar);
            try   { body(); }
            catch (System.Exception ex) { Print("  [SECTION ERROR: " + ex.Message + "]"); }
        }

        private void FileHeader(Database db, Entity ent)
        {
            string bar = new string('═', 70);
            Print(bar);
            Print("  URBANO ENTITY SNOOP — complete data dump");
            Print(bar);
            Print($"  DWG    : {db.Filename}");
            Print($"  Date   : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            Print($"  Entity : {ent.GetRXClass()?.Name ?? "?"}  Handle:{ent.Handle}");
            Print(bar);
        }

        private void Footer()
        {
            string bar = new string('═', 70);
            PrintBlank();
            Print(bar);
            Print("  SNOOP COMPLETE");
            Print(bar);
        }
    }
}
