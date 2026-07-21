using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.DolguCatalog.Models;

namespace UrbanoMetraj.BoQ.DolguCatalog.Services
{
    public static class DolguCatalogXmlManager
    {
        public static readonly string InternalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj", "DolguCatalog.xml");

        public static void SaveInternal(IEnumerable<DolguMalzemesi> items)
            => Write(items, InternalPath);

        public static void Export(IEnumerable<DolguMalzemesi> items, string path)
            => Write(items, path);

        private static void Write(IEnumerable<DolguMalzemesi> items, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);

            var root = new XElement("DolguCatalog",
                new XAttribute("exportedAt", DateTime.UtcNow.ToString("o")));

            foreach (var d in items)
                root.Add(new XElement("Dolgu",
                    new XAttribute("id",          d.Id.ToString("D")),
                    new XAttribute("boqItemCode", d.BoqItemCode ?? ""),
                    new XAttribute("dolguAdi",    d.DolguAdi   ?? ""),
                    new XAttribute("aciklama",    d.Aciklama   ?? "")));

            new XDocument(new XDeclaration("1.0", "utf-8", null), root)
                .Save(path);
        }

        public static List<DolguMalzemesi> LoadInternal()
            => File.Exists(InternalPath) ? Import(InternalPath) : new List<DolguMalzemesi>();

        public static List<DolguMalzemesi> Import(string path)
        {
            var list = new List<DolguMalzemesi>();
            var doc  = XDocument.Load(path);

            foreach (var el in doc.Root?.Elements("Dolgu") ?? System.Linq.Enumerable.Empty<XElement>())
            {
                Guid.TryParse((string)el.Attribute("id"), out Guid id);

                list.Add(new DolguMalzemesi
                {
                    Id          = id == Guid.Empty ? Guid.NewGuid() : id,
                    BoqItemCode = (string)el.Attribute("boqItemCode") ?? "",
                    DolguAdi    = (string)el.Attribute("dolguAdi")    ?? "",
                    Aciklama    = (string)el.Attribute("aciklama")    ?? ""
                });
            }
            return list;
        }
    }
}
