using System.Collections.Generic;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>
    /// Min/max quantity constraint for one component role within a depth tier.
    /// MaxCount = 0 means unlimited (same convention as MaxDepthM).
    /// </summary>
    public class ComponentTypeConstraint
    {
        public ComponentRole Role     { get; set; }
        public int           MinCount { get; set; } = 0;
        /// <summary>-1 = unlimited (∞); 0 = don't use; N>0 = use at most N.</summary>
        public int           MaxCount { get; set; } = -1;
    }
}
