using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Automation;
using Autodesk.AutoCAD.EditorInput;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Automates the Urbano "XML'e topoloji ihraç" modal dialog using a hybrid
    /// approach: Win32 FindWindow for robust HWND discovery, then
    /// AutomationElement.FromHandle for control interaction.
    ///
    /// Threading contract
    /// ------------------
    /// Must be called from a dedicated STA thread — NOT Task.Run.
    /// UI Automation uses COM; MTA deadlocks when AutoCAD's main thread is
    /// blocked by the modal dialog.
    ///
    /// CRITICAL: Never call _ed.WriteMessage() while the dialog is alive.
    /// All messages are buffered and flushed only after the window is closed.
    /// </summary>
    public class UrbanoExportService : IUrbanoExportService
    {
        // ── Win32 imports ─────────────────────────────────────────────────────
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr SendMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr hWnd);

        private const uint WM_CLOSE = 0x0010;

        // ── Dialog / button names ─────────────────────────────────────────────
        private const string MainWindowTitle  = "Urbano XML'e topoloji ihraç";
        private const string ExportButtonName = "Dışa aktar";
        private const string OkButtonName     = "Tamam";

        // ── Timeouts ──────────────────────────────────────────────────────────
        // Phase 1 – wait for main dialog to appear   : 30 × 500 ms = 15 s
        private const int DialogPollCount  = 30;
        // Phase 3 – wait for "Tamam" success popup   : 20 × 500 ms = 10 s
        private const int PopupPollCount   = 20;
        // Phase 5 – wait for XML file to be ready    : 20 × 500 ms = 10 s
        private const int FilePollCount    = 20;
        private const int PollMs           = 500;

        private readonly Editor _editor;
        private readonly StringBuilder _log = new StringBuilder();

        public UrbanoExportService(Editor editor) => _editor = editor;

        // =====================================================================
        // Public entry point
        // =====================================================================

        public bool WaitAndAutomate(string exportPath, CancellationToken ct)
        {
            IntPtr mainHwnd = IntPtr.Zero;
            bool   result   = false;

            try
            {
                // ── Phase 1: Find main window via Win32 FindWindow ────────────
                Dbg("Phase 1 — Waiting for main dialog HWND...");
                mainHwnd = WaitForWindowHandle(MainWindowTitle, DialogPollCount, ct);
                if (mainHwnd == IntPtr.Zero)
                {
                    Dbg("TIMEOUT — main dialog did not appear.");
                    return false;
                }
                Dbg($"Phase 1 — HWND found: 0x{mainHwnd.ToInt64():X}. Waiting 500 ms for UI render...");
                Thread.Sleep(500);

                // Convert HWND → AutomationElement for control interaction.
                AutomationElement mainWindow = AutomationElement.FromHandle(mainHwnd);
                if (mainWindow == null)
                {
                    Dbg("ERROR — AutomationElement.FromHandle returned null.");
                    return false;
                }
                Dbg($"Phase 1 — AutomationElement acquired: \"{mainWindow.Current.Name}\"");

                // ── Phase 2a: Set file path ───────────────────────────────────
                Dbg("Phase 2a — Setting export file path...");
                SetFilePath(mainWindow, exportPath);
                Dbg($"Phase 2a — Path set to: {exportPath}");

                // ── Phase 2b: Check all system checkboxes ─────────────────────
                Dbg("Phase 2b — Checking all system checkboxes...");
                int cbCount = CheckAllCheckboxes(mainWindow);
                Dbg($"Phase 2b — {cbCount} checkbox(es) processed.");

                // ── Phase 2c: Click "Dışa aktar" ──────────────────────────────
                Dbg($"Phase 2c — Clicking export button \"{ExportButtonName}\"...");
                ClickNamedButton(mainWindow, ExportButtonName);
                Dbg("Phase 2c — Export button invoked.");

                // ── Phase 3: Dismiss "Tamam" success popup ────────────────────
                Dbg($"Phase 3 — Polling for \"{OkButtonName}\" success popup...");
                bool dismissed = DismissOkPopup(mainHwnd, PopupPollCount, ct);
                Dbg(dismissed
                    ? $"Phase 3 — \"{OkButtonName}\" popup dismissed."
                    : $"Phase 3 — \"{OkButtonName}\" popup not found (may not be required).");

                // ── Phase 4 (pre-close): Wait for XML file ────────────────────
                Dbg("Phase 4 — Waiting for XML file to be ready...");
                result = WaitForFileReady(exportPath, FilePollCount, ct);
                Dbg(result ? "Phase 4 — XML file confirmed ready." : "Phase 4 — TIMEOUT waiting for XML file.");

                return result;
            }
            catch (OperationCanceledException)
            {
                Dbg("CANCELLED — operation was cancelled.");
                return false;
            }
            catch (Exception ex)
            {
                Dbg($"EXCEPTION — {ex.GetType().Name}: {ex.Message}");
                return false;
            }
            finally
            {
                // ── Phase 5 (CRITICAL): Force-close main window ───────────────
                // AutoCAD is still blocked by the modal dialog. We MUST close it
                // here so the main thread can unblock, regardless of success/fail.
                if (mainHwnd != IntPtr.Zero && IsWindow(mainHwnd))
                {
                    Dbg($"Phase 5 — Sending WM_CLOSE to HWND 0x{mainHwnd.ToInt64():X}...");
                    SendMessage(mainHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                    Dbg("Phase 5 — WM_CLOSE sent.");
                }
                else
                {
                    Dbg("Phase 5 — Window already closed or HWND invalid, skipping WM_CLOSE.");
                }

                // Dialog is now closed → main thread unblocked → safe to write.
                FlushLog();
            }
        }

        // =====================================================================
        // Phase 1 — Wait for main window HWND
        // =====================================================================

        private static IntPtr WaitForWindowHandle(
            string title, int maxIterations, CancellationToken ct)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (ct.IsCancellationRequested) return IntPtr.Zero;

                IntPtr hwnd = FindWindow(null, title);
                if (hwnd != IntPtr.Zero) return hwnd;

                Dbg($"  Phase 1 poll {i + 1}/{maxIterations} — window not found yet...");
                Thread.Sleep(PollMs);
            }
            return IntPtr.Zero;
        }

        // =====================================================================
        // Phase 2a — Set file path in the first Edit control
        // =====================================================================

        private static void SetFilePath(AutomationElement window, string path)
        {
            Dbg("  SetFilePath — searching for Edit control...");

            var editCond = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.Edit);

            AutomationElement edit = window.FindFirst(TreeScope.Descendants, editCond);

            if (edit == null)
                throw new InvalidOperationException(
                    "No Edit control found in the export dialog.");

            Dbg($"  SetFilePath — Edit found (AutomationId=\"{edit.Current.AutomationId}\", " +
                $"Name=\"{edit.Current.Name}\").");

            if (!edit.TryGetCurrentPattern(ValuePattern.Pattern, out object vpo))
                throw new NotSupportedException(
                    "Edit control does not support ValuePattern.");

            ((ValuePattern)vpo).SetValue(path);
            Dbg("  SetFilePath — ValuePattern.SetValue called.");
        }

        // =====================================================================
        // Phase 2b — Check all system checkboxes
        // =====================================================================

        private static int CheckAllCheckboxes(AutomationElement window)
        {
            var cbCond = new PropertyCondition(
                AutomationElement.ControlTypeProperty, ControlType.CheckBox);

            var checkboxes = window.FindAll(TreeScope.Descendants, cbCond);
            Dbg($"  CheckAll — found {checkboxes.Count} checkbox(es).");

            int processed = 0;
            foreach (AutomationElement cb in checkboxes)
            {
                string preName = cb.Current.Name;
                string preState = cb.TryGetCurrentPattern(TogglePattern.Pattern, out object tpo0)
                    ? ((TogglePattern)tpo0).Current.ToggleState.ToString()
                    : "N/A";
                Dbg($"  CheckAll — checkbox \"{preName}\" state={preState}");

                if (!cb.TryGetCurrentPattern(TogglePattern.Pattern, out object tpo))
                    continue;

                var toggle = (TogglePattern)tpo;
                int attempts = 0;
                while (toggle.Current.ToggleState != ToggleState.On && attempts < 3)
                {
                    toggle.Toggle();
                    Thread.Sleep(80);
                    attempts++;
                }

                Dbg($"  CheckAll — checkbox \"{cb.Current.Name}\" → " +
                    $"{toggle.Current.ToggleState} (after {attempts} toggle(s)).");
                processed++;
            }
            return processed;
        }

        // =====================================================================
        // Phase 2c — Click a named button inside a window
        // =====================================================================

        private static void ClickNamedButton(AutomationElement window, string name)
        {
            Dbg($"  ClickButton — searching for button \"{name}\"...");

            var cond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, name));

            AutomationElement button = window.FindFirst(TreeScope.Descendants, cond);

            if (button == null)
                throw new InvalidOperationException(
                    $"Button \"{name}\" not found in the dialog.");

            Dbg($"  ClickButton — button found (AutomationId=\"{button.Current.AutomationId}\").");

            if (!button.TryGetCurrentPattern(InvokePattern.Pattern, out object ipo))
                throw new NotSupportedException(
                    $"Button \"{name}\" does not support InvokePattern.");

            ((InvokePattern)ipo).Invoke();
            Dbg($"  ClickButton — InvokePattern.Invoke() called on \"{name}\".");
        }

        // =====================================================================
        // Phase 3 — Dismiss "Tamam" success popup
        // =====================================================================

        private static bool DismissOkPopup(
            IntPtr mainHwnd, int maxIterations, CancellationToken ct)
        {
            var btnCond = new AndCondition(
                new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
                new PropertyCondition(AutomationElement.NameProperty, OkButtonName));

            for (int i = 0; i < maxIterations; i++)
            {
                if (ct.IsCancellationRequested) return false;

                // Strategy A: look for "Tamam" anywhere on the desktop.
                AutomationElement okBtn = AutomationElement.RootElement
                    .FindFirst(TreeScope.Descendants, btnCond);

                if (okBtn != null)
                {
                    Dbg($"  DismissOk — found \"{OkButtonName}\" via desktop search " +
                        $"(parent: \"{okBtn.Current.ClassName}\").");

                    if (okBtn.TryGetCurrentPattern(InvokePattern.Pattern, out object ipo))
                    {
                        ((InvokePattern)ipo).Invoke();
                        Dbg($"  DismissOk — \"{OkButtonName}\" invoked.");
                        Thread.Sleep(200);
                        return true;
                    }
                }

                // Strategy B: look inside the main dialog itself (in case it's
                // a panel rather than a separate popup window).
                if (mainHwnd != IntPtr.Zero && IsWindow(mainHwnd))
                {
                    AutomationElement mainWin = AutomationElement.FromHandle(mainHwnd);
                    if (mainWin != null)
                    {
                        AutomationElement okInMain =
                            mainWin.FindFirst(TreeScope.Descendants, btnCond);
                        if (okInMain != null)
                        {
                            Dbg($"  DismissOk — found \"{OkButtonName}\" inside main window.");
                            if (okInMain.TryGetCurrentPattern(InvokePattern.Pattern, out object ipo2))
                            {
                                ((InvokePattern)ipo2).Invoke();
                                Dbg($"  DismissOk — \"{OkButtonName}\" (in main) invoked.");
                                Thread.Sleep(200);
                                return true;
                            }
                        }
                    }
                }

                Dbg($"  DismissOk poll {i + 1}/{maxIterations} — popup not visible yet...");
                Thread.Sleep(PollMs);
            }

            return false;
        }

        // =====================================================================
        // Phase 4 — Wait for XML file to be ready
        // =====================================================================

        private static bool WaitForFileReady(
            string path, int maxIterations, CancellationToken ct)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (ct.IsCancellationRequested) return false;

                if (File.Exists(path))
                {
                    try
                    {
                        using (var fs = File.Open(
                            path, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            if (fs.Length > 0)
                            {
                                Dbg($"  FileReady — file exists, length={fs.Length} bytes.");
                                return true;
                            }
                        }
                    }
                    catch (IOException)
                    {
                        Dbg($"  FileReady poll {i + 1}/{maxIterations} — file locked, still writing...");
                    }
                }
                else
                {
                    Dbg($"  FileReady poll {i + 1}/{maxIterations} — file does not exist yet...");
                }

                Thread.Sleep(PollMs);
            }
            return false;
        }

        // =====================================================================
        // Logging helpers
        // =====================================================================

        // Static so Phase 1 helper (static method) can also log.
        // We accumulate into the instance buffer via the instance wrapper.
        private static readonly StringBuilder _staticLog = new StringBuilder();

        private static void Dbg(string msg)
        {
            string line = $"[BoQ] {msg}";
            Debug.WriteLine(line);
            Console.WriteLine(line);
            _staticLog.Append('\n').Append(line);
        }

        /// <summary>
        /// Write all buffered messages to the AutoCAD command line.
        /// Safe to call only AFTER the modal dialog has been closed.
        /// </summary>
        private void FlushLog()
        {
            // Merge static buffer (used by static helpers) into instance log.
            _log.Append(_staticLog);
            _staticLog.Clear();

            if (_editor != null && _log.Length > 0)
                _editor.WriteMessage(_log.ToString());

            _log.Clear();
        }
    }
}
