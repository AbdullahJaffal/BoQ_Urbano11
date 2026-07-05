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

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.BacaKesifTablosuCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// BACA_KESIF_TABLOSU — generates a standalone manhole quantity-takeoff
    /// workbook from a BoQReport already computed and saved by URBANO_BOQ_HESAPLA.
    ///
    /// Does NOT talk to Urbano at all (no dialog automation, no STA thread needed)
    /// — it only reads what's already persisted in the DWG's NOD, so it can be run
    /// any time after URBANO_BOQ_HESAPLA, in the same session or a later one.
    /// </summary>
    public class BacaKesifTablosuCommand
    {
        [CommandMethod("BACA_KESIF_TABLOSU", CommandFlags.Modal)]
        public void Run()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage("\n[Baca Kesif] Once URBANO_BOQ_HESAPLA calistirilmali " +
                                "(kaydedilmis BoQ verisi bulunamadi).\n");
                MessageBox.Show(
                    "Once URBANO_BOQ_HESAPLA komutu calistirilarak BoQ verisi hesaplanip kaydedilmelidir.",
                    "Baca Kesif Tablosu", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var (report, settings) = DwgBoQStore.Load(doc.Database);
            if (report == null || settings == null)
            {
                ed.WriteMessage("\n[Baca Kesif] BoQ verisi okunamadi.\n");
                return;
            }

            ManholeConnectionLinkService.Populate(report);

            string defaultName = string.IsNullOrWhiteSpace(doc.Name)
                ? "Baca_Kesif_Tablosu.xlsx"
                : Path.GetFileNameWithoutExtension(doc.Name) + "_Baca_Kesif_Tablosu.xlsx";

            string path;
            using (var dlg = new SaveFileDialog
            {
                Title = "Baca Kesif Tablosunu Kaydet",
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
                ManholeKesifExportService.Export(report, settings, path);
                ed.WriteMessage("\n[Baca Kesif] Tablo kaydedildi: " + path + "\n");

                MessageBox.Show("Baca kesif tablosu kaydedildi:\n" + path,
                    "Baca Kesif Tablosu", MessageBoxButtons.OK, MessageBoxIcon.Information);

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
                ed.WriteMessage("\n[Baca Kesif] HATA: " + ex.Message + "\n");
            }
        }
    }
}
