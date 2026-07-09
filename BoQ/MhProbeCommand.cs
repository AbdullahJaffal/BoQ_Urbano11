using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.EditorInput;
using Autodesk.AutoCAD.Runtime;
using UrbanoMetraj.BoQ.Services;
using UrbanoMetraj.BoQ.UI;

using Exception = System.Exception;

[assembly: CommandClass(typeof(UrbanoMetraj.BoQ.MhProbeCommand))]

namespace UrbanoMetraj.BoQ
{
    /// <summary>
    /// UT_MH_PROBE — temporary diagnostic command.
    ///
    /// Triggers ARS_EXPORT_XML (same automation as UT_BOQ), then in the Idle
    /// callback reads only node GUIDs + names from the XML and cross-references
    /// them against the MANHOLE_ASSIGNMENTS stored in the DWG NOD.
    ///
    /// Output is written to the AutoCAD command line — no files changed, no DWG
    /// state modified.  Safe to run at any time.
    /// </summary>
    public class MhProbeCommand
    {
        private static readonly TimeSpan DialogTimeout = TimeSpan.FromSeconds(30);

        private static string       _xmlPath;
        private static Editor       _ed;
        private static EventHandler _idleHandler;

        // =====================================================================
        // Command entry point
        // =====================================================================

        [CommandMethod("UT_MH_PROBE", CommandFlags.Modal)]
        public void RunProbe()
        {
            var doc = Application.DocumentManager.MdiActiveDocument;
            var ed  = doc?.Editor;
            if (doc == null || ed == null) return;

            if (_idleHandler != null)
            {
                ed.WriteMessage("\n[UT_MH_PROBE] Önceki çalışma henüz bitmedi, bekleyin.\n");
                return;
            }

            _ed      = ed;
            _xmlPath = Path.Combine(Path.GetTempPath(), "mh_probe_export.xml");
            TryDelete(_xmlPath);

            ed.WriteMessage("\n[UT_MH_PROBE] ARS_EXPORT_XML başlatılıyor...");

            var exportService = new UrbanoExportService(ed);
            var cts = new System.Threading.CancellationTokenSource(DialogTimeout);

            var sta = new Thread(() => RunAuto(exportService, cts));
            sta.SetApartmentState(ApartmentState.STA);
            sta.IsBackground = true;
            sta.Start();

            InputBlocker.Show();
            doc.SendStringToExecute("_ARS_EXPORT_XML\n", true, false, true);
        }

        // =====================================================================
        // STA background thread
        // =====================================================================

        private static void RunAuto(UrbanoExportService svc,
                                    System.Threading.CancellationTokenSource cts)
        {
            bool ok = false;
            try   { ok = svc.WaitAndAutomate(_xmlPath, cts.Token); }
            catch (Exception ex) { _ed?.WriteMessage($"\n[UT_MH_PROBE] Hata: {ex.Message}"); }
            finally { cts.Dispose(); }

            _idleHandler = ok ? (EventHandler)OnIdleProbe : OnIdleAbort;
            Application.Idle += _idleHandler;
        }

        // =====================================================================
        // Idle — abort path
        // =====================================================================

        private static void OnIdleAbort(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;
            InputBlocker.Hide();
            Reset();
            _ed?.WriteMessage("\n[UT_MH_PROBE] XML export başarısız oldu.\n");
        }

        // =====================================================================
        // Idle — main probe (AutoCAD main thread)
        // =====================================================================

