using System;
using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// One depth tier inside a <see cref="PipeRangeRule"/>.
    /// Maps a burial-depth band to a specific base assembly (or cast-in-situ).
    /// </summary>
    public class DepthTierRule
    {
        public Guid   Id             { get; set; } = Guid.NewGuid();

        /// <summary>Lower bound of burial depth (m, inclusive).</summary>
        public double MinDepthM      { get; set; }

        /// <summary>Upper bound of burial depth (m, inclusive). 0 = unlimited.</summary>
        public double MaxDepthM      { get; set; }

        /// <summary>References <see cref="Models.BottomElementComponent.Id"/>. Ignored when <see cref="IsCastInSitu"/>.</summary>
        public Guid   SelectedBaseId { get; set; }

        public bool   IsCastInSitu   { get; set; }

        public string Notes          { get; set; }

        /// <summary>Per-role quantity constraints for this tier. MaxCount=0 means unlimited.</summary>
        public List<ComponentTypeConstraint> ComponentConstraints { get; set; }
            = new List<ComponentTypeConstraint>();

        /// <summary>Returns the existing constraint for a role, or creates and adds a new default one.</summary>
        public ComponentTypeConstraint GetOrCreateConstraint(ComponentRole role)
        {
            foreach (var c in ComponentConstraints)
                if (c.Role == role) return c;
            var newC = new ComponentTypeConstraint { Role = role };
            ComponentConstraints.Add(newC);
            return newC;
        }
    }
}
