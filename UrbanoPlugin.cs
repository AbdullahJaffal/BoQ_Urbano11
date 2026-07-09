using System;
using System.Reflection;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.Runtime;
using Autodesk.Windows;
using UrbanoLicensing;
using UrbanoMetraj.BoQ.UI;

[assembly: ExtensionApplication(typeof(UrbanoMetraj.UrbanoPlugin))]

// The suite-wide licence commands (UT_LICENSE / _STATUS / _RELEASE) live in the
// shared UrbanoLicensing core and are registered here — in EXACTLY ONE loaded
// plugin — to avoid a double-definition conflict. (UrbanoLock must drop its own
// copies when it migrates to the shared core.)
[assembly: CommandClass(typeof(UrbanoLicensing.LicenseCommand))]

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
            var doc = Application.DocumentManager.MdiActiveDocument;
            doc?.Editor.WriteMessage(
                "\nUrbanoMetraj yüklendi. Şerit: 'Urbano Tools > Metraj'.\n" +
                "  UT_BOQ_VIEW      - Metraj (BoQ) penceresini açar\n" +
                "  UT_SMART_ASSEMBLY - Akıllı Montaj (katalog yönetimi)\n" +
                "  UT_PROJE_AYARLARI - Proje Ayarları (Proje Kurulumu + Tür Eşleştirme)\n");

            // ── Licensing (shared UrbanoLicensing core) ──────────────────────────
            // Register every UT_ command this assembly declares under the "boq"
            // feature, then arm the shared licence manager. Attach() is idempotent
            // and process-wide, so if UrbanoLock also loads only ONE session lease
            // runs. Fully guarded: a licensing failure must never abort plugin load.
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
                ComponentManager.ItemInitialized += OnRibbonReady;
            }
        }

        private static void OnRibbonReady(object sender, RibbonItemEventArgs e)
        {
            if (ComponentManager.Ribbon == null) return;
            ComponentManager.ItemInitialized -= OnRibbonReady;
            RibbonSetup.Initialize();
        }

        public void Terminate()
        {
            // Best-effort: free this device's session lease so another device can
            // take the single slot immediately. Guarded — never throw on shutdown.
            try { LicenseManager.Detach(); } catch { }
        }
    }
}
