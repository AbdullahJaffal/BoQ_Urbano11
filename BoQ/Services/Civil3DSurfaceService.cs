using System.Collections.Generic;
using Bricscad.ApplicationServices;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Civil 3D surface lookup — NOT AVAILABLE in the BricsCAD edition.
    ///
    /// The AutoCAD / Civil 3D build reads every TIN/Grid surface in the drawing
    /// (AeccDbMgd + Autodesk.Civil) to populate the "Kot Ayarları" surface picker.
    /// BricsCAD ships no Civil 3D managed API — there is no AeccDbMgd equivalent —
    /// so this edition returns an empty list.
    ///
    /// The signature is deliberately unchanged so every caller and the surface
    /// picker keep working. An empty list is exactly what the AutoCAD build
    /// already returns for a plain .dwg with no Civil document, and that path is
    /// handled everywhere.
    /// </summary>
    public static class Civil3DSurfaceService
    {
        public static List<string> GetSurfaceNames(Document doc)
        {
            return new List<string>();
        }
    }
}
