using System.Collections.Generic;
using Autodesk.AutoCAD.DatabaseServices;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Persists AG_GUID → CatalogGroupId assignments inside the DWG NOD.
    ///
    /// <code>
    /// NOD["URBANO_BOQ"]["MANHOLE_ASSIGNMENTS"]   (DBDictionary)
    ///   "ASSIGN_META"                            (Xrecord – Int32 count)
    ///   "ASSIGN_0", "ASSIGN_1", …                (Xrecord – Str AG_GUID, Str Handle, Str GroupId)
    /// </code>
    ///
    /// Save() replaces the whole branch. Merge() updates only the provided GUIDs.
    /// </summary>
    public static class ManholeAssignStore
    {
        private const string NOD_ROOT     = "URBANO_BOQ";
        private const string K_CATALOGS   = "KATALOGLAR";   // shared catalog container
        private const string K_ASSIGN     = "MANHOLE_ASSIGNMENTS";
        private const string K_META       = "ASSIGN_META";
        private const string K_PFX        = "ASSIGN_";

        // =====================================================================
        // Public API
        // =====================================================================

        public static void Save(Database db, IList<ManholeAssignment> assignments)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                DBDictionary boqRoot;
                if (nod.Contains(NOD_ROOT))
                    boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForWrite);
                else
                {
                    boqRoot = new DBDictionary { TreatElementsAsHard = true };
                    nod.SetAt(NOD_ROOT, boqRoot);
                    tr.AddNewlyCreatedDBObject(boqRoot, true);
                }

                // Assignments live under KATALOGLAR, separate from AGLAR (pipe networks).
                DBDictionary kataloglar;
                if (boqRoot.Contains(K_CATALOGS))
                    kataloglar = (DBDictionary)tr.GetObject(boqRoot.GetAt(K_CATALOGS), OpenMode.ForWrite);
                else
                    kataloglar = MakeSubDict(tr, boqRoot, K_CATALOGS);

                if (kataloglar.Contains(K_ASSIGN))
                {
                    var old = (DBDictionary)tr.GetObject(
                        kataloglar.GetAt(K_ASSIGN), OpenMode.ForWrite);
                    EraseTree(tr, old);
                    old.Erase();
                }

                var dict = MakeSubDict(tr, kataloglar, K_ASSIGN);
                MakeXRec(tr, dict, K_META,
                    new TypedValue((int)DxfCode.Int32, assignments.Count));

                for (int i = 0; i < assignments.Count; i++)
                {
                    var a = assignments[i];
                    MakeXRec(tr, dict, K_PFX + i,
                        new TypedValue((int)DxfCode.Text, a.AgGuid  ?? ""),
                        new TypedValue((int)DxfCode.Text, a.Handle  ?? ""),
                        new TypedValue((int)DxfCode.Text, a.GroupId ?? ""));
                }

                tr.Commit();
            }
        }

        public static List<ManholeAssignment> Load(Database db)
        {
            var result = new List<ManholeAssignment>();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_ROOT)) { tr.Commit(); return result; }

                var boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);

                // New path: KATALOGLAR/MANHOLE_ASSIGNMENTS.
                // Fallback: old DWGs had MANHOLE_ASSIGNMENTS directly in boqRoot.
                DBDictionary assignContainer = boqRoot.Contains(K_CATALOGS)
                    ? (DBDictionary)tr.GetObject(boqRoot.GetAt(K_CATALOGS), OpenMode.ForRead)
                    : boqRoot;
                if (!assignContainer.Contains(K_ASSIGN)) { tr.Commit(); return result; }

                var dict = (DBDictionary)tr.GetObject(assignContainer.GetAt(K_ASSIGN), OpenMode.ForRead);

                foreach (DBDictionaryEntry e in dict)
                {
                    if (e.Key == K_META) continue;
                    var xr = tr.GetObject(e.Value, OpenMode.ForRead) as Xrecord;
                    if (xr?.Data == null) continue;

                    var tvs = new List<TypedValue>();
                    foreach (TypedValue tv in xr.Data) tvs.Add(tv);
                    if (tvs.Count < 3) continue;

                    result.Add(new ManholeAssignment
                    {
                        AgGuid  = tvs[0].Value as string ?? "",
                        Handle  = tvs[1].Value as string ?? "",
                        GroupId = tvs[2].Value as string ?? ""
                    });
                }

                tr.Commit();
            }
            return result;
        }

        public static bool HasData(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_ROOT)) { tr.Commit(); return false; }
                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
                DBDictionary assignContainer2 = root.Contains(K_CATALOGS)
                    ? (DBDictionary)tr.GetObject(root.GetAt(K_CATALOGS), OpenMode.ForRead)
                    : root;
                bool found = assignContainer2.Contains(K_ASSIGN);
                tr.Commit();
                return found;
            }
        }

        /// <summary>
        /// Updates only the provided GUIDs; existing entries for other manholes are preserved.
        /// </summary>
        public static void Merge(Database db, IList<ManholeAssignment> incoming)
        {
            var existing = Load(db);
            var map = new Dictionary<string, ManholeAssignment>(
                System.StringComparer.OrdinalIgnoreCase);
            foreach (var a in existing)  map[a.AgGuid] = a;
            foreach (var a in incoming)  map[a.AgGuid] = a;
            Save(db, new List<ManholeAssignment>(map.Values));
        }

        // =====================================================================
        // NOD helpers
        // =====================================================================

        private static DBDictionary MakeSubDict(Transaction tr, DBDictionary parent, string key)
        {
            var d = new DBDictionary { TreatElementsAsHard = true };
            parent.SetAt(key, d);
            tr.AddNewlyCreatedDBObject(d, true);
            return d;
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

        private static void MakeXRec(Transaction tr, DBDictionary parent,
                                      string key, params TypedValue[] tvs)
        {
            var rec = new Xrecord { Data = new ResultBuffer(tvs) };
            parent.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
        }
    }

    public class ManholeAssignment
    {
        /// <summary>AG_GUID from entity XData — primary key for XML matching (node @g).</summary>
        public string AgGuid  { get; set; }

        /// <summary>AutoCAD entity handle at time of assignment (secondary ref, may change).</summary>
        public string Handle  { get; set; }

        /// <summary>ManholeCatalog group Id chosen by the user.</summary>
        public string GroupId { get; set; }
    }
}
