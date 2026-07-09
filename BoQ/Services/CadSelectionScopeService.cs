using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.UI.NetworkPanel;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Drawing-selection scope picker for URBANO_BOQ_HESAPLA's "CAD Seçimi" mode.
    ///
    /// Mirrors UrbanoLock's RestoreScopeSelector: restricts the pick to the ACTIVE
    /// networks' layers via a layer-name wildcard SelectionFilter — layer filter ONLY,
    /// never combined with an entity-type filter, because some AutoCAD 2023 builds
    /// silently drop combined filters on non-ASCII / long network names. Each picked
    /// entity is classified into a manhole (circle / block reference → AG_GUID) or a
    /// pipe (line → AG_GUID + endpoints for the geometry fallback). The picked set is
    /// resolved into actual sections later, in BoQParserService.ApplySelectionScope,
    /// which needs the parsed sn/en topology.
    /// </summary>
    public static class CadSelectionScopeService
    {
        /// <summary>
        /// Prompts the user to pick manhole/pipe entities (restricted to active-network
        /// layers) and returns the raw <see cref="SelectionScope"/>, or null if the user
        /// cancelled or nothing usable was picked. <paramref name="summary"/> is a short
        /// Turkish count for the UI ("N baca, M boru seçildi").
        /// </summary>
        public static SelectionScope Prompt(Document doc, out string summary)
        {
            summary = "";
            if (doc == null) return null;
            Editor   ed = doc.Editor;
            Database db = doc.Database;

            // ── Layer filter from the active networks (net_*) ────────────────────
            // Empty active set (panel never scraped this drawing) → no filter, the
            // user can pick anything carrying an Urbano GUID (graceful degrade).
            NetworkSessionManager.InitializeFromDrawing(db);
            var active = NetworkSessionManager.GetAllNetworks(db)
                .Where(NetworkSessionManager.IsActive)
                .ToList();

            SelectionFilter filter = null;
            if (active.Count > 0)
            {
                // AutoCAD layer wildcards accept a comma-separated list in one value.
                string layerPattern = string.Join(",", active.Select(n => n + "_*"));
                filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.LayerName, layerPattern)
                });
                ed.WriteMessage(
                    $"\n[BoQ] Katman filtresi: \"{layerPattern}\" — yalnızca aktif ağ öğeleri seçilebilir.\n");
            }

            var pso = new PromptSelectionOptions
            {
                MessageForAdding            = "\nHesaplanacak boru / baca öğelerini seçin: ",
                MessageForRemoval           = "\nSeçimden çıkarmak için seçin: ",
                AllowDuplicates             = false,
                RejectObjectsOnLockedLayers = false
            };

            PromptSelectionResult psr = filter != null
                ? ed.GetSelection(pso, filter)
                : ed.GetSelection(pso);
            if (psr.Status != PromptStatus.OK || psr.Value.Count == 0) return null;

            var scope = new SelectionScope();

            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;

                    DBObject obj;
                    try { obj = tr.GetObject(so.ObjectId, OpenMode.ForRead); }
                    catch { continue; }

                    string guid = GetUrbanoGuid(obj);

                    if (obj is Line ln)
                    {
                        if (!string.IsNullOrEmpty(guid)) scope.PipeGuids.Add(guid);
                        scope.PipeSegments.Add(new[]
                        {
                            ln.StartPoint.X, ln.StartPoint.Y,
                            ln.EndPoint.X,   ln.EndPoint.Y
                        });
                    }
                    else if (obj is Circle || obj is BlockReference)
                    {
                        if (!string.IsNullOrEmpty(guid)) scope.ManholeGuids.Add(guid);
                    }
                    else if (!string.IsNullOrEmpty(guid))
                    {
                        // Label / other entity: its GUID (via AG_LAB_ENTITY) points at
                        // the owning node in the common case. Treat as a manhole GUID —
                        // harmless if it is actually a pipe's, since it then matches no
                        // node and is ignored by ApplySelectionScope.
                        scope.ManholeGuids.Add(guid);
                    }
                }
                tr.Commit();
            }

            if (scope.IsEmpty)
            {
                ed.WriteMessage("\n[BoQ] Seçilen öğelerde Urbano kimliği (AG_GUID) bulunamadı.\n");
                return null;
            }

            int pipeCount = scope.PipeGuids.Count + scope.PipeSegments.Count;
            summary = $"{scope.ManholeGuids.Count} baca, {pipeCount} boru seçildi";
            ed.WriteMessage($"\n[BoQ] {psr.Value.Count} öğe → {summary}.\n");
            return scope;
        }

        // =====================================================================
        // Self-contained XData reader (mirrors ManholeAssignCommand — no dependency
        // on UrbanoPlugin internals). Tries AG_GUID, then TOPOGUID, then AG_LAB_ENTITY.
        // =====================================================================

        private static string GetUrbanoGuid(DBObject obj)
        {
            var xd = ReadXData(obj);
            string g = XDataFirst(xd, "AG_GUID")
                    ?? XDataFirst(xd, "TOPOGUID")
                    ?? XDataFirst(xd, "AG_LAB_ENTITY");
            return string.IsNullOrEmpty(g) ? null : g.ToUpperInvariant();
        }

        private static Dictionary<string, List<TypedValue>> ReadXData(DBObject obj)
        {
            var result = new Dictionary<string, List<TypedValue>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using (ResultBuffer rb = obj.XData)
                {
                    if (rb == null) return result;
                    string app = null;
                    foreach (TypedValue tv in rb)
                    {
                        if (tv.TypeCode == (int)DxfCode.ExtendedDataRegAppName)
                        {
                            app = tv.Value?.ToString();
                            if (app != null && !result.ContainsKey(app))
                                result[app] = new List<TypedValue>();
                        }
                        else if (app != null && result.ContainsKey(app))
                        {
                            result[app].Add(tv);
                        }
                    }
                }
            }
            catch { /* entity without / with unreadable XData */ }
            return result;
        }

        private static string XDataFirst(Dictionary<string, List<TypedValue>> xd, string app)
        {
            if (xd.TryGetValue(app, out var v) && v.Count > 0)
                return v[0].Value?.ToString();
            return null;
        }
    }
}
