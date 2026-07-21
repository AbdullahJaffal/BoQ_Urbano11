using Teigha.DatabaseServices;
using UrbanoMetraj.BoQ.ProjectRules.Models;

namespace UrbanoMetraj.BoQ.ProjectRules.Services
{
    /// <summary>
    /// Session cache of the active DWG's <see cref="ProjectRuleSet"/>, mirroring
    /// <c>TypeMappingStore</c>: NOD reads need a transaction, so the calc loads the rule set once
    /// (via <see cref="LoadFromDwg"/>) and then reads the per-network rules from memory during the
    /// parse. When <see cref="IsRulesMode"/> is false the calc keeps its current Tür Eşleştirme path.
    /// </summary>
    internal static class ProjectRulesStore
    {
        private static ProjectRuleSet _ruleSet;
        private static bool _loaded;

        /// <summary>
        /// HOLD (2026-07): while the Tür Eşleştirme (Type Mapping) path is hidden from the UI
        /// as incomplete, the calc is forced onto the Rules path whenever a rule set is loaded.
        /// Set to <c>false</c> to restore the user-selectable TypeMapping / Rules switch.
        /// </summary>
        private const bool HoldForceRules = true;

        public static void LoadFromDwg(Database db)
        {
            _ruleSet = ProjectRulesNodManager.Load(db) ?? new ProjectRuleSet();
            _loaded  = true;
        }

        public static void Invalidate() => _loaded = false;

        /// <summary>
        /// True when a rule set is loaded AND its mode is RULES — or, while <see cref="HoldForceRules"/>
        /// is set, whenever a rule set is loaded (TypeMapping is on HOLD).
        /// </summary>
        public static bool IsRulesMode
            => _loaded && _ruleSet != null && (HoldForceRules || _ruleSet.CalcMode == CalcMode.Rules);

        public static NetworkRule FindNetwork(string systemName)
            => _loaded ? _ruleSet?.FindNetwork(systemName) : null;
    }
}
