using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using OfficeOpenXml;
using OfficeOpenXml.Style;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Generates a professionally formatted multi-sheet Excel workbook from a BoQReport.
    ///
    /// Engine: EPPlus 4.5.3.3  (zero external dependencies — safe inside AutoCAD AppDomain)
    ///
    /// Workbook layout
    /// ---------------
    ///  [Summary]             - grand totals across all network systems
    ///  [{Sys}_Pipes]         - pipe line-items for one system (one sheet per system)
    ///  [{Sys}_Manholes]      - manhole line-items + Phase 2 smart type name
    ///  [Manhole_BOM]         - Phase 2 aggregated bill of materials
    ///  [Clash_Debug]         - overlap detection log (only present when clashes exist)
    ///
    /// Localization
    /// ------------
    ///  Column headers are written in English, Turkish, or Russian depending on
    ///  BoQSettings.Language.  All string literals in this source file are English
    ///  or ASCII-safe transliterations to comply with the project coding convention.
    ///
    /// Math invariant
    /// --------------
    ///  All volume values consumed here come from BoQReport, which already carries
    ///  the clash-deducted figures.  This service performs NO re-calculation.
    ///
    /// IMPORTANT: AdjustToContents / AutoFit are intentionally NOT used.
    ///  Both call GDI+ font-measurement APIs that crash inside AutoCAD's AppDomain.
    ///  Fixed column widths are applied instead.
    /// </summary>
    public static class ExcelExportService
    {
        // =====================================================================
        // Theme palette  (System.Drawing.Color — EPPlus 4 does not use XLColor)
        // =====================================================================

        private static readonly Color ThemeBlue     = Color.FromArgb(0,   70, 127);
        private static readonly Color ThemeMidBlue = Color.FromArgb(0,   50, 100);   // total / BOM header rows
        private static readonly Color ThemeSubtotal= Color.FromArgb(0,   90, 160);   // diameter subtotal rows
        private static readonly Color ThemeAltRow  = Color.FromArgb(235, 241, 250);
        private static readonly Color ThemeBorder  = Color.FromArgb(180, 198, 231);
        private static readonly Color ThemeWhite   = Color.White;
        private static readonly Color ThemeGold    = Color.FromArgb(198, 146, 20);   // BOM title accent

        // =====================================================================
        // Localized header tables
        // All strings are ASCII-safe (no diacritics / Cyrillic in source code).
        // =====================================================================

        private static readonly Dictionary<ExportLanguage, string[]> PipeHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                // Col 0: Pipe section name  Col 1: Diameter  Col 2: Material
                // Col 3: Length  Col 4: Excav  Col 5: Bed  Col 6: Surr  Col 7: Backfill
                // Col 8: PozNo  Col 9: Sinif  Col 10: Aciklama (from Type Mapping link)
                // Col 11: Overlap Excav Deducted  Col 12: Overlap Backfill Deducted
                [ExportLanguage.English] = new[]
                {
                    "Pipe Section", "Diameter (mm)", "Material", "Length (m)",
                    "Excavation (m3)", "Bedding (m3)", "Surround (m3)", "Backfill (m3)",
                    "Poz No", "Class", "Notes",
                    "Excav. Deducted (m3)", "Backfill Deducted (m3)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Boru Hatti", "Cap (mm)", "Malzeme", "Boy (m)",
                    "Kazi (m3)", "Yataklama (m3)", "Gomlekleme (m3)", "Geri Dolgu (m3)",
                    "Poz No", "Sinif", "Aciklama",
                    "Kazi Dusumu (m3)", "Dolgu Dusumu (m3)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Uchastok", "Diametr (mm)", "Material", "Dlina (m)",
                    "Vykopka (m3)", "Podstilka (m3)", "Okruzheniye (m3)", "Zasypka (m3)",
                    "Poz No", "Klass", "Primechaniye",
                    "Vych. Vykopka (m3)", "Vych. Zasypka (m3)"
                }
            };

        /// <summary>
        /// Manhole sheet columns (Phase 2): Type column added after Node Name.
        /// Columns: NodeName | Type (SmartName) | Diameter | Depth | Easting | Northing | TerrainElev
        /// </summary>
        private static readonly Dictionary<ExportLanguage, string[]> ManholeHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "Node Name", "Type", "Diameter (mm)", "Depth (m)",
                    "Excav. Depth (m)", "Excav. Volume (m3)",
                    "Easting (m)", "Northing (m)", "Terrain Elev (m)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Baca Adi", "Tip", "Cap (mm)", "Derinlik (m)",
                    "Kazi Derinligi (m)", "Kazi Hacmi (m3)",
                    "X (m)", "Y (m)", "Arazi Kotu (m)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Nazvanie Uzla", "Tip", "Diametr (mm)", "Glubina (m)",
                    "Glubina Vykopki (m)", "Ob'em Vykopki (m3)",
                    "X (m)", "Y (m)", "Otmetka Terr. (m)"
                }
            };

        private static readonly Dictionary<ExportLanguage, string[]> SummaryHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                // Indices: 0=System 1=PipeLength 2=ManholeCount 3=Excav 4=Bed 5=Surr 6=Backfill 7=MhExcav 8=OverlapExcav 9=OverlapBackfill
                [ExportLanguage.English] = new[]
                {
                    "System", "Total Pipe Length (m)", "Manhole Count",
                    "Total Excavation (m3)", "Total Bedding (m3)",
                    "Total Surround (m3)", "Total Backfill (m3)",
                    "Manhole Excav. (m3)", "Excav. Deducted (m3)", "Backfill Deducted (m3)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Sistem", "Toplam Boru Boyu (m)", "Baca Sayisi",
                    "Toplam Kazi (m3)", "Toplam Yataklama (m3)",
                    "Toplam Gomlekleme (m3)", "Toplam Geri Dolgu (m3)",
                    "Baca Kazi (m3)", "Kazi Dusumu (m3)", "Dolgu Dusumu (m3)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Sistema", "Obshch. Dlina Trub (m)", "Kol-vo Kolodtsev",
                    "Obshch. Vykopka (m3)", "Obshch. Podstilka (m3)",
                    "Okruzheniye (m3)", "Obshch. Zasypka (m3)",
                    "Vykopka Kolodtsev (m3)", "Vych. Vykopka (m3)", "Vych. Zasypka (m3)"
                }
            };

        /// <summary>
        /// Trench layer breakdown sheet (Phase 2b): per PipeTrenchCatalog sub-layer
        /// volumes (Yataklama/Boru Etrafı/Boru Üstü/Geri Dolgu), aggregated the same
        /// way as the Pipes sheet (by Diameter + Pipe Material), one row per unique
        /// sub-layer within that group. Empty when no linked pipe has a matching
        /// PipeTrenchCatalog rule (old DWG, or no rule configured for that diameter).
        /// </summary>
        private static readonly Dictionary<ExportLanguage, string[]> TrenchLayersHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "Diameter (mm)", "Pipe Material", "Layer Group", "Layer Name",
                    "Layer Material", "Volume (m3)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Cap (mm)", "Boru Malzemesi", "Katman Grubu", "Katman Adi",
                    "Katman Malzemesi", "Hacim (m3)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Diametr (mm)", "Material Truby", "Gruppa Sloya", "Nazvanie Sloya",
                    "Material Sloya", "Ob'em (m3)"
                }
            };

        private static readonly Dictionary<ExportLanguage, string[]> TrenchLayerGroupNameMap =
            new Dictionary<ExportLanguage, string[]>
            {
                // 0=Yataklama 1=BoruEtrafi 2=BoruUstu 3=GeriDolgu
                [ExportLanguage.English] = new[] { "Bedding", "Pipe Surround", "Above Pipe", "Backfill" },
                [ExportLanguage.Turkish] = new[] { "Yataklama", "Boru Etrafi", "Boru Ustu", "Geri Dolgu" },
                [ExportLanguage.Russian] = new[] { "Podstilka", "Vokrug Truby", "Nad Truboy", "Zasypka" }
            };

        private static readonly Dictionary<ExportLanguage, string[]> BomHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[] { "Description", "Quantity", "Unit", "Total Depth (m)" },
                [ExportLanguage.Turkish] = new[] { "Aciklama",    "Miktar",   "Birim","Toplam Derinlik (m)" },
                [ExportLanguage.Russian] = new[] { "Opisanie",    "Kolichestvo", "Edinitsa", "Obshch. Glubina (m)" }
            };

        /// <summary>
        /// Pre-cast Manhole_BOM columns only — proves each stacked part came from our
        /// own ComponentFamily catalog (Poz No / Notes / volumes), not just a name+count.
        /// </summary>
        private static readonly Dictionary<ExportLanguage, string[]> BomHeaderMapPreCast =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "Description", "Quantity", "Unit",
                    "Poz No", "Notes", "Material Volume (m3)", "External Volume (m3)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Aciklama", "Miktar", "Birim",
                    "Poz No", "Notlar", "Malzeme Hacmi (m3)", "Dis Hacim (m3)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Opisanie", "Kolichestvo", "Edinitsa",
                    "Poz No", "Primechaniya", "Ob'em Materiala (m3)", "Vneshniy Ob'em (m3)"
                }
            };

        private static readonly Dictionary<ExportLanguage, string> TotalLabelMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "GRAND TOTAL",
                [ExportLanguage.Turkish] = "GENEL TOPLAM",
                [ExportLanguage.Russian] = "VSEGO"
            };

        private static readonly Dictionary<ExportLanguage, string> SubtotalLabelMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Subtotal",
                [ExportLanguage.Turkish] = "Ara Toplam",
                [ExportLanguage.Russian] = "Promezh. Itog"
            };

        // =====================================================================
        // Public entry point
        // =====================================================================

        /// <summary>
        /// Generates and saves the Excel workbook to <c>settings.ExportFilePath</c>.
        /// Shows a MessageBox and re-throws if the file is locked (already open in Excel).
        /// </summary>
        public static void Export(BoQReport report, BoQSettings settings)
        {
            string[] pHeaders = PipeHeaderMap        [settings.Language];
            string[] mHeaders = ManholeHeaderMap     [settings.Language];
            string[] sHeaders = SummaryHeaderMap     [settings.Language];
            string[] bHeaders = BomHeaderMap         [settings.Language];
            string[] bPreCastHeaders = BomHeaderMapPreCast[settings.Language];
            string[] tHeaders = TrenchLayersHeaderMap[settings.Language];
            string[] tGroupNames = TrenchLayerGroupNameMap[settings.Language];
            string   totalLbl = TotalLabelMap   [settings.Language];

            bool showOverlap = settings.EnableClashDetection
                            && (report.TotalOverlapExcavDeducted + report.TotalOverlapBackfillDeducted) > 1e-6;

            using (var pkg = new ExcelPackage())
            {
                WriteSummarySheet(pkg, report, sHeaders, totalLbl, showOverlap);

                foreach (var sys in report.Systems)
                {
                    string safe = SanitizeSheetName(sys.SystemName);
                    var sections = report.SectionDebug?
                        .Where(r => r.SystemName == sys.SystemName).ToList();
                    WritePipeSheet(pkg, sys, sections, safe, pHeaders,
                                   totalLbl, SubtotalLabelMap[settings.Language], showOverlap);
                    WriteManholeSheet(pkg, sys, safe, mHeaders);
                    WriteTrenchLayersSheet(pkg, sys, sections, safe, tHeaders, tGroupNames,
                                            totalLbl, SubtotalLabelMap[settings.Language]);
                }

                // Phase 2 – Bill of Materials sheet (system-isolated)
                var bomGroups = ManholeAIService.BuildBomBySystem(report, settings);
                WriteBomSheet(pkg, bomGroups, bHeaders, bPreCastHeaders, settings);

                if (showOverlap
                    && report.SectionDebug != null
                    && report.SectionDebug.Any(s => s.ClashLog != null && s.ClashLog.Count > 0))
                {
                    WriteClashDebugSheet(pkg, report);
                }

                SavePackage(pkg, settings.ExportFilePath);
            }
        }

        // =====================================================================
        // Summary sheet
        // =====================================================================

        private static void WriteSummarySheet(ExcelPackage pkg, BoQReport report,
            string[] hdr, string totalLbl, bool showOverlap)
        {
            var ws       = pkg.Workbook.Worksheets.Add("Summary");
            int colCount = showOverlap ? hdr.Length : hdr.Length - 2;

            WriteTitle(ws, "Urbano Network — Bill of Quantities", colCount, 1);

            var subtitleCell = ws.Cells[2, 1];
            subtitleCell.Value = "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            subtitleCell.Style.Font.Italic = true;
            subtitleCell.Style.Font.Color.SetColor(Color.Gray);
            if (colCount > 1) ws.Cells[2, 1, 2, colCount].Merge = true;

            const int hdrRow = 4;
            WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int  row = hdrRow + 1;
            bool alt = false;
            foreach (var sys in report.Systems)
            {
                ws.Cells[row, 1].Value = sys.SystemName;
                ws.Cells[row, 2].Value = sys.Pipes.Sum(p => p.TotalLength);
                ws.Cells[row, 3].Value = (double)sys.Manholes.Sum(m => m.Count);
                ws.Cells[row, 4].Value = sys.Pipes.Sum(p => p.TotalExcavationVolume);
                ws.Cells[row, 5].Value = sys.Pipes.Sum(p => p.TotalBeddingVolume);
                ws.Cells[row, 6].Value = sys.Pipes.Sum(p => p.TotalSurroundVolume);
                ws.Cells[row, 7].Value = sys.Pipes.Sum(p => p.TotalBackfillVolume);
                ws.Cells[row, 8].Value = sys.Manholes.Sum(m => m.ExcavationVolume);
                if (showOverlap)
                {
                    ws.Cells[row, 9].Value  = sys.Pipes.Sum(p => p.OverlapExcavDeducted);
                    ws.Cells[row, 10].Value = sys.Pipes.Sum(p => p.OverlapBackfillDeducted);
                }

                ApplyDataRowStyle(ws, row, colCount, alt);
                SetNumericFormat(ws, row, 2, colCount, "#,##0.000");
                alt = !alt;
                row++;
            }

            ws.Cells[row, 1].Value = totalLbl;
            ws.Cells[row, 2].Value = report.TotalPipeLength;
            ws.Cells[row, 3].Value = (double)report.TotalManholeCount;
            ws.Cells[row, 4].Value = report.TotalExcavationVolume;
            ws.Cells[row, 5].Value = report.TotalBeddingVolume;
            ws.Cells[row, 6].Value = report.TotalSurroundVolume;
            ws.Cells[row, 7].Value = report.TotalBackfillVolume;
            ws.Cells[row, 8].Value = report.TotalManholeExcavationVolume;
            if (showOverlap)
            {
                ws.Cells[row, 9].Value  = report.TotalOverlapExcavDeducted;
                ws.Cells[row, 10].Value = report.TotalOverlapBackfillDeducted;
            }
            ApplyTotalRowStyle(ws, row, colCount);
            SetNumericFormat(ws, row, 2, colCount, "#,##0.000");

            SetFixedWidths(ws, colCount, firstColWidth: 22, dataColWidth: 20);
        }

        // =====================================================================
        // Pipes sheet  —  one per network system
        // =====================================================================

        private static void WritePipeSheet(ExcelPackage pkg, SystemBoQ sys,
            List<SectionDebugRow> sections, string safeName,
            string[] hdr, string totalLbl, string subtotalLbl, bool showOverlap)
        {
            string sheetName = Truncate(safeName + "_Pipes", 31);
            var ws           = pkg.Workbook.Worksheets.Add(sheetName);
            int colCount     = showOverlap ? hdr.Length : hdr.Length - 2;

            WriteTitle(ws, sys.SystemName + " — Pipes", colCount, 1);

            const int hdrRow = 3;
            WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int row = hdrRow + 1;

            // ── Per-section breakdown: grouped by diameter ──────────────────────
            if (sections != null && sections.Count > 0)
            {
                var groups = sections
                    .OrderBy(r => r.DiameterMm)
                    .ThenBy(r => r.PipeName)
                    .GroupBy(r => new { r.DiameterMm, Mat = r.Material ?? "" });

                double gtLen = 0, gtEx = 0, gtBe = 0, gtSu = 0, gtBa = 0, gtOvEx = 0, gtOvBf = 0;

                foreach (var grp in groups)
                {
                    bool alt = false;
                    double stLen = 0, stEx = 0, stBe = 0, stSu = 0, stBa = 0, stOvEx = 0, stOvBf = 0;

                    foreach (var r in grp)
                    {
                        ws.Cells[row, 1].Value = r.PipeName;
                        ws.Cells[row, 2].Value = r.DiameterMm + " mm";
                        ws.Cells[row, 3].Value = r.Material;
                        ws.Cells[row, 4].Value = r.Length2D;
                        ws.Cells[row, 5].Value = r.VExcav;
                        ws.Cells[row, 6].Value = r.VBedding;
                        ws.Cells[row, 7].Value = r.VSurround;
                        ws.Cells[row, 8].Value = r.VBackfill;
                        ws.Cells[row, 9].Value  = r.PozNo;
                        ws.Cells[row, 10].Value = r.Sinif;
                        ws.Cells[row, 11].Value = r.Aciklama;
                        if (showOverlap)
                        {
                            ws.Cells[row, 12].Value = r.OverlapExcavDeducted;
                            ws.Cells[row, 13].Value = r.OverlapBackfillDeducted;
                        }

                        ApplyDataRowStyle(ws, row, colCount, alt);
                        SetNumericFormat(ws, row, 4, 8, "#,##0.000");
                        if (showOverlap) SetNumericFormat(ws, row, 12, 13, "#,##0.000");
                        alt = !alt;

                        stLen += r.Length2D;  stEx += r.VExcav;
                        stBe  += r.VBedding;  stSu += r.VSurround;
                        stBa  += r.VBackfill;
                        stOvEx += r.OverlapExcavDeducted;
                        stOvBf += r.OverlapBackfillDeducted;
                        row++;
                    }

                    string subLbl = subtotalLbl + " Ø" + grp.Key.DiameterMm + " mm";
                    ws.Cells[row, 1].Value = subLbl;
                    ws.Cells[row, 4].Value = stLen;
                    ws.Cells[row, 5].Value = stEx;
                    ws.Cells[row, 6].Value = stBe;
                    ws.Cells[row, 7].Value = stSu;
                    ws.Cells[row, 8].Value = stBa;
                    if (showOverlap)
                    {
                        ws.Cells[row, 12].Value = stOvEx;
                        ws.Cells[row, 13].Value = stOvBf;
                    }
                    ApplySubtotalRowStyle(ws, row, colCount);
                    SetNumericFormat(ws, row, 4, 8, "#,##0.000");
                    if (showOverlap) SetNumericFormat(ws, row, 12, 13, "#,##0.000");
                    row++;

                    gtLen += stLen; gtEx += stEx; gtBe += stBe;
                    gtSu  += stSu;  gtBa += stBa;
                    gtOvEx += stOvEx; gtOvBf += stOvBf;
                }

                ws.Cells[row, 1].Value = totalLbl;
                ws.Cells[row, 4].Value = gtLen;
                ws.Cells[row, 5].Value = gtEx;
                ws.Cells[row, 6].Value = gtBe;
                ws.Cells[row, 7].Value = gtSu;
                ws.Cells[row, 8].Value = gtBa;
                if (showOverlap)
                {
                    ws.Cells[row, 12].Value = gtOvEx;
                    ws.Cells[row, 13].Value = gtOvBf;
                }
                ApplyTotalRowStyle(ws, row, colCount);
                SetNumericFormat(ws, row, 4, 8, "#,##0.000");
                if (showOverlap) SetNumericFormat(ws, row, 12, 13, "#,##0.000");
            }
            else
            {
                // Fallback: old DWG without section debug data — aggregate view
                bool alt = false;
                foreach (var p in sys.Pipes)
                {
                    ws.Cells[row, 1].Value = "—";
                    ws.Cells[row, 2].Value = p.Diameter + " mm";
                    ws.Cells[row, 3].Value = p.Material;
                    ws.Cells[row, 4].Value = p.TotalLength;
                    ws.Cells[row, 5].Value = p.TotalExcavationVolume;
                    ws.Cells[row, 6].Value = p.TotalBeddingVolume;
                    ws.Cells[row, 7].Value = p.TotalSurroundVolume;
                    ws.Cells[row, 8].Value = p.TotalBackfillVolume;
                    // Columns 9-11 (PozNo/Sinif/Aciklama) unavailable in this fallback
                    // path — PipeItem is a diameter+material aggregate, no Type
                    // Mapping link data survives the old (pre-SectionDebug) format.
                    if (showOverlap)
                    {
                        ws.Cells[row, 12].Value = p.OverlapExcavDeducted;
                        ws.Cells[row, 13].Value = p.OverlapBackfillDeducted;
                    }

                    ApplyDataRowStyle(ws, row, colCount, alt);
                    SetNumericFormat(ws, row, 4, 8, "#,##0.000");
                    if (showOverlap) SetNumericFormat(ws, row, 12, 13, "#,##0.000");
                    alt = !alt;
                    row++;
                }

                ws.Cells[row, 1].Value = totalLbl;
                ws.Cells[row, 4].Value = sys.Pipes.Sum(p => p.TotalLength);
                ws.Cells[row, 5].Value = sys.Pipes.Sum(p => p.TotalExcavationVolume);
                ws.Cells[row, 6].Value = sys.Pipes.Sum(p => p.TotalBeddingVolume);
                ws.Cells[row, 7].Value = sys.Pipes.Sum(p => p.TotalSurroundVolume);
                ws.Cells[row, 8].Value = sys.Pipes.Sum(p => p.TotalBackfillVolume);
                if (showOverlap)
                {
                    ws.Cells[row, 12].Value = sys.Pipes.Sum(p => p.OverlapExcavDeducted);
                    ws.Cells[row, 13].Value = sys.Pipes.Sum(p => p.OverlapBackfillDeducted);
                }
                ApplyTotalRowStyle(ws, row, colCount);
                SetNumericFormat(ws, row, 4, 8, "#,##0.000");
                if (showOverlap) SetNumericFormat(ws, row, 12, 13, "#,##0.000");
            }

            ws.Column(1).Width = 22;   // Pipe section name
            ws.Column(2).Width = 14;   // Diameter
            ws.Column(3).Width = 12;   // Material
            for (int c = 4; c <= colCount; c++)
                ws.Column(c).Width = 18;
        }

        // =====================================================================
        // Trench layer breakdown sheet — one per network system (Phase 2b)
        // =====================================================================

        private static void WriteTrenchLayersSheet(ExcelPackage pkg, SystemBoQ sys,
            List<SectionDebugRow> sections, string safeName,
            string[] hdr, string[] groupNames, string totalLbl, string subtotalLbl)
        {
            string sheetName = Truncate(safeName + "_Trench_Layers", 31);
            var ws           = pkg.Workbook.Worksheets.Add(sheetName);
            int colCount     = hdr.Length;

            WriteTitle(ws, sys.SystemName + " — Trench Layer Breakdown", colCount, 1);

            const int hdrRow = 3;
            WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int row = hdrRow + 1;

            // (GroupIndex, LayerName, LayerMaterial, Volume) flattened out of the
            // four split lists on every section — group index picks the localized
            // group name and keeps bedding/etrafi/ustu/backfill in a fixed order.
            var flat = new List<(int DiameterMm, string PipeMat, int GroupIdx, string LayerName, string LayerMat, double Volume)>();
            foreach (var r in sections ?? Enumerable.Empty<SectionDebugRow>())
            {
                void Add(int gi, List<TrenchLayerSplit> splits)
                {
                    foreach (var l in splits ?? Enumerable.Empty<TrenchLayerSplit>())
                        flat.Add((r.DiameterMm, r.Material ?? "", gi, l.LayerName ?? "", l.MaterialType ?? "", l.Volume));
                }
                Add(0, r.BeddingLayerSplits);
                Add(1, r.BoruEtrafiLayerSplits);
                Add(2, r.BoruUstuLayerSplits);
                Add(3, r.BackfillLayerSplits);
            }

            double grandTotal = 0;
            var diamGroups = flat
                .GroupBy(f => new { f.DiameterMm, f.PipeMat })
                .OrderBy(g => g.Key.DiameterMm);

            foreach (var dGrp in diamGroups)
            {
                bool alt = false;
                double subtotal = 0;

                var layerGroups = dGrp
                    .GroupBy(f => new { f.GroupIdx, f.LayerName, f.LayerMat })
                    .OrderBy(g => g.Key.GroupIdx)
                    .ThenBy(g => g.Key.LayerName);

                foreach (var lGrp in layerGroups)
                {
                    double vol = lGrp.Sum(x => x.Volume);

                    ws.Cells[row, 1].Value = dGrp.Key.DiameterMm + " mm";
                    ws.Cells[row, 2].Value = dGrp.Key.PipeMat;
                    ws.Cells[row, 3].Value = groupNames[lGrp.Key.GroupIdx];
                    ws.Cells[row, 4].Value = lGrp.Key.LayerName;
                    ws.Cells[row, 5].Value = lGrp.Key.LayerMat;
                    ws.Cells[row, 6].Value = vol;

                    ApplyDataRowStyle(ws, row, colCount, alt);
                    SetNumericFormat(ws, row, 6, colCount, "#,##0.000");
                    alt = !alt;

                    subtotal += vol;
                    row++;
                }

                string subLbl = subtotalLbl + " Ø" + dGrp.Key.DiameterMm + " mm";
                ws.Cells[row, 1].Value = subLbl;
                ws.Cells[row, 6].Value = subtotal;
                ApplySubtotalRowStyle(ws, row, colCount);
                SetNumericFormat(ws, row, 6, colCount, "#,##0.000");
                row++;

                grandTotal += subtotal;
            }

            if (flat.Count > 0)
            {
                ws.Cells[row, 1].Value = totalLbl;
                ws.Cells[row, 6].Value = grandTotal;
                ApplyTotalRowStyle(ws, row, colCount);
                SetNumericFormat(ws, row, 6, colCount, "#,##0.000");
            }

            ws.Column(1).Width = 14;   // Diameter
            ws.Column(2).Width = 16;   // Pipe material
            ws.Column(3).Width = 16;   // Layer group
            ws.Column(4).Width = 20;   // Layer name
            ws.Column(5).Width = 16;   // Layer material
            ws.Column(6).Width = 16;   // Volume
        }

        // =====================================================================
        // Manholes sheet  —  one per network system  (Phase 2: Type column added)
        // =====================================================================

        private static void WriteManholeSheet(ExcelPackage pkg, SystemBoQ sys,
            string safeName, string[] hdr)
        {
            string sheetName = Truncate(safeName + "_Manholes", 31);
            var ws           = pkg.Workbook.Worksheets.Add(sheetName);
            int colCount     = hdr.Length;

            WriteTitle(ws, sys.SystemName + " — Manholes", colCount, 1);

            const int hdrRow = 3;
            WriteHeaders(ws, hdrRow, hdr, colCount);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int  row = hdrRow + 1;
            bool alt = false;
            foreach (var m in sys.Manholes)
            {
                // Col 1: Node Name
                ws.Cells[row, 1].Value = m.NodeName;
                // Col 2: Type — use SmartTypeName if available, otherwise fallback
                ws.Cells[row, 2].Value = !string.IsNullOrEmpty(m.SmartTypeName)
                    ? m.SmartTypeName
                    : $"Baca {m.DiameterDisplay}";
                // Col 3: Diameter (mm) — "side×side"/"length×width" for non-circular
                ws.Cells[row, 3].Value = m.DiameterDisplay + " mm";
                // Col 4: Depth (m)
                ws.Cells[row, 4].Value = m.Depth;
                // Col 5: Excavation depth H = TerrainElev − lowestInvert (m)
                ws.Cells[row, 5].Value = m.ExcavationDepth;
                // Col 6: Isolated excavation volume (m³) — no trench overlap deduction yet
                ws.Cells[row, 6].Value = m.ExcavationVolume;
                // Col 7: Easting
                ws.Cells[row, 7].Value = m.X;
                // Col 8: Northing
                ws.Cells[row, 8].Value = m.Y;
                // Col 9: Terrain elevation
                ws.Cells[row, 9].Value = m.TerrainElevation;

                ApplyDataRowStyle(ws, row, colCount, alt);
                SetNumericFormat(ws, row, 4, 9, "#,##0.000");
                alt = !alt;
                row++;
            }

            // Fixed widths — Type column (col 2) is wider to accommodate SmartTypeName
            ws.Column(1).Width = 16;   // Node Name
            ws.Column(2).Width = 36;   // Type (smart name — widest)
            ws.Column(3).Width = 14;   // Diameter
            ws.Column(4).Width = 14;   // Depth
            ws.Column(5).Width = 18;   // Excav. Depth
            ws.Column(6).Width = 18;   // Excav. Volume
            ws.Column(7).Width = 16;   // Easting
            ws.Column(8).Width = 16;   // Northing
            ws.Column(9).Width = 16;   // Terrain Elev
        }

        // =====================================================================
        // Manhole BOM sheet  (Phase 2 — system-isolated groups)
        // =====================================================================

        /// <summary>
        /// Writes one Manhole_BOM sheet.  Each network system gets its own
        /// clearly labelled section within the sheet:
        ///
        ///   ┌─ SYSTEM: ET1_YSU ──────────────────────────────────────────┐
        ///   │  [column headers]                                          │
        ///   │  Taban O1000 mm          3   Adet                         │
        ///   │  Konik O1000 mm          3   Adet                         │
        ///   │  Govde 1.00m O1000 mm    5   Adet                         │
        ///   └────────────────────────────────────────────────────────────┘
        ///   [blank separator row]
        ///   ┌─ SYSTEM: ET2_ASU ──────────────────────────────────────────┐
        ///   │  ...                                                       │
        ///   └────────────────────────────────────────────────────────────┘
        /// </summary>
        private static void WriteBomSheet(
            ExcelPackage          pkg,
            List<SystemBomGroup>  groups,
            string[]              hdr,
            string[]              hdrPreCast,
            BoQSettings           settings)
        {
            var  ws        = pkg.Workbook.Worksheets.Add("Manhole_BOM");
            bool isPreCast = settings.ManholeType == ManholeType.PreCast;
            // Pre-cast: Description/Quantity/Unit + PozNo/Notes/MaterialVol/ExternalVol
            // (proves each part came from our ComponentFamily catalog). CIP: adds
            // TotalDepth instead — no per-component catalog data applies there.
            if (isPreCast) hdr = hdrPreCast;
            int dataCols = isPreCast ? 7 : 4;

            // ── Workbook title ────────────────────────────────────────────────
            string pageTitle = isPreCast
                ? "Manhole Bill of Materials — Prefabrik Baca Malzeme Listesi"
                : "Manhole Bill of Materials — Yerinde Dokme Baca Listesi";

            ws.Cells[1, 1].Value = pageTitle;
            ws.Cells[1, 1].Style.Font.Bold = true;
            ws.Cells[1, 1].Style.Font.Size = 13;
            ws.Cells[1, 1].Style.Font.Color.SetColor(ThemeBlue);
            ws.Cells[1, 1, 1, dataCols].Merge = true;
            ws.Row(1).Height = 24;
            ws.Row(2).Height = 6;

            ws.Cells[2, 1].Value = "Generated: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            ws.Cells[2, 1].Style.Font.Italic = true;
            ws.Cells[2, 1].Style.Font.Color.SetColor(Color.Gray);
            ws.Cells[2, 1, 2, dataCols].Merge = true;

            // No global freeze — freeze after first column-header row is written below.

            int  sheetRow  = 3;  // current writing position
            bool anySystem = false;

            foreach (var grp in groups)
            {
                // ── Blank separator between systems (skip before the first) ───
                if (anySystem)
                {
                    ws.Row(sheetRow).Height = 8;
                    sheetRow++;
                }
                anySystem = true;

                // ── System header band ────────────────────────────────────────
                string sysLabel = $"  SYSTEM: {grp.SystemName}";
                for (int c = 1; c <= dataCols; c++)
                {
                    var cell = ws.Cells[sheetRow, c];
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(ThemeMidBlue);
                    cell.Style.Font.Bold  = true;
                    cell.Style.Font.Color.SetColor(ThemeWhite);
                    cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
                    // Bottom border to visually separate from column headers
                    cell.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
                    cell.Style.Border.Bottom.Color.SetColor(ThemeWhite);
                }
                ws.Cells[sheetRow, 1].Value = sysLabel;
                ws.Cells[sheetRow, 1, sheetRow, dataCols].Merge = true;
                ws.Row(sheetRow).Height = 22;
                sheetRow++;

                // ── Column headers for this system's table ────────────────────
                WriteHeaders(ws, sheetRow, hdr, dataCols);
                sheetRow++;

                // ── Data rows ─────────────────────────────────────────────────
                bool alt = false;

                if (grp.Lines.Count == 0)
                {
                    ws.Cells[sheetRow, 1].Value = "(No data — catalog entry not found for this system's manholes)";
                    ws.Cells[sheetRow, 1, sheetRow, dataCols].Merge = true;
                    ws.Cells[sheetRow, 1].Style.Font.Italic = true;
                    ws.Cells[sheetRow, 1].Style.Font.Color.SetColor(Color.Gray);
                    sheetRow++;
                }
                else
                {
                    foreach (var bl in grp.Lines)
                    {
                        ws.Cells[sheetRow, 1].Value = bl.Description;
                        ws.Cells[sheetRow, 2].Value = bl.Quantity;
                        ws.Cells[sheetRow, 3].Value = bl.Unit;

                        ws.Cells[sheetRow, 2].Style.Numberformat.Format = "#,##0";
                        ws.Cells[sheetRow, 2].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;

                        if (isPreCast)
                        {
                            ws.Cells[sheetRow, 4].Value = bl.PozNo;
                            ws.Cells[sheetRow, 5].Value = bl.Aciklama;
                            ws.Cells[sheetRow, 6].Value = Math.Round(bl.TotalMaterialVolume, 4);
                            ws.Cells[sheetRow, 7].Value = Math.Round(bl.TotalExternalVolume, 4);
                            ws.Cells[sheetRow, 6].Style.Numberformat.Format = "#,##0.0000";
                            ws.Cells[sheetRow, 7].Style.Numberformat.Format = "#,##0.0000";
                            ws.Cells[sheetRow, 6].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                            ws.Cells[sheetRow, 7].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                        }
                        else if (bl.TotalDepthM > 0)
                        {
                            ws.Cells[sheetRow, 4].Value = Math.Round(bl.TotalDepthM, 3);
                            ws.Cells[sheetRow, 4].Style.Numberformat.Format  = "#,##0.000";
                            ws.Cells[sheetRow, 4].Style.HorizontalAlignment  = ExcelHorizontalAlignment.Right;
                        }

                        ApplyDataRowStyle(ws, sheetRow, dataCols, alt);
                        alt = !alt;
                        sheetRow++;
                    }
                }
            }

            // Handle completely empty report
            if (!anySystem)
            {
                ws.Cells[sheetRow, 1].Value = "(No systems found in the report)";
                ws.Cells[sheetRow, 1].Style.Font.Italic = true;
                ws.Cells[sheetRow, 1].Style.Font.Color.SetColor(Color.Gray);
            }

            // ── Column widths ─────────────────────────────────────────────────
            ws.Column(1).Width = 42;   // Description / System label
            ws.Column(2).Width = 14;   // Quantity
            ws.Column(3).Width = 10;   // Unit
            if (isPreCast)
            {
                ws.Column(4).Width = 14;   // Poz No
                ws.Column(5).Width = 24;   // Notes
                ws.Column(6).Width = 18;   // Material Volume
                ws.Column(7).Width = 18;   // External Volume
            }
            else
            {
                ws.Column(4).Width = 22;   // Total Depth
            }
        }

        // =====================================================================
        // Clash debug sheet
        // =====================================================================

        private static void WriteClashDebugSheet(ExcelPackage pkg, BoQReport report)
        {
            var ws = pkg.Workbook.Worksheets.Add("Clash_Debug");

            string[] hdr = { "System", "Pipe Section", "Overlap Partner", "Excav Deducted (m3)", "Backfill Deducted (m3)", "Detail" };
            WriteTitle(ws, "Trench Overlap — Clash Detection Log", hdr.Length, 1);

            const int hdrRow = 3;
            WriteHeaders(ws, hdrRow, hdr, hdr.Length);
            ws.View.FreezePanes(hdrRow + 1, 1);

            int  row = hdrRow + 1;
            bool alt = false;
            foreach (var sec in report.SectionDebug
                .Where(s => s.ClashLog != null && s.ClashLog.Count > 0))
            {
                foreach (string msg in sec.ClashLog)
                {
                    int s1 = msg.IndexOf('[') + 1;
                    int s2 = msg.IndexOf(']');
                    string partner = (s1 > 0 && s2 > s1)
                        ? msg.Substring(s1, s2 - s1) : "—";

                    ws.Cells[row, 1].Value = sec.SystemName;
                    ws.Cells[row, 2].Value = sec.PipeName;
                    ws.Cells[row, 3].Value = partner;
                    ws.Cells[row, 4].Value = sec.OverlapExcavDeducted;
                    ws.Cells[row, 5].Value = sec.OverlapBackfillDeducted;
                    ws.Cells[row, 6].Value = msg;

                    ApplyDataRowStyle(ws, row, hdr.Length, alt);
                    ws.Cells[row, 4].Style.Numberformat.Format = "#,##0.0000";
                    ws.Cells[row, 4].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                    ws.Cells[row, 5].Style.Numberformat.Format = "#,##0.0000";
                    ws.Cells[row, 5].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
                    alt = !alt;
                    row++;
                }
            }

            ws.Column(1).Width = 14;
            ws.Column(2).Width = 18;
            ws.Column(3).Width = 18;
            ws.Column(4).Width = 18;
            ws.Column(5).Width = 80;
        }

        // =====================================================================
        // Style primitives
        // =====================================================================

        internal static void WriteTitle(ExcelWorksheet ws, string title, int colSpan, int startRow)
        {
            var cell = ws.Cells[startRow, 1];
            cell.Value = title;
            cell.Style.Font.Bold = true;
            cell.Style.Font.Size = 13;
            cell.Style.Font.Color.SetColor(ThemeBlue);

            if (colSpan > 1)
            {
                ws.Cells[startRow, 1, startRow, colSpan].Merge = true;
                ws.Cells[startRow, 1].Style.HorizontalAlignment = ExcelHorizontalAlignment.Left;
            }

            ws.Row(startRow).Height     = 24;
            ws.Row(startRow + 1).Height = 6;
        }

        internal static void WriteHeaders(ExcelWorksheet ws, int hdrRow,
            string[] hdr, int colCount)
        {
            for (int c = 0; c < colCount; c++)
            {
                var cell = ws.Cells[hdrRow, c + 1];
                cell.Value = hdr[c];

                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(ThemeBlue);
                cell.Style.Font.Bold = true;
                cell.Style.Font.Color.SetColor(ThemeWhite);
                cell.Style.HorizontalAlignment = ExcelHorizontalAlignment.Center;
                cell.Style.VerticalAlignment   = ExcelVerticalAlignment.Center;
                cell.Style.WrapText = true;

                cell.Style.Border.Top.Style    = ExcelBorderStyle.Thin;
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                cell.Style.Border.Left.Style   = ExcelBorderStyle.Thin;
                cell.Style.Border.Right.Style  = ExcelBorderStyle.Thin;
                cell.Style.Border.Top.Color.SetColor(ThemeWhite);
                cell.Style.Border.Bottom.Color.SetColor(ThemeWhite);
                cell.Style.Border.Left.Color.SetColor(ThemeWhite);
                cell.Style.Border.Right.Color.SetColor(ThemeWhite);
            }
            ws.Row(hdrRow).Height = 30;
        }

        internal static void ApplyDataRowStyle(ExcelWorksheet ws, int row, int colCount, bool alt)
        {
            for (int c = 1; c <= colCount; c++)
            {
                var cell = ws.Cells[row, c];
                if (alt)
                {
                    cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                    cell.Style.Fill.BackgroundColor.SetColor(ThemeAltRow);
                }
                cell.Style.Border.Top.Style    = ExcelBorderStyle.Hair;
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Hair;
                cell.Style.Border.Left.Style   = ExcelBorderStyle.Hair;
                cell.Style.Border.Right.Style  = ExcelBorderStyle.Hair;
                cell.Style.Border.Top.Color.SetColor(ThemeBorder);
                cell.Style.Border.Bottom.Color.SetColor(ThemeBorder);
                cell.Style.Border.Left.Color.SetColor(ThemeBorder);
                cell.Style.Border.Right.Color.SetColor(ThemeBorder);
                cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            }
        }

        private static void ApplySubtotalRowStyle(ExcelWorksheet ws, int row, int colCount)
        {
            for (int c = 1; c <= colCount; c++)
            {
                var cell = ws.Cells[row, c];
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(ThemeSubtotal);
                cell.Style.Font.Bold = true;
                cell.Style.Font.Color.SetColor(ThemeWhite);
                cell.Style.Border.Top.Style    = ExcelBorderStyle.Thin;
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Thin;
                cell.Style.Border.Left.Style   = ExcelBorderStyle.Thin;
                cell.Style.Border.Right.Style  = ExcelBorderStyle.Thin;
                cell.Style.Border.Top.Color.SetColor(ThemeWhite);
                cell.Style.Border.Bottom.Color.SetColor(ThemeWhite);
                cell.Style.Border.Left.Color.SetColor(ThemeWhite);
                cell.Style.Border.Right.Color.SetColor(ThemeWhite);
                cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            }
        }

        private static void ApplyTotalRowStyle(ExcelWorksheet ws, int row, int colCount)
        {
            for (int c = 1; c <= colCount; c++)
            {
                var cell = ws.Cells[row, c];
                cell.Style.Fill.PatternType = ExcelFillStyle.Solid;
                cell.Style.Fill.BackgroundColor.SetColor(ThemeMidBlue);
                cell.Style.Font.Bold = true;
                cell.Style.Font.Color.SetColor(ThemeWhite);
                cell.Style.Border.Top.Style    = ExcelBorderStyle.Medium;
                cell.Style.Border.Bottom.Style = ExcelBorderStyle.Medium;
                cell.Style.Border.Left.Style   = ExcelBorderStyle.Medium;
                cell.Style.Border.Right.Style  = ExcelBorderStyle.Medium;
                cell.Style.Border.Top.Color.SetColor(ThemeWhite);
                cell.Style.Border.Bottom.Color.SetColor(ThemeWhite);
                cell.Style.Border.Left.Color.SetColor(ThemeWhite);
                cell.Style.Border.Right.Color.SetColor(ThemeWhite);
                cell.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
            }
        }

        internal static void SetNumericFormat(ExcelWorksheet ws, int row,
            int fromCol, int toCol, string fmt)
        {
            for (int c = fromCol; c <= toCol; c++)
            {
                ws.Cells[row, c].Style.Numberformat.Format = fmt;
                ws.Cells[row, c].Style.HorizontalAlignment = ExcelHorizontalAlignment.Right;
            }
        }

        private static void SetFixedWidths(ExcelWorksheet ws, int colCount,
            double firstColWidth, double dataColWidth)
        {
            ws.Column(1).Width = firstColWidth;
            for (int c = 2; c <= colCount; c++)
                ws.Column(c).Width = dataColWidth;
        }

        // =====================================================================
        // Save with lock detection
        // =====================================================================

        internal static void SavePackage(ExcelPackage pkg, string path)
        {
            try
            {
                pkg.SaveAs(new FileInfo(path));
            }
            catch (IOException ioEx)
            {
                MessageBox.Show(
                    "Could not save the Excel file:\n" + path + "\n\n" +
                    "The file may already be open in Excel. " +
                    "Please close it and run the export again.\n\n" +
                    "Details: " + ioEx.Message,
                    "Export Failed — File Locked",
                    MessageBoxButtons.OK,
                    MessageBoxIcon.Warning);
                throw;
            }
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        internal static string SanitizeSheetName(string name)
        {
            foreach (char ch in new[] { '\\', '/', '?', '*', '[', ']', ':' })
                name = name.Replace(ch, '_');
            return name;
        }

        internal static string Truncate(string s, int maxLen)
            => s.Length <= maxLen ? s : s.Substring(0, maxLen);
    }
}
