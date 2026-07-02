using System.IO;
using UrbanoMetraj.BoQ.SmartAssembly.Models;
using UrbanoMetraj.BoQ.SmartAssembly.Serialization;

namespace UrbanoMetraj.BoQ.SmartAssembly.Services
{
    /// <summary>
    /// Session-scoped singleton for the Smart Assembly component catalog.
    /// When the SMART_ASSEMBLY window is open, <see cref="Current"/> returns its live
    /// in-memory <see cref="SmartAssemblyMasterCatalog"/> directly so that any
    /// components added during the session are visible immediately.
    /// When the window is closed, falls back to a disk-cached load.
    /// </summary>
    internal static class SmartAssemblyCatalogStore
    {
        private static SmartAssemblyMasterCatalog _current;

        internal static SmartAssemblyMasterCatalog Current
        {
            get
            {
                // If the Smart Assembly window is open, use its live in-memory catalog
                // so that newly added families/tabans are visible without restarting.
                var liveVm = SmartAssemblyCommand.GetMainVm();
                if (liveVm != null) return liveVm.MasterCatalog;

                // Fallback: disk-cached catalog (loaded once per session).
                if (_current == null)
                    _current = TryLoad();
                return _current;
            }
            set => _current = value ?? new SmartAssemblyMasterCatalog();
        }

        /// <summary>
        /// Forces the next access to reload from disk (call after the SA window closes
        /// if you need the on-disk version to be fresh).
        /// </summary>
        internal static void Invalidate() { _current = null; }

        private static SmartAssemblyMasterCatalog TryLoad()
        {
            var catalog = new SmartAssemblyMasterCatalog();
            try
            {
                string path = MasterCatalogXmlManager.DefaultComponentsPath;
                if (File.Exists(path))
                    foreach (var f in MasterCatalogXmlManager.ImportFamilies(path))
                        catalog.Families.Add(f);
            }
            catch { }
            return catalog;
        }
    }
}
