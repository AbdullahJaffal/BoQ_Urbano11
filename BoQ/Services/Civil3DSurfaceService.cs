using System.Collections.Generic;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.Civil.ApplicationServices;
using CivilSurface = Autodesk.Civil.DatabaseServices.Surface;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Reads the names of every Civil 3D surface (TIN/Grid) present in the active
    /// drawing, for use in the "Kot Ayarları" surface picker dropdowns.
    /// </summary>
    public static class Civil3DSurfaceService
    {
        public static List<string> GetSurfaceNames(Document doc)
        {
            var names = new List<string>();
            if (doc == null) return names;
            try
            {
                var civilDoc = CivilDocument.GetCivilDocument(doc.Database);
                using (var tr = doc.Database.TransactionManager.StartTransaction())
                {
                    foreach (ObjectId id in civilDoc.GetSurfaceIds())
                    {
                        if (tr.GetObject(id, OpenMode.ForRead) is CivilSurface surf)
                            names.Add(surf.Name);
                    }
                    tr.Commit();
                }
            }
            catch (System.Exception ex)
            {
                // No Civil document / no surfaces → return an empty picker list, but
                // record why so an empty surface dropdown isn't a total mystery.
                System.Diagnostics.Trace.WriteLine(
                    $"[UrbanoMetraj] Civil 3D yüzey adları okunamadı: {ex.Message}");
            }
            return names;
        }
    }
}
