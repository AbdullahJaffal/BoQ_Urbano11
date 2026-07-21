using System;
using UrbanoMetraj.BoQ.DolguCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.ProjectRules.UI.ViewModels;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SoilCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.TypeMapping.UI.ViewModels;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>
    /// Root ViewModel for the Akıllı Montaj modeless window.
    /// Owns the shared <see cref="SmartAssemblyMasterCatalog"/>, the Smart Assembly
    /// tab ViewModels, and the ViewModels of every other catalog that has been folded
    /// into this single window as an additional tab.
    /// </summary>
    public class SmartAssemblyMainVm : ViewModelBase
    {
        // ── Tab indices (must match TabControl order in SmartAssemblyWindow.xaml) ──
        // Order (2026-07-09): Prefabrik Baca / Boru / Kazı Tipi / Dolgu Katalogları,
        // then Baca Seçim / Baca Kazı / Boru Hendek Kuralları. Proje Kurulumu and
        // Tür Eşleştirme moved out to the separate Proje Ayarları window
        // (ProjectSettingsWindow) and no longer have a tab index here.
        public const int TAB_REPOSITORY    = 0;   // Prefabrik Baca Kataloğu
        public const int TAB_PIPE_CATALOG  = 1;   // Boru Kataloğu
        public const int TAB_SOIL_CATALOG  = 2;   // Kazı Tipi Kataloğu
        public const int TAB_DOLGU_CATALOG = 3;   // Dolgu Kataloğu
        public const int TAB_MASTER_RULES  = 4;   // Baca Seçim Kuralları
        public const int TAB_MANHOLE_EXCAV = 5;   // Baca Kazı Kuralları
        public const int TAB_PIPE_TRENCH   = 6;   // Boru Hendek Kuralları

        public SmartAssemblyMasterCatalog MasterCatalog { get; }

        public RepositoryTabVm   RepositoryTab   { get; }
        public MasterRulesTabVm  MasterRulesTab  { get; }
        public ProjectRulesTabVm ProjectRulesTab { get; }

        public ManholeExcavationMainVm ManholeExcavTab { get; }
        public PipeCatalogMainVm       PipeCatalogTab  { get; }
        public PipeTrenchMainVm        PipeTrenchTab   { get; }
        public SoilCatalogVm           SoilCatalogTab  { get; }
        public DolguCatalogVm          DolguCatalogTab { get; }
        public TypeMappingTabVm        TypeMappingTab  { get; }

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                Set(ref _selectedTabIndex, value);
                if (value == TAB_MASTER_RULES) MasterRulesTab.RefreshBasesCombo();
                // ProjectSetup's tab-select refresh now lives in the Proje Ayarları
                // window's open path (SmartAssemblyCommand.ShowProjectSettings).
            }
        }

        public SmartAssemblyMainVm() : this(new SmartAssemblyMasterCatalog(), null) { }

        public SmartAssemblyMainVm(SmartAssemblyMasterCatalog catalog) : this(catalog, null) { }

        public SmartAssemblyMainVm(SmartAssemblyMasterCatalog catalog, PipeCatalog pipeCatalog)
        {
            MasterCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

            // RepositoryTab first: its constructor auto-loads Components from XML,
            // so _catalog.Components is populated before MasterRulesTab
            // calls RefreshBasesCombo() in its constructor.
            RepositoryTab = new RepositoryTabVm(MasterCatalog, onComponentsChanged: () =>
            {
                MasterRulesTab?.RefreshBasesCombo();
            });

            MasterRulesTab  = new MasterRulesTabVm (MasterCatalog, pipeCatalog);
            ProjectRulesTab = new ProjectRulesTabVm(pipeCatalog ?? PipeCatalogStore.Current, MasterCatalog);

            // Diğer kataloglar — her biri kendi XML deposundan otomatik yüklenir.
            ManholeExcavTab = new ManholeExcavationMainVm();
            PipeCatalogTab  = new PipeCatalogMainVm(pipeCatalog ?? PipeCatalogStore.Current);
            PipeTrenchTab   = new PipeTrenchMainVm();
            SoilCatalogTab  = new SoilCatalogVm();
            DolguCatalogTab = new DolguCatalogVm();
            // TypeMappingTab needs its DWG-bound links + save callback supplied
            // externally (see TypeMappingTabVm.Initialize) — SmartAssemblyCommand
            // does this right after construction, once the active Database is known.
            TypeMappingTab  = new TypeMappingTabVm(pipeCatalog ?? PipeCatalogStore.Current, MasterCatalog.Families);
        }

        /// <summary>
        /// Hot-swaps the pipe catalog after initial construction (called when the user
        /// imports or extracts a new catalog while the Smart Assembly window is open).
        /// </summary>
        /// <param name="updatePipeCatalogTab">
        /// Pass false when the call originates from inside PipeCatalogTab itself (e.g. Kaydet),
        /// to avoid a self-referential Clear() that empties the catalog.
        /// </param>
        public void RefreshPipeCatalog(PipeCatalog pipeCatalog, bool updatePipeCatalogTab = true)
        {
            MasterRulesTab.SetPipeCatalog(pipeCatalog);
            if (updatePipeCatalogTab)
                PipeCatalogTab.ApplyExtractedCatalog(pipeCatalog);
        }
    }
}
