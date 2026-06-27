using UrbanoMetraj.BoQ.PipeCatalogs.Models;

namespace UrbanoMetraj.BoQ.PipeCatalogs.Services
{
    /// <summary>
    /// Session-scoped singleton.  Shared between PIPE_CATALOG and SMART_ASSEMBLY
    /// so that pipe-range rules in the manhole window stay synchronized with
    /// whatever catalog was last imported or extracted.
    /// </summary>
    internal static class PipeCatalogStore
    {
        public static PipeCatalog Current { get; set; } = new PipeCatalog();
    }
}
