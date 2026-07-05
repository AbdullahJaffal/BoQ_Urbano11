using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.TypeMapping.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.TypeMapping.Services
{
    /// <summary>
    /// Persists <see cref="PipeTypeLink"/>/<see cref="ManholeTypeLink"/> inside the
    /// active DWG Named Object Dictionary. Mirrors
    /// SmartAssembly.Serialization.ProjectTemplateNodManager's pattern exactly
    /// (self-contained low-level helpers, surgical — never touches other URBANO_BOQ
    /// branches).
    ///
    /// NOD layout:
    /// <code>
    /// NOD["URBANO_BOQ"]
    ///   "TYPE_MAPPING"                (DBDictionary)
    ///     "PIPE_LINKS"                (DBDictionary)
    ///       [SafeKey(UrbanoGuid)]     (XRecord: UrbanoGuid · UrbanoName · LinkedPipeDefinitionId)
    ///     "MANHOLE_LINKS"             (DBDictionary)
    ///       [SafeKey(UrbanoGuid)]     (XRecord: UrbanoGuid · UrbanoName · DiameterMode)
    ///     "DISCOVERED_ITEMS"          (DBDictionary)
    ///       [SafeKey(UrbanoGuid)]     (XRecord: UrbanoGuid · UrbanoName · Reference)
    /// </code>
    ///
    /// IMPORTANT: DwgBoQStore.Save preserves "TYPE_MAPPING" explicitly (see its
    /// `preserve` HashSet) so PIPE_LINKS/MANHOLE_LINKS survive every "Metraj Verisi
    /// Güncelle" re-save untouched. DISCOVERED_ITEMS is the one exception — it is
    /// explicitly overwritten by SaveDiscoveredItems, called right after
    /// DwgBoQStore.Save on every successful export, so the Type Mapping UI's "Urbano
    /// side" listing lives entirely inside the DWG (portable to another machine)
    /// instead of depending on the ephemeral %TEMP% export file.
    /// </summary>
    public static class TypeMappingNodManager
    {
        private const string NOD_ROOT      = "URBANO_BOQ";
        private const string K_TYPE_MAP    = "TYPE_MAPPING";
        private const string K_PIPE_LINKS  = "PIPE_LINKS";
        private const string K_MH_LINKS    = "MANHOLE_LINKS";
        private const string K_DISCOVERED  = "DISCOVERED_ITEMS";

        // =====================================================================
        // Public API — Pipe links
        // =====================================================================

        public static void SavePipeLink(Database db, PipeTypeLink link)
        {
            if (db == null)   throw new ArgumentNullException(nameof(db));
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (string.IsNullOrWhiteSpace(link.UrbanoCatalogItemGuid))
                throw new ArgumentException("UrbanoCatalogItemGuid boş olamaz.", nameof(link));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetOrCreateSubDict(tr, db, K_PIPE_LINKS);
                string key = SafeKey(link.UrbanoCatalogItemGuid);
                MakeXRecord(tr, dict, key,
                    Str(link.UrbanoCatalogItemGuid),
                    Str(link.UrbanoCatalogItemName ?? ""),
                    Str(link.LinkedPipeDefinitionId.ToString()));
                tr.Commit();
            }
        }

        public static List<PipeTypeLink> LoadAllPipeLinks(Database db)
        {
            var result = new List<PipeTypeLink>();
            if (db == null) return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetSubDictReadOnly(tr, db, K_PIPE_LINKS);
                if (dict == null) { tr.Commit(); return result; }

                foreach (DBDictionaryEntry entry in dict)
                {
                    var tvs = ReadXRecord(tr, dict, entry.Key);
                    if (tvs == null) continue;
                    int i = 0;
                    var link = new PipeTypeLink
                    {
                        UrbanoCatalogItemGuid = ReadStr(tvs, ref i),
                        UrbanoCatalogItemName = ReadStr(tvs, ref i)
                    };
                    Guid pid;
                    Guid.TryParse(ReadStr(tvs, ref i), out pid);
                    link.LinkedPipeDefinitionId = pid;
                    result.Add(link);
                }
                tr.Commit();
            }
            return result;
        }

        public static bool DeletePipeLink(Database db, string urbanoCatalogItemGuid)
            => DeleteLink(db, K_PIPE_LINKS, urbanoCatalogItemGuid);

        // =====================================================================
        // Public API — Manhole links
        // =====================================================================

        public static void SaveManholeLink(Database db, ManholeTypeLink link)
        {
            if (db == null)   throw new ArgumentNullException(nameof(db));
            if (link == null) throw new ArgumentNullException(nameof(link));
            if (string.IsNullOrWhiteSpace(link.UrbanoCatalogItemGuid))
                throw new ArgumentException("UrbanoCatalogItemGuid boş olamaz.", nameof(link));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetOrCreateSubDict(tr, db, K_MH_LINKS);
                string key = SafeKey(link.UrbanoCatalogItemGuid);
                MakeXRecord(tr, dict, key,
                    Str(link.UrbanoCatalogItemGuid),
                    Str(link.UrbanoCatalogItemName ?? ""),
                    I16((short)link.DiameterMode),
                    Str(link.LinkedFamilyId.ToString()),
                    Str(link.LinkedBottomComponentId.ToString()));
                tr.Commit();
            }
        }

        public static List<ManholeTypeLink> LoadAllManholeLinks(Database db)
        {
            var result = new List<ManholeTypeLink>();
            if (db == null) return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetSubDictReadOnly(tr, db, K_MH_LINKS);
                if (dict == null) { tr.Commit(); return result; }

                foreach (DBDictionaryEntry entry in dict)
                {
                    var tvs = ReadXRecord(tr, dict, entry.Key);
                    if (tvs == null) continue;
                    int i = 0;
                    var link = new ManholeTypeLink
                    {
                        UrbanoCatalogItemGuid = ReadStr(tvs, ref i),
                        UrbanoCatalogItemName = ReadStr(tvs, ref i),
                        DiameterMode = (ManholeDiameterMode)ReadI16(tvs, ref i)
                    };
                    Guid famId, bottomId;
                    Guid.TryParse(ReadStr(tvs, ref i), out famId);
                    link.LinkedFamilyId = famId;
                    Guid.TryParse(ReadStr(tvs, ref i), out bottomId);
                    link.LinkedBottomComponentId = bottomId;
                    result.Add(link);
                }
                tr.Commit();
            }
            return result;
        }

        public static bool DeleteManholeLink(Database db, string urbanoCatalogItemGuid)
            => DeleteLink(db, K_MH_LINKS, urbanoCatalogItemGuid);

        // =====================================================================
        // Public API — Discovered catalog items (system-managed, not user-edited)
        // =====================================================================

        /// <summary>
        /// Full replace: erases every previously-discovered item and writes the
        /// current export's list. Called once per successful "Metraj Verisi
        /// Güncelle" — safe to call outside any transaction the caller already has
        /// open elsewhere, since it manages its own.
        /// </summary>
        public static void SaveDiscoveredItems(Database db, List<CatalogItemInfo> items)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));
            items = items ?? new List<CatalogItemInfo>();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetOrCreateSubDict(tr, db, K_DISCOVERED);
                EraseAllEntries(tr, dict);
                foreach (var item in items)
                {
                    string key = SafeKey(item.Guid);
                    // Shape/LengthM/WidthM appended after the original 4 fields
                    // (added 2026-07-06 for shape-aware Taban matching) — old
                    // DWGs missing them read back as Circular/0/0 via the same
                    // past-end-of-array fallback ReadStr/ReadDbl already use.
                    MakeXRecord(tr, dict, key,
                        Str(item.Guid), Str(item.Name ?? ""), Str(item.Reference ?? ""),
                        Str(item.GroupName ?? ""),
                        I16((short)item.Shape), Dbl(item.LengthM), Dbl(item.WidthM));
                }
                tr.Commit();
            }
        }

        public static List<CatalogItemInfo> LoadDiscoveredItems(Database db)
        {
            var result = new List<CatalogItemInfo>();
            if (db == null) return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetSubDictReadOnly(tr, db, K_DISCOVERED);
                if (dict == null) { tr.Commit(); return result; }

                foreach (DBDictionaryEntry entry in dict)
                {
                    var tvs = ReadXRecord(tr, dict, entry.Key);
                    if (tvs == null) continue;
                    int i = 0;
                    string guid = ReadStr(tvs, ref i);
                    string name = ReadStr(tvs, ref i);
                    string reference = ReadStr(tvs, ref i);
                    string groupName = ReadStr(tvs, ref i);
                    var shape = (FootprintShape)ReadI16(tvs, ref i);
                    double lengthM = ReadDbl(tvs, ref i);
                    double widthM  = ReadDbl(tvs, ref i);
                    result.Add(new CatalogItemInfo
                    {
                        Guid      = guid,
                        Name      = name,
                        Reference = reference,
                        GroupName = groupName,
                        Shape     = shape,
                        LengthM   = lengthM,
                        WidthM    = widthM
                    });
                }
                tr.Commit();
            }
            return result;
        }

        private static void EraseAllEntries(Transaction tr, DBDictionary dict)
        {
            var ids = new List<ObjectId>();
            foreach (DBDictionaryEntry e in dict) ids.Add(e.Value);
            foreach (var id in ids)
                tr.GetObject(id, OpenMode.ForWrite).Erase();
        }

        // =====================================================================
        // Shared delete
        // =====================================================================

        private static bool DeleteLink(Database db, string groupKey, string urbanoCatalogItemGuid)
        {
            if (db == null || string.IsNullOrWhiteSpace(urbanoCatalogItemGuid)) return false;
            string key = SafeKey(urbanoCatalogItemGuid);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var dict = GetSubDictReadOnly(tr, db, groupKey);
                if (dict == null || !dict.Contains(key)) { tr.Commit(); return false; }

                dict.UpgradeOpen();
                var rec = tr.GetObject(dict.GetAt(key), OpenMode.ForWrite);
                rec.Erase();
                tr.Commit();
                return true;
            }
        }

        // =====================================================================
        // NOD navigation
        // =====================================================================

        private static DBDictionary GetOrCreateSubDict(Transaction tr, Database db, string subKey)
        {
            var nod = (DBDictionary)tr.GetObject(
                db.NamedObjectsDictionaryId, OpenMode.ForRead);

            DBDictionary boqRoot;
            if (nod.Contains(NOD_ROOT))
            {
                boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
            }
            else
            {
                nod.UpgradeOpen();
                boqRoot = new DBDictionary { TreatElementsAsHard = true };
                nod.SetAt(NOD_ROOT, boqRoot);
                tr.AddNewlyCreatedDBObject(boqRoot, true);
            }

            DBDictionary typeMapDict;
            if (boqRoot.Contains(K_TYPE_MAP))
            {
                typeMapDict = (DBDictionary)tr.GetObject(boqRoot.GetAt(K_TYPE_MAP), OpenMode.ForRead);
            }
            else
            {
                boqRoot.UpgradeOpen();
                typeMapDict = MakeSubDict(tr, boqRoot, K_TYPE_MAP);
            }

            if (typeMapDict.Contains(subKey))
                return (DBDictionary)tr.GetObject(typeMapDict.GetAt(subKey), OpenMode.ForWrite);

            typeMapDict.UpgradeOpen();
            return MakeSubDict(tr, typeMapDict, subKey);
        }

        private static DBDictionary GetSubDictReadOnly(Transaction tr, Database db, string subKey)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(NOD_ROOT)) return null;
            var boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
            if (!boqRoot.Contains(K_TYPE_MAP)) return null;
            var typeMapDict = (DBDictionary)tr.GetObject(boqRoot.GetAt(K_TYPE_MAP), OpenMode.ForRead);
            if (!typeMapDict.Contains(subKey)) return null;
            return (DBDictionary)tr.GetObject(typeMapDict.GetAt(subKey), OpenMode.ForRead);
        }

        // =====================================================================
        // Low-level helpers (same pattern as ProjectTemplateNodManager / DwgBoQStore)
        // =====================================================================

        private static DBDictionary MakeSubDict(Transaction tr, DBDictionary parent, string key)
        {
            var d = new DBDictionary { TreatElementsAsHard = true };
            parent.SetAt(key, d);
            tr.AddNewlyCreatedDBObject(d, true);
            return d;
        }

        private static void MakeXRecord(
            Transaction tr, DBDictionary parent, string key, params TypedValue[] tvs)
        {
            if (parent.Contains(key))
            {
                var old = tr.GetObject(parent.GetAt(key), OpenMode.ForWrite);
                old.Erase();
            }
            var rec = new Xrecord { Data = new ResultBuffer(tvs) };
            parent.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
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

        // ── TypedValue factories ──────────────────────────────────────────────
        private static TypedValue Str(string v) => new TypedValue((int)DxfCode.Text,  v ?? "");
        private static TypedValue I16(short v)  => new TypedValue((int)DxfCode.Int16, v);
        private static TypedValue Dbl(double v) => new TypedValue((int)DxfCode.Real,  v);

        // ── TypedValue readers ────────────────────────────────────────────────
        private static string ReadStr(TypedValue[] tvs, ref int i)
            => (i < tvs.Length) ? (tvs[i++].Value as string ?? "") : "";

        private static short ReadI16(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is short s) return s;
            if (tv.Value is int n)   return (short)n;
            return 0;
        }

        private static double ReadDbl(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            return (tv.Value is double d) ? d : 0;
        }

        // ── Key sanitizer (mirrors DwgBoQStore.SafeKey) ───────────────────────
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
    }
}
