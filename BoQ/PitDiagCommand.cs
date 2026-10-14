using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Services;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.PitDiagCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// UT_BOQ_PIT_DIAG — READ-ONLY diagnostic of the pipe-inside-manhole-pit split.
    /// Loads the saved BoQ report, re-derives every pipe-in-pit volume with the exact
    /// geometry ManholeExcavOverlapService.Compute uses, and prints where
    /// T (trench removed from the pipe) ≠ F (backfill removed from the pipe) +
    /// S (bedding/surround removed from the manhole fill). Nothing is saved.
    /// Detail per pipe end: %TEMP%\UrbanoMetraj_PitDiag.csv
    /// </summary>
    public class PitDiagCommand
    {
        [CommandMethod("UT_BOQ_PIT_DIAG", CommandFlags.Modal)]
        public void Run()
        {
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage("\n[Pit Diag] Once UT_BOQ_HESAPLA calistirilmali.\n");
                return;
            }

            try
            {
                var (report, _) = DwgBoQStore.Load(doc.Database);
                if (report == null) { ed.WriteMessage("\n[Pit Diag] BoQ verisi okunamadi.\n"); return; }

                // Same values the window/Keşif use (in-memory only, never saved here).
                ManholeExcavOverlapService.Compute(report);
                var rows = ManholeExcavOverlapService.DiagnosePipeInPit(report);

                double sT    = rows.Sum(r => r.T),    sF    = rows.Sum(r => r.F);
                double sSK   = rows.Sum(r => r.BedK + r.SurK), sSD = rows.Sum(r => r.BedD + r.SurD);
                double resid = sT - sF - sSD;

                double c6 = sSK - sSD;

                double computeT = report.SectionDebug.Sum(s => s.ManholeExcavDeducted);
                double computeF = report.SectionDebug.Sum(s => s.ManholeBackfillDeducted);
                var mhs = report.Systems.SelectMany(s => s.Manholes).Where(m => m.ExcavationDepth > 1e-6).ToList();
                double kdBasis = mhs.Sum(m => m.ExcavationVolume - m.DolguBasisVolume);
                double struc   = mhs.Sum(m => m.StructureVolume);

                var sb = new StringBuilder();
                sb.Append("\n================ UT_BOQ_PIT_DIAG (salt okunur) ================");
                sb.Append($"\n  Boru ucu sayisi (baca icinde)      : {rows.Count}");
                sb.Append($"\n  T  boru kazisindan dusulen          : {sT,12:F3}   (Compute: {computeT:F3})");
                sb.Append($"\n  F  boru geri dolgusundan dusulen    : {sF,12:F3}   (Compute: {computeF:F3})");
                sb.Append($"\n  S  baca dolgusundan dusulen kum (D) : {sSD,12:F3}");
                sb.Append($"\n  FARK = T - F - S                    : {resid,12:F3}   (0 olmali)");
                sb.Append($"\n  Kum Kazi/Dolgu bazi farki           : {c6,12:F3}");
                sb.Append("\n  ---- EK BILGI ----");
                sb.Append($"\n  T bolge Simpson - 24-aralik Simpson   : {rows.Sum(r => r.T - r.TFine),12:F3}");
                sb.Append($"\n  Baca: Kazi hacmi - Dolgu bazi hacmi   : {kdBasis,12:F3}");
                sb.Append($"\n  Baca yapi hacmi (StructureVolume)     : {struc,12:F3}");

                string csv = Path.Combine(Path.GetTempPath(), "UrbanoMetraj_PitDiag.csv");
                WriteCsv(csv, rows);
                sb.Append($"\n  Detay: {csv}");
                sb.Append("\n================================================================\n");
                ed.WriteMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Pit Diag] Hata: {ex.Message}\n");
            }
        }

        private static void WriteCsv(string path, System.Collections.Generic.List<ManholeExcavOverlapService.PitDiagRow> rows)
        {
            var ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            string N(double v) => v.ToString("0.0000", ci);

            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep, "Sistem", "Baca", "Boru", "ZBacaTaban", "ZUst", "Akar",
                "BoruDisAlt", "HendekTabani", "KumUstu", "T", "F", "BedK", "SurK", "BedD", "SurD",
                "T_Ince", "FARK_T-F-S"));
            foreach (var r in rows)
                sb.AppendLine(string.Join(sep, r.System, r.Manhole, r.Pipe,
                    N(r.ZPitBottom), N(r.ZTop), N(r.Invert), N(r.OuterBottom), N(r.TrenchBottom), N(r.SandTop),
                    N(r.T), N(r.F), N(r.BedK), N(r.SurK), N(r.BedD), N(r.SurD),
                    N(r.TFine),
                    N(r.T - r.F - r.BedD - r.SurD)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }
    }
}
