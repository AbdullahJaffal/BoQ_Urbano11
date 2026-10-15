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

                // ── §5.2: pipe trench in two overlapping pits / beyond the pipe end ──
                var dp = ManholeExcavOverlapService.DiagnoseDoublePit(report);
                double bothS = dp.Sum(r => r.BothPitsFromStart), bothE = dp.Sum(r => r.BothPitsFromEnd);
                double overS = dp.Sum(r => r.OvershootStart),    overE = dp.Sum(r => r.OvershootEnd);
                sb.Append("\n  ---- IKI BACA / BORU UCU (KAZI_DOLGU_DOGRULAMA §5.2) ----");
                sb.Append($"\n  Iki bacanin kesisiminde kalan hendek  : {bothS,12:F3}  (bitis tarafindan {bothE:F3})");
                sb.Append("\n     -> borudan iki kez dusuluyor; kazi ve dolgu ayni miktarda eksik");
                sb.Append($"\n  Boru boyunu asan hendek (Reach 10 m)  : {overS + overE,12:F3}  (bas {overS:F3} / son {overE:F3})");
                sb.Append($"\n  Etkilenen boru sayisi                 : {dp.Count}");

                string csv = Path.Combine(Path.GetTempPath(), "UrbanoMetraj_PitDiag.csv");
                WriteCsv(csv, rows);
                string csv2 = Path.Combine(Path.GetTempPath(), "UrbanoMetraj_DoublePitDiag.csv");
                WriteDoublePitCsv(csv2, dp);
                sb.Append($"\n  Detay: {csv}");
                sb.Append($"\n         {csv2}");
                sb.Append("\n================================================================\n");
                ed.WriteMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Pit Diag] Hata: {ex.Message}\n");
            }
        }

        /// <summary>
        /// UT_BOQ_PIPE_DIAG — READ-ONLY. Per-pipe gross identity check (no clash, no
        /// manhole): Kazı − (Yataklama + Gömlekleme + Geri Dolgu) − pipe body, split by
        /// cause from the stored station polygons (KAZI_DOLGU_DOGRULAMA.md §5.3).
        /// Needs a HESAPLA from V249+ (gross Yataklama/Gömlekleme stored).
        /// </summary>
        [CommandMethod("UT_BOQ_PIPE_DIAG", CommandFlags.Modal)]
        public void RunPipe()
        {
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;
            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage("\n[Pipe Diag] Once UT_BOQ_HESAPLA calistirilmali.\n");
                return;
            }

            try
            {
                var (report, _) = DwgBoQStore.Load(doc.Database);
                if (report == null) { ed.WriteMessage("\n[Pipe Diag] BoQ verisi okunamadi.\n"); return; }

                var rows = PipeIdentityDiagService.Run(report);
                var v2   = rows.Where(r => r.HasV2).ToList();
                double R = v2.Sum(r => r.R);

                var sb = new StringBuilder();
                sb.Append("\n================ UT_BOQ_PIPE_DIAG (salt okunur) ================");
                sb.Append($"\n  Boru sayisi (V2 hacimli / toplam)     : {v2.Count} / {rows.Count}");
                sb.Append($"\n  Kazi (brut)                           : {v2.Sum(r => r.ExcavGross),12:F3}");
                sb.Append($"\n  Yataklama + Gomlekleme + Geri Dolgu   : {v2.Sum(r => r.BeddingGross + r.SurroundGross + r.BackfillGross),12:F3}");
                sb.Append($"\n  Boru govdesi (PipeArea x boy)         : {v2.Sum(r => r.PipeArea * r.Length2D),12:F3}");
                sb.Append($"\n  FARK R = Kazi - Dolgu - Govde         : {R,12:F3}   (0 olmali)");
                sb.Append("\n  ---- FARKIN DAGILIMI ----");
                sb.Append($"\n  1 Dolgu kotu != Kazi kotu             : {v2.Sum(r => r.ShapeDatum),12:F3}   ({v2.Sum(r => r.DatumStations)} istasyon)");
                sb.Append($"\n  2 Geri dolgu poligonu yok (dejenere)  : {v2.Sum(r => r.ShapeDegenerate),12:F3}   ({v2.Sum(r => r.DegenerateStations)} istasyon)");
                sb.Append($"\n  3 Diger kesit boslugu                 : {v2.Sum(r => r.ShapeOther),12:F3}");
                sb.Append($"\n  4 Gomlekleme 0'a kirpma (-)           : {-v2.Sum(r => r.Clamp),12:F3}");
                sb.Append($"\n  5 Istasyon yolu - boru boyu (govde)   : {v2.Sum(r => r.Path),12:F3}");
                sb.Append($"\n  6 Kalan (yuvarlama / bisektor entegr.) : {v2.Sum(r => r.Rest),12:F3}");
                sb.Append("\n  ---- EK BILGI ----");
                sb.Append($"\n  Bisektor bolgesinde dusulmeyen govde  : {v2.Sum(r => r.BisectorBody),12:F3}   ({v2.Sum(r => r.BisectorStations)} istasyon)");
                bool grossMissing = v2.Count > 0 && v2.All(r => r.BeddingGross == 0 && r.SurroundGross == 0);
                if (grossMissing)
                    sb.Append("\n  UYARI: brut Yataklama/Gomlekleme yok — V249+ ile HESAPLA yeniden calistirilmali.");
                sb.Append("\n  En buyuk |R| borular:");
                foreach (var r in v2.OrderByDescending(r => Math.Abs(r.R)).Take(10))
                    sb.Append($"\n    {r.System} {r.Pipe,-22} R={r.R,9:F3}  dolgu-kotu={r.ShapeDatum,8:F3}  dejenere={r.ShapeDegenerate,8:F3}  diger={r.ShapeOther,8:F3}  yol={r.Path,8:F3}  kalan={r.Rest,8:F3}");

                string csv = Path.Combine(Path.GetTempPath(), "UrbanoMetraj_PipeDiag.csv");
                WritePipeCsv(csv, rows);
                sb.Append($"\n  Detay: {csv}");
                sb.Append("\n================================================================\n");
                ed.WriteMessage(sb.ToString());
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Pipe Diag] Hata: {ex.Message}\n");
            }
        }

        private static void WriteDoublePitCsv(string path, System.Collections.Generic.List<ManholeExcavOverlapService.DoublePitRow> rows)
        {
            var ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            string N(double v) => v.ToString("0.0000", ci);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep, "Sistem", "Boru", "BasBaca", "SonBaca", "Boy",
                "IkiBaca_BastanKesit", "IkiBaca_SondanKesit", "Tasma_Bas", "Tasma_Son"));
            foreach (var r in rows)
                sb.AppendLine(string.Join(sep, r.System, r.Pipe, r.MhStart, r.MhEnd, N(r.Length),
                    N(r.BothPitsFromStart), N(r.BothPitsFromEnd), N(r.OvershootStart), N(r.OvershootEnd)));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
        }

        private static void WritePipeCsv(string path, System.Collections.Generic.List<PipeIdentityDiagService.Row> rows)
        {
            var ci = CultureInfo.CurrentCulture;
            string sep = ci.TextInfo.ListSeparator;
            string N(double v) => v.ToString("0.0000", ci);
            var sb = new StringBuilder();
            sb.AppendLine(string.Join(sep, "Sistem", "Boru", "V2", "Boy", "PipeArea",
                "KaziBrut", "YataklamaBrut", "GomleklemeBrut", "GeriDolguBrut", "R",
                "DolguKotu", "Dejenere", "Diger", "Kirpma", "Yol", "Kalan", "BisektorGovde",
                "DolguKotuIst", "DejenereIst", "BisektorIst"));
            foreach (var r in rows)
                sb.AppendLine(string.Join(sep, r.System, r.Pipe, r.HasV2 ? "1" : "0", N(r.Length2D), N(r.PipeArea),
                    N(r.ExcavGross), N(r.BeddingGross), N(r.SurroundGross), N(r.BackfillGross), N(r.R),
                    N(r.ShapeDatum), N(r.ShapeDegenerate), N(r.ShapeOther), N(r.Clamp), N(r.Path), N(r.Rest),
                    N(r.BisectorBody), r.DatumStations, r.DegenerateStations, r.BisectorStations));
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(true));
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
