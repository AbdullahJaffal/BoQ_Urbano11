using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

using Exception = System.Exception;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// Writes a laid-out <see cref="SectionSheet"/> into model space with the
    /// <see cref="SectionStyle"/>'s layers / colours / linetypes / hatches. Every
    /// entity (or block reference) carries URBANO_KS XData (run id, pipe, chainage) so a
    /// later run can replace it and UT_CROSS_SECTIONS_CLEAR can remove it — nothing the
    /// user drew is ever touched.
    /// </summary>
    public static class SectionRenderer
    {
        public const string RegApp = "URBANO_KS";

        public sealed class Result
        {
            public int Entities;
            public int Sections;
            public int HatchFailures;
            public string FirstHatchError;
        }

        public static Result Render(Database db, SectionSheet sheet, SectionStyle style, Point3d origin, string runId)
        {
            var res = new Result();
            using (var tr = db.TransactionManager.StartTransaction())
            {
                EnsureRegApp(db, tr);
                var ctx = new Ctx { Db = db, Tr = tr, Style = style };
                ctx.TextStyleId = EnsureTextStyle(db, tr, style.TextStyle);
                ctx.ShxFont = IsShx(tr, ctx.TextStyleId);
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForWrite);

                foreach (var sec in sheet.Sections)
                {
                    string pipeKey = SectionRunOptions.PipeKey(sec.Cut.SystemName, sec.Cut.StartNodeName, sec.Cut.EndNodeName);
                    var xdata = new ResultBuffer(
                        new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, runId),
                        new TypedValue((int)DxfCode.ExtendedDataAsciiString, pipeKey),
                        new TypedValue((int)DxfCode.ExtendedDataReal, sec.Cut.LocalChainage));

                    if (style.Grouping == SectionGrouping.Block)
                    {
                        var bt = (BlockTable)tr.GetObject(db.BlockTableId, OpenMode.ForWrite);
                        var btr = new BlockTableRecord { Name = UniqueBlockName(bt, tr, "URB_KS_" + runId + "_" + sec.Index), Origin = Point3d.Origin };
                        bt.Add(btr); tr.AddNewlyCreatedDBObject(btr, true);
                        var baseOff = new Vector3d(sec.MinX, sec.MinY, 0);
                        foreach (var p in sec.Prims) res.Entities += Emit(ctx, btr, p, new Vector3d(-sec.MinX, -sec.MinY, 0), null);
                        var br = new BlockReference(origin + baseOff, btr.ObjectId) { Layer = "0" };
                        ms.AppendEntity(br); tr.AddNewlyCreatedDBObject(br, true);
                        br.XData = xdata;
                    }
                    else
                    {
                        var ids = new ObjectIdCollection();
                        var off = origin.GetAsVector();
                        foreach (var p in sec.Prims) res.Entities += Emit(ctx, ms, p, off, xdata, ids);
                        if (style.Grouping == SectionGrouping.Group && ids.Count > 0)
                        {
                            var gd = (DBDictionary)tr.GetObject(db.GroupDictionaryId, OpenMode.ForWrite);
                            var g = new Group("UT_CROSS_SECTIONS " + sec.Cut.PipeName + " " + sec.Cut.LocalChainage.ToString("F2"), true);
                            gd.SetAt("*", g); tr.AddNewlyCreatedDBObject(g, true);
                            g.Append(ids);
                        }
                    }
                    res.Sections++;
                }

                // Row titles (outside any section).
                var offAll = origin.GetAsVector();
                var rowX = new ResultBuffer(
                    new TypedValue((int)DxfCode.ExtendedDataRegAppName, RegApp),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, runId),
                    new TypedValue((int)DxfCode.ExtendedDataAsciiString, ""),
                    new TypedValue((int)DxfCode.ExtendedDataReal, 0.0));
                foreach (var p in sheet.SheetPrims) res.Entities += Emit(ctx, ms, p, offAll, rowX);

                res.HatchFailures = ctx.HatchFailures;
                res.FirstHatchError = ctx.FirstHatchError;
                tr.Commit();
            }
            return res;
        }

        /// <summary>Erases every entity / block reference tagged by UT_CROSS_SECTIONS (all runs, or one run).</summary>
        public static int Clear(Database db, string runId = null)
        {
            int n = 0;
            using (var tr = db.TransactionManager.StartTransaction())
            {
                var ms = (BlockTableRecord)tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(db), OpenMode.ForRead);
                var blockDefs = new HashSet<ObjectId>();
                foreach (ObjectId id in ms)
                {
                    var ent = tr.GetObject(id, OpenMode.ForRead) as Entity;
                    var xd = ent?.GetXDataForApplication(RegApp);
                    if (xd == null) continue;
                    var arr = xd.AsArray();
                    string rid = arr.Length > 1 ? arr[1].Value as string : null;
                    if (runId != null && rid != runId) continue;
                    if (ent is BlockReference br) blockDefs.Add(br.BlockTableRecord);
                    ent.UpgradeOpen(); ent.Erase(); n++;
                }
                foreach (var bid in blockDefs)
                {
                    var btr = (BlockTableRecord)tr.GetObject(bid, OpenMode.ForWrite);
                    if (btr.GetBlockReferenceIds(true, false).Count == 0) btr.Erase();
                }
                tr.Commit();
            }
            return n;
        }

        // ── Entity emission ──────────────────────────────────────────────────

        private sealed class Ctx
        {
            public Database Db; public Transaction Tr; public SectionStyle Style;
            public ObjectId TextStyleId; public bool ShxFont; public int HatchFailures; public string FirstHatchError;
            public readonly Dictionary<string, ObjectId> Layers = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
            public readonly Dictionary<string, ObjectId> Linetypes = new Dictionary<string, ObjectId>(StringComparer.OrdinalIgnoreCase);
        }

        private static int Emit(Ctx c, BlockTableRecord owner, DrawPrim p, Vector3d off, ResultBuffer xdata, ObjectIdCollection ids = null)
        {
            var es = c.Style.E(p.Element);
            if (!es.Visible) return 0;
            var made = new List<Entity>();
            switch (p.Kind)
            {
                case DrawPrimKind.Ring:
                    foreach (var loop in p.Loops)
                    {
                        var pl = Poly(loop, off, true);
                        if (pl != null) made.Add(pl);
                    }
                    break;
                case DrawPrimKind.Polyline:
                    {
                        var pl = Poly(p.Points, off, false);
                        if (pl != null) made.Add(pl);
                    }
                    break;
                case DrawPrimKind.Hatch:
                    {
                        var h = MakeHatch(c, owner, p, es, off);
                        if (h != null) { Tag(h, c, es, p, xdata, ids, isHatch: true); return 1; }
                        return 0;
                    }
                case DrawPrimKind.Text:
                    {
                        var t = MakeText(c, p, off);
                        if (t == null) return 0;
                        owner.AppendEntity(t); c.Tr.AddNewlyCreatedDBObject(t, true);
                        Tag(t, c, es, p, xdata, ids, isHatch: false);
                        if (p.Anchor != TextAnchor.BottomLeft)
                        {
                            var prev = HostApplicationServices.WorkingDatabase;
                            try { HostApplicationServices.WorkingDatabase = c.Db; t.AdjustAlignment(c.Db); }
                            finally { HostApplicationServices.WorkingDatabase = prev; }
                        }
                        return 1;
                    }
            }
            foreach (var e in made)
            {
                owner.AppendEntity(e); c.Tr.AddNewlyCreatedDBObject(e, true);
                Tag(e, c, es, p, xdata, ids, isHatch: false);
            }
            return made.Count;
        }

        private static void Tag(Entity e, Ctx c, SectionElementStyle es, DrawPrim p, ResultBuffer xdata, ObjectIdCollection ids, bool isHatch)
        {
            e.LayerId = EnsureLayer(c, es, p.Network, p.Diameter);
            // Colour: per-network override for clashing pipes, else the element's own setting.
            short aci = es.ColorAci;
            if (isHatch)
            {
                short hc = SectionLayoutEngine.HatchOf(c.Style, p).ColorAci;
                if (hc != 256) aci = hc;
            }
            if (p.IsOther && c.Style.OtherColorMode == OtherNetworkColorMode.PerNetwork
                && p.Network != null && c.Style.NetworkColors.TryGetValue(p.Network, out var nc)) aci = nc;
            if (es.TrueColor >= 0 && !(p.IsOther && c.Style.OtherColorMode == OtherNetworkColorMode.PerNetwork))
                e.Color = Color.FromRgb((byte)((es.TrueColor >> 16) & 0xFF), (byte)((es.TrueColor >> 8) & 0xFF), (byte)(es.TrueColor & 0xFF));
            else if (aci != 256)
                e.Color = Color.FromColorIndex(ColorMethod.ByAci, aci);
            if (!isHatch)
            {
                if (!string.IsNullOrEmpty(es.Linetype) && !es.Linetype.Equals("ByLayer", StringComparison.OrdinalIgnoreCase))
                {
                    var lt = EnsureLinetype(c, es.Linetype);
                    if (!lt.IsNull) e.LinetypeId = lt;
                }
                e.LinetypeScale = c.Style.LinetypeScale > 0 ? c.Style.LinetypeScale : 1.0;
                if (es.LineWeight >= 0) e.LineWeight = ToLineWeight(es.LineWeight);
            }
            if (xdata != null) e.XData = xdata;
            ids?.Add(e.ObjectId);
        }

        private static Polyline Poly(List<double[]> pts, Vector3d off, bool closed)
        {
            if (pts == null || pts.Count < (closed ? 3 : 2)) return null;
            var pl = new Polyline();
            for (int i = 0; i < pts.Count; i++) pl.AddVertexAt(i, new Point2d(pts[i][0] + off.X, pts[i][1] + off.Y), 0, 0, 0);
            pl.Closed = closed;
            return pl;
        }

        // Boundary loops come from temporary closed polylines (by ObjectId) — the most robust
        // AppendLoop form; the hatch is non-associative and the temporaries are erased after.
        private static Hatch MakeHatch(Ctx c, BlockTableRecord owner, DrawPrim p, SectionElementStyle es, Vector3d off)
        {
            Hatch h = null;
            var temps = new List<Polyline>();
            var hp = SectionLayoutEngine.HatchOf(c.Style, p);
            string pattern = string.IsNullOrEmpty(hp.Pattern) ? "SOLID" : hp.Pattern.Trim().ToUpperInvariant();
            try
            {
                foreach (var ring in p.Loops)
                {
                    var pl = Poly(ring, off, true);
                    if (pl == null || Math.Abs(pl.Area) < 1e-8) { pl?.Dispose(); continue; }
                    owner.AppendEntity(pl); c.Tr.AddNewlyCreatedDBObject(pl, true);
                    temps.Add(pl);
                }
                if (temps.Count == 0) return null;

                h = new Hatch();
                h.SetDatabaseDefaults(c.Db);
                owner.AppendEntity(h); c.Tr.AddNewlyCreatedDBObject(h, true);
                h.Associative = false;
                h.PatternScale = hp.Scale > 0 ? hp.Scale : 1.0;
                h.PatternAngle = hp.Angle * Math.PI / 180.0;
                h.SetHatchPattern(HatchPatternType.PreDefined, pattern);
                h.HatchStyle = HatchStyle.Normal;
                // Largest ring first (the outer boundary); holes are found by the Normal island style.
                foreach (var pl in temps.OrderByDescending(t => Math.Abs(t.Area)))
                    h.AppendLoop(HatchLoopTypes.Default, new ObjectIdCollection { pl.ObjectId });
                h.EvaluateHatch(true);
                int tr = Math.Max(0, Math.Min(90, hp.Transparency));
                if (tr > 0) h.Transparency = new Transparency((byte)Math.Round(255 * (100 - tr) / 100.0));
                return h;
            }
            catch (Exception ex)
            {
                c.HatchFailures++;
                if (c.FirstHatchError == null) c.FirstHatchError = $"{pattern} ({p.Element}{(p.SubLayer != null ? " / " + p.SubLayer : "")}): {ex.Message}";
                try { if (h != null && !h.IsErased) h.Erase(); } catch { }
                return null;
            }
            finally
            {
                foreach (var pl in temps) { try { if (!pl.IsErased) pl.Erase(); } catch { } }
            }
        }

        private static DBText MakeText(Ctx c, DrawPrim p, Vector3d off)
        {
            string s = p.Text ?? "";
            if (c.ShxFont) s = s.Replace("²", "2").Replace("Ø", "%%c").Replace("°", "%%d");
            if (s.Length == 0) return null;
            var pos = new Point3d(p.X + off.X, p.Y + off.Y, 0);
            var t = new DBText
            {
                TextString = s, Height = Math.Max(1e-4, p.Height), Rotation = p.Rotation,
                TextStyleId = c.TextStyleId, Position = pos
            };
            switch (p.Anchor)
            {
                case TextAnchor.BottomLeft:   break;
                case TextAnchor.BottomCenter: t.HorizontalMode = TextHorizontalMode.TextCenter; t.VerticalMode = TextVerticalMode.TextBase;   break;
                case TextAnchor.BottomRight:  t.HorizontalMode = TextHorizontalMode.TextRight;  t.VerticalMode = TextVerticalMode.TextBase;   break;
                case TextAnchor.MiddleLeft:   t.HorizontalMode = TextHorizontalMode.TextLeft;   t.VerticalMode = TextVerticalMode.TextVerticalMid; break;
                case TextAnchor.MiddleCenter: t.HorizontalMode = TextHorizontalMode.TextCenter; t.VerticalMode = TextVerticalMode.TextVerticalMid; break;
                case TextAnchor.MiddleRight:  t.HorizontalMode = TextHorizontalMode.TextRight;  t.VerticalMode = TextVerticalMode.TextVerticalMid; break;
                case TextAnchor.TopLeft:      t.HorizontalMode = TextHorizontalMode.TextLeft;   t.VerticalMode = TextVerticalMode.TextTop;    break;
                case TextAnchor.TopCenter:    t.HorizontalMode = TextHorizontalMode.TextCenter; t.VerticalMode = TextVerticalMode.TextTop;    break;
                case TextAnchor.TopRight:     t.HorizontalMode = TextHorizontalMode.TextRight;  t.VerticalMode = TextVerticalMode.TextTop;    break;
            }
            if (p.Anchor != TextAnchor.BottomLeft) t.AlignmentPoint = pos;
            return t;
        }

        // ── Symbol tables ────────────────────────────────────────────────────

        private static ObjectId EnsureLayer(Ctx c, SectionElementStyle es, string network, int diameterMm)
        {
            string name = LayerName(es, network, diameterMm);
            if (c.Layers.TryGetValue(name, out var id)) return id;
            var lt = (LayerTable)c.Tr.GetObject(c.Db.LayerTableId, OpenMode.ForRead);
            if (lt.Has(name)) id = lt[name];
            else
            {
                lt.UpgradeOpen();
                var ltr = new LayerTableRecord
                {
                    Name  = name,
                    Color = es.TrueColor >= 0
                        ? Color.FromRgb((byte)((es.TrueColor >> 16) & 0xFF), (byte)((es.TrueColor >> 8) & 0xFF), (byte)(es.TrueColor & 0xFF))
                        : Color.FromColorIndex(ColorMethod.ByAci, es.LayerColorAci is short a && a >= 1 && a <= 255 ? a : (short)7)
                };
                if (es.LineWeight >= 0) ltr.LineWeight = ToLineWeight(es.LineWeight);
                id = lt.Add(ltr);
                c.Tr.AddNewlyCreatedDBObject(ltr, true);
            }
            c.Layers[name] = id;
            return id;
        }

        /// <summary>Layer name from the element's template — tokens {AG} network, {KATMAN} element key, {CAP} diameter (mm).</summary>
        public static string LayerName(SectionElementStyle es, string network, int diameterMm = 0)
        {
            string tmpl = string.IsNullOrWhiteSpace(es.Layer) ? "URB_KS_{KATMAN}" : es.Layer;
            string n = tmpl.Replace("{AG}", network ?? "").Replace("{KATMAN}", es.Key ?? "")
                           .Replace("{CAP}", diameterMm > 0 ? diameterMm.ToString(System.Globalization.CultureInfo.InvariantCulture) : "");
            foreach (char ch in "<>/\\\":;?*|,=`") n = n.Replace(ch, '_');
            n = n.Trim();
            return n.Length == 0 ? "URB_KS" : n;
        }

        private static ObjectId EnsureLinetype(Ctx c, string name)
        {
            if (c.Linetypes.TryGetValue(name, out var id)) return id;
            var lt = (LinetypeTable)c.Tr.GetObject(c.Db.LinetypeTableId, OpenMode.ForRead);
            if (!lt.Has(name))
            {
                foreach (var file in new[] { "acadiso.lin", "acad.lin" })
                {
                    try { c.Db.LoadLineTypeFile(name, file); } catch { }
                    lt = (LinetypeTable)c.Tr.GetObject(c.Db.LinetypeTableId, OpenMode.ForRead);
                    if (lt.Has(name)) break;
                }
            }
            id = lt.Has(name) ? lt[name] : ObjectId.Null;
            c.Linetypes[name] = id;
            return id;
        }

        private static ObjectId EnsureTextStyle(Database db, Transaction tr, string name)
        {
            var ts = (TextStyleTable)tr.GetObject(db.TextStyleTableId, OpenMode.ForRead);
            if (!string.IsNullOrEmpty(name) && ts.Has(name)) return ts[name];
            if (string.Equals(name, "URB_KS", StringComparison.OrdinalIgnoreCase))
            {
                ts.UpgradeOpen();
                var rec = new TextStyleTableRecord { Name = "URB_KS", FileName = "arial.ttf" };
                var id = ts.Add(rec);
                tr.AddNewlyCreatedDBObject(rec, true);
                return id;
            }
            return db.Textstyle;
        }

        private static bool IsShx(Transaction tr, ObjectId styleId)
        {
            try
            {
                var rec = (TextStyleTableRecord)tr.GetObject(styleId, OpenMode.ForRead);
                return (rec.FileName ?? "").EndsWith(".shx", StringComparison.OrdinalIgnoreCase)
                    || (string.IsNullOrEmpty(rec.FileName) && string.IsNullOrEmpty(rec.Font.TypeFace));
            }
            catch { return false; }
        }

        private static void EnsureRegApp(Database db, Transaction tr)
        {
            var rat = (RegAppTable)tr.GetObject(db.RegAppTableId, OpenMode.ForRead);
            if (rat.Has(RegApp)) return;
            rat.UpgradeOpen();
            var r = new RegAppTableRecord { Name = RegApp };
            rat.Add(r); tr.AddNewlyCreatedDBObject(r, true);
        }

        private static string UniqueBlockName(BlockTable bt, Transaction tr, string baseName)
        {
            string n = baseName; int k = 1;
            while (bt.Has(n)) n = baseName + "_" + (k++);
            return n;
        }

        private static LineWeight ToLineWeight(int hundredthsMm)
        {
            int[] valid = { 0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211 };
            int best = valid.OrderBy(v => Math.Abs(v - hundredthsMm)).First();
            return (LineWeight)best;
        }
    }
}
