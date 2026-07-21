using System.Collections.Generic;
using UrbanoMetraj.BoQ.PipeTrenchCatalog.Models;

namespace UrbanoMetraj.BoQ.PipeTrenchCatalog.Services
{
    /// <summary>
    /// Session-scoped singleton, mirrors PipeCatalogStore.
    /// On first access, loads persisted rules from %APPDATA%\UrbanoMetraj\PipeTrenchCatalog.xml.
    /// Lets the BoQ calc engine resolve trench geometry from the same rule set the
    /// Hendek Kataloğu window edits, without re-reading the XML file per pipe.
    /// </summary>
    internal static class PipeTrenchCatalogStore
    {
        internal static readonly string DefaultSavePath = PipeTrenchCatalogXmlManager.InternalPath;

        private static List<PipeTrenchRule> _current;

        public static List<PipeTrenchRule> Current
        {
            get
            {
                if (_current == null)
                    _current = TryLoadFromDisk() ?? new List<PipeTrenchRule>();
                return _current;
            }
            set => _current = value ?? new List<PipeTrenchRule>();
        }

        // Attempts to load the saved rules; returns null on any failure.
        private static List<PipeTrenchRule> TryLoadFromDisk()
        {
            try   { return PipeTrenchCatalogXmlManager.LoadInternal(); }
            catch { return null; }
        }
    }
}
