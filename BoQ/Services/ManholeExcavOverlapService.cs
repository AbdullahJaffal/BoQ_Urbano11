using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Services
{
    /// <summary>
    /// Step 2 (diagnostic only): calculates the raw overlap volume between each
    /// manhole's isolated excavation frustum and every connected pipe trench,
    /// using horizontal Z-slicing + 2-D Clipper intersection + Simpson's 1/3 rule.
    ///
    /// The manhole square footprint is rotated to the bisector angle between the
    /// lowest inlet pipe and the lowest outlet pipe directions (or to the single
    /// available pipe's direction when only one type exists).
    ///
    /// The overlap volumes are NOT yet deducted from the BoQ totals — results are
    /// returned as human-readable strings for printing to the AutoCAD command line.
    /// </summary>
    public static class ManholeExcavOverlapService
    {
        // Excavation pit geometry (base side, H:V slope) is no longer hardcoded
        // here — Phase 7 resolves it per-manhole from ManholeExcavationCatalog in
        // ManholeAIService.ProcessManhole and stores it on ManholeItem
        // (ExcavBaseSideM/ExcavSlopeRatio), so both this diagnostic and the real
        // BoQ volume always build the exact same pit, never two copies that can
        // drift apart. A manhole with no matching catalog rule has both at 0,
        // which ManholeSquareAt already handles gracefully (returns null → zero
        // overlap contribution, no crash).

        // =====================================================================
        // Public API
        // =====================================================================

        /// <summary>
        /// For every manhole in the report, computes the total volume of overlap
        /// between the manhole excavation frustum and all connected pipe trenches.
        ///
        /// Returns one diagnostic line per manhole (ready for WriteMessage).
        /// </summary>
        public static List<string> Compute(BoQReport report)
        {
            var lines = new List<string>();
            if (report?.Systems == null || report.SectionDebug == null) return lines;

            // Reset before accumulating (safe to call multiple times).
            foreach (var s in report.SectionDebug)
            {
                s.ManholeExcavDeducted    = 0;
                s.ManholeBeddingDeducted  = 0;
                s.ManholeSurroundDeducted = 0;
                s.ManholeBackfillDeducted = 0;
            }

            // Index sections by node name for fast lookup.
            // "outlet" = pipe that STARTS at this manhole (water flows out).
            // "inlet"  = pipe that ENDS   at this manhole (water flows in).
            var outlets = report.SectionDebug          // StartNodeName == manhole
                .Where(s => s.StartNodeName != null)
                .GroupBy(s => s.StartNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var inlets = report.SectionDebug           // EndNodeName == manhole
                .Where(s => s.EndNodeName != null)
                .GroupBy(s => s.EndNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            foreach (var sys in report.Systems)
            {
                foreach (var mh in sys.Manholes)
                {
                    if (mh.ExcavationDepth <= 1e-6) continue;

                    double zTop    = mh.ZKazi;
                    double zBottom = zTop - mh.ExcavationDepth;  // absolute lowest invert

                    // ── Compute the square's rotation angle ───────────────────
                    double rotAngle = ComputeRotationAngle(mh, outlets, inlets);

                    // ── Collect connected pipes: (sdr, invertAtMh, dirX, dirY) ─
                    // dirX/dirY = unit vector FROM manhole outward along the pipe.
                    var connectedPipes = new List<(SectionDebugRow sdr,
                                                   double invertAtMh,
                                                   double dirX, double dirY)>();

                    if (outlets.TryGetValue(mh.NodeName, out var outList))
                    {
                        foreach (var sdr in outList)
                        {
                            double dx = sdr.EndX - sdr.StartX;
                            double dy = sdr.EndY - sdr.StartY;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < 1e-6) continue;
                            connectedPipes.Add((sdr, sdr.InvertStart, dx / len, dy / len));
                        }
                    }

                    if (inlets.TryGetValue(mh.NodeName, out var inList))
                    {
                        foreach (var sdr in inList)
                        {
                            // Direction away from manhole = back along the pipe toward its start
                            double dx = sdr.StartX - sdr.EndX;
                            double dy = sdr.StartY - sdr.EndY;
                            double len = Math.Sqrt(dx * dx + dy * dy);
                            if (len < 1e-6) continue;
                            connectedPipes.Add((sdr, sdr.InvertEnd, dx / len, dy / len));
                        }
                    }

                    // Simpson's rule uses 3 Z-slices tied to the manhole span:
                    // Z_bottom (base) → Z_mid → Z_top (terrain).
                    double zManhMid = (zBottom + zTop) * 0.5;
                    double Hmh      = zTop - zBottom;
                    if (Hmh <= 1e-6) continue;

                    // At each slice: manhole square polygon
                    var mhBot = ManholeSquareAt(mh.X, mh.Y, zBottom, zBottom, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio);
                    var mhMid = ManholeSquareAt(mh.X, mh.Y, zBottom, zManhMid, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio);
                    var mhTop = ManholeSquareAt(mh.X, mh.Y, zBottom, zTop,     rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio);

                    // Per-pipe intersection polygons at each slice (union across all pipes)
                    var unionBot = new List<List<double[]>>();
                    var unionMid = new List<List<double[]>>();
                    var unionTop = new List<List<double[]>>();

                    // Per-pipe individual intersection volumes (Value 1 & 2 per pipe) —
                    // TEMPORARY diagnostic fields (user request 2026-07-05) carry the
                    // bedding/surround/backfill overlap alongside the excavation overlap
                    // so Compute()'s printed lines show the full per-layer breakdown.
                    var perPipeVolumes = new List<(string label, double excavVol, double bedVol, double surrVol, double bfVol)>();

                    // Accumulate bedding + surround overlaps for this manhole's BackfillVolume.
                    double mhBeddingSum  = 0;
                    double mhSurroundSum = 0;

                    // ── Dolgu-basis Z-range (independent of the Kazı range above) ──
                    // Backfill's own top/bottom, from mh.ZDolgu/mh.DolguFinalDepth —
                    // NOT derived from zTop/zBottom (user directive 2026-07-06: redo
                    // the same extraction with the new level, don't slice the Kazı result).
                    bool   dolguGeomValid   = !mh.DolguInvalid && mh.DolguFinalDepth > 1e-6;
                    double zTopDolgu    = mh.ZDolgu;
                    double zBottomDolgu = zTopDolgu - mh.DolguFinalDepth;
                    double mhBeddingSumDolgu  = 0;
                    double mhSurroundSumDolgu = 0;

                    foreach (var (sdr, invertAtMh, dirX, dirY) in connectedPipes)
                    {
                        // Trench polygon at each manhole slice elevation
                        var trBot = TrenchRectAt(mh.X, mh.Y, zBottom,  invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                        var trMid = TrenchRectAt(mh.X, mh.Y, zManhMid, invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                        var trTop = TrenchRectAt(mh.X, mh.Y, zTop,     invertAtMh,
                                                 sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);

                        // Individual intersection with manhole at each slice
                        double aBot = AreaOfIntersect(mhBot, trBot);
                        double aMid = AreaOfIntersect(mhMid, trMid);
                        double aTop = AreaOfIntersect(mhTop, trTop);

                        double pipeVol = (Hmh / 6.0) * (aBot + 4.0 * aMid + aTop);
                        string lbl = $"{sdr.StartNodeName}→{sdr.EndNodeName}";

                        // Accumulate into the section row for BoQ deduction.
                        sdr.ManholeExcavDeducted += pipeVol;

                        // Per-pipe bedding/surround/backfill overlap — 0 unless the
                        // corresponding Z-range below actually overlaps this manhole's
                        // excavation span (populated further down, diagnostic only).
                        double bedVolDbg = 0, surrVolDbg = 0, bfVolDbg = 0;

                        // ── Bedding overlap ───────────────────────────────────
                        // Bedding occupies Z: [invertAtMh − TrBedHeight, invertAtMh].
                        // Fixed width TrWidth (no slope) → reuse TrenchRectAt with slopeRatio=0.
                        double bedZBot = invertAtMh - sdr.TrBedHeight;
                        double bedZTop = invertAtMh;
                        double bedZLo  = Math.Max(zBottom, bedZBot);
                        double bedZHi  = Math.Min(zTop,    bedZTop);
                        if (bedZHi - bedZLo > 1e-6)
                        {
                            double bedZMid = (bedZLo + bedZHi) * 0.5;
                            double Hbed    = bedZHi - bedZLo;
                            var bBot = TrenchRectAt(mh.X, mh.Y, bedZLo,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            var bMid = TrenchRectAt(mh.X, mh.Y, bedZMid, invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            var bTop = TrenchRectAt(mh.X, mh.Y, bedZHi,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            double abBot = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bedZLo,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bBot);
                            double abMid = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bedZMid, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bMid);
                            double abTop = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bedZHi,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bTop);
                            double bedVol = (Hbed / 6.0) * (abBot + 4.0 * abMid + abTop);
                            sdr.ManholeBeddingDeducted += bedVol;
                            mhBeddingSum               += bedVol;
                            bedVolDbg                    = bedVol;
                        }

                        // ── Surround overlap ──────────────────────────────────
                        // Surround occupies Z: [invertAtMh, invertAtMh + PipeOD + TrSandOverPipe].
                        // Fixed width TrWidth (no slope).
                        double surrZBot = invertAtMh;
                        double surrZTop = invertAtMh + sdr.PipeOuterDiamM + sdr.TrSandOverPipe;
                        double surrZLo  = Math.Max(zBottom, surrZBot);
                        double surrZHi  = Math.Min(zTop,    surrZTop);
                        if (surrZHi - surrZLo > 1e-6)
                        {
                            double surrZMid = (surrZLo + surrZHi) * 0.5;
                            double Hsurr    = surrZHi - surrZLo;
                            var sBot = TrenchRectAt(mh.X, mh.Y, surrZLo,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            var sMid = TrenchRectAt(mh.X, mh.Y, surrZMid, invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            var sTop = TrenchRectAt(mh.X, mh.Y, surrZHi,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                            double asBot = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, surrZLo,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sBot);
                            double asMid = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, surrZMid, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sMid);
                            double asTop = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, surrZHi,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sTop);
                            double surrVol = (Hsurr / 6.0) * (asBot + 4.0 * asMid + asTop);
                            sdr.ManholeSurroundDeducted += surrVol;
                            mhSurroundSum               += surrVol;
                            surrVolDbg                    = surrVol;
                        }

                        // ── Bedding/Surround overlap, Dolgu-basis range ───────
                        // Same zones (bedding/surround are invert-referenced, not
                        // top-referenced) but clipped to [zBottomDolgu, zTopDolgu]
                        // instead of [zBottom, zTop] — recomputed rather than reused,
                        // since the two ranges can differ. Local-only (not written to
                        // sdr.ManholeBeddingDeducted/ManholeSurroundDeducted, which
                        // stay Kazı-only diagnostics).
                        if (dolguGeomValid)
                        {
                            double bedZLoD = Math.Max(zBottomDolgu, bedZBot);
                            double bedZHiD = Math.Min(zTopDolgu,    bedZTop);
                            if (bedZHiD - bedZLoD > 1e-6)
                            {
                                double bedZMidD = (bedZLoD + bedZHiD) * 0.5;
                                double HbedD    = bedZHiD - bedZLoD;
                                var bBotD = TrenchRectAt(mh.X, mh.Y, bedZLoD,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                var bMidD = TrenchRectAt(mh.X, mh.Y, bedZMidD, invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                var bTopD = TrenchRectAt(mh.X, mh.Y, bedZHiD,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                double abBotD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, bedZLoD,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bBotD);
                                double abMidD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, bedZMidD, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bMidD);
                                double abTopD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, bedZHiD,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), bTopD);
                                mhBeddingSumDolgu += (HbedD / 6.0) * (abBotD + 4.0 * abMidD + abTopD);
                            }

                            double surrZLoD = Math.Max(zBottomDolgu, surrZBot);
                            double surrZHiD = Math.Min(zTopDolgu,    surrZTop);
                            if (surrZHiD - surrZLoD > 1e-6)
                            {
                                double surrZMidD = (surrZLoD + surrZHiD) * 0.5;
                                double HsurrD    = surrZHiD - surrZLoD;
                                var sBotD = TrenchRectAt(mh.X, mh.Y, surrZLoD,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                var sMidD = TrenchRectAt(mh.X, mh.Y, surrZMidD, invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                var sTopD = TrenchRectAt(mh.X, mh.Y, surrZHiD,  invertAtMh, sdr.TrWidth, 0.0, dirX, dirY);
                                double asBotD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, surrZLoD,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sBotD);
                                double asMidD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, surrZMidD, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sMidD);
                                double asTopD = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottomDolgu, surrZHiD,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), sTopD);
                                mhSurroundSumDolgu += (HsurrD / 6.0) * (asBotD + 4.0 * asMidD + asTopD);
                            }
                        }

                        // ── Backfill overlap ──────────────────────────────────
                        // Backfill occupies Z: [invertAtMh + PipeOD + TrSandOverPipe, zTop].
                        // Width grows with SlopeRatio (same as excavation trench).
                        double bfZLo = Math.Max(zBottom, invertAtMh + sdr.PipeOuterDiamM + sdr.TrSandOverPipe);
                        double bfZHi = zTop;
                        if (bfZHi - bfZLo > 1e-6)
                        {
                            double bfZMid = (bfZLo + bfZHi) * 0.5;
                            double Hbf    = bfZHi - bfZLo;
                            var fBot = TrenchRectAt(mh.X, mh.Y, bfZLo,  invertAtMh, sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                            var fMid = TrenchRectAt(mh.X, mh.Y, bfZMid, invertAtMh, sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                            var fTop = TrenchRectAt(mh.X, mh.Y, bfZHi,  invertAtMh, sdr.TrWidth, sdr.SlopeRatio, dirX, dirY);
                            double afBot = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bfZLo,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), fBot);
                            double afMid = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bfZMid, rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), fMid);
                            double afTop = AreaOfIntersect(ManholeSquareAt(mh.X, mh.Y, zBottom, bfZHi,  rotAngle, mh.ExcavBaseSideM, mh.ExcavSlopeRatio), fTop);
                            bfVolDbg = (Hbf / 6.0) * (afBot + 4.0 * afMid + afTop);
                            sdr.ManholeBackfillDeducted += bfVolDbg;
                        }

                        perPipeVolumes.Add((lbl, pipeVol, bedVolDbg, surrVolDbg, bfVolDbg));

                        // Accumulate into union (for Value 3 calculation)
                        if (trBot != null) unionBot.Add(trBot);
                        if (trMid != null) unionMid.Add(trMid);
                        if (trTop != null) unionTop.Add(trTop);
                    }

                    // ── StructureVolume: Σ(UnitExternalVolume × Count) ───────────
                    mh.StructureVolume = mh.StackPreCast?.Parts?
                        .Sum(p => p.UnitExternalVolume * p.Count) ?? 0.0;

                    // ── SubBaseVolume: Alt Temel Katmanları at pit bottom ─────────
                    // Uses Simpson's 1/3 rule on the frustum slice at the pit base.
                    // H = total thickness of ResolvedSubBaseLayers; slice spans
                    // [zBottom, zBottom+H] where the frustum area grows with slope.
                    {
                        double sbH = mh.ResolvedSubBaseLayers != null
                            ? mh.ResolvedSubBaseLayers.Sum(l => l.ThicknessMm / 1000.0)
                            : 0.0;
                        if (sbH > 1e-6 && mh.ExcavBaseSideM > 1e-6)
                        {
                            double sideBot = mh.ExcavBaseSideM;
                            double sideMid = mh.ExcavBaseSideM + 2.0 * (sbH * 0.5) * mh.ExcavSlopeRatio;
                            double sideTop = mh.ExcavBaseSideM + 2.0 * sbH           * mh.ExcavSlopeRatio;
                            mh.SubBaseVolume = (sbH / 6.0) * (sideBot * sideBot + 4.0 * sideMid * sideMid + sideTop * sideTop);
                        }
                        else
                        {
                            mh.SubBaseVolume = 0.0;
                        }
                    }

                    // ── Baca Geri Dolgu ───────────────────────────────────────
                    // = DolguBasisVolume − StructureVolume − SubBaseVolume − Σbedding − Σsurround
                    // (bedding/surround recomputed against Dolgu-basis Z-range above).
                    mh.BackfillVolume = dolguGeomValid
                        ? Math.Max(0, mh.DolguBasisVolume - mh.StructureVolume - mh.SubBaseVolume - mhBeddingSumDolgu - mhSurroundSumDolgu)
                        : 0.0;

                    // ── Geri Dolgu layer split (Phase 7d) ─────────────────────
                    SplitManholeBackfillLayers(mh);

                    // Value 3: manhole area NOT covered by any trench = Difference(mh, Union(trenches))
                    double freeBot = AreaOfDiff(mhBot, unionBot);
                    double freeMid = AreaOfDiff(mhMid, unionMid);
                    double freeTop = AreaOfDiff(mhTop, unionTop);
                    double freeVol = (Hmh / 6.0) * (freeBot + 4.0 * freeMid + freeTop);

                    // Build output line — TEMPORARY diagnostic detail (user request
                    // 2026-07-05) to verify the manhole-vs-pipe overlap deduction is
                    // actually being computed: depth/base/slope/volume + per-pipe
                    // excavation, Yataklama, Gömlekleme, and Geri Dolgu overlap.
                    var sb = new System.Text.StringBuilder();
                    sb.Append($"\n  Manhole {mh.NodeName}" +
                              $"  (rot={rotAngle * 180.0 / Math.PI:F1}°)" +
                              $"\n    ExcavDepth={mh.ExcavationDepth:F3} m" +
                              $"  Base={mh.ExcavBaseSideM:F3}x{mh.ExcavBaseSideM:F3} m" +
                              $"  Slope(H:V)={mh.ExcavSlopeRatio:F3}" +
                              $"  ExcavVolume={mh.ExcavationVolume:F4} m3" +
                              $"  BackfillVolume={mh.BackfillVolume:F4} m3");
                    foreach (var (lbl, excavVol, bedVol, surrVol, bfVol) in perPipeVolumes)
                        sb.Append($"\n    Pipe [{lbl}]  ExcavOverlap={excavVol:F4} m3" +
                                  $"  YataklamaOverlap={bedVol:F4} m3" +
                                  $"  GomleklemeOverlap={surrVol:F4} m3" +
                                  $"  GeriDolguOverlap={bfVol:F4} m3");
                    sb.Append($"\n    Free (no pipe)      = {freeVol:F4} m3");
                    lines.Add(sb.ToString());
                }
            }

            return lines;
        }

        /// <summary>
        /// Splits mh.BackfillVolume among the matched tier's Geri Dolgu layers
        /// (mh.ResolvedBackfillLayers, set by ManholeAIService) by thickness ratio
        /// — mirrors the Alt Temel Katmanları convention (plain thickness, no
        /// area weighting) rather than pipe-trench's full width-at-height area
        /// integration, since exactly where the shaft's own footprint starts
        /// narrowing within this zone isn't modeled per-component here.
        ///
        /// The "Yüzeye Doldur" layer has no configured thickness of its own, so
        /// its share is approximated from the zone's own height, itself back-out
        /// from BackfillVolume using the pit's base side as a representative
        /// (non-sloped) cross-section — a deliberate simplification for this
        /// first pass, since the zone typically spans a short height near the
        /// top of the pit where slope widening is small. Revisit if real splits
        /// look off.
        /// </summary>
        private static void SplitManholeBackfillLayers(ManholeItem mh)
        {
            mh.BackfillLayerSplits.Clear();
            var layers = mh.ResolvedBackfillLayers;
            if (layers == null || layers.Count == 0 || mh.BackfillVolume <= 1e-9) return;

            double repArea = mh.ExcavBaseSideM * mh.ExcavBaseSideM;
            if (repArea <= 1e-9) return;
            double approxHeightM = mh.BackfillVolume / repArea;

            double fixedSum = layers.Where(l => !l.IsFillToSurface).Sum(l => l.ThicknessM);
            double fillToSurfaceThickness = Math.Max(0, approxHeightM - fixedSum);
            double totalThickness = fixedSum + fillToSurfaceThickness;
            if (totalThickness <= 1e-9) return;

            bool filled = false;
            foreach (var l in layers)
            {
                double thickness;
                if (l.IsFillToSurface)
                {
                    thickness = filled ? 0.0 : fillToSurfaceThickness;
                    filled = true;
                }
                else
                {
                    thickness = l.ThicknessM;
                }

                double ratio = thickness / totalThickness;
                mh.BackfillLayerSplits.Add(new TrenchLayerSplit
                {
                    LayerName    = l.LayerName,
                    MaterialType = l.MaterialType,
                    Ratio        = ratio,
                    Volume       = ratio * mh.BackfillVolume
                });
            }
        }

        // =====================================================================
        // Public API – Manhole vs Manhole
        // =====================================================================

        /// <summary>
        /// Geometry State Retention pass. For each overlapping pair:
        ///   GeoLower = segment from ZBottom → zTouch  (pure raw, no overlap)
        ///   GeoUpper = segment from zTouch  → ZTop    (overlap zone, split Inside/Outside)
        ///
        /// OutsideRegion = Difference(Raw, totalIntersect)  — exclusive zone.
        /// InsideRegion  = this manhole's share of totalIntersect (split via TouchPoly_High).
        /// </summary>
        public static List<string> ComputeManholeVsManhole(BoQReport report)
        {
            var lines = new List<string>();
            if (report?.Systems == null || report.SectionDebug == null) return lines;

            var outlets = report.SectionDebug
                .Where(s => s.StartNodeName != null)
                .GroupBy(s => s.StartNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var inlets = report.SectionDebug
                .Where(s => s.EndNodeName != null)
                .GroupBy(s => s.EndNodeName, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            // Build MhInfo list (no geo initialisation here — done per pair)
            var all = new List<MhInfo>();
            foreach (var sys in report.Systems)
            {
                foreach (var mh in sys.Manholes)
                {
                    if (mh.ExcavationDepth <= 1e-6) continue;
                    double zTop    = mh.ZKazi;
                    double zBottom = zTop - mh.ExcavationDepth;
                    double rot     = ComputeRotationAngle(mh, outlets, inlets);
                    double halfTop = mh.ExcavBaseSideM / 2.0 + mh.ExcavationDepth * mh.ExcavSlopeRatio;
                    double extent  = halfTop * (Math.Abs(Math.Cos(rot)) + Math.Abs(Math.Sin(rot)));
                    all.Add(new MhInfo
                    {
                        Mh         = mh,
                        ZTop       = zTop,
                        ZBottom    = zBottom,
                        RotAngle   = rot,
                        BaseSideM  = mh.ExcavBaseSideM,
                        SlopeRatio = mh.ExcavSlopeRatio,
                        AabbMinX   = mh.X - extent,
                        AabbMaxX   = mh.X + extent,
                        AabbMinY   = mh.Y - extent,
                        AabbMaxY   = mh.Y + extent,
                    });
                }
            }

            for (int i = 0; i < all.Count - 1; i++)
            {
                for (int j = i + 1; j < all.Count; j++)
                {
                    var a = all[i];
                    var b = all[j];

                    // AABB pre-filter
                    if (a.AabbMaxX < b.AabbMinX || b.AabbMaxX < a.AabbMinX) continue;
                    if (a.AabbMaxY < b.AabbMinY || b.AabbMaxY < a.AabbMinY) continue;

                    double dist = Math.Sqrt(
                        (a.Mh.X - b.Mh.X) * (a.Mh.X - b.Mh.X) +
                        (a.Mh.Y - b.Mh.Y) * (a.Mh.Y - b.Mh.Y));
                    if (dist < 1e-6) continue;

                    // Approximate touch height using the pair's averaged base/slope
                    // (a rough starting estimate only — clamped below, and the real
                    // Inside/Outside split further down uses each manhole's own
                    // correct per-manhole polygon, so this average only affects
                    // where the search starts, not the final overlap shape).
                    double avgBaseSideM  = (a.BaseSideM  + b.BaseSideM)  / 2.0;
                    double avgSlopeRatio = (a.SlopeRatio + b.SlopeRatio) / 2.0;
                    double zTouchRaw = avgSlopeRatio > 1e-9
                        ? (dist - avgBaseSideM) / (2.0 * avgSlopeRatio) + (a.ZBottom + b.ZBottom) / 2.0
                        : Math.Max(a.ZBottom, b.ZBottom);
                    double zTopEff   = Math.Min(a.ZTop, b.ZTop);
                    double zTouch    = Math.Max(zTouchRaw, Math.Max(a.ZBottom, b.ZBottom));
                    if (zTouch >= zTopEff) continue;

                    // M_high = higher ZBottom (shallower). Its footprint at zTouch is the cutter.
                    var mHigh = a.ZBottom >= b.ZBottom ? a : b;
                    var mLow  = a.ZBottom >= b.ZBottom ? b : a;

                    // Half-plane: large CCW polygon on M_high's side of the facing edge
                    // at zTouch. This edge is fixed in XY for all elevations above zTouch;
                    // only its active length changes as the intersection polygon grows.
                    var halfPlane = BuildHalfPlane(mHigh, mLow, zTouch);
                    if (halfPlane == null) continue;

                    // Build geo segments for both manholes
                    BuildGeoSegments(mHigh, mHigh, mLow, zTouch, zTopEff, halfPlane, isHigh: true);
                    BuildGeoSegments(mLow,  mHigh, mLow, zTouch, zTopEff, halfPlane, isHigh: false);

                    // Volumes
                    double volHigh = EffectiveVolume(mHigh.Mh);
                    double volLow  = EffectiveVolume(mLow.Mh);

                    lines.Add(
                        $"\n  [{mHigh.Mh.NodeName}](HIGH) & [{mLow.Mh.NodeName}](LOW)" +
                        $"  dist={dist:F3}  zTouch={zTouch:F3}  zTopEff={zTopEff:F3}" +
                        $"\n  [{mHigh.Mh.NodeName}] zBot={mHigh.ZBottom:F3}  zTop={mHigh.ZTop:F3}" +
                        SegmentSummary(mHigh.Mh.GeoLower, "Lower") +
                        SegmentSummary(mHigh.Mh.GeoUpper, "Upper") +
                        $"  → effVol={volHigh:F4} m³" +
                        $"\n  [{mLow.Mh.NodeName}]  zBot={mLow.ZBottom:F3}  zTop={mLow.ZTop:F3}" +
                        SegmentSummary(mLow.Mh.GeoLower, "Lower") +
                        SegmentSummary(mLow.Mh.GeoUpper, "Upper") +
                        $"  → effVol={volLow:F4} m³");
                }
            }

            return lines;
        }

        // ── Segment builders ─────────────────────────────────────────────────

        /// <summary>
        /// Builds GeoLower and GeoUpper for <paramref name="self"/>.
        /// GeoLower spans ZBottom → zTouch (pure raw, no intersection).
        /// GeoUpper spans zTouch  → zTopEff (intersection zone, Inside/Outside split).
        /// </summary>
        private static void BuildGeoSegments(
            MhInfo self,
            MhInfo mHigh, MhInfo mLow,
            double zTouch, double zTopEff,
            List<double[]> halfPlane,
            bool isHigh)
        {
            var mh = self.Mh;

            // ── Lower segment: ZBottom → zTouch ──────────────────────────────
            double hLower = zTouch - self.ZBottom;
            if (hLower > 1e-6)
            {
                double zLowerMid = self.ZBottom + hLower * 0.5;
                mh.GeoLower = new ManholeGeoSegment
                {
                    Bottom = MakeRawLevel(mh.X, mh.Y, self.ZBottom, self.ZBottom, self.RotAngle, self.BaseSideM, self.SlopeRatio),
                    Mid    = MakeRawLevel(mh.X, mh.Y, self.ZBottom, zLowerMid,   self.RotAngle, self.BaseSideM, self.SlopeRatio),
                    Top    = MakeRawLevel(mh.X, mh.Y, self.ZBottom, zTouch,      self.RotAngle, self.BaseSideM, self.SlopeRatio),
                };
            }
            else
            {
                mh.GeoLower = null;  // zTouch == ZBottom, no lower zone
            }

            // ── Upper segment: zTouch → zTopEff ──────────────────────────────
            double hUpper = zTopEff - zTouch;
            if (hUpper > 1e-6)
            {
                double zUpperMid = zTouch + hUpper * 0.5;
                mh.GeoUpper = new ManholeGeoSegment
                {
                    Bottom = MakeUpperLevel(mh, self, mHigh, mLow, zTouch,    halfPlane, isHigh),
                    Mid    = MakeUpperLevel(mh, self, mHigh, mLow, zUpperMid, halfPlane, isHigh),
                    Top    = MakeUpperLevel(mh, self, mHigh, mLow, zTopEff,   halfPlane, isHigh),
                };
            }
        }

        private static ManholeGeoLevel MakeRawLevel(
            double cx, double cy, double zBottom, double z, double rot,
            double baseSideM, double slopeRatio)
        {
            var raw = ManholeSquareAt(cx, cy, zBottom, z, rot, baseSideM, slopeRatio);
            return new ManholeGeoLevel
            {
                Z             = z,
                RawPoly       = raw,
                InsideRegion  = null,
                OutsideRegion = raw != null
                    ? new List<List<double[]>> { raw }
                    : null,
            };
        }

        /// <summary>
        /// Builds one upper-segment level at elevation z with correct Inside/Outside split.
        /// OutsideRegion = Difference(Raw, totalIntersect)  — exclusive zone only.
        /// InsideRegion  = this manhole's claimed share of totalIntersect.
        /// </summary>
        private static ManholeGeoLevel MakeUpperLevel(
            ManholeItem mh,
            MhInfo self, MhInfo mHigh, MhInfo mLow,
            double z,
            List<double[]> halfPlane,
            bool isHigh)
        {
            var raw = ManholeSquareAt(mh.X, mh.Y, self.ZBottom, z, self.RotAngle, self.BaseSideM, self.SlopeRatio);
            var level = new ManholeGeoLevel { Z = z, RawPoly = raw };
            if (raw == null) return level;

            // Total 2-D intersection of both manholes at this elevation
            var polyHigh = ManholeSquareAt(
                mHigh.Mh.X, mHigh.Mh.Y, mHigh.ZBottom, z, mHigh.RotAngle, mHigh.BaseSideM, mHigh.SlopeRatio);
            var polyLow  = ManholeSquareAt(
                mLow.Mh.X,  mLow.Mh.Y,  mLow.ZBottom,  z, mLow.RotAngle, mLow.BaseSideM, mLow.SlopeRatio);

            List<List<double[]>> totalIntersect = null;
            if (polyHigh != null && polyLow != null)
                totalIntersect = ClipperGeo.Intersect(polyHigh, polyLow);

            if (totalIntersect == null || totalIntersect.Count == 0 ||
                ClipperGeo.Area(totalIntersect) <= 1e-9)
            {
                level.InsideRegion  = null;
                level.OutsideRegion = new List<List<double[]>> { raw };
                return level;
            }

            // Split totalIntersect using the fixed half-plane:
            // The half-plane is defined by M_high's facing edge at zTouch (fixed XY),
            // but its active length changes at each z as totalIntersect grows.
            //
            // M_high's share = Intersect(totalIntersect, halfPlane)
            // M_low's share  = Difference(totalIntersect, halfPlane)
            List<List<double[]>> insideRegion;
            if (isHigh)
            {
                insideRegion = totalIntersect.Count == 1
                    ? ClipperGeo.Intersect(totalIntersect[0], halfPlane)
                    : IntersectRegionWithRing(totalIntersect, halfPlane);
            }
            else
            {
                insideRegion = ClipperGeo.Difference(
                    totalIntersect,
                    new List<List<double[]>> { halfPlane });
            }

            bool hasInside = insideRegion != null && insideRegion.Count > 0 &&
                             ClipperGeo.Area(insideRegion) > 1e-9;

            level.InsideRegion = hasInside ? insideRegion : null;

            // OutsideRegion = Raw minus the TOTAL intersection (exclusive zone only)
            level.OutsideRegion = ClipperGeo.Difference(
                new List<List<double[]>> { raw },
                totalIntersect);

            return level;
        }

        /// <summary>
        /// Builds a large CCW half-plane polygon on M_high's side of its facing edge
        /// at zTouch. The facing edge is the side of M_high's polygon at zTouch whose
        /// outward normal points most toward M_low. This edge stays fixed in XY for
        /// all elevations above zTouch — only its active intersection length changes.
        /// </summary>
        private static List<double[]> BuildHalfPlane(MhInfo mHigh, MhInfo mLow, double zTouch)
        {
            var poly = ManholeSquareAt(
                mHigh.Mh.X, mHigh.Mh.Y, mHigh.ZBottom, zTouch, mHigh.RotAngle, mHigh.BaseSideM, mHigh.SlopeRatio);
            if (poly == null || poly.Count < 3) return null;

            // Unit vector from M_high toward M_low
            double dxDir = mLow.Mh.X - mHigh.Mh.X;
            double dyDir = mLow.Mh.Y - mHigh.Mh.Y;
            double dirLen = Math.Sqrt(dxDir * dxDir + dyDir * dyDir);
            if (dirLen < 1e-9) return null;
            dxDir /= dirLen;
            dyDir /= dirLen;

            // For a CCW polygon, the outward normal of edge k→k+1 is (dy, -dx)
            // Find the edge whose outward normal aligns best with M_high→M_low
            int n = poly.Count;
            int bestEdge = 0;
            double bestDot = double.MinValue;
            for (int k = 0; k < n; k++)
            {
                double edgeDx = poly[(k + 1) % n][0] - poly[k][0];
                double edgeDy = poly[(k + 1) % n][1] - poly[k][1];
                double eLen   = Math.Sqrt(edgeDx * edgeDx + edgeDy * edgeDy);
                if (eLen < 1e-9) continue;
                double normX = edgeDy / eLen;   // outward normal X
                double normY = -edgeDx / eLen;  // outward normal Y
                double dot   = normX * dxDir + normY * dyDir;
                if (dot > bestDot) { bestDot = dot; bestEdge = k; }
            }

            // Facing edge: A → B
            double[] edgeA = poly[bestEdge];
            double[] edgeB = poly[(bestEdge + 1) % n];

            // Edge unit direction
            double ex = edgeB[0] - edgeA[0];
            double ey = edgeB[1] - edgeA[1];
            double eL = Math.Sqrt(ex * ex + ey * ey);
            if (eL < 1e-9) return null;
            ex /= eL;
            ey /= eL;

            // Left perpendicular of edge direction (interior of M_high for CCW polygon)
            // = (-ey, ex)
            const double Large = 50000.0;
            double[] p0 = new[] { edgeA[0] - Large * ex,             edgeA[1] - Large * ey             };
            double[] p1 = new[] { edgeB[0] + Large * ex,             edgeB[1] + Large * ey             };
            double[] p2 = new[] { edgeB[0] + Large * ex - Large * ey, edgeB[1] + Large * ey + Large * ex };
            double[] p3 = new[] { edgeA[0] - Large * ex - Large * ey, edgeA[1] - Large * ey + Large * ex };

            // CCW: p0 → p1 (along edge) → p2 (inward) → p3 (inward) → p0
            return new List<double[]> { p0, p1, p2, p3 };
        }

        private static List<List<double[]>> IntersectRegionWithRing(
            List<List<double[]>> region, List<double[]> ring)
        {
            List<List<double[]>> result = null;
            foreach (var poly in region)
            {
                var part = ClipperGeo.Intersect(poly, ring);
                if (part == null || part.Count == 0) continue;
                result = result == null ? part : ClipperGeo.Union(result, part);
            }
            return result;
        }

        // ── Volume helpers ───────────────────────────────────────────────────

        /// <summary>
        /// Effective excavation volume = lower segment (full raw) + upper segment
        /// (exclusive zone + this manhole's Inside share).
        /// </summary>
        private static double EffectiveVolume(ManholeItem mh)
        {
            return SegmentVolume(mh.GeoLower, includeInside: false) +
                   SegmentVolume(mh.GeoUpper, includeInside: true);
        }

        private static double SegmentVolume(ManholeGeoSegment seg, bool includeInside)
        {
            if (seg?.Bottom == null || seg.Mid == null || seg.Top == null) return 0;
            double H = seg.Top.Z - seg.Bottom.Z;
            if (H <= 1e-6) return 0;
            double aBot = LevelEffArea(seg.Bottom, includeInside);
            double aMid = LevelEffArea(seg.Mid,    includeInside);
            double aTop = LevelEffArea(seg.Top,    includeInside);
            return (H / 6.0) * (aBot + 4.0 * aMid + aTop);
        }

        private static double LevelEffArea(ManholeGeoLevel lvl, bool includeInside)
        {
            if (lvl == null) return 0;
            double area = TotalArea(lvl.OutsideRegion);
            if (includeInside) area += TotalArea(lvl.InsideRegion);
            return area;
        }

        private static double TotalArea(List<List<double[]>> region)
        {
            if (region == null || region.Count == 0) return 0;
            return ClipperGeo.Area(region);
        }

        // ── Diagnostic helpers ───────────────────────────────────────────────

        private static string SegmentSummary(ManholeGeoSegment seg, string tag)
        {
            if (seg == null) return $"\n    {tag}: (none)";
            return $"\n    {tag}: " +
                   LevelSummary(seg.Bottom, "Bot") + "  " +
                   LevelSummary(seg.Mid,    "Mid") + "  " +
                   LevelSummary(seg.Top,    "Top");
        }

        private static string LevelSummary(ManholeGeoLevel lvl, string tag)
        {
            if (lvl == null) return $"[{tag}:null]";
            double rawA = lvl.RawPoly != null ? ClipperGeo.Area(lvl.RawPoly) : 0;
            double outA = TotalArea(lvl.OutsideRegion);
            double inA  = TotalArea(lvl.InsideRegion);
            return $"[{tag} z={lvl.Z:F2} raw={rawA:F3} out={outA:F3} in={inA:F3}]";
        }

        // ── Precomputed per-manhole data (used only inside ComputeManholeVsManhole) ──
        private struct MhInfo
        {
            public ManholeItem Mh;
            public double ZTop, ZBottom, RotAngle;
            public double BaseSideM, SlopeRatio;
            public double AabbMinX, AabbMaxX, AabbMinY, AabbMaxY;
        }

        /// <summary>
        /// 2-D Clipper intersection area (m²) of two manhole footprints at elevation z.
        /// Each footprint uses its own zBottom, rotAngle, base side and slope.
        /// </summary>
        private static double IntersectTwoManholes(MhInfo a, MhInfo b, double z)
        {
            var polyA = ManholeSquareAt(a.Mh.X, a.Mh.Y, a.ZBottom, z, a.RotAngle, a.BaseSideM, a.SlopeRatio);
            var polyB = ManholeSquareAt(b.Mh.X, b.Mh.Y, b.ZBottom, z, b.RotAngle, b.BaseSideM, b.SlopeRatio);
            if (polyA == null || polyB == null) return 0;
            return ClipperGeo.Area(ClipperGeo.Intersect(polyA, polyB));
        }

        // =====================================================================
        // Rotation angle
        // =====================================================================

        /// <summary>
        /// Computes the rotation angle (radians) to apply to the manhole square.
        ///
        /// PRIMARY source (confirmed 2026-07-06 against a real manually-rotated
        /// manhole in test5.xml): Urbano's own node rotation
        /// (<see cref="ManholeItem.RotationAngleRad"/>, sourced from the "NR"/
        /// "NLR" node properties) — radians, standard math CCW-from-+X, used
        /// directly whenever present. This supersedes the old always-bisector
        /// heuristic entirely for real projects, since "NLR" is present on every
        /// node regardless of whether the user manually rotated it.
        ///
        /// FALLBACK (only when Urbano gives no rotation data at all — a very old
        /// export predating this fix):
        /// • Both inlet and outlet exist → bisector of lowest-inlet and lowest-outlet directions.
        /// • Only outlets → direction of the lowest outlet.
        /// • Only inlets  → direction of the lowest inlet.
        /// • Neither       → 0.
        ///
        /// "Lowest" = pipe with the smallest invert elevation at this manhole.
        /// Directions are unit vectors FROM the manhole outward along each pipe.
        /// </summary>
        private static double ComputeRotationAngle(
            ManholeItem mh,
            Dictionary<string, List<SectionDebugRow>> outlets,
            Dictionary<string, List<SectionDebugRow>> inlets)
        {
            if (mh.RotationAngleRad.HasValue)
                return mh.RotationAngleRad.Value;

            // Lowest outlet (StartNodeName == mh): lowest InvertStart
            double[] outDir = null;
            if (outlets.TryGetValue(mh.NodeName, out var outList) && outList.Count > 0)
            {
                var lowest = outList.OrderBy(s => s.InvertStart).First();
                double dx = lowest.EndX - lowest.StartX;
                double dy = lowest.EndY - lowest.StartY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len >= 1e-6) outDir = new[] { dx / len, dy / len };
            }

            // Lowest inlet (EndNodeName == mh): lowest InvertEnd
            double[] inDir = null;
            if (inlets.TryGetValue(mh.NodeName, out var inList) && inList.Count > 0)
            {
                var lowest = inList.OrderBy(s => s.InvertEnd).First();
                // Direction away from manhole = back toward the pipe's start node
                double dx = lowest.StartX - lowest.EndX;
                double dy = lowest.StartY - lowest.EndY;
                double len = Math.Sqrt(dx * dx + dy * dy);
                if (len >= 1e-6) inDir = new[] { dx / len, dy / len };
            }

            if (outDir != null && inDir != null)
            {
                // Bisector: average the two unit vectors, then take atan2.
                // This correctly handles the circular wrap-around.
                double bx = outDir[0] + inDir[0];
                double by = outDir[1] + inDir[1];
                double bLen = Math.Sqrt(bx * bx + by * by);
                if (bLen < 1e-9)
                {
                    // The two directions are exactly opposite (180° apart) —
                    // fall back to the outlet direction.
                    return Math.Atan2(outDir[1], outDir[0]);
                }
                return Math.Atan2(by / bLen, bx / bLen);
            }

            if (outDir != null) return Math.Atan2(outDir[1], outDir[0]);
            if (inDir  != null) return Math.Atan2(inDir[1],  inDir[0]);
            return 0.0;
        }

        // =====================================================================
        // Area helpers
        // =====================================================================

        private static double AreaOfIntersect(List<double[]> mhPoly, List<double[]> trPoly)
        {
            if (mhPoly == null || trPoly == null) return 0;
            return ClipperGeo.Area(ClipperGeo.Intersect(mhPoly, trPoly));
        }

        private static double AreaOfDiff(List<double[]> mhPoly, List<List<double[]>> trPolys)
        {
            if (mhPoly == null) return 0;
            if (trPolys == null || trPolys.Count == 0) return ClipperGeo.Area(mhPoly);

            // Union all trench polygons, then subtract from manhole polygon
            var region = new List<List<double[]>> { trPolys[0] };
            for (int i = 1; i < trPolys.Count; i++)
                region = ClipperGeo.Union(region, new List<List<double[]>> { trPolys[i] });

            return ClipperGeo.Area(ClipperGeo.Difference(
                new List<List<double[]>> { mhPoly }, region));
        }

        // =====================================================================
        // Polygon builders
        // =====================================================================

        /// <summary>
        /// Rotated square manhole footprint in XY at elevation z.
        /// Half-side = baseSideM/2 + (z - zBottom) * slopeRatio.
        /// The square is centred at (cx, cy) and rotated by <paramref name="rotAngle"/> radians.
        /// baseSideM/slopeRatio come from ManholeItem.ExcavBaseSideM/ExcavSlopeRatio
        /// (Phase 7, resolved per-manhole in ManholeAIService) — 0 for an
        /// unresolved manhole, which correctly yields no polygon here.
        /// </summary>
        private static List<double[]> ManholeSquareAt(
            double cx, double cy, double zBottom, double z, double rotAngle,
            double baseSideM, double slopeRatio)
        {
            double rise     = Math.Max(0, z - zBottom);
            double halfSide = baseSideM / 2.0 + rise * slopeRatio;
            if (halfSide <= 1e-9) return null;

            double cosA = Math.Cos(rotAngle);
            double sinA = Math.Sin(rotAngle);

            // Axis-aligned offsets, then rotate each corner about (cx, cy)
            var localCorners = new[]
            {
                new[] { -halfSide, -halfSide },  // BL
                new[] {  halfSide, -halfSide },  // BR
                new[] {  halfSide,  halfSide },  // TR
                new[] { -halfSide,  halfSide },  // TL
            };

            var ring = new List<double[]>(4);
            foreach (var lc in localCorners)
            {
                ring.Add(new[]
                {
                    cx + lc[0] * cosA - lc[1] * sinA,
                    cy + lc[0] * sinA + lc[1] * cosA
                });
            }
            return ring;  // CCW preserved by rotation
        }

        /// <summary>
        /// Trench rectangle footprint in XY at elevation z.
        /// Width = baseTrenchWidth + 2*(z - invertAtMh)*slopeRatio, centred on pipe axis.
        /// Starts at the manhole centre and extends 10 m outward along the pipe direction.
        /// </summary>
        private static List<double[]> TrenchRectAt(
            double cx, double cy,
            double z, double invertAtMh,
            double baseTrenchWidth, double slopeRatio,
            double dirX, double dirY)
        {
            double rise  = Math.Max(0, z - invertAtMh);
            double halfW = (baseTrenchWidth + 2.0 * rise * slopeRatio) * 0.5;
            if (halfW <= 1e-9) return null;

            // Perpendicular (90° CCW from dir)
            double perpX = -dirY, perpY = dirX;
            const double Reach = 10.0;  // m — safely beyond any manhole footprint

            // CCW: near-left → far-left → far-right → near-right
            return new List<double[]>
            {
                new[] { cx + perpX * halfW,                cy + perpY * halfW                },
                new[] { cx + dirX * Reach + perpX * halfW, cy + dirY * Reach + perpY * halfW },
                new[] { cx + dirX * Reach - perpX * halfW, cy + dirY * Reach - perpY * halfW },
                new[] { cx - perpX * halfW,                cy - perpY * halfW                },
            };
        }
    }
}
