using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.Models;

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog.Services
{
    /// <summary>
    /// XML read/write for the pipe trench rules catalog.
    ///
    /// Internal path (Kaydet / auto-load):
    ///   %APPDATA%\UrbanoMetraj\PipeTrenchCatalog.xml
    ///
    /// Schema:
    ///   &lt;PipeTrenchCatalog exportedAt="ISO-8601"&gt;
    ///     &lt;Rule id="..." ruleName="..." minDiamMm="..." maxDiamMm="..."&gt;
    ///       &lt;Tier minDepth="..." maxDepth="..." clearance="..." slope="..."
    ///             shoring="true" stepped="false" maxStepH="1.5" stepBerm="0.5"&gt;
    ///         &lt;Bedding  layerName="..." material="..." thickM="..." fillToSurface="false"/&gt;
    ///         &lt;Backfill layerName="..." material="..." thickM="..." fillToSurface="false"/&gt;
    ///       &lt;/Tier&gt;
    ///     &lt;/Rule&gt;
    ///   &lt;/PipeTrenchCatalog&gt;
    /// </summary>
    public static class PipeTrenchCatalogXmlManager
    {
        public static readonly string InternalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj", "PipeTrenchCatalog.xml");

        // ── Write ─────────────────────────────────────────────────────────────

        public static void SaveInternal(IEnumerable<PipeTrenchRule> rules)
            => Write(rules, InternalPath);

        public static void Export(IEnumerable<PipeTrenchRule> rules, string path)
            => Write(rules, path);

        private static void Write(IEnumerable<PipeTrenchRule> rules, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);

            var root = new XElement("PipeTrenchCatalog",
                new XAttribute("exportedAt", DateTime.UtcNow.ToString("o")));

            foreach (var rule in rules)
            {
                var ruleEl = new XElement("Rule",
                    new XAttribute("id",         rule.Id.ToString("D")),
                    new XAttribute("ruleName",   rule.RuleName ?? ""),
                    new XAttribute("minDiamMm",  rule.MinPipeDiameterMm.ToString("G")),
                    new XAttribute("maxDiamMm",  rule.MaxPipeDiameterMm.ToString("G")));

                if (rule.SelectedFamilyNames.Count > 0)
                {
                    var familiesEl = new XElement("SelectedFamilies");
                    foreach (var name in rule.SelectedFamilyNames)
                        familiesEl.Add(new XElement("Family", new XAttribute("name", name)));
                    ruleEl.Add(familiesEl);
                }

                if (rule.SelectedSoilNames.Count > 0)
                {
                    var soilsEl = new XElement("SelectedSoils");
                    foreach (var name in rule.SelectedSoilNames)
                        soilsEl.Add(new XElement("Soil", new XAttribute("name", name)));
                    ruleEl.Add(soilsEl);
                }

                foreach (var tier in rule.DepthTiers)
                {
                    var tierEl = new XElement("Tier",
                        new XAttribute("minDepth",  tier.MinDepthM.ToString("G")),
                        new XAttribute("maxDepth",  tier.MaxDepthM.ToString("G")),
                        new XAttribute("clearance", tier.TrenchWidthClearanceM.ToString("G")),
                        new XAttribute("slope",     tier.SlopeRatio.ToString("G")),
                        new XAttribute("shoring",   tier.RequiresShoring.ToString()),
                        new XAttribute("stepped",   tier.IsSteppedExcavation.ToString()),
                        new XAttribute("maxStepH",  tier.MaxStepHeightM.ToString("G")),
                        new XAttribute("stepBerm",  tier.StepBermWidthM.ToString("G")));

                    foreach (var bl in tier.BeddingLayers)
                        tierEl.Add(new XElement("Bedding",
                            new XAttribute("layerName",     bl.LayerName ?? ""),
                            new XAttribute("material",      bl.MaterialType ?? ""),
                            new XAttribute("thickM",        bl.ThicknessM.ToString("G")),
                            new XAttribute("fillToSurface", bl.IsFillToSurface.ToString())));

                    foreach (var bf in tier.BackfillLayers)
                        tierEl.Add(new XElement("Backfill",
                            new XAttribute("layerName",     bf.LayerName ?? ""),
                            new XAttribute("material",      bf.MaterialType ?? ""),
                            new XAttribute("thickM",        bf.ThicknessM.ToString("G")),
                            new XAttribute("fillToSurface", bf.IsFillToSurface.ToString())));

                    foreach (var gl in tier.GomleklemeLayers)
                        tierEl.Add(new XElement("Gomlekleme",
                            new XAttribute("layerName",    gl.LayerName ?? ""),
                            new XAttribute("material",     gl.MaterialType ?? ""),
                            new XAttribute("position",     gl.Position ?? "boru etrafı"),
                            new XAttribute("thickM",       gl.ThicknessM.ToString("G")),
                            new XAttribute("upToPipeTop",  gl.IsUpToPipeTop.ToString())));

                    ruleEl.Add(tierEl);
                }

                root.Add(ruleEl);
            }

            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        }

        // ── Read ──────────────────────────────────────────────────────────────

        public static List<PipeTrenchRule> LoadInternal()
            => File.Exists(InternalPath) ? Import(InternalPath) : new List<PipeTrenchRule>();

        public static List<PipeTrenchRule> Import(string path)
        {
            var list = new List<PipeTrenchRule>();
            var doc  = XDocument.Load(path);

            foreach (var ruleEl in doc.Root?.Elements("Rule")
                     ?? System.Linq.Enumerable.Empty<XElement>())
            {
                Guid.TryParse((string)ruleEl.Attribute("id"), out Guid ruleId);

                var rule = new PipeTrenchRule
                {
                    Id                = ruleId == Guid.Empty ? Guid.NewGuid() : ruleId,
                    RuleName          = (string)ruleEl.Attribute("ruleName") ?? "",
                    MinPipeDiameterMm = ParseDouble((string)ruleEl.Attribute("minDiamMm")),
                    MaxPipeDiameterMm = ParseDouble((string)ruleEl.Attribute("maxDiamMm"))
                };

                var familiesEl = ruleEl.Element("SelectedFamilies");
                if (familiesEl != null)
                    foreach (var fEl in familiesEl.Elements("Family"))
                    {
                        var name = (string)fEl.Attribute("name");
                        if (!string.IsNullOrEmpty(name))
                            rule.SelectedFamilyNames.Add(name);
                    }

                var soilsEl = ruleEl.Element("SelectedSoils");
                if (soilsEl != null)
                    foreach (var sEl in soilsEl.Elements("Soil"))
                    {
                        var name = (string)sEl.Attribute("name");
                        if (!string.IsNullOrEmpty(name))
                            rule.SelectedSoilNames.Add(name);
                    }

                foreach (var tierEl in ruleEl.Elements("Tier"))
                {
                    var tier = new PipeTrenchDepthTier
                    {
                        MinDepthM             = ParseDouble((string)tierEl.Attribute("minDepth")),
                        MaxDepthM             = ParseDouble((string)tierEl.Attribute("maxDepth")),
                        TrenchWidthClearanceM = ParseDouble((string)tierEl.Attribute("clearance")),
                        SlopeRatio            = ParseDouble((string)tierEl.Attribute("slope")),
                        RequiresShoring       = ParseBool((string)tierEl.Attribute("shoring")),
                        IsSteppedExcavation   = ParseBool((string)tierEl.Attribute("stepped")),
                        MaxStepHeightM        = ParseDouble((string)tierEl.Attribute("maxStepH"),  1.5),
                        StepBermWidthM        = ParseDouble((string)tierEl.Attribute("stepBerm"), 0.5)
                    };

                    foreach (var blEl in tierEl.Elements("Bedding"))
                        tier.BeddingLayers.Add(new TrenchLayer
                        {
                            LayerName       = (string)blEl.Attribute("layerName") ?? "",
                            MaterialType    = (string)blEl.Attribute("material")  ?? "",
                            ThicknessM      = ParseDouble((string)blEl.Attribute("thickM")),
                            IsFillToSurface = ParseBool((string)blEl.Attribute("fillToSurface"))
                        });

                    foreach (var bfEl in tierEl.Elements("Backfill"))
                        tier.BackfillLayers.Add(new TrenchLayer
                        {
                            LayerName       = (string)bfEl.Attribute("layerName") ?? "",
                            MaterialType    = (string)bfEl.Attribute("material")  ?? "",
                            ThicknessM      = ParseDouble((string)bfEl.Attribute("thickM")),
                            IsFillToSurface = ParseBool((string)bfEl.Attribute("fillToSurface"))
                        });

                    foreach (var glEl in tierEl.Elements("Gomlekleme"))
                        tier.GomleklemeLayers.Add(new GomleklemeLayer
                        {
                            LayerName     = (string)glEl.Attribute("layerName") ?? "",
                            MaterialType  = (string)glEl.Attribute("material")  ?? "",
                            Position      = (string)glEl.Attribute("position")  ?? "boru etrafı",
                            ThicknessM    = ParseDouble((string)glEl.Attribute("thickM")),
                            IsUpToPipeTop = ParseBool((string)glEl.Attribute("upToPipeTop"))
                        });

                    rule.DepthTiers.Add(tier);
                }

                list.Add(rule);
            }

            return list;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static double ParseDouble(string s, double fallback = 0.0)
        {
            if (double.TryParse(s,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double v))
                return v;
            return fallback;
        }

        private static bool ParseBool(string s)
            => string.Equals(s, "True", StringComparison.OrdinalIgnoreCase);
    }
}
