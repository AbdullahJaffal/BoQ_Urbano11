using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Services
{
    /// <summary>
    /// Tri-modal pipe catalog import/export.
    ///
    /// Mode A — Native XML  : our own schema (round-trips without loss).
    /// Mode B — Urbano catalog XML : parses &lt;catalog reference="PIPE"&gt; from a
    ///           standalone Urbano catalog export or from the full ARS_EXPORT_XML.
    ///           Properties use t= / v= attributes on &lt;pEx&gt; elements.
    ///           DN is parsed from the item name string ("300 mm MBB" → 300 mm).
    ///           PIPE_DU = outer diameter (hex-float), PIPE_DV = inner diameter,
    ///           PIPE_T = wall thickness.  All values may be zero if Urbano hasn't
    ///           filled them in — only the name-based DN is reliable.
    /// Mode C — Live DWG    : handled entirely by PipeCatalogCommand, which
    ///           follows the same STA-thread + Application.Idle contract as
    ///           BoQCommand, then calls ImportUrbanoXml on the temp file.
    /// </summary>
    public static class PipeCatalogImportService
    {
        // ── Compiled helpers ───────────────────────────────────────────────────

        // C99 hex-float:  [sign] 0x H.H p±E
        private static readonly Regex HexFloatRx = new Regex(
            @"[-+]?0x[0-9a-fA-F]+\.[0-9a-fA-F]+p[-+]?[0-9]+",
            RegexOptions.Compiled);

        // Extracts the leading integer before " mm" in pipe names ("300 mm MBB" → "300")
        private static readonly Regex DnFromNameRx = new Regex(
            @"(\d+(?:\.\d+)?)\s*mm",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // Strips <?xml ...?> declarations so we can re-parse with a fixed encoding
        private static readonly Regex XmlDeclRx = new Regex(
            @"<\?xml\b[^?]*\?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        // ══════════════════════════════════════════════════════════════════════
        // MODE A — Native XML
        // ══════════════════════════════════════════════════════════════════════

        public static PipeCatalog ImportNativeXml(string filePath)
        {
            var root    = LoadXmlRobust(filePath).Root;
            var catalog = new PipeCatalog();

            if (root?.Attribute("lastModified") != null)
                catalog.LastModified = DateTime.Parse(root.Attribute("lastModified").Value);

            // Catalog-level shared classes
            foreach (var clsEl in root?.Elements("Class")
                                  ?? System.Linq.Enumerable.Empty<XElement>())
            {
                string cls = clsEl.Attribute("name")?.Value ?? "";
                if (!string.IsNullOrEmpty(cls)) catalog.Classes.Add(cls);
            }

            foreach (var famEl in root?.Elements("PipeFamily")
                                  ?? System.Linq.Enumerable.Empty<XElement>())
            {
                var fam = new PipeFamily
                {
                    Id         = ParseGuid(famEl.Attribute("id")?.Value),
                    FamilyName = famEl.Attribute("name")?.Value     ?? "",
                    Material   = famEl.Attribute("material")?.Value ?? ""
                };
                foreach (var pipeEl in famEl.Elements("PipeDefinition"))
                {
                    fam.Pipes.Add(new PipeDefinition
                    {
                        Id              = ParseGuid(pipeEl.Attribute("id")?.Value),
                        PozNo           = pipeEl.Attribute("pozNo")?.Value    ?? "",
                        NominalDiameter = ParseDouble(pipeEl.Attribute("dn")?.Value),
                        OuterDiameter   = ParseDouble(pipeEl.Attribute("od")?.Value),
                        InnerDiameter   = ParseDouble(pipeEl.Attribute("id_mm")?.Value),
                        WallThickness   = ParseDouble(pipeEl.Attribute("wt")?.Value),
                        Sinif           = pipeEl.Attribute("sinif")?.Value    ?? "",
                        Aciklama        = pipeEl.Attribute("aciklama")?.Value ?? ""
                    });
                }
                catalog.Families.Add(fam);
            }
            return catalog;
        }

        public static void ExportNativeXml(PipeCatalog catalog, string filePath)
        {
            var root = new XElement("PipeCatalog",
                new XAttribute("lastModified", catalog.LastModified.ToString("O")));

            // Catalog-level shared classes
            foreach (var cls in catalog.Classes)
                root.Add(new XElement("Class", new XAttribute("name", cls)));

            foreach (var fam in catalog.Families)
            {
                var famEl = new XElement("PipeFamily",
                    new XAttribute("id",       fam.Id),
                    new XAttribute("name",     fam.FamilyName),
                    new XAttribute("material", fam.Material));

                foreach (var pipe in fam.Pipes)
                    famEl.Add(new XElement("PipeDefinition",
                        new XAttribute("id",       pipe.Id),
                        new XAttribute("pozNo",    pipe.PozNo    ?? ""),
                        new XAttribute("dn",       pipe.NominalDiameter),
                        new XAttribute("od",       pipe.OuterDiameter),
                        new XAttribute("id_mm",    pipe.InnerDiameter),
                        new XAttribute("wt",       pipe.WallThickness),
                        new XAttribute("sinif",    pipe.Sinif    ?? ""),
                        new XAttribute("aciklama", pipe.Aciklama ?? "")));

                root.Add(famEl);
            }
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(filePath);
            catalog.LastModified = DateTime.Now;
        }

        // ══════════════════════════════════════════════════════════════════════
        // MODE B — Urbano catalog XML
        // ══════════════════════════════════════════════════════════════════════

        /// <summary>
        /// Parses an Urbano pipe catalog XML file.
        ///
        /// The file may be either:
        ///   • A standalone Urbano catalog export whose root is
        ///     &lt;catalog reference="PIPE"&gt;
        ///   • The full ARS_EXPORT_XML project export, in which case the parser
        ///     locates &lt;catalog reference="PIPE"&gt; anywhere inside the document.
        ///
        /// Pipe family   = &lt;catalogItem isGroup="1"&gt; — CATALOGITEM_NAME
        ///                 gives the family name (stripped of 4-char type prefix).
        /// Pipe definition = &lt;catalogItem isGroup="0"&gt; nested directly inside
        ///                 the family element.
        ///                 • DN  : extracted from the name string ("300 mm MBB" → 300)
        ///                 • OD  : PIPE_DU attribute (hex-float with 4-char prefix)
        ///                 • ID  : PIPE_DV attribute
        ///                 • WT  : PIPE_T  attribute
        ///                 All dimension fields may be 0 in this catalog format.
        /// </summary>
        public static PipeCatalog ImportUrbanoXml(string xmlPath)
        {
            var xdoc    = LoadXmlRobust(xmlPath);
            var catalog = new PipeCatalog();

            XElement catalogRoot = FindPipeCatalogRoot(xdoc);
            if (catalogRoot == null) return catalog;

            CollectFamilies(catalogRoot, catalog);
            return catalog;
        }

        // ── Private: structure traversal ──────────────────────────────────────

        private static XElement FindPipeCatalogRoot(XDocument doc)
        {
            // Standalone catalog: root IS the <catalog reference="PIPE"> element
            if (doc.Root?.Attribute("reference")?.Value == "PIPE")
                return doc.Root;

            // Full ARS_EXPORT_XML: catalog is nested somewhere inside
            foreach (var el in doc.Descendants("catalog"))
                if (el.Attribute("reference")?.Value == "PIPE")
                    return el;

            return null;
        }

        private static void CollectFamilies(XElement root, PipeCatalog catalog)
        {
            // isGroup="1" items are pipe families (search recursively in root)
            foreach (var familyEl in root.Descendants("catalogItem"))
            {
                if (familyEl.Attribute("isGroup")?.Value != "1") continue;

                var famProps  = GetPexProps(familyEl);
                string name   = StripTypePrefix(GetProp(famProps, "CATALOGITEM_NAME"));
                if (string.IsNullOrWhiteSpace(name)) continue;

                var family = new PipeFamily { FamilyName = name };

                // Direct <catalogItem isGroup="0"> children are the pipe rows
                foreach (var pipeEl in familyEl.Elements("catalogItem"))
                {
                    if (pipeEl.Attribute("isGroup")?.Value == "1") continue; // sub-group

                    var pipeProps = GetPexProps(pipeEl);
                    string pipeName = StripTypePrefix(GetProp(pipeProps, "CATALOGITEM_NAME"));
                    double dn = ExtractDnMm(pipeName);
                    if (dn <= 0) continue;

                    double du  = DecodeHexDouble(GetProp(pipeProps, "PIPE_DU"));
                    double dv  = DecodeHexDouble(GetProp(pipeProps, "PIPE_DV"));
                    double wt  = DecodeHexDouble(GetProp(pipeProps, "PIPE_T"));
                    string mat = StripTypePrefix(GetProp(pipeProps, "PIPE_MATERIAL"));

                    // Derive whichever dimension is missing
                    if (wt <= 0 && du > 0 && dv > 0) wt = (du - dv) / 2.0;
                    if (dv <= 0 && du > 0 && wt > 0) dv = du - 2.0 * wt;
                    if (du <= 0) du = dn;  // use nominal as outer-diameter fallback

                    if (string.IsNullOrEmpty(family.Material) && !string.IsNullOrEmpty(mat))
                        family.Material = mat;

                    family.Pipes.Add(new PipeDefinition
                    {
                        NominalDiameter = dn,
                        OuterDiameter   = du,
                        InnerDiameter   = dv,
                        WallThickness   = wt
                    });
                }

                if (family.Pipes.Count > 0)
                    catalog.Families.Add(family);
            }
        }

        // ── pEx property extraction ────────────────────────────────────────────

        // Read all <pEx t="KEY" v="VALUE"/> from the direct <ppsEx><ct> of this item.
        private static Dictionary<string, string> GetPexProps(XElement catalogItem)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var ct   = catalogItem.Element("ppsEx")?.Element("ct");
            if (ct == null) return dict;
            foreach (var pex in ct.Elements("pEx"))
            {
                string t = pex.Attribute("t")?.Value;
                string v = pex.Attribute("v")?.Value ?? "";
                if (!string.IsNullOrEmpty(t)) dict[t] = v;
            }
            return dict;
        }

        private static string GetProp(Dictionary<string, string> d, string key)
        {
            string v;
            return d.TryGetValue(key, out v) ? v : "";
        }

        // "5005FamilyName" → "FamilyName"   (4-char tag prefix stripped)
        private static string StripTypePrefix(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Length > 4 ? s.Substring(4) : s;
        }

        // "300 mm MBB" → 300.0
        private static double ExtractDnMm(string name)
        {
            if (string.IsNullOrEmpty(name)) return 0;
            var m = DnFromNameRx.Match(name);
            if (!m.Success) return 0;
            double v;
            return double.TryParse(m.Groups[1].Value,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out v) ? v : 0;
        }

        // ── Hex-float decoder ─────────────────────────────────────────────────

        private static double DecodeHexDouble(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return 0;

            // Strip 4-char type-tag prefix (8001, 8005, 5003, …) if present
            if (raw.Length > 4
                && !raw.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                && !raw.StartsWith("-")
                && !raw.StartsWith("+"))
                raw = raw.Substring(4);

            var m = HexFloatRx.Match(raw);
            if (!m.Success) return 0;
            try { return ParseHexFloat(m.Value); } catch { return 0; }
        }

        private static double ParseHexFloat(string hexFloat)
        {
            bool   neg = hexFloat.StartsWith("-");
            string s   = hexFloat.TrimStart('-', '+');

            int pIdx = s.IndexOf('p');
            if (pIdx < 0) pIdx = s.IndexOf('P');
            if (pIdx < 0) return 0;

            string mantissaPart = s.Substring(2, pIdx - 2);  // skip "0x"
            int    exp          = int.Parse(s.Substring(pIdx + 1));

            string[] parts = mantissaPart.Split('.');
            long   ipart   = Convert.ToInt64(parts[0], 16);
            double fpart   = 0;
            if (parts.Length > 1)
            {
                string frac  = parts[1];
                double denom = 1;
                long   num   = 0;
                foreach (char c in frac)
                {
                    denom *= 16;
                    num    = num * 16 + Convert.ToInt32(c.ToString(), 16);
                }
                fpart = num / denom;
            }
            double value = (ipart + fpart) * Math.Pow(2, exp);
            return neg ? -value : value;
        }

        // ── Robust XML loader (mirrors BoQParserService.LoadXmlRobust) ─────────

        private static XDocument LoadXmlRobust(string path)
        {
            try { return XDocument.Load(path); }
            catch (System.Xml.XmlException) { }

            byte[] raw = File.ReadAllBytes(path);
            // Try: system default, Windows-1254 (Turkish), cp-1252, ISO-8859-1
            foreach (int cp in new[] { 0, 1254, 1252, 28591 })
            {
                try
                {
                    string txt = cp == 0
                        ? Encoding.Default.GetString(raw)
                        : Encoding.GetEncoding(cp).GetString(raw);

                    // Strip or fix any existing <?xml?> declaration so XDocument.Parse
                    // accepts the re-encoded string as UTF-16
                    txt = XmlDeclRx.Replace(txt, "<?xml version=\"1.0\" encoding=\"utf-8\"?>");

                    return XDocument.Parse(txt);
                }
                catch { }
            }
            throw new InvalidOperationException(
                "XML yüklenemedi — UTF-8, sistem varsayılanı, cp1254, cp1252, ISO-8859-1 " +
                "kodlamaları denendi.");
        }

        // ── Misc helpers ──────────────────────────────────────────────────────

        private static Guid ParseGuid(string s) =>
            Guid.TryParse(s, out Guid g) ? g : Guid.NewGuid();

        private static double ParseDouble(string s) =>
            double.TryParse(s, System.Globalization.NumberStyles.Any,
                            System.Globalization.CultureInfo.InvariantCulture, out double v) ? v : 0;
    }
}
