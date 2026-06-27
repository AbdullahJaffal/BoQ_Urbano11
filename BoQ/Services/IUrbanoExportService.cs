using System.Threading;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Drives the Urbano "ars_export_xml" modal dialog via UI Automation.
    /// Must be called from a background thread while AutoCAD's main thread
    /// is blocked inside the modal dialog.
    /// </summary>
    public interface IUrbanoExportService
    {
        /// <summary>
        /// Waits for the Urbano export dialog to appear, fills in the file path,
        /// checks all system checkboxes, and clicks the export button.
        /// Returns true when the output file is confirmed written to disk.
        /// </summary>
        bool WaitAndAutomate(string exportPath, CancellationToken cancellationToken);
    }
}
