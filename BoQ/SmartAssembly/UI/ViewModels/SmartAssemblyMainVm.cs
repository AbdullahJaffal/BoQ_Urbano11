using System;
using UrbanoMetraj.BoQ.DolguCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.PipeCatalogs.Services;
using UrbanoMetraj.BoQ.PipeCatalogs.UI.ViewModels;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.UI.ViewModels;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SoilCatalog.UI.ViewModels;

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
        public const int TAB_REPOSITORY    = 0;
        public const int TAB_MASTER_RULES  = 1;
        public const int TAB_PROJECT_SETUP = 2;
        public const int TAB_MANHOLE_EXCAV = 3;
        public const int TAB_PIPE_CATALOG  = 4;
        public const int TAB_PIPE_TRENCH   = 5;
        public const int TAB_SOIL_CATALOG  = 6;
        public const int TAB_DOLGU_CATALOG = 7;

        public SmartAssemblyMasterCatalog MasterCatalog { get; }

        public RepositoryTabVm   RepositoryTab   { get; }
        public MasterRulesTabVm  MasterRulesTab  { get; }
        public ProjectSetupTabVm ProjectSetupTab { get; }

        public ManholeExcavationMainVm ManholeExcavTab { get; }
        public PipeCatalogMainVm       PipeCatalogTab  { get; }
        public PipeTrenchMainVm        PipeTrenchTab   { get; }
        public SoilCatalogVm           SoilCatalogTab  { get; }
        public DolguCatalogVm          DolguCatalogTab { get; }

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                Set(ref _selectedTabIndex, value);
                if (value == TAB_MASTER_RULES) MasterRulesTab.RefreshBasesCombo();
                if (value == TAB_PROJECT_SETUP) { ProjectSetupTab.RefreshBasesCombo(); ProjectSetupTab.RefreshMaterialFamilies(); }
            }
        }

        public SmartAssemblyMainVm() : this(new SmartAssemblyMasterCatalog(), null) { }

        public SmartAssemblyMainVm(SmartAssemblyMasterCatalog catalog) : this(catalog, null) { }

        public SmartAssemblyMainVm(SmartAssemblyMasterCatalog catalog, PipeCatalog pipeCatalog)
        {
            MasterCatalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

            // RepositoryTab first: its constructor auto-loads Components from XML,
            // so _catalog.Components is populated before MasterRulesTab/ProjectSetupTab
            // call RefreshBasesCombo() in their constructors.
            RepositoryTab = new RepositoryTabVm(MasterCatalog, onComponentsChanged: () =>
            {
                MasterRulesTab ?.RefreshBasesCombo();
                ProjectSetupTab?.RefreshBasesCombo();
                ProjectSetupTab?.RefreshMaterialFamilies();
            });

            MasterRulesTab  = new MasterRulesTabVm (MasterCatalog, pipeCatalog);
            ProjectSetupTab = new ProjectSetupTabVm(MasterCatalog, pipeCatalog);

            // Diğer kataloglar — her biri kendi XML deposundan otomatik yüklenir.
            ManholeExcavTab = new ManholeExcavationMainVm();
            PipeCatalogTab  = new PipeCatalogMainVm(pipeCatalog ?? PipeCatalogStore.Current);
            PipeTrenchTab   = new PipeTrenchMainVm();
            SoilCatalogTab  = new SoilCatalogVm();
            DolguCatalogTab = new DolguCatalogVm();
        }

        /// <summary>
        /// Hot-swaps the pipe catalog after initial construction (called when the user
        /// imports or extracts a new catalog while the Smart Assembly window is open).
        /// </summary>
        public void RefreshPipeCatalog(PipeCatalog pipeCatalog)
        {
            MasterRulesTab .SetPipeCatalog(pipeCatalog);
            ProjectSetupTab.SetPipeCatalog(pipeCatalog);
            PipeCatalogTab .ApplyExtractedCatalog(pipeCatalog);
        }
    }
}
