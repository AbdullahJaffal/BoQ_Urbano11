using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.ProjectRules.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;   // ComponentRole

namespace UrbanoMetraj.BoQ.ProjectRules.Services
{
    /// <summary>
    /// Portable XML persistence for a <see cref="ProjectRuleSet"/> — the "project rules template"
    /// the user can export from one DWG and import into another.
    ///
    /// The AG_GUID-keyed per-pipe / per-manhole exceptions are intentionally NOT serialized here:
    /// they reference specific drawing entities and are meaningless in another DWG (decision 7 in
    /// PROJECT_RULES_REDESIGN.md). Catalog references (family GUIDs / class names) assume the target
    /// machine shares the same catalogs, mirroring the existing MasterPipeRules XML behavior.
    /// </summary>
    public static class ProjectRulesXmlManager
    {
        private const string RootName = "ProjectRules";
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        public static void Export(ProjectRuleSet ruleSet, string filePath)
        {
            if (ruleSet == null) throw new ArgumentNullException(nameof(ruleSet));
            EnsureDir(filePath);

            var root = new XElement(RootName,
                new XAttribute("Version",      ruleSet.SchemaVersion ?? "1.0"),
                new XAttribute("LastModified", DateTime.UtcNow.ToString("u", IC).Replace(" ", "T")),
                new XAttribute("CalcMode",     ruleSet.CalcMode.ToString()));

            foreach (var net in ruleSet.NetworkRules ?? new List<NetworkRule>())
            {
                if (net == null) continue;
                var netEl = new XElement("Network",
                    new XAttribute("SystemName",      net.SystemName      ?? ""),
                    new XAttribute("PipeFamilyId",    net.PipeFamilyId),
                    new XAttribute("PipeSinif",       net.PipeSinif       ?? ""),
                    new XAttribute("ManholeFamilyId", net.ManholeFamilyId),
                    new XAttribute("SoilName",        net.SoilName        ?? ""));

                foreach (var r in net.ConnectionRules ?? new List<ConnectionRule>())
                {
                    var rEl = new XElement("ConnRule",
                        new XAttribute("MinPipeMm", r.MinPipeMm.ToString("G", IC)),
                        new XAttribute("MaxPipeMm", r.MaxPipeMm.ToString("G", IC)));
                    foreach (var t in r.Tiers ?? new List<ConnDepthTier>())
                    {
                        var tEl = new XElement("Tier",
                            new XAttribute("MinDepthM",         t.MinDepthM.ToString("G", IC)),
                            new XAttribute("MaxDepthM",         t.MaxDepthM.ToString("G", IC)),
                            new XAttribute("ManholeDiameterMm", t.ManholeDiameterMm.ToString("G", IC)),
                            new XAttribute("IsCastInSitu",      t.IsCastInSitu.ToString().ToLowerInvariant()),
                            new XAttribute("Notes",             t.Notes ?? ""));
                        foreach (var cc in t.ComponentConstraints ?? new List<ComponentTypeConstraint>())
                            tEl.Add(new XElement("CC",
                                new XAttribute("Role", cc.Role.ToString()),
                                new XAttribute("Min",  cc.MinCount),
                                new XAttribute("Max",  cc.MaxCount)));
                        rEl.Add(tEl);
                    }
                    netEl.Add(rEl);
                }

                foreach (var n in net.PipeTrenchRuleNames ?? new List<string>())
                    netEl.Add(new XElement("TrenchFilter", new XAttribute("Name", n ?? "")));
                foreach (var n in net.ManholeExcavRuleNames ?? new List<string>())
                    netEl.Add(new XElement("ManholeExcavFilter", new XAttribute("Name", n ?? "")));

                foreach (var row in net.PieceExclusionRows ?? new List<PieceExclusionRow>())
                {
                    var rowEl = new XElement("PieceRow",
                        new XAttribute("FamilyId", row.ManholeFamilyId),
                        new XAttribute("Diameter", row.ManholeDiameterMm.ToString("G", IC)));
                    foreach (var pe in row.Roles ?? new List<PieceExclusion>())
                        rowEl.Add(new XElement("Role",
                            new XAttribute("Role",    pe.Role.ToString()),
                            new XAttribute("Heights", string.Join(",",
                                (pe.AllowedHeightsMm ?? new List<double>()).Select(h => h.ToString("G", IC))))));
                    netEl.Add(rowEl);
                }

                root.Add(netEl);
            }

            new XDocument(new XDeclaration("1.0", "utf-8", "yes"), root).Save(filePath);
        }

