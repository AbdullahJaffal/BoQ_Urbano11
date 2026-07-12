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

        // ── Layer visibility (cache-driven) ───────────────────────────────────

        private static void ApplyLayerVisibility(string networkId, bool visible)
        {
            if (string.IsNullOrEmpty(networkId)) return;
            ApplyLayerVisibilityBatch(new[] { networkId }, visible);
        }

        /// <summary>
        /// Turns the layers of every supplied network on/off inside a <b>single</b>
        /// document lock + transaction, followed by <b>one</b> Editor.Regen() at the
        /// end. This is what keeps the [TÜMÜ] master toggle cheap: the old per-network
        /// path opened a fresh transaction and issued a Regen for each network, so
        /// N networks meant N full-drawing regens — the visible flicker/heaviness.
        /// The layer outcome is identical to calling the single-network path per id.
        /// </summary>
        private static void ApplyLayerVisibilityBatch(IEnumerable<string> networkIds, bool visible)
        {
            if (networkIds == null) return;

            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var db = doc.Database;

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    bool anyChanged = false;

                    foreach (var networkId in networkIds)
                    {
                        if (string.IsNullOrEmpty(networkId)) continue;
                        if (!_layerCache.TryGetValue(networkId, out var layerIds) || layerIds.Count == 0)
                            continue;

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

                    tr.Commit();
                    if (anyChanged) try { doc.Editor.Regen(); } catch { }
                }
            }
            catch { /* best-effort; silently skip if document is busy */ }
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

        private static void SaveUiStateToNod(Database db)
        {
            if (db == null) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;

            string knownCsv   = string.Join(",", _known);
            string activeCsv  = string.Join(",", _active);
            string visibleCsv = string.Join(",", _visible);

            try
            {
                using (doc.LockDocument())
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    WriteXrecord(tr, db, NodUiStateKey,
                        new TypedValue((int)DxfCode.Text, "KNOWN="   + knownCsv),
                        new TypedValue((int)DxfCode.Text, "ACTIVE="  + activeCsv),
                        new TypedValue((int)DxfCode.Text, "VISIBLE=" + visibleCsv));
                    tr.Commit();
                }
            }
            catch { /* non-fatal */ }
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

            if (!_suppressSave)
                SaveUiStateToNod(Application.DocumentManager.MdiActiveDocument?.Database);
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
