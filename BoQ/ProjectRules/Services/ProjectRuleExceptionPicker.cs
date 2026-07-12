using System;
using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.ProjectRules.Models;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.ProjectRules.Services.ProjectRuleExceptionPicker))]

namespace UrbanoMetraj.BoQ.ProjectRules.Services
{
    /// <summary>
    /// Bridges the modeless Proje Ayarları window to an interactive drawing pick. A WPF button
    /// handler can't call <c>Editor.GetSelection</c> directly (no command/document context), so we
    /// queue a modal command via SendStringToExecute — the same pattern UT_BOQ_CADSELECT uses — and
    /// hand the picked AG_GUIDs back to the ViewModel through a stored continuation, on the main
    /// thread, once the pick completes.
    /// </summary>
    public class ProjectRuleExceptionPicker
    {
        private static Action<List<string>> _pending;
        private static string _prompt;
        private static ExceptionEntityKind _kind;
        private static string _layerNet;

        /// <summary>
        /// Queues an entity pick scoped to one network. The selection is LAYER-filtered to
        /// "<paramref name="layerNetwork"/>_*" (so only that network's entities are pickable, regardless
        /// of the panel's active state); the entity TYPE (pipes = line/polyline, manholes = block/circle;
        /// text ignored) is checked AFTER the pick — this split avoids the combined-filter drop some
        /// AutoCAD 2023 builds hit on non-ASCII / long layer names. <paramref name="onPicked"/> is
        /// invoked on the main thread with the distinct AG_GUIDs, or empty if cancelled.
        /// </summary>
        public static void RequestPick(string prompt, ExceptionEntityKind kind, string layerNetwork,
                                       Action<List<string>> onPicked)
        {
            if (onPicked == null) return;
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) { onPicked(new List<string>()); return; }

            _prompt   = prompt;
            _kind     = kind;
            _layerNet = layerNetwork;
            _pending  = onPicked;
            doc.SendStringToExecute("UT_PROJE_ISTISNA_SEC\n", true, false, true);
        }

        [CommandMethod("UT_PROJE_ISTISNA_SEC", CommandFlags.Modal)]
        public void PickException()
        {
            var cb = _pending;
            _pending = null;
            if (cb == null) return;

            var guids = new List<string>();
            try
            {
                var doc = Application.DocumentManager.MdiActiveDocument;
                if (doc != null)
                    guids = PickAgGuids(doc, _prompt ?? "\nİstisna uygulanacak öğeleri seçin: ", _kind, _layerNet);
            }
            catch (Exception ex)
            {
                Application.DocumentManager.MdiActiveDocument?
                    .Editor.WriteMessage("\nUT_PROJE_ISTISNA_SEC hata: " + ex.Message);
            }

            // We are on the main thread inside the command — safe to touch the WPF ViewModel.
            try { cb(guids); } catch { /* VM refresh failure is non-fatal */ }
        }

        // =====================================================================
        // Interactive pick + AG_GUID extraction (mirrors CadSelectionScopeService)
        // =====================================================================

        private static List<string> PickAgGuids(Document doc, string prompt, ExceptionEntityKind kind, string layerNet)
        {
            var result    = new List<string>();
            var seen      = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Editor   ed   = doc.Editor;
            Database db   = doc.Database;

            var pso = new PromptSelectionOptions
            {
                MessageForAdding            = prompt,
                MessageForRemoval           = "\nSeçimden çıkarmak için seçin: ",
                AllowDuplicates             = false,
                RejectObjectsOnLockedLayers = false
            };

            // LAYER filter only (scopes the pick to this network's "<net>_*" layers) — never combined
            // with an entity-type filter, which some AutoCAD 2023 builds silently drop on long /
            // non-ASCII layer names. The type is enforced below, after the pick.
            PromptSelectionResult psr;
            if (!string.IsNullOrEmpty(layerNet))
            {
                var filter = new SelectionFilter(new[]
                {
                    new TypedValue((int)DxfCode.LayerName, layerNet + "_*")
                });
                ed.WriteMessage($"\n[BoQ] Katman filtresi: \"{layerNet}_*\" — yalnızca bu ağın öğeleri seçilebilir.");
                psr = ed.GetSelection(pso, filter);
            }
            else
            {
                psr = ed.GetSelection(pso);
            }
            if (psr.Status != PromptStatus.OK || psr.Value.Count == 0) return result;

            int wrongType = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                foreach (SelectedObject so in psr.Value)
                {
                    if (so == null) continue;
                    DBObject obj;
                    try { obj = tr.GetObject(so.ObjectId, OpenMode.ForRead); }
                    catch { continue; }

                    // Entity-type check (post-pick): pipes = Line/Polyline, manholes = block/circle.
                    // Text and anything else is ignored before the AG_GUID is stored.
                    if (!IsKindMatch(obj, kind)) { wrongType++; continue; }

                    string guid = GetUrbanoGuid(obj);
                    if (string.IsNullOrEmpty(guid)) continue;
                    if (seen.Add(guid)) result.Add(guid);
                }
                tr.Commit();
            }

            ed.WriteMessage($"\n[BoQ] {psr.Value.Count} öğe seçildi, {result.Count} uygun Urbano kimliği bulundu" +
                            (wrongType > 0 ? $" ({wrongType} yanlış tür yok sayıldı)." : "."));
            return result;
        }

        private static bool IsKindMatch(DBObject obj, ExceptionEntityKind kind)
        {
            if (kind == ExceptionEntityKind.Pipe)
                return obj is Line || obj is Polyline || obj is Polyline2d || obj is Polyline3d;
            return obj is BlockReference || obj is Circle;
        }

        private static string GetUrbanoGuid(DBObject obj)
        {
            // AG_GUID / TOPOGUID only — NOT AG_LAB_ENTITY: that lives on label texts, which this
            // picker deliberately excludes (pipes/manholes carry their own AG_GUID directly).
            var xd = ReadXData(obj);
            string g = XDataFirst(xd, "AG_GUID")
                    ?? XDataFirst(xd, "TOPOGUID");
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
