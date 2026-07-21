using System;
using System.Drawing;
using System.IO;
using System.Windows.Forms;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Manages the pre-cast manhole catalog Excel file.
    ///
    /// Engine: EPPlus 4.5.3.3  (zero external dependencies — safe inside AutoCAD AppDomain)
    ///
    /// Responsibilities
    /// ----------------
    ///  1. On first run (or if the stored path is missing), generate a blank
    ///     template and ask the user where to save it.
    ///  2. Persist the chosen path to %APPDATA%\UrbanoMetraj\catalog_path.txt
    ///     so subsequent runs load the same file automatically.
    ///  3. Expose a GenerateTemplateInteractive() helper for the dialog button.
    ///
    /// Catalog schema (row-per-part — changed in Phase 2 refinement)
    /// --------------------------------------------------------------
    ///  Column A: Nominal_Diameter  — shaft internal diameter in mm (e.g. 1000)
    ///  Column B: Part_Name         — short label, e.g. "Taban", "Konik", "Govde"
    ///  Column C: Height_m          — height of this component in metres
    ///  Column D: Is_Mandatory      — "Yes" / "No"
    ///            Yes → add exactly 1 unit per manhole regardless of depth
    ///  Column E: Is_Variable_Ring  — "Yes" / "No"
    ///            Yes → eligible for the greedy height-filling (stacking) algorithm
    ///
    /// One row per part per diameter.  Multiple rows share the same Nominal_Diameter.
    /// </summary>
    public static class ManholeConfigService
    {
        // ── Paths ─────────────────────────────────────────────────────────────

        private static readonly string AppDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj");

        private const string TemplateFileName   = "Manhole_Catalog_Template.xlsx";
        private const string CatalogPointerFile = "catalog_path.txt";

        // ── Column headers ────────────────────────────────────────────────────

        private static readonly string[] CatalogHeaders =
        {
            "Nominal_Diameter",
            "Part_Name",
            "Height_m",
            "Is_Mandatory",
            "Is_Variable_Ring"
        };

        // ── Theme ─────────────────────────────────────────────────────────────

        private static readonly Color HeaderBg  = Color.FromArgb(0,   70, 127);
        private static readonly Color AltRowBg  = Color.FromArgb(235, 241, 250);
        private static readonly Color MandBg    = Color.FromArgb(198, 224, 180);   // light green for mandatory
        private static readonly Color VarBg     = Color.FromArgb(255, 242, 204);   // light amber for variable rings

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// Returns the path to the active manhole catalog Excel file.
        /// If no valid catalog is recorded, generates a template and prompts
        /// the user to choose a save location.
        /// </summary>
        public static string EnsureCatalogExists()
        {
            EnsureAppDataDir();

            string pointerPath = Path.Combine(AppDataDir, CatalogPointerFile);
            string catalogPath = ReadPointer(pointerPath);

            if (string.IsNullOrEmpty(catalogPath) || !File.Exists(catalogPath))
            {
                catalogPath = GenerateTemplateInteractive();
                if (!string.IsNullOrEmpty(catalogPath))
                    File.WriteAllText(pointerPath, catalogPath);
            }

            return catalogPath ?? "";
        }

        /// <summary>
        /// Generates the blank catalog template, shows a SaveFileDialog,
        /// and returns the chosen path.  Also called from the dialog's
        /// "Generate Template" button.
        /// </summary>
        public static string GenerateTemplateInteractive()
        {
            EnsureAppDataDir();

            string tempPath = Path.Combine(AppDataDir, TemplateFileName);
            GenerateTemplate(tempPath);

            using (var dlg = new SaveFileDialog
            {
                Title            = "Manhole catalog template created — choose save location",
                Filter           = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt       = "xlsx",
                FileName         = TemplateFileName,
                InitialDirectory = AppDataDir,
                OverwritePrompt  = false
            })
            {
                if (dlg.ShowDialog() != DialogResult.OK)
                    return tempPath;

                string chosen = dlg.FileName;
                if (!string.Equals(chosen, tempPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Copy(tempPath, chosen, overwrite: true); }
                    catch { return tempPath; }
                }

                string pointerPath = Path.Combine(AppDataDir, CatalogPointerFile);
                File.WriteAllText(pointerPath, chosen);
                return chosen;
            }
        }

        // =====================================================================
        // Template generator
        // =====================================================================

        /// <summary>
        /// Generates a ready-to-use Manhole_Catalog.xlsx with the new row-per-part
        /// schema and four sample diameters (800 / 1000 / 1200 / 1500 mm).
        ///
        /// Row color coding:
        ///   Green  background → mandatory parts    (Is_Mandatory = Yes)
        ///   Amber  background → variable rings     (Is_Variable_Ring = Yes)
        ///   White  background → inactive / neither (can be used for notes)
        /// </summary>
        internal static void GenerateTemplate(string path)
        {
            using (var pkg = new ExcelPackage())
            {
                // ── Catalog sheet ─────────────────────────────────────────────
                var ws = pkg.Workbook.Worksheets.Add("Manhole_Catalog");

                // Header row
                for (int c = 0; c < CatalogHeaders.Length; c++)
                {
                    var cell = ws.Cells[1, c + 1];
                    cell.Value = CatalogHeaders[c];
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(HeaderBg);
                    cell.Style.Font.Bold  = true;
                    cell.Style.Font.Color.SetColor(Color.White);
                    cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                    cell.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;
                    cell.Style.WrapText = true;
                    ApplyThinBorder(cell, Color.White);
                }
                ws.Row(1).Height = 30;

                // Sample rows
                // Format: (Nominal_Diameter, Part_Name, Height_m, Is_Mandatory, Is_Variable_Ring)
                // Yes/No strings are intentional — the catalog reader uses ParseBool() which
                // accepts "Yes", "True", "1", "Evet" (case-insensitive).
                var rows = new object[][]
                {
                    // ── 800 mm ──────────────────────────────────────────────────────
                    new object[] {  800, "Taban",  0.25, "Yes", "No"  },
                    new object[] {  800, "Konik",  0.55, "Yes", "No"  },
                    new object[] {  800, "Kapak",  0.10, "Yes", "No"  },
                    new object[] {  800, "Govde",  0.25, "No",  "Yes" },
                    new object[] {  800, "Govde",  0.50, "No",  "Yes" },
                    // ── 1000 mm ─────────────────────────────────────────────────────
                    new object[] { 1000, "Taban",  0.30, "Yes", "No"  },
                    new object[] { 1000, "Konik",  0.60, "Yes", "No"  },
                    new object[] { 1000, "Kapak",  0.10, "Yes", "No"  },
                    new object[] { 1000, "Govde",  0.25, "No",  "Yes" },
                    new object[] { 1000, "Govde",  0.50, "No",  "Yes" },
                    new object[] { 1000, "Govde",  0.75, "No",  "Yes" },
                    new object[] { 1000, "Govde",  1.00, "No",  "Yes" },
                    // ── 1200 mm ─────────────────────────────────────────────────────
                    new object[] { 1200, "Taban",  0.35, "Yes", "No"  },
                    new object[] { 1200, "Konik",  0.70, "Yes", "No"  },
                    new object[] { 1200, "Kapak",  0.10, "Yes", "No"  },
                    new object[] { 1200, "Govde",  0.25, "No",  "Yes" },
                    new object[] { 1200, "Govde",  0.50, "No",  "Yes" },
                    new object[] { 1200, "Govde",  0.75, "No",  "Yes" },
                    new object[] { 1200, "Govde",  1.00, "No",  "Yes" },
                    new object[] { 1200, "Govde",  1.25, "No",  "Yes" },
                    // ── 1500 mm ─────────────────────────────────────────────────────
                    new object[] { 1500, "Taban",  0.40, "Yes", "No"  },
                    new object[] { 1500, "Konik",  0.80, "Yes", "No"  },
                    new object[] { 1500, "Kapak",  0.10, "Yes", "No"  },
                    new object[] { 1500, "Govde",  0.25, "No",  "Yes" },
                    new object[] { 1500, "Govde",  0.50, "No",  "Yes" },
                    new object[] { 1500, "Govde",  0.75, "No",  "Yes" },
                    new object[] { 1500, "Govde",  1.00, "No",  "Yes" },
                    new object[] { 1500, "Govde",  1.25, "No",  "Yes" },
                    new object[] { 1500, "Govde",  1.50, "No",  "Yes" },
                };

                for (int r = 0; r < rows.Length; r++)
                {
                    int excelRow = r + 2;
                    for (int c = 0; c < rows[r].Length; c++)
                        ws.Cells[excelRow, c + 1].Value = rows[r][c];

                    // Color-code rows by part type
                    string isMand = rows[r][3]?.ToString() ?? "";
                    string isVar  = rows[r][4]?.ToString() ?? "";
                    Color rowBg   = isMand.Equals("Yes", StringComparison.OrdinalIgnoreCase) ? MandBg
                                  : isVar .Equals("Yes", StringComparison.OrdinalIgnoreCase) ? VarBg
                                  : Color.White;

                    if (rowBg != Color.White)
                    {
                        for (int c = 1; c <= CatalogHeaders.Length; c++)
                        {
                            ws.Cells[excelRow, c].Style.Fill.PatternType = ExcelFillStyle.Solid;
                            ws.Cells[excelRow, c].Style.Fill.BackgroundColor.SetColor(rowBg);
                        }
                    }
                }

                // Freeze header row
                ws.View.FreezePanes(2, 1);

                // Fixed column widths (no AutoFit — GDI+ crash in AutoCAD)
                ws.Column(1).Width = 20;   // Nominal_Diameter
                ws.Column(2).Width = 18;   // Part_Name
                ws.Column(3).Width = 12;   // Height_m
                ws.Column(4).Width = 16;   // Is_Mandatory
                ws.Column(5).Width = 18;   // Is_Variable_Ring

                // ── Instructions sheet ────────────────────────────────────────
                var instr = pkg.Workbook.Worksheets.Add("Instructions");

                var titleCell = instr.Cells[1, 1];
                titleCell.Value = "Manhole Catalog — Field Descriptions (row-per-part schema)";
                titleCell.Style.Font.Bold = true;
                titleCell.Style.Font.Size = 12;
                titleCell.Style.Font.Color.SetColor(Color.FromArgb(0, 70, 127));
                instr.Row(1).Height = 22;

                string[] lines =
                {
                    "",
                    "COLUMN DEFINITIONS",
                    "  Nominal_Diameter  : Manhole shaft internal diameter in mm (e.g. 1000).",
                    "                     Group as many rows as needed under the same diameter.",
                    "  Part_Name         : Short label for this component (e.g. Taban, Konik, Govde).",
                    "                     You may add country-specific mandatory parts here.",
                    "  Height_m          : Component height in metres (plain number, no units).",
                    "  Is_Mandatory      : Yes / No.  Yes → always 1 unit per manhole.",
                    "  Is_Variable_Ring  : Yes / No.  Yes → used by the greedy ring-stacking algorithm.",
                    "",
                    "ROW COLOR CODING (reference only — does not affect the algorithm)",
                    "  Green background  → mandatory part    (Is_Mandatory = Yes)",
                    "  Amber background  → variable ring     (Is_Variable_Ring = Yes)",
                    "",
                    "ALGORITHM OVERVIEW",
                    "  1. Collect all rows for this manhole diameter.",
                    "  2. Mandatory parts: add 1 unit each; sum their heights = Fixed_Stack_Height.",
                    "  3. Remaining = Total_Manhole_Depth - Fixed_Stack_Height.",
                    "  4. Variable rings: greedy fill (largest first) until Remaining <= 0.",
                    "  5. If leftover gap > 0.05 m, add 1 extra of the smallest ring.",
                    "",
                    "RULES",
                    "  - One row per part per diameter.  Do NOT merge rows.",
                    "  - Numeric values must be plain numbers (no units, no text).",
                    "  - Is_Mandatory and Is_Variable_Ring accept: Yes / No / True / False / 1 / 0.",
                    "  - A part can be both mandatory AND variable-ring (unusual, but supported).",
                    "  - Do NOT rename or delete the Manhole_Catalog sheet.",
                    "  - Save as .xlsx."
                };

                for (int i = 0; i < lines.Length; i++)
                {
                    var cell = instr.Cells[i + 2, 1];
                    cell.Value = lines[i];
                    if (lines[i].StartsWith("COLUMN") || lines[i].StartsWith("ROW") ||
                        lines[i].StartsWith("ALGO")   || lines[i].StartsWith("RULES"))
                    {
                        cell.Style.Font.Bold = true;
                        cell.Style.Font.Color.SetColor(Color.FromArgb(0, 70, 127));
                    }
                }

                instr.Column(1).Width = 80;

                // Save with IO-lock guard
                try
                {
                    pkg.SaveAs(new FileInfo(path));
                }
                catch (IOException ioEx)
                {
                    MessageBox.Show(
                        "Could not save the catalog template:\n" + path + "\n\n" +
                        "The file may be open in Excel. Please close it and try again.\n\n" +
                        "Details: " + ioEx.Message,
                        "Save Failed — File Locked",
                        MessageBoxButtons.OK,
                        MessageBoxIcon.Warning);
                    throw;
                }
            }
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        private static void ApplyThinBorder(ExcelRange cell, Color color)
        {
            var b = cell.Style.Border;
            b.Top.Style    = ExcelBorderStyle.Thin; b.Top.Color.SetColor(color);
            b.Bottom.Style = ExcelBorderStyle.Thin; b.Bottom.Color.SetColor(color);
            b.Left.Style   = ExcelBorderStyle.Thin; b.Left.Color.SetColor(color);
            b.Right.Style  = ExcelBorderStyle.Thin; b.Right.Color.SetColor(color);
        }

        private static string ReadPointer(string pointerPath)
        {
            if (!File.Exists(pointerPath)) return "";
            try { return File.ReadAllText(pointerPath).Trim(); }
            catch { return ""; }
        }

        private static void EnsureAppDataDir()
        {
            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);
        }
    }
}