        private static void OnIdleProbe(object sender, EventArgs e)
        {
            Application.Idle -= _idleHandler;
            _idleHandler = null;

            var ed      = _ed;
            var xmlPath = _xmlPath;

            try
            {
                var activeDoc = Application.DocumentManager.MdiActiveDocument;
                var db        = activeDoc?.Database;
                if (db == null || ed == null) return;

                // ── 1. Parse XML: GUID → node name ───────────────────────────
                var guidToName = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                if (!File.Exists(xmlPath))
                {
                    ed.WriteMessage("\n[UT_MH_PROBE] XML dosyası bulunamadı.\n");
                    return;
                }

                var xdoc    = LoadXml(xmlPath);
                var tplMain = xdoc.Descendants("topology")
                                  .Descendants("networkTopology")
                                  .Descendants("main")
                                  .Descendants("tpl")
                                  .FirstOrDefault();

                if (tplMain == null)
                {
                    ed.WriteMessage("\n[UT_MH_PROBE] XML'de tpl/main bulunamadı.\n");
                    return;
                }

                foreach (var nEl in tplMain.Descendants("ns").Elements("n"))
                {
                    string guid = ((string)nEl.Attribute("g") ?? "").ToUpperInvariant();
                    if (string.IsNullOrEmpty(guid)) continue;

                    var    props = ReadProps(nEl);
                    string name  = DecodeStr(GetProp(props, "AG_NAME"));
                    guidToName[guid] = string.IsNullOrEmpty(name) ? "(isimsiz)" : name;
                }

                ed.WriteMessage($"\n[UT_MH_PROBE] XML'den {guidToName.Count} baca node'u okundu.");

                // ── 2. Load stored assignments ────────────────────────────────
                var assignments = ManholeAssignStore.HasData(db)
                    ? ManholeAssignStore.Load(db)
                    : new List<ManholeAssignment>();

                ed.WriteMessage($"\n[UT_MH_PROBE] DWG'de {assignments.Count} atama kaydı bulundu.");

                // ── 3. Load catalog for group name lookup ─────────────────────
                var groupNames = new Dictionary<string, string>(
                    StringComparer.OrdinalIgnoreCase);

                if (ManholeCatalogStore.HasData(db))
                {
                    var cat = ManholeCatalogStore.Load(db);
                    if (cat?.Groups != null)
                        foreach (var g in cat.Groups)
                            if (!string.IsNullOrEmpty(g.Id))
                                groupNames[g.Id] = $"{g.Name} ({(g.Mode == "Dynamic" ? "Dinamik" : "Sabit")})";
                }

                // ── 4. Correlate & print ──────────────────────────────────────
                var assignByGuid = assignments.ToDictionary(
                    a => a.AgGuid ?? "",
                    a => a,
                    StringComparer.OrdinalIgnoreCase);

                ed.WriteMessage("\n");
                ed.WriteMessage("\n══════════════════════════════════════════════════════");
                ed.WriteMessage("\n  UT_MH_PROBE — AG_GUID  ↔  Node Adı  ↔  Katalog Grubu");
                ed.WriteMessage("\n══════════════════════════════════════════════════════");

                int matched = 0, unmatched = 0;

                foreach (var kv in guidToName.OrderBy(x => x.Value))
                {
                    string guid     = kv.Key;
                    string nodeName = kv.Value;

                    if (assignByGuid.TryGetValue(guid, out var asgn))
                    {
                        string grpName = groupNames.TryGetValue(asgn.GroupId ?? "", out var gn)
                            ? gn : $"[ID: {asgn.GroupId}]";
                        ed.WriteMessage(
                            $"\n  ✓  {nodeName,-8} {guid}  →  {grpName}");
                        matched++;
                    }
                    else
                    {
                        ed.WriteMessage(
                            $"\n  –  {nodeName,-8} {guid}  (atama yok)");
                        unmatched++;
                    }
                }

                // Assignments that reference a GUID not found in XML
                foreach (var a in assignments)
                {
                    if (!guidToName.ContainsKey(a.AgGuid ?? ""))
                    {
                        string grpName = groupNames.TryGetValue(a.GroupId ?? "", out var gn)
                            ? gn : $"[ID: {a.GroupId}]";
                        ed.WriteMessage(
                            $"\n  ⚠  {a.AgGuid}  →  {grpName}  (XML'de node bulunamadı!)");
                    }
                }

                ed.WriteMessage("\n══════════════════════════════════════════════════════");
                ed.WriteMessage(
                    $"\n  Özet: {guidToName.Count} node, " +
                    $"{matched} atanmış, {unmatched} atamasız.");
                ed.WriteMessage("\n══════════════════════════════════════════════════════\n");
            }
            catch (Exception ex)
            {
                ed?.WriteMessage($"\n[UT_MH_PROBE ERROR] {ex.GetType().Name}: {ex.Message}\n");
            }
            finally
            {
                InputBlocker.Hide();
                TryDelete(xmlPath);
                Reset();
            }
        }

        // =====================================================================
        // Robust XML loader (mirrors BoQParserService.LoadXmlRobust)
        // =====================================================================

        private static readonly Regex XmlDeclRx = new Regex(
            @"<\?xml\b[^?]*\?>",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static XDocument LoadXml(string path)
        {
            try { return XDocument.Load(path); }
            catch (System.Xml.XmlException) { }

            byte[] raw = File.ReadAllBytes(path);
            foreach (int cp in new[] { 0, 1254, 1252, 28591 })
            {
                try
                {
                    string txt = cp == 0
                        ? Encoding.Default.GetString(raw)
                        : Encoding.GetEncoding(cp).GetString(raw);
                    return XDocument.Parse(
                        XmlDeclRx.Replace(txt, "<?xml version=\"1.0\" encoding=\"utf-8\"?>"));
                }
                catch { }
            }
            throw new Exception("XML yüklenemedi — encoding uyuşmazlığı.");
        }

        // =====================================================================
        // Minimal XML helpers (mirrors BoQParserService private methods)
        // =====================================================================

        private static Dictionary<string, string> ReadProps(XElement el)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in el.Elements("ps").Elements("p"))
            {
                string t = (string)p.Attribute("t") ?? "";
                string v = (string)p.Attribute("v") ?? "";
                if (!string.IsNullOrEmpty(t) && !d.ContainsKey(t)) d[t] = v;
            }
            return d;
        }

        private static string GetProp(Dictionary<string, string> d, string key)
            => d.TryGetValue(key, out string v) ? v : "";

        private static string DecodeStr(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "";
            if (raw.StartsWith("5005") || raw.StartsWith("5003")) return raw.Substring(4);
            return raw;
        }

        // =====================================================================
        // Utilities
        // =====================================================================

        private static void TryDelete(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                try { File.Delete(path); } catch { }
        }

        private static void Reset()
        {
            _xmlPath     = null;
            _ed          = null;
            _idleHandler = null;
        }
    }
}
