using System;

namespace UrbanoMetraj.BoQ.SmartAssembly.Models
{
    /// <summary>A named network/system type (e.g. "Yağmur Suyu", "Atık Su") used to filter rule matrices.</summary>
    public class SystemType
    {
        public Guid   Id   { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = "";
    }
}
