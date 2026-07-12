using System;
using System.Collections.Generic;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using UrbanoMetraj.BoQ.ProjectRules.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;   // ComponentRole

namespace UrbanoMetraj.BoQ.ProjectRules.Services
{
    /// <summary>
    /// Persists the <see cref="ProjectRuleSet"/> inside the active DWG NOD.
    ///
    /// NOD layout (surgical — only touches PROJE_KURALLARI, leaves all other URBANO_BOQ data intact):
    /// <code>
    /// NOD["URBANO_BOQ"]["PROJE_KURALLARI"]        (DBDictionary)
    ///   "PR_META"                                 (XRecord: SchemaVersion · CalcMode)
    ///   "SEBEKELER"                               (DBDictionary)
    ///     [SafeKey(SystemName)]                   (DBDictionary)
    ///       "NET_META"                            (XRecord: SystemName · PipeFamilyId · PipeSinif · ManholeFamilyId)
    ///       (later: CONN_RULES / PIECE_EXCL child dicts)
    /// </code>
    ///
    /// IMPORTANT: <c>DwgBoQStore.Save</c> purges every URBANO_BOQ root child that is not in its
    /// preserve set. "PROJE_KURALLARI" was added to that set so these rules survive a Hesapla run.
    /// </summary>
    public static class ProjectRulesNodManager
    {
        private const string NOD_ROOT   = "URBANO_BOQ";
        private const string K_ROOT     = "PROJE_KURALLARI";
        private const string K_META     = "PR_META";
        private const string K_NETWORKS = "SEBEKELER";
        private const string K_NET_META = "NET_META";
        private const string K_EXC      = "ISTISNALAR";
        private const string K_EXC_PIPE = "PIPE_FAMILY";
        private const string K_EXC_MH   = "MANHOLE_FAMILY";
        private const string K_CONN     = "CONN_RULES";
        private const string K_CONN_META = "R_META";
        private const string K_PIECE      = "PIECE_EXCL";
        private const string K_PIECE_META = "ROW_META";

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>Replaces the whole PROJE_KURALLARI branch with <paramref name="ruleSet"/>.</summary>
        public static void Save(Database db, ProjectRuleSet ruleSet)
        {
            if (db == null)      throw new ArgumentNullException(nameof(db));
            if (ruleSet == null) throw new ArgumentNullException(nameof(ruleSet));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var boqRoot = GetOrCreateBoqRoot(tr, db);

                // Wipe any existing PROJE_KURALLARI branch, then rewrite from scratch.
                if (boqRoot.Contains(K_ROOT))
                {
                    var old = (DBDictionary)tr.GetObject(boqRoot.GetAt(K_ROOT), OpenMode.ForWrite);
                    EraseTree(tr, old);
                    old.Erase();
                }

                var rootDict = MakeSubDict(tr, boqRoot, K_ROOT);

                MakeXRecord(tr, rootDict, K_META,
                    Str(ruleSet.SchemaVersion ?? "1.0"),
                    I16((short)ruleSet.CalcMode));

                var netsDict = MakeSubDict(tr, rootDict, K_NETWORKS);
                foreach (var net in ruleSet.NetworkRules ?? new List<NetworkRule>())
                {
                    if (net == null) continue;
                    string key    = UniqueKey(netsDict, SafeKey(net.SystemName));
                    var netDict   = MakeSubDict(tr, netsDict, key);
                    MakeXRecord(tr, netDict, K_NET_META,
                        Str(net.SystemName ?? ""),
                        Str(net.PipeFamilyId.ToString()),
                        Str(net.PipeSinif ?? ""),
                        Str(net.ManholeFamilyId.ToString()));

                    // Connection rules (baca seçim) — R_### subdicts, each with R_META + T_### tiers.
                    var connDict = MakeSubDict(tr, netDict, K_CONN);
                    var connRules = net.ConnectionRules ?? new List<ConnectionRule>();
                    for (int ri = 0; ri < connRules.Count; ri++)
                    {
                        var r     = connRules[ri];
                        var rDict = MakeSubDict(tr, connDict, "R_" + ri.ToString("D3"));
                        MakeXRecord(tr, rDict, K_CONN_META, Dbl(r.MinPipeMm), Dbl(r.MaxPipeMm));
                        var tiers = r.Tiers ?? new List<ConnDepthTier>();
                        for (int ti = 0; ti < tiers.Count; ti++)
                        {
                            var t    = tiers[ti];
                            var ttvs = new List<TypedValue>
                            {
                                Dbl(t.MinDepthM), Dbl(t.MaxDepthM), Dbl(t.ManholeDiameterMm),
                                I16((short)(t.IsCastInSitu ? 1 : 0)), Str(t.Notes ?? "")
                            };
                            foreach (var cc in t.ComponentConstraints ?? new List<ComponentTypeConstraint>())
                            {
                                ttvs.Add(Str(cc.Role.ToString()));
                                ttvs.Add(I16((short)cc.MinCount));
                                ttvs.Add(I16((short)cc.MaxCount));
                            }
                            MakeXRecord(tr, rDict, "T_" + ti.ToString("D3"), ttvs.ToArray());
                        }
                    }

                    // Piece exclusions — PR_### row subdict (ROW_META = familyId+diameter) + PE_## role records.
                    var pieceDict = MakeSubDict(tr, netDict, K_PIECE);
                    var rows = net.PieceExclusionRows ?? new List<PieceExclusionRow>();
                    for (int pri = 0; pri < rows.Count; pri++)
                    {
                        var row     = rows[pri];
                        var rowDict = MakeSubDict(tr, pieceDict, "PR_" + pri.ToString("D3"));
                        MakeXRecord(tr, rowDict, K_PIECE_META,
                            Str(row.ManholeFamilyId.ToString()), Dbl(row.ManholeDiameterMm));
                        var roles = row.Roles ?? new List<PieceExclusion>();
                        for (int pi = 0; pi < roles.Count; pi++)
                        {
                            var pe  = roles[pi];
                            var tvs = new List<TypedValue> { Str(pe.Role.ToString()) };
                            foreach (var h in pe.AllowedHeightsMm ?? new List<double>()) tvs.Add(Dbl(h));
                            MakeXRecord(tr, rowDict, "PE_" + pi.ToString("D2"), tvs.ToArray());
                        }
                    }

                    // Exceptions (AG_GUID-keyed) — scoped to this network.
                    WriteExceptions(tr, netDict, net.Exceptions ?? new ProjectExceptions());
                }

                tr.Commit();
            }
        }

