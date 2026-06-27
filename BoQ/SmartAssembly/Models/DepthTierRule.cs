using System;

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
    }
}
