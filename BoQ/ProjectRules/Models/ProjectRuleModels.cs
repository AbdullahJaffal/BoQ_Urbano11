using System;
using System.Collections.Generic;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.ProjectRules.Models
{
    /// <summary>
    /// Project-level exclusive switch that decides how the BoQ calc resolves pipe / manhole
    /// types. See PROJECT_RULES_REDESIGN.md.
    /// </summary>
    public enum CalcMode
    {
        /// <summary>Current behavior — resolve types from Tür Eşleştirme links + catalog MasterPipeRules.</summary>
        TypeMapping = 0,

        /// <summary>New behavior — resolve types from the per-network, project-scoped rule set below.</summary>
        Rules = 1
    }

    /// <summary>
    /// The whole DWG's project rule set. Persisted in NOD (see <c>ProjectRulesNodManager</c>) and
    /// exportable/importable as XML (see <c>ProjectRulesXmlManager</c>) — EXCEPT the AG_GUID-keyed
    /// exceptions, which are DWG-only and never travel in the portable XML template.
    ///
    /// This is a growing model: Step 1 covers the network defaults (pipe family/class, manhole
    /// family) + the mode switch. Connection rules, piece exclusions, excavation copies and the
    /// exception layer are added in their own implementation phases.
    /// </summary>
    public class ProjectRuleSet
    {
        public string SchemaVersion { get; set; } = "1.0";

        public CalcMode CalcMode { get; set; } = CalcMode.TypeMapping;

        /// <summary>One entry per active network (keyed by <see cref="NetworkRule.SystemName"/>).</summary>
        public List<NetworkRule> NetworkRules { get; set; } = new List<NetworkRule>();

        public NetworkRule FindNetwork(string systemName)
        {
            if (string.IsNullOrEmpty(systemName)) return null;
            foreach (var n in NetworkRules)
                if (string.Equals(n.SystemName, systemName, StringComparison.Ordinal))
                    return n;
            return null;
        }
    }

    /// <summary>
    /// Rules for a single network. Pipes resolve as (<see cref="PipeFamilyId"/>, <see cref="PipeSinif"/>)
    /// → PipeDefinition per drawn diameter; manholes resolve via <see cref="ManholeFamilyId"/> + the
    /// connection rules (added later). <see cref="SystemName"/> is the join key with the real
    /// network (SectionDebugRow.SystemName).
    /// </summary>
    public class NetworkRule
    {
        public string SystemName { get; set; } = "";

        // ── Pipes (network default) ───────────────────────────────────────────
        /// <summary>References PipeCatalogs.Models.PipeFamily.Id (Guid.Empty = not chosen).</summary>
        public Guid PipeFamilyId { get; set; }

        /// <summary>The pressure/stiffness class (Sınıf, e.g. "SN8") within the chosen family.</summary>
        public string PipeSinif { get; set; } = "";

        // ── Excavation ────────────────────────────────────────────────────────
        /// <summary>Zemin Tipi (soil classification) NAME for this network, from the Soil catalog.
        /// Drives excavation pricing (poz/bulking) and filters which trench/manhole-excavation rules
        /// apply (a rule's SelectedSoilNames must be empty or contain this). Empty = not chosen.</summary>
        public string SoilName { get; set; } = "";

        /// <summary>Filter: only these pipe-trench (boru hendek) rule NAMES apply. Empty = every rule that
        /// also passes the soil filter. Instead of editing rule values we just narrow the global catalog.</summary>
        public List<string> PipeTrenchRuleNames   { get; set; } = new List<string>();

        /// <summary>Filter: only these manhole-excavation (baca kazı) rule NAMES apply. Empty = all (soil-filtered).</summary>
        public List<string> ManholeExcavRuleNames { get; set; } = new List<string>();

        // ── Network kind ──────────────────────────────────────────────────────
        /// <summary>
        /// True = pressure / non-gravity network (BASINÇLI). Such networks have no manholes:
        /// the whole manhole pipeline (depth calc, precast piece stacking, pipe↔manhole linking,
        /// manhole rows/sheet and manhole-linking warnings) is SKIPPED for this network, while the
        /// pipe-trench geometry (excavation/bedding/surround/backfill) is computed unchanged — it is
        /// already pipe-level (from each node's terrain + pipe invert), independent of any manhole.
        /// Manual per-network flag (decision 2026-07-23). VANTUZ/fittings at the nodes are deferred.
        /// </summary>
        public bool IsBasincli { get; set; }

        // ── Manholes (network default) ────────────────────────────────────────
        /// <summary>References SmartAssembly.Models.ComponentFamily.Id (Guid.Empty = not chosen).</summary>
        public Guid ManholeFamilyId { get; set; }

        // ── Manhole connection rules (project copy of "baca seçim kuralları") ──
        // Per-network, imported from the catalog's MasterPipeRules then editable for this project.
        // Output is a specific Taban (by component Id) of this network's family — same as the
        // catalog's base-referencing PipeRangeRule (decision 2026-09-25; was a bare diameter).
        public List<ConnectionRule> ConnectionRules { get; set; } = new List<ConnectionRule>();

        // ── Piece exclusions ("remove pieces from use") — one row per (family, diameter) ──
        public List<PieceExclusionRow> PieceExclusionRows { get; set; } = new List<PieceExclusionRow>();

        // ── Per-entity overrides (AG_GUID-keyed), scoped to THIS network ──────
        // The pick is layer-filtered to this network, so an exception always belongs to it. DWG-only
        // (excluded from the portable XML template — they reference specific drawing entities).
        public ProjectExceptions Exceptions { get; set; } = new ProjectExceptions();
    }

    /// <summary>
    /// One "baca seçim" rule: a connected-pipe diameter band mapped, per depth tier, to a Taban of
    /// the network's chosen family.
    /// </summary>
    public class ConnectionRule
    {
        public Guid Id { get; set; } = Guid.NewGuid();

        /// <summary>Connected-pipe nominal diameter band (mm, inclusive).</summary>
        public double MinPipeMm { get; set; }
        public double MaxPipeMm { get; set; }

        public List<ConnDepthTier> Tiers { get; set; } = new List<ConnDepthTier>();
    }

    /// <summary>A depth band within a <see cref="ConnectionRule"/> → the Taban to use.</summary>
    public class ConnDepthTier
    {
        public double MinDepthM { get; set; }
        public double MaxDepthM { get; set; }

        /// <summary>The chosen Taban — a BottomElementComponent.Id of the network's manhole family
        /// (Guid.Empty = not chosen). The ring diameter follows its TopOpeningDiameterMm.</summary>
        public Guid TabanId { get; set; }

        public bool   IsCastInSitu { get; set; }
        public string Notes        { get; set; } = "";

        /// <summary>Per-role quantity constraints (min/max) for the manhole stack at this tier —
        /// imported from the catalog then editable per project. Same convention as
        /// SmartAssembly's DepthTierRule.ComponentConstraints (MaxCount -1 = unlimited, 0 = none).</summary>
        public List<ComponentTypeConstraint> ComponentConstraints { get; set; }
            = new List<ComponentTypeConstraint>();

        public ComponentTypeConstraint GetOrCreateConstraint(ComponentRole role)
        {
            foreach (var c in ComponentConstraints)
                if (c.Role == role) return c;
            var n = new ComponentTypeConstraint { Role = role };
            ComponentConstraints.Add(n);
            return n;
        }
    }

    /// <summary>
    /// A user-added "remove pieces from use" row, scoped to one (family, Taban). Its
    /// <see cref="Roles"/> hold the per-role height restrictions. The candidate (family, Taban) list
    /// comes from the network's main manhole family PLUS the families used by this network's manhole
    /// exceptions; each pair may appear at most once (decision 2026-07-11; keyed by Taban since 2026-09-25).
    /// </summary>
    public class PieceExclusionRow
    {
        /// <summary>References SmartAssembly.Models.ComponentFamily.Id.</summary>
        public Guid   ManholeFamilyId   { get; set; }

        /// <summary>The Taban (BottomElementComponent.Id) this row applies to.</summary>
        public Guid TabanId { get; set; }

        public List<PieceExclusion> Roles { get; set; } = new List<PieceExclusion>();
    }

    /// <summary>
    /// Restricts which heights of a manhole role are usable within a <see cref="PieceExclusionRow"/>.
    /// PRESENCE of a record for a role = "restrict to <see cref="AllowedHeightsMm"/>" (empty list =
    /// none allowed); ABSENCE of a record for the role = no restriction (all heights allowed).
    /// </summary>
    public class PieceExclusion
    {
        public ComponentRole Role { get; set; }
        public List<double> AllowedHeightsMm { get; set; } = new List<double>();
    }

    /// <summary>
    /// CAD-selection overrides that beat the per-network defaults for specific entities. Each
    /// dimension is an independent list keyed by AG_GUID — a conflict is only possible WITHIN one
    /// dimension (decision 3). Excavation-dimension exceptions are added with the excavation rules
    /// in a later phase.
    /// </summary>
    public class ProjectExceptions
    {
        public List<PipeFamilyException>    PipeFamily    { get; set; } = new List<PipeFamilyException>();
        public List<ManholeFamilyException> ManholeFamily { get; set; } = new List<ManholeFamilyException>();

        /// <summary>Excavation overrides (Zemin Tipi + Kural Adı) for specific pipes / manholes.</summary>
        public List<ExcavException> PipeExcav    { get; set; } = new List<ExcavException>();
        public List<ExcavException> ManholeExcav { get; set; } = new List<ExcavException>();

        public PipeFamilyException    FindPipe(string agGuid)    => Find(PipeFamily, agGuid);
        public ManholeFamilyException FindManhole(string agGuid) => Find(ManholeFamily, agGuid);
        public ExcavException FindPipeExcav(string agGuid)       => Find(PipeExcav, agGuid);
        public ExcavException FindManholeExcav(string agGuid)    => Find(ManholeExcav, agGuid);

        private static T Find<T>(List<T> list, string agGuid) where T : ExceptionBase
        {
            if (string.IsNullOrEmpty(agGuid)) return null;
            foreach (var e in list)
                if (string.Equals(e.AgGuid, agGuid, StringComparison.OrdinalIgnoreCase))
                    return e;
            return null;
        }
    }

    /// <summary>Entity class an exception pick is restricted to (decision: pipes = line/polyline,
    /// manholes = block/circle; text is always ignored).</summary>
    public enum ExceptionEntityKind
    {
        Pipe,
        Manhole
    }

    public abstract class ExceptionBase
    {
        /// <summary>AG_GUID (or TOPOGUID) of the target entity — the primary key for XML matching.</summary>
        public string AgGuid { get; set; } = "";

        /// <summary>Resolved display name from the exported XML (manhole AG_NAME or "sn → en" for a
        /// pipe). Cached so it survives a reload without re-parsing; refreshed by the "İsimleri
        /// Güncelle" button. Empty until the XML has been read.</summary>
        public string EntityName { get; set; } = "";
    }

    /// <summary>Overrides a pipe's resolved type (family + class) for one drawing entity.</summary>
    public sealed class PipeFamilyException : ExceptionBase
    {
        public Guid   PipeFamilyId { get; set; }
        public string PipeSinif    { get; set; } = "";
        /// <summary>Cached label for the UI (e.g. "koruge / SN8") — not a key.</summary>
        public string OverrideLabel { get; set; } = "";
    }

    /// <summary>
    /// Overrides a manhole's resolved family AND Taban for one drawing entity. Unlike pipes (whose
    /// diameter always comes from Urbano's drawing), a manhole's Taban comes from the connection
    /// rules — which are scoped to the network's DEFAULT family — so an exception must pin its own
    /// Taban, otherwise the default-family Taban won't exist in the exception family.
    /// </summary>
    public sealed class ManholeFamilyException : ExceptionBase
    {
        public Guid   ManholeFamilyId   { get; set; }

        /// <summary>The Taban (BottomElementComponent.Id of the exception family) to use for this
        /// manhole. Guid.Empty = not chosen (falls back to the connection-rule Taban).</summary>
        public Guid   TabanId           { get; set; }

        /// <summary>
        /// When true, this manhole is forced to CAST-IN-PLACE (Yerinde Döküm) regardless of the
        /// network's pre-cast family — no pre-cast stacking, its own concrete-volume BOM line.
        /// ManholeFamilyId/TabanId are then ignored. Resolution: ResolveFamilyAndTabanRules
        /// returns null family/taban so ProcessManhole leaves StackPreCast null (cast-in-place path).
        /// </summary>
        public bool   IsCastInPlace     { get; set; }

        public string OverrideLabel     { get; set; } = "";
    }

    /// <summary>
    /// Overrides the excavation resolution (Zemin Tipi + Kural Adı) for one drawing entity — used for
    /// both pipe-trench and manhole-excavation exceptions. When present, that entity's excavation is
    /// filtered by <see cref="SoilName"/> and (if non-empty) <see cref="RuleNames"/> instead of the
    /// network's. Empty RuleNames = every rule that passes the soil filter.
    /// </summary>
    public sealed class ExcavException : ExceptionBase
    {
        public string       SoilName      { get; set; } = "";
        public List<string> RuleNames     { get; set; } = new List<string>();
        public string       OverrideLabel { get; set; } = "";
    }
}
