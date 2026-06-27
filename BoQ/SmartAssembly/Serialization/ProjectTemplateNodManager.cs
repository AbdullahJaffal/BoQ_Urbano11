using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Autodesk.AutoCAD.DatabaseServices;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.Serialization
{
    /// <summary>
    /// Persists <see cref="ProjectTemplate"/> instances inside the active DWG
    /// Named Object Dictionary.
    ///
    /// NOD layout (surgical — only touches MANHOLE_TEMPLATES, leaves all other URBANO_BOQ data intact):
    /// <code>
    /// NOD["URBANO_BOQ"]                        (created on demand; coexists with pipe BoQ data)
    ///   "MANHOLE_TEMPLATES"                    (DBDictionary)
    ///     [TemplateName]                       (DBDictionary, key = SafeKey(TemplateName))
    ///       "TPL_META"                         (XRecord: Id · Name · ExcavProfile · MatOverride · LastModified)
    ///       "TPL_RULES"                        (DBDictionary)
    ///         "RULE_000"                       (XRecord: all AssemblyRule fields)
    ///         "RULE_001"                       ...
    /// </code>
    ///
    /// IMPORTANT: <see cref="DwgBoQStore.Save"/> replaces the entire URBANO_BOQ tree.
    /// Until that method is made template-aware, always save templates AFTER the pipe BoQ
    /// or reload them from the XML master catalog if the DWG is re-processed.
    /// </summary>
    public static class ProjectTemplateNodManager
    {
        private const string NOD_ROOT    = "URBANO_BOQ";
        private const string K_TEMPLATES = "MANHOLE_TEMPLATES";
        private const string K_TPL_META  = "TPL_META";
        private const string K_TPL_RULES = "TPL_RULES";

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>Upserts one template into the current DWG NOD.</summary>
        public static void SaveTemplate(Database db, ProjectTemplate template)
        {
            if (db == null)       throw new ArgumentNullException(nameof(db));
            if (template == null) throw new ArgumentNullException(nameof(template));
            if (string.IsNullOrWhiteSpace(template.TemplateName))
                throw new ArgumentException("Template adı boş olamaz.", nameof(template));

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var tplsDict = GetOrCreateTemplatesDict(tr, db);
                string key   = SafeKey(template.TemplateName);

                if (tplsDict.Contains(key))
                {
                    var old = (DBDictionary)tr.GetObject(
                        tplsDict.GetAt(key), OpenMode.ForWrite);
                    EraseTree(tr, old);
                    old.Erase();
                }

                var tplDict = MakeSubDict(tr, tplsDict, key);
                WriteTemplateMeta (tr, tplDict, template);
                WriteTemplateRules(tr, tplDict, template.LocalRules);

                tr.Commit();
            }
        }

        /// <summary>Loads every template stored under MANHOLE_TEMPLATES.</summary>
        public static List<ProjectTemplate> LoadAllTemplates(Database db)
        {
            var result = new List<ProjectTemplate>();
            if (db == null) return result;

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var tplsDict = GetTemplatesDictReadOnly(tr, db);
                if (tplsDict == null) { tr.Commit(); return result; }

                foreach (DBDictionaryEntry entry in tplsDict)
                {
                    var tplDict = tr.GetObject(entry.Value, OpenMode.ForRead) as DBDictionary;
                    if (tplDict == null) continue;
                    var tpl = ReadTemplate(tr, tplDict);
                    if (tpl == null) continue;
                    if (string.IsNullOrWhiteSpace(tpl.TemplateName))
                        tpl.TemplateName = entry.Key;
                    result.Add(tpl);
                }

                tr.Commit();
            }
            return result;
        }

        /// <summary>Returns true if at least one template exists in the current DWG.</summary>
        public static bool HasTemplates(Database db)
        {
            if (db == null) return false;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var d = GetTemplatesDictReadOnly(tr, db);
                bool found = d != null;
                tr.Commit();
                return found;
            }
        }

        /// <summary>Permanently removes the named template from the DWG NOD.</summary>
        public static bool DeleteTemplate(Database db, string templateName)
        {
            if (db == null || string.IsNullOrWhiteSpace(templateName)) return false;
            string key = SafeKey(templateName);

            using (var tr = db.TransactionManager.StartTransaction())
            {
                var tplsDict = GetTemplatesDictReadOnly(tr, db);
                if (tplsDict == null || !tplsDict.Contains(key))
                {
                    tr.Commit();
                    return false;
                }

                tplsDict.UpgradeOpen();
                var old = (DBDictionary)tr.GetObject(
                    tplsDict.GetAt(key), OpenMode.ForWrite);
                EraseTree(tr, old);
                old.Erase();
                tr.Commit();
                return true;
            }
        }

        // =====================================================================
        // Write helpers
        // =====================================================================

        private static void WriteTemplateMeta(
            Transaction tr, DBDictionary tplDict, ProjectTemplate t)
        {
            MakeXRecord(tr, tplDict, K_TPL_META,
                Str(t.Id.ToString()),
                Str(t.TemplateName              ?? ""),
                Str(t.AssociatedExcavationProfile ?? ""),
                Str(t.MaterialFamilyOverride     ?? ""),
                Str(t.LastModified.ToString("u", CultureInfo.InvariantCulture)
                                  .Replace(" ", "T")));
        }

        private static void WriteTemplateRules(
            Transaction tr, DBDictionary tplDict, List<AssemblyRule> rules)
        {
            var rulesDict = MakeSubDict(tr, tplDict, K_TPL_RULES);
            if (rules == null) return;
            for (int i = 0; i < rules.Count; i++)
            {
                var r   = rules[i];
                string k = "RULE_" + i.ToString("D3");
                MakeXRecord(tr, rulesDict, k,
                    Str(r.Id.ToString()),
                    Dbl(r.MinPipeDiameterMm),
                    Dbl(r.MaxPipeDiameterMm),
                    Dbl(r.MinDepthM),
                    Dbl(r.MaxDepthM),
                    Str(r.SelectedBaseId.ToString()),
                    I16(r.IsCastInSitu    ? (short)1 : (short)0),
                    I16(r.IsLocalOverride ? (short)1 : (short)0),
                    Str(r.SourceMasterRuleId.ToString()),
                    Str(r.Notes ?? ""));
            }
        }

        // =====================================================================
        // Read helpers
        // =====================================================================

        private static ProjectTemplate ReadTemplate(Transaction tr, DBDictionary tplDict)
        {
            var metaTvs = ReadXRecord(tr, tplDict, K_TPL_META);
            if (metaTvs == null) return null;

            int i = 0;
            Guid tid;
            Guid.TryParse(ReadStr(metaTvs, ref i), out tid);

            var tpl = new ProjectTemplate
            {
                Id                         = tid == Guid.Empty ? Guid.NewGuid() : tid,
                TemplateName               = ReadStr(metaTvs, ref i),
                AssociatedExcavationProfile = ReadStr(metaTvs, ref i),
                MaterialFamilyOverride     = ReadStr(metaTvs, ref i)
            };
            DateTime mod;
            if (DateTime.TryParse(ReadStr(metaTvs, ref i), CultureInfo.InvariantCulture,
                    DateTimeStyles.None, out mod))
                tpl.LastModified = mod;

            var rulesDict = GetSubDict(tr, tplDict, K_TPL_RULES);
            if (rulesDict != null)
            {
                foreach (DBDictionaryEntry rEntry in rulesDict)
                {
                    var tvs = ReadXRecord(tr, rulesDict, rEntry.Key);
                    if (tvs == null) continue;
                    int ri = 0;
                    Guid rid, baseId, srcId;
                    Guid.TryParse(ReadStr(tvs, ref ri), out rid);
                    var rule = new AssemblyRule
                    {
                        Id                = rid == Guid.Empty ? Guid.NewGuid() : rid,
                        MinPipeDiameterMm = ReadDbl(tvs, ref ri),
                        MaxPipeDiameterMm = ReadDbl(tvs, ref ri),
                        MinDepthM         = ReadDbl(tvs, ref ri),
                        MaxDepthM         = ReadDbl(tvs, ref ri),
                    };
                    Guid.TryParse(ReadStr(tvs, ref ri), out baseId);
                    rule.SelectedBaseId  = baseId;
                    rule.IsCastInSitu    = ReadI16(tvs, ref ri) == 1;
                    rule.IsLocalOverride = ReadI16(tvs, ref ri) == 1;
                    Guid.TryParse(ReadStr(tvs, ref ri), out srcId);
                    rule.SourceMasterRuleId = srcId;
                    rule.Notes           = ReadStr(tvs, ref ri);
                    tpl.LocalRules.Add(rule);
                }
            }

            return tpl;
        }

        // =====================================================================
        // NOD navigation
        // =====================================================================

        private static DBDictionary GetOrCreateTemplatesDict(Transaction tr, Database db)
        {
            var nod = (DBDictionary)tr.GetObject(
                db.NamedObjectsDictionaryId, OpenMode.ForRead);

            DBDictionary boqRoot;
            if (nod.Contains(NOD_ROOT))
            {
                boqRoot = (DBDictionary)tr.GetObject(
                    nod.GetAt(NOD_ROOT), OpenMode.ForRead);
            }
            else
            {
                nod.UpgradeOpen();
                boqRoot = new DBDictionary { TreatElementsAsHard = true };
                nod.SetAt(NOD_ROOT, boqRoot);
                tr.AddNewlyCreatedDBObject(boqRoot, true);
            }

            if (boqRoot.Contains(K_TEMPLATES))
            {
                return (DBDictionary)tr.GetObject(
                    boqRoot.GetAt(K_TEMPLATES), OpenMode.ForWrite);
            }

            boqRoot.UpgradeOpen();
            return MakeSubDict(tr, boqRoot, K_TEMPLATES);
        }

        private static DBDictionary GetTemplatesDictReadOnly(Transaction tr, Database db)
        {
            var nod = (DBDictionary)tr.GetObject(
                db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(NOD_ROOT)) return null;
            var boqRoot = (DBDictionary)tr.GetObject(
                nod.GetAt(NOD_ROOT), OpenMode.ForRead);
            if (!boqRoot.Contains(K_TEMPLATES)) return null;
            return (DBDictionary)tr.GetObject(
                boqRoot.GetAt(K_TEMPLATES), OpenMode.ForRead);
        }

        // =====================================================================
        // Low-level helpers (same pattern as ManholeCatalogStore / DwgBoQStore)
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

        private static void MakeXRecord(
            Transaction tr, DBDictionary parent, string key, params TypedValue[] tvs)
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

        // ── TypedValue factories ──────────────────────────────────────────────
        private static TypedValue Str(string v) => new TypedValue((int)DxfCode.Text,  v ?? "");
        private static TypedValue Dbl(double v) => new TypedValue((int)DxfCode.Real,  v);
        private static TypedValue I16(short v)  => new TypedValue((int)DxfCode.Int16, v);

        // ── TypedValue readers ────────────────────────────────────────────────
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
