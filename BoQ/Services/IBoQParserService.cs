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
        /// </summary>
        BoQReport Parse(string xmlPath, Editor ed = null);
    }
}
