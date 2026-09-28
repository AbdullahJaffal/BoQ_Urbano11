using System;
using System.Collections.Generic;
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

        // Extra imports for the caption-INDEPENDENT modal-popup dismissal (see DismissOkPopup).
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)]
        private static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")]
        private static extern bool IsWindowEnabled(IntPtr hWnd);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        private const uint WM_CLOSE   = 0x0010;
        private const uint WM_COMMAND = 0x0111;
        private const uint WM_KEYDOWN = 0x0100;
        private const uint WM_KEYUP   = 0x0101;
        private const int  VK_RETURN  = 0x0D;
        private const int  IDOK       = 1;      // OK control id — SAME in every Windows UI language
        private const uint GW_OWNER   = 4;

        // OK/confirm button captions across the languages the suite targets (TR/EN/RU/AR + a few
        // more), lower-cased. Used ONLY as a tie-breaker when a popup has more than one button —
        // a single-button success popup is dismissed structurally without consulting this set.
        private static readonly HashSet<string> OkCaptions = new HashSet<string>
        {
            "tamam", "ok", "okay", "ок", "موافق", "aceptar", "確定", "确定"
        };

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

        // LANGUAGE-INDEPENDENT dismissal. The success popup Urbano shows after export is a
        // standard Windows MessageBox whose OK-button CAPTION is localised to the machine's
        // Windows UI language ("Tamam" on Turkish, "OK"/"موافق"/"ОК" elsewhere). Matching the
        // literal string "Tamam" therefore worked only on Turkish Windows and left the modal
        // popup up on every other machine — which (being modal) froze the AutoCAD main thread,
        // starved Application.Idle, and left the InputBlocker stuck. This version never reads the
        // caption: it locates the modal popup structurally and dismisses it by mechanism.
        private static bool DismissOkPopup(
            IntPtr mainHwnd, int maxIterations, CancellationToken ct)
        {
            for (int i = 0; i < maxIterations; i++)
            {
                if (ct.IsCancellationRequested) return false;

                IntPtr popup = FindModalPopup(mainHwnd);
                if (popup != IntPtr.Zero && DismissPopupWindow(popup))
                {
                    Dbg("  DismissOk — modal popup dismissed (caption-independent).");
                    return true;
                }

                // Fallback: a confirm button sitting INSIDE the main dialog (panel, not a
                // separate window). Invoke by button role, not by name.
                if (mainHwnd != IntPtr.Zero && IsWindow(mainHwnd))
                {
                    try
                    {
                        var mainWin = AutomationElement.FromHandle(mainHwnd);
                        var btn = PickOkButton(mainWin?.FindAll(TreeScope.Descendants,
                            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                        if (btn != null && btn.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
                        {
                            ((InvokePattern)ip).Invoke();
                            Dbg("  DismissOk — confirm button inside main window invoked.");
                            Thread.Sleep(200);
                            return true;
                        }
                    }
                    catch { /* keep polling */ }
                }

                Dbg($"  DismissOk poll {i + 1}/{maxIterations} — popup not visible yet...");
                Thread.Sleep(PollMs);
            }

            return false;
        }

        // Finds the modal confirmation window Urbano opens after export: a visible+enabled
        // top-level window OWNED by the export dialog, or (fallback) a standard "#32770" dialog
        // on the same UI thread. Never matches our own WinForms InputBlocker (different class and
        // owner) nor the main dialog itself.
        private static IntPtr FindModalPopup(IntPtr mainHwnd)
        {
            IntPtr found = IntPtr.Zero;
            if (mainHwnd == IntPtr.Zero) return found;
            uint mainProc;
            uint mainThread = GetWindowThreadProcessId(mainHwnd, out mainProc);

            EnumWindows((hwnd, l) =>
            {
                if (hwnd == mainHwnd || !IsWindowVisible(hwnd) || !IsWindowEnabled(hwnd)) return true;

                bool owned = GetWindow(hwnd, GW_OWNER) == mainHwnd;
                bool sameThreadDialog = false;
                if (!owned)
                {
                    var sb = new StringBuilder(64);
                    GetClassName(hwnd, sb, sb.Capacity);
                    uint proc;
                    sameThreadDialog = sb.ToString() == "#32770"
                                    && GetWindowThreadProcessId(hwnd, out proc) == mainThread;
                }
                if (owned || sameThreadDialog) { found = hwnd; return false; }  // stop enumeration
                return true;
            }, IntPtr.Zero);

            return found;
        }

        // Dismisses a modal popup WITHOUT reading its button text, trying in order until the
        // window is gone: (1) UIA-invoke its sole/confirm button, (2) WM_COMMAND IDOK (control
        // id 1 — identical in every Windows language), (3) Enter keypress, (4) WM_CLOSE.
        private static bool DismissPopupWindow(IntPtr popup)
        {
            try
            {
                var el = AutomationElement.FromHandle(popup);
                var btn = PickOkButton(el?.FindAll(TreeScope.Descendants,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button)));
                if (btn != null && btn.TryGetCurrentPattern(InvokePattern.Pattern, out object ip))
                {
                    ((InvokePattern)ip).Invoke();
                    Thread.Sleep(200);
                    if (!IsWindow(popup) || !IsWindowVisible(popup)) return true;
                }
            }
            catch { /* fall through to the Win32 paths */ }

            SendMessage(popup, WM_COMMAND, (IntPtr)IDOK, IntPtr.Zero);
            Thread.Sleep(150);
            if (!IsWindow(popup) || !IsWindowVisible(popup)) return true;

            PostMessage(popup, WM_KEYDOWN, (IntPtr)VK_RETURN, IntPtr.Zero);
            PostMessage(popup, WM_KEYUP,   (IntPtr)VK_RETURN, IntPtr.Zero);
            Thread.Sleep(150);
            if (!IsWindow(popup) || !IsWindowVisible(popup)) return true;

            SendMessage(popup, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            Thread.Sleep(150);
            return !IsWindow(popup);
        }

        // From a popup's buttons, pick the confirm button caption-independently: the only button
        // when there is just one (the success-popup case); otherwise the one whose name matches a
        // known OK caption across languages, else the first button.
        private static AutomationElement PickOkButton(AutomationElementCollection buttons)
        {
            if (buttons == null || buttons.Count == 0) return null;
            if (buttons.Count == 1) return buttons[0];
            foreach (AutomationElement b in buttons)
            {
                string n = "";
                try { n = (b.Current.Name ?? "").Trim().ToLowerInvariant(); } catch { }
                if (OkCaptions.Contains(n)) return b;
            }
            return buttons[0];
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
                        // FileShare.ReadWrite (not None): Urbano often keeps its own
                        // handle open AFTER the "Tamam" success popup — by then the
                        // file is already fully written, so an exclusive (None) open
                        // would throw IOException and make us poll for the full timeout
                        // for no reason. We only need to confirm it exists and is
                        // non-empty, which a shared open does without blocking.
                        using (var fs = File.Open(
                            path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
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
                        Dbg($"  FileReady poll {i + 1}/{maxIterations} — file not readable yet...");
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
