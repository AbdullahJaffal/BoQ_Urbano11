using System.Collections.ObjectModel;
using UrbanoMetraj.BoQ.SoilCatalog.Models;

namespace UrbanoMetraj.BoQ.SoilCatalog.Services
{
    /// <summary>
    /// Session-scoped singleton for the soil classification catalog.
    /// Loaded once from disk; shared between SOIL_CATALOG and PIPE_TRENCH_CATALOG
    /// so that zemin-tipi filters stay synchronized with the active soil catalog.
    /// </summary>
    internal static class SoilCatalogStore
    {
        private static ObservableCollection<SoilClassification> _items;

        internal static ObservableCollection<SoilClassification> Items
        {
            get
            {
                if (_items == null)
                    _items = TryLoad();
                return _items;
            }
            set => _items = value ?? new ObservableCollection<SoilClassification>();
        }

        private static ObservableCollection<SoilClassification> TryLoad()
        {
            var list = new ObservableCollection<SoilClassification>();
            try
            {
                foreach (var s in SoilCatalogXmlManager.LoadInternal())
                    list.Add(s);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[UrbanoMetraj] Zemin kataloğu yüklenemedi: {ex.Message}");
            }
            return list;
        }
    }
}