        public static ProjectRuleSet Import(string filePath)
        {
            if (!File.Exists(filePath))
                throw new FileNotFoundException("Proje kuralları XML dosyası bulunamadı.", filePath);

            var root = XDocument.Load(filePath).Root;
            if (root == null || root.Name.LocalName != RootName)
                throw new InvalidOperationException("XML kök öğesi <" + RootName + "> olmalı.");

            var result = new ProjectRuleSet
            {
                SchemaVersion = (string)root.Attribute("Version") ?? "1.0"
            };

            CalcMode mode;
            if (Enum.TryParse((string)root.Attribute("CalcMode") ?? "", out mode))
                result.CalcMode = mode;

            foreach (var el in root.Elements("Network"))
            {
                Guid pf, mf;
                Guid.TryParse((string)el.Attribute("PipeFamilyId"),    out pf);
                Guid.TryParse((string)el.Attribute("ManholeFamilyId"), out mf);
                var net = new NetworkRule
                {
                    SystemName      = (string)el.Attribute("SystemName") ?? "",
                    PipeFamilyId    = pf,
                    PipeSinif       = (string)el.Attribute("PipeSinif") ?? "",
                    ManholeFamilyId = mf,
                    SoilName        = (string)el.Attribute("SoilName") ?? ""
                };

                foreach (var fEl in el.Elements("TrenchFilter"))
                {
                    var n = (string)fEl.Attribute("Name");
                    if (!string.IsNullOrEmpty(n)) net.PipeTrenchRuleNames.Add(n);
                }
                foreach (var fEl in el.Elements("ManholeExcavFilter"))
                {
                    var n = (string)fEl.Attribute("Name");
                    if (!string.IsNullOrEmpty(n)) net.ManholeExcavRuleNames.Add(n);
                }

                foreach (var rEl in el.Elements("ConnRule"))
                {
                    var rule = new ConnectionRule
                    {
                        MinPipeMm = ParseD(rEl, "MinPipeMm"),
                        MaxPipeMm = ParseD(rEl, "MaxPipeMm")
                    };
                    foreach (var tEl in rEl.Elements("Tier"))
                    {
                        bool cis;
                        bool.TryParse((string)tEl.Attribute("IsCastInSitu"), out cis);
                        var tier = new ConnDepthTier
                        {
                            MinDepthM         = ParseD(tEl, "MinDepthM"),
                            MaxDepthM         = ParseD(tEl, "MaxDepthM"),
                            ManholeDiameterMm = ParseD(tEl, "ManholeDiameterMm"),
                            IsCastInSitu      = cis,
                            Notes             = (string)tEl.Attribute("Notes") ?? ""
                        };
                        foreach (var ccEl in tEl.Elements("CC"))
                        {
                            ComponentRole crole;
                            if (!Enum.TryParse((string)ccEl.Attribute("Role") ?? "", out crole)) continue;
                            int mn, mx;
                            int.TryParse((string)ccEl.Attribute("Min"), out mn);
                            if (!int.TryParse((string)ccEl.Attribute("Max"), out mx)) mx = -1;
                            tier.ComponentConstraints.Add(new ComponentTypeConstraint
                            { Role = crole, MinCount = mn, MaxCount = mx });
                        }
                        rule.Tiers.Add(tier);
                    }
                    net.ConnectionRules.Add(rule);
                }

                foreach (var rowEl in el.Elements("PieceRow"))
                {
                    Guid fid;
                    Guid.TryParse((string)rowEl.Attribute("FamilyId"), out fid);
                    var row = new PieceExclusionRow { ManholeFamilyId = fid, ManholeDiameterMm = ParseD(rowEl, "Diameter") };
                    foreach (var pEl in rowEl.Elements("Role"))
                    {
                        ComponentRole role;
                        if (!Enum.TryParse((string)pEl.Attribute("Role") ?? "", out role)) continue;
                        var ex = new PieceExclusion { Role = role };
                        foreach (var h in ((string)pEl.Attribute("Heights") ?? "")
                                     .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            double d;
                            if (double.TryParse(h, System.Globalization.NumberStyles.Float, IC, out d))
                                ex.AllowedHeightsMm.Add(d);
                        }
                        row.Roles.Add(ex);
                    }
                    net.PieceExclusionRows.Add(row);
                }

                result.NetworkRules.Add(net);
            }

            return result;
        }

        private static double ParseD(XElement el, string attr)
        {
            double d;
            return double.TryParse((string)el?.Attribute(attr),
                                   System.Globalization.NumberStyles.Float, IC, out d) ? d : 0;
        }

        private static void EnsureDir(string filePath)
        {
            var dir = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }
    }
}
