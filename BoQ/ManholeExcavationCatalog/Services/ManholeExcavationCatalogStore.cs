using System.Collections.Generic;
using UrbanoMetraj.BoQ.ManholeExcavationCatalog.Models;

namespace UrbanoMetraj.BoQ.ManholeExcavationCatalog.Services
{
    /// <summary>
    /// Session-scoped singleton, mirrors PipeCatalogStore.
    /// On first access, loads persisted rules from %APPDATA%\UrbanoMetraj\ManholeExcavCatalog.xml.
    /// Lets the BoQ calc engine resolve manhole excavation pit geometry from the same
    /// rule set the Baca Kazı Kataloğu window edits, without re-reading the XML file
    /// per manhole.
    /// </summary>
    internal static class ManholeExcavationCatalogStore
    {
        internal static readonly string DefaultSavePath = ManholeExcavationCatalogXmlManager.InternalPath;

        private static List<ManholeExcavationRule> _current;

        public static List<ManholeExcavationRule> Current
        {
            get
            {
                if (_current == null)
                    _current = TryLoadFromDisk() ?? new List<ManholeExcavationRule>();
                return _current;
            }
            set => _current = value ?? new List<ManholeExcavationRule>();
        }

        // Attempts to load the saved rules; returns null on any failure.
        private static List<ManholeExcavationRule> TryLoadFromDisk()
        {
            try   { return ManholeExcavationCatalogXmlManager.LoadInternal(); }
            catch { return null; }
        }
    }
}
