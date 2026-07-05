using System;
using System.Collections.Generic;
using System.Linq;
using OfficeOpenXml;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Generates the standalone "Baca Kesif Tablosu" workbook — one row per
    /// manhole, one sheet per network system — modeled after a reference
    /// manhole quantity-takeoff table. Unlike ExcelExportService.Export(), this
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
    ///    first so Inlets/Outlets are filled in.
    /// </summary>
    public static class ManholeKesifExportService
    {
        private static readonly Dictionary<ExportLanguage, string[]> HeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                // Col: No | Manhole | Inlet Neighbor | Outlet Neighbor | Distance | Pipe Dia
                //    | Manhole Dia | Cover Top Elev | Existing Ground Elev
                //    | Inlet Invert | Outlet Invert | Depth | Excav Depth | Excav Volume
                //    | Component Breakdown | Other Connections
                [ExportLanguage.English] = new[]
                {
                    "No", "Manhole", "Inlet Neighbor", "Outlet Neighbor",
                    "Distance (m)", "Pipe Diameter (mm)",
                    "Manhole Diameter (mm)", "Cover Top Elev (m)", "Existing Ground Elev (m)",
                    "Inlet Invert (m)", "Outlet Invert (m)", "Manhole Depth (m)",
                    "Excav. Depth (m)", "Excav. Volume (m3)",
                    "Component Breakdown", "Other Connections"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Sira No", "Baca Adi", "Komsu Baca (Giris)", "Komsu Baca (Cikis)",
                    "Mesafe (m)", "Boru Capi (mm)",
                    "Baca Capi (mm)", "Kapak Ustu Kotu (m)", "Mevcut Zemin Kotu (m)",
                    "Giris Invert Kotu (m)", "Cikis Invert Kotu (m)", "Baca Derinligi (m)",
                    "Kazi Derinligi (m)", "Kazi Hacmi (m3)",
                    "Parca Dokumu", "Diger Baglantilar"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "No", "Kolodets", "Sosedniy (Vkhod)", "Sosedniy (Vykhod)",
                    "Rasstoyanie (m)", "Diametr Truby (mm)",
                    "Diametr Kolodtsa (mm)", "Otm. Verkha Lyuka (m)", "Otm. Sushch. Poverkhnosti (m)",
                    "Invert Vkhoda (m)", "Invert Vykhoda (m)", "Glubina Kolodtsa (m)",
                    "Glubina Vykopki (m)", "Ob'em Vykopki (m3)",
                    "Detali Kolodtsa", "Drugie Soyedineniya"
                }
            };

        public static void Export(BoQReport report, BoQSettings settings, string path)
        {
            string[] hdr = HeaderMap[settings.Language];

            using (var pkg = new ExcelPackage())
            {
                foreach (var sys in report.Systems ?? new List<SystemBoQ>())
                {
                    if (sys.Manholes == null || sys.Manholes.Count == 0) continue;
                    WriteSheet(pkg, sys, hdr);
                }
                ExcelExportService.SavePackage(pkg, path);
            }
        }

        private static void WriteSheet(ExcelPackage pkg, SystemBoQ sys, string[] hdr)
        {
            string safe      = ExcelExportService.SanitizeSheetName(sys.SystemName);
            string sheetName = ExcelExportService.Truncate(safe + "_BacaKesif", 31);
            var    ws        = pkg.Workbook.Worksheets.Add(sheetName);
            int    colCount  = hdr.Length;

            ExcelExportService.WriteTitle(ws, sys.SystemName + " - Baca Kesif Tablosu", colCount, 1);

            const int hdrRow = 3;
            ExcelExportService.WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int  row = hdrRow + 1;
            bool alt = false;
            int  no  = 1;

            foreach (var m in sys.Manholes.OrderBy(m => m.NodeName, StringComparer.OrdinalIgnoreCase))
            {
                var primaryInlet  = m.Inlets.OrderByDescending(c => c.DiameterMm).FirstOrDefault();
                var primaryOutlet = m.Outlets.OrderByDescending(c => c.DiameterMm).FirstOrDefault();
                double distance   = primaryOutlet?.Distance2D ?? primaryInlet?.Distance2D ?? 0;
                int    pipeDiam   = primaryOutlet?.DiameterMm ?? primaryInlet?.DiameterMm ?? 0;

                ws.Cells[row, 1].Value  = no++;
                ws.Cells[row, 2].Value  = m.NodeName;
                ws.Cells[row, 3].Value  = primaryInlet?.NeighborNodeName  ?? "-";
                ws.Cells[row, 4].Value  = primaryOutlet?.NeighborNodeName ?? "-";
                ws.Cells[row, 5].Value  = distance;
                ws.Cells[row, 6].Value  = pipeDiam;
                ws.Cells[row, 7].Value  = m.DiameterDisplay;
                ws.Cells[row, 8].Value  = m.TerrainElevation;          // Kapak Ustu Kotu (TH1)
                ws.Cells[row, 9].Value  = m.ExistingGroundElevation;   // Mevcut Zemin Kotu (TH2)
                ws.Cells[row, 10].Value = primaryInlet?.InvertElevation;
                ws.Cells[row, 11].Value = primaryOutlet?.InvertElevation;
                ws.Cells[row, 12].Value = m.Depth;
                ws.Cells[row, 13].Value = m.ExcavationDepth;
                ws.Cells[row, 14].Value = m.ExcavationVolume;
                ws.Cells[row, 15].Value = DescribeParts(m);
                ws.Cells[row, 16].Value = DescribeOtherConnections(m, primaryInlet, primaryOutlet);

                ExcelExportService.ApplyDataRowStyle(ws, row, colCount, alt);
                ExcelExportService.SetNumericFormat(ws, row, 5, 5, "#,##0.00");
                ExcelExportService.SetNumericFormat(ws, row, 6, 7, "#,##0");
                ExcelExportService.SetNumericFormat(ws, row, 8, 14, "#,##0.000");
                alt = !alt;
                row++;
            }

            ws.Column(1).Width  = 8;
            ws.Column(2).Width  = 16;
            ws.Column(3).Width  = 16;
            ws.Column(4).Width  = 16;
            ws.Column(5).Width  = 14;
            ws.Column(6).Width  = 16;
            ws.Column(7).Width  = 16;
            ws.Column(8).Width  = 16;
            ws.Column(9).Width  = 18;
            ws.Column(10).Width = 14;
            ws.Column(11).Width = 14;
            ws.Column(12).Width = 14;
            ws.Column(13).Width = 16;
            ws.Column(14).Width = 16;
            ws.Column(15).Width = 42;
            ws.Column(16).Width = 30;
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

        private static string DescribeOtherConnections(
            ManholeItem m, ManholeConnectionInfo primaryInlet, ManholeConnectionInfo primaryOutlet)
        {
            var extra = new List<string>();
            foreach (var c in m.Inlets)
                if (!ReferenceEquals(c, primaryInlet))
                    extra.Add($"Giris: {c.NeighborNodeName} (D{c.DiameterMm}, {c.Distance2D:0.00}m)");
            foreach (var c in m.Outlets)
                if (!ReferenceEquals(c, primaryOutlet))
                    extra.Add($"Cikis: {c.NeighborNodeName} (D{c.DiameterMm}, {c.Distance2D:0.00}m)");
            return extra.Count > 0 ? string.Join("; ", extra) : "-";
        }
    }
}
