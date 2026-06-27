using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using AcadApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Strict modal input blocker shown on the AutoCAD main thread while the
    /// Urbano <c>ars_export_xml</c> automation and BoQ processing run.
    ///
    /// Mirrors the UT_LOCK "WaitBlocker" (UrbanoLock): AutoCAD's main window is
    /// disabled via <c>EnableWindow(hwnd, false)</c> so the engineer cannot click
    /// the canvas, the ribbon, or any Urbano dialog underneath. AutoCAD's command
    /// queue, WM_TIMER and posted application messages are NOT affected, so
    /// <c>Application.Idle</c> keeps firing and the export automation still drives
    /// the Urbano dialog. <see cref="Hide"/> always re-enables the window so
    /// AutoCAD is never left permanently disabled, even on failure.
    ///
    /// All members must be called on the AutoCAD main (UI) thread.
    /// </summary>
    internal static class InputBlocker
    {
        // EnableWindow(false) blocks keyboard + mouse INPUT to the target HWND and
        // all of its child/owned windows; the message pump keeps running.
        [DllImport("user32.dll")]
        private static extern bool EnableWindow(IntPtr hWnd, bool bEnable);

        private static Form          _form;
        private static IntPtr        _ownerHwnd;
        private static volatile bool _allowClose;

        // Lets Form.Show(owner) accept a raw HWND.
        private sealed class Win32Window : IWin32Window
        {
            public Win32Window(IntPtr h) { Handle = h; }
            public IntPtr Handle { get; }
        }

        /// <summary>
        /// Borderless, non-movable, non-closable form. The hardened WndProc
        /// swallows every system command (move / size / minimise / maximise /
        /// close / keyboard menu) so the engineer cannot drag the window, resize
        /// it, or close it with Alt+F4 while the export runs.
        /// </summary>
        private sealed class BlockerForm : Form
        {
            private const int WM_SYSCOMMAND   = 0x0112;
            private const int WM_NCLBUTTONDOWN = 0x00A1;
            private const int HTCAPTION       = 2;

            // System-command codes (low 4 bits are reserved → mask with 0xFFF0).
            private const int SC_SIZE     = 0xF000;
            private const int SC_MOVE     = 0xF010;
            private const int SC_MINIMIZE = 0xF020;
            private const int SC_MAXIMIZE = 0xF030;
            private const int SC_CLOSE    = 0xF060;
            private const int SC_KEYMENU  = 0xF100;

            protected override void WndProc(ref Message m)
            {
                if (m.Msg == WM_SYSCOMMAND)
                {
                    int cmd = m.WParam.ToInt32() & 0xFFF0;
                    if (cmd == SC_MOVE || cmd == SC_SIZE || cmd == SC_MINIMIZE ||
                        cmd == SC_MAXIMIZE || cmd == SC_CLOSE || cmd == SC_KEYMENU)
                        return;   // swallow — no move/size/close
                }

                // Defensive: ignore any attempt to drag by a caption area.
                if (m.Msg == WM_NCLBUTTONDOWN && m.WParam.ToInt32() == HTCAPTION)
                    return;

                base.WndProc(ref m);
            }
        }

        private const string DefaultMessage =
            "Urbano BoQ dışa aktarımı devam ediyor…  Lütfen bekleyin, ekrana tıklamayın.";

        /// <summary>
        /// Shows the blocker and disables AutoCAD's main window. Idempotent — a
        /// second call while already showing is a no-op. Never throws.
        /// </summary>
        public static void Show(string message = null)
        {
            if (_form != null) return;   // already showing

            try
            {
                _allowClose = false;

                var label = new Label
                {
                    Text      = string.IsNullOrEmpty(message) ? DefaultMessage : message,
                    Dock      = DockStyle.Fill,
                    TextAlign = ContentAlignment.MiddleCenter,
                    Font      = new Font("Segoe UI", 10.5f, FontStyle.Bold),
                    ForeColor = Color.FromArgb(0x2E, 0x4A, 0x6B)
                };

                var bar = new ProgressBar
                {
                    Dock                  = DockStyle.Fill,
                    Style                 = ProgressBarStyle.Marquee,
                    MarqueeAnimationSpeed = 30
                };

                var tlp = new TableLayoutPanel
                {
                    Dock        = DockStyle.Fill,
                    ColumnCount = 1,
                    RowCount    = 2,
                    Padding     = new Padding(20, 14, 20, 14),
                    BackColor   = Color.White
                };
                tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 60));
                tlp.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
                tlp.Controls.Add(label, 0, 0);
                tlp.Controls.Add(bar,   0, 1);

                _form = new BlockerForm
                {
                    // Borderless ⇒ no title bar to grab ⇒ cannot be moved by the user.
                    FormBorderStyle = FormBorderStyle.None,
                    StartPosition   = FormStartPosition.CenterScreen,
                    ClientSize      = new Size(480, 130),
                    ControlBox      = false,        // no [X] — cannot be closed by user
                    TopMost         = true,         // float above AutoCAD AND Urbano dialogs
                    ShowInTaskbar   = false,
                    Cursor          = Cursors.WaitCursor,
                    Padding         = new Padding(2), // 2 px accent frame (form back colour)
                    BackColor       = Color.FromArgb(0x2E, 0x4A, 0x6B)
                };
                _form.Controls.Add(tlp);
                _form.FormClosing += (s, e) => { if (!_allowClose) e.Cancel = true; };

                _ownerHwnd = IntPtr.Zero;
                try
                {
                    IntPtr acad = AcadApp.MainWindow.Handle;
                    _ownerHwnd = acad;
                    EnableWindow(acad, false);                 // block all input to AutoCAD
                    _form.Show(new Win32Window(acad));
                }
                catch
                {
                    _form.Show();                               // fallback: no owner, still TopMost
                }
            }
            catch
            {
                // Building/showing the blocker must never break the export pipeline.
                Hide();
            }
        }

        /// <summary>
        /// Re-enables AutoCAD's main window, then closes and disposes the blocker.
        /// Safe to call multiple times and when nothing is showing. Never throws.
        /// </summary>
        public static void Hide()
        {
            var form  = _form;
            var owner = _ownerHwnd;

            // Clear references first so the FormClosing guard sees _allowClose=true
            // and re-entrancy cannot reach a half-disposed form.
            _form       = null;
            _ownerHwnd  = IntPtr.Zero;
            _allowClose = true;

            // Re-enable AutoCAD BEFORE destroying the dialog so focus returns to it.
            if (owner != IntPtr.Zero)
            {
                try { EnableWindow(owner, true); } catch { }
            }

            if (form == null || form.IsDisposed) return;
            try { form.Close();   } catch { }
            try { form.Dispose(); } catch { }
        }
    }
}
