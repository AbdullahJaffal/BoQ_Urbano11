using System;
using System.Collections.Generic;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Generates the standalone "Baca Kesif Tablosu" workbook — one row per
    /// pipe CONNECTION (every inlet and every outlet gets its own row, in its
    /// own Inlet/Outlet column); manhole-level columns are merged and
    /// vertically centered across that manhole's row span so they read as one
    /// entry per manhole. One sheet per network system, modeled after a
    /// reference manhole quantity-takeoff table.
    /// Unlike ExcelExportService.Export(), this
    /// produces its OWN workbook (not a sheet inside the main multi-sheet BoQ
    /// export), per user's explicit choice.
    ///
    /// Reuses ExcelExportService's styling primitives (WriteTitle/WriteHeaders/
    /// ApplyDataRowStyle/SetNumericFormat/SavePackage/SanitizeSheetName/Truncate)
    /// so both workbooks share the exact same EPPlus 4.5.3.3 conventions (fixed
    /// column widths only — no AutoFit/AdjustToContents, which crash with a
    /// GDI+ exception inside AutoCAD's AppDomain).
    ///
    /// Scope locked for v1 (see project memory "Baca kesif gap analysis"):
    ///  - Excavation figures are the existing ISOLATED ManholeItem.ExcavationDepth/
    ///    ExcavationVolume (no trench-overlap deduction / crushed-stone-vs-soil split).
    ///  - Component breakdown is a flexible text list of ManholeItem.StackPreCast.Parts
    ///    (Taban/Govde/Konik/Kapak/Boyun Bilezigi have no persisted Role tag — shown
    ///    as-is rather than guessed at).
    ///  - Requires ManholeConnectionLinkService.Populate(report) to have been called
    ///    first so Inlets/Outlets are filled in, and PipeNetLengthService.Compute(report)
    ///    to have been (re-)run first so ManholeConnectionInfo.NetLength is non-zero
    ///    (NetLength is runtime-only, never persisted to the DWG).
    ///
    /// Sub-base-piece columns (one per distinct Boy×En×Kalinlik dimension found
    /// among this sheet's manholes' ResolvedSubBaseParts) are DYNAMIC — computed
    /// per sheet and inserted right before the fixed "Excav. Depth" column, so the
    /// header layout is built per-sheet rather than a single static array.
    /// </summary>
    public static class ManholeKesifExportService
    {
        // Fixed leading columns 1-16 (No .. Invert Elev for cross-system inlet).
        private static readonly Dictionary<ExportLanguage, string[]> HeaderPrefixMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "No", "Manhole", "Inlet", "Outlet",
                    "Distance (m)", "Net Pipe Length (m)", "Manhole Diameter (mm)",
                    "Pipe Diameter (mm)", "Design Elev - Kirmizi Kot (m)",
                    "Invert Elev (m) Outlet", "Manhole Depth (m)", "Invert Elev (m) Inlet",
                    "Ground Elev - Arazi Kot (m)",
                    "Inlet from another system", "Pipe Diameter (mm) for Inlet from another system",
                    "Invert Elev (m) for Inlet from another system"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Sira No", "Baca Adi", "Giris", "Cikis",
                    "Mesafe (m)", "Boru Net Uzunluk (m)", "Baca Capi (mm)",
                    "Boru Capi (mm)", "Kirmizi Kot (m)",
                    "Invert Kotu (m) Cikis", "Baca Derinligi (m)", "Invert Kotu (m) Giris",
                    "Arazi Kot (m)",
                    "Baska Sebekeden Giris", "Baska Sebekeden Giris Boru Capi (mm)",
                    "Baska Sebekeden Giris Invert Kotu (m)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "No", "Kolodets", "Vkhod", "Vykhod",
                    "Rasstoyanie (m)", "Chistaya Dlina Truby (m)", "Diametr Kolodtsa (mm)",
                    "Diametr Truby (mm)", "Krasnaya Otmetka (m)",
                    "Otmetka Inverta (m) Vykhod", "Glubina Kolodtsa (m)", "Otmetka Inverta (m) Vkhod",
                    "Otmetka Poverkhnosti (m)",
                    "Vkhod iz drugoy seti", "Diametr Truby dlya Vkhoda iz drugoy seti (mm)",
                    "Otmetka Inverta dlya Vkhoda iz drugoy seti (m)"
                }
            };

        // Fixed trailing columns, after the dynamic sub-base-piece block.
        private static readonly Dictionary<ExportLanguage, string[]> HeaderSuffixMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[] { "Excav. Depth (m)", "Excav. Volume (m3)", "Component Breakdown" },
                [ExportLanguage.Turkish] = new[] { "Kazi Derinligi (m)", "Kazi Hacmi (m3)", "Parca Dokumu" },
                [ExportLanguage.Russian] = new[] { "Glubina Vykopki (m)", "Ob'em Vykopki (m3)", "Detali Kolodtsa" }
            };

        // Single fixed column right after the dynamic sub-base-piece block — reads
        // ManholeItem.SubBaseVolume directly (Alt Temel Katmanları frustum volume
        // at the pit bottom, computed by ManholeExcavOverlapService.Compute — see
        // BacaKesifTablosuCommand, which re-runs it after Load() since it's
        // runtime-only). No calculation happens in this export service itself.
        private static readonly Dictionary<ExportLanguage, string> YataklamaHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Bedding (Yataklama) Volume (m3)",
                [ExportLanguage.Turkish] = "Yataklama Hacmi (m3)",
                [ExportLanguage.Russian] = "Ob'em Podstilki (m3)"
            };

        // Single fixed column right after the dynamic stacked-part block — Sum of
        // (StackedPart.UnitMaterialVolume x Count) across this manhole's whole
        // stack, mirroring ManholeItem.StructureVolume's own pattern (which sums
        // UnitExternalVolume x Count instead) — same existing catalog values,
        // just the material-volume sibling total, computed per manhole.
        private static readonly Dictionary<ExportLanguage, string> TotalMaterialVolumeHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Total Material Volume (m3)",
                [ExportLanguage.Turkish] = "Toplam Malzeme Hacmi (m3)",
                [ExportLanguage.Russian] = "Obshchiy Ob'em Materiala (m3)"
            };

        public static void Export(BoQReport report, BoQSettings settings, string path)
        {
            using (var pkg = new ExcelPackage())
            {
                foreach (var sys in report.Systems ?? new List<SystemBoQ>())
                {
                    if (sys.Manholes == null || sys.Manholes.Count == 0) continue;
                    WriteSheet(pkg, sys, settings.Language);
                }
                ExcelExportService.SavePackage(pkg, path);
            }
        }

        private static void WriteSheet(ExcelPackage pkg, SystemBoQ sys, ExportLanguage language)
        {
            // One column per distinct sub-base-piece dimension (Boy x En x Kalinlik)
            // found anywhere in this sheet — regardless of which Taban/diameter it
            // belongs to, per user's explicit criterion ("only the piece's own
            // dimensions matter"). First-appearance order.
            var subBaseGroups = new List<(double Boy, double En, double Kalinlik)>();
            foreach (var m in sys.Manholes)
                foreach (var p in m.ResolvedSubBaseParts ?? new List<TemelAltiParca>())
                {
                    var key = (p.Boy, p.En, p.Kalinlik);
                    if (!subBaseGroups.Contains(key)) subBaseGroups.Add(key);
                }

            // One column per distinct stacked-part (PartName + HeightM) found anywhere
            // in this sheet's StackPreCast.Parts — e.g. "Taban 0.80m", "Govde 0.60m",
            // "Konik 0.70m" each get their own column, same dynamic-pivot pattern as
            // the sub-base-piece columns above. First-appearance order.
            var stackGroups = new List<(string PartName, double HeightM)>();
            foreach (var m in sys.Manholes)
                foreach (var p in m.StackPreCast?.Parts ?? new List<StackedPart>())
                {
                    var key = (p.PartName, p.HeightM);
                    if (!stackGroups.Contains(key)) stackGroups.Add(key);
                }

            string[] prefix = HeaderPrefixMap[language];
            string[] suffix = HeaderSuffixMap[language];
            string[] subBaseHeaders = subBaseGroups
                .Select(g => $"{g.Boy:0}x{g.En:0}x{g.Kalinlik:0} mm")
                .ToArray();
            string[] stackHeaders = stackGroups
                .Select(g => $"{g.PartName} {g.HeightM:0.00}m")
                .ToArray();
            string[] hdr = prefix.Concat(subBaseHeaders)
                                  .Concat(new[] { YataklamaHeaderMap[language] })
                                  .Concat(stackHeaders)
                                  .Concat(new[] { TotalMaterialVolumeHeaderMap[language] })
                                  .Concat(suffix).ToArray();

            int subBaseStart   = prefix.Length + 1;                    // col 17 when no dynamic columns exist yet
            int yataklamaCol   = subBaseStart + subBaseGroups.Count;
            int stackStart     = yataklamaCol + 1;
            int totalMatVolCol = stackStart + stackGroups.Count;
            int excavDepthCol  = totalMatVolCol + 1;
            int excavVolCol    = excavDepthCol + 1;
            int partsCol       = excavVolCol + 1;
            int colCount       = partsCol;

            var manholeLevelColumns = new List<int> { 2, 7, 9, 11, 13 };
            for (int c = subBaseStart; c < subBaseStart + subBaseGroups.Count; c++) manholeLevelColumns.Add(c);
            manholeLevelColumns.Add(yataklamaCol);
            for (int c = stackStart; c < stackStart + stackGroups.Count; c++) manholeLevelColumns.Add(c);
            manholeLevelColumns.Add(totalMatVolCol);
            manholeLevelColumns.Add(excavDepthCol);
            manholeLevelColumns.Add(excavVolCol);
            manholeLevelColumns.Add(partsCol);

            string safe      = ExcelExportService.SanitizeSheetName(sys.SystemName);
            string sheetName = ExcelExportService.Truncate(safe + "_BacaKesif", 31);
            var    ws        = pkg.Workbook.Worksheets.Add(sheetName);

            ExcelExportService.WriteTitle(ws, sys.SystemName + " - Baca Kesif Tablosu", colCount, 1);

            const int hdrRow = 3;
            ExcelExportService.WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int  row = hdrRow + 1;
            bool alt = false;
            int  no  = 1;

            foreach (var m in sys.Manholes.OrderBy(m => m.NodeName, StringComparer.OrdinalIgnoreCase))
            {
                // Local connections get the normal Inlet/Outlet row treatment. Outlets
                // are ALWAYS local-treated even when the outlet pipe itself belongs to
                // another system (per user's instruction — only inlets get the special
                // "another system" columns). Outlets listed first, then local inlets.
                var localConnections = new List<(ManholeConnectionInfo c, bool isInlet)>();
                foreach (var c in m.Outlets) localConnections.Add((c, false));
                foreach (var c in m.Inlets)
                    if (string.Equals(c.SystemName, sys.SystemName, StringComparison.Ordinal))
                        localConnections.Add((c, true));

                // Cross-system inlets (from a different network) don't get their own
                // row — they're packed into columns 14-16 of the manhole's existing
                // rows (name/diameter/invert only), and only spill into extra rows if
                // there are more of them than existing local-connection rows.
                var crossInlets = new List<ManholeConnectionInfo>();
                foreach (var c in m.Inlets)
                    if (!string.Equals(c.SystemName, sys.SystemName, StringComparison.Ordinal))
                        crossInlets.Add(c);

                if (localConnections.Count == 0 && crossInlets.Count == 0)
                    localConnections.Add((null, true));   // isolated manhole — still emit one row

                int rowSpan = Math.Max(localConnections.Count, crossInlets.Count);
                if (rowSpan == 0) rowSpan = 1;

                int groupStart = row;
                for (int i = 0; i < rowSpan; i++)
                {
                    ws.Cells[row, 1].Value = no;

                    if (i < localConnections.Count)
                    {
                        var (c, isInlet) = localConnections[i];
                        ws.Cells[row, 3].Value  = isInlet  ? (c?.NeighborNodeName ?? "-") : null;
                        ws.Cells[row, 4].Value  = !isInlet ? (c?.NeighborNodeName ?? "-") : null;
                        ws.Cells[row, 5].Value  = c?.Distance2D;
                        ws.Cells[row, 6].Value  = c?.NetLength;
                        ws.Cells[row, 8].Value  = c?.DiameterMm;
                        // Invert elevation is split into its own Outlet/Inlet column (col 10 /
                        // col 12), mirroring the Inlet/Outlet neighbor-name split above, instead
                        // of one shared column whose meaning depended on reading the row's
                        // direction elsewhere.
                        ws.Cells[row, 10].Value = !isInlet ? c?.InvertElevation : null;
                        ws.Cells[row, 12].Value = isInlet  ? c?.InvertElevation : null;
                    }

                    if (i < crossInlets.Count)
                    {
                        var cc = crossInlets[i];
                        ws.Cells[row, 14].Value = cc.NeighborNodeName;
                        ws.Cells[row, 15].Value = cc.DiameterMm;
                        ws.Cells[row, 16].Value = cc.InvertElevation;
                    }

                    ExcelExportService.ApplyDataRowStyle(ws, row, colCount, alt);
                    ExcelExportService.SetNumericFormat(ws, row, 5, 6, "#,##0.00");
                    ExcelExportService.SetNumericFormat(ws, row, 8, 8, "#,##0");
                    ExcelExportService.SetNumericFormat(ws, row, 10, 10, "#,##0.000");
                    ExcelExportService.SetNumericFormat(ws, row, 12, 12, "#,##0.000");
                    ExcelExportService.SetNumericFormat(ws, row, 15, 15, "#,##0");
                    ExcelExportService.SetNumericFormat(ws, row, 16, 16, "#,##0.000");
                    row++;
                }
                int groupEnd = row - 1;

                // Manhole-level values — written once (top row of the group), then
                // merged + vertically centered across the group's row span so they
                // read as "one entry per manhole" even though the group has several
                // connection rows.
                ws.Cells[groupStart, 2].Value  = m.NodeName;
                // Prefer ResolvedFootprint (the ACTUAL linked precast Taban's shape/size,
                // set by ManholeAIService from the catalog — e.g. "600x600 mm (kare)" for
                // an Izgara base) over DiameterDisplay (Urbano's as-drawn geometry, which
                // falls back to "?" for any non-circular shape it can't size). Only fall
                // back to DiameterDisplay when no Taban has been resolved yet (AI not run
                // / catalog link missing), and treat "?" as blank rather than a fabricated
                // placeholder.
                string diaDisplay = m.ResolvedFootprint != null
                    ? m.ResolvedFootprint.DisplayString
                    : (m.DiameterDisplay == "?" ? null : m.DiameterDisplay);
                ws.Cells[groupStart, 7].Value  = diaDisplay;
                // 0 is the "not found" sentinel for both kots (no real project elevation
                // is exactly 0 a.s.l.) — leave blank instead of writing a fabricated 0.
                ws.Cells[groupStart, 9].Value   = m.TerrainElevation > 0        ? m.TerrainElevation        : (double?)null;
                ws.Cells[groupStart, 11].Value  = m.Depth;
                ws.Cells[groupStart, 13].Value  = m.ExistingGroundElevation > 0 ? m.ExistingGroundElevation : (double?)null;

                // One count per distinct sub-base-piece dimension — blank when this
                // manhole has none of that exact dimension, the count (1, 2, ...)
                // when it does (e.g. two identical plates under the same Taban).
                var mSubParts = m.ResolvedSubBaseParts ?? new List<TemelAltiParca>();
                for (int gi = 0; gi < subBaseGroups.Count; gi++)
                {
                    var g = subBaseGroups[gi];
                    int count = mSubParts.Count(p => p.Boy == g.Boy && p.En == g.En && p.Kalinlik == g.Kalinlik);
                    ws.Cells[groupStart, subBaseStart + gi].Value = count > 0 ? (int?)count : null;
                }

                ws.Cells[groupStart, yataklamaCol].Value  = m.SubBaseVolume > 0 ? (double?)m.SubBaseVolume : null;

                // One count per distinct stacked-part (PartName + HeightM) — blank
                // when this manhole's stack doesn't use that exact part, the count
                // (StackedPart.Count, e.g. "Govde x6") when it does.
                var mParts = m.StackPreCast?.Parts ?? new List<StackedPart>();
                for (int gi = 0; gi < stackGroups.Count; gi++)
                {
                    var g = stackGroups[gi];
                    int count = mParts.Where(p => p.PartName == g.PartName && p.HeightM == g.HeightM)
                                      .Sum(p => p.Count);
                    ws.Cells[groupStart, stackStart + gi].Value = count > 0 ? (int?)count : null;
                }

                double totalMaterialVol = mParts.Sum(p => p.UnitMaterialVolume * p.Count);
                ws.Cells[groupStart, totalMatVolCol].Value = totalMaterialVol > 0 ? (double?)totalMaterialVol : null;

                ws.Cells[groupStart, excavDepthCol].Value = m.ExcavationDepth;
                ws.Cells[groupStart, excavVolCol].Value   = m.ExcavationVolume;
                ws.Cells[groupStart, partsCol].Value      = DescribeParts(m);
                ExcelExportService.SetNumericFormat(ws, groupStart, 9, 9,   "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, 11, 11, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, 13, 13, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, yataklamaCol, yataklamaCol, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, totalMatVolCol, excavVolCol, "#,##0.000");

                if (groupEnd > groupStart)
                {
                    foreach (int col in manholeLevelColumns)
                    {
                        var rng = ws.Cells[groupStart, col, groupEnd, col];
                        rng.Merge = true;
                        rng.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                    }
                }

                // Shade by MANHOLE group — every row belonging to the same manhole
                // got the same `alt` value above, so grouped connections stay
                // visually together; the NEXT manhole's group toggles the band.
                alt = !alt;
                no++;
            }

            ws.Column(1).Width  = 8;
            ws.Column(2).Width  = 16;
            ws.Column(3).Width  = 14;
            ws.Column(4).Width  = 14;
            ws.Column(5).Width  = 14;
            ws.Column(6).Width  = 16;
            ws.Column(7).Width  = 16;
            ws.Column(8).Width  = 16;
            ws.Column(9).Width  = 18;
            ws.Column(10).Width = 16;
            ws.Column(11).Width = 14;
            ws.Column(12).Width = 16;
            ws.Column(13).Width = 18;
            ws.Column(14).Width = 16;
            ws.Column(15).Width = 20;
            ws.Column(16).Width = 20;
            for (int c = subBaseStart; c < subBaseStart + subBaseGroups.Count; c++)
                ws.Column(c).Width = 16;
            ws.Column(yataklamaCol).Width  = 20;
            for (int c = stackStart; c < stackStart + stackGroups.Count; c++)
                ws.Column(c).Width = 16;
            ws.Column(totalMatVolCol).Width = 20;
            ws.Column(excavDepthCol).Width = 16;
            ws.Column(excavVolCol).Width   = 16;
            ws.Column(partsCol).Width      = 42;
        }

        private static string DescribeParts(ManholeItem m)
        {
            var parts = m.StackPreCast?.Parts;
            if (parts != null && parts.Count > 0)
                return string.Join("; ", parts.Select(p => $"{p.PartName} x{p.Count} ({p.HeightM:0.00}m)"));
            if (m.StackCastInPlace != null)
                return $"Yerinde Beton, Derinlik {m.StackCastInPlace.ConcreteDepth:0.00}m";
            return "-";
        }
    }
}
