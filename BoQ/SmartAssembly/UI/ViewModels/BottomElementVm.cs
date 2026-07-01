using System;
using UrbanoMetraj.BoQ.SmartAssembly.Models;

namespace UrbanoMetraj.BoQ.SmartAssembly.UI.ViewModels
{
    /// <summary>Lightweight wrapper around BottomElementComponent for ComboBox binding.</summary>
    public class BottomElementVm
    {
        public BottomElementVm(BottomElementComponent model)
        {
            Id = model?.Id ?? Guid.Empty;
            if (model == null)
                Display = "(yok)";
            else if (model.IsVariable)
                Display = string.Format("{0}  —  değişken", model.Name);
            else
                Display = string.Format("{0}  —  {1} mm", model.Name, (int)model.EffectiveHeight);
        }

        public Guid   Id      { get; }
        public string Display { get; }
    }
}
