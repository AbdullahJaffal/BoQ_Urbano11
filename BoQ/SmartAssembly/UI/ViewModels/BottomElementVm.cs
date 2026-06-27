using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Lightweight wrapper around BottomElementComponent for ComboBox binding.</summary>
    public class BottomElementVm
    {
        public BottomElementVm(BottomElementComponent model)
        {
            Id      = model?.Id   ?? Guid.Empty;
            Display = model?.Name ?? "(yok)";
        }

        public Guid   Id      { get; }
        public string Display { get; }
    }
}
