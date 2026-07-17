using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.BoQViewCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// AutoCAD command: UT_BOQ_VIEW
    ///
    /// Reads the BoQ data previously stored in the DWG by UT_BOQ and
    /// shows the <see cref="BoQResultsDialog"/> as a modeless window so the
    /// user can keep using AutoCAD while reviewing quantities.
    /// </summary>
    public class BoQViewCommand
    {
        private static BoQResultsDialog _openDialog;

        [CommandMethod("UT_BOQ_VIEW", CommandFlags.Modal)]
        public void ShowResults()
        {
            // Bring existing window to front if already open.
            if (_openDialog != null && !_openDialog.IsDisposed)
            {
                _openDialog.BringToFront();
                return;
            }

            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            BoQReport   report;
            BoQSettings settings;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage(
                    "\n[BoQ] No saved BoQ data in this DWG. " +
                    "Use 'Metraj Verisi Güncelle' to calculate.\n");
                report   = new BoQReport();
                settings = new BoQSettings();
            }
            else
            {
                try
                {
                    (report, settings) = DwgBoQStore.Load(doc.Database);
                }
                catch (System.Exception ex)
                {
                    ed.WriteMessage($"\n[BoQ ERROR] Failed to load DWG data: {ex.Message}\n");
                    return;
                }

                if (report == null)
                {
                    ed.WriteMessage(
                        "\n[BoQ] DWG data is empty or corrupted. " +
                        "Use 'Metraj Verisi Güncelle' to recalculate.\n");
                    report   = new BoQReport();
                    settings = new BoQSettings();
                }
                else
                {
                    // NetLength is runtime-only (not persisted) — recompute it now
                    // from the just-loaded data (Length2D, inverts, manhole
                    // diameter/stack/WallThicknessMm all round-trip through the DWG).
                    try { PipeNetLengthService.Compute(report, settings.NetLengthMode); }
                    catch (System.Exception netLenEx)
                    { ed.WriteMessage($"\n[BoQ] Uyarı: Net uzunluk hesap hatası: {netLenEx.Message}"); }
                }
            }

            _openDialog = new BoQResultsDialog(report, settings, doc);
            _openDialog.FormClosed += (s, e) => _openDialog = null;
            Application.ShowModelessDialog(_openDialog);
        }
    }
}
