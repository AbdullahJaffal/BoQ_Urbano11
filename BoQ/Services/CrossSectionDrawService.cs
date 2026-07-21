using System;
using System.Collections.Generic;
using System.Linq;
using Bricscad.ApplicationServices;
using Teigha.Colors;
using Teigha.DatabaseServices;
using Bricscad.EditorInput;
using Teigha.Geometry;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Draws 2-D cross-section profiles for selected stations of a pipe network.
    ///
    /// Layout: stations are placed side by side horizontally from the insertion
    /// point. Each cross-section's local (U, Z) frame maps to (X, Y) in model
    /// space, with the pipe invert aligned to the insertion Y. Crossing-boundary
    /// stations are always included regardless of the sampling interval.
    ///
    /// One LWPolyline per polygon ring is written to dedicated layers:
    ///   URB_KS_Hafriyat   — excavation (ACI 30)
    ///   URB_KS_Yataklama  — bedding    (ACI 2)
    ///   URB_KS_Gomlekleme — surround   (ACI 3)
    ///   URB_KS_GeriDolgu  — backfill   (ACI 6)
    ///   URB_KS_Boru       — pipe body  (ACI 4)
    ///   URB_KS_Text       — labels     (ACI 7)
    /// </summary>
    public static class CrossSectionDrawService
    {
        // ── Layer / colour constants ──────────────────────────────────────────
        private const string LayerExcavation = "URB_KS_Hafriyat";
        private const string LayerBedding    = "URB_KS_Yataklama";
        private const string LayerSurround   = "URB_KS_Gomlekleme";
        private const string LayerBackfill   = "URB_KS_GeriDolgu";
        private const string LayerPipe       = "URB_KS_Boru";
        private const string LayerText       = "URB_KS_Text";

        private const short AciExcavation = 30;
        private const short AciBedding    = 2;
        private const short AciSurround   = 3;
        private const short AciBackfill   = 6;
        private const short AciPipe       = 4;
        private const short AciText       = 7;
        private const short AciBoundary   = 1;   // red — crossing-boundary label

        // ─────────────────────────────────────────────────────────────────────
        public static int Draw(
            BoQReport    report,
            string       systemName,    // null = draw all networks
            Point3d      insertPoint,
            TiePreference kazi,
            TiePreference dolgu,
            double       interval,
            Document     doc,
            Editor       ed)
        {
            if (report?.SectionDebug == null) return 0;

            var sections = report.SectionDebug
                .Where(r => systemName == null ||
                            string.Equals(r.SystemName, systemName,
                                          StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (sections.Count == 0)
            {
                ed?.WriteMessage($"\n[Sections] '{systemName}' için kesit bulunamadı.\n");
                return 0;
            }

            // Global geometry measurements across all qualifying stations.
            double maxHalfW   = 1.0;
            double maxSecH    = 2.0;   // max section height (used for text sizing)
            foreach (var sec in sections)
            {
                var qs = SelectStations(sec.Stations, interval);
                foreach (var st in qs)
                {
                    if (st.ExcavPoly == null || st.ExcavPoly.Count == 0) continue;
                    foreach (var p in st.ExcavPoly)
                        maxHalfW = Math.Max(maxHalfW, Math.Abs(p[0]));
                    double lo = st.ExcavPoly.Min(p => p[1]);
                    double hi = st.ExcavPoly.Max(p => p[1]);
                    maxSecH = Math.Max(maxSecH, hi - lo);
                }
            }
            // ── Layout constants ─────────────────────────────────────────────
            // Each station occupies one "slot" split into two side-by-side sub-panels:
            //
            //   [ EXCAV panel ] [subGap] [ FILL layers panel ] [interGap]
            //      (left)                     (right)
            //
            // Both panels share the same height scale (Z→Y) so the trapezoidal
            // excavation outline sits clearly beside the bedding/surround/backfill
            // stack instead of on top of it.
            double panelHalfW  = maxHalfW;            // half-width of each sub-panel
            double subGap      = panelHalfW * 0.15;   // gap between excav and fill panels
            double interGap    = panelHalfW * 0.20;   // gap between consecutive station slots
            // Slot width = excav(2×panelHalfW) + subGap + fill(2×panelHalfW)
            double colStep     = panelHalfW * 4.0 + subGap + interGap;
            // Excav sub-panel center is at slot-relative x = panelHalfW
            // Fill  sub-panel center is at slot-relative x = 3×panelHalfW + subGap
            double excavLocalX = panelHalfW;
            double fillLocalX  = panelHalfW * 3.0 + subGap;
            double labelLocalX = panelHalfW * 2.0 + subGap * 0.5; // slot center for labels
            double rowStep     = maxSecH * 1.3;        // based on actual section height
            double textHeight  = maxSecH * 0.04;       // 4% of section height

            var plines = new List<(Polyline pl, string layer, short aci)>();
            var texts  = new List<(DBText t, string layer, short aci)>();

            double rowOffsetY = 0.0;

            foreach (var sec in sections)
            {
                var qualStations = SelectStations(sec.Stations, interval);
                if (qualStations.Count == 0) continue;

                // Pipe-name label to the left of this row.
                // Replace Unicode arrows that SHX fonts render as '?'.
                string pipeLabel = (sec.PipeName ?? sec.SystemName ?? "")
                    .Replace("→", "->").Replace("←", "<-")
                    .Replace("↔", "<->").Replace("→", "->").Replace("←", "<-");
                texts.Add(MakeLabel(
                    pipeLabel,
                    insertPoint.X - panelHalfW * 1.5,
                    insertPoint.Y + rowOffsetY,
                    textHeight * 1.2,
                    LayerText, AciText));

                double slotOffsetX = 0.0;

                foreach (var st in qualStations)
                {
                    double refZ   = st.InvertZ;
                    double excavX = slotOffsetX + excavLocalX;  // excav panel center
                    double fillX  = slotOffsetX + fillLocalX;   // fill panel center

                    // ── LEFT SUB-PANEL: Excavation only ──────────────────────
                    AddPolylines(plines, GetRegion(st, TrenchLayerType.Excavation, kazi, dolgu, st.ExcavPoly),
                        insertPoint, excavX, rowOffsetY, refZ, LayerExcavation, AciExcavation);

                    // ── RIGHT SUB-PANEL: Fill layers + pipe ──────────────────
                    AddPolylines(plines, GetRegion(st, TrenchLayerType.Bedding,  kazi, dolgu, st.BeddingPoly),
                        insertPoint, fillX, rowOffsetY, refZ, LayerBedding,  AciBedding);
                    AddPolylines(plines, GetRegion(st, TrenchLayerType.Surround, kazi, dolgu, st.SurroundPoly),
                        insertPoint, fillX, rowOffsetY, refZ, LayerSurround, AciSurround);
                    AddPolylines(plines, GetRegion(st, TrenchLayerType.Backfill, kazi, dolgu, st.BackfillPoly),
                        insertPoint, fillX, rowOffsetY, refZ, LayerBackfill, AciBackfill);

                    var pipePoly = st.PipeBodyPoly;
                    if (pipePoly != null && pipePoly.Count >= 3)
                        AddPolylines(plines, new List<List<double[]>> { pipePoly },
                            insertPoint, fillX, rowOffsetY, refZ, LayerPipe, AciPipe);

                    // ── Station chainage label (above slot centre) ────────────
                    double topZ = st.ExcavPoly?.Count > 0
                        ? st.ExcavPoly.Max(p => p[1])
                        : (st.InvertZ + st.TrueDepth);
                    double labelY = insertPoint.Y + rowOffsetY + (topZ - refZ) + textHeight * 0.05;

                    string chainLabel = $"{st.StationDist:N1} m";
                    short  chainAci   = st.IsCrossingBoundary ? AciBoundary : AciText;
                    texts.Add(MakeLabel(chainLabel,
                        insertPoint.X + slotOffsetX + labelLocalX,
                        labelY, textHeight,
                        LayerText, chainAci));

                    slotOffsetX += colStep;
                }

                rowOffsetY -= rowStep;
            }

            // ── Write to model space ──────────────────────────────────────────
            int created = 0;
            using (var tr = doc.Database.TransactionManager.StartTransaction())
            {
                var btr = (BlockTableRecord)tr.GetObject(
                    SymbolUtilityServices.GetBlockModelSpaceId(doc.Database), OpenMode.ForWrite);

                foreach (var (pl, layer, aci) in plines)
                {
                    pl.LayerId = EnsureLayer(doc.Database, tr, layer, aci);
                    btr.AppendEntity(pl);
                    tr.AddNewlyCreatedDBObject(pl, true);
                    created++;
                }
                foreach (var (t, layer, aci) in texts)
                {
                    t.LayerId = EnsureLayer(doc.Database, tr, layer, aci);
                    btr.AppendEntity(t);
                    tr.AddNewlyCreatedDBObject(t, true);
                    created++;
                }
                tr.Commit();
            }

            ed?.WriteMessage($"\n[Sections] {created} nesne oluşturuldu.\n");
            return created;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Station sampling (same logic as SolidBuilderService)
        // ─────────────────────────────────────────────────────────────────────
        internal static List<CrossSectionStation> SelectStations(
            List<CrossSectionStation> stations, double interval)
        {
            var selected = new List<CrossSectionStation>();
            if (stations == null || stations.Count == 0) return selected;

            var boundaryDists = new SortedSet<double>(
                stations.Where(s => s.IsCrossingBoundary).Select(s => s.StationDist));

            int iStart = 0;
            while (true)
            {
                selected.Add(stations[iStart]);
                if (iStart >= stations.Count - 1) break;

                double currentDist = stations[iStart].StationDist;
                double targetDist  = currentDist + interval;

                var view = boundaryDists.GetViewBetween(currentDist + 1e-6, targetDist - 1e-6);
                if (view.Count > 0) targetDist = view.Min;

                int j = stations.Count - 1;
                for (int k = iStart + 1; k < stations.Count; k++)
                {
                    if (stations[k].StationDist >= targetDist - 1e-6)
                    {
                        j = k;
                        break;
                    }
                }

                iStart = j;
            }

            return selected;
        }

        // ─────────────────────────────────────────────────────────────────────
        // Polygon helpers
        // ─────────────────────────────────────────────────────────────────────

        /// <summary>Returns the net region for a layer under the chosen preference.</summary>
        private static List<List<double[]>> GetRegion(
            CrossSectionStation st, TrenchLayerType layer,
            TiePreference kazi, TiePreference dolgu, List<double[]> grossFallback)
        {
            TiePreference pref = layer == TrenchLayerType.Excavation ? kazi : dolgu;

            if (pref == TiePreference.Ignore)
                return grossFallback != null ? new List<List<double[]>> { grossFallback } : null;

            var prof = st.Scenario(pref);
            var ln   = prof?.Layer(layer);
            if (ln?.Polygon?.Count > 0 && ln.Polygon.Any(r => r?.Count >= 3))
                return ln.Polygon;

            return grossFallback != null ? new List<List<double[]>> { grossFallback } : null;
        }

        /// <summary>Largest single ring from a scenario layer (used for simple fallback).</summary>
        private static List<double[]> PickPoly(
            CrossSectionStation st, TrenchLayerType layer,
            TiePreference kazi, TiePreference dolgu)
        {
            TiePreference pref = layer == TrenchLayerType.Excavation ? kazi : dolgu;
            if (pref == TiePreference.Ignore) return null;
            var prof = st.Scenario(pref);
            var ln   = prof?.Layer(layer);
            if (ln?.Polygon == null) return null;
            return LargestRing(ln.Polygon);
        }

        private static List<double[]> LargestRing(List<List<double[]>> region)
        {
            List<double[]> best = null;
            double bestArea = -1;
            foreach (var r in region)
            {
                if (r == null || r.Count < 3) continue;
                double a = PolyArea2D(r);
                if (a > bestArea) { bestArea = a; best = r; }
            }
            return best;
        }

        private static double PolyArea2D(List<double[]> ring)
        {
            double sum = 0;
            int n = ring.Count;
            for (int i = 0; i < n; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % n];
                sum += a[0] * b[1] - b[0] * a[1];
            }
            return Math.Abs(sum * 0.5);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Drawing helpers
        // ─────────────────────────────────────────────────────────────────────

        private static void AddPolylines(
            List<(Polyline, string, short)> list,
            List<List<double[]>> region,
            Point3d origin, double colX, double rowY, double refZ,
            string layer, short aci)
        {
            if (region == null) return;
            foreach (var ring in region)
            {
                if (ring == null || ring.Count < 3) continue;
                var pl = MakePolyline(ring, origin, colX, rowY, refZ);
                if (pl != null) list.Add((pl, layer, aci));
            }
        }

        private static Polyline MakePolyline(
            List<double[]> ring, Point3d origin,
            double colX, double rowY, double refZ)
        {
            if (ring == null || ring.Count < 3) return null;
            var pl = new Polyline();
            pl.Closed = true;
            int vi = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                if (ring[i] == null || ring[i].Length < 2) continue;
                double u = ring[i][0];
                double z = ring[i][1];
                pl.AddVertexAt(vi++,
                    new Point2d(origin.X + colX + u,
                                origin.Y + rowY + (z - refZ)),
                    0, 0, 0);
            }
            if (vi < 3) { pl.Dispose(); return null; }
            return pl;
        }

        private static (DBText, string, short) MakeLabel(
            string text, double x, double y, double height, string layer, short aci)
        {
            var t = new DBText
            {
                TextString = text,
                Height     = Math.Max(0.01, height),
                Position   = new Point3d(x, y, 0),
                Normal     = Vector3d.ZAxis
            };
            return (t, layer, aci);
        }

        // ─────────────────────────────────────────────────────────────────────
        private static ObjectId EnsureLayer(Database db, Transaction tr, string name, short aci)
        {
            var lt = (LayerTable)tr.GetObject(db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) return lt[name];
            lt.UpgradeOpen();
            var ltr = new LayerTableRecord
            {
                Name  = name,
                Color = Color.FromColorIndex(ColorMethod.ByAci, aci)
            };
            var id = lt.Add(ltr);
            tr.AddNewlyCreatedDBObject(ltr, true);
            return id;
        }
    }
}
