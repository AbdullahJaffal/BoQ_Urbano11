using System;

namespace UrbanoMetraj.BoQ.DolguCatalog.Models
{
    public class DolguMalzemesi
    {
        public Guid   Id            { get; set; } = Guid.NewGuid();
        public string BoqItemCode   { get; set; } = "";
        public string DolguAdi      { get; set; } = "";
        public string Aciklama      { get; set; } = "";
    }
}
