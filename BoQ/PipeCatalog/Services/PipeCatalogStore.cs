using System;
using System.IO;
using UrbanoMetraj.BoQ.PipeCatalogs.Models;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Services
{
    /// <summary>
    /// Session-scoped singleton.
    /// On first access, attempts to load from the user's roaming-profile save file.
    /// Shared between PIPE_CATALOG and SMART_ASSEMBLY so that pipe-range rules
    /// in the manhole window stay synchronized with whatever catalog is active.
    /// </summary>
    internal static class PipeCatalogStore
    {
        internal static readonly string DefaultSavePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "UrbanoMetraj", "PipeCatalog.xml");

        private static PipeCatalog _current;

        public static PipeCatalog Current
        {
            get
            {
                if (_current == null)
                    _current = TryLoadFromDisk() ?? new PipeCatalog();
                return _current;
            }
            set => _current = value ?? new PipeCatalog();
        }

        // Attempts to load the saved native-format catalog; returns null on any failure.
        private static PipeCatalog TryLoadFromDisk()
        {
            if (!File.Exists(DefaultSavePath)) return null;
            try   { return PipeCatalogImportService.ImportNativeXml(DefaultSavePath); }
            catch { return null; }
        }
    }
}
