using System;
using System.Collections.Generic;
using Teigha.DatabaseServices;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Persists a <see cref="ManholeCatalog"/> inside the active DWG NOD:
    ///
    /// <code>
    /// NOD["URBANO_BOQ"]                 (shared root, created on demand)
    ///   "MANHOLES_CATALOG"              (DBDictionary)
    ///     "CATALOG_META"                (Xrecord – LastModified + group count)
    ///     "GRP_0", "GRP_1", …           (DBDictionary – one per group)
    ///       "GROUP_HEADER"              (Xrecord – Id, Name)
    ///       "TPL_0", "TPL_1", …         (DBDictionary – one per sub-template)
    ///         "TPL_HEADER"              (Xrecord – Id, Name, Mode, MinPipe, MaxPipe,
    ///                                             ManholeDn, WallThick, ClearanceM)
    ///         "EXCAV_SHAPE"             (Xrecord – PolygonSides, IsVertical, SlopeX)
    ///         "BEDDING_LAYERS"          (Xrecord – count + (Name, ThicknessM) pairs)
    /// </code>
    ///
    /// Only the MANHOLES_CATALOG sub-tree is replaced on Save;
    /// the pipe BoQ data stored elsewhere under URBANO_BOQ is untouched.
    /// </summary>
    public static class ManholeCatalogStore
    {
        private const string NOD_ROOT     = "URBANO_BOQ";
        private const string K_CATALOGS   = "KATALOGLAR";   // shared catalog container
        private const string K_CATALOG    = "MANHOLES_CATALOG";
        private const string K_CAT_META   = "CATALOG_META";
        private const string K_GRP_PREFIX = "GRP_";
        private const string K_GRP_HDR    = "GROUP_HEADER";
        private const string K_TPL_PREFIX = "TPL_";
        private const string K_TPL_HDR    = "TPL_HEADER";
        private const string K_EXCAV      = "EXCAV_SHAPE";
        private const string K_BEDDING    = "BEDDING_LAYERS";

        // =====================================================================
        // Public API
        // =====================================================================

        public static void Save(Database db, ManholeCatalog catalog)
        {
            if (catalog == null) throw new ArgumentNullException(nameof(catalog));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForWrite);

                // Ensure URBANO_BOQ root exists.
                DBDictionary boqRoot;
                if (nod.Contains(NOD_ROOT))
                    boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForWrite);
                else
                {
                    boqRoot = new DBDictionary { TreatElementsAsHard = true };
                    nod.SetAt(NOD_ROOT, boqRoot);
                    tr.AddNewlyCreatedDBObject(boqRoot, true);
                }

                // Catalogs live under KATALOGLAR, separate from AGLAR (pipe networks).
                DBDictionary kataloglar;
                if (boqRoot.Contains(K_CATALOGS))
                    kataloglar = (DBDictionary)tr.GetObject(boqRoot.GetAt(K_CATALOGS), OpenMode.ForWrite);
                else
                    kataloglar = MakeSubDict(tr, boqRoot, K_CATALOGS);

                // Replace MANHOLES_CATALOG branch.
                if (kataloglar.Contains(K_CATALOG))
                {
                    var old = (DBDictionary)tr.GetObject(
                        kataloglar.GetAt(K_CATALOG), OpenMode.ForWrite);
                    EraseTree(tr, old);
                    old.Erase();
                }

                var catDict = MakeSubDict(tr, kataloglar, K_CATALOG);

                MakeXRecord(tr, catDict, K_CAT_META,
                    Str(catalog.LastModified.ToString("u")),
                    I32(catalog.Groups?.Count ?? 0));

                var groups = catalog.Groups ?? new List<ManholeTemplateGroup>();
                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var grp     = groups[gi];
                    var grpDict = MakeSubDict(tr, catDict, K_GRP_PREFIX + gi);

                    MakeXRecord(tr, grpDict, K_GRP_HDR,
                        Str(grp.Id   ?? ""),
                        Str(grp.Name ?? ""),
                        Str(grp.Mode ?? "Explicit"));

                    var tpls = grp.Templates ?? new List<ManholeSubTemplate>();
                    for (int ti = 0; ti < tpls.Count; ti++)
                    {
                        var tpl     = tpls[ti];
                        var tplDict = MakeSubDict(tr, grpDict, K_TPL_PREFIX + ti);

                        MakeXRecord(tr, tplDict, K_TPL_HDR,
                            Str(tpl.Id              ?? ""),
                            Str(tpl.Name            ?? ""),
                            I32(tpl.MinPipeDiamMm),
                            I32(tpl.MaxPipeDiamMm),
                            I32(tpl.ManholeDn),
                            I32(tpl.WallThicknessMm),
                            Dbl(tpl.ClearanceM));

                        var sh = tpl.ExcavShape ?? new ExcavationShape();
                        MakeXRecord(tr, tplDict, K_EXCAV,
                            I32(sh.PolygonSides),
                            I16(sh.IsVertical ? (short)1 : (short)0),
                            Dbl(sh.SlopeX));

                        WriteBeddingLayers(tr, tplDict, tpl.BeddingLayers);
                    }
                }

                tr.Commit();
            }
        }

        public static ManholeCatalog Load(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);

                if (!nod.Contains(NOD_ROOT)) { tr.Commit(); return null; }
                var boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);

                // New path: KATALOGLAR/MANHOLES_CATALOG.
                // Fallback: old DWGs had MANHOLES_CATALOG directly in boqRoot.
                DBDictionary catalogContainer = boqRoot.Contains(K_CATALOGS)
                    ? (DBDictionary)tr.GetObject(boqRoot.GetAt(K_CATALOGS), OpenMode.ForRead)
                    : boqRoot;
                if (!catalogContainer.Contains(K_CATALOG)) { tr.Commit(); return null; }
                var catDict = (DBDictionary)tr.GetObject(catalogContainer.GetAt(K_CATALOG), OpenMode.ForRead);

                var catalog = new ManholeCatalog();

                var meta = ReadXRecord(tr, catDict, K_CAT_META);
                if (meta != null)
                {
                    int mi = 0;
                    if (DateTime.TryParse(ReadStr(meta, ref mi), out var dt))
                        catalog.LastModified = dt;
                }

                foreach (DBDictionaryEntry grpEntry in catDict)
                {
                    if (grpEntry.Key == K_CAT_META) continue;
                    var grpDict = tr.GetObject(grpEntry.Value, OpenMode.ForRead) as DBDictionary;
                    if (grpDict == null) continue;

                    var grpHdr = ReadXRecord(tr, grpDict, K_GRP_HDR);
                    if (grpHdr == null) continue;
                    int ghi = 0;
                    var grp = new ManholeTemplateGroup
                    {
                        Id   = ReadStr(grpHdr, ref ghi),
                        Name = ReadStr(grpHdr, ref ghi),
                        Mode = ReadStr(grpHdr, ref ghi)
                    };
                    if (string.IsNullOrEmpty(grp.Mode)) grp.Mode = "Explicit";

                    foreach (DBDictionaryEntry tplEntry in grpDict)
                    {
                        if (tplEntry.Key == K_GRP_HDR) continue;
                        var tplDict = tr.GetObject(tplEntry.Value, OpenMode.ForRead) as DBDictionary;
                        if (tplDict == null) continue;

                        var tplHdr = ReadXRecord(tr, tplDict, K_TPL_HDR);
                        if (tplHdr == null) continue;
                        int thi = 0;
                        var tpl = new ManholeSubTemplate
                        {
                            Id              = ReadStr(tplHdr, ref thi),
                            Name            = ReadStr(tplHdr, ref thi),
                            MinPipeDiamMm   = ReadI32(tplHdr, ref thi),
                            MaxPipeDiamMm   = ReadI32(tplHdr, ref thi),
                            ManholeDn       = ReadI32(tplHdr, ref thi),
                            WallThicknessMm = ReadI32(tplHdr, ref thi),
                            ClearanceM      = ReadDbl(tplHdr, ref thi)
                        };

                        var shTvs = ReadXRecord(tr, tplDict, K_EXCAV);
                        if (shTvs != null)
                        {
                            int si = 0;
                            tpl.ExcavShape = new ExcavationShape
                            {
                                PolygonSides = Math.Max(4, Math.Min(32, ReadI32(shTvs, ref si))),
                                IsVertical   = ReadI16(shTvs, ref si) != 0,
                                SlopeX       = Math.Max(0.01, ReadDbl(shTvs, ref si))
                            };
                        }

                        tpl.BeddingLayers = ReadBeddingLayers(tr, tplDict);
                        grp.Templates.Add(tpl);
                    }

                    catalog.Groups.Add(grp);
                }

                tr.Commit();
                return catalog;
            }
        }

        public static bool HasData(Database db)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForRead);
                if (!nod.Contains(NOD_ROOT)) { tr.Commit(); return false; }
                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
                DBDictionary catContainer = root.Contains(K_CATALOGS)
                    ? (DBDictionary)tr.GetObject(root.GetAt(K_CATALOGS), OpenMode.ForRead)
                    : root;
                bool found = catContainer.Contains(K_CATALOG);
                tr.Commit();
                return found;
            }
        }

        // =====================================================================
        // Bedding helpers
        // =====================================================================

        private static void WriteBeddingLayers(
            Transaction tr, DBDictionary tplDict, List<BeddingSubLayer> layers)
        {
            var tvs = new List<TypedValue>();
            int count = layers?.Count ?? 0;
            tvs.Add(I32(count));
            for (int i = 0; i < count; i++)
            {
                tvs.Add(Str(layers[i].Name      ?? ""));
                tvs.Add(Dbl(layers[i].ThicknessM));
            }
            MakeXRecord(tr, tplDict, K_BEDDING, tvs.ToArray());
        }

        private static List<BeddingSubLayer> ReadBeddingLayers(
            Transaction tr, DBDictionary tplDict)
        {
            var result = new List<BeddingSubLayer>();
            var tvs = ReadXRecord(tr, tplDict, K_BEDDING);
            if (tvs == null) return result;
            int i = 0;
            int count = ReadI32(tvs, ref i);
            for (int l = 0; l < count; l++)
                result.Add(new BeddingSubLayer
                {
                    Name       = ReadStr(tvs, ref i),
                    ThicknessM = ReadDbl(tvs, ref i)
                });
            return result;
        }

        // =====================================================================
        // NOD helpers  (same pattern as DwgBoQStore)
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

        private static Xrecord MakeXRecord(
            Transaction tr, DBDictionary parent, string key, params TypedValue[] tvs)
        {
            var rec = new Xrecord { Data = new ResultBuffer(tvs) };
            parent.SetAt(key, rec);
            tr.AddNewlyCreatedDBObject(rec, true);
            return rec;
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

        private static TypedValue Str(string v) => new TypedValue((int)DxfCode.Text,  v ?? "");
        private static TypedValue Dbl(double v) => new TypedValue((int)DxfCode.Real,  v);
        private static TypedValue I32(int v)    => new TypedValue((int)DxfCode.Int32, v);
        private static TypedValue I16(short v)  => new TypedValue((int)DxfCode.Int16, v);

        private static string ReadStr(TypedValue[] tvs, ref int i)
            => (i < tvs.Length) ? (tvs[i++].Value as string ?? "") : "";

        private static double ReadDbl(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            return tv.Value is double d ? d : 0;
        }

        private static int ReadI32(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is int n)   return n;
            if (tv.Value is short s) return s;
            if (tv.Value is long l)  return (int)l;
            return 0;
        }

        private static short ReadI16(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is short s) return s;
            if (tv.Value is int n)   return (short)n;
            return 0;
        }
    }
}
