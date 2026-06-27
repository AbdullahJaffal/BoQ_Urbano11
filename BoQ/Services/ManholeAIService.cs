using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Phase 2 — Manhole AI: Smart Topology, Drop-Pipe Filtering, Dynamic Pre-cast Stacking.
    ///
    /// Catalog schema (row-per-part)
    /// --------------------------------
    ///  Nominal_Diameter | Part_Name | Height_m | Is_Mandatory | Is_Variable_Ring
    ///
    ///  Mandatory parts  (Is_Mandatory = Yes) → 1 unit added per manhole regardless of depth.
    ///  Variable rings   (Is_Variable_Ring = Yes) → filled greedily to cover the remaining depth.
    ///
    /// Stacking algorithm (per manhole, pre-cast only)
    /// ------------------------------------------------
    ///  1. Collect mandatory parts → add 1 each → Fixed_Stack_Height = sum of their heights.
    ///  2. Remaining = Depth − Fixed_Stack_Height.
    ///  3. Greedy largest-first fill with variable rings until Remaining ≤ 0.
    ///  4. If Remaining > 0.05 m after all rings, add 1 more of the smallest ring.
    ///
    /// BOM output (system-isolated)
    /// --------------------------------
    ///  BuildBomBySystem() returns one SystemBomGroup per network system,
    ///  preserving the same system order as BoQReport.Systems.
    ///
    /// Drop-Pipe Rule (pre-cast only)
    /// --------------------------------
    ///  An inlet at a manhole is a "drop-pipe" (Selale) when:
    ///      InvertAtManhole  >  (Lowest_Invert_At_Manhole + sum of mandatory heights)
    ///  Cast-in-place manholes never have drop-pipes.
    ///
    /// ASCII-safe strings used throughout (project coding convention):
    ///   O  = diameter symbol (Ø),  Giris / Cikis = Giriş / Çıkış,  Selale = Şelale
    /// </summary>
    public static class ManholeAIService
    {
        /// <summary>
        /// Remaining-height tolerance in metres.  If the greedy algorithm leaves
        /// a gap smaller than this, no extra ring is added.
        /// </summary>
        private const double LeftoverTolerance = 0.05;

        // =====================================================================
        // 1. Catalog reader  (new row-per-part schema)
        // =====================================================================

        /// <summary>
        /// Reads the Manhole_Catalog.xlsx produced by ManholeConfigService.
        /// Returns a dictionary keyed by NominalDiameter (mm).
        ///
        /// If the path is empty, missing, or unreadable, returns an empty
        /// dictionary — processing continues without stacking data.
        /// </summary>
        public static Dictionary<int, CatalogEntry> ReadCatalog(string path)
        {
            var result = new Dictionary<int, CatalogEntry>();
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return result;

            try
            {
                using (var pkg = new ExcelPackage(new FileInfo(path)))
                {
                    // Prefer "Manhole_Catalog"; fall back to the first sheet.
                    ExcelWorksheet ws = pkg.Workbook.Worksheets["Manhole_Catalog"]
                                     ?? (pkg.Workbook.Worksheets.Count > 0
                                         ? pkg.Workbook.Worksheets[1] : null);

                    if (ws == null || ws.Dimension == null || ws.Dimension.Rows < 2)
                        return result;

                    // ── Discover column positions from header row ─────────────
                    int colDiam = 1, colName = 2, colHeight = 3,
                        colMand = 4, colVar  = 5;

                    int totalCols = ws.Dimension.Columns;
                    for (int c = 1; c <= totalCols; c++)
                    {
                        string hdr = (ws.Cells[1, c].Text ?? "").Trim();
                        if      (hdr.Equals("Nominal_Diameter",  StringComparison.OrdinalIgnoreCase)) colDiam   = c;
                        else if (hdr.Equals("Part_Name",         StringComparison.OrdinalIgnoreCase)) colName   = c;
                        else if (hdr.Equals("Height_m",          StringComparison.OrdinalIgnoreCase)) colHeight = c;
                        else if (hdr.Equals("Is_Mandatory",      StringComparison.OrdinalIgnoreCase)) colMand   = c;
                        else if (hdr.Equals("Is_Variable_Ring",  StringComparison.OrdinalIgnoreCase)) colVar    = c;
                    }

                    // ── Read data rows ────────────────────────────────────────
                    int totalRows = ws.Dimension.Rows;
                    for (int r = 2; r <= totalRows; r++)
                    {
                        int diam = ParseCellInt(ws.Cells[r, colDiam].Value);
                        if (diam <= 0) continue;  // skip blank / header repetition rows

                        string partName = (ws.Cells[r, colName].Text ?? "Part").Trim();
                        double heightM  = ParseCellDouble(ws.Cells[r, colHeight].Value);
                        bool   isMand   = ParseBool(ws.Cells[r, colMand].Text);
                        bool   isVar    = ParseBool(ws.Cells[r, colVar].Text);

                        if (!result.ContainsKey(diam))
                            result[diam] = new CatalogEntry { NominalDiameter = diam };

                        result[diam].Parts.Add(new CatalogPart
                        {
                            PartName       = partName,
                            HeightM        = heightM,
                            IsMandatory    = isMand,
                            IsVariableRing = isVar
                        });
                    }
                }
            }
            catch
            {
                // Non-fatal — return whatever was collected before the error.
            }

            return result;
        }

        // =====================================================================
        // 2. Main processing pass
        // =====================================================================

        /// <summary>
        /// Runs the full Phase 2 analysis on every ManholeItem in the report.
        /// Mutates ManholeItem in-place (SmartTypeName, Stack, inlet/outlet counts).
        /// </summary>
        public static void Process(
            BoQReport                     report,
            BoQSettings                   settings,
            Dictionary<int, CatalogEntry> catalog)
        {
            if (report == null) return;

            foreach (var sys in report.Systems)
                foreach (var mh in sys.Manholes)
                    ProcessManhole(mh, report.SectionDebug, catalog);
        }

        // =====================================================================
        // 3. System-isolated BOM builder
        // =====================================================================

        /// <summary>
        /// Builds the bill-of-materials list system by system, preserving the
        /// same system order as <see cref="BoQReport.Systems"/>.
        ///
        /// Pre-cast: aggregates discrete part counts (mandatory parts + variable rings).
        /// Cast-in-place: lists manhole count and total concrete depth per diameter.
        /// </summary>
        public static List<SystemBomGroup> BuildBomBySystem(
            BoQReport   report,
            BoQSettings settings)
        {
            var groups    = new List<SystemBomGroup>();
            if (report == null) return groups;
            bool isPreCast = settings.ManholeType == ManholeType.PreCast;

            foreach (var sys in report.Systems)
            {
                var group = new SystemBomGroup { SystemName = sys.SystemName };
                var manholes = sys.Manholes;

                if (isPreCast)
                    BuildPreCastBom(manholes, group.Lines);
                else
                    BuildCipBom(manholes, group.Lines);

                groups.Add(group);
            }

            return groups;
        }

        // =====================================================================
        // Private: per-manhole topology + stacking
        // =====================================================================

        private static void ProcessManhole(
            ManholeItem                   mh,
            List<SectionDebugRow>         allSections,
            Dictionary<int, CatalogEntry> catalog)
        {
            // ── Topology ──────────────────────────────────────────────────────
            var inlets  = allSections
                .Where(s => string.Equals(s.EndNodeName,   mh.NodeName, StringComparison.OrdinalIgnoreCase))
                .ToList();
            var outlets = allSections
                .Where(s => string.Equals(s.StartNodeName, mh.NodeName, StringComparison.OrdinalIgnoreCase))
                .ToList();

            var invertsAtNode = inlets .Select(s => s.InvertEnd)
                .Concat(outlets.Select(s => s.InvertStart))
                .ToList();

            double lowestInvert = invertsAtNode.Count > 0
                ? invertsAtNode.Min() : double.NaN;

            // ── Catalog lookup ────────────────────────────────────────────────
            CatalogEntry entry = null;
            if (mh.Diameter > 0 && catalog != null)
                catalog.TryGetValue(mh.Diameter, out entry);

            // ── Drop-pipe rule (pre-cast logic drives the SmartTypeName) ──────
            var dropInlets   = new List<SectionDebugRow>();
            var normalInlets = new List<SectionDebugRow>(inlets);

            if (!double.IsNaN(lowestInvert))
            {
                double mandH = entry != null ? entry.TotalMandatoryHeight : 0;
                double dropThreshold = lowestInvert + mandH;

                dropInlets   = inlets.Where(s => s.InvertEnd > dropThreshold).ToList();
                normalInlets = inlets.Where(s => s.InvertEnd <= dropThreshold).ToList();
            }

            mh.HasDropPipe      = dropInlets.Count > 0;
            mh.ValidInletCount  = normalInlets.Count;
            mh.ValidOutletCount = outlets.Count;

            // ── Smart type name ───────────────────────────────────────────────
            string diamTag  = mh.Diameter > 0 ? $"O{mh.Diameter}" : "O?";
            string dropNote = mh.HasDropPipe ? $" [+{dropInlets.Count} Selale]" : "";
            mh.SmartTypeName =
                $"Baca {diamTag} - ({mh.ValidInletCount} Giris / {mh.ValidOutletCount} Cikis){dropNote}";

            // ── Stacking — both scenarios cached simultaneously ───────────────
            mh.StackPreCast = entry != null
                ? ComputePreCastStack(mh.Depth, mh.Diameter, entry)
                : null;

            mh.StackCastInPlace = new ManholeStackResult
            {
                NominalDiameter = mh.Diameter,
                IsPreCast       = false,
                ConcreteDepth   = mh.Depth
            };
        }

        // =====================================================================
        // Private: greedy stacking (dynamic mandatory parts)
        // =====================================================================

        /// <summary>
        /// Builds a ManholeStackResult for one pre-cast manhole using the catalog.
        ///
        /// Step 1: Add 1 unit of every mandatory part; accumulate their heights.
        /// Step 2: Greedily fill the remaining depth with variable rings (largest first).
        /// Step 3: If the leftover gap exceeds LeftoverTolerance, add one more of
        ///         the smallest ring to close the gap.
        /// </summary>
        private static ManholeStackResult ComputePreCastStack(
            double depth, int diameter, CatalogEntry entry)
        {
            var stack = new ManholeStackResult
            {
                NominalDiameter = diameter,
                IsPreCast       = true
            };

            // ── Step 1: mandatory parts ───────────────────────────────────────
            double fixedHeight = 0;
            foreach (var part in entry.MandatoryParts)
            {
                stack.Parts.Add(new StackedPart
                {
                    PartName       = part.PartName,
                    HeightM        = part.HeightM,
                    Count          = 1,
                    IsVariableRing = false
                });
                fixedHeight += part.HeightM;
            }

            // ── Step 2: greedy variable ring fill ─────────────────────────────
            double remaining = depth - fixedHeight;
            if (remaining <= 0 || entry.VariableRings.Count == 0)
            {
                stack.ResidualM = remaining;
                return stack;
            }

            // Deduplicate by height (keep first named part for each unique height).
            var uniqueRings = entry.VariableRings
                .GroupBy(r => r.HeightM)
                .Select(g => g.First())
                .OrderByDescending(r => r.HeightM)
                .ToList();

            // Height → (PartName, Count) usage map
            var ringUsage = new Dictionary<double, RingUsageEntry>();

            foreach (var ring in uniqueRings)
            {
                if (ring.HeightM <= 1e-9) continue;
                int count = (int)(remaining / ring.HeightM);
                if (count > 0)
                {
                    ringUsage[ring.HeightM] = new RingUsageEntry
                    {
                        PartName = ring.PartName,
                        Count    = count
                    };
                    remaining -= count * ring.HeightM;
                }
            }

            // ── Step 3: leftover gap correction ───────────────────────────────
            if (remaining > LeftoverTolerance && uniqueRings.Count > 0)
            {
                var smallest = uniqueRings.Last();
                if (ringUsage.ContainsKey(smallest.HeightM))
                    ringUsage[smallest.HeightM] = new RingUsageEntry
                    {
                        PartName = ringUsage[smallest.HeightM].PartName,
                        Count    = ringUsage[smallest.HeightM].Count + 1
                    };
                else
                    ringUsage[smallest.HeightM] = new RingUsageEntry
                    {
                        PartName = smallest.PartName,
                        Count    = 1
                    };

                remaining -= smallest.HeightM;
            }

            stack.ResidualM = Math.Max(0, remaining);

            // Convert usage map to StackedPart list (largest ring first)
            foreach (var kv in ringUsage.OrderByDescending(k => k.Key))
            {
                if (kv.Value.Count > 0)
                    stack.Parts.Add(new StackedPart
                    {
                        PartName       = kv.Value.PartName,
                        HeightM        = kv.Key,
                        Count          = kv.Value.Count,
                        IsVariableRing = true
                    });
            }

            return stack;
        }

        // ── Small helper to avoid tuples in .NET 4.8 ─────────────────────────
        private sealed class RingUsageEntry
        {
            public string PartName { get; set; }
            public int    Count    { get; set; }
        }

        // =====================================================================
        // Private: BOM builders
        // =====================================================================

        private static void BuildPreCastBom(
            List<ManholeItem> manholes,
            List<BomLine>     lines)
        {
            // Aggregate: group by (Diameter, PartName, HeightM, IsVariableRing)
            var bomData = manholes
                .Where(m => m.StackPreCast != null)
                .SelectMany(m => m.StackPreCast.Parts.Select(p => new
                {
                    m.Diameter,
                    p.PartName,
                    p.HeightM,
                    p.Count,
                    p.IsVariableRing
                }))
                .GroupBy(x => new
                {
                    x.Diameter,
                    x.PartName,
                    HeightKey = Math.Round(x.HeightM, 4),
                    x.IsVariableRing
                })
                // Sort: diameter asc, mandatory before variable, variable rings largest first
                .OrderBy(g => g.Key.Diameter)
                .ThenBy(g => g.Key.IsVariableRing ? 1 : 0)
                .ThenByDescending(g => g.Key.HeightKey)
                .ThenBy(g => g.Key.PartName);

            foreach (var grp in bomData)
            {
                int totalCount = grp.Sum(x => x.Count);
                if (totalCount <= 0) continue;

                lines.Add(new BomLine
                {
                    Description = FormatPartDescription(
                        grp.Key.PartName,
                        grp.Key.HeightKey,
                        grp.Key.Diameter,
                        grp.Key.IsVariableRing),
                    Quantity = totalCount,
                    Unit     = "Adet"
                });
            }

            // Manholes with no catalog match → flag them
            var noCatalog = manholes
                .Where(m => m.StackPreCast == null)
                .GroupBy(m => m.Diameter)
                .OrderBy(g => g.Key);

            foreach (var grp in noCatalog)
            {
                string tag = grp.Key > 0 ? $" O{grp.Key} mm" : " (unknown)";
                lines.Add(new BomLine
                {
                    Description = "(Catalog not found)" + tag,
                    Quantity    = grp.Count(),
                    Unit        = "Adet"
                });
            }
        }

        private static void BuildCipBom(
            List<ManholeItem> manholes,
            List<BomLine>     lines)
        {
            var byDiam = manholes
                .Where(m => m.StackCastInPlace != null)
                .GroupBy(m => m.Diameter)
                .OrderBy(g => g.Key);

            foreach (var grp in byDiam)
            {
                string tag        = grp.Key > 0 ? $" O{grp.Key} mm" : "";
                double totalDepth = grp.Sum(m => m.StackCastInPlace.ConcreteDepth);
                lines.Add(new BomLine
                {
                    Description  = "Beton Baca" + tag,
                    Quantity     = grp.Count(),
                    Unit         = "Adet",
                    TotalDepthM  = totalDepth
                });
            }
        }

        // =====================================================================
        // Private: helpers
        // =====================================================================

        private static string FormatPartDescription(
            string partName, double heightM, int diameter, bool isVariableRing)
        {
            // Variable rings include their height in the label; mandatory parts do not
            // (their height is fixed and defined by the catalog).
            string heightTag = isVariableRing && heightM > 1e-9
                ? $" {heightM:F2}m" : "";
            string diamTag   = diameter > 0 ? $" O{diameter} mm" : "";
            return $"{partName}{heightTag}{diamTag}";
        }

        // ── Cell parsing ──────────────────────────────────────────────────────

        private static int ParseCellInt(object cellValue)
        {
            if (cellValue == null) return 0;
            if (cellValue is double d) return (int)Math.Round(d);
            if (cellValue is int    i) return i;
            return int.TryParse(cellValue.ToString().Trim(),
                NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : 0;
        }

        private static double ParseCellDouble(object cellValue)
        {
            if (cellValue == null) return 0;
            if (cellValue is double d) return d;
            if (cellValue is int    i) return i;
            return double.TryParse(cellValue.ToString().Trim(),
                NumberStyles.Float, CultureInfo.InvariantCulture, out double v) ? v : 0;
        }

        /// <summary>
        /// Accepts "Yes", "True", "1", "Evet" (case-insensitive) as true.
        /// Everything else is false.
        /// </summary>
        private static bool ParseBool(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return false;
            string s = raw.Trim();
            return s.Equals("yes",  StringComparison.OrdinalIgnoreCase)
                || s.Equals("true", StringComparison.OrdinalIgnoreCase)
                || s.Equals("1")
                || s.Equals("evet", StringComparison.OrdinalIgnoreCase);
        }
    }

    // =========================================================================
    // BOM output models
    // =========================================================================

    /// <summary>
    /// One aggregated line in the Bill of Materials.
    /// Used by both ManholeAIService (builder) and ExcelExportService (writer).
    /// </summary>
    public class BomLine
    {
        /// <summary>Part description, e.g. "Taban O1000 mm" or "Govde 0.50m O1000 mm".</summary>
        public string Description { get; set; }
        /// <summary>Total number of pieces (pre-cast) or number of manholes (cast-in-place).</summary>
        public int    Quantity    { get; set; }
        /// <summary>Unit string, e.g. "Adet".</summary>
        public string Unit        { get; set; }
        /// <summary>Total concrete depth in metres (cast-in-place lines only; 0 for pre-cast).</summary>
        public double TotalDepthM { get; set; }
    }

    /// <summary>
    /// BOM lines for one network system.
    /// Returned by ManholeAIService.BuildBomBySystem() in system order.
    /// </summary>
    public class SystemBomGroup
    {
        public string        SystemName { get; set; }
        public List<BomLine> Lines      { get; set; } = new List<BomLine>();
    }
}
