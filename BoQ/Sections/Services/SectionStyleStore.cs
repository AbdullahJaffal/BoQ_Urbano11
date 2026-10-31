using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// Persistence for UT_CROSS_SECTIONS:
    ///  • style templates (Şablonlar) — one XML per template under
    ///    %AppData%\UrbanoMetraj\SectionStyles (shared by every project, like the catalogs);
    ///  • per drawing — last style + run options + last run (id, insertion point) in the
    ///    NOD under its own top-level key, so a BoQ Save (which rewrites URBANO_BOQ) never touches it.
    /// </summary>
    public static class SectionStyleStore
    {
        public static readonly string TemplateFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UrbanoMetraj", "SectionStyles");

        private const string NOD_KEY   = "URBANO_CROSS_SECTIONS";
        private const string K_STYLE   = "STYLE";
        private const string K_RUN     = "RUN";
        private const string K_LASTRUN = "LAST_RUN";
        private const int    Chunk     = 1000;   // xrecord text chunk length

        // ── Templates ────────────────────────────────────────────────────────

        public static List<SectionStyle> LoadTemplates()
        {
            var res = new List<SectionStyle>();
            try
            {
                if (Directory.Exists(TemplateFolder))
                    foreach (var f in Directory.GetFiles(TemplateFolder, "*.xml").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                        try { res.Add(SectionStyle.FromXml(XDocument.Load(f).Root)); } catch { /* skip unreadable */ }
            }
            catch { }
            if (!res.Any(s => s.Name == "Varsayılan")) res.Insert(0, SectionStyle.Default());
            return res;
        }

        public static void SaveTemplate(SectionStyle style)
        {
            Directory.CreateDirectory(TemplateFolder);
            new XDocument(style.ToXml()).Save(Path.Combine(TemplateFolder, SafeFile(style.Name) + ".xml"));
        }

        public static void DeleteTemplate(string name)
        {
            var p = Path.Combine(TemplateFolder, SafeFile(name) + ".xml");
            if (File.Exists(p)) File.Delete(p);
        }

        public static void Export(SectionStyle style, string path) => new XDocument(style.ToXml()).Save(path);
        public static SectionStyle Import(string path) => SectionStyle.FromXml(XDocument.Load(path).Root);

        private static string SafeFile(string name)
        {
            var s = string.IsNullOrWhiteSpace(name) ? "Sablon" : name.Trim();
            foreach (char c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_');
            return s;
        }

        // ── Drawing (NOD) ────────────────────────────────────────────────────

        public sealed class LastRun
        {
            public string  RunId;
            public Point3d Insert;
            public bool    IsValid => !string.IsNullOrEmpty(RunId);
        }

        public static SectionStyle LoadStyle(Database db)
        {
            string xml = ReadText(db, K_STYLE);
            if (string.IsNullOrEmpty(xml)) return SectionStyle.Default();
            try { return SectionStyle.FromXml(XElement.Parse(xml)); } catch { return SectionStyle.Default(); }
        }

        public static SectionRunOptions LoadRunOptions(Database db)
        {
            string xml = ReadText(db, K_RUN);
            if (string.IsNullOrEmpty(xml)) return new SectionRunOptions();
            try { return SectionRunOptions.FromXml(XElement.Parse(xml)); } catch { return new SectionRunOptions(); }
        }

        public static LastRun LoadLastRun(Database db)
        {
            var tvs = Read(db, K_LASTRUN);
            if (tvs == null || tvs.Length < 3) return new LastRun();
            return new LastRun
            {
                RunId  = tvs[0].Value as string,
                Insert = new Point3d(tvs[1].Value is double x ? x : 0, tvs[2].Value is double y ? y : 0, 0)
            };
        }

        public static void Save(Database db, SectionStyle style, SectionRunOptions run)
        {
            WriteText(db, K_STYLE, style.ToXml().ToString(SaveOptions.DisableFormatting));
            WriteText(db, K_RUN,   run.ToXml().ToString(SaveOptions.DisableFormatting));
        }

        public static void SaveLastRun(Database db, string runId, Point3d insert)
            => Write(db, K_LASTRUN, new[]
            {
                new TypedValue((int)DxfCode.Text, runId ?? ""),
                new TypedValue((int)DxfCode.Real, insert.X),
                new TypedValue((int)DxfCode.Real, insert.Y)
            });

        private static void WriteText(Database db, string key, string text)
        {
            var tvs = new List<TypedValue>();
            text = text ?? "";
            for (int i = 0; i < text.Length; i += Chunk)
                tvs.Add(new TypedValue((int)DxfCode.Text, text.Substring(i, Math.Min(Chunk, text.Length - i))));
            if (tvs.Count == 0) tvs.Add(new TypedValue((int)DxfCode.Text, ""));
            Write(db, key, tvs.ToArray());
        }

        private static string ReadText(Database db, string key)
        {
            var tvs = Read(db, key);
            if (tvs == null) return null;
            var sb = new StringBuilder();
            foreach (var tv in tvs) sb.Append(tv.Value as string ?? "");
            return sb.ToString();
        }

        private static void Write(Database db, string key, TypedValue[] values)
        {
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                DBDictionary root;
                if (nod.Contains(NOD_KEY)) root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForWrite);
                else
                {
                    root = new DBDictionary();
                    nod.SetAt(NOD_KEY, root);
                    tr.AddNewlyCreatedDBObject(root, true);
                }
                if (root.Contains(key))
                {
                    var old = (Xrecord)tr.GetObject(root.GetAt(key), OpenMode.ForWrite);
                    old.Data = new ResultBuffer(values);
                }
                else
                {
                    var xr = new Xrecord { Data = new ResultBuffer(values) };
                    root.SetAt(key, xr);
                    tr.AddNewlyCreatedDBObject(xr, true);
                }
                tr.Commit();
            }
        }

        private static TypedValue[] Read(Database db, string key)
        {
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    if (!nod.Contains(NOD_KEY)) return null;
                    var root = (DBDictionary)tr.GetObject(nod.GetAt(NOD_KEY), OpenMode.ForRead);
                    if (!root.Contains(key)) return null;
                    var xr = tr.GetObject(root.GetAt(key), OpenMode.ForRead) as Xrecord;
                    return xr?.Data?.AsArray();
                }
            }
            catch (Exception) { return null; }
        }
    }
}
