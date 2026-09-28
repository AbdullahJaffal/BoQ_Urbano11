using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;

namespace UrbanoMetraj.BoQ.UI.NetworkPanel
{
    /// <summary>
    /// Per-session network state for UrbanoMetraj's clone of the UrbanoLock network
    /// palette. Trimmed port of UrbanoLock.UI.UrbanoSessionManager: keeps the
    /// Active/Visible toggle state and the layer-visibility cache (both generic
    /// AutoCAD behaviour), but drops everything tied to UrbanoLock-only features
    /// that don't exist here (vault/lock, native Urbano ComboBox sync, per-network
    /// system-type tagging).
    ///
    ///   Active  = checkbox checked  → reserved for future BoQ-side filtering.
    ///   Visible = eye icon on       → this network's AutoCAD layers are turned on.
    ///
    /// Deliberately shares NOD keys with UrbanoLock (URBANO_NETWORKS,
    /// URBANOLOCK_UI_STATE) so the network list and the checkbox/eye state stay
    /// consistent for a given drawing no matter which plugin's palette instance
    /// is currently showing it.
    /// </summary>
    public static class NetworkSessionManager
    {
        // ── In-memory state ───────────────────────────────────────────────────

        private static readonly HashSet<string> _known =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _active =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _visible =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Cache: network ID → ObjectIds of every AutoCAD layer that belongs to it.
        private static readonly Dictionary<string, List<ObjectId>> _layerCache =
            new Dictionary<string, List<ObjectId>>(StringComparer.OrdinalIgnoreCase);

        private static bool _suppressSave;

        /// <summary>Raised on any Active or Visible state change.</summary>
        public static event EventHandler StateChanged;

        // ── Read accessors ────────────────────────────────────────────────────

        public static bool IsActive(string networkId) =>
            !string.IsNullOrEmpty(networkId) && _active.Contains(networkId);

        public static bool IsVisible(string networkId) =>
            !string.IsNullOrEmpty(networkId) && _visible.Contains(networkId);

        // ── State mutators ────────────────────────────────────────────────────

        public static void SetActive(string networkId, bool active)
        {
            if (string.IsNullOrEmpty(networkId)) return;

            bool changed = active ? _active.Add(networkId) : _active.Remove(networkId);

            if (active && changed)
            {
                if (_visible.Add(networkId))
                    ApplyLayerVisibility(networkId, true);
            }
            else if (!active)
            {
                if (_visible.Remove(networkId))
                    ApplyLayerVisibility(networkId, false);
            }

            if (changed) RaiseChanged();
        }

        public static void SetVisible(string networkId, bool visible)
        {
            if (string.IsNullOrEmpty(networkId)) return;

            bool changed = visible ? _visible.Add(networkId) : _visible.Remove(networkId);

            if (changed)
            {
                ApplyLayerVisibility(networkId, visible);
                RaiseChanged();
            }
        }

        public static void SetAllActive(IEnumerable<string> networkIds, bool active)
        {
            var ids = networkIds?.ToList() ?? new List<string>();

            // Collect only the networks whose visibility actually flips, so the
            // batch below toggles exactly the same layers the per-network path did.
            var toToggle = new List<string>();
            foreach (var id in ids)
            {
                if (active)
                {
                    _active.Add(id);
                    if (_visible.Add(id)) toToggle.Add(id);
                }
                else
                {
                    _active.Remove(id);
                    if (_visible.Remove(id)) toToggle.Add(id);
                }
            }

            // Single transaction + single Regen for the whole set (see remarks on
            // ApplyLayerVisibilityBatch) — avoids the N-Regen flicker on [TÜMÜ].
            ApplyLayerVisibilityBatch(toToggle, active);
            RaiseChanged();
        }

        public static void SetAllVisible(IEnumerable<string> networkIds, bool visible)
        {
            var ids = networkIds?.ToList() ?? new List<string>();
            foreach (var id in ids)
            {
                if (visible) _visible.Add(id);
                else         _visible.Remove(id);
            }

            // One transaction + one Regen for all networks at once.
            ApplyLayerVisibilityBatch(ids, visible);
            RaiseChanged();
        }

        // ── Drawing initialisation ────────────────────────────────────────────

