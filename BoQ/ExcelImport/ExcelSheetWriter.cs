using System.Collections.Generic;
using System.Drawing;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace UrbanoMetraj.BoQ.ExcelImport
{
    /// <summary>
    /// Small shared helper for writing a styled header row + fixed column widths into an
    /// EPPlus worksheet. Reused by every catalog's "Excel'e Aktar" export so headers look
    /// consistent and always match the import field labels (round-trip friendly).
    ///
    /// IMPORTANT: never call AutoFit here — GDI+ font measurement crashes in AutoCAD's
    /// AppDomain. Fixed widths only (see CLAUDE.md).
    /// </summary>
    public static class ExcelSheetWriter
    {
        private static readonly Color HeaderFill  = Color.FromArgb(0, 70, 127);
        private static readonly Color HeaderText  = Color.White;

        /// <summary>Writes a bold banded header row at row 1 and sets each column's width.</summary>
        public static void WriteHeader(ExcelWorksheet ws, string[] headers, double[] widths)
        {
            for (int c = 0; c < headers.Length; c++)
            {
                var cell = ws.Cells[1, c + 1];
                cell.Value = headers[c];
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(HeaderFill);
                cell.Style.Font.Bold  = true;
                cell.Style.Font.Color.SetColor(HeaderText);
                cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                cell.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;

                double w = (widths != null && c < widths.Length) ? widths[c] : 16;
                ws.Column(c + 1).Width = w;
            }
            ws.Row(1).Height = 22;
            ws.View.FreezePanes(2, 1);
        }

        /// <summary>Sanitizes a worksheet name (Excel rules: max 31 chars, no \ / ? * [ ] : ).
        /// Ensures uniqueness against names already used (case-insensitive).</summary>
        public static string SafeSheetName(string raw, HashSet<string> used)
        {
            string name = string.IsNullOrWhiteSpace(raw) ? "Sayfa" : raw.Trim();
            foreach (char bad in new[] { '\\', '/', '?', '*', '[', ']', ':' })
                name = name.Replace(bad, '-');
            if (name.Length > 31) name = name.Substring(0, 31);

            string baseName = name;
            int suffix = 2;
            while (!used.Add(name))
            {
                string tail = " (" + suffix++ + ")";
                int keep = 31 - tail.Length;
                name = (baseName.Length > keep ? baseName.Substring(0, keep) : baseName) + tail;
            }
            return name;
        }
    }
}
