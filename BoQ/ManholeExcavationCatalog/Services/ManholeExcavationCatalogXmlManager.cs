using System;
using System.Collections.Generic;
using System.IO;
using System.Xml.Linq;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services
{
    /// <summary>
    /// XML read/write for the manhole excavation rules catalog.
    ///
    /// Internal path (Kaydet / auto-load):
    ///   %APPDATA%\UrbanoMetraj\ManholeExcavCatalog.xml
    ///
    /// Schema:
    ///   &lt;ManholeExcavCatalog exportedAt="ISO-8601"&gt;
    ///     &lt;Rule id="..." minDiamMm="..." maxDiamMm="..."&gt;
    ///       &lt;Tier minDepth="..." maxDepth="..." clearance="..." slope="..."
    ///             shoring="true" stepped="false" maxStepH="1.5" stepBerm="0.5"&gt;
    ///         &lt;SubBase layerName="..." thickMm="..."/&gt;
    ///         &lt;Backfill layerName="..." material="..." thickM="..." fillToSurface="false"/&gt;
    ///       &lt;/Tier&gt;
    ///     &lt;/Rule&gt;
    ///   &lt;/ManholeExcavCatalog&gt;
    /// </summary>
    public static class ManholeExcavationCatalogXmlManager
    {
        public static readonly string InternalPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj", "ManholeExcavCatalog.xml");

        // ── Write ─────────────────────────────────────────────────────────────

        public static void SaveInternal(IEnumerable<ManholeExcavationRule> rules)
            => Write(rules, InternalPath);

        public static void Export(IEnumerable<ManholeExcavationRule> rules, string path)
            => Write(rules, path);

        private static void Write(IEnumerable<ManholeExcavationRule> rules, string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (dir != null) Directory.CreateDirectory(dir);

            var root = new XElement("ManholeExcavCatalog",
                new XAttribute("exportedAt", DateTime.UtcNow.ToString("o")));

            foreach (var rule in rules)
            {
                var ruleEl = new XElement("Rule",
                    new XAttribute("id",         rule.Id.ToString("D")),
                    new XAttribute("minDiamMm",  rule.MinBaseDiameterMm.ToString("G")),
                    new XAttribute("maxDiamMm",  rule.MaxBaseDiameterMm.ToString("G")));

                foreach (var tier in rule.DepthTiers)
                {
                    var tierEl = new XElement("Tier",
                        new XAttribute("minDepth",  tier.MinDepthM.ToString("G")),
                        new XAttribute("maxDepth",  tier.MaxDepthM.ToString("G")),
                        new XAttribute("clearance", tier.WorkingClearanceM.ToString("G")),
                        new XAttribute("slope",     tier.SlopeRatio.ToString("G")),
                        new XAttribute("shoring",   tier.RequiresShoring.ToString()),
                        new XAttribute("stepped",   tier.IsSteppedExcavation.ToString()),
                        new XAttribute("maxStepH",  tier.MaxStepHeightM.ToString("G")),
                        new XAttribute("stepBerm",  tier.StepBermWidthM.ToString("G")));

                    foreach (var sb in tier.SubBaseLayers)
                        tierEl.Add(new XElement("SubBase",
                            new XAttribute("layerName", sb.LayerName ?? ""),
                            new XAttribute("thickMm",   sb.ThicknessMm.ToString("G"))));

                    foreach (var bf in tier.BackfillLayers)
                        tierEl.Add(new XElement("Backfill",
                            new XAttribute("layerName",     bf.LayerName ?? ""),
                            new XAttribute("material",      bf.MaterialType ?? ""),
                            new XAttribute("thickM",        bf.ThicknessM.ToString("G")),
                            new XAttribute("fillToSurface", bf.IsFillToSurface.ToString())));

                    ruleEl.Add(tierEl);
                }

                root.Add(ruleEl);
            }

            new XDocument(new XDeclaration("1.0", "utf-8", null), root).Save(path);
        }

        // ── Read ──────────────────────────────────────────────────────────────

        public static List<ManholeExcavationRule> LoadInternal()
            => File.Exists(InternalPath) ? Import(InternalPath) : new List<ManholeExcavationRule>();

        public static List<ManholeExcavationRule> Import(string path)
        {
            var list = new List<ManholeExcavationRule>();
            var doc  = XDocument.Load(path);

            foreach (var ruleEl in doc.Root?.Elements("Rule")
                     ?? System.Linq.Enumerable.Empty<XElement>())
            {
                Guid.TryParse((string)ruleEl.Attribute("id"), out Guid ruleId);

                var rule = new ManholeExcavationRule
                {
                    Id                = ruleId == Guid.Empty ? Guid.NewGuid() : ruleId,
                    MinBaseDiameterMm = ParseDouble((string)ruleEl.Attribute("minDiamMm")),
                    MaxBaseDiameterMm = ParseDouble((string)ruleEl.Attribute("maxDiamMm"))
                };

                foreach (var tierEl in ruleEl.Elements("Tier"))
                {
                    var tier = new ManholeExcavationDepthTier
                    {
                        MinDepthM          = ParseDouble((string)tierEl.Attribute("minDepth")),
                        MaxDepthM          = ParseDouble((string)tierEl.Attribute("maxDepth")),
                        WorkingClearanceM  = ParseDouble((string)tierEl.Attribute("clearance")),
                        SlopeRatio         = ParseDouble((string)tierEl.Attribute("slope")),
                        RequiresShoring    = ParseBool((string)tierEl.Attribute("shoring")),
                        IsSteppedExcavation = ParseBool((string)tierEl.Attribute("stepped")),
                        MaxStepHeightM     = ParseDouble((string)tierEl.Attribute("maxStepH"),  1.5),
                        StepBermWidthM     = ParseDouble((string)tierEl.Attribute("stepBerm"), 0.5)
                    };

                    foreach (var sbEl in tierEl.Elements("SubBase"))
                        tier.SubBaseLayers.Add(new SubBaseLayer
                        {
                            LayerName   = (string)sbEl.Attribute("layerName") ?? "",
                            ThicknessMm = ParseDouble((string)sbEl.Attribute("thickMm"))
                        });

                    foreach (var bfEl in tierEl.Elements("Backfill"))
                        tier.BackfillLayers.Add(new ManholeBackfillLayer
                        {
                            LayerName      = (string)bfEl.Attribute("layerName") ?? "",
                            MaterialType   = (string)bfEl.Attribute("material")  ?? "",
                            ThicknessM     = ParseDouble((string)bfEl.Attribute("thickM")),
                            IsFillToSurface = ParseBool((string)bfEl.Attribute("fillToSurface"))
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
