using System.Collections.Generic;
using Autodesk.AutoCAD.EditorInput;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Parses the XML file produced by Urbano's "ars_export_xml" command
    /// and returns an aggregated <see cref="BoQReport"/>.
    /// </summary>
    public interface IBoQParserService
    {
        /// <summary>
        /// Parse the export and return a BoQReport.
        /// If <paramref name="ed"/> is supplied the parser writes a real-time
        /// schema dump of the first pipe section and first manhole node
        /// directly to the AutoCAD command line before running the BoQ
        /// aggregation — no guessing required to find the exact attribute names.
        /// <paramref name="settings"/> drives the Kot Ayarları/Seviye Ayarları
        /// elevation resolution (ZKazi/ZDolgu/ZBacaKapak) — null uses the
        /// BoQSettings defaults (Kırmızı Kot = Arazi1 = TH1 for all three),
        /// preserving the historical "always TH1" behaviour.
        /// <paramref name="activeSystems"/> scopes the parse to the given network
        /// names (URBANOLOCK — Ağ Seçimi "Aktif" set): nodes/sections of any other
        /// network are skipped entirely, so their volumes AND warnings never appear.
        /// null (default) parses the whole drawing, as before.
        /// <paramref name="selectionScope"/> further restricts the calc to a drawing
        /// selection (CAD Seçimi): only the sections the picked manholes/pipes resolve
        /// to are kept for clash + output. null (default) = no selection restriction.
        /// </summary>
        BoQReport Parse(string xmlPath, Editor ed = null, BoQSettings settings = null,
                        ISet<string> activeSystems = null, SelectionScope selectionScope = null);
    }
}
