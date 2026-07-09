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
    /// Header rows: row 3 is the user's chosen settings.Language, row 4 is
    /// ALWAYS Russian in addition (per explicit user request, using real
    /// Cyrillic — the ASCII-transliteration convention used elsewhere in this
    /// module does not apply to this dedicated Russian row) — data starts at
    /// row 5. Dynamic column headers (sub-base piece dimensions, stacked part
    /// name+height) are catalog-derived literal text, so they're shown
    /// identically on both header rows rather than "translated".
    ///
    /// Scope locked for v1 (see project memory "Baca kesif gap analysis"):
    ///  - Excavation figures are the existing ISOLATED ManholeItem.ExcavationDepth/
    ///    ExcavationVolume (no trench-overlap deduction / crushed-stone-vs-soil split)
    ///    EXCEPT the two dedicated overlap columns (Kazi-Boru Kesisimi / Geri Dolgu
    ///    Kesisim Dusumu) which DO read ManholeExcavOverlapService's real output.
    ///  - Component breakdown is a flexible text list of ManholeItem.StackPreCast.Parts
    ///    (Taban/Govde/Konik/Kapak/Boyun Bilezigi have no persisted Role tag — shown
    ///    as-is rather than guessed at).
    ///  - Requires ManholeExcavOverlapService.Compute(report) and
    ///    ManholeConnectionLinkService.Populate(report) to have been (re-)run first
    ///    (both runtime-only — see BacaKesifTablosuCommand), and
    ///    PipeNetLengthService.Compute(report) for ManholeConnectionInfo.NetLength.
    ///
    /// Dynamic column blocks (computed per sheet, inserted right before the fixed
    /// "Excav. Depth" column):
    ///  - Cross-system-inlet columns (3): only added AT ALL when at least one
    ///    manhole in this sheet has an inlet from a different system — entirely
    ///    absent otherwise (per explicit user request), not just left blank.
    ///  - Sub-base-piece columns: one per distinct Boy×En×EffectiveHeight dimension
    ///    found among this sheet's manholes' ResolvedSubBaseParts.
    ///  - Fixed-height stacked-part columns: one per distinct (PartName, HeightM)
    ///    among StackPreCast.Parts where IsVariableRing == false.
    ///  - Variable-height stacked-part columns: one per distinct PartName among
    ///    parts where IsVariableRing == true — grouped by name ONLY (not height,
    ///    since the greedy-fill height differs per manhole); the cell shows the
    ///    actual height (m) that manhole needed, not a count.
    /// </summary>
    public static class ManholeKesifExportService
    {
        // Fixed leading columns 1-13 (No .. Ground Elev - Arazi Kot).
        private static readonly Dictionary<ExportLanguage, string[]> HeaderPrefixMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "No", "Manhole", "Inlet", "Outlet",
                    "Distance (m)", "Net Pipe Length (m)", "Manhole Diameter (mm)",
                    "Pipe Diameter (mm)", "Design Elev - Kirmizi Kot (m)",
                    "Invert Elev (m) Outlet", "Manhole Depth (m)", "Invert Elev (m) Inlet",
                    "Ground Elev - Arazi Kot (m)"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Sira No", "Baca Adi", "Giris", "Cikis",
                    "Mesafe (m)", "Boru Net Uzunluk (m)", "Baca Capi (mm)",
                    "Boru Capi (mm)", "Kirmizi Kot (m)",
                    "Invert Kotu (m) Cikis", "Baca Derinligi (m)", "Invert Kotu (m) Giris",
                    "Arazi Kot (m)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "№", "Колодец", "Вход", "Выход",
                    "Расстояние (м)", "Чистая длина трубы (м)", "Диаметр колодца (мм)",
                    "Диаметр трубы (мм)", "Красная отметка (м)",
                    "Отметка инверта (м) Выход", "Глубина колодца (м)", "Отметка инверта (м) Вход",
                    "Отметка поверхности (м)"
                }
            };

        // Cross-system-inlet block (3 columns) — only included when at least one
        // manhole in the sheet actually has a cross-system inlet.
        private static readonly Dictionary<ExportLanguage, string[]> CrossSystemHeaderMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[]
                {
                    "Inlet from another system", "Pipe Diameter (mm) for Inlet from another system",
                    "Invert Elev (m) for Inlet from another system"
                },
                [ExportLanguage.Turkish] = new[]
                {
                    "Baska Sebekeden Giris", "Baska Sebekeden Giris Boru Capi (mm)",
                    "Baska Sebekeden Giris Invert Kotu (m)"
                },
                [ExportLanguage.Russian] = new[]
                {
                    "Вход из другой сети", "Диаметр трубы (мм) для входа из другой сети",
                    "Отметка инверта (м) для входа из другой сети"
                }
            };

        // Fixed trailing columns, after every dynamic/computed block.
        private static readonly Dictionary<ExportLanguage, string[]> HeaderSuffixMap =
            new Dictionary<ExportLanguage, string[]>
            {
                [ExportLanguage.English] = new[] { "Excav. Depth (m)", "Excav. Volume (m3)", "Component Breakdown" },
                [ExportLanguage.Turkish] = new[] { "Kazi Derinligi (m)", "Kazi Hacmi (m3)", "Parca Dokumu" },
                [ExportLanguage.Russian] = new[] { "Глубина выкопки (м)", "Объём выкопки (м3)", "Детали колодца" }
            };

        // Single fixed column right after the dynamic sub-base-piece block — reads
        // ManholeItem.SubBaseVolume directly (Alt Temel Katmanları frustum volume
        // at the pit bottom, computed by ManholeExcavOverlapService.Compute). No
        // calculation happens in this export service for this column.
        private static readonly Dictionary<ExportLanguage, string> YataklamaHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Bedding (Yataklama) Volume (m3)",
                [ExportLanguage.Turkish] = "Yataklama Hacmi (m3)",
                [ExportLanguage.Russian] = "Объём подстилки (м3)"
            };

        // Single fixed column right after the dynamic stacked-part blocks — Sum of
        // (StackedPart.UnitMaterialVolume x Count) across this manhole's whole
        // stack, mirroring ManholeItem.StructureVolume's own pattern (which sums
        // UnitExternalVolume x Count instead) — same existing catalog values,
        // just the material-volume sibling total, computed per manhole.
        private static readonly Dictionary<ExportLanguage, string> TotalMaterialVolumeHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Total Material Volume (m3)",
                [ExportLanguage.Turkish] = "Toplam Malzeme Hacmi (m3)",
                [ExportLanguage.Russian] = "Общий объём материала (м3)"
            };

        // Reads ManholeItem.BackfillVolume directly (final Geri Dolgu, already net
        // of structure/sub-base/overlap deductions).
        private static readonly Dictionary<ExportLanguage, string> TotalBackfillHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Total Backfill (Geri Dolgu) (m3)",
                [ExportLanguage.Turkish] = "Toplam Geri Dolgu (m3)",
                [ExportLanguage.Russian] = "Общая обратная засыпка (м3)"
            };

        // Reads ManholeItem.ExcavBaseSideM directly (pit square side at the bottom —
        // unchanged by slope, since slope widening starts from zBottom).
        private static readonly Dictionary<ExportLanguage, string> PitBottomHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Pit Dimension - Bottom (m)",
                [ExportLanguage.Turkish] = "Kazi Cukuru Alt Boyutu (m)",
                [ExportLanguage.Russian] = "Размер котлована - низ (м)"
            };

        // ExcavBaseSideM + 2 x ExcavationDepth x ExcavSlopeRatio — the SAME pit-side-
        // at-elevation formula already used throughout ManholeExcavOverlapService
        // (e.g. ManholeSquareAt's halfSide = baseSideM/2 + rise*slopeRatio, applied
        // here at rise = full ExcavationDepth to reach the top of the pit).
        private static readonly Dictionary<ExportLanguage, string> PitTopHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Pit Dimension - Top (m)",
                [ExportLanguage.Turkish] = "Kazi Cukuru Ust Boyutu (m)",
                [ExportLanguage.Russian] = "Размер котлована - верх (м)"
            };

        // Reads ManholeItem.TotalPipeExcavOverlap directly (sum of
        // SectionDebugRow.ManholeExcavDeducted across every connected pipe,
        // accumulated by ManholeConnectionLinkService.Populate).
        private static readonly Dictionary<ExportLanguage, string> PipeOverlapHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Excavation-Pipe Overlap (m3)",
                [ExportLanguage.Turkish] = "Kazi-Boru Kesisimi (m3)",
                [ExportLanguage.Russian] = "Пересечение выкопки с трубами (м3)"
            };

        // Derived (subtraction of already-computed fields only, no new geometry):
        // Max(0, DolguBasisVolume - StructureVolume - SubBaseVolume - BackfillVolume)
        // — exactly reverses ManholeExcavOverlapService's own BackfillVolume formula
        // to recover how much of it was deducted due to bedding/surround overlap
        // with connected pipe trenches (Dolgu-basis range).
        private static readonly Dictionary<ExportLanguage, string> BackfillOverlapDeductedHeaderMap =
            new Dictionary<ExportLanguage, string>
            {
                [ExportLanguage.English] = "Backfill Deducted for Overlap (m3)",
                [ExportLanguage.Turkish] = "Geri Dolgudan Kesisim Icin Dusulen (m3)",
                [ExportLanguage.Russian] = "Вычет из засыпки за пересечение (м3)"
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
            // Cross-system-inlet block is entirely omitted (not just left blank)
            // when no manhole in this sheet actually has one.
            bool hasCrossSystem = sys.Manholes.Any(m => m.Inlets.Any(
                c => !string.Equals(c.SystemName, sys.SystemName, StringComparison.Ordinal)));

            // One column per distinct sub-base-piece dimension (Boy x En x
            // EffectiveHeight) found anywhere in this sheet — regardless of which
            // Taban/diameter it belongs to, per user's explicit criterion ("only the
            // piece's own dimensions matter"). First-appearance order.
            var subBaseGroups = new List<(double Boy, double En, double Kalinlik)>();
            foreach (var m in sys.Manholes)
                foreach (var p in m.ResolvedSubBaseParts ?? new List<TemelAltiParcaComponent>())
                {
                    var key = (p.Boy, p.En, p.EffectiveHeight);
                    if (!subBaseGroups.Contains(key)) subBaseGroups.Add(key);
                }

            // Fixed-height stacked parts: one column per distinct (PartName, HeightM),
            // e.g. "Taban 0.80m", "Govde 0.60m", "Konik 0.70m". Variable-height parts
            // (IsVariableRing) are EXCLUDED here — they get their own block below,
            // grouped by name only, since their height differs per manhole.
            var fixedStackGroups = new List<(string PartName, double HeightM)>();
            var variableStackNames = new List<string>();
            foreach (var m in sys.Manholes)
                foreach (var p in m.StackPreCast?.Parts ?? new List<StackedPart>())
                {
                    if (p.IsVariableRing)
                    {
                        if (!variableStackNames.Contains(p.PartName)) variableStackNames.Add(p.PartName);
                    }
                    else
                    {
                        var key = (p.PartName, p.HeightM);
                        if (!fixedStackGroups.Contains(key)) fixedStackGroups.Add(key);
                    }
                }

            string[] subBaseHeaders = subBaseGroups
                .Select(g => $"{g.Boy:0}x{g.En:0}x{g.Kalinlik:0} mm")
                .ToArray();
            string[] fixedStackHeaders = fixedStackGroups
                .Select(g => $"{g.PartName} {g.HeightM:0.00}m")
                .ToArray();
            string[] variableStackHeaders = variableStackNames
                .Select(name => $"{name} (Degisken, m)")
                .ToArray();

            string[] hdrPrimary = BuildHeaderRow(language, hasCrossSystem, subBaseHeaders, fixedStackHeaders, variableStackHeaders);
            string[] hdrRussian = BuildHeaderRow(ExportLanguage.Russian, hasCrossSystem, subBaseHeaders, fixedStackHeaders, variableStackHeaders);

            int prefixLen          = HeaderPrefixMap[language].Length;    // 13
            int crossSystemStart   = prefixLen + 1;                       // 14
            int subBaseStart       = crossSystemStart + (hasCrossSystem ? 3 : 0);
            int yataklamaCol       = subBaseStart + subBaseGroups.Count;
            int fixedStackStart    = yataklamaCol + 1;
            int variableStackStart = fixedStackStart + fixedStackGroups.Count;
            int totalMatVolCol     = variableStackStart + variableStackNames.Count;
            int totalBackfillCol   = totalMatVolCol + 1;
            int pitBottomCol       = totalBackfillCol + 1;
            int pitTopCol          = pitBottomCol + 1;
            int pipeOverlapCol     = pitTopCol + 1;
            int backfillOverlapCol = pipeOverlapCol + 1;
            int excavDepthCol      = backfillOverlapCol + 1;
            int excavVolCol        = excavDepthCol + 1;
            int partsCol           = excavVolCol + 1;
            int colCount           = partsCol;

            var manholeLevelColumns = new List<int> { 2, 7, 9, 11, 13 };
            for (int c = subBaseStart; c < subBaseStart + subBaseGroups.Count; c++) manholeLevelColumns.Add(c);
            manholeLevelColumns.Add(yataklamaCol);
            for (int c = fixedStackStart; c < fixedStackStart + fixedStackGroups.Count; c++) manholeLevelColumns.Add(c);
            for (int c = variableStackStart; c < variableStackStart + variableStackNames.Count; c++) manholeLevelColumns.Add(c);
            manholeLevelColumns.Add(totalMatVolCol);
            manholeLevelColumns.Add(totalBackfillCol);
            manholeLevelColumns.Add(pitBottomCol);
            manholeLevelColumns.Add(pitTopCol);
            manholeLevelColumns.Add(pipeOverlapCol);
            manholeLevelColumns.Add(backfillOverlapCol);
            manholeLevelColumns.Add(excavDepthCol);
            manholeLevelColumns.Add(excavVolCol);
            manholeLevelColumns.Add(partsCol);

            string safe      = ExcelExportService.SanitizeSheetName(sys.SystemName);
            string sheetName = ExcelExportService.Truncate(safe + "_BacaKesif", 31);
            var    ws        = pkg.Workbook.Worksheets.Add(sheetName);

            ExcelExportService.WriteTitle(ws, sys.SystemName + " - Baca Kesif Tablosu", colCount, 1);

            const int hdrRow    = 3;   // primary language
            const int hdrRowRu  = 4;   // always Russian, in addition
            ExcelExportService.WriteHeaders(ws, hdrRow,   hdrPrimary, colCount);
            ExcelExportService.WriteHeaders(ws, hdrRowRu, hdrRussian, colCount);
            ws.View.FreezePanes(hdrRowRu + 1, 1);

            int  row = hdrRowRu + 1;
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
                // row — they're packed into the cross-system columns of the manhole's
                // existing rows (name/diameter/invert only), and only spill into extra
                // rows if there are more of them than existing local-connection rows.
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

                    if (hasCrossSystem && i < crossInlets.Count)
                    {
                        var cc = crossInlets[i];
                        ws.Cells[row, crossSystemStart].Value     = cc.NeighborNodeName;
                        ws.Cells[row, crossSystemStart + 1].Value = cc.DiameterMm;
                        ws.Cells[row, crossSystemStart + 2].Value = cc.InvertElevation;
                    }

                    ExcelExportService.ApplyDataRowStyle(ws, row, colCount, alt);
                    ExcelExportService.SetNumericFormat(ws, row, 5, 6, "#,##0.00");
                    ExcelExportService.SetNumericFormat(ws, row, 8, 8, "#,##0");
                    ExcelExportService.SetNumericFormat(ws, row, 10, 10, "#,##0.000");
                    ExcelExportService.SetNumericFormat(ws, row, 12, 12, "#,##0.000");
                    if (hasCrossSystem)
                    {
                        ExcelExportService.SetNumericFormat(ws, row, crossSystemStart + 1, crossSystemStart + 1, "#,##0");
                        ExcelExportService.SetNumericFormat(ws, row, crossSystemStart + 2, crossSystemStart + 2, "#,##0.000");
                    }
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
                var mSubParts = m.ResolvedSubBaseParts ?? new List<TemelAltiParcaComponent>();
                for (int gi = 0; gi < subBaseGroups.Count; gi++)
                {
                    var g = subBaseGroups[gi];
                    int count = mSubParts.Count(p => p.Boy == g.Boy && p.En == g.En && p.EffectiveHeight == g.Kalinlik);
                    ws.Cells[groupStart, subBaseStart + gi].Value = count > 0 ? (int?)count : null;
                }

                ws.Cells[groupStart, yataklamaCol].Value  = m.SubBaseVolume > 0 ? (double?)m.SubBaseVolume : null;

                var mParts = m.StackPreCast?.Parts ?? new List<StackedPart>();

                // One count per distinct FIXED-height stacked-part — blank when this
                // manhole's stack doesn't use that exact part, the count
                // (StackedPart.Count, e.g. "Govde x6") when it does.
                for (int gi = 0; gi < fixedStackGroups.Count; gi++)
                {
                    var g = fixedStackGroups[gi];
                    int count = mParts.Where(p => !p.IsVariableRing && p.PartName == g.PartName && p.HeightM == g.HeightM)
                                      .Sum(p => p.Count);
                    ws.Cells[groupStart, fixedStackStart + gi].Value = count > 0 ? (int?)count : null;
                }

                // One column per distinct VARIABLE-height part name — the cell shows
                // the actual height (m) this manhole's greedy-fill needed for that
                // part, not a count (a manhole either needs one such ring or it
                // doesn't; height is what varies, not quantity).
                for (int gi = 0; gi < variableStackNames.Count; gi++)
                {
                    string name = variableStackNames[gi];
                    var match = mParts.FirstOrDefault(p => p.IsVariableRing && p.PartName == name);
                    ws.Cells[groupStart, variableStackStart + gi].Value = match != null ? (double?)match.HeightM : null;
                }

                double totalMaterialVol = mParts.Sum(p => p.UnitMaterialVolume * p.Count);
                ws.Cells[groupStart, totalMatVolCol].Value = totalMaterialVol > 0 ? (double?)totalMaterialVol : null;

                // Toplam Geri Dolgu — final backfill volume, already net of
                // structure/sub-base/overlap deductions (ManholeExcavOverlapService).
                ws.Cells[groupStart, totalBackfillCol].Value = m.BackfillVolume > 0 ? (double?)m.BackfillVolume : null;

                // Pit dimensions: bottom = ExcavBaseSideM as-is; top = same value
                // widened by slope over the full excavation depth (the identical
                // formula ManholeSquareAt/ManholeExcavOverlapService already use
                // internally for every Z-slice, just evaluated at the top).
                if (m.ExcavBaseSideM > 0)
                {
                    ws.Cells[groupStart, pitBottomCol].Value = m.ExcavBaseSideM;
                    double pitTop = m.ExcavBaseSideM + 2.0 * m.ExcavationDepth * m.ExcavSlopeRatio;
                    ws.Cells[groupStart, pitTopCol].Value = pitTop;
                }

                // Kazi-Boru Kesisimi — total excavation/pipe-trench overlap volume,
                // summed across every connected pipe by ManholeConnectionLinkService.
                ws.Cells[groupStart, pipeOverlapCol].Value = m.TotalPipeExcavOverlap > 0 ? (double?)m.TotalPipeExcavOverlap : null;

                // Geri Dolgudan Kesisim Icin Dusulen — derived by reversing
                // ManholeExcavOverlapService's own BackfillVolume formula:
                //   BackfillVolume = Max(0, DolguBasisVolume - StructureVolume - SubBaseVolume - deducted)
                // so deducted = DolguBasisVolume - StructureVolume - SubBaseVolume - BackfillVolume
                // (only existing persisted fields combined — no new geometry here).
                double backfillDeducted = Math.Max(0,
                    m.DolguBasisVolume - m.StructureVolume - m.SubBaseVolume - m.BackfillVolume);
                ws.Cells[groupStart, backfillOverlapCol].Value = backfillDeducted > 0 ? (double?)backfillDeducted : null;

                ws.Cells[groupStart, excavDepthCol].Value = m.ExcavationDepth;
                ws.Cells[groupStart, excavVolCol].Value   = m.ExcavationVolume;
                ws.Cells[groupStart, partsCol].Value      = DescribeParts(m);

                ExcelExportService.SetNumericFormat(ws, groupStart, 9, 9,   "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, 11, 11, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, 13, 13, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, yataklamaCol, yataklamaCol, "#,##0.000");
                for (int c = variableStackStart; c < variableStackStart + variableStackNames.Count; c++)
                    ExcelExportService.SetNumericFormat(ws, groupStart, c, c, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, totalMatVolCol, totalMatVolCol, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, totalBackfillCol, backfillOverlapCol, "#,##0.000");
                ExcelExportService.SetNumericFormat(ws, groupStart, excavDepthCol, excavVolCol, "#,##0.000");

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
            if (hasCrossSystem)
            {
                ws.Column(crossSystemStart).Width     = 16;
                ws.Column(crossSystemStart + 1).Width = 20;
                ws.Column(crossSystemStart + 2).Width = 20;
            }
            for (int c = subBaseStart; c < subBaseStart + subBaseGroups.Count; c++)
                ws.Column(c).Width = 16;
            ws.Column(yataklamaCol).Width  = 20;
            for (int c = fixedStackStart; c < fixedStackStart + fixedStackGroups.Count; c++)
                ws.Column(c).Width = 16;
            for (int c = variableStackStart; c < variableStackStart + variableStackNames.Count; c++)
                ws.Column(c).Width = 18;
            ws.Column(totalMatVolCol).Width     = 20;
            ws.Column(totalBackfillCol).Width   = 20;
            ws.Column(pitBottomCol).Width       = 18;
            ws.Column(pitTopCol).Width          = 18;
            ws.Column(pipeOverlapCol).Width     = 20;
            ws.Column(backfillOverlapCol).Width = 22;
            ws.Column(excavDepthCol).Width = 16;
            ws.Column(excavVolCol).Width   = 16;
            ws.Column(partsCol).Width      = 42;
        }

        private static string[] BuildHeaderRow(
            ExportLanguage language, bool hasCrossSystem,
            string[] subBaseHeaders, string[] fixedStackHeaders, string[] variableStackHeaders)
        {
            string[] prefix      = HeaderPrefixMap[language];
            string[] crossSystem = hasCrossSystem ? CrossSystemHeaderMap[language] : Array.Empty<string>();
            string[] suffix      = HeaderSuffixMap[language];
            return prefix
                .Concat(crossSystem)
                .Concat(subBaseHeaders)
                .Concat(new[] { YataklamaHeaderMap[language] })
                .Concat(fixedStackHeaders)
                .Concat(variableStackHeaders)
                .Concat(new[] { TotalMaterialVolumeHeaderMap[language] })
                .Concat(new[] { TotalBackfillHeaderMap[language] })
                .Concat(new[] { PitBottomHeaderMap[language] })
                .Concat(new[] { PitTopHeaderMap[language] })
                .Concat(new[] { PipeOverlapHeaderMap[language] })
                .Concat(new[] { BackfillOverlapDeductedHeaderMap[language] })
                .Concat(suffix)
                .ToArray();
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
