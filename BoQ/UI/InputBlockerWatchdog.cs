using System;
using System.Windows.Forms;

namespace UrbanoMetraj.BoQ.UI
{
    /// <summary>
    /// Safety valve for <see cref="InputBlocker"/>. If the ARS_EXPORT_XML automation gets
    /// stuck — e.g. a modal success popup it could not dismiss keeps AutoCAD's main thread
    /// busy so the normal <c>Application.Idle</c>-driven <see cref="InputBlocker.Hide"/> never
    /// runs — this force-hides the blocker after a timeout, so the user is never permanently
    /// locked out and can reach the dialog behind it.
    ///
    /// A <see cref="System.Windows.Forms.Timer"/> is used DELIBERATELY: WM_TIMER is dispatched
    /// even inside a modal message loop, whereas <c>Application.Idle</c> is starved by one.
    ///
    /// <see cref="Arm"/> must be called on the AutoCAD main (UI) thread, right after
    /// <c>InputBlocker.Show()</c>. It cancels any previously-armed timer first, so back-to-back
    /// operations never let an old timer hide a new operation's blocker. Because
    /// <see cref="InputBlocker.Hide"/> is idempotent, a normal completion that already hid the
    /// blocker makes the eventual timeout a harmless no-op. The blocker is NOT modified — this is
    /// a wholly external watchdog.
    /// </summary>
    internal static class InputBlockerWatchdog
    {
        private static Timer _timer;

        public static void Arm(int timeoutMs)
        {
            Disarm();
            try
            {
                _timer = new Timer { Interval = Math.Max(1000, timeoutMs) };
                _timer.Tick += (s, e) =>
                {
                    Disarm();
                    try { InputBlocker.Hide(); } catch { }
                };
                _timer.Start();
            }
            catch { /* a missing watchdog must never break the export pipeline */ }
        }

        public static void Disarm()
        {
            var t = _timer;
            _timer = null;
            if (t != null) { try { t.Stop(); } catch { } try { t.Dispose(); } catch { } }
        }
    }
}
