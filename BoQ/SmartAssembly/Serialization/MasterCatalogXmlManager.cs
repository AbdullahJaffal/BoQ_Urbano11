using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.Serialization
{
    /// <summary>
    /// XML persistence for <see cref="SmartAssemblyMasterCatalog"/>.
    ///
    /// Two separate fixed-path files mirror the PipeCatalog pattern:
    ///   SmartAssemblyComponents.xml  ← Tab 1 (Ana Bileşen Deposu)
    ///   SmartAssemblyRules.xml       ← Tab 2 (Ana Kural Matrisi)
    /// </summary>
    public static class MasterCatalogXmlManager
    {
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        // ── Fixed default paths (AppData) ─────────────────────────────────────

        public static string DefaultComponentsPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UrbanoMetraj", "SmartAssemblyComponents.xml");

        public static string DefaultRulesPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "UrbanoMetraj", "SmartAssemblyRules.xml");

        // ── Family export / import (current format) ───────────────────────────

        public static void ExportFamilies(SmartAssemblyMasterCatalog catalog, string filePath)
        {
            EnsureDir(filePath);
            var root = new XElement("SmartAssemblyComponents",
                new XAttribute("Version",      "2.0"),
                new XAttribute("LastModified", UtcNow()));
            foreach (var f in catalog.Families)
                root.Add(SerializeFamily(f));
            SaveDoc(root, filePath);
        }

        public static List<ComponentFamily> ImportFamilies(string filePath)
        {
            var result = new List<ComponentFamily>();
            var root = XDocument.Load(filePath).Root;
            if (root == null) return result;

            // Current format (v2): <Family> children
            foreach (var fEl in root.Elements("Family"))
            {
                var f = DeserializeFamily(fEl);
                if (f != null) result.Add(f);
            }

            // Legacy format (v1): flat <Component> children — migrate into one default family
            var legacyComps = new List<ManholeComponent>();
            foreach (var cEl in root.Elements("Component"))
            {
                var c = DeserializeComponent(cEl);
                if (c != null) legacyComps.Add(c);
            }
            if (legacyComps.Count > 0)
            {
                var migrated = new ComponentFamily { Name = "Genel", Malzeme = "" };
                foreach (var c in legacyComps) migrated.Components.Add(c);
                result.Insert(0, migrated);
            }

            return result;
        }

        private static XElement SerializeFamily(ComponentFamily f)
        {
            var el = new XElement("Family",
                new XAttribute("Id",      f.Id),
                new XAttribute("Name",    f.Name    ?? ""),
                new XAttribute("Malzeme", f.Malzeme ?? ""));
            foreach (var c in f.Components)
                el.Add(SerializeComponent(c));
            return el;
        }

        private static ComponentFamily DeserializeFamily(XElement el)
        {
            if (el == null) return null;
            Guid id;
            Guid.TryParse((string)el.Attribute("Id"), out id);
            var f = new ComponentFamily
            {
                Id      = id == Guid.Empty ? Guid.NewGuid() : id,
                Name    = (string)el.Attribute("Name")    ?? "",
                Malzeme = (string)el.Attribute("Malzeme") ?? ""
            };
            foreach (var cEl in el.Elements("Component"))
            {
                var c = DeserializeComponent(cEl);
                if (c != null) f.Components.Add(c);
            }
            return f;
        }

        // ── Legacy flat-components export / import (kept for backward compat) ──

        public static void ExportComponents(SmartAssemblyMasterCatalog catalog, string filePath)
            => ExportFamilies(catalog, filePath);

        public static List<ManholeComponent> ImportComponents(string filePath)
        {
            var result = new List<ManholeComponent>();
            foreach (var f in ImportFamilies(filePath))
                foreach (var c in f.Components)
                    result.Add(c);
            return result;
        }

        // ── Pipe-range rules export / import ──────────────────────────────────

        public static void ExportPipeRules(SmartAssemblyMasterCatalog catalog, string filePath)
        {
            EnsureDir(filePath);
            var el = new XElement("SmartAssemblyRules",
                new XAttribute("Version",      "2.0"),
                new XAttribute("LastModified", UtcNow()));

            // System types block
            var stEl = new XElement("SystemTypes");
            foreach (var st in catalog.SystemTypes ?? new System.Collections.ObjectModel.ObservableCollection<SystemType>())
                stEl.Add(new XElement("SystemType",
                    new XAttribute("Id",   st.Id),
                    new XAttribute("Name", st.Name ?? "")));
            el.Add(stEl);

            foreach (var pr in catalog.MasterPipeRules ?? new List<PipeRangeRule>())
                el.Add(SerializePipeRange(pr));
            SaveDoc(el, filePath);
        }

        public static List<SystemType> ImportSystemTypes(string filePath)
        {
            var result = new List<SystemType>();
            if (!File.Exists(filePath)) return result;
            var root = XDocument.Load(filePath).Root;
            var stEl = root?.Element("SystemTypes");
            if (stEl == null) return result;
            foreach (var el in stEl.Elements("SystemType"))
            {
                Guid id;
                Guid.TryParse((string)el.Attribute("Id"), out id);
                result.Add(new SystemType
                {
                    Id   = id == Guid.Empty ? Guid.NewGuid() : id,
                    Name = (string)el.Attribute("Name") ?? ""
                });
            }
            return result;
        }

        public static List<PipeRangeRule> ImportPipeRules(string filePath)
        {
            var result = new List<PipeRangeRule>();
            var root = XDocument.Load(filePath).Root;
            if (root == null) return result;
            foreach (var prEl in root.Elements("PipeRange"))
            {
                var pr = DeserializePipeRange(prEl);
                if (pr != null) result.Add(pr);
            }
            return result;
        }

        // ── Full catalog export / import (legacy + backward compat) ──────────

        public static void Export(SmartAssemblyMasterCatalog catalog, string filePath)
        {
            EnsureDir(filePath);
            var compsEl = new XElement("Components");
            foreach (var c in catalog.Components)
                compsEl.Add(SerializeComponent(c));

            var legacyEl = new XElement("MasterRules");
            foreach (var r in catalog.MasterRules)
                legacyEl.Add(SerializeLegacyRule(r));

            var pipeRulesEl = new XElement("MasterPipeRules");
            foreach (var pr in catalog.MasterPipeRules ?? new List<PipeRangeRule>())
                pipeRulesEl.Add(SerializePipeRange(pr));

            SaveDoc(
                new XElement("SmartAssemblyMasterCatalog",
                    new XAttribute("Version",      catalog.Version ?? "1.0"),
                    new XAttribute("CatalogId",    catalog.CatalogId),
                    new XAttribute("LastModified", UtcNow()),
                    compsEl, legacyEl, pipeRulesEl),
                filePath);
        }

        public static SmartAssemblyMasterCatalog Import(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Master katalog XML dosyası bulunamadı.", filePath);

            var root = XDocument.Load(filePath).Root;
            if (root == null || root.Name.LocalName != "SmartAssemblyMasterCatalog")
                throw new InvalidOperationException("XML kök öğesi <SmartAssemblyMasterCatalog> olmalı.");

            Guid catalogId;
            Guid.TryParse((string)root.Attribute("CatalogId"), out catalogId);
            var catalog = new SmartAssemblyMasterCatalog
            {
                Version   = (string)root.Attribute("Version") ?? "1.0",
                CatalogId = catalogId == Guid.Empty ? Guid.NewGuid() : catalogId
            };

            DateTime mod;
            if (DateTime.TryParse((string)root.Attribute("LastModified") ?? "", IC,
                                  DateTimeStyles.None, out mod))
                catalog.LastModified = mod;

            var compsEl = root.Element("Components");
            if (compsEl != null)
            {
                var defFamily = new ComponentFamily { Name = "Genel", Malzeme = "" };
                foreach (var cEl in compsEl.Elements("Component"))
                {
                    var c = DeserializeComponent(cEl);
                    if (c != null) defFamily.Components.Add(c);
                }
                if (defFamily.Components.Count > 0)
                    catalog.Families.Add(defFamily);
            }

            var rulesEl = root.Element("MasterRules");
            if (rulesEl != null)
                foreach (var rEl in rulesEl.Elements("Rule"))
                {
                    var r = DeserializeLegacyRule(rEl);
                    if (r != null) catalog.MasterRules.Add(r);
                }

            var pipeRulesEl = root.Element("MasterPipeRules");
            if (pipeRulesEl != null)
                foreach (var prEl in pipeRulesEl.Elements("PipeRange"))
                {
                    var pr = DeserializePipeRange(prEl);
                    if (pr != null) catalog.MasterPipeRules.Add(pr);
                }

            return catalog;
        }

        // =====================================================================
        // Component serialization
        // =====================================================================

        private static XElement SerializeComponent(ManholeComponent c)
        {
            var el = new XElement("Component",
                new XAttribute("Type",            c.Role.ToString()),
                new XAttribute("Id",              c.Id),
                new XAttribute("PozNo",           c.PozNo           ?? ""),
                new XAttribute("Name",            c.Name            ?? ""),
                new XAttribute("EffectiveHeight", c.EffectiveHeight.ToString("G", IC)),
                new XAttribute("FamilyTag",       c.FamilyTag       ?? ""),
                new XAttribute("IsVariable",      c.IsVariable.ToString().ToLowerInvariant()),
                new XAttribute("ZorunluParca",    c.ZorunluParca.ToString().ToLowerInvariant()),
                new XAttribute("YukseltmeParcasi",c.YukseltmeParcasi.ToString().ToLowerInvariant()),
                new XAttribute("ExternalVolume",  c.ExternalVolume.ToString("G", IC)),
                new XAttribute("MaterialVolume",  c.MaterialVolume.ToString("G", IC)),
                new XAttribute("Aciklama",        c.Aciklama ?? ""));

            var bottom = c as BottomElementComponent;
            if (bottom != null)
            {
                el.Add(new XAttribute("WallThicknessMm",    bottom.WallThicknessMm.ToString("G", IC)));
                el.Add(new XAttribute("TabanKalinligiMm",   bottom.TabanKalinligiMm.ToString("G", IC)));
                el.Add(new XAttribute("TopOpeningDiamMm",   bottom.TopOpeningDiameterMm.ToString("G", IC)));
                el.Add(new XAttribute("FloorExternalVolume", bottom.FloorExternalVolume.ToString("G", IC)));
                el.Add(new XAttribute("FloorMaterialVolume", bottom.FloorMaterialVolume.ToString("G", IC)));
                el.Add(new XAttribute("IsComposite", bottom.IsComposite.ToString().ToLowerInvariant()));
                el.Add(SerializeFootprint(bottom.Footprint ?? new Footprint()));
                var spEl = new XElement("SubPieces");
                foreach (var sp in bottom.SubPieces ?? new List<SubPiece>())
                    spEl.Add(new XElement("SubPiece",
                        new XAttribute("Name",        sp.Name        ?? ""),
                        new XAttribute("HeightMm",    sp.HeightMm.ToString("G", IC)),
                        new XAttribute("Description", sp.Description ?? "")));
                el.Add(spEl);
                return el;
            }

            var middle = c as MiddleElementComponent;
            if (middle != null)
            {
                el.Add(new XAttribute("WallThicknessMm", middle.WallThicknessMm.ToString("G", IC)));
                el.Add(new XAttribute("InnerDiamMm",     middle.InnerDiameterMm.ToString("G", IC)));
                return el;
            }

            var reducer = c as ReducerComponent;
            if (reducer != null)
            {
                el.Add(new XAttribute("WallThicknessMm",  reducer.WallThicknessMm.ToString("G", IC)));
                el.Add(new XAttribute("BottomInnerDiamMm", reducer.BottomInnerDiameterMm.ToString("G", IC)));
                el.Add(new XAttribute("TopInnerDiamMm",    reducer.TopInnerDiameterMm.ToString("G", IC)));
                return el;
            }

            var adjuster = c as AdjusterComponent;
            if (adjuster != null)
            {
                el.Add(new XAttribute("WallThicknessMm", adjuster.WallThicknessMm.ToString("G", IC)));
                el.Add(new XAttribute("InnerDiamMm",     adjuster.InnerDiameterMm.ToString("G", IC)));
                return el;
            }

            var cover = c as CoverComponent;
            if (cover != null)
            {
                el.Add(new XAttribute("LoadClass",      cover.LoadClass      ?? ""));
                el.Add(new XAttribute("ClearOpeningMm", cover.ClearOpeningMm.ToString("G", IC)));
                return el;
            }

            var tap = c as TemelAltiParcaComponent;
            if (tap != null)
            {
                el.Add(new XAttribute("Boy",               tap.Boy.ToString("G", IC)));
                el.Add(new XAttribute("En",                tap.En.ToString("G", IC)));
                el.Add(new XAttribute("BaglandiTabanCapi", tap.BaglandiTabanCapiMm.ToString("G", IC)));
            }

            return el;
        }

        private static XElement SerializeFootprint(Footprint fp)
        {
            var el = new XElement("Footprint",
                new XAttribute("Shape", fp.Shape.ToString()));
            if (fp.Shape == FootprintShape.Circular)
            {
                el.Add(new XAttribute("DiameterMm", fp.DiameterMm.ToString("G", IC)));
            }
            else if (fp.Shape == FootprintShape.Square)
            {
                el.Add(new XAttribute("SideMm", fp.SideMm.ToString("G", IC)));
            }
            else
            {
                el.Add(new XAttribute("LengthMm",              fp.LengthMm.ToString("G", IC)));
                el.Add(new XAttribute("WidthMm",               fp.WidthMm.ToString("G", IC)));
                el.Add(new XAttribute("RectangularOrientation", fp.RectangularOrientation ?? ""));
            }
            return el;
        }

        private static ManholeComponent DeserializeComponent(XElement el)
        {
            if (el == null) return null;
            string type = (string)el.Attribute("Type") ?? "";

            Guid id;
            Guid.TryParse((string)el.Attribute("Id"), out id);
            if (id == Guid.Empty) id = Guid.NewGuid();

            ManholeComponent comp;
            switch (type)
            {
                case "BottomElement":
                {
                    var b = new BottomElementComponent
                    {
                        WallThicknessMm      = ParseDouble(el, "WallThicknessMm",  0),
                        TabanKalinligiMm     = ParseDouble(el, "TabanKalinligiMm", 0),
                        TopOpeningDiameterMm = ParseDouble(el, "TopOpeningDiamMm", 0),
                        FloorExternalVolume  = ParseDouble(el, "FloorExternalVolume", 0),
                        FloorMaterialVolume  = ParseDouble(el, "FloorMaterialVolume", 0),
                        IsComposite          = ParseBool  (el, "IsComposite",      false),
                        Footprint            = DeserializeFootprint(el.Element("Footprint"))
                    };
                    var spEl = el.Element("SubPieces");
                    if (spEl != null)
                        foreach (var sp in spEl.Elements("SubPiece"))
                            b.SubPieces.Add(new SubPiece
                            {
                                Name        = (string)sp.Attribute("Name")        ?? "",
                                HeightMm    = ParseDouble(sp, "HeightMm",          0),
                                Description = (string)sp.Attribute("Description") ?? ""
                            });
                    comp = b;
                    break;
                }
                case "MiddleElement":
                    comp = new MiddleElementComponent
                    {
                        WallThicknessMm = ParseDouble(el, "WallThicknessMm", 0),
                        InnerDiameterMm = ParseDouble(el, "InnerDiamMm",     0)
                    };
                    break;
                case "Reducer":
                    comp = new ReducerComponent
                    {
                        WallThicknessMm       = ParseDouble(el, "WallThicknessMm",  0),
                        BottomInnerDiameterMm = ParseDouble(el, "BottomInnerDiamMm", 0),
                        TopInnerDiameterMm    = ParseDouble(el, "TopInnerDiamMm",    0)
                    };
                    break;
                case "Adjuster":
                    comp = new AdjusterComponent
                    {
                        WallThicknessMm = ParseDouble(el, "WallThicknessMm", 0),
                        InnerDiameterMm = ParseDouble(el, "InnerDiamMm",     0)
                    };
                    break;
                case "Cover":
                    comp = new CoverComponent
                    {
                        LoadClass      = (string)el.Attribute("LoadClass") ?? "",
                        ClearOpeningMm = ParseDouble(el, "ClearOpeningMm", 0)
                    };
                    break;
                case "TemelAltiParca":
                    comp = new TemelAltiParcaComponent
                    {
                        Boy               = ParseDouble(el, "Boy",               0),
                        En                = ParseDouble(el, "En",                0),
                        BaglandiTabanCapiMm = ParseDouble(el, "BaglandiTabanCapi", 0)
                    };
                    break;
                default: return null;
            }

            comp.Id               = id;
            comp.PozNo            = (string)el.Attribute("PozNo")    ?? "";
            comp.Name             = (string)el.Attribute("Name")     ?? "";
            comp.EffectiveHeight  = ParseDouble(el, "EffectiveHeight", 0);
            comp.FamilyTag        = (string)el.Attribute("FamilyTag") ?? "";
            comp.IsVariable       = ParseBool(el, "IsVariable",       false);
            comp.ZorunluParca     = ParseBool(el, "ZorunluParca",     false);
            comp.YukseltmeParcasi = ParseBool(el, "YukseltmeParcasi", false);
            comp.ExternalVolume   = ParseDouble(el, "ExternalVolume",  0);
            comp.MaterialVolume   = ParseDouble(el, "MaterialVolume",  0);
            comp.Aciklama         = (string)el.Attribute("Aciklama")  ?? "";
            return comp;
        }

        private static Footprint DeserializeFootprint(XElement el)
        {
            if (el == null) return new Footprint();
            FootprintShape shape;
            Enum.TryParse((string)el.Attribute("Shape") ?? "Circular", out shape);
            return new Footprint
            {
                Shape                  = shape,
                DiameterMm             = ParseDouble(el, "DiameterMm", 0),
                SideMm                 = ParseDouble(el, "SideMm",     0),
                LengthMm               = ParseDouble(el, "LengthMm",   0),
                WidthMm                = ParseDouble(el, "WidthMm",    0),
                RectangularOrientation = (string)el.Attribute("RectangularOrientation") ?? ""
            };
        }

        // =====================================================================
        // PipeRangeRule / DepthTierRule serialization
        // =====================================================================

        private static XElement SerializePipeRange(PipeRangeRule pr)
        {
            var el = new XElement("PipeRange",
                new XAttribute("Id",                   pr.Id),
                new XAttribute("MinPipeMm",            pr.MinPipeMm.ToString("G", IC)),
                new XAttribute("MaxPipeMm",            pr.MaxPipeMm.ToString("G", IC)),
                new XAttribute("SelectedPipeFamilyId", pr.SelectedPipeFamilyId),
                new XAttribute("MinPipeId",            pr.MinPipeId),
                new XAttribute("MaxPipeId",            pr.MaxPipeId),
                new XAttribute("SystemTypeId",         pr.SystemTypeId));
            foreach (var t in pr.DepthTiers ?? new List<DepthTierRule>())
                el.Add(SerializeDepthTier(t));
            return el;
        }

        private static XElement SerializeDepthTier(DepthTierRule t)
        {
            var el = new XElement("DepthTier",
                new XAttribute("Id",             t.Id),
                new XAttribute("MinDepthM",      t.MinDepthM.ToString("G", IC)),
                new XAttribute("MaxDepthM",      t.MaxDepthM.ToString("G", IC)),
                new XAttribute("SelectedBaseId", t.SelectedBaseId),
                new XAttribute("IsCastInSitu",   t.IsCastInSitu.ToString().ToLowerInvariant()),
                new XAttribute("Notes",          t.Notes ?? ""));

            if (t.ComponentConstraints != null && t.ComponentConstraints.Count > 0)
            {
                var ccEl = new XElement("ComponentConstraints");
                foreach (var cc in t.ComponentConstraints)
                    ccEl.Add(new XElement("CC",
                        new XAttribute("Role", cc.Role.ToString()),
                        new XAttribute("Min",  cc.MinCount),
                        new XAttribute("Max",  cc.MaxCount)));
                el.Add(ccEl);
            }
            return el;
        }

        private static PipeRangeRule DeserializePipeRange(XElement el)
        {
            if (el == null) return null;
            Guid id, familyId, minPipeId, maxPipeId;
            Guid.TryParse((string)el.Attribute("Id"),                  out id);
            Guid.TryParse((string)el.Attribute("SelectedPipeFamilyId"), out familyId);
            Guid.TryParse((string)el.Attribute("MinPipeId"),            out minPipeId);
            Guid.TryParse((string)el.Attribute("MaxPipeId"),            out maxPipeId);
            Guid systemTypeId;
            Guid.TryParse((string)el.Attribute("SystemTypeId"), out systemTypeId);
            var pr = new PipeRangeRule
            {
                Id                   = id == Guid.Empty ? Guid.NewGuid() : id,
                MinPipeMm            = ParseDouble(el, "MinPipeMm", 0),
                MaxPipeMm            = ParseDouble(el, "MaxPipeMm", 0),
                SelectedPipeFamilyId = familyId,
                MinPipeId            = minPipeId,
                MaxPipeId            = maxPipeId,
                SystemTypeId         = systemTypeId
            };
            foreach (var tEl in el.Elements("DepthTier"))
            {
                var t = DeserializeDepthTier(tEl);
                if (t != null) pr.DepthTiers.Add(t);
            }
            return pr;
        }

        private static DepthTierRule DeserializeDepthTier(XElement el)
        {
            if (el == null) return null;
            Guid id, baseId;
            Guid.TryParse((string)el.Attribute("Id"),             out id);
            Guid.TryParse((string)el.Attribute("SelectedBaseId"), out baseId);
            var tier = new DepthTierRule
            {
                Id             = id == Guid.Empty ? Guid.NewGuid() : id,
                MinDepthM      = ParseDouble(el, "MinDepthM",    0),
                MaxDepthM      = ParseDouble(el, "MaxDepthM",    0),
                SelectedBaseId = baseId,
                IsCastInSitu   = ParseBool  (el, "IsCastInSitu", false),
                Notes          = (string)el.Attribute("Notes")  ?? ""
            };

            var ccEl = el.Element("ComponentConstraints");
            if (ccEl != null)
            {
                foreach (var ccItem in ccEl.Elements("CC"))
                {
                    ComponentRole role;
                    if (!System.Enum.TryParse((string)ccItem.Attribute("Role") ?? "", out role)) continue;
                    int min, max;
                    int.TryParse((string)ccItem.Attribute("Min"), out min);
                    int.TryParse((string)ccItem.Attribute("Max"), out max);
                    tier.ComponentConstraints.Add(new ComponentTypeConstraint
                    {
                        Role = role, MinCount = min, MaxCount = max
                    });
                }
            }
            return tier;
        }

        // =====================================================================
        // Legacy AssemblyRule serialization (backward compat)
        // =====================================================================

        private static XElement SerializeLegacyRule(AssemblyRule r) =>
            new XElement("Rule",
                new XAttribute("Id",             r.Id),
                new XAttribute("MinPipeMm",      r.MinPipeDiameterMm.ToString("G", IC)),
                new XAttribute("MaxPipeMm",      r.MaxPipeDiameterMm.ToString("G", IC)),
                new XAttribute("MinDepthM",      r.MinDepthM.ToString("G", IC)),
                new XAttribute("MaxDepthM",      r.MaxDepthM.ToString("G", IC)),
                new XAttribute("SelectedBaseId", r.SelectedBaseId),
                new XAttribute("IsCastInSitu",   r.IsCastInSitu.ToString().ToLowerInvariant()),
                new XAttribute("Notes",          r.Notes ?? ""));

        private static AssemblyRule DeserializeLegacyRule(XElement el)
        {
            if (el == null) return null;
            Guid id, baseId;
            Guid.TryParse((string)el.Attribute("Id"),             out id);
            Guid.TryParse((string)el.Attribute("SelectedBaseId"), out baseId);
            return new AssemblyRule
            {
                Id                = id == Guid.Empty ? Guid.NewGuid() : id,
                MinPipeDiameterMm = ParseDouble(el, "MinPipeMm",    0),
                MaxPipeDiameterMm = ParseDouble(el, "MaxPipeMm",    0),
                MinDepthM         = ParseDouble(el, "MinDepthM",    0),
                MaxDepthM         = ParseDouble(el, "MaxDepthM",    0),
                SelectedBaseId    = baseId,
                IsCastInSitu      = ParseBool  (el, "IsCastInSitu", false),
                Notes             = (string)el.Attribute("Notes")  ?? ""
            };
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void EnsureDir(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static void SaveDoc(XElement root, string filePath)
        {
            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(filePath);
        }

        private static string UtcNow() =>
            DateTime.UtcNow.ToString("u", IC).Replace(" ", "T");

        private static double ParseDouble(XElement el, string attr, double fallback)
        {
            double d;
            return double.TryParse((string)el?.Attribute(attr),
                                   NumberStyles.Float, IC, out d) ? d : fallback;
        }

        private static bool ParseBool(XElement el, string attr, bool fallback)
        {
            bool b;
            return bool.TryParse((string)el?.Attribute(attr), out b) ? b : fallback;
        }
    }
}