        /// <summary>Loads the project rule set, or an empty (TypeMapping-default) set if none exists.</summary>
        public static ProjectRuleSet Load(Database db)
        {
            var result = new ProjectRuleSet();
            if (db == null) return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var rootDict = GetRootReadOnly(tr, db);
                if (rootDict == null) { tr.Commit(); return result; }

                var meta = ReadXRecord(tr, rootDict, K_META);
                if (meta != null)
                {
                    int i = 0;
                    result.SchemaVersion = ReadStr(meta, ref i);
                    result.CalcMode      = (CalcMode)ReadI16(meta, ref i);
                    if (string.IsNullOrEmpty(result.SchemaVersion)) result.SchemaVersion = "1.0";
                }

                var netsDict = GetSubDict(tr, rootDict, K_NETWORKS);
                if (netsDict != null)
                {
                    foreach (DBDictionaryEntry entry in netsDict)
                    {
                        var netDict = tr.GetObject(entry.Value, OpenMode.ForRead) as DBDictionary;
                        if (netDict == null) continue;
                        var tvs = ReadXRecord(tr, netDict, K_NET_META);
                        if (tvs == null) continue;

                        int i = 0;
                        var net = new NetworkRule { SystemName = ReadStr(tvs, ref i) };
                        Guid pf; Guid.TryParse(ReadStr(tvs, ref i), out pf); net.PipeFamilyId = pf;
                        net.PipeSinif = ReadStr(tvs, ref i);
                        Guid mf; Guid.TryParse(ReadStr(tvs, ref i), out mf); net.ManholeFamilyId = mf;

                        var connDict = GetSubDict(tr, netDict, K_CONN);
                        if (connDict != null)
                        {
                            var rKeys = new List<string>();
                            foreach (DBDictionaryEntry re in connDict) rKeys.Add(re.Key);
                            rKeys.Sort(StringComparer.Ordinal);
                            foreach (var rKey in rKeys)
                            {
                                var rDict = GetSubDict(tr, connDict, rKey);
                                if (rDict == null) continue;
                                var rmeta = ReadXRecord(tr, rDict, K_CONN_META);
                                if (rmeta == null) continue;
                                int ci = 0;
                                var rule = new ConnectionRule
                                {
                                    MinPipeMm = ReadDbl(rmeta, ref ci),
                                    MaxPipeMm = ReadDbl(rmeta, ref ci)
                                };
                                var tKeys = new List<string>();
                                foreach (DBDictionaryEntry te in rDict)
                                    if (te.Key != K_CONN_META) tKeys.Add(te.Key);
                                tKeys.Sort(StringComparer.Ordinal);
                                foreach (var tKey in tKeys)
                                {
                                    var ttv = ReadXRecord(tr, rDict, tKey);
                                    if (ttv == null) continue;
                                    int ti = 0;
                                    var tier = new ConnDepthTier
                                    {
                                        MinDepthM         = ReadDbl(ttv, ref ti),
                                        MaxDepthM         = ReadDbl(ttv, ref ti),
                                        ManholeDiameterMm = ReadDbl(ttv, ref ti),
                                        IsCastInSitu      = ReadI16(ttv, ref ti) == 1,
                                        Notes             = ReadStr(ttv, ref ti)
                                    };
                                    while (ti + 3 <= ttv.Length)   // trailing constraint triples: role, min, max
                                    {
                                        string rs = ReadStr(ttv, ref ti);
                                        int    mn = ReadI16(ttv, ref ti);
                                        int    mx = ReadI16(ttv, ref ti);
                                        ComponentRole crole;
                                        if (Enum.TryParse(rs, out crole))
                                            tier.ComponentConstraints.Add(new ComponentTypeConstraint
                                            { Role = crole, MinCount = mn, MaxCount = mx });
                                    }
                                    rule.Tiers.Add(tier);
                                }
                                net.ConnectionRules.Add(rule);
                            }
                        }

                        var pieceDict = GetSubDict(tr, netDict, K_PIECE);
                        if (pieceDict != null)
                        {
                            var prKeys = new List<string>();
                            foreach (DBDictionaryEntry pr in pieceDict) prKeys.Add(pr.Key);
                            prKeys.Sort(StringComparer.Ordinal);
                            foreach (var prKey in prKeys)
                            {
                                var rowDict = GetSubDict(tr, pieceDict, prKey);
                                if (rowDict == null) continue;
                                var rmeta = ReadXRecord(tr, rowDict, K_PIECE_META);
                                if (rmeta == null) continue;
                                int ri2 = 0;
                                Guid fid; Guid.TryParse(ReadStr(rmeta, ref ri2), out fid);
                                var row = new PieceExclusionRow
                                {
                                    ManholeFamilyId   = fid,
                                    ManholeDiameterMm = ReadDbl(rmeta, ref ri2)
                                };
                                foreach (DBDictionaryEntry pe in rowDict)
                                {
                                    if (pe.Key == K_PIECE_META) continue;
                                    var ptv = ReadXRecord(tr, rowDict, pe.Key);
                                    if (ptv == null || ptv.Length == 0) continue;
                                    int pj = 0;
                                    ComponentRole role;
                                    if (!Enum.TryParse(ReadStr(ptv, ref pj), out role)) continue;
                                    var ex = new PieceExclusion { Role = role };
                                    while (pj < ptv.Length) ex.AllowedHeightsMm.Add(ReadDbl(ptv, ref pj));
                                    row.Roles.Add(ex);
                                }
                                net.PieceExclusionRows.Add(row);
                            }
                        }

                        // Exceptions (AG_GUID-keyed) — scoped to this network.
                        net.Exceptions = ReadExceptions(tr, netDict);

                        result.NetworkRules.Add(net);
                    }
                }

                tr.Commit();
            }
            return result;
        }

        /// <summary>True if the DWG has a stored project rule set.</summary>
        public static bool HasData(Database db)
        {
            if (db == null) return false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                bool found = GetRootReadOnly(tr, db) != null;
                tr.Commit();
                return found;
            }
        }

        /// <summary>Reads only the mode flag (cheap — no network walk). TypeMapping if absent.</summary>
        public static CalcMode LoadCalcMode(Database db)
        {
            if (db == null) return CalcMode.TypeMapping;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var rootDict = GetRootReadOnly(tr, db);
                var meta = rootDict == null ? null : ReadXRecord(tr, rootDict, K_META);
                CalcMode mode = CalcMode.TypeMapping;
                if (meta != null)
                {
                    int i = 0;
                    ReadStr(meta, ref i);                 // skip SchemaVersion
                    mode = (CalcMode)ReadI16(meta, ref i);
                }
                tr.Commit();
                return mode;
            }
        }

        // =====================================================================
        // Exceptions (per-network ISTISNALAR subdict)
        // =====================================================================

        private static void WriteExceptions(Transaction tr, DBDictionary netDict, ProjectExceptions exc)
        {
            var excDict = MakeSubDict(tr, netDict, K_EXC);

            var pipeExcDict = MakeSubDict(tr, excDict, K_EXC_PIPE);
            for (int i = 0; i < exc.PipeFamily.Count; i++)
            {
                var e = exc.PipeFamily[i];
                MakeXRecord(tr, pipeExcDict, "EX_" + i.ToString("D3"),
                    Str(e.AgGuid ?? ""),
                    Str(e.PipeFamilyId.ToString()),
                    Str(e.PipeSinif ?? ""),
                    Str(e.OverrideLabel ?? ""),
                    Str(e.EntityName ?? ""));
            }

            var mhExcDict = MakeSubDict(tr, excDict, K_EXC_MH);
            for (int i = 0; i < exc.ManholeFamily.Count; i++)
            {
                var e = exc.ManholeFamily[i];
                MakeXRecord(tr, mhExcDict, "EX_" + i.ToString("D3"),
                    Str(e.AgGuid ?? ""),
                    Str(e.ManholeFamilyId.ToString()),
                    Str(e.OverrideLabel ?? ""),
                    Str(e.EntityName ?? ""),
                    Dbl(e.ManholeDiameterMm));
            }
        }

        private static ProjectExceptions ReadExceptions(Transaction tr, DBDictionary netDict)
        {
            var exc     = new ProjectExceptions();
            var excDict = GetSubDict(tr, netDict, K_EXC);
            if (excDict == null) return exc;

            var pipeExcDict = GetSubDict(tr, excDict, K_EXC_PIPE);
            if (pipeExcDict != null)
                foreach (DBDictionaryEntry e in pipeExcDict)
                {
                    var tvs = ReadXRecord(tr, pipeExcDict, e.Key);
                    if (tvs == null) continue;
                    int i = 0;
                    var ex = new PipeFamilyException { AgGuid = ReadStr(tvs, ref i) };
                    Guid pf; Guid.TryParse(ReadStr(tvs, ref i), out pf); ex.PipeFamilyId = pf;
                    ex.PipeSinif     = ReadStr(tvs, ref i);
                    ex.OverrideLabel = ReadStr(tvs, ref i);
                    ex.EntityName    = ReadStr(tvs, ref i);
                    if (!string.IsNullOrEmpty(ex.AgGuid)) exc.PipeFamily.Add(ex);
                }

            var mhExcDict = GetSubDict(tr, excDict, K_EXC_MH);
            if (mhExcDict != null)
                foreach (DBDictionaryEntry e in mhExcDict)
                {
                    var tvs = ReadXRecord(tr, mhExcDict, e.Key);
                    if (tvs == null) continue;
                    int i = 0;
                    var ex = new ManholeFamilyException { AgGuid = ReadStr(tvs, ref i) };
                    Guid mf; Guid.TryParse(ReadStr(tvs, ref i), out mf); ex.ManholeFamilyId = mf;
                    ex.OverrideLabel      = ReadStr(tvs, ref i);
                    ex.EntityName         = ReadStr(tvs, ref i);
                    ex.ManholeDiameterMm  = ReadDbl(tvs, ref i);   // 0 for pre-diameter DWGs
                    if (!string.IsNullOrEmpty(ex.AgGuid)) exc.ManholeFamily.Add(ex);
                }

            return exc;
        }

        // =====================================================================
        // NOD navigation
        // =====================================================================

        private static DBDictionary GetOrCreateBoqRoot(Transaction tr, Database db)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (nod.Contains(NOD_ROOT))
            {
                var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
                root.UpgradeOpen();
                return root;
            }
            nod.UpgradeOpen();
            var boqRoot = new DBDictionary { TreatElementsAsHard = true };
            nod.SetAt(NOD_ROOT, boqRoot);
            tr.AddNewlyCreatedDBObject(boqRoot, true);
            return boqRoot;
        }

        private static DBDictionary GetRootReadOnly(Transaction tr, Database db)
        {
            var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(NOD_ROOT)) return null;
            var boqRoot = (DBDictionary)tr.GetObject(nod.GetAt(NOD_ROOT), OpenMode.ForRead);
            if (!boqRoot.Contains(K_ROOT)) return null;
            return (DBDictionary)tr.GetObject(boqRoot.GetAt(K_ROOT), OpenMode.ForRead);
        }

        // =====================================================================
        // Low-level helpers (same pattern as ProjectTemplateNodManager)
        // =====================================================================

        private static DBDictionary MakeSubDict(Transaction tr, DBDictionary parent, string key)
        {
            var d = new DBDictionary { TreatElementsAsHard = true };
            parent.SetAt(key, d);
            tr.AddNewlyCreatedDBObject(d, true);
            return d;
        }

        private static DBDictionary GetSubDict(Transaction tr, DBDictionary parent, string key)
        {
            if (parent == null || !parent.Contains(key)) return null;
            return tr.GetObject(parent.GetAt(key), OpenMode.ForRead) as DBDictionary;
        }

        private static void EraseTree(Transaction tr, DBDictionary dict)
        {
            var ids = new List<ObjectId>();
            foreach (DBDictionaryEntry e in dict) ids.Add(e.Value);
            foreach (var id in ids)
            {
                var obj = tr.GetObject(id, OpenMode.ForWrite);
                var sub = obj as DBDictionary;
                if (sub != null) EraseTree(tr, sub);
                obj.Erase();
            }
        }

        private static void MakeXRecord(Transaction tr, DBDictionary parent, string key, params TypedValue[] tvs)
        {
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

        // ── TypedValue factories / readers ────────────────────────────────────
        private static TypedValue Str(string v) => new TypedValue((int)DxfCode.Text,  v ?? "");
        private static TypedValue I16(short v)  => new TypedValue((int)DxfCode.Int16, v);
        private static TypedValue Dbl(double v) => new TypedValue((int)DxfCode.Real,  v);

        private static string ReadStr(TypedValue[] tvs, ref int i)
            => (i < tvs.Length) ? (tvs[i++].Value as string ?? "") : "";

        private static double ReadDbl(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            return tv.Value is double d ? d : 0;
        }

        private static short ReadI16(TypedValue[] tvs, ref int i)
        {
            if (i >= tvs.Length) return 0;
            var tv = tvs[i++];
            if (tv.Value is short s) return s;
            if (tv.Value is int n)   return (short)n;
            return 0;
        }

        // ── Key sanitizer + uniquifier (mirrors DwgBoQStore) ──────────────────
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

        private static string UniqueKey(DBDictionary parent, string baseKey)
        {
            if (!parent.Contains(baseKey)) return baseKey;
            for (int i = 1; i < 100000; i++)
            {
                string k = baseKey + "~" + i;
                if (!parent.Contains(k)) return k;
            }
            return baseKey + "~" + Guid.NewGuid().ToString("N");
        }
    }
}
