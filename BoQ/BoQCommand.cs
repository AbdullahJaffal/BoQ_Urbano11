using System;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;
// ManholeAIService is in UrbanoMetraj.BoQ.Services â€” same namespace, no extra using needed.

// Resolve ambiguity: Autodesk.AutoCAD.Runtime also defines Exception.
using Exception = System.Exception;

// Register this class so AutoCAD discovers the [CommandMethod] attributes below.
[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.BoQCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// AutoCAD command: URBANO_BOQ
    ///
    /// Execution flow (Phase 1)
    /// â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
    ///  1. URBANO_BOQ runs on AutoCAD's main thread.
    ///  2. ManholeConfigService.EnsureCatalogExists() guarantees the pre-cast
    ///     manhole catalog Excel file exists (generates template if first run).
    ///  3. BoQStartupDialog opens modally â€” user configures all export settings.
    ///  4. On "Run": a dedicated STA background thread polls for the Urbano
    ///     export dialog.  STA is required because System.Windows.Automation
    ///     uses COM; MTA (Task.Run) deadlocks with AutoCAD's modal dialog.
    ///  5. "_ARS_EXPORT_XML" is queued via SendStringToExecute.
    ///  6. The STA thread locates the dialog, fills the file path, selects all
    ///     systems, clicks "Disa aktar", waits for the XML file, then sends
    ///     WM_CLOSE to unblock AutoCAD's main thread.
    ///  7. The STA thread registers a one-shot Application.Idle handler.
    ///  8. The Idle handler fires on the main thread: parses the XML, runs
    ///     ExcelExportService, opens the output file for the user.
    /// </summary>
    public class BoQCommand
    {
        // â”€â”€ Configuration â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);

        // â”€â”€ Shared state (main thread â†” idle callback) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
        private static string       _exportXmlPath;
        private static Editor       _editor;
        private static EventHandler _idleHandler;
        private static BoQSettings  _settings;

        // Set by BoQ_VIEW's "Refresh data" button: reopen the view window once the
        // refresh has computed + saved successfully.
        private static bool _reopenViewAfterSave;
        public static void RequestReopenView() => _reopenViewAfterSave = true;

        // =====================================================================
        // Command entry point
        // =====================================================================

        [CommandMethod("URBANO_BOQ", CommandFlags.Modal)]
        public void ExtractBoQ()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            // â”€â”€ Build/version stamp: proves WHICH assembly serves this command. â”€â”€
            ed.WriteMessage(
                "\n[BoQ] >>> BUILD STAMP: V24-conserv-debug " +
                $"(asm: {System.Reflection.Assembly.GetExecutingAssembly().Location}) <<<");

            if (_idleHandler != null)
            {
                ed.WriteMessage(
                    "\n[BoQ] A previous extraction is still running. " +
                    "Wait for it to complete before running URBANO_BOQ again.\n");
                return;
            }

            // â”€â”€ Step 1: Build settings â€” prefer saved DWG settings, else defaults â”€â”€
            BoQSettings settings;
            if (DwgBoQStore.HasData(doc.Database))
            {
                try
                {
                    (_, settings) = DwgBoQStore.Load(doc.Database);
                    settings = settings ?? new BoQSettings();
                }
                catch
                {
                    settings = new BoQSettings();
                }
            }
            else
            {
                settings = new BoQSettings();
            }

            // Ensure a valid catalog path is set.
            if (string.IsNullOrWhiteSpace(settings.ManholeConfigPath))
            {
                try { settings.ManholeConfigPath = ManholeConfigService.EnsureCatalogExists(); }
                catch (Exception ex)
                {
                    ed.WriteMessage($"\n[BoQ] Catalog check failed: {ex.Message}\n");
                }
            }

            // â”€â”€ Step 2: Prepare for Urbano XML automation â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            _settings       = settings;
            _editor         = ed;
            _exportXmlPath  = Path.Combine(Path.GetTempPath(), "urbano_boq_export.xml");

            TryDelete(_exportXmlPath);

            ed.WriteMessage("\n[BoQ] Starting Urbano XML export automation...");

            var exportService = new UrbanoExportService(ed);
            var cts           = new System.Threading.CancellationTokenSource(DialogTimeout);

            // â”€â”€ Step 3: Start STA background thread â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            var staThread = new Thread(() => RunAutomation(exportService, cts));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            // â”€â”€ Step 3b: Lock the screen (mirrors UT_LOCK) â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            // Disable AutoCAD's main window so the engineer cannot click the
            // canvas/ribbon/Urbano dialog while the export automation runs.
            // Always re-enabled in OnIdleParseAndExport / OnIdleAbort.
            UI.InputBlocker.Show();

            // â”€â”€ Step 4: Queue Urbano export command â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
            doc.SendStringToExecute("_ARS_EXPORT_XML\n", true, false, true);

            ed.WriteMessage(
                "\n[BoQ] Dialog automation started. " +
                "The Excel report will open automatically when complete.\n");
        }

        // =====================================================================
        // Background automation  (STA thread)
        // =====================================================================

        private static void RunAutomation(
            IUrbanoExportService exportService,
            System.Threading.CancellationTokenSource cts)
        {
            bool success = false;
            try
            {
                success = exportService.WaitAndAutomate(_exportXmlPath, cts.Token);
            }
            catch (Exception ex)
            {
                _editor?.WriteMessage($"\n[BoQ] Automation exception: {ex.Message}");
            }
            finally
            {
                cts.Dispose();
            }

            if (success)
            {
                _idleHandler = OnIdleParseAndExport;
                Application.Idle += _idleHandler;
            }
            else
            {
                _editor?.WriteMessage(
                    "\n[BoQ] Export automation failed. " +
                    "Verify that Urbano is loaded and the command ars_export_xml exists.\n");

                // Hide the input blocker + reset on the MAIN thread (Win32 window
                // re-enable and form disposal must not run on this STA thread).
                _idleHandler = OnIdleAbort;
                Application.Idle += _idleHandler;
            }
        }

        // =====================================================================
        // Idle callback for the failure path  (AutoCAD main thread)
        // =====================================================================

        private static void OnIdleAbort(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            UI.InputBlocker.Hide();   // re-enable AutoCAD
            _reopenViewAfterSave = false;
            ResetState();
        }

        // =====================================================================
        // Idle callback  (AutoCAD main thread)
        // =====================================================================

        private static void OnIdleParseAndExport(object sender, EventArgs e)
        {
            // Unregister immediately â€” fires exactly once.
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            Editor      ed       = _editor;
            string      xmlPath  = _exportXmlPath;
            BoQSettings settings = _settings;

            if (ed == null || xmlPath == null || settings == null) return;

            bool ok = false;
            try
            {
                // â”€â”€ Parse â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ed.WriteMessage("\n[BoQ] Parsing Urbano XML...");
                var parser = new BoQParserService(
                    enableClashDetection: settings.EnableClashDetection,
                    excavAssignment:      settings.ExcavationOverlap,
                    backfillAssignment:   settings.BackfillOverlap);
                BoQReport report = parser.Parse(xmlPath, ed);

                ed.WriteMessage(
                    $"\n[BoQ] Parsed: {report.TotalManholeCount} manholes, " +
                    $"{report.Systems.SelectMany(s => s.Pipes).Count()} pipe groups, " +
                    $"{report.SectionDebug?.Count ?? 0} sections.");

                if (settings.EnableClashDetection && report.TotalOverlapVolumeDeducted > 1e-6)
                    ed.WriteMessage(
                        $"\n[BoQ] Clash deduction applied: " +
                        $"{report.TotalOverlapVolumeDeducted:F3} m3 removed from Excavation + Backfill.");

                // â”€â”€ Phase 2: Manhole AI â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ed.WriteMessage("\n[BoQ] Running Manhole AI (topology + stacking)...");
                try
                {
                    var catalog = ManholeAIService.ReadCatalog(settings.ManholeConfigPath);
                    ed.WriteMessage($"\n[BoQ] Catalog: {catalog.Count} diameter(s) indexed.");
                    ManholeAIService.Process(report, settings, catalog);
                    int dropCount = report.Systems
                        .SelectMany(s => s.Manholes)
                        .Count(m => m.HasDropPipe);
                    ed.WriteMessage(
                        $"\n[BoQ] Manhole AI complete: " +
                        $"{report.TotalManholeCount} manholes processed" +
                        (dropCount > 0 ? $", {dropCount} with drop-pipe (Selale) connections." : "."));
                }
                catch (Exception aiEx)
                {
                    ed.WriteMessage(
                        $"\n[BoQ] Manhole AI warning: {aiEx.Message} (export continues without BOM data)");
                }

                // â”€â”€ Save results to DWG â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€â”€
                ed.WriteMessage("\n[BoQ] Saving results to DWG database...");
                var activeDoc = Autodesk.AutoCAD.ApplicationServices.Application
                    .DocumentManager.MdiActiveDocument;
                using (activeDoc.LockDocument())
                {
                    DwgBoQStore.Save(activeDoc.Database, report, settings);
                }
                ed.WriteMessage(
                    $"\n[BoQ] Results saved. " +
                    $"{report.SectionDebug?.Count ?? 0} section(s) with per-station data stored in DWG.\n" +
                    "[BoQ] Open the Metraj window (URBANO_BOQ_VIEW) to choose the KazÄ±/Dolgu\n" +
                    "[BoQ] calculation method, view quantities, export to Excel, or build 3-D solids.\n" +
                    "[BoQ] Done.\n");
                ok = true;
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[BoQ ERROR] {ex.GetType().Name}: {ex.Message}\n");
            }
            finally
            {
                UI.InputBlocker.Hide();   // re-enable AutoCAD's main window
                TryDelete(xmlPath);

                bool reopen = ok && _reopenViewAfterSave;
                _reopenViewAfterSave = false;
                ResetState();

                if (reopen)
                    Application.DocumentManager.MdiActiveDocument?
                        .SendStringToExecute("URBANO_BOQ_VIEW\n", true, false, true);
            }
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        private static void TryDelete(string path)
        {
            if (path != null && File.Exists(path))
                try { File.Delete(path); } catch { /* ignore */ }
        }

        private static void ResetState()
        {
            _exportXmlPath = null;
            _editor        = null;
            _idleHandler   = null;
            _settings      = null;
        }
    }
}