        /// <summary>
        /// Restores Active + Visible state for the given drawing from the shared
        /// URBANOLOCK_UI_STATE NOD entry, defaulting newly-seen networks to
        /// Active + Visible. Also (re)builds the layer cache.
        /// </summary>
        public static void InitializeFromDrawing(Database db)
        {
            // Land any queued palette changes on their drawing before the state
            // and layer cache are replaced.
            FlushPending();

            _suppressSave = true;
            try
            {
                _known.Clear();
                _active.Clear();
                _visible.Clear();
                _layerCache.Clear();

                var all   = GetAllNetworks(db);
                var saved = LoadUiStateFromNod(db);

                foreach (var n in all)
                {
                    _known.Add(n);

                    bool wasKnown  = saved != null && saved.Known.Contains(n);
                    bool isActive  = !wasKnown || saved.Active.Contains(n);
                    bool isVisible = !wasKnown || saved.Visible.Contains(n);

                    if (isActive)  _active.Add(n);
                    if (isVisible) _visible.Add(n);
                }

                if (db != null) RebuildLayerCache(db, all);

                RaiseChanged();
            }
            finally
            {
                _suppressSave = false;
            }
        }

        /// <summary>
        /// Returns all distinct network IDs for the drawing, read from the shared
        /// URBANO_NETWORKS NOD entry (written by UrbanoLock's scrape / palette
        /// Refresh). Returns an empty list until UrbanoLock has scraped this
        /// drawing at least once — UrbanoMetraj has no scraper of its own.
        /// </summary>
        public static List<string> GetAllNetworks(Database db)
        {
            if (db == null) return new List<string>();
            return LoadNetworksFromNod(db);
        }

        /// <summary>
        /// Resolves the Active set straight from the shared URBANOLOCK_UI_STATE NOD entry — WITHOUT
        /// touching in-memory state, the layer cache or visibility (no side effects). Use this from
        /// readers (e.g. the Proje Kurulumu tab) that must reflect the panel's active checkboxes even
        /// when the live palette belongs to UrbanoLock and this plugin's in-memory <c>_active</c> set
        /// was never populated. Mirrors <see cref="InitializeFromDrawing"/>'s default rule: a network
        /// not present in the saved Known set (or no saved state at all) counts as Active.
        /// </summary>
        public static HashSet<string> ResolveActiveFromNod(Database db, IEnumerable<string> allNetworks)
        {
            var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var all    = allNetworks?.ToList() ?? new List<string>();
            var saved  = LoadUiStateFromNod(db);
            foreach (var n in all)
            {
                bool wasKnown = saved != null && saved.Known.Contains(n);
                bool isActive = !wasKnown || saved.Active.Contains(n);
                if (isActive) result.Add(n);
            }
            return result;
        }

        // ── Layer cache ───────────────────────────────────────────────────────

        /// <summary>
        /// Scans the layer table once and maps each layer to its owning network
        /// using a longest-prefix-match algorithm (identical logic to UrbanoLock's).
        /// </summary>
        public static void RebuildLayerCache(Database db, List<string> networks)
        {
            _layerCache.Clear();
            if (db == null || networks == null || networks.Count == 0) return;

            foreach (var n in networks)
                _layerCache[n] = new List<ObjectId>();

            var sortedNetworks = networks
                .OrderByDescending(n => n.Length)
                .ToList();

            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);

