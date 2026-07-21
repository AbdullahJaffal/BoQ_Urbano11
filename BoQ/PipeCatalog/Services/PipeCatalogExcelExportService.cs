using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using UrbanoMetraj.BoQ.ExcelImport;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Services
{
    /// <summary>
    /// Exports a PipeCatalog to .xlsx — one worksheet per family. Column headers match the
    /// import field labels (see <see cref="PipeCatalogExcelImportService.Fields"/>) so an
    /// exported sheet can be edited in Excel and re-imported into the same family cleanly.
    /// </summary>
    public static class PipeCatalogExcelExportService
    {
        private static readonly string[] Headers =
        {
            "Poz No", "DN (mm)", "OD (mm)", "ID (mm)", "Et Kalınlığı (mm)", "Sınıf", "Açıklama"
        };

        private static readonly double[] Widths = { 14, 10, 10, 10, 16, 12, 30 };

        public static void Export(PipeCatalog catalog, string filePath)
        {
            using (var pkg = new ExcelPackage())
            {
                var used = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

                if (catalog == null || catalog.Families.Count == 0)
                {
                    var empty = pkg.Workbook.Worksheets.Add("Boru Kataloğu");
                    ExcelSheetWriter.WriteHeader(empty, Headers, Widths);
                }
                else
                {
                    foreach (var fam in catalog.Families)
                    {
                        string sheetName = ExcelSheetWriter.SafeSheetName(fam.FamilyName, used);
                        var ws = pkg.Workbook.Worksheets.Add(sheetName);
                        ExcelSheetWriter.WriteHeader(ws, Headers, Widths);

                        int r = 2;
                        foreach (var p in fam.Pipes)
                        {
                            ws.Cells[r, 1].Value = p.PozNo ?? "";
                            ws.Cells[r, 2].Value = p.NominalDiameter;
                            ws.Cells[r, 3].Value = p.OuterDiameter;
                            ws.Cells[r, 4].Value = p.InnerDiameter;
                            ws.Cells[r, 5].Value = p.WallThickness;
                            ws.Cells[r, 6].Value = p.Sinif ?? "";
                            ws.Cells[r, 7].Value = p.Aciklama ?? "";

                            ws.Cells[r, 2].Style.Numberformat.Format = "#,##0.###";
                            ws.Cells[r, 3].Style.Numberformat.Format = "#,##0.###";
                            ws.Cells[r, 4].Style.Numberformat.Format = "#,##0.###";
                            ws.Cells[r, 5].Style.Numberformat.Format = "#,##0.###";
                            r++;
                        }
                    }
                }

                pkg.SaveAs(new FileInfo(filePath));
            }
        }
    }
}
