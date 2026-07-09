using System;
using System.IO;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;

using Exception = System.Exception;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;
using SaveFileDialog = System.Windows.Forms.SaveFileDialog;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.MetrajKesifTablosuCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// UT_METRAJ_KESIF_TABLOSU — generates the paged "Metraj Keşif Tablosu" workbook
    /// (project-info sheet + one sheet per active network) from a BoQReport already
    /// computed and saved by UT_BOQ_HESAPLA.
    ///
    /// Like UT_BACA_KESIF_TABLOSU, this does NOT talk to Urbano (no dialog automation,
    /// no STA thread) — it only reads what's persisted in the DWG's NOD, so it can be
    /// run any time after UT_BOQ_HESAPLA. The report's Systems are already scoped
    /// to the Ağ Seçimi "Aktif" networks, so each becomes one sheet.
    /// </summary>
    public class MetrajKesifTablosuCommand
    {
        [CommandMethod("UT_METRAJ_KESIF_TABLOSU", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage("\n[Metraj Kesif] Once UT_BOQ_HESAPLA calistirilmali " +
                                "(kaydedilmis BoQ verisi bulunamadi).\n");
                MessageBox.Show(
                    "Once UT_BOQ_HESAPLA komutu calistirilarak BoQ verisi hesaplanip kaydedilmelidir.",
                    "Metraj Kesif Tablosu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var (report, settings) = DwgBoQStore.Load(doc.Database);
            if (report == null || settings == null)
            {
                ed.WriteMessage("\n[Metraj Kesif] BoQ verisi okunamadi.\n");
                return;
            }

            // Manhole Yataklama (SubBaseVolume) and Geri Dolgu (BackfillLayerSplits) are
            // runtime-only (never persisted) — recompute them after Load(), exactly as
            // BACA_KESIF_TABLOSU does. Safe to call repeatedly; needs only report.Systems/
            // SectionDebug, both fully restored by Load() above.
            ManholeExcavOverlapService.Compute(report);

            string defaultName = string.IsNullOrWhiteSpace(doc.Name)
                ? "Metraj_Kesif_Tablosu.xlsx"
                : Path.GetFileNameWithoutExtension(doc.Name) + "_Metraj_Kesif_Tablosu.xlsx";

            string path;
            using (var dlg = new SaveFileDialog
            {
                Title = "Metraj Kesif Tablosunu Kaydet",
                Filter = "Excel Workbook (*.xlsx)|*.xlsx",
                DefaultExt = "xlsx",
                FileName = defaultName,
                OverwritePrompt = true
            })
            {
                if (dlg.ShowDialog() != System.Windows.Forms.DialogResult.OK) return;
                path = dlg.FileName;
            }

            try
            {
                MetrajKesifExportService.Export(report, settings, path);
                ed.WriteMessage("\n[Metraj Kesif] Tablo kaydedildi: " + path + "\n");

                MessageBox.Show("Metraj kesif tablosu kaydedildi:\n" + path,
                    "Metraj Kesif Tablosu", MessageBoxButtons.OK, MessageBoxIcon.Information);

                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = path, UseShellExecute = true
                    });
                }
                catch { }
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\n[Metraj Kesif] HATA: " + ex.Message + "\n");
            }
        }
    }
}
