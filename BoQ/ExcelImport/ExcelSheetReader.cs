using System.Collections.Generic;
using System.IO;
using OfficeOpenXml;

namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>Reads the first worksheet of an .xlsx file into a header row + raw string rows.</summary>
    public static class ExcelSheetReader
    {
        public static void ReadFirstSheet(string filePath, out string[] headers, out List<string[]> rows)
        {
            using (var pkg = new ExcelPackage(new FileInfo(filePath)))
            {
                var ws = pkg.Workbook.Worksheets.Count > 0 ? pkg.Workbook.Worksheets[1] : null;
                if (ws == null || ws.Dimension == null)
                {
                    headers = new string[0];
                    rows    = new List<string[]>();
                    return;
                }

                int firstRow = ws.Dimension.Start.Row;
                int lastRow  = ws.Dimension.End.Row;
                int firstCol = ws.Dimension.Start.Column;
                int lastCol  = ws.Dimension.End.Column;
                int colCount = lastCol - firstCol + 1;

                headers = new string[colCount];
                for (int c = 0; c < colCount; c++)
                {
                    string h = ws.Cells[firstRow, firstCol + c].Text?.Trim();
                    headers[c] = string.IsNullOrEmpty(h) ? "Sütun " + (c + 1) : h;
                }

                rows = new List<string[]>();
                for (int r = firstRow + 1; r <= lastRow; r++)
                {
                    var row = new string[colCount];
                    bool anyValue = false;
                    for (int c = 0; c < colCount; c++)
                    {
                        string v = ws.Cells[r, firstCol + c].Text?.Trim() ?? "";
                        row[c] = v;
                        if (!string.IsNullOrEmpty(v)) anyValue = true;
                    }
                    if (anyValue) rows.Add(row);
                }
            }
        }
    }
}
