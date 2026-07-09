using System;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Services;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.SolidsCommand))]

namespace UrbanoMetraj.BoQ
{
    public class SolidsCommand
    {
        // Preferences chosen in the BoQ_VIEW drop-downs / settings before this command
        // is queued. Cleared after use (one-shot).
        public static OverlapAssignment? OverrideExcavation;
        public static OverlapAssignment? OverrideBackfill;
        public static double             OverrideDisplayInterval = 5.0;

        [CommandMethod("UT_SOLIDS", CommandFlags.Modal)]
        public void GenerateSolids()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (!DwgBoQStore.HasData(doc.Database))
            {
                ed.WriteMessage(
                    "\n[Solids] لا توجد بيانات BoQ في هذا الملف.\n" +
                    "         شغّل UT_BOQ أولاً لحساب الكميات وحفظ الاحداثيات.\n");
                return;
            }

            ed.WriteMessage("\n[Solids] جاري تحميل البيانات من DWG...");

            BoQReport   report;
            BoQSettings boqSettings;

            try
            {
                (report, boqSettings) = DwgBoQStore.Load(doc.Database);
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Solids ERROR] فشل تحميل البيانات: {ex.Message}\n");
                return;
            }

            if (report?.SectionDebug == null || report.SectionDebug.Count == 0)
            {
                ed.WriteMessage("\n[Solids] لا توجد بيانات مقاطع. أعد تشغيل UT_BOQ.\n");
                return;
            }

            // Build settings from stored BoQ data — no dialog, no questions.
            // Drop-down overrides from BoQ_VIEW win over the stored defaults.
            var cfg = new SolidsSettings
            {
                DrawExcavation    = true,
                DrawBackfill      = true,
                DrawBedding       = true,
                DrawSurround      = true,
                ExcavationOverlap = OverrideExcavation
                                    ?? boqSettings?.ExcavationOverlap ?? OverlapAssignment.Split,
                BackfillOverlap   = OverrideBackfill
                                    ?? boqSettings?.BackfillOverlap   ?? OverlapAssignment.Split
            };
            double displayInterval = OverrideDisplayInterval;
            OverrideExcavation      = null;   // one-shot
            OverrideBackfill        = null;
            OverrideDisplayInterval = 5.0;

            ed.WriteMessage(
                $"\n[Solids] {report.SectionDebug.Count} مقطع محمّل. جاري إنشاء المجسمات...");

            try
            {
                int n;
                using (doc.LockDocument())
                {
                    n = SolidBuilderService.Build(report, doc, cfg, displayInterval, ed);
                }

                ed.WriteMessage(
                    $"\n[Solids] تم. {n} مجسم أُضيف على الطبقات: " +
                    "URB_Hafriyat / URB_GeriDolgu / URB_Yataklama / URB_Gomlekleme.\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[Solids ERROR] {ex.GetType().Name}: {ex.Message}\n");
            }
        }
    }
}
