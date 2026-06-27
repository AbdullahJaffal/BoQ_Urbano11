using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.SoilCatalog.Models;

namespace UrbanoMetraj.BoQ.SoilCatalog.Services
{
    /// <summary>
    /// XML read/write for the soil classification catalog.
    ///
    /// Internal path (Kaydet / auto-load):
    ///   %APPDATA%\UrbanoMetraj\SoilCatalog.xml
    ///
    /// Schema:
    ///   &lt;SoilCatalog exportedAt="ISO-8601"&gt;
    ///     &lt;Soil id="guid" soilName="..." bulkingFactor="1.20" boqItemCode="..."/&gt;
    ///   &lt;/SoilCatalog&gt;
    /// </summary>
    public static class SoilCatalogXmlManager
    {
        public static readonly string InternalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj", "SoilCatalog.xml");

        // ── Write ─────────────────────────────────────────────────────────────

        public static void SaveInternal(IEnumerable<SoilClassification> items)
            => Write(items, InternalPath);

        public static void Export(IEnumerable<SoilClassification> items, string path)
            => Write(items, path);

        private static void Write(IEnumerable<SoilClassification> items, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);

            var root = new XElement("SoilCatalog",
                new XAttribute("exportedAt", DateTime.UtcNow.ToString("o")));

            foreach (var s in items)
                root.Add(new XElement("Soil",
                    new XAttribute("id",            s.Id.ToString("D")),
                    new XAttribute("soilName",      s.SoilName      ?? ""),
                    new XAttribute("bulkingFactor", s.BulkingFactor.ToString("G")),
                    new XAttribute("boqItemCode",   s.BoqItemCode   ?? "")));

            new XDocument(new XDeclaration("1.0", "utf-8", null), root)
                .Save(path);
        }

        // ── Read ──────────────────────────────────────────────────────────────

        public static List<SoilClassification> LoadInternal()
            => File.Exists(InternalPath) ? Import(InternalPath) : new List<SoilClassification>();

        public static List<SoilClassification> Import(string path)
        {
            var list = new List<SoilClassification>();
            var doc  = XDocument.Load(path);

            foreach (var el in doc.Root?.Elements("Soil") ?? System.Linq.Enumerable.Empty<XElement>())
            {
                Guid.TryParse((string)el.Attribute("id"), out Guid id);
                double.TryParse((string)el.Attribute("bulkingFactor"),
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double bf);

                list.Add(new SoilClassification
                {
                    Id            = id == Guid.Empty ? Guid.NewGuid() : id,
                    SoilName      = (string)el.Attribute("soilName")    ?? "",
                    BulkingFactor = bf > 0 ? bf : 1.0,
                    BoqItemCode   = (string)el.Attribute("boqItemCode") ?? ""
                });
            }
            return list;
        }
    }
}