                    foreach (ObjectId layerId in lt)
                    {
                        if (layerId.IsNull || !layerId.IsValid) continue;

                        LayerTableRecord lr;
                        try   { lr = tr.GetObject(layerId, OpenMode.ForRead) as LayerTableRecord; }
                        catch { continue; }
                        if (lr == null || lr.IsDependent || lr.Name.IndexOf('|') >= 0) continue;

                        string layerName = lr.Name;

                        foreach (var net in sortedNetworks)
                        {
                            if (layerName.Length > net.Length + 1 &&
                                layerName[net.Length] == '_' &&
                                layerName.StartsWith(net, StringComparison.OrdinalIgnoreCase))
                            {
                                _layerCache[net].Add(layerId);
                                break;
                            }
                        }
                    }
                }
            }
            catch { /* cache build failure is non-fatal; toggles will be no-ops */ }
        }

        // ── Layer visibility (cache-driven, DEFERRED) ─────────────────────────

        private static void ApplyLayerVisibility(string networkId, bool visible)
        {
            if (string.IsNullOrEmpty(networkId)) return;
            ApplyLayerVisibilityBatch(new[] { networkId }, visible);
        }

        /// <summary>
        /// Queues the layers of EVERY supplied network to be turned on/off. Nothing
        /// touches the drawing here: palette clicks only change the in-memory state
        /// and the palette rows, and the drawing is updated in ONE batch by
        /// <see cref="FlushPending"/> (see "Deferred apply" below). Last write per
        /// network wins, so on/off/on between two flushes costs nothing.
        /// </summary>
        private static void ApplyLayerVisibilityBatch(IEnumerable<string> networkIds, bool visible)
        {
            if (networkIds == null) return;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            bool any = false;
            foreach (var networkId in networkIds)
            {
                if (string.IsNullOrEmpty(networkId)) continue;
                _pendingLayers[networkId] = visible;
                any = true;
            }
            if (any) MarkPending(doc);
        }

        // ── Deferred apply (palette → drawing) ────────────────────────────────
        //
        // Why: the old path committed the layer change AND issued a full
        // Editor.Regen() on EVERY palette click. Regen rebuilds the display of
        // every entity in the drawing (tens of thousands in an Urbano DWG), so
        // each click froze AutoCAD. Turning a layer on/off does not need a Regen
        // (only freeze/thaw does) — a screen update is enough.
        //
        // How: clicks only queue work. FlushPending pushes all queued layer
        // changes + the NOD UI-state save in ONE lock + transaction, followed by
        // ONE UpdateScreen, when the first of these happens:
        //   • the mouse leaves the palette (NetworkPaletteControl.MouseLeave) —
        //     i.e. before the user can click anywhere in the drawing;
        //   • an idle safety-net timer fires while the pointer is NOT over the
        //     palette (covers a missed MouseLeave);
        //   • any command is about to start (CommandWillStart) — so commands and
        //     sibling plugins reading URBANOLOCK_UI_STATE always see fresh state;
        //   • the document is being deactivated or closed;
        //   • the palette is re-initialised for another drawing.
        // Same mechanism as UrbanoLock.UI.UrbanoSessionManager.

        // Network → desired visibility, waiting to be pushed to the drawing.
        private static readonly Dictionary<string, bool> _pendingLayers =
            new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        // UI state (Known/Active/Visible) waiting to be written to the NOD.
        private static bool _pendingSave;

        // The document the pending work belongs to (the layer cache is per drawing).
        private static Document _pendingDoc;

        private static bool _flushing;

        private static System.Windows.Threading.DispatcherTimer _flushTimer;
        private const int FlushIdleMs = 1500;

        private static readonly HashSet<Document> _hookedDocs = new HashSet<Document>();
        private static bool _docManagerHooked;

        /// <summary>
        /// Set by the palette control: returns true while the pointer is over the
        /// palette. The idle timer never flushes while this is true, so the drawing
        /// only updates once the user has left the panel.
        /// </summary>
        public static Func<bool> IsPointerOverPanel;

        /// <summary>True when palette changes are queued but not yet in the drawing.</summary>
        public static bool HasPending => _pendingDoc != null;

        private static void MarkPending(Document doc)
        {
            // Pending work belongs to exactly one drawing — push the old batch first.
            if (_pendingDoc != null && _pendingDoc != doc) FlushPending();

            _pendingDoc = doc;
            EnsureDocumentHooks(doc);

            if (_flushTimer == null)
            {
                _flushTimer = new System.Windows.Threading.DispatcherTimer
                {
                    Interval = TimeSpan.FromMilliseconds(FlushIdleMs)
                };
                _flushTimer.Tick += OnFlushTimerTick;
            }
            _flushTimer.Stop();
            _flushTimer.Start();
        }

        private static void OnFlushTimerTick(object sender, EventArgs e)
        {
            bool overPanel = false;
            try { overPanel = IsPointerOverPanel?.Invoke() ?? false; } catch { }
            if (overPanel) return;   // keep waiting — MouseLeave will flush
            FlushPending();
        }

        /// <summary>
        /// Pushes every queued palette change to the drawing: layer on/off for the
        /// queued networks and the NOD UI-state save, in one document lock + one
        /// transaction, followed by one cheap screen update (no Regen).
        /// Safe to call at any time; a no-op when nothing is pending.
        /// </summary>
        public static void FlushPending()
        {
            if (_flushing) return;
            _flushTimer?.Stop();

            var doc = _pendingDoc;
            if (doc == null) return;

            // Snapshot + clear first so a re-entrant call is a no-op.
            var layers = new Dictionary<string, bool>(_pendingLayers, StringComparer.OrdinalIgnoreCase);
            bool save  = _pendingSave;
            _pendingLayers.Clear();
            _pendingSave = false;
            _pendingDoc  = null;

            if (!IsDocumentOpen(doc)) return;
            if (layers.Count == 0 && !save) return;

            _flushing = true;
            try
            {
                var db = doc.Database;
                bool anyChanged = false;

                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    foreach (var kv in layers)
                    {
                        if (!_layerCache.TryGetValue(kv.Key, out var layerIds) || layerIds.Count == 0)
                            continue;

                        bool visible = kv.Value;
                        foreach (var id in layerIds)
                        {
                            if (id.IsNull || !id.IsValid || id.IsErased) continue;

                            LayerTableRecord lr;
                            try   { lr = tr.GetObject(id, OpenMode.ForRead) as LayerTableRecord; }
                            catch { continue; }
                            if (lr == null) continue;

                            if (lr.IsOff == visible)
                            {
                                lr = tr.GetObject(id, OpenMode.ForWrite) as LayerTableRecord;
                                if (lr != null) { lr.IsOff = !visible; anyChanged = true; }
                            }
                        }
                    }

                    if (save) WriteUiState(tr, db);

                    tr.Commit();
                }

                // On/off only needs a redraw, not a Regen (the UrbanoLabels
                // UpdateScreen approach). Regen is reserved for freeze/thaw.
                if (anyChanged)
                {
                    try { doc.Editor.UpdateScreen(); } catch { }
                    try { Application.UpdateScreen(); } catch { }
                }
            }
            catch { /* best-effort; silently skip if document is busy */ }
            finally
            {
                _flushing = false;
            }
        }

        private static bool IsDocumentOpen(Document doc)
        {
            if (doc == null) return false;
            try
            {
                foreach (Document d in Application.DocumentManager)
                    if (d == doc) return !doc.IsDisposed;
            }
            catch { }
            return false;
        }

        private static void EnsureDocumentHooks(Document doc)
        {
            if (!_docManagerHooked)
            {
                var dm = Application.DocumentManager;
                dm.DocumentToBeDeactivated += (s, e) => FlushPending();
                dm.DocumentToBeDestroyed   += OnDocumentToBeDestroyed;
                _docManagerHooked = true;
            }

            if (doc != null && _hookedDocs.Add(doc))
            {
                doc.CommandWillStart   += (s, e) => FlushPending();
                doc.BeginDocumentClose += (s, e) => FlushPending();
            }
        }

        private static void OnDocumentToBeDestroyed(object sender, DocumentCollectionEventArgs e)
        {
            if (e.Document == null) return;
            // The drawing is going away — its queued work has nowhere to land.
            if (e.Document == _pendingDoc)
            {
                _flushTimer?.Stop();
                _pendingLayers.Clear();
                _pendingSave = false;
                _pendingDoc  = null;
            }
            _hookedDocs.Remove(e.Document);
        }

        // ── NOD: network list (shared with UrbanoLock) ────────────────────────

        private const string NodNetworksKey = "URBANO_NETWORKS";

        public static List<string> LoadNetworksFromNod(Database db)
        {
            if (db == null) return new List<string>();
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var tv = ReadFirstXrecordValue(tr, db, NodNetworksKey, (int)DxfCode.Text);
                    if (tv == null) return new List<string>();

                    string csv = (tv.Value.Value as string) ?? "";
                    return csv
                        .Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(s => s.Trim())
                        .Where(s => !string.IsNullOrEmpty(s))
                        .ToList();
                }
            }
            catch { }
            return new List<string>();
        }

        // ── NOD: UI state (shared with UrbanoLock) ────────────────────────────

        private const string NodUiStateKey = "URBANOLOCK_UI_STATE";

        /// <summary>
        /// Writes the current checkbox + eye state to the shared NOD entry. Runs
        /// inside the <see cref="FlushPending"/> transaction. Caller commits.
        /// </summary>
        private static void WriteUiState(Transaction tr, Database db)
        {
            WriteXrecord(tr, db, NodUiStateKey,
                new TypedValue((int)DxfCode.Text, "KNOWN="   + string.Join(",", _known)),
                new TypedValue((int)DxfCode.Text, "ACTIVE="  + string.Join(",", _active)),
                new TypedValue((int)DxfCode.Text, "VISIBLE=" + string.Join(",", _visible)));
        }

        private static UiStateRecord LoadUiStateFromNod(Database db)
        {
            if (db == null) return null;
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var nod = (DBDictionary)tr.GetObject(
                        db.NamedObjectsDictionaryId, OpenMode.ForRead);
                    if (!nod.Contains(NodUiStateKey)) return null;

                    var xrec = (Xrecord)tr.GetObject(
                        nod.GetAt(NodUiStateKey), OpenMode.ForRead);
                    if (xrec?.Data == null) return null;

                    var record = new UiStateRecord();
                    foreach (TypedValue tv in xrec.Data)
                    {
                        if (tv.TypeCode != (int)DxfCode.Text) continue;
                        string line = tv.Value as string ?? "";

                        if (line.StartsWith("KNOWN=",   StringComparison.Ordinal))
                            ParseCsvInto(line.Substring(6),  record.Known);
                        else if (line.StartsWith("ACTIVE=",  StringComparison.Ordinal))
                            ParseCsvInto(line.Substring(7),  record.Active);
                        else if (line.StartsWith("VISIBLE=", StringComparison.Ordinal))
                            ParseCsvInto(line.Substring(8), record.Visible);
                    }

                    return record.Known.Count > 0 ? record : null;
                }
            }
            catch { }
            return null;
        }

        // ── Private helpers ───────────────────────────────────────────────────

        private static void RaiseChanged()
        {
            StateChanged?.Invoke(null, EventArgs.Empty);

            // UI state is queued for the NOD and written at the next FlushPending.
            if (_suppressSave) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            _pendingSave = true;
            MarkPending(doc);
        }

        private static void WriteXrecord(
            Transaction tr, Database db, string key, params TypedValue[] values)
        {
            var nodRo = (DBDictionary)tr.GetObject(
                db.NamedObjectsDictionaryId, OpenMode.ForRead);

            Xrecord xrec;
            if (nodRo.Contains(key))
            {
                xrec = (Xrecord)tr.GetObject(nodRo.GetAt(key), OpenMode.ForWrite);
            }
            else
            {
                var nodRw = (DBDictionary)tr.GetObject(
                    db.NamedObjectsDictionaryId, OpenMode.ForWrite);
                xrec = new Xrecord();
                nodRw.SetAt(key, xrec);
                tr.AddNewlyCreatedDBObject(xrec, true);
            }

            xrec.Data = new ResultBuffer(values);
        }

        private static TypedValue? ReadFirstXrecordValue(
            Transaction tr, Database db, string key, int dxfCode)
        {
            var nod = (DBDictionary)tr.GetObject(
                db.NamedObjectsDictionaryId, OpenMode.ForRead);
            if (!nod.Contains(key)) return null;

            var xrec = (Xrecord)tr.GetObject(nod.GetAt(key), OpenMode.ForRead);
            if (xrec?.Data == null) return null;

            foreach (TypedValue tv in xrec.Data)
                if (tv.TypeCode == dxfCode) return tv;

            return null;
        }

        private static void ParseCsvInto(string csv, HashSet<string> target)
        {
            foreach (var part in csv.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                string s = part.Trim();
                if (!string.IsNullOrEmpty(s)) target.Add(s);
            }
        }

        // ── Private types ─────────────────────────────────────────────────────

        private class UiStateRecord
        {
            public readonly HashSet<string> Known =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Active =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            public readonly HashSet<string> Visible =
                new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
