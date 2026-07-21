using System;
using System.IO;
using System.Reflection;
using Bricscad.ApplicationServices;
using Teigha.Runtime;
using Bricscad.Windows;
using Bricscad.Ribbon;
using UrbanoLicensing;
using UrbanoMetraj.BoQ.UI;

[assembly: ExtensionApplication(typeof(UrbanoMetraj.UrbanoPlugin))]

// The suite-wide licence commands (UT_LICENSE / _STATUS / _RELEASE) live in the
// shared UrbanoLicensing core and are registered here — in EXACTLY ONE loaded
// plugin — to avoid a double-definition conflict. (UrbanoLock must drop its own
// copies when it migrates to the shared core.)
//
// Suite merge (2026-07-18): UrbanoMetraj declares NO [assembly: CommandClass].
// This is deliberate — with ZERO CommandClass attributes, AutoCAD auto-scans EVERY
// public type in this DLL for [CommandMethod], INCLUDING the nested
// NetworkPaletteSet.NetworkPanelCommand (UT_NET_PANEL). Adding even a single
// CommandClass here would switch AutoCAD to "only listed classes" mode and silently
// hide every command not explicitly listed (that regression made UT_NET_PANEL read
// as "Unknown command"). The three shared UT_LICENSE/_STATUS/_RELEASE commands are
// therefore registered by UrbanoLock instead (it already uses explicit-CommandClass
// mode) via [assembly: CommandClass(typeof(UrbanoLicensing.LicenseCommand))] there.
// See memory: urbano_suite_merge / project_suite_licensing_core.

namespace UrbanoMetraj
{
    /// <summary>
    /// Extension application entry point. Registers the "Urbano Tools &gt; Metraj"
    /// ribbon panel on load. All user-facing functionality is reached through the
    /// ribbon (see <see cref="RibbonSetup"/>); this class exposes no typed commands.
    ///
    /// The legacy CSV-BoQ pipeline and the reverse-engineering diagnostic scanners
    /// (URBANO_METRAJ, URBANO_SCAN, URBANO_DERIN, ExtractUrbanoXML, SnoopUrbanoData,
    /// URBANO_MINE, …) were removed once the Urbano XML schema was fully documented
    /// in URBANO_ARCHITECTURE_RULES.md and the BoQ pipeline stabilised. They remain
    /// in git history if ever needed for future schema archaeology.
    /// </summary>
    public class UrbanoPlugin : IExtensionApplication
    {
        public void Initialize()
        {
            // ── Assembly resolver (REQUIRED for suite-bundle deployment) ─────────
            // When shipped inside UrbanoSuite.bundle, UrbanoMetraj.dll and its
            // dependencies (UrbanoLicensing.dll, Clipper2Lib.dll, EPPlus.dll) all
            // live in the same Contents\Windows folder — but AutoCAD's AppDomain
            // does NOT probe the bundle folder automatically, so the CLR would
            // throw FileNotFoundException when a dependency is first used. This
            // handler resolves any missing assembly from UrbanoMetraj.dll's own
            // folder. Registered FIRST, before anything touches those types, and
            // independent of ComponentEntry load order (UrbanoLock installs an
            // identical handler; duplicates are harmless — both point here).
            AppDomain.CurrentDomain.AssemblyResolve += OnAssemblyResolve;

            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                "\nUrbanoMetraj yüklendi. Şerit: 'Urbano Tools > Metraj'.\n" +
                "  UT_BOQ_VIEW      - Metraj (BoQ) penceresini açar\n" +
                "  UT_SMART_ASSEMBLY - Akıllı Montaj (katalog yönetimi)\n" +
                "  UT_PROJE_AYARLARI - Proje Ayarları (Proje Kurulumu + Tür Eşleştirme)\n");

            // ── Licensing (shared UrbanoLicensing core) — ENFORCEMENT ON ─────────
            // Suite merge (2026-07-18): register EVERY UrbanoMetraj [CommandMethod]
            // under the "boq" feature, then arm the shared gate. Attach() hooks each
            // document's CommandWillStart and blocks a command only when it is
            // registered under a feature the active licence does NOT grant — so with
            // no/lock-only licence the BoQ commands now show the upsell dialog and
            // stop, exactly like UrbanoLock's. Attach() is idempotent (UrbanoLock may
            // have armed it already); RegisterFeatureFromAssembly is what makes the
            // boq commands enforceable. UT_LICENSE/_STATUS/_RELEASE are whitelisted in
            // the core so they always stay usable. See memory: urbano_suite_merge.
            try
            {
                LicenseManager.RegisterFeatureFromAssembly(
                    Features.Boq, Assembly.GetExecutingAssembly());
                LicenseManager.Attach();
            }
            catch (System.Exception lex)
            {
                doc?.Editor.WriteMessage($"\n[UrbanoMetraj] Lisans başlatma hatası: {lex.Message}\n");
            }

            // If the ribbon is already initialised (common when reloading the DLL),
            // set it up immediately. Otherwise subscribe to ItemInitialized so we
            // run as soon as the ribbon becomes available.
            if (ComponentManager.Ribbon != null)
            {
                RibbonSetup.Initialize();
            }
            else
            {
                Application.Idle += OnRibbonIdle;
            }
        }

        // BricsCAD has no ComponentManager.ItemInitialized event, so poll the Idle
        // tick until the ribbon infrastructure exists, then unsubscribe.
        private static void OnRibbonIdle(object sender, EventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;   // not ready yet — keep waiting
            Application.Idle -= OnRibbonIdle;
            RibbonSetup.Initialize();
        }

        public void Terminate()
        {
            try { Application.Idle -= OnRibbonIdle; } catch { }
            AppDomain.CurrentDomain.AssemblyResolve -= OnAssemblyResolve;

            // Best-effort: free this device's session lease so another device can
            // take the single slot immediately. Guarded — never throw on shutdown.
            try { LicenseManager.Detach(); } catch { }
        }

        // Resolves bundled dependencies (UrbanoLicensing / Clipper2Lib / EPPlus)
        // from UrbanoMetraj.dll's own folder. See the note in Initialize().
        private static Assembly OnAssemblyResolve(object sender, ResolveEventArgs args)
        {
            try
            {
                string simpleName = new AssemblyName(args.Name).Name;

                // Reuse an ALREADY-LOADED assembly before probing our folder.
                // UrbanoLock and UrbanoMetraj ship as SEPARATE BricsCAD bundles, so
                // each folder carries its own UrbanoLicensing.dll. Without this
                // check the CLR would load BOTH copies (LoadFrom caches per path),
                // giving two LicenseManager statics — i.e. two session leases — and
                // breaking the single-active-device rule. Whichever plugin loads
                // first therefore owns the one shared licence core.
                foreach (Assembly loaded in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (string.Equals(loaded.GetName().Name, simpleName,
                                      StringComparison.OrdinalIgnoreCase))
                        return loaded;
                }

                string pluginDir  = Path.GetDirectoryName(
                    Assembly.GetExecutingAssembly().Location) ?? "";
                string candidate  = Path.Combine(pluginDir, simpleName + ".dll");
                if (File.Exists(candidate))
                    return Assembly.LoadFrom(candidate);
            }
            catch { /* fall through — let the CLR raise the original load error */ }
            return null;
        }
    }
}
