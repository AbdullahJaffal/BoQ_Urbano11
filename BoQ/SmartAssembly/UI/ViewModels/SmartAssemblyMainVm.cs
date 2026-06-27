using System;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>
    /// Root ViewModel for the Smart Assembly modeless window.
    /// Owns the shared <see cref="SmartAssemblyMasterCatalog"/>, the three tab ViewModels,
    /// and an optional <see cref="PipeCatalog"/> for the cascading pipe-range ComboBoxes.
    /// </summary>
    public class SmartAssemblyMainVm : ViewModelBase
    {
        public SmartAssemblyMasterCatalog MasterCatalog { get; }

        public RepositoryTabVm   RepositoryTab   { get; }
        public MasterRulesTabVm  MasterRulesTab  { get; }
        public ProjectSetupTabVm ProjectSetupTab { get; }

        private int _selectedTabIndex;
        public int SelectedTabIndex
        {
            get => _selectedTabIndex;
            set
            {
                Set(ref _selectedTabIndex, value);
                if (value == 1) MasterRulesTab.RefreshBasesCombo();
                if (value == 2) { ProjectSetupTab.RefreshBasesCombo(); ProjectSetupTab.RefreshMaterialFamilies(); }
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
        }

        /// <summary>
        /// Hot-swaps the pipe catalog after initial construction (called when the user
        /// imports or extracts a new catalog while the Smart Assembly window is open).
        /// </summary>
        public void RefreshPipeCatalog(PipeCatalog pipeCatalog)
        {
            MasterRulesTab .SetPipeCatalog(pipeCatalog);
            ProjectSetupTab.SetPipeCatalog(pipeCatalog);
        }
    }
}
