using System;
using System.Diagnostics;
using System.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Geometry;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Sections;
using UrbanoMetraj.BoQ.Sections.UI;

using Application = Autodesk.AutoCAD.ApplicationServices.Application;
using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.Sections.CrossSectionsCommand))]

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// UT_CROSS_SECTIONS — draws the excavation / fill cross-sections (En Kesit) of the
    /// last UT_BOQ_HESAPLA run, exactly as calculated (same engine, same scenarios).
    /// UT_CROSS_SECTIONS_CLEAR — removes drawn sections (last run or all).
    ///
    /// Opens the control panel (CrossSectionsWindow: Şablon + scope / scenario / content /
    /// appearance / layout + live preview); its state is saved per drawing on close, and
    /// "Çiz" places the sheet (drag preview) or redraws in place of the previous run.
    /// </summary>
    public class CrossSectionsCommand
    {
        [CommandMethod("UT_CROSS_SECTIONS", CommandFlags.Modal)]
        public void DrawCrossSections()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor; var db = doc.Database;
            var sw = Stopwatch.StartNew();

            var session = CrossSectionSession.Prepare(db, out string error);
            if (session == null)
            {
                ed.WriteMessage("\n[En Kesit] " + error + "\n");
                Application.ShowAlertDialog("En Kesit\n\n" + error);
                return;
            }

            var last = SectionStyleStore.LoadLastRun(db);
            var vm = new CrossSectionsVm(session, SectionStyleStore.LoadStyle(db), SectionStyleStore.LoadRunOptions(db))
            {
                HasLastRun = last.IsValid
            };
            var win = new CrossSectionsWindow(vm, TextStyleNames(db))
            {
                LastRunId = last.RunId,
                ClearHandler = rid => { using (doc.LockDocument()) return SectionRenderer.Clear(db, string.IsNullOrEmpty(rid) ? null : rid); }
            };
            Application.ShowModalWindow(win);

            // The panel state is remembered per drawing whether or not anything is drawn.
            vm.CommitScope();
            var style = vm.Style; var run = vm.Run;
            try { using (doc.LockDocument()) SectionStyleStore.Save(db, style, run); }
            catch (Exception ex) { ed.WriteMessage($"\n[En Kesit] Ayarlar kaydedilemedi: {ex.Message}"); }
            if (win.Result != PanelResult.Draw) return;
            sw.Restart();

            var (kazi, dolgu, interval) = session.Resolve(run);
            ed.WriteMessage($"\n[En Kesit] Şablon '{style.Name}' · aralık {interval:0.0#} m · Kazı: {SectionLayoutEngine.ScenarioLabel(kazi)} · " +
                            $"Dolgu: {SectionLayoutEngine.ScenarioLabel(dolgu)}" + (run.UseProjectSettings ? " (Proje Ayarları)" : " (bu çizim için özel)"));

            // ── Build + layout ────────────────────────────────────────────────
            System.Collections.Generic.List<SectionCut> cuts;
            SectionSheet sheet;
            try
            {
                cuts  = session.BuildCuts(run);
                sheet = SectionLayoutEngine.Layout(cuts, style);
            }
            catch (Exception ex)
            {
                string where = (ex.StackTrace ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
                ed.WriteMessage($"\n[En Kesit HATA] Kesitler hazırlanamadı: {ex.GetType().Name}: {ex.Message} @ {where}\n");
                return;
            }
            if (session.SkippedPipes.Count > 0)
                ed.WriteMessage($"\n[En Kesit] {session.SkippedPipes.Count} sıfır uzunluklu boru (düşü) kesitsiz geçildi: " +
                                string.Join(", ", session.SkippedPipes.Take(8).Select(SectionLayoutEngine.Sanitize)) +
                                (session.SkippedPipes.Count > 8 ? " …" : ""));
            if (session.FailedCuts.Count > 0)
            {
                ed.WriteMessage($"\n[En Kesit] ⚠ {session.FailedCuts.Count} kesit üretilemedi:");
                foreach (var f in session.FailedCuts.Take(5))
                    ed.WriteMessage($"\n    {SectionLayoutEngine.Sanitize(f.Pipe)} @ {f.Dist:0.00} m — {f.Error}");
            }
            if (cuts.Count == 0) { ed.WriteMessage("\n[En Kesit] Çizilecek kesit yok.\n"); return; }

            int nEngine = cuts.Count(c => c.Kind == SectionStationKind.EngineStation);
            int nGross  = cuts.Count(c => c.Kind == SectionStationKind.GrossAtChainage);
            int nSnap   = cuts.Count(c => c.Kind == SectionStationKind.SnappedInClash);
            ed.WriteMessage($"\n[En Kesit] {cuts.Count} kesit hazır ({cuts.Select(c => c.PipeIndex).Distinct().Count()} boru; " +
                            $"motor istasyonu {nEngine}, çakışmasız brüt {nGross}, en yakın istasyona oturan {nSnap}). " +
                            $"Pafta {sheet.MaxX - sheet.MinX:0.#} × {sheet.MaxY - sheet.MinY:0.#} m.");

            // ── Insertion point: previous run's point (replace) or drag preview ─
            Point3d origin;
            int erased = 0;
            if (vm.ReplaceLast && last.IsValid)
            {
                using (doc.LockDocument()) erased = SectionRenderer.Clear(db, last.RunId);
                origin = last.Insert;
            }
            else
            {
                var jig = new SheetJig(sheet);
                var pr = ed.Drag(jig);
                if (pr.Status != PromptStatus.OK) return;
                origin = jig.Origin;
            }

            string runId = DateTime.Now.ToString("yyyyMMddHHmmss");
            try
            {
                SectionRenderer.Result r;
                using (doc.LockDocument())
                {
                    r = SectionRenderer.Render(db, sheet, style, origin, runId);
                    SectionStyleStore.SaveLastRun(db, runId, origin);
                }
                ed.WriteMessage($"\n[En Kesit] Tamamlandı: {r.Sections} kesit, {r.Entities} nesne ({sw.Elapsed.TotalSeconds:0.0} sn)" +
                                (erased > 0 ? $", önceki çizimden {erased} nesne silindi" : "") + "." +
                                (r.HatchFailures > 0 ? $" ⚠ {r.HatchFailures} tarama oluşturulamadı — ilk hata: {r.FirstHatchError}" : "") +
                                " Silmek için: UT_CROSS_SECTIONS_CLEAR\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[En Kesit HATA] Çizim: {ex.GetType().Name}: {ex.Message}\n");
            }
        }

        [CommandMethod("UT_CROSS_SECTIONS_CLEAR", CommandFlags.Modal)]
        public void ClearCrossSections()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            if (doc == null) return;
            var ed = doc.Editor; var db = doc.Database;
            var last = SectionStyleStore.LoadLastRun(db);
            var kw = new PromptKeywordOptions("\n[En Kesit] Hangi kesitler silinsin?") { AllowNone = false };
            kw.Keywords.Add("Son"); kw.Keywords.Add("Tumu");
            kw.Keywords.Default = last.IsValid ? "Son" : "Tumu";
            var kr = ed.GetKeywords(kw);
            if (kr.Status != PromptStatus.OK) return;
            int n;
            using (doc.LockDocument())
                n = SectionRenderer.Clear(db, kr.StringResult == "Son" ? last.RunId : null);
            ed.WriteMessage($"\n[En Kesit] {n} nesne silindi.\n");
        }

        /// <summary>Text styles of the drawing for the panel's combo ("URB_KS" = created with Arial on first draw).</summary>
        private static string[] TextStyleNames(Database db)
        {
            var names = new System.Collections.Generic.List<string> { "URB_KS" };
            try
            {
                using (var tr = db.TransactionManager.StartOpenCloseTransaction())
                {
                    var ts = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
                    foreach (ObjectId id in ts)
                    {
                        var r = (TextStyleTableRecord)tr.GetObject(id, OpenMode.ForRead);
                        if (!string.IsNullOrEmpty(r.Name) && !names.Contains(r.Name, StringComparer.OrdinalIgnoreCase)) names.Add(r.Name);
                    }
                }
            }
            catch (Exception) { }
            return names.ToArray();
        }

        /// <summary>Drags the sheet's outline so the user sees how much room the sections need.</summary>
        private sealed class SheetJig : DrawJig
        {
            private readonly double _w, _h, _dx, _dy;
            public Point3d Origin { get; private set; }

            public SheetJig(SectionSheet sheet)
            {
                _w = sheet.MaxX - sheet.MinX; _h = sheet.MaxY - sheet.MinY;
                _dx = sheet.MinX; _dy = sheet.MaxY;   // cursor = top-left corner of the sheet
            }

            protected override SamplerStatus Sampler(JigPrompts prompts)
            {
                var o = new JigPromptPointOptions("\n[En Kesit] Kesitlerin yerleşeceği sol üst köşeyi seçin: ")
                {
                    UserInputControls = UserInputControls.Accept3dCoordinates | UserInputControls.NullResponseAccepted
                };
                var r = prompts.AcquirePoint(o);
                if (r.Status != PromptStatus.OK) return SamplerStatus.Cancel;
                var p = new Point3d(r.Value.X - _dx, r.Value.Y - _dy, 0);
                if (p.IsEqualTo(Origin)) return SamplerStatus.NoChange;
                Origin = p;
                return SamplerStatus.OK;
            }

            protected override bool WorldDraw(Autodesk.AutoCAD.GraphicsInterface.WorldDraw draw)
            {
                double x0 = Origin.X + _dx, y1 = Origin.Y + _dy;
                var pts = new Point3dCollection
                {
                    new Point3d(x0, y1, 0), new Point3d(x0 + _w, y1, 0),
                    new Point3d(x0 + _w, y1 - _h, 0), new Point3d(x0, y1 - _h, 0), new Point3d(x0, y1, 0)
                };
                draw.Geometry.Polyline(pts, Vector3d.ZAxis, IntPtr.Zero);
                return true;
            }
        }
    }
}
