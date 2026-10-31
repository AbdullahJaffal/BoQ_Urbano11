using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using OfficeOpenXml;
using OfficeOpenXml.Style;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// Section area table (UT_CROSS_SECTIONS → "Excel'e aktar"): one row per drawn section with
    /// its levels, dimensions and the AREAS measured on the drawn regions (decision 3 — no
    /// volumes), plus a long-format sheet of the catalog sub-layer areas.
    /// EPPlus 4.5.3.3, fixed column widths only (AutoFit crashes GDI+ inside AutoCAD).
    /// </summary>
    public static class SectionExcelExport
    {
        private static readonly Color HeaderFill = Color.FromArgb(0x00, 0x46, 0x7F);

        public static void Write(string path, IList<SectionCut> cuts, SectionStyle style, string title)
        {
            using (var pkg = new ExcelPackage())
            {
                var ws = pkg.Workbook.Worksheets.Add("Kesitler");
                var cols = new List<(string Header, double Width, string Format, Func<SectionCut, object> Value)>
                {
                    ("Ağ",              8,  null,      c => c.SystemName),
                    ("Kol",            10,  null,      c => c.KolName ?? ""),
                    ("Boru",           18,  null,      c => SectionLayoutEngine.Sanitize(c.PipeName)),
                    ("Çap (mm)",        8,  "0",       c => c.DiameterMm),
                    ("Yerel Km",       11,  null,      c => SectionLayoutEngine.FormatChainage(c.LocalChainage, style.ChainageFormat)),
                    ("Yerel (m)",       9,  "0.00",    c => c.LocalChainage),
                    ("Genel Km",       11,  null,      c => double.IsNaN(c.GlobalChainage) ? "" : SectionLayoutEngine.FormatChainage(c.GlobalChainage, style.ChainageFormat)),
                    ("Kesit türü",     16,  null,      c => KindLabel(c)),
                    ("Kazı senaryosu", 18,  null,      c => SectionLayoutEngine.ScenarioLabel(c.ExcavScenario)),
                    ("Dolgu senaryosu",18,  null,      c => SectionLayoutEngine.ScenarioLabel(c.BackfillScenario)),
                    ("Kazı Kotu",      10,  "0.000",   c => c.KaziGroundZ),
                    ("Dolgu Kotu",     10,  "0.000",   c => c.DolguInvalid ? (object)"geçersiz" : c.DolguGroundZ),
                    ("Akar Kotu",      10,  "0.000",   c => c.InvertZ),
                    ("Kazı Tabanı",    10,  "0.000",   c => c.TrenchBottomZ),
                    ("Derinlik (m)",   10,  "0.000",   c => c.TrueDepth),
                    ("Taban Gen. (m)", 11,  "0.000",   c => c.BottomWidth),
                    ("Üst Gen. (m)",   11,  "0.000",   c => c.TopWidth),
                    ("Şev (1:x)",       9,  "0.##",    c => c.SlopeX),
                    ("Kazı net (m²)",       12, "0.000", c => c.AreaKaziNet),
                    ("Kazı brüt (m²)",      12, "0.000", c => c.AreaKaziGross),
                    ("Yataklama net (m²)",  12, "0.000", c => c.AreaYataklamaNet),
                    ("Yataklama brüt (m²)", 12, "0.000", c => c.AreaYataklamaGross),
                    ("Gömlekleme net (m²)", 12, "0.000", c => c.AreaGomleklemeNet),
                    ("Gömlekleme brüt (m²)",12, "0.000", c => c.AreaGomleklemeGross),
                    ("Geri Dolgu net (m²)", 12, "0.000", c => c.AreaGeriDolguNet),
                    ("Geri Dolgu brüt (m²)",12, "0.000", c => c.AreaGeriDolguGross),
                    ("Kesişen borular",     30, null,    c => string.Join(", ", c.Clashes.GroupBy(k => k.OtherPipeIndex).Select(g => g.First())
                                                                .Select(k => SectionLayoutEngine.Sanitize(k.OtherPipeName) + " [" + k.OtherSystemName + "]"))),
                };

                ws.Cells[1, 1].Value = title;
                ws.Cells[1, 1].Style.Font.Bold = true;
                ws.Cells[1, 1].Style.Font.Size = 13;
                ws.Cells[2, 1].Value = "Alanlar çizilen kesit bölgelerinden ölçülmüştür (m²). Hacimler için Metraj tablolarını kullanın.";
                ws.Cells[2, 1].Style.Font.Italic = true;
                const int head = 4;
                for (int k = 0; k < cols.Count; k++)
                {
                    var h = ws.Cells[head, k + 1];
                    h.Value = cols[k].Header;
                    StyleHeader(h);
                    ws.Column(k + 1).Width = cols[k].Width;
                }
                ws.Row(head).Height = 30;
                for (int i = 0; i < cuts.Count; i++)
                    for (int k = 0; k < cols.Count; k++)
                    {
                        var cell = ws.Cells[head + 1 + i, k + 1];
                        cell.Value = cols[k].Value(cuts[i]);
                        if (cols[k].Format != null && !(cell.Value is string)) cell.Style.Numberformat.Format = cols[k].Format;
                    }
                ws.View.FreezePanes(head + 1, 4);
                if (cuts.Count > 0) ws.Cells[head, 1, head + cuts.Count, cols.Count].AutoFilter = true;

                // Sub-layer areas (long format).
                var wb = pkg.Workbook.Worksheets.Add("Alt Katmanlar");
                string[] sh = { "Ağ", "Boru", "Yerel (m)", "Kazı senaryosu", "Dolgu senaryosu", "Grup", "Alt katman", "Malzeme", "Alt kot", "Üst kot", "Alan (m²)" };
                double[] sw = { 8, 18, 9, 18, 18, 14, 22, 16, 10, 10, 11 };
                for (int k = 0; k < sh.Length; k++)
                {
                    StyleHeader(wb.Cells[1, k + 1]);
                    wb.Cells[1, k + 1].Value = sh[k];
                    wb.Column(k + 1).Width = sw[k];
                }
                int r = 2;
                foreach (var c in cuts)
                    foreach (var b in c.SubBands.OrderBy(b => b.ZLow))
                    {
                        wb.Cells[r, 1].Value = c.SystemName;
                        wb.Cells[r, 2].Value = SectionLayoutEngine.Sanitize(c.PipeName);
                        wb.Cells[r, 3].Value = c.LocalChainage;  wb.Cells[r, 3].Style.Numberformat.Format = "0.00";
                        wb.Cells[r, 4].Value = SectionLayoutEngine.ScenarioLabel(c.ExcavScenario);
                        wb.Cells[r, 5].Value = SectionLayoutEngine.ScenarioLabel(c.BackfillScenario);
                        wb.Cells[r, 6].Value = GroupLabel(b.Group);
                        wb.Cells[r, 7].Value = SectionLayoutEngine.Sanitize(b.LayerName);
                        wb.Cells[r, 8].Value = b.MaterialType ?? "";
                        wb.Cells[r, 9].Value = b.ZLow;   wb.Cells[r, 9].Style.Numberformat.Format = "0.000";
                        wb.Cells[r, 10].Value = b.ZHigh; wb.Cells[r, 10].Style.Numberformat.Format = "0.000";
                        wb.Cells[r, 11].Value = b.Area;  wb.Cells[r, 11].Style.Numberformat.Format = "0.000";
                        r++;
                    }
                wb.View.FreezePanes(2, 1);

                pkg.SaveAs(new FileInfo(path));
            }
        }

        private static void StyleHeader(ExcelRange h)
        {
            h.Style.Font.Bold = true;
            h.Style.Font.Color.SetColor(Color.White);
            h.Style.Fill.PatternType = ExcelFillStyle.Solid;
            h.Style.Fill.BackgroundColor.SetColor(HeaderFill);
            h.Style.WrapText = true;
            h.Style.VerticalAlignment = ExcelVerticalAlignment.Center;
        }

        private static string KindLabel(SectionCut c)
        {
            string k = c.Kind == SectionStationKind.EngineStation ? "Hesap istasyonu"
                     : c.Kind == SectionStationKind.GrossAtChainage ? "Çakışmasız (brüt)"
                     : "En yakın istasyon";
            if (c.IsPipeStart) k += " · başlangıç";
            else if (c.IsPipeEnd) k += " · bitiş";
            if (c.IsZoneBoundary) k += " · bölge sınırı";
            return k;
        }

        private static string GroupLabel(UrbanoMetraj.BoQ.Models.TrenchStackGroup g)
        {
            switch (g)
            {
                case UrbanoMetraj.BoQ.Models.TrenchStackGroup.Yataklama:  return "Yataklama";
                case UrbanoMetraj.BoQ.Models.TrenchStackGroup.BoruEtrafi: return "Boru etrafı";
                case UrbanoMetraj.BoQ.Models.TrenchStackGroup.BoruUstu:   return "Boru üstü";
                default:                                                   return "Geri Dolgu";
            }
        }
    }
}
