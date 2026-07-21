using System;
using System.Collections.Generic;
using Bricscad.ApplicationServices;
using Teigha.DatabaseServices;
using Bricscad.EditorInput;
using Teigha.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.ManholeAssignCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// UT_MANHOLE_ASSIGN — lets the user select manholes in the DWG, choose a
    /// ManholeCatalog group, and saves the mapping AG_GUID → GroupId to NOD.
    ///
    /// Stored under: URBANO_BOQ → MANHOLE_ASSIGNMENTS
    /// Used later by the BoQ engine to look up excavation parameters for each
    /// manhole when correlating DWG entities with the ARS_EXPORT_XML topology.
    /// </summary>
    public class ManholeAssignCommand
    {
        [CommandMethod("UT_MANHOLE_ASSIGN", CommandFlags.Modal)]
        public void AssignCatalogToManholes()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            try
            {
                var db = doc.Database;

                // ── 1. Catalog must exist ────────────────────────────────────
                if (!ManholeCatalogStore.HasData(db))
                {
                    ed.WriteMessage(
                        "\nKatalog bulunamadı. Önce MANHOLE_CATALOG komutu ile" +
                        " bir katalog oluşturun ve DWG'ye kaydedin.");
                    return;
                }

                var catalog = ManholeCatalogStore.Load(db);
                if (catalog == null || catalog.Groups.Count == 0)
                {
                    ed.WriteMessage(
                        "\nKatalogda hiç grup yok. MANHOLE_CATALOG ile grup ekleyin.");
                    return;
                }

                // ── 2. User selects entities ─────────────────────────────────
                var pso = new PromptSelectionOptions
                {
                    MessageForAdding  = "\nKazı kataloğu atanacak bacaları seçin: ",
                    MessageForRemoval = "\nSeçimden çıkarmak için seçin: ",
                    AllowDuplicates   = false,
                    RejectObjectsOnLockedLayers = false
                };

                var psr = ed.GetSelection(pso);
                if (psr.Status != PromptStatus.OK || psr.Value.Count == 0)
                {
                    ed.WriteMessage("\nSeçim iptal edildi.");
                    return;
                }

                // ── 3. Filter: any entity that carries AG_GUID or AG_LAB_ENTITY ──
                // Master entities  → have AG_GUID  (use it directly as manhole GUID)
                // Child/label entities → have AG_LAB_ENTITY (its value IS the manhole GUID)
                // Any entity type is accepted; duplicates are removed by GUID.
                var found      = new List<(string guid, string handle)>();
                var seenGuids  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                using (var tr = db.TransactionManager.StartTransaction())
                {
                    foreach (SelectedObject so in psr.Value)
                    {
                        if (so == null) continue;

                        DBObject obj;
                        try { obj = tr.GetObject(so.ObjectId, OpenMode.ForRead); }
                        catch { continue; }

                        var xd = ReadXData(obj);

                        string guid = XDataFirst(xd, "AG_GUID");
                        if (string.IsNullOrEmpty(guid))
                            guid = XDataFirst(xd, "AG_LAB_ENTITY");
                        if (string.IsNullOrEmpty(guid)) continue;

                        if (!seenGuids.Add(guid.ToUpperInvariant())) continue;

                        found.Add((guid.ToUpperInvariant(), obj.Handle.ToString()));
                    }
                    tr.Commit();
                }

                ed.WriteMessage(
                    $"\n{psr.Value.Count} entity seçildi, " +
                    $"{found.Count} tanesi Urbano bacası olarak tanındı.");

                // ── 4. Show assignment dialog ─────────────────────────────────
                ManholeTemplateGroup chosenGroup;
                using (var dlg = new ManholeAssignDialog(found.Count, catalog.Groups))
                {
                    var dr = Application.ShowModalDialog(dlg);
                    if (dr != System.Windows.Forms.DialogResult.OK ||
                        dlg.SelectedGroup == null)
                    {
                        ed.WriteMessage("\nİptal edildi.");
                        return;
                    }
                    chosenGroup = dlg.SelectedGroup;
                }

                if (found.Count == 0)
                {
                    ed.WriteMessage("\nAtanacak baca yok — işlem tamamlandı.");
                    return;
                }

                // ── 5. Build assignments and merge into NOD ───────────────────
                var incoming = new List<ManholeAssignment>(found.Count);
                foreach (var (guid, handle) in found)
                    incoming.Add(new ManholeAssignment
                    {
                        AgGuid  = guid,
                        Handle  = handle,
                        GroupId = chosenGroup.Id
                    });

                ManholeAssignStore.Merge(db, incoming);

                ed.WriteMessage(
                    $"\n✓  {incoming.Count} baca → \"{chosenGroup.Name}\" " +
                    $"({(chosenGroup.Mode == "Dynamic" ? "Dinamik" : "Sabit")}) " +
                    $"olarak DWG'ye kaydedildi.");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\nUT_MANHOLE_ASSIGN hata: {ex.Message}");
            }
        }

        // =====================================================================
        // XData helpers (self-contained — no dependency on UrbanoPlugin internals)
        // =====================================================================

        private static Dictionary<string, List<TypedValue>> ReadXData(DBObject obj)
        {
            var result = new Dictionary<string, List<TypedValue>>(
                StringComparer.OrdinalIgnoreCase);
            using (var rb = obj.XData)
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
                    else if (app != null)
                        result[app].Add(tv);
                }
            }
            return result;
        }

        private static string XDataFirst(
            Dictionary<string, List<TypedValue>> xd, string app)
        {
            List<TypedValue> v;
            return xd.TryGetValue(app, out v) && v.Count > 0
                ? v[0].Value?.ToString()
                : null;
        }

    }
}
