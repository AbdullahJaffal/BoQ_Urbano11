using System;
using System.IO;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.SmartAssembly;
using UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels;
using UrbanoMetraj.BoQ.UI;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.PipeCatalogs.PipeCatalogCommand))]

namespace UrbanoMetraj.BoQ.PipeCatalogs
{
    /// <summary>
    /// Commands
    /// ────────────────────────────────────────────────────────────
    ///  UT_PIPE_CATALOG              — opens the shared Akıllı Montaj window on the
    ///                              "Boru Kataloğu" tab (formerly a standalone window).
    ///
    ///  UT_PIPE_CATALOG_LIVE_EXTRACT — triggers ARS_EXPORT_XML on an STA thread
    ///                              and parses the resulting XML for pipe
    ///                              catalog data (families, DN/OD/ID/wall).
    ///
    /// Threading contract (mirrors BoQCommand exactly)
    /// ────────────────────────────────────────────────────────────
    ///  1. UT_PIPE_CATALOG_LIVE_EXTRACT sets up static shared state.
    ///  2. InputBlocker.Show() disables the AutoCAD main window.
    ///  3. A background STA thread starts UrbanoExportService.WaitAndAutomate().
    ///  4. doc.SendStringToExecute("_ARS_EXPORT_XML\n") is queued and the
    ///     command method returns — AutoCAD is free to show the Urbano dialog.
    ///  5. When the STA thread confirms the XML is written, it registers a
    ///     one-shot Application.Idle handler and exits.
    ///  6. The Idle handler fires on the main thread: parses the XML, updates
    ///     PipeCatalogStore, refreshes the Akıllı Montaj window, hides InputBlocker.
    /// </summary>
    public class PipeCatalogCommand
    {
        // ── Shared state  (main thread ↔ Idle callback) ───────────────────────
        private static string       _exportXmlPath;
        private static Editor       _editor;
        private static EventHandler _idleHandler;

        // =====================================================================
        // UT_PIPE_CATALOG  — open / activate the catalog manager tab
        // =====================================================================

        [CommandMethod("UT_PIPE_CATALOG")]
        public void OpenPipeCatalog()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_PIPE_CATALOG);
            }
            catch (Exception ex)
            {
                ed.WriteMessage("\nUT_PIPE_CATALOG hatası: " + ex.Message);
            }
        }

        // =====================================================================
        // UT_PIPE_CATALOG_LIVE_EXTRACT  — Mode C: STA-thread ARS_EXPORT_XML
        // =====================================================================

        [CommandMethod("UT_PIPE_CATALOG_LIVE_EXTRACT", CommandFlags.Modal)]
        public void LiveExtractPipeCatalog()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            if (_idleHandler != null)
            {
                ed.WriteMessage(
                    "\n[PipeCatalog] Önceki çıkarma işlemi devam ediyor. " +
                    "Lütfen tamamlanmasını bekleyin.\n");
                return;
            }

            _exportXmlPath = Path.Combine(
                Path.GetTempPath(), "urbano_pipe_cat_export.xml");
            TryDelete(_exportXmlPath);
            _editor = ed;

            ed.WriteMessage("\n[PipeCatalog] Urbano XML dışa aktarma otomasyonu başlatılıyor...");

            var exportService = new UrbanoExportService(ed);
            var cts           = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            // ── STA background thread: waits for Urbano dialog, automates it ──
            var staThread = new Thread(() => RunAutomation(exportService, cts));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            // ── Block user interaction while the dialog runs ──────────────────
            InputBlocker.Show();

            // ── Queue the Urbano export command; the STA thread will find the ─
            // dialog that appears, fill the path, click "Dışa aktar", and then ─
            // register the Idle callback below.  The command method returns now.─
            doc.SendStringToExecute("_ARS_EXPORT_XML\n", true, false, true);

            ed.WriteMessage(
                "\n[PipeCatalog] Dialog otomasyonu başlatıldı. " +
                "XML ayrıştırıldıktan sonra katalog sekmesi otomatik güncellenir.\n");
        }

        // =====================================================================
        // STA background thread
        // =====================================================================

        private static void RunAutomation(
            IUrbanoExportService      exportService,
            CancellationTokenSource   cts)
        {
            bool success = false;
            try
            {
                success = exportService.WaitAndAutomate(_exportXmlPath, cts.Token);
            }
            catch (Exception ex)
            {
                _editor?.WriteMessage("\n[PipeCatalog] Otomasyon istisnası: " + ex.Message);
            }
            finally
            {
                cts.Dispose();
            }

            _idleHandler = success ? (EventHandler)OnIdleParsePipeCatalog
                                   : (EventHandler)OnIdleAbort;
            Application.Idle += _idleHandler;
        }

        // =====================================================================
        // Idle callback — success path  (AutoCAD main thread)
        // =====================================================================

        private static void OnIdleParsePipeCatalog(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            Editor ed      = _editor;
            string xmlPath = _exportXmlPath;

            try
            {
                ed?.WriteMessage("\n[PipeCatalog] Urbano XML ayrıştırılıyor...");

                var extracted = PipeCatalogImportService.ImportUrbanoXml(xmlPath);
                PipeCatalogStore.Current = extracted;

                ed?.WriteMessage(string.Format(
                    "\n[PipeCatalog] {0} aile çıkarıldı.",
                    extracted.Families.Count));

                // Push into the shared Akıllı Montaj window (creates it if not already open)
                // and jump to the Boru Kataloğu tab so the user sees the result immediately.
                var mainVm = SmartAssemblyCommand.EnsureMainVm();
                mainVm.RefreshPipeCatalog(extracted);
                SmartAssemblyCommand.ShowWindowOnTab(SmartAssemblyMainVm.TAB_PIPE_CATALOG);
            }
            catch (Exception ex)
            {
                ed?.WriteMessage("\n[PipeCatalog ERROR] " + ex.Message);
            }
            finally
            {
                InputBlocker.Hide();
                TryDelete(xmlPath);
                ResetState();
            }
        }

        // =====================================================================
        // Idle callback — failure path  (AutoCAD main thread)
        // =====================================================================

        private static void OnIdleAbort(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;
            InputBlocker.Hide();
            ResetState();
        }

        // =====================================================================
        // Helpers
        // =====================================================================

        private static void TryDelete(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                try { File.Delete(path); } catch { }
        }

        private static void ResetState()
        {
            _exportXmlPath = null;
            _editor        = null;
            _idleHandler   = null;
        }
    }
}
