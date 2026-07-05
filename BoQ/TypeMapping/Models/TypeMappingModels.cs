using System;

namespace UrbanoMetraj.BoQ.TypeMapping.Models
{
    /// <summary>
    /// Links one Urbano-embedded PIPE catalog item (identified by its GUID inside the
    /// project's own &lt;catalogs&gt; block) to a specific pipe in our own PipeCatalog.
    /// Global per Urbano-catalog-item-GUID across the whole DWG — NOT per network, since
    /// the same Urbano catalog item is by definition the same real product wherever it
    /// appears. See project memory "Catalog-Urbano linking design".
    /// </summary>
    public class PipeTypeLink
    {
        public string UrbanoCatalogItemGuid { get; set; } = "";

        /// <summary>Cached display name (e.g. "300 mm KRG") — for the mapping UI only, not a key.</summary>
        public string UrbanoCatalogItemName { get; set; } = "";

        /// <summary>References PipeCatalogs.Models.PipeDefinition.Id.</summary>
        public Guid LinkedPipeDefinitionId { get; set; }
    }

    /// <summary>How a manhole's diameter is resolved once its Urbano catalog item is linked.</summary>
    public enum ManholeDiameterMode
    {
        /// <summary>Use the diameter parsed from Urbano's own MANHOLE catalog item name (current default behavior).</summary>
        FollowDrawing,

        /// <summary>Compute the diameter from our own catalog rules (e.g. PipeRangeRule range lookup).</summary>
        ComputeFromCatalog
    }

    /// <summary>
    /// Links one Urbano-embedded MANHOLE catalog item to a diameter-resolution mode
    /// AND to a specific "Taban" (base/bottom) piece in our own component catalog —
    /// independently of DiameterMode. Our manhole catalog is built assembly-style,
    /// every stack sequenced from its base component, so the link target is a
    /// BottomElementComponent (via its ComponentFamily) rather than a flat
    /// diameter-keyed product row like PipeCatalog. Global per Urbano-catalog-item-
    /// GUID, same scoping rule as PipeTypeLink.
    /// </summary>
    public class ManholeTypeLink
    {
        public string UrbanoCatalogItemGuid { get; set; } = "";
        public string UrbanoCatalogItemName { get; set; } = "";
        public ManholeDiameterMode DiameterMode { get; set; } = ManholeDiameterMode.FollowDrawing;

        /// <summary>References SmartAssembly.Models.ComponentFamily.Id.</summary>
        public Guid LinkedFamilyId { get; set; }

        /// <summary>References SmartAssembly.Models.BottomElementComponent.Id (must belong to LinkedFamilyId).</summary>
        public Guid LinkedBottomComponentId { get; set; }
    }
}
