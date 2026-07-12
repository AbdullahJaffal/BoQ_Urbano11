using Autodesk.AutoCAD.DatabaseServices;
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

        public static void LoadFromDwg(Database db)
        {
            _ruleSet = ProjectRulesNodManager.Load(db) ?? new ProjectRuleSet();
            _loaded  = true;
        }

        public static void Invalidate() => _loaded = false;

        /// <summary>True only when a rule set is loaded AND its mode is RULES (not TypeMapping).</summary>
        public static bool IsRulesMode
            => _loaded && _ruleSet != null && _ruleSet.CalcMode == CalcMode.Rules;

        public static NetworkRule FindNetwork(string systemName)
            => _loaded ? _ruleSet?.FindNetwork(systemName) : null;
    }
}
