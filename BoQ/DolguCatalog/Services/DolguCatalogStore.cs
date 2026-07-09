using System.Collections.ObjectModel;
using UrbanoMetraj.BoQ.DolguCatalog.Models;

namespace UrbanoMetraj.BoQ.DolguCatalog.Services
{
    internal static class DolguCatalogStore
    {
        private static ObservableCollection<DolguMalzemesi> _items;

        internal static ObservableCollection<DolguMalzemesi> Items
        {
            get
            {
                if (_items == null)
                    _items = TryLoad();
                return _items;
            }
            set => _items = value ?? new ObservableCollection<DolguMalzemesi>();
        }

        private static ObservableCollection<DolguMalzemesi> TryLoad()
        {
            var list = new ObservableCollection<DolguMalzemesi>();
            try
            {
                foreach (var d in DolguCatalogXmlManager.LoadInternal())
                    list.Add(d);
            }
            catch (System.Exception ex)
            {
                System.Diagnostics.Trace.WriteLine(
                    $"[UrbanoMetraj] Dolgu kataloğu yüklenemedi: {ex.Message}");
            }
            return list;
        }
    }
}
