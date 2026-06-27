using System;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SectionsCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// AutoCAD command: URBANO_SECTIONS
    ///
    /// Draws 2-D cross-section profiles for the selected pipe network, placed
    /// side by side from a user-picked insertion point.  Respects the Kazı/Dolgu
    /// overlap preferences and the CrossSectionInterval setting.
    /// Crossing-boundary stations are always included as hard stops.
    /// </summary>
    public class SectionsCommand
    {
        // One-shot overrides set by BoQResultsDialog before queuing the command.
        public static OverlapAssignment? OverrideExcavation;
        public static OverlapAssignment? OverrideBackfill;
        public static double             OverrideCrossSectionInterval = 5.0;

        [CommandMethod("URBANO_SECTIONS", CommandFlags.Modal)]
        public void DrawSections()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage(
                    "\n[Sections] DWG'de BoQ verisi yok. Önce URBANO_BOQ komutunu çalıştırın.\n");
                return;
            }

            BoQReport   report;
            BoQSettings settings;
            try
            {
                (report, settings) = DwgBoQStore.Load(doc.Database);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Sections ERROR] Veri yüklenemedi: {ex.Message}\n");
                return;
            }

            if (report?.SectionDebug == null || report.SectionDebug.Count == 0)
            {
                ed.WriteMessage("\n[Sections] Kesit verisi bulunamadı.\n");
                return;
            }

            // Consume one-shot overrides.
            TiePreference kazi  = BoQScenarioAggregator.Map(
                OverrideExcavation ?? settings?.ExcavationOverlap ?? OverlapAssignment.Split);
            TiePreference dolgu = BoQScenarioAggregator.Map(
                OverrideBackfill   ?? settings?.BackfillOverlap   ?? OverlapAssignment.Split);
            double interval = OverrideCrossSectionInterval > 0
                ? OverrideCrossSectionInterval
                : settings?.CrossSectionInterval ?? 5.0;

            OverrideExcavation           = null;
            OverrideBackfill             = null;
            OverrideCrossSectionInterval = 5.0;

            // ── Network selection ─────────────────────────────────────────────
            // Only include networks that have actual pipe station data.
            // (Other root-level dictionary keys like MANHOLES_CATALOG appear in
            //  SectionDebug as empty rows and must be excluded.)
            var sysNames = report.SectionDebug
                .Where(r => r.Stations != null && r.Stations.Count > 0)
                .Select(r => r.SystemName ?? "")
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(n => n)
                .ToList();

            string chosenSystem = null;

            if (sysNames.Count == 0)
            {
                ed.WriteMessage("\n[Sections] Ağ adı bulunamadı.\n");
                return;
            }
            else if (sysNames.Count == 1)
            {
                chosenSystem = sysNames[0];
                ed.WriteMessage($"\n[Sections] Ağ: {chosenSystem}");
            }
            else
            {
                // Build keyword list for selection.
                var kw = new PromptKeywordOptions(
                    "\n[Sections] Çizilecek ağı seçin")
                {
                    AllowNone = false
                };

                // Add "Tümü" (All) first, then each system name.
                kw.Keywords.Add("Tümü");
                foreach (var name in sysNames)
                {
                    // AutoCAD keyword must be alphanumeric; replace spaces with underscores.
                    string kw1 = name.Replace(" ", "_").Replace("-", "_");
                    if (kw1.Length > 0 && char.IsLetter(kw1[0]))
                        kw.Keywords.Add(kw1);
                    else
                        kw.Keywords.Add("N_" + kw1);
                }
                kw.Keywords.Default = "Tümü";

                ed.WriteMessage($"\n  Mevcut ağlar: {string.Join(", ", sysNames)}");

                var kwRes = ed.GetKeywords(kw);
                if (kwRes.Status != PromptStatus.OK) return;

                chosenSystem = kwRes.StringResult == "Tümü" ? null
                    : sysNames.FirstOrDefault(n =>
                        string.Equals(n.Replace(" ", "_").Replace("-", "_"),
                                      kwRes.StringResult,
                                      StringComparison.OrdinalIgnoreCase))
                      ?? kwRes.StringResult;
            }

            // ── Insertion point ───────────────────────────────────────────────
            var ptOpts = new PromptPointOptions(
                "\n[Sections] Kesitlerin başlangıç noktasını seçin: ")
            { AllowNone = false };

            var ptRes = ed.GetPoint(ptOpts);
            if (ptRes.Status != PromptStatus.OK) return;

            Point3d insertPt = ptRes.Value;

            // ── Draw ─────────────────────────────────────────────────────────
            ed.WriteMessage(
                $"\n[Sections] '{chosenSystem ?? "Tümü"}' ağı çiziliyor, " +
                $"aralık={interval:N1} m …");

            try
            {
                int n;
                using (doc.LockDocument())
                {
                    n = CrossSectionDrawService.Draw(
                        report, chosenSystem, insertPt,
                        kazi, dolgu, interval, doc, ed);
                }
                ed.WriteMessage(
                    $"\n[Sections] Tamamlandı. {n} nesne oluşturuldu.\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Sections ERROR] {ex.GetType().Name}: {ex.Message}\n");
            }
        }
    }
}
