using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Bricscad.ApplicationServices;
using Teigha.Runtime;
using Bricscad.Windows;
using Exception = System.Exception;
using AcApp = Bricscad.ApplicationServices.Application;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.UI.NetworkPanel.NetworkPaletteSet.NetworkPanelCommand))]

namespace UrbanoMetraj.BoQ.UI.NetworkPanel
{
    /// <summary>
    /// Manages the lifecycle of UrbanoMetraj's own copy of the network-selection
    /// PaletteSet — a clone of UrbanoLock.UI.NetworkPaletteSet.
    ///
    /// Only ever instantiated when UrbanoLock's own palette isn't available in the
    /// current AutoCAD session (see <see cref="UrbanoLockBridge"/>, checked by the
    /// UT_NET_PANEL command below). This guarantees a single network
    /// panel exists per session even when both plugins are netloaded together:
    /// whichever one is present takes ownership, and the other one's command
    /// just shows/refreshes that same palette instead of creating its own.
    ///
    /// Same fixed GUID as UrbanoLock's palette so docking position/size persist
    /// identically regardless of which plugin ends up owning the window on a
    /// given machine.
    /// </summary>
    public static class NetworkPaletteSet
    {
        // Same GUID as UrbanoLock.UI.NetworkPaletteSet — intentional, see remarks above.
        private static readonly System.Guid PaletteGuid =
            new System.Guid("A1B2C3D4-E5F6-7890-ABCD-EF1234567891");

        private static PaletteSet            _ps;
        private static NetworkPaletteControl  _control;
        private static bool                   _documentHookAttached;

        // ── Public API ────────────────────────────────────────────────────────

        /// <summary>
        /// Creates (first call) or makes visible the network palette.
        /// Safe to call repeatedly — idempotent after first creation.
        /// </summary>
        public static void EnsureVisible()
        {
            if (_ps == null)
                Create();

            if (_ps != null)
                _ps.Visible = true;
        }

        /// <summary>
        /// Refreshes the network list from the currently active document.
        /// </summary>
        public static void RefreshFromCurrentDocument()
        {
            if (_control == null) return;

            var doc = AcApp.DocumentManager.MdiActiveDocument;
            var db  = doc?.Database;

            var networks = db != null
                ? NetworkSessionManager.GetAllNetworks(db)
                : new List<string>();

            NetworkSessionManager.InitializeFromDrawing(db);

            if (_control.Dispatcher.CheckAccess())
                _control.LoadNetworks(networks);
            else
                _control.Dispatcher.BeginInvoke(
                    new System.Action(() => _control.LoadNetworks(networks)));
        }

        // ── Creation ──────────────────────────────────────────────────────────

        private static void Create()
        {
            try
            {
                _ps = new PaletteSet("Urbano — Ağ Seçimi", PaletteGuid)
                {
                    DockEnabled = DockSides.Left | DockSides.Right | DockSides.None,
                    Dock        = DockSides.Right,
                    MinimumSize = new Size(220, 120),
                    Style       = PaletteSetStyles.ShowAutoHideButton
                               | PaletteSetStyles.ShowCloseButton
                               | PaletteSetStyles.Snappable
                };

                // ── WPF control wrapped in ElementHost ────────────────────────
                _control = new NetworkPaletteControl();

                var host = new ElementHost
                {
                    Dock  = DockStyle.Fill,
                    Child = _control
                };

                _ps.Add("Ağlar", host);

                // ── Populate from current drawing ──────────────────────────────
                var doc      = AcApp.DocumentManager.MdiActiveDocument;
                var networks = doc != null
                    ? NetworkSessionManager.GetAllNetworks(doc.Database)
                    : new List<string>();

                NetworkSessionManager.InitializeFromDrawing(doc?.Database);
                _control.LoadNetworks(networks);

                // ── Subscribe to document events ───────────────────────────────
                if (!_documentHookAttached)
                {
                    AcApp.DocumentManager.DocumentActivated += OnDocumentActivated;
                    AcApp.DocumentManager.DocumentCreated   += OnDocumentCreated;
                    _documentHookAttached = true;
                }

                _ps.Visible = true;
            }
            catch (Exception ex)
            {
                try
                {
                    AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(
                        $"\n[UrbanoMetraj] NetworkPaletteSet.Create() hata: {ex.Message}\n");
                }
                catch { }
                _ps      = null;
                _control = null;
            }
        }

        // ── Event handlers ────────────────────────────────────────────────────

        private static void OnDocumentActivated(object sender, DocumentCollectionEventArgs e)
        {
            RefreshFromCurrentDocument();
        }

        private static void OnDocumentCreated(object sender, DocumentCollectionEventArgs e)
        {
            RefreshFromCurrentDocument();
        }

        // ── UT_NET_PANEL command ──────────────────────────────────────

        /// <summary>
        /// AutoCAD command class that exposes UT_NET_PANEL — UrbanoMetraj's
        /// entry point to the (shared) network-selection panel. Named differently
        /// from UrbanoLock's UT_NETWORK_PANEL because two loaded assemblies can't
        /// register the same AutoCAD command name; both point at the same panel.
        /// </summary>
        public class NetworkPanelCommand
        {
            [CommandMethod("UT_NET_PANEL", CommandFlags.Modal)]
            public void ShowNetworkPanel()
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument;
                if (doc == null) return;

                try
                {
                    // If UrbanoLock's own palette is loaded in this session, reuse it
                    // instead of creating a second network panel.
                    if (UrbanoLockBridge.TryShowExisting())
                    {
                        doc.Editor.WriteMessage(
                            "\n[UrbanoMetraj] Ağ seçim paleti (UrbanoLock) açıldı.\n");
                        return;
                    }

                    EnsureVisible();
                    RefreshFromCurrentDocument();
                    doc.Editor.WriteMessage(
                        "\n[UrbanoMetraj] Ağ seçim paleti açıldı.\n");
                }
                catch (Exception ex)
                {
                    doc.Editor.WriteMessage(
                        $"\n[UrbanoMetraj] UT_NET_PANEL — hata: {ex.Message}\n");
                }
            }
        }
    }
}
