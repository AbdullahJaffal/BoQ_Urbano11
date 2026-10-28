using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.ProjectRules.Services;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.TypeMapping.Services;
using UrbanoMetraj.BoQ.UI;
using UrbanoMetraj.BoQ.UI.NetworkPanel;

using Exception = System.Exception;
using MessageBox = System.Windows.Forms.MessageBox;
using MessageBoxButtons = System.Windows.Forms.MessageBoxButtons;
using MessageBoxIcon = System.Windows.Forms.MessageBoxIcon;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.BoQSimplifiedTestV2Command))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// BOQ_SIMPLIFIED_V2 — Angle-aware cross-section pipeline (development branch).
    ///
    /// Split (2026-07-04, user-directed) into two independent commands so
    /// engineering data linking (Tür Eşleştirme / Baca-Boru Bağlantı Kuralları)
    /// happens BEFORE the real calculation, not silently mid-run:
    ///   • UT_BOQ         — pulls Urbano's export + parses it just enough
    ///                          to refresh the discovered-catalog-item list for
    ///                          linking. No geometry, no Manhole AI, no save.
    ///   • UT_BOQ_HESAPLA — re-parses the last export and runs the full
    ///                          geometry + Manhole AI pipeline + save. No
    ///                          Urbano dialog automation needed here.
    /// Both surface any linking gaps (unlinked pipes, missing trench/Baca-Boru
    /// rules) via DiscoveryNotes — see PrintNotes.
    /// </summary>
    public partial class BoQSimplifiedTestV2Command
    {
        private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);

        private static string       _exportXmlPath;
        private static Editor       _editor;
        private static EventHandler _idleHandler;
        private static BoQSettings  _settings;

        private static bool _reopenViewAfterSave;
        public static void RequestReopenView() => _reopenViewAfterSave = true;

        // CAD Seçimi scope — set by UT_BOQ_CADSELECT, read by RunFullCalculation.
        // Lifecycle is owned by BoQResultsDialog: it sets this to the picked scope for
        // "CAD Seçimi" and back to null for "Tüm Aktif Ağlar" before each Hesapla, so a
        // null here always means a whole-active-network calc.
        public static SelectionScope PendingSelectionScope;
        public static string         PendingSelectionSummary;

        // "Son Seçilen": the last successful CAD pick this session, reused without
        // re-picking. LastScopeMode lets the split button's main face repeat the last
        // choice. All session-only (in-memory) by design.
        public static SelectionScope LastCadScope;
        public static string         LastCadSummary;
        public static ScopeMode      LastScopeMode = ScopeMode.None;

        // =====================================================================
        // Command entry point
        // =====================================================================

        [CommandMethod("UT_BOQ", CommandFlags.Modal)]
        public void Run()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (_idleHandler != null)
            {
                ed.WriteMessage("\n[BoQ] Zaten çalışıyor — tamamlanmasını bekleyin.\n");
                return;
            }

            // Load saved settings from DWG, or use defaults.
            BoQSettings settings;
            if (DwgBoQStore.HasData(doc.Database))
            {
                try   { (_, settings) = DwgBoQStore.Load(doc.Database); settings = settings ?? new BoQSettings(); }
                catch { settings = new BoQSettings(); }
            }
            else
            {
                settings = new BoQSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.ManholeConfigPath))
            {
                try { settings.ManholeConfigPath = ManholeConfigService.EnsureCatalogExists(); }
                catch (Exception ex) { ed.WriteMessage($"\n[BoQ] Catalog check failed: {ex.Message}\n"); }
            }

            _settings      = settings;
            _editor        = ed;
            _exportXmlPath = Path.Combine(Path.GetTempPath(), "urbano_boq_export.xml");

            try { if (File.Exists(_exportXmlPath)) File.Delete(_exportXmlPath); } catch { }

            var exportService = new UrbanoExportService(ed);
            var cts           = new System.Threading.CancellationTokenSource(DialogTimeout);

            var staThread = new Thread(() => RunAutomation(exportService, cts));
            staThread.SetApartmentState(ApartmentState.STA);
            staThread.IsBackground = true;
            staThread.Start();

            InputBlocker.Show();
            // Safety valve: force-hide the blocker if the automation gets stuck (e.g. a modal
            // popup it could not dismiss starves the Idle-driven Hide). DialogTimeout is 30 s, so
            // 45 s only fires when genuinely wedged, never during a normal run.
            InputBlockerWatchdog.Arm(45000);
            doc.SendStringToExecute("_ARS_EXPORT_XML\n", true, false, true);
        }

        // =====================================================================
        // STA automation thread
        // =====================================================================

        private static void RunAutomation(
            IUrbanoExportService                          exportService,
            System.Threading.CancellationTokenSource cts)
        {
            bool success = false;
            try   { success = exportService.WaitAndAutomate(_exportXmlPath, cts.Token); }
            catch (Exception ex)
            { _editor?.WriteMessage($"\n[SimplifiedBoQV2] Automation error: {ex.Message}"); }
            finally { cts.Dispose(); }

            _idleHandler = success ? (EventHandler)OnIdleExtractOnly : OnIdleAbort;
            Application.Idle += _idleHandler;
        }

        private static void OnIdleAbort(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;
            InputBlocker.Hide();
            _editor?.WriteMessage("\n[SimplifiedBoQV2] Export automation failed.\n");
        }

        // =====================================================================
        // Data extraction only — AutoCAD main thread (Idle callback)
        //
        // UT_BOQ's job is now limited to: pull Urbano's XML export, parse it
        // (Phase 1 only — no geometry/clash/Manhole AI), and refresh the
        // discovered-catalog-item list so Tür Eşleştirme can link them. Running
        // the full geometry+Manhole AI pipeline before linking is complete
        // produces silently incomplete results (unresolved PozNo/Sınıf, manhole
        // diameter/BOM) — see UT_BOQ_HESAPLA for the actual calculation,
        // meant to be run only after linking is done.
        // =====================================================================

        private static void OnIdleExtractOnly(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            Editor      ed      = _editor;
            string      xmlPath = _exportXmlPath;

            try
            {
                // "Metraj Verisi Güncelle" only needs to refresh the Tür Eşleştirme
                // discovered-item list, so we extract ONLY the export's catalog block
                // — no full topology parse (nodes/sections/geometry/Manhole AI), whose
                // BoQReport was discarded here anyway. This keeps the command fast no
                // matter how many networks the drawing has. The real calc + save is
                // UT_BOQ_HESAPLA. (Linking-completeness warnings are shown there,
                // AFTER the user has had a chance to complete the mappings — showing
                // them here, before linking, was premature noise.)
                var items = BoQParserService.ExtractCatalogItems(xmlPath);

                var activeDoc = Application.DocumentManager.MdiActiveDocument;
                using (activeDoc.LockDocument())
                {
                    // Discovered Urbano catalog items live inside the DWG (not the
                    // %TEMP% export file) so the Type Mapping tab works even after
                    // the DWG is moved to another machine — refreshed every run.
                    TypeMappingNodManager.SaveDiscoveredItems(activeDoc.Database, items);
                }

                ed.WriteMessage($"\n[BoQ] Veri çekme tamamlandı — {items.Count} katalog öğesi bulundu.\n");
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[BoQ ERROR] {ex.GetType().Name}: {ex.Message}\n");
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    ed.WriteMessage($"[BoQ ERROR]  ↳ InnerException: {inner.GetType().FullName}: {inner.Message}\n");
                ed.WriteMessage($"[BoQ ERROR]  Stack:\n{ex}\n");
            }
            finally
            {
                InputBlocker.Hide();
                // NOTE: the export XML is intentionally NOT deleted here — both
                // UT_BOQ_HESAPLA and the Tür Eşleştirme tab read it after this
                // command finishes. It's cleaned up at the START of the next
                // UT_BOQ run (see the File.Delete a few lines above Run()'s
                // beginning), so at most one stale file ever lingers, always
                // representing "the last successful export".

                bool reopen = _reopenViewAfterSave;
                _reopenViewAfterSave = false;
                _settings      = null;
                _editor        = null;
                _exportXmlPath = null;

                if (reopen)
                    Application.DocumentManager.MdiActiveDocument?
                        .SendStringToExecute("UT_BOQ_VIEW\n", true, false, true);
            }
        }

        // Prints DiscoveryNotes (pipe/manhole linking-completeness warnings collected
        // during Parse/ManholeAI) in a clearly delimited block — previously collected
        // but never surfaced anywhere, so incomplete linking failed silently.
        private static void PrintNotes(Editor ed, List<string> notes)
        {
            // Command-line warning dump silenced for release — the same DiscoveryNotes are
            // surfaced to the user in the "Eksik Eşleştirme Uyarısı" MessageBox instead.
            // Restore the block below to bring the command-line listing back.
            // if (notes == null || notes.Count == 0) return;
            // ed.WriteMessage("\n[BoQ] ── Uyarılar ───────────────────────────────────────────");
            // foreach (var n in notes) ed.WriteMessage("\n  " + n);
            // ed.WriteMessage("\n[BoQ] ──────────────────────────────────────────────────────────");
        }

        // =====================================================================
        // UT_BOQ_HESAPLA — full calculation command entry point
        //
        // No Urbano dialog automation here: reuses the XML from the last
        // successful UT_BOQ extraction. Meant to be run AFTER Tür
        // Eşleştirme / Baca-Boru Bağlantı Kuralları links are complete —
        // running it earlier still works (graceful degrade) but the linking
        // warnings below (and the BOM's own "(Catalog not found)" fallback
        // lines) will flag whatever is still missing.
        // =====================================================================

        [CommandMethod("UT_BOQ_HESAPLA", CommandFlags.Modal)]
        public void RunCalculate()
        {
            // Licence gate (boq). CommandWillStart's queued ESC cannot abort a
            // command that opens its window immediately, so block HERE. The warning
            // is already shown once by LicenseManager - stay silent, just return.
            if (!UrbanoLicensing.LicenseManager.IsFeatureUsable(UrbanoLicensing.Features.Boq)) return;
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc.Editor;

            if (_idleHandler != null)
            {
                ed.WriteMessage("\n[BoQ] Zaten çalışıyor — tamamlanmasını bekleyin.\n");
                return;
            }

            string xmlPath = Path.Combine(Path.GetTempPath(), "urbano_boq_export.xml");
            if (!File.Exists(xmlPath))
            {
                MessageBox.Show(
                    "Henüz Urbano'dan veri çekilmemiş.\n\n" +
                    "Önce \"Metraj Verisi Güncelle\" (UT_BOQ) ile Urbano ağ verisini çekin, " +
                    "gerekli eşleştirmeleri tamamlayın, sonra tekrar Hesapla'ya basın.",
                    "Veri Bulunamadı", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            BoQSettings settings;
            if (DwgBoQStore.HasData(doc.Database))
            {
                try   { (_, settings) = DwgBoQStore.Load(doc.Database); settings = settings ?? new BoQSettings(); }
                catch { settings = new BoQSettings(); }
            }
            else
            {
                settings = new BoQSettings();
            }

            if (string.IsNullOrWhiteSpace(settings.ManholeConfigPath))
            {
                try { settings.ManholeConfigPath = ManholeConfigService.EnsureCatalogExists(); }
                catch (Exception ex) { ed.WriteMessage($"\n[BoQ] Catalog check failed: {ex.Message}\n"); }
            }

            InputBlocker.Show();
            try
            {
                RunFullCalculation(ed, xmlPath, settings);
            }
            finally
            {
                InputBlocker.Hide();
                bool reopen = _reopenViewAfterSave;
                _reopenViewAfterSave = false;

                if (reopen)
                    Application.DocumentManager.MdiActiveDocument?
                        .SendStringToExecute("UT_BOQ_VIEW\n", true, false, true);
            }
        }

        // =====================================================================
        // UT_BOQ_CADSELECT — pick a drawing subset to calculate ("CAD Seçimi").
        //
        // The dialog closes itself and sends this command; it runs the layer-filtered
        // selection, stores the picked scope in PendingSelectionScope, then reopens the
        // Metraj window so the user can press Hesapla. Cancelling leaves the previous
        // scope untouched and simply reopens the window.
        // =====================================================================
        [CommandMethod("UT_BOQ_CADSELECT", CommandFlags.Modal)]
        public void RunCadSelect()
        {
            Document doc = Application.DocumentManager.MdiActiveDocument;
            Editor   ed  = doc?.Editor;
            if (doc == null) return;

            try
            {
                var scope = CadSelectionScopeService.Prompt(doc, out string summary);
                if (scope != null && !scope.IsEmpty)
                {
                    PendingSelectionScope   = scope;
                    PendingSelectionSummary = summary;
                    LastCadScope            = scope;          // enables "Son Seçilen"
                    LastCadSummary          = summary;
                    LastScopeMode           = ScopeMode.Cad;
                    ed.WriteMessage($"\n[BoQ] CAD Seçimi: {summary}. 'Hesapla' ile hesaplayın.\n");
                }
                else
                {
                    ed.WriteMessage("\n[BoQ] CAD Seçimi iptal edildi / boş.\n");
                }
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n[BoQ] CAD Seçimi hata: {ex.Message}\n");
            }
            finally
            {
                doc.SendStringToExecute("UT_BOQ_VIEW\n", true, false, true);
            }
        }

        // =====================================================================
        // Full geometry + Manhole AI pipeline — shared body for UT_BOQ_HESAPLA
        // =====================================================================

        private static void RunFullCalculation(Editor ed, string xmlPath, BoQSettings settings)
        {
            try
            {
                // Type Mapping links live in THIS document's DWG NOD — load them now
                // (don't rely on Akıllı Montaj having been opened this session) so
                // ParseSections can resolve PozNo/Sınıf for linked pipes below.
                var mappingDoc = Application.DocumentManager.MdiActiveDocument;
                if (mappingDoc != null)
                {
                    TypeMappingStore.LoadFromDwg(mappingDoc.Database);
                    // Per-network project rules (RULES mode). When its mode is RULES, the parser +
                    // ManholeAIService resolve types from these instead of Tür Eşleştirme.
                    ProjectRulesStore.LoadFromDwg(mappingDoc.Database);
                }

                // ── Phase 0: Active-network scope (URBANOLOCK — Ağ Seçimi) ───────
                // Networks left unchecked in the panel are excluded from the
                // calculation ENTIRELY — they are never parsed, so their sections/
                // bacalar, their volumes AND their warnings/counts (DiscoveryNotes)
                // are all scoped to the selected networks only. Only applies once the
                // panel has scraped this drawing at least once (URBANO_NETWORKS NOD
                // present); otherwise activeSystems stays null and the full drawing is
                // calculated as before.
                HashSet<string> activeSystems = null;
                if (mappingDoc != null)
                {
                    NetworkSessionManager.InitializeFromDrawing(mappingDoc.Database);
                    var knownNetworks = NetworkSessionManager.GetAllNetworks(mappingDoc.Database);
                    if (knownNetworks.Count > 0)
                    {
                        activeSystems = new HashSet<string>(
                            knownNetworks.Where(NetworkSessionManager.IsActive),
                            StringComparer.OrdinalIgnoreCase);
                    }
                }

                // ── Phase 0b: CAD Seçimi scope (narrows within the active networks) ──
                // The picked subset was already restricted to active-network layers, so
                // the active-network scope above still applies (keeps the parse cheap);
                // the selection just narrows it further for clash + output. Only when
                // nothing is marked active (empty set) do we drop to an unscoped parse,
                // so the (empty) network filter can't wipe everything before the
                // selection resolves.
                var selectionScope = PendingSelectionScope;
                if (selectionScope != null && !selectionScope.IsEmpty)
                {
                    if (activeSystems != null && activeSystems.Count == 0) activeSystems = null;
                }

                // ── Phase 1: Parse (scoped to active networks / CAD selection) ───
                var parser = new BoQParserService(enableClashDetection: false);
                BoQReport report = parser.Parse(xmlPath, ed, settings, activeSystems, selectionScope);

                var rows = report.SectionDebug;

                if (rows.Count == 0)
                {
                    ed.WriteMessage("\n[BoQ] Hata: boru kesiti bulunamadı, işlem durduruldu.\n");
                    return;
                }

                // ── Phases 2–9c: V2 trench engine (BoQSimplifiedTestV2Engine.cs, AutoCAD-free,
                // exercised by Tests/GeoHarness) ─────────────────────────────────────
                RunV2Engine(report);

                // ── Phase 11: Manhole AI ──────────────────────────────────────────
                try
                {
                    // Stacking source is our own ComponentFamily catalog, resolved per
                    // manhole via its Type Mapping link — no separate Excel file needed.
                    ManholeAIService.Process(report, settings);
                }
                catch (Exception aiEx)
                {
                    ed.WriteMessage($"\n[BoQ] Uyarı: Baca AI hatası: {aiEx.Message} (BOM verisi eksik kalabilir)");
                }

                // ── Phase 11.4: Net pipe length (raw length minus connected manholes' outer radius) ──
                try { PipeNetLengthService.Compute(report, settings.NetLengthMode); }
                catch (Exception netLenEx)
                { ed.WriteMessage($"\n[BoQ] Uyarı: Net uzunluk hesap hatası: {netLenEx.Message}"); }

                // ── Phase 11.7: Linking-completeness warnings ────────────────────
                // Collected during Parse (unlinked pipes/missing catalog or trench
                // rules) and Manhole AI (unresolved Baca-Boru Bağlantı Kuralı match).
                PrintNotes(ed, report.DiscoveryNotes);

                // ── Phase 11.5/11.6: Manhole–Trench / Manhole–Manhole Overlap ────
                // Diagnostic-only return values are discarded — the real work here
                // is the side effect (populating ManholeExcavDeducted/BackfillVolume/
                // BackfillLayerSplits on the report), not the printed detail.
                try { ManholeExcavOverlapService.Compute(report); }
                catch (Exception ovEx)
                { ed.WriteMessage($"\n[BoQ] Uyarı: Baca-boru kesişim hatası: {ovEx.Message}"); }

                try { ManholeExcavOverlapService.ComputeManholeVsManhole(report); }
                catch (Exception mhEx)
                { ed.WriteMessage($"\n[BoQ] Uyarı: Baca-baca kesişim hatası: {mhEx.Message}"); }

                // ── Phase 12: Save to DWG ────────────────────────────────────────
                var activeDoc = Application.DocumentManager.MdiActiveDocument;
                using (activeDoc.LockDocument())
                {
                    DwgBoQStore.Save(activeDoc.Database, report, settings);
                    // Discovered Urbano catalog items live inside the DWG (not the
                    // %TEMP% export file) so the Type Mapping tab works even after
                    // the DWG is moved to another machine — refreshed every run.
                    TypeMappingNodManager.SaveDiscoveredItems(activeDoc.Database, report.CatalogItems);
                }
                ed.WriteMessage("\n[BoQ] Hesaplama tamamlandı.\n");

                if (report.DiscoveryNotes.Count > 0)
                {
                    // Sort by severity (Error → Warning → Info) and pick the dialog icon/tone from
                    // the highest level present, so a run that produced ONLY informational notes
                    // (e.g. an ignored rounding gap) is shown calmly as info, not as a scary warning.
                    var ordered = report.DiscoveryNotes
                        .OrderBy(n => (int)NoteMarker.SeverityOf(n))
                        .ToList();
                    bool hasError   = ordered.Any(n => NoteMarker.SeverityOf(n) == NoteSeverity.Error);
                    bool hasWarning = ordered.Any(n => NoteMarker.SeverityOf(n) == NoteSeverity.Warning);
                    bool onlyInfo   = !hasError && !hasWarning;

                    string guidance = ProjectRulesStore.IsRulesMode
                        ? "\n\nKurallar modu etkin. Eksikler için Proje Ayarları → Proje Kurulumu (DWG) → " +
                          "Kurallar'da her aktif ağ için: Boru ailesi/sınıfı, Baca ailesi ve Bağlantı " +
                          "Kuralları'nı (özellikle her katmanın Taban'ını — o ailenin Taban'larından " +
                          "biri olmalı) tamamlayın."
                        : "\n\nEksik eşleştirmeler nedeniyle Poz No/Sınıf, baca çapı veya Prefabrik Malzeme " +
                          "Listesi hatalı/eksik olabilir. Devam etmeden önce eşleştirmeleri Proje Ayarları'ndan " +
                          "(Tür Eşleştirme) ve Akıllı Montaj'dan (Baca Seçim Kuralları) tamamlamanız önerilir.";

                    string intro = onlyInfo
                        ? "Hesaplama tamamlandı. Aşağıdakiler yalnızca bilgilendirme amaçlıdır:\n\n"
                        : "Hesaplama tamamlandı. Dikkat edilmesi gereken kalemler:\n\n";

                    string footer = onlyInfo
                        ? "\n\nℹ Bu kalemler hesabı VE malzeme listesini ETKİLEMEZ — sonuç geçerlidir."
                        : guidance + "\n\nİşaretler:  ⛔ Hata   ⚠ Uyarı   ℹ Bilgi (hesabı etkilemez)";

                    MessageBox.Show(
                        intro + string.Join("\n", ordered) + footer,
                        onlyInfo ? "Hesaplama Tamamlandı — Bilgi" : "Hesaplama Uyarıları",
                        MessageBoxButtons.OK,
                        hasError ? MessageBoxIcon.Error
                                 : hasWarning ? MessageBoxIcon.Warning
                                              : MessageBoxIcon.Information);
                }
            }
            catch (Exception ex)
            {
                ed.WriteMessage($"\n[BoQ ERROR] {ex.GetType().Name}: {ex.Message}\n");
                for (var inner = ex.InnerException; inner != null; inner = inner.InnerException)
                    ed.WriteMessage($"[BoQ ERROR]  ↳ InnerException: {inner.GetType().FullName}: {inner.Message}\n");
                ed.WriteMessage($"[BoQ ERROR]  Stack:\n{ex}\n");
            }
        }

        // =====================================================================
        // Phase 9b — Wedge corrections for crossing pipes
        //
        // When two pipes cross at angle X >= threshold, the transition from
        // "perpendicular-to-pipe" stations outside the zone to "bisector-frame"
        // stations inside the zone creates a triangular wedge volume at each
        // zone boundary (entry and exit). This wedge is already counted by the
        // outside-zone integration and must be subtracted once.
        //
        // Formula per half-trench (W1=bottom half-width, W2=top half-width, H=depth):
        //   V_half  = H × (W1² + W1W2 + W2²) / 6 × tan(X/2)
        //   V_total = 2 × V_half = H × (W1² + W1W2 + W2²) / 3 × tan(X/2)
        //
        // Applied at 4 points per pair: entry/exit × pipe A / pipe B.
        // Each wedge is subtracted equally from KU, KL, and SP totals.
        // =====================================================================

        private static void ApplyWedgeCorrections(
            List<PairZone>                pairZones,
            List<SectionDebugRow>         rows,
            List<List<SimplifiedStation>> allStations,
            List<SimplifiedSectionResult> results,
            Editor                        ed)
        {
            foreach (var pz in pairZones)
            {
                if (!pz.UseBisectorFrame) continue;

                ApplyWedgeAtBoundary(allStations[pz.PipeA], results[pz.PipeA],
                    pz.EnterA, pz.TanHalfAngle, rows[pz.PipeA].PipeName, "entry", ed);
                ApplyWedgeAtBoundary(allStations[pz.PipeA], results[pz.PipeA],
                    pz.ExitA,  pz.TanHalfAngle, rows[pz.PipeA].PipeName, "exit",  ed);
                ApplyWedgeAtBoundary(allStations[pz.PipeB], results[pz.PipeB],
                    pz.EnterB, pz.TanHalfAngle, rows[pz.PipeB].PipeName, "entry", ed);
                ApplyWedgeAtBoundary(allStations[pz.PipeB], results[pz.PipeB],
                    pz.ExitB,  pz.TanHalfAngle, rows[pz.PipeB].PipeName, "exit",  ed);
            }
        }

        private static void ApplyWedgeAtBoundary(
            List<SimplifiedStation> stations,
            SimplifiedSectionResult result,
            double                  boundaryDist,
            double                  tanHalfAngle,
            string                  pipeName,
            string                  side,
            Editor                  ed)
        {
            var st = stations.FirstOrDefault(
                s => Math.Abs(s.StationDist - boundaryDist) < 1e-4 && s.ExcavPoly != null);
            if (st == null) return;

            // Extract half-widths and height from stored polygon vertices.
            // ExcavPoly:    v[0]={-hwBase,zBot}, v[2]={hwExcav,zTop}
            // BackfillPoly: v[0]={-hwSurr,zSurrTop}, v[2]={hwExcav,zTop}
            double hw1E = -st.ExcavPoly[0][0];
            double hw2E =  st.ExcavPoly[2][0];
            double hE   =  st.ExcavPoly[2][1] - st.ExcavPoly[0][1];
            double vWedgeE = WedgeVolume(hw1E, hw2E, hE, tanHalfAngle);

            result.VExcavKU -= vWedgeE;
            result.VExcavKL -= vWedgeE;
            result.VExcavSP -= vWedgeE;

            // BackfillPoly is empty when this station's Dolgu Seviyesi resolved
            // below the invert (degenerate — see BuildGrossProfiles/BuildBisectorStation);
            // no backfill wedge to subtract in that case.
            if (st.BackfillPoly == null || st.BackfillPoly.Count < 4) return;

            double hw1B = -st.BackfillPoly[0][0];
            double hw2B =  st.BackfillPoly[2][0];
            double hB   =  st.BackfillPoly[2][1] - st.BackfillPoly[0][1];
            double vWedgeBf = WedgeVolume(hw1B, hw2B, hB, tanHalfAngle);

            result.VBackfillKU -= vWedgeBf;
            result.VBackfillKL -= vWedgeBf;
            result.VBackfillSP -= vWedgeBf;
        }

        // V_full = H × (hw1² + hw1·hw2 + hw2²) / 3 × tan(X/2)
        // (both halves of the symmetric trapezoidal trench combined)
        private static double WedgeVolume(double hw1, double hw2, double H, double tanHalfAngle)
        {
            return H * (hw1 * hw1 + hw1 * hw2 + hw2 * hw2) / 3.0 * tanHalfAngle;
        }

    }
}
