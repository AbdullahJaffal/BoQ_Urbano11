using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Sections
{
    public enum DrawPrimKind { Ring = 0, Polyline = 1, Hatch = 2, Text = 3 }

    public enum TextAnchor { BottomLeft, BottomCenter, BottomRight, MiddleLeft, MiddleCenter, MiddleRight, TopLeft, TopCenter, TopRight }

    /// <summary>One drawing primitive in sheet coordinates (metres, origin = insertion point).</summary>
    public sealed class DrawPrim
    {
        public DrawPrimKind Kind;
        public string       Element;
        /// <summary>Network the geometry belongs to (fills the {AG} layer token; colours clashing pipes per network).</summary>
        public string       Network;
        /// <summary>True for geometry of a clashing pipe (Diğer* elements).</summary>
        public bool         IsOther;
        /// <summary>Nominal diameter of the pipe the geometry belongs to (fills the {CAP} layer token).</summary>
        public int          Diameter;
        /// <summary>Catalog sub-layer of a band hatch (its own <see cref="SubLayerStyle"/>), else null.</summary>
        public string       SubLayer;
        public List<List<double[]>> Loops;   // Ring / Hatch
        public List<double[]>       Points;  // Polyline (open)
        public string     Text;
        public double     X, Y, Height, Rotation;
        public TextAnchor Anchor;
    }

    /// <summary>Resolved hatch parameters of one hatch primitive.</summary>
    public struct HatchParams
    {
        public string Pattern;
        public double Scale, Angle;
        /// <summary>256 = element / network colour.</summary>
        public short  ColorAci;
        public int    Transparency;
    }

    public sealed class SectionDrawing
    {
        public int        Index;
        public SectionCut Cut;
        /// <summary>Elevation of the datum line (KK) at local y = 0.</summary>
        public double     Datum;
        public double     MinX, MinY, MaxX, MaxY;     // sheet bbox of the slot
        public List<DrawPrim> Prims = new List<DrawPrim>();
    }

    public sealed class SectionSheet
    {
        public List<SectionDrawing> Sections   = new List<SectionDrawing>();
        public List<DrawPrim>       SheetPrims = new List<DrawPrim>();
        public double MinX, MinY, MaxX, MaxY;
    }

    /// <summary>
    /// Places cross-sections on a sheet and turns each into primitives according to a
    /// <see cref="SectionStyle"/>. AutoCAD-free — the AutoCAD renderer and the panel's
    /// preview both consume its output. Horizontal model units are metres (plotted at
    /// 1:HorizontalScale); elevations are stretched by the vertical exaggeration H/V.
    /// Every dimension/level text shows the TRUE value, never the stretched one.
    /// </summary>
    public static class SectionLayoutEngine
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public static string FormatChainage(double d, ChainageFormat f)
        {
            if (double.IsNaN(d)) return "-";
            if (f == ChainageFormat.Metric) return d.ToString("F2", Inv) + " m";
            int km = (int)Math.Floor(d / 1000.0 + 1e-9);
            double m = d - km * 1000.0;
            return km.ToString(Inv) + "+" + m.ToString("000.00", Inv);
        }

        public static string ScenarioLabel(TiePreference p)
        {
            switch (p)
            {
                case TiePreference.KeepUpper: return "Üst boru korur";
                case TiePreference.KeepLower: return "Alt boru korur";
                case TiePreference.Split:     return "%50 paylaşım";
                default:                      return "Kesişim yok sayıldı";
            }
        }

        // ── Sheet ────────────────────────────────────────────────────────────

        /// <summary>Lays out the cuts in order. Cuts of one pipe are consecutive in <paramref name="cuts"/>.</summary>
        public static SectionSheet Layout(IList<SectionCut> cuts, SectionStyle style)
        {
            var sheet = new SectionSheet();
            if (cuts == null || cuts.Count == 0) return sheet;
            double gapX = style.ModelText(style.GapXmm), gapY = style.ModelText(style.GapYmm);
            int cols = Math.Max(1, style.Columns);

            // Rows of cut indices. Comparison mode: one row per station (all its scenarios).
            var rowIdx = new List<List<int>>();
            List<int> curIdx = null;
            for (int i = 0; i < cuts.Count; i++)
            {
                bool newPipe = i == 0 || cuts[i].PipeIndex != cuts[i - 1].PipeIndex;
                bool compare = cuts[i].CompareGroup >= 0;
                bool newGroup = compare && (i == 0 || cuts[i].CompareGroup != cuts[i - 1].CompareGroup);
                if (curIdx == null || newGroup
                    || (!compare && (curIdx.Count >= cols || (style.Ordering == SectionOrdering.RowPerPipe && newPipe))))
                    rowIdx.Add(curIdx = new List<int>());
                curIdx.Add(i);
            }

            // Slots in local coordinates (datum line at y = 0); a shared datum per row when asked
            // (always for a comparison row — it is one station).
            var rows = new List<List<SectionDrawing>>();
            foreach (var ri in rowIdx)
            {
                bool common = style.RowCommonDatum || cuts[ri[0]].CompareGroup >= 0;
                double? datum = common ? ri.Min(i => DatumOf(cuts[i])) : (double?)null;
                rows.Add(ri.Select(i => BuildSlot(cuts[i], style, i, datum)).ToList());
            }

            double y = 0;   // top of the current row
            bool first = true;
            foreach (var row in rows)
            {
                double top = row.Max(s => s.MaxY), bottom = row.Min(s => s.MinY);
                double baseY = first ? -top : y - gapY - top;   // datum line of this row
                first = false;
                double x = 0;
                foreach (var s in row)
                {
                    double dx = x - s.MinX, dy = baseY;
                    Shift(s, dx, dy);
                    x = s.MaxX + gapX;
                    sheet.Sections.Add(s);
                }
                if (style.ShowRowTitle)
                {
                    var c0 = row[0].Cut;
                    string t = Sanitize(c0.PipeName) + (style.ShowNetwork && !string.IsNullOrEmpty(c0.SystemName) ? "  [" + c0.SystemName + "]" : "");
                    if (c0.CompareGroup >= 0)
                        t += "  " + FormatChainage(c0.LocalChainage, style.ChainageFormat) + "  — senaryo karşılaştırması";
                    sheet.SheetPrims.Add(Txt(SectionElements.Baslik, t, row[0].MinX, baseY + top + style.ModelText(style.TitleHeightMm) * 0.8,
                                             style.ModelText(style.TitleHeightMm) * 1.2, TextAnchor.BottomLeft));
                }
                y = baseY + bottom;
            }

            var all = sheet.Sections.SelectMany(s => s.Prims).Concat(sheet.SheetPrims).ToList();
            Bounds(all, out sheet.MinX, out sheet.MinY, out sheet.MaxX, out sheet.MaxY);
            return sheet;
        }

        private static void Shift(SectionDrawing s, double dx, double dy)
        {
            foreach (var p in s.Prims) ShiftPrim(p, dx, dy);
            s.MinX += dx; s.MaxX += dx; s.MinY += dy; s.MaxY += dy;
        }

        private static void ShiftPrim(DrawPrim p, double dx, double dy)
        {
            if (p.Loops != null) foreach (var l in p.Loops) foreach (var v in l) { v[0] += dx; v[1] += dy; }
            if (p.Points != null) foreach (var v in p.Points) { v[0] += dx; v[1] += dy; }
            p.X += dx; p.Y += dy;
        }

        // ── One section ──────────────────────────────────────────────────────

        /// <summary>Datum (KK) elevation of one section: 0.25 m below its lowest point, on a 0.5 m step.</summary>
        public static double DatumOf(SectionCut c) => Math.Floor((c.MinZ - 0.25) / 0.5) * 0.5;

        public static SectionDrawing BuildSlot(SectionCut c, SectionStyle st, int index, double? datumOverride = null)
        {
            var d = new SectionDrawing { Index = index, Cut = c };
            var P = d.Prims;
            double ve = st.VerticalExaggeration;
            double th = st.ModelText(st.TextHeightMm);
            double datum = datumOverride ?? DatumOf(c);
            d.Datum = datum;
            double Y(double z) => (z - datum) * ve;
            List<double[]> Map(List<double[]> ring) => ring.Select(v => new[] { v[0], Y(v[1]) }).ToList();
            List<List<double[]>> MapR(List<List<double[]>> rings) => rings.Select(Map).ToList();
            bool Vis(string key) => st.E(key).Visible;

            void Region(string key, List<List<double[]>> rings, bool other, string net, bool hatch = true, int dia = 0)
            {
                if (rings == null || rings.Count == 0 || !Vis(key)) return;
                var m = MapR(rings);
                if (hatch && !string.IsNullOrEmpty(st.E(key).Hatch))
                    P.Add(new DrawPrim { Kind = DrawPrimKind.Hatch, Element = key, Network = net, IsOther = other, Loops = MapR(rings), Diameter = dia });
                P.Add(new DrawPrim { Kind = DrawPrimKind.Ring, Element = key, Network = net, IsOther = other, Loops = m, Diameter = dia });
            }
            void Line(string key, double x0, double y0, double x1, double y1, string net = null)
            {
                if (!Vis(key)) return;
                P.Add(new DrawPrim { Kind = DrawPrimKind.Polyline, Element = key, Network = net ?? c.SystemName,
                                     Points = new List<double[]> { new[] { x0, y0 }, new[] { x1, y1 } } });
            }
            void Text(string key, string text, double x, double y, double h, TextAnchor a, double rot = 0)
            {
                if (!Vis(key) || string.IsNullOrEmpty(text)) return;
                var t = Txt(key, text, x, y, h, a, rot); t.Network = c.SystemName; P.Add(t);
            }

            string own = c.SystemName;
            bool showOthers = st.Crossing != CrossingDisplayMode.OwnOnly;

            // Clashing pipes first (drawn underneath).
            if (showOthers)
                foreach (var rg in c.Regions.Where(r => r.Role == SectionRegionRole.OtherTrench))
                {
                    string key = rg.Layer == SectionLayer.Kazi ? SectionElements.DigerKazi
                               : rg.Layer == SectionLayer.BoruGovdesi ? SectionElements.DigerBoru
                               : SectionElements.DigerDolgu;
                    Region(key, rg.Rings, true, rg.OtherSystemName, key != SectionElements.DigerKazi, rg.OtherDiameterMm);
                }
            if (st.Crossing == CrossingDisplayMode.FullWithGhost)
            {
                foreach (var rg in c.Regions.Where(r => r.Role == SectionRegionRole.GrossOutline))
                    Region(rg.OtherPipeIndex >= 0 ? SectionElements.DigerKazi : SectionElements.BrutHat,
                           rg.Rings, rg.OtherPipeIndex >= 0, rg.OtherPipeIndex >= 0 ? rg.OtherSystemName : own, false,
                           rg.OtherPipeIndex >= 0 ? rg.OtherDiameterMm : 0);
            }
            if (showOthers)
                foreach (var rg in c.Regions.Where(r => r.Role == SectionRegionRole.LostToOther))
                    Region(SectionElements.Kaybedilen, rg.Rings, false, own);

            // Own layers.
            foreach (var rg in c.Regions.Where(r => r.Role == SectionRegionRole.Owned))
            {
                string key = rg.Layer == SectionLayer.Kazi ? SectionElements.Kazi
                           : rg.Layer == SectionLayer.Yataklama ? SectionElements.Yataklama
                           : rg.Layer == SectionLayer.Gomlekleme ? SectionElements.Gomlekleme
                           : rg.Layer == SectionLayer.GeriDolgu ? SectionElements.GeriDolgu
                           : SectionElements.Boru;
                // Bands: each catalog sub-layer hatched on its own (the group keeps its outline).
                var bands = st.HatchSubLayers && rg.Layer != SectionLayer.Kazi && rg.Layer != SectionLayer.BoruGovdesi
                    ? c.SubBands.Where(b => b.Layer == rg.Layer && b.Rings.Count > 0).ToList() : null;
                bool banded = bands != null && bands.Count > 0;
                Region(key, rg.Rings, false, own, hatch: !banded);
                if (banded && Vis(key))
                    foreach (var b in bands)
                        if (!string.IsNullOrEmpty(HatchOf(st, key, b.LayerName).Pattern))
                            P.Add(new DrawPrim { Kind = DrawPrimKind.Hatch, Element = key, Network = own, Loops = MapR(b.Rings), SubLayer = b.LayerName });
            }

            // Sub-layer division lines + names.
            foreach (var dl in c.DivisionLines)
                foreach (var sg in dl.Segments) Line(SectionElements.AltKatmanCizgi, sg[0], Y(dl.Z), sg[1], Y(dl.Z));
            if (st.ShowSubLayerNames)
                foreach (var b in c.SubBands)
                {
                    double hb = (b.ZHigh - b.ZLow) * ve;
                    if (hb < th * 1.3 || string.IsNullOrEmpty(b.LayerName)) continue;
                    var segs = SubBandMid(b);
                    if (segs == null) continue;
                    Text(SectionElements.Etiket, Sanitize(b.LayerName), segs.Value, Y((b.ZLow + b.ZHigh) * 0.5), th * 0.8, TextAnchor.MiddleCenter);
                }

            // Extents (true U, stretched Y).
            double uMin = c.MinU, uMax = c.MaxU;
            double ext = Math.Max(0, st.GroundExtendM);
            double gx0 = uMin - ext, gx1 = uMax + ext;

            // Ground lines.
            Line(SectionElements.ZeminKazi, gx0, Y(c.KaziGroundZ), gx1, Y(c.KaziGroundZ));
            if (!c.DolguInvalid) Line(SectionElements.ZeminDolgu, gx0, Y(c.DolguGroundZ), gx1, Y(c.DolguGroundZ));

            // Axis + SP split lines.
            double zTop = Math.Max(c.MaxZ, c.KaziGroundZ);
            Line(SectionElements.Eksen, 0, Y(c.MinZ) - th, 0, Y(zTop) + th);
            if (c.ExcavScenario == TiePreference.Split || c.BackfillScenario == TiePreference.Split)
                foreach (var cl in c.Clashes.Where(k => !double.IsNaN(k.SplitU)))
                    Line(SectionElements.AyrimCizgisi, cl.SplitU, Y(c.TrenchBottomZ), cl.SplitU, Y(zTop));

            // Other-pipe labels.
            if (showOthers && st.ShowOtherLabel)
                foreach (var cl in c.Clashes)
                {
                    var body = c.Regions.FirstOrDefault(r => r.Role == SectionRegionRole.OtherTrench && r.Layer == SectionLayer.BoruGovdesi
                                                          && r.OtherPipeIndex == cl.OtherPipeIndex);
                    double zc = body != null ? body.Rings.SelectMany(r => r).Max(v => v[1]) : c.InvertZ;
                    Text(SectionElements.Etiket, Sanitize(cl.OtherPipeName) + (cl.OtherSystemName != own ? " [" + cl.OtherSystemName + "]" : ""),
                         cl.OtherOffsetU, Y(zc) + th * 0.4, th * 0.8, TextAnchor.BottomCenter);
                }

            // Levels.
            string L(double z) => z.ToString("F" + st.LevelDecimals, Inv);
            if (st.ShowLevels)
            {
                LevelMark(P, st, gx1, Y(c.KaziGroundZ), th, "Kazı " + L(c.KaziGroundZ), TextAnchor.BottomRight, c.SystemName);
                if (!c.DolguInvalid && Math.Abs(c.DolguGroundZ - c.KaziGroundZ) > 1e-3)
                    LevelMark(P, st, gx0, Y(c.DolguGroundZ), th, "Dolgu " + L(c.DolguGroundZ), TextAnchor.BottomLeft, c.SystemName);
                else if (!c.DolguInvalid)
                    Text(SectionElements.Etiket, "Dolgu " + L(c.DolguGroundZ), gx0, Y(c.DolguGroundZ) + th * 0.3, th, TextAnchor.BottomLeft);
                Text(SectionElements.Etiket, "Akar " + L(c.InvertZ), c.OuterDiamM * 0.5 + th * 0.5, Y(c.InvertZ), th * 0.9, TextAnchor.MiddleLeft);
                Text(SectionElements.Etiket, "Taban " + L(c.TrenchBottomZ), c.BottomWidth * 0.5 + th * 0.3, Y(c.TrenchBottomZ) - th * 0.3, th * 0.9, TextAnchor.TopLeft);
            }

            // Dimensions (true values).
            if (st.ShowDimensions && Vis(SectionElements.Olcu))
            {
                double yb = Y(c.TrenchBottomZ) - th * 2.2;
                Dim(P, st, -c.BottomWidth * 0.5, yb, c.BottomWidth * 0.5, yb, th, "B=" + c.BottomWidth.ToString("F2", Inv), false, c.SystemName);
                double yt = Y(c.KaziGroundZ) + th * 2.2;
                Dim(P, st, -c.TopWidth * 0.5, yt, c.TopWidth * 0.5, yt, th, "Üst=" + c.TopWidth.ToString("F2", Inv), false, c.SystemName);
                double xr = gx1 + th * 1.5;
                Dim(P, st, xr, Y(c.TrenchBottomZ), xr, Y(c.TrenchBottomZ + c.TrueDepth), th, "H=" + c.TrueDepth.ToString("F2", Inv), true, c.SystemName);
                if (c.SlopeX > 1e-9)
                    Text(SectionElements.Olcu, "1:" + c.SlopeX.ToString("0.##", Inv),
                         (c.BottomWidth + c.TopWidth) * 0.25 + th * 0.4, Y(c.TrenchBottomZ + c.TrueDepth * 0.5), th * 0.9, TextAnchor.MiddleLeft);
            }

            // Datum line (KK): the reference the vertical scale is read from — always at local y = 0.
            Line(SectionElements.KiyasCizgisi, gx0, 0, gx1, 0);
            Text(SectionElements.KiyasCizgisi, "KK " + L(datum), gx0, -th * 0.3, th * 0.8, TextAnchor.TopLeft);

            // Geometry bbox so far → title / notes above, table below.
            Bounds(P, out double bx0, out double by0, out double bx1, out double by1);
            bx0 = Math.Min(bx0, gx0); bx1 = Math.Max(bx1, gx1);
            double cx = (bx0 + bx1) * 0.5;

            // Title block (stacked upward from the geometry top).
            var lines = new List<(string Key, string Text, double H)>();
            double tH = st.ModelText(st.TitleHeightMm);
            if (c.CompareGroup >= 0)
                lines.Add((SectionElements.Baslik, c.ExcavScenario == c.BackfillScenario
                    ? ScenarioLabel(c.ExcavScenario)
                    : "Kazı: " + ScenarioLabel(c.ExcavScenario) + " · Dolgu: " + ScenarioLabel(c.BackfillScenario), tH));
            if (st.ShowTitle)
            {
                if (st.ShowLocalChainage)
                    lines.Add((SectionElements.Baslik, st.LocalPrefix + FormatChainage(c.LocalChainage, st.ChainageFormat), tH));
                if (st.ShowGlobalChainage && !double.IsNaN(c.GlobalChainage))
                    lines.Add((SectionElements.Baslik, st.GlobalPrefix + (st.ShowKolName && !string.IsNullOrEmpty(c.KolName) ? Sanitize(c.KolName) + " " : "")
                                                        + FormatChainage(c.GlobalChainage, st.ChainageFormat), th));
                var idParts = new List<string>();
                if (st.ShowPipeName) idParts.Add(Sanitize(c.PipeName));
                if (st.ShowNetwork && !string.IsNullOrEmpty(c.SystemName)) idParts.Add("[" + c.SystemName + "]");
                if (st.ShowDiameter && c.DiameterMm > 0) idParts.Add("Ø" + c.DiameterMm + (string.IsNullOrEmpty(c.Material) ? "" : " " + c.Material));
                if (idParts.Count > 0) lines.Add((SectionElements.Baslik, string.Join("  ", idParts), th));
            }
            if (st.ShowStationNote)
            {
                if (c.Kind == SectionStationKind.SnappedInClash)
                    lines.Add((SectionElements.Etiket, "* En yakın hesap istasyonu (istenen " + c.RequestedDist.ToString("F2", Inv) + " m)", th * 0.85));
                if (c.IsBisectorFrame)
                    lines.Add((SectionElements.Etiket, "Açıortay kesiti (kesişim açısı " + c.CrossingAngleDeg.ToString("F1", Inv) + "°)", th * 0.85));
            }
            if (st.ShowClashNote && c.Clashes.Count > 0)
            {
                foreach (var cl in c.Clashes.GroupBy(k => k.OtherPipeIndex).Select(g => g.First()))
                    lines.Add((SectionElements.Etiket, "Kesişim: " + Sanitize(cl.OtherPipeName) + " [" + cl.OtherSystemName + "] — "
                               + (cl.ThisIsUpper ? "bu boru üstte" : "bu boru altta"), th * 0.85));
                lines.Add((SectionElements.Etiket, "Kazı: " + ScenarioLabel(c.ExcavScenario) + " · Dolgu: " + ScenarioLabel(c.BackfillScenario), th * 0.85));
            }
            if (st.ShowAreas)
            {
                string A(double net, double gross)
                {
                    string f = "F" + Math.Max(0, st.AreaDecimals);
                    switch (st.AreaDisplay)
                    {
                        case AreaDisplay.Gross: return gross.ToString(f, Inv) + " m²";
                        case AreaDisplay.Both:  return net.ToString(f, Inv) + " / " + gross.ToString(f, Inv) + " m²";
                        default:                return net.ToString(f, Inv) + " m²";
                    }
                }
                lines.Add((SectionElements.Etiket, "Kazı " + A(c.AreaKaziNet, c.AreaKaziGross) + "   Geri Dolgu " + A(c.AreaGeriDolguNet, c.AreaGeriDolguGross), th * 0.85));
                lines.Add((SectionElements.Etiket, "Yataklama " + A(c.AreaYataklamaNet, c.AreaYataklamaGross) + "   Gömlekleme " + A(c.AreaGomleklemeNet, c.AreaGomleklemeGross), th * 0.85));
            }
            double ty = by1 + th * 0.8;
            for (int k = lines.Count - 1; k >= 0; k--)
            {
                Text(lines[k].Key, lines[k].Text, cx, ty, lines[k].H, TextAnchor.BottomCenter);
                ty += lines[k].H * 1.5;
            }

            // Info table below the geometry.
            if (st.ShowTable && Vis(SectionElements.Tablo))
            {
                var rows = new List<(string Label, string Value)>();
                foreach (var r in st.TableRows.Where(r => r.Visible))
                {
                    if (r.Key == SectionTableRows.AltKatmanAlanlari)
                    {
                        // Top to bottom, as read on the section.
                        foreach (var b in c.SubBands.Where(b => !string.IsNullOrEmpty(b.LayerName)).OrderByDescending(b => b.ZHigh))
                            rows.Add((Sanitize(b.LayerName), b.Area.ToString("F" + Math.Max(0, st.AreaDecimals), Inv) + " m²"));
                        continue;
                    }
                    var v = TableValue(c, r.Key, st);
                    if (v != null) rows.Add((SectionTableRows.Label(r.Key), v));
                }
                if (rows.Count > 0)
                {
                    double wl = st.ModelText(st.TableLabelWidthMm), wv = st.ModelText(st.TableValueWidthMm), rh = st.ModelText(st.TableRowHeightMm);
                    double x0 = cx - (wl + wv) * 0.5, y0 = by0 - th;
                    double tw = wl + wv;
                    for (int k = 0; k <= rows.Count; k++) Line(SectionElements.Tablo, x0, y0 - k * rh, x0 + tw, y0 - k * rh);
                    Line(SectionElements.Tablo, x0, y0, x0, y0 - rows.Count * rh);
                    Line(SectionElements.Tablo, x0 + wl, y0, x0 + wl, y0 - rows.Count * rh);
                    Line(SectionElements.Tablo, x0 + tw, y0, x0 + tw, y0 - rows.Count * rh);
                    for (int k = 0; k < rows.Count; k++)
                    {
                        double yc = y0 - (k + 0.5) * rh;
                        Text(SectionElements.Tablo, rows[k].Label, x0 + th * 0.4, yc, th * 0.85, TextAnchor.MiddleLeft);
                        Text(SectionElements.Tablo, rows[k].Value, x0 + tw - th * 0.4, yc, th * 0.85, TextAnchor.MiddleRight);
                    }
                }
            }

            // Grid.
            if (st.ShowGrid && Vis(SectionElements.Izgara) && st.GridStepM > 1e-6)
            {
                double step = st.GridStepM;
                for (double z = datum; z <= zTop + 1e-9; z += step)
                {
                    Line(SectionElements.Izgara, gx0, Y(z), gx1, Y(z));
                    Text(SectionElements.Izgara, L(z), gx0 - th * 0.3, Y(z), th * 0.7, TextAnchor.MiddleRight);
                }
                for (double u = Math.Ceiling(gx0 / step) * step; u <= gx1 + 1e-9; u += step)
                    Line(SectionElements.Izgara, u, Y(datum), u, Y(zTop));
            }

            // Frame.
            Bounds(P, out double fx0, out double fy0, out double fx1, out double fy1);
            double pad = th * 0.8;
            fx0 -= pad; fy0 -= pad; fx1 += pad; fy1 += pad;
            if (st.ShowFrame && Vis(SectionElements.Cerceve))
                P.Add(new DrawPrim
                {
                    Kind = DrawPrimKind.Ring, Element = SectionElements.Cerceve, Network = c.SystemName,
                    Loops = new List<List<double[]>> { new List<double[]> { new[] { fx0, fy0 }, new[] { fx1, fy0 }, new[] { fx1, fy1 }, new[] { fx0, fy1 } } }
                });
            d.MinX = fx0; d.MinY = fy0; d.MaxX = fx1; d.MaxY = fy1;
            foreach (var p in P) if (!p.IsOther && p.Diameter == 0) p.Diameter = c.DiameterMm;
            return d;
        }

        /// <summary>Hatch of a primitive: the sub-layer's own style when it has one, else the element's.</summary>
        public static HatchParams HatchOf(SectionStyle st, DrawPrim p) => HatchOf(st, p.Element, p.SubLayer);

        public static HatchParams HatchOf(SectionStyle st, string element, string subLayer)
        {
            var es = st.E(element);
            if (!string.IsNullOrEmpty(subLayer) && st.SubLayers.TryGetValue(subLayer, out var sl) && !string.IsNullOrEmpty(sl.Hatch))
                return new HatchParams
                {
                    Pattern = sl.Hatch, Scale = sl.HatchScale, Angle = sl.HatchAngle,
                    ColorAci = sl.HatchColorAci != 256 ? sl.HatchColorAci : es.HatchColorAci, Transparency = sl.HatchTransparency
                };
            return new HatchParams
            {
                Pattern = es.Hatch, Scale = es.HatchScale, Angle = es.HatchAngle, ColorAci = es.HatchColorAci, Transparency = es.HatchTransparency
            };
        }

        private static double? SubBandMid(SectionSubBand b)
        {
            double z = (b.ZLow + b.ZHigh) * 0.5;
            var segs = BoQSimplifiedTestV2Command.ScanLine(b.Rings, z);
            if (segs.Count == 0) return null;
            var best = segs.OrderByDescending(s => s[1] - s[0]).First();
            return (best[0] + best[1]) * 0.5;
        }

        private static string TableValue(SectionCut c, string key, SectionStyle st)
        {
            string L(double z) => z.ToString("F" + st.LevelDecimals, Inv);
            string M(double v) => v.ToString("F2", Inv) + " m";
            string A(double v) => v.ToString("F" + Math.Max(0, st.AreaDecimals), Inv) + " m²";
            switch (key)
            {
                case SectionTableRows.YerelKm:        return FormatChainage(c.LocalChainage, st.ChainageFormat);
                case SectionTableRows.GenelKm:        return double.IsNaN(c.GlobalChainage) ? null : FormatChainage(c.GlobalChainage, st.ChainageFormat);
                case SectionTableRows.KaziKotu:       return L(c.KaziGroundZ);
                case SectionTableRows.DolguKotu:      return c.DolguInvalid ? "geçersiz" : L(c.DolguGroundZ);
                case SectionTableRows.AkarKotu:       return L(c.InvertZ);
                case SectionTableRows.KaziTabani:     return L(c.TrenchBottomZ);
                case SectionTableRows.Derinlik:       return M(c.TrueDepth);
                case SectionTableRows.TabanGenislik:  return M(c.BottomWidth);
                case SectionTableRows.UstGenislik:    return M(c.TopWidth);
                case SectionTableRows.Sev:            return c.SlopeX > 1e-9 ? "1:" + c.SlopeX.ToString("0.##", Inv) : "düşey";
                case SectionTableRows.KaziAlani:      return A(st.AreaDisplay == AreaDisplay.Gross ? c.AreaKaziGross : c.AreaKaziNet);
                case SectionTableRows.YataklamaAlani: return A(st.AreaDisplay == AreaDisplay.Gross ? c.AreaYataklamaGross : c.AreaYataklamaNet);
                case SectionTableRows.GomleklemeAlani:return A(st.AreaDisplay == AreaDisplay.Gross ? c.AreaGomleklemeGross : c.AreaGomleklemeNet);
                case SectionTableRows.GeriDolguAlani: return A(st.AreaDisplay == AreaDisplay.Gross ? c.AreaGeriDolguGross : c.AreaGeriDolguNet);
                default:                              return null;
            }
        }

        // ── Small drawing helpers ────────────────────────────────────────────

        private static DrawPrim Txt(string key, string text, double x, double y, double h, TextAnchor a, double rot = 0)
            => new DrawPrim { Kind = DrawPrimKind.Text, Element = key, Text = text, X = x, Y = y, Height = h, Anchor = a, Rotation = rot };

        private static void LevelMark(List<DrawPrim> P, SectionStyle st, double x, double y, double th, string text, TextAnchor a, string net)
        {
            if (!st.E(SectionElements.Etiket).Visible) return;
            double s = th * 0.6;
            double mx = a == TextAnchor.BottomRight ? x - s : x + s;
            P.Add(new DrawPrim { Kind = DrawPrimKind.Ring, Element = SectionElements.Etiket, Network = net,
                Loops = new List<List<double[]>> { new List<double[]> { new[] { mx - s * 0.6, y + s }, new[] { mx + s * 0.6, y + s }, new[] { mx, y } } } });
            var t = Txt(SectionElements.Etiket, text, a == TextAnchor.BottomRight ? mx - s : mx + s, y + s * 1.2, th, a);
            t.Network = net; P.Add(t);
        }

        private static void Dim(List<DrawPrim> P, SectionStyle st, double x0, double y0, double x1, double y1, double th,
                                string text, bool vertical, string net)
        {
            double tick = th * 0.5;
            void Seg(double a, double b, double c, double d)
                => P.Add(new DrawPrim { Kind = DrawPrimKind.Polyline, Element = SectionElements.Olcu, Network = net,
                                        Points = new List<double[]> { new[] { a, b }, new[] { c, d } } });
            Seg(x0, y0, x1, y1);
            // 45° architectural ticks.
            Seg(x0 - tick * 0.5, y0 - tick * 0.5, x0 + tick * 0.5, y0 + tick * 0.5);
            Seg(x1 - tick * 0.5, y1 - tick * 0.5, x1 + tick * 0.5, y1 + tick * 0.5);
            var t = vertical
                ? Txt(SectionElements.Olcu, text, x0 - th * 0.4, (y0 + y1) * 0.5, th * 0.9, TextAnchor.BottomCenter, Math.PI / 2)
                : Txt(SectionElements.Olcu, text, (x0 + x1) * 0.5, y0 + th * 0.3, th * 0.9, TextAnchor.BottomCenter);
            t.Network = net; P.Add(t);
        }

        /// <summary>Unicode arrows render as '?' in SHX fonts.</summary>
        public static string Sanitize(string s)
            => (s ?? "").Replace("→", "->").Replace("←", "<-").Replace("↔", "<->");

        /// <summary>Approximate bounds (texts estimated at 0.65·h per character).</summary>
        public static void Bounds(IEnumerable<DrawPrim> prims, out double x0, out double y0, out double x1, out double y1)
        {
            double ax0 = double.MaxValue, ay0 = double.MaxValue, ax1 = double.MinValue, ay1 = double.MinValue;
            void Acc(double x, double y) { if (x < ax0) ax0 = x; if (x > ax1) ax1 = x; if (y < ay0) ay0 = y; if (y > ay1) ay1 = y; }
            foreach (var p in prims)
            {
                if (p.Loops != null) foreach (var l in p.Loops) foreach (var v in l) Acc(v[0], v[1]);
                if (p.Points != null) foreach (var v in p.Points) Acc(v[0], v[1]);
                if (p.Kind == DrawPrimKind.Text && !string.IsNullOrEmpty(p.Text))
                {
                    double w = 0.65 * p.Height * p.Text.Length, h = p.Height;
                    if (Math.Abs(p.Rotation) > 1e-6) { double t = w; w = h; h = t; }
                    double ax = p.Anchor == TextAnchor.BottomLeft || p.Anchor == TextAnchor.MiddleLeft || p.Anchor == TextAnchor.TopLeft ? 0
                              : p.Anchor == TextAnchor.BottomRight || p.Anchor == TextAnchor.MiddleRight || p.Anchor == TextAnchor.TopRight ? -w : -w * 0.5;
                    double ay = p.Anchor == TextAnchor.BottomLeft || p.Anchor == TextAnchor.BottomCenter || p.Anchor == TextAnchor.BottomRight ? 0
                              : p.Anchor == TextAnchor.TopLeft || p.Anchor == TextAnchor.TopCenter || p.Anchor == TextAnchor.TopRight ? -h : -h * 0.5;
                    if (Math.Abs(p.Rotation) > 1e-6) { ax = -w; ay = -h * 0.5; }
                    Acc(p.X + ax, p.Y + ay); Acc(p.X + ax + w, p.Y + ay + h);
                }
            }
            if (ax0 > ax1) { ax0 = ay0 = 0; ax1 = ay1 = 1; }
            x0 = ax0; y0 = ay0; x1 = ax1; y1 = ay1;
        }
    }
}
