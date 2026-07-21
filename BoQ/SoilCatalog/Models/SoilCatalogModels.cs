using System;

namespace UrbanoMetraj.BoQ.SoilCatalog.Models
{
    /// <summary>
    /// One soil type entry in the project soil classification catalog.
    /// BulkingFactor: bank-to-loose volume ratio (e.g. 1.20 = 20 % swell).
    /// BoqItemCode: pricing poz number used in the BOQ output sheets.
    /// </summary>
    public class SoilClassification
    {
        public Guid   Id            { get; set; } = Guid.NewGuid();
        public string SoilName      { get; set; } = "";
        public double BulkingFactor { get; set; } = 1.20;
        public string BoqItemCode   { get; set; } = "";
        public string KaziTipi      { get; set; } = "";
        public string Aciklama      { get; set; } = "";
    }
}
