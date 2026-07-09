using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;
using UrbanoMetraj.BoQ.ExcelImport;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.Services
{
    /// <summary>
    /// Exports manhole component families to .xlsx — one worksheet per family. Column headers
    /// and the "Rol" text values match the import side (see <see cref="ComponentExcelImportService"/>),
    /// so an exported sheet can be edited and re-imported (mapping "Rol" back through the value dialog).
    ///
    /// Mirrors the import's V1 scope: only fields common to every ComponentRole are exported.
    /// Role-specific geometry (Footprint, sub-pieces, inner diameters…) is intentionally omitted.
    /// </summary>
    public static class ComponentExcelExportService
    {
        private static readonly string[] Headers =
        {
            "Poz No", "Ad", "Rol", "Yükseklik (mm)", "Aile Etiketi",
            "Dış Hacim (m³)", "Malzeme Hacmi (m³)", "Açıklama",
            "Değişken", "Zorunlu Parça", "Yükseltme Parçası"
        };

        private static readonly double[] Widths = { 14, 22, 16, 14, 18, 14, 16, 26, 12, 14, 16 };

        public static void Export(IEnumerable<ComponentFamily> families, string filePath)
        {
            using (var pkg = new ExcelPackage())
            {
                var used = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                bool any = false;

                foreach (var fam in families ?? new List<ComponentFamily>())
                {
                    any = true;
                    string sheetName = ExcelSheetWriter.SafeSheetName(fam.Name, used);
                    var ws = pkg.Workbook.Worksheets.Add(sheetName);
                    ExcelSheetWriter.WriteHeader(ws, Headers, Widths);

                    int r = 2;
                    foreach (var c in fam.Components)
                    {
                        ws.Cells[r, 1].Value  = c.PozNo ?? "";
                        ws.Cells[r, 2].Value  = c.Name ?? "";
                        ws.Cells[r, 3].Value  = RoleDisplay(c.Role);
                        ws.Cells[r, 4].Value  = c.EffectiveHeight;
                        ws.Cells[r, 5].Value  = c.FamilyTag ?? "";
                        ws.Cells[r, 6].Value  = c.ExternalVolume;
                        ws.Cells[r, 7].Value  = c.MaterialVolume;
                        ws.Cells[r, 8].Value  = c.Aciklama ?? "";
                        ws.Cells[r, 9].Value  = c.IsVariable       ? "Evet" : "Hayır";
                        ws.Cells[r, 10].Value = c.ZorunluParca     ? "Evet" : "Hayır";
                        ws.Cells[r, 11].Value = c.YukseltmeParcasi ? "Evet" : "Hayır";

                        ws.Cells[r, 4].Style.Numberformat.Format = "#,##0.###";
                        ws.Cells[r, 6].Style.Numberformat.Format = "#,##0.0000";
                        ws.Cells[r, 7].Style.Numberformat.Format = "#,##0.0000";
                        r++;
                    }
                }

                if (!any)
                {
                    var empty = pkg.Workbook.Worksheets.Add("Baca Parça Kataloğu");
                    ExcelSheetWriter.WriteHeader(empty, Headers, Widths);
                }

                pkg.SaveAs(new FileInfo(filePath));
            }
        }

        private static string RoleDisplay(ComponentRole role)
        {
            foreach (var opt in ComponentExcelImportService.RoleOptions)
                if (opt.Value == role.ToString()) return opt.Display;
            return role.ToString();
        }
    }
}
