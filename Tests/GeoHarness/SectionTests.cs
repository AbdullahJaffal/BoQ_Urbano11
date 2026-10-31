using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ;
using UrbanoMetraj.BoQ.Models;
using UrbanoMetraj.BoQ.Sections;
using UrbanoMetraj.BoQ.Services;

namespace GeoHarness
{
    /// <summary>
    /// Cross-section (UT_CROSS_SECTIONS) extraction on the REAL V2 engine:
    ///   S1  a section at an engine station reproduces that station's resolved Kazı area
    ///   S2  owned + lost-to-other = gross (Kazı) in every scenario
    ///   S3  "Yok say" ⇒ owned = gross, nothing lost
    ///   S4  same bisector cut of a crossing pair: Σ owned Kazı / Geri Dolgu identical in KU / KL / SP
    ///   S5  extracting sections leaves the engine volumes untouched (E1–E4 stay as they are)
    ///   S6  an off-grid clash-free section = linear interpolation of its engine neighbours
    ///   P*  station planner (0.5 m grid, ends always, zone edges, min gap)
    ///   K*  kol chainage (longest line from the outlet, branches from their junction)
    /// </summary>
    internal static partial class Program
    {
        private static readonly TiePreference[] SectionScenarios =
            { TiePreference.KeepUpper, TiePreference.KeepLower, TiePreference.Split };

        private static double RegionArea(SectionCut c, SectionLayer layer, SectionRegionRole role)
            => c.Regions.Where(r => r.Layer == layer && r.Role == role && (role == SectionRegionRole.LostToOther || r.OtherPipeIndex < 0))
                        .Sum(r => ClipperGeo.AreaRaw(r.Rings));

        private static void S_Sections()
        {
            S_ClashCases();
            S5_VolumesUntouched();
            S6_OffGridInterpolation();
            S9_RerunFromPersistedFields();
            S10_ZeroLengthPipe();
            S11_SubLayers();
            P_Planner();
            K_KolChainage();
            L_Layout();
        }

        private static void L_Layout()
        {
            Section("L — yerleşim motoru + stil XML");
            var (_, cap) = RunCapture(
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 15, Y0 = -15, X1 = 15, Y1 = 15, G0 = 80, G1 = 80, I0 = 78.0, I1 = 77.9, Dn = 0.4, Od = 0.47 });
            var cuts = new List<SectionCut>();
            for (int pi = 0; pi < 2; pi++)
                foreach (double dd in SectionStationPlanner.Plan(cap.Rows[pi].Length2D, cap.PerPipeZones[pi].ToList(),
                                                                 new SectionStationPlanner.Options { Interval = 5 }))
                    cuts.Add(BoQSimplifiedTestV2Command.BuildSectionCut(cap, pi, dd, TiePreference.Split, TiePreference.Split));

            foreach (var mode in new[] { CrossingDisplayMode.OwnOnly, CrossingDisplayMode.FullExcavation, CrossingDisplayMode.FullWithGhost })
            {
                var style = SectionStyle.Default(); style.Crossing = mode; style.VerticalScale = 50; style.ShowGrid = true;
                style.E(SectionElements.Izgara).Visible = true;
                var sheet = SectionLayoutEngine.Layout(cuts, style);
                bool finite = sheet.Sections.SelectMany(s => s.Prims).All(p =>
                    (p.Loops == null || p.Loops.All(l => l.All(v => !double.IsNaN(v[0]) && !double.IsNaN(v[1]) && !double.IsInfinity(v[0]))))
                    && (p.Points == null || p.Points.All(v => !double.IsNaN(v[0]) && !double.IsNaN(v[1])))
                    && !double.IsNaN(p.X) && !double.IsNaN(p.Y));
                // No two slots overlap.
                var ss = sheet.Sections;
                bool overlap = false;
                for (int i = 0; i < ss.Count; i++)
                    for (int j = i + 1; j < ss.Count; j++)
                        if (ss[i].MinX < ss[j].MaxX - 1e-6 && ss[j].MinX < ss[i].MaxX - 1e-6 &&
                            ss[i].MinY < ss[j].MaxY - 1e-6 && ss[j].MinY < ss[i].MaxY - 1e-6) overlap = true;
                int others = sheet.Sections.SelectMany(s => s.Prims).Count(p => p.IsOther);
                Report(sheet.Sections.Count == cuts.Count && finite && !overlap && (mode == CrossingDisplayMode.OwnOnly ? others == 0 : others > 0),
                       $"L1 {mode}: {cuts.Count} kesit, sonlu, çakışmasız yerleşim, diğer boru {(mode == CrossingDisplayMode.OwnOnly ? "yok" : "var")}",
                       $"bölüm {sheet.Sections.Count}, diğer {others}, örtüşme {overlap}");
            }

            // Visual check: SECTION_SVG=<file> dumps the FullExcavation sheet (layout debugging only).
            string svgPath = Environment.GetEnvironmentVariable("SECTION_SVG");
            if (!string.IsNullOrEmpty(svgPath))
            {
                var style = SectionStyle.Default(); style.Crossing = CrossingDisplayMode.FullWithGhost; style.VerticalScale = 50;
                System.IO.File.WriteAllText(svgPath, ToSvg(SectionLayoutEngine.Layout(cuts, style), style));
                Console.WriteLine("   SVG → " + svgPath);
            }

            var st0 = SectionStyle.Default();
            st0.Name = "Test"; st0.VerticalScale = 25; st0.E(SectionElements.GeriDolgu).Hatch = "AR-CONC";
            st0.NetworkColors["YSU"] = 5; st0.TableRows.Reverse();
            var back = SectionStyle.FromXml(st0.ToXml());
            Report(back.Name == "Test" && Math.Abs(back.VerticalScale - 25) < 1e-12 && back.E(SectionElements.GeriDolgu).Hatch == "AR-CONC"
                   && back.NetworkColors["YSU"] == 5 && back.TableRows[0].Key == st0.TableRows[0].Key,
                   "L2 stil XML gidiş-dönüş", back.Name);

            st0.HatchSubLayers = true; st0.RowCommonDatum = true;
            st0.SubLayers["Toprak"] = new SubLayerStyle { Name = "Toprak", Hatch = "EARTH", HatchScale = 0.02, HatchAngle = 45, HatchColorAci = 32, HatchTransparency = 20 };
            var b2 = SectionStyle.FromXml(st0.ToXml());
            var run = new SectionRunOptions { CompareScenarios = true, CompareSet = new List<TiePreference> { TiePreference.Split, TiePreference.Ignore } };
            var r2 = SectionRunOptions.FromXml(run.ToXml());
            var sl = b2.SubLayers.TryGetValue("Toprak", out var t) ? t : null;
            Report(b2.HatchSubLayers && b2.RowCommonDatum && sl != null && sl.Hatch == "EARTH" && Math.Abs(sl.HatchScale - 0.02) < 1e-12
                   && sl.HatchAngle == 45 && sl.HatchColorAci == 32 && sl.HatchTransparency == 20
                   && r2.CompareScenarios && r2.CompareSet.SequenceEqual(new[] { TiePreference.Split, TiePreference.Ignore })
                   && b2.TableRows.Any(r => r.Key == SectionTableRows.AltKatmanAlanlari),
                   "L2b alt katman stilleri, ortak KK, karşılaştırma seçenekleri XML gidiş-dönüş", "");
        }

        private static (BoQReport report, BoQSimplifiedTestV2Command.V2SectionCapture cap) RunCapture(params PipeSpec[] specs)
        {
            var report = new BoQReport();
            foreach (var p in specs) report.SectionDebug.Add(Row(p));
            var cap = BoQSimplifiedTestV2Command.RunV2EngineCapture(report);
            return (report, cap);
        }

        private static void S_ClashCases()
        {
            var cases = new (string Title, PipeSpec[] Specs)[]
            {
                ("90° kesişme", new[] {
                    new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                    new PipeSpec { Name = "B", X0 = 15, Y0 = -15, X1 = 15, Y1 = 15, G0 = 80, G1 = 80, I0 = 78.0, I1 = 77.9, Dn = 0.4, Od = 0.47 } }),
                ("30° kesişme (B ters yönde)", new[] {
                    new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                    new PipeSpec { Name = "B", X0 = 28, Y0 = 7.5, X1 = 2, Y1 = -7.5, G0 = 80, G1 = 80, I0 = 77.6, I1 = 77.8 } }),
                ("paralel yakın borular", new[] {
                    new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                    new PipeSpec { Name = "B", X0 = 0, Y0 = 1.2, X1 = 30, Y1 = 1.25, G0 = 80, G1 = 80, I0 = 77.6, I1 = 77.4 } }),
                ("paralel ters yönlü borular", new[] {
                    new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                    new PipeSpec { Name = "B", X0 = 30, Y0 = 1.25, X1 = 0, Y1 = 1.2, G0 = 80, G1 = 80, I0 = 77.4, I1 = 77.6 } }),
            };

            foreach (var (title, specs) in cases)
            {
                Section("S — kesit çıkarımı: " + title);
                var (_, cap) = RunCapture(specs);

                // Every engine station with a clash on pipe A.
                var clashSts = cap.Stations[0].Where(s => (s.Clashes?.Count ?? 0) > 0).ToList();
                Report(clashSts.Count > 0, "S0 A borusunda çakışmalı istasyon var", $"{clashSts.Count}");
                if (clashSts.Count == 0) continue;

                int s1Bad = 0, s2Bad = 0, s3Bad = 0, s4Bad = 0, s4n = 0;
                double s1Worst = 0, s2Worst = 0, s4Worst = 0;
                foreach (var st in clashSts)
                {
                    var c0 = st.Clashes[0];
                    foreach (var sc in SectionScenarios)
                    {
                        var cut = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, st.StationDist, sc, sc);

                        // S1 — single clash: owned Kazı = gross − this pipe's scenario loss.
                        if (st.Clashes.Count == 1)
                        {
                            double gross = ClipperGeo.AreaRaw(new List<List<double[]>> { st.ExcavPoly });
                            double loss  = sc == TiePreference.Split ? ClipperGeo.AreaRaw(c0.LoseSPE)
                                         : (sc == TiePreference.KeepUpper) != c0.IsUpper ? ClipperGeo.AreaRaw(c0.OvE) : 0;
                            double err = Math.Abs(cut.AreaKaziNet - (gross - loss));
                            s1Worst = Math.Max(s1Worst, err);
                            if (err > 2e-3) s1Bad++;
                        }

                        // S2 — owned + lost = gross (Kazı).
                        double own  = RegionArea(cut, SectionLayer.Kazi, SectionRegionRole.Owned);
                        double lost = RegionArea(cut, SectionLayer.Kazi, SectionRegionRole.LostToOther);
                        double gr   = RegionArea(cut, SectionLayer.Kazi, SectionRegionRole.GrossOutline);
                        double e2   = Math.Abs(own + lost - gr);
                        s2Worst = Math.Max(s2Worst, e2);
                        if (e2 > 2e-3) s2Bad++;
                    }

                    // S3 — Ignore.
                    var ig = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, st.StationDist, TiePreference.Ignore, TiePreference.Ignore);
                    if (Math.Abs(ig.AreaKaziNet - ig.AreaKaziGross) > 2e-3
                        || ig.Regions.Any(r => r.Role == SectionRegionRole.LostToOther)) s3Bad++;

                    // S4 — same cut of both pipes (the paired station): Σ owned identical across scenarios.
                    var other = c0.Other;
                    if ((other.Clashes?.Count ?? 0) == 1 && st.Clashes.Count == 1)
                    {
                        s4n++;
                        double[] ex = new double[3], bf = new double[3];
                        for (int k = 0; k < 3; k++)
                        {
                            var ca = ResolveFor(cap, 0, st, SectionScenarios[k]);
                            var cb = ResolveFor(cap, c0.OtherPipe, other, SectionScenarios[k]);
                            ex[k] = ca.AreaKaziNet + cb.AreaKaziNet;
                            bf[k] = ca.AreaGeriDolguNet + cb.AreaGeriDolguNet;
                        }
                        double e4 = Math.Max(Math.Max(Math.Abs(ex[0] - ex[1]), Math.Abs(ex[0] - ex[2])),
                                             Math.Max(Math.Abs(bf[0] - bf[1]), Math.Abs(bf[0] - bf[2])));
                        s4Worst = Math.Max(s4Worst, e4);
                        if (e4 > 3e-3) s4Bad++;
                    }
                }
                Report(s1Bad == 0, "S1 istasyon kesiti = motorun çözülmüş Kazı alanı", $"kötü {s1Bad}, en büyük fark {s1Worst:F4} m²");
                Report(s2Bad == 0, "S2 sahip + kaybedilen = brüt (Kazı)", $"kötü {s2Bad}, en büyük fark {s2Worst:F4} m²");
                Report(s3Bad == 0, "S3 Yok say ⇒ sahip = brüt, kayıp yok", $"kötü {s3Bad}");
                Report(s4n > 0 && s4Bad == 0, "S4 aynı kesitte Σ sahip KU = KL = SP", $"{s4n} kesit, kötü {s4Bad}, en büyük fark {s4Worst:F4} m²");

                // S8 — no sliver rings (mean width 2·A/P under 2 mm) in any owned region, on either
                // pipe, any scenario. Small but real corner pieces (e.g. 3×5 cm of Gömlekleme) are fine.
                int slivers = 0;
                for (int pi = 0; pi < cap.Rows.Count; pi++)
                    foreach (var st in cap.Stations[pi].Where(s => (s.Clashes?.Count ?? 0) > 0))
                        foreach (var sc in SectionScenarios)
                        {
                            var cut = BoQSimplifiedTestV2Command.BuildSectionCutAtStation(cap, pi, st, sc, sc);
                            slivers += cut.Regions.Where(r => r.Role == SectionRegionRole.Owned)
                                .SelectMany(r => r.Rings).Count(rg => MeanWidth(rg) < 2e-3);
                        }
                Report(slivers == 0, "S8 sahip bölgelerde ince şerit (sliver) yok", $"{slivers}");

                // Mirroring: the other pipe's axis lies on the side where its trench is drawn.
                var cutA = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, clashSts[clashSts.Count / 2].StationDist,
                                                                       TiePreference.Ignore, TiePreference.Ignore);
                var oth = cutA.Regions.FirstOrDefault(r => r.Role == SectionRegionRole.OtherTrench && r.Layer == SectionLayer.BoruGovdesi);
                if (oth != null)
                {
                    double cu = oth.Rings[0].Average(v => v[0]);
                    double off = cutA.Clashes[0].OtherOffsetU;
                    Report(Math.Abs(cu - off) < 0.05, "S7 diğer borunun gövdesi kendi eksen ofsetinde", $"merkez {cu:F3}, ofset {off:F3}");
                }
            }
        }

        private static string ToSvg(SectionSheet sheet, SectionStyle style)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.###", inv);
            string Col(string key)
            {
                switch (style.E(key).LayerColorAci)
                {
                    case 1: return "#d00"; case 2: return "#cc0"; case 3: return "#0a0"; case 4: return "#0aa"; case 5: return "#00c";
                    case 6: return "#c0c"; case 8: return "#888"; case 9: return "#bbb"; case 30: return "#e70"; case 94: return "#2a6";
                    case 150: return "#08c"; default: return "#222";
                }
            }
            double w = sheet.MaxX - sheet.MinX, h = sheet.MaxY - sheet.MinY;
            var sb = new System.Text.StringBuilder();
            sb.Append($"<svg xmlns='http://www.w3.org/2000/svg' viewBox='{N(sheet.MinX)} {N(-sheet.MaxY)} {N(w)} {N(h)}' width='{N(w * 40)}' height='{N(h * 40)}' style='background:#fff'>");
            foreach (var p in sheet.Sections.SelectMany(s => s.Prims).Concat(sheet.SheetPrims))
            {
                string c = Col(p.Element);
                string dash = style.E(p.Element).Linetype.StartsWith("DASH", StringComparison.OrdinalIgnoreCase) || style.E(p.Element).Linetype == "CENTER" ? " stroke-dasharray='0.08 0.05'" : "";
                if (p.Kind == DrawPrimKind.Ring || p.Kind == DrawPrimKind.Hatch)
                {
                    string d = string.Join(" ", p.Loops.Select(l => "M" + string.Join(" L", l.Select(v => N(v[0]) + "," + N(-v[1]))) + " Z"));
                    sb.Append(p.Kind == DrawPrimKind.Hatch
                        ? $"<path d='{d}' fill='{c}' fill-opacity='0.25' fill-rule='evenodd' stroke='none'/>"
                        : $"<path d='{d}' fill='none' stroke='{c}' stroke-width='0.012'{dash}/>");
                }
                else if (p.Kind == DrawPrimKind.Polyline)
                    sb.Append($"<polyline points='{string.Join(" ", p.Points.Select(v => N(v[0]) + "," + N(-v[1])))}' fill='none' stroke='{c}' stroke-width='0.01'{dash}/>");
                else
                {
                    string anchor = p.Anchor.ToString().EndsWith("Left") ? "start" : p.Anchor.ToString().EndsWith("Right") ? "end" : "middle";
                    string baseline = p.Anchor.ToString().StartsWith("Top") ? "hanging" : p.Anchor.ToString().StartsWith("Middle") ? "middle" : "auto";
                    string rot = Math.Abs(p.Rotation) > 1e-6 ? $" transform='rotate({N(-p.Rotation * 180 / Math.PI)} {N(p.X)} {N(-p.Y)})'" : "";
                    sb.Append($"<text x='{N(p.X)}' y='{N(-p.Y)}' font-size='{N(p.Height)}' font-family='Arial' text-anchor='{anchor}' dominant-baseline='{baseline}' fill='{c}'{rot}>{System.Security.SecurityElement.Escape(p.Text)}</text>");
                }
            }
            sb.Append("</svg>");
            return sb.ToString();
        }

        private static double MeanWidth(List<double[]> ring)
        {
            double per = 0;
            for (int i = 0; i < ring.Count; i++)
            {
                var a = ring[i]; var b = ring[(i + 1) % ring.Count];
                per += Math.Sqrt((b[0] - a[0]) * (b[0] - a[0]) + (b[1] - a[1]) * (b[1] - a[1]));
            }
            return per < 1e-12 ? 0 : 2.0 * ClipperGeo.AreaRaw(new List<List<double[]>> { ring }) / per;
        }

        private static SectionCut ResolveFor(BoQSimplifiedTestV2Command.V2SectionCapture cap, int pipe,
                                             BoQSimplifiedTestV2Command.SimplifiedStation st, TiePreference sc)
            => BoQSimplifiedTestV2Command.BuildSectionCutAtStation(cap, pipe, st, sc, sc);

        private static void S5_VolumesUntouched()
        {
            Section("S5 — kesit çıkarımı hacimleri değiştirmez");
            var specs = new[] {
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 15, Y0 = -15, X1 = 15, Y1 = 15, G0 = 80, G1 = 80, I0 = 78.0, I1 = 77.9 } };
            var r1 = new BoQReport(); foreach (var p in specs) r1.SectionDebug.Add(Row(p));
            BoQSimplifiedTestV2Command.RunV2Engine(r1);
            var (r2, cap) = RunCapture(specs);
            foreach (var sc in SectionScenarios)
                for (double d = 0; d <= 30; d += 0.5) BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, d, sc, sc);
            for (int i = 0; i < 2; i++)
            {
                var a = r1.SectionDebug[i]; var b = r2.SectionDebug[i];
                double diff = Math.Abs(a.VExcavKU - b.VExcavKU) + Math.Abs(a.VExcavKL - b.VExcavKL) + Math.Abs(a.VExcavSP - b.VExcavSP)
                            + Math.Abs(a.VBackfillKU - b.VBackfillKU) + Math.Abs(a.VBackfillSP - b.VBackfillSP)
                            + Math.Abs(a.VBeddingNet - b.VBeddingNet) + Math.Abs(a.VSurroundNet - b.VSurroundNet);
                Report(diff < 1e-12, $"S5 {a.PipeName}: hacimler aynı", $"Σ|fark| {diff:E2}");
            }
        }

        // Exactly the inputs DwgBoQStore persists (METADATA incl. the 2026-10-29 tail) — the
        // re-run in UT_CROSS_SECTIONS sees nothing else.
        private static SectionDebugRow PersistedCopy(SectionDebugRow s, int calcOrder) => new SectionDebugRow
        {
            SystemName = s.SystemName, PipeName = s.PipeName, StartNodeName = s.StartNodeName, EndNodeName = s.EndNodeName,
            DiameterMm = s.DiameterMm, Material = s.Material, PipeOuterDiamM = s.PipeOuterDiamM, Length2D = s.Length2D,
            StartX = s.StartX, StartY = s.StartY, StartTerrainZ = s.StartTerrainZ, EndX = s.EndX, EndY = s.EndY, EndTerrainZ = s.EndTerrainZ,
            InvertStart = s.InvertStart, InvertEnd = s.InvertEnd, DepthToInvStart = s.DepthToInvStart, DepthToInvEnd = s.DepthToInvEnd,
            TrWidth = s.TrWidth, TrBedHeight = s.TrBedHeight, TrSandOverPipe = s.TrSandOverPipe, TrAngleL = s.TrAngleL, TrAngleR = s.TrAngleR,
            SlopeRatio = s.SlopeRatio, TopWidthBed = s.TopWidthBed, ABed = s.ABed, HSurround = s.HSurround, BaseWidthSurr = s.BaseWidthSurr,
            TopWidthSurr = s.TopWidthSurr, ASurroundGross = s.ASurroundGross, PipeArea = s.PipeArea, ASurroundNet = s.ASurroundNet,
            TrueDepthStart = s.TrueDepthStart, TrueDepthEnd = s.TrueDepthEnd, TopWidthExcavS = s.TopWidthExcavS, TopWidthExcavE = s.TopWidthExcavE,
            AExcavStart = s.AExcavStart, AExcavEnd = s.AExcavEnd, ABackfillStart = s.ABackfillStart, ABackfillEnd = s.ABackfillEnd,
            StartDolguZ = s.StartDolguZ, EndDolguZ = s.EndDolguZ, StartNodeGuid = s.StartNodeGuid, EndNodeGuid = s.EndNodeGuid,
            CalcOrder = calcOrder
        };

        private static void S9_RerunFromPersistedFields()
        {
            Section("S9 — kayıtlı alanlardan yeniden çalıştırma = hesap (bayat-veri kontrolünün temeli)");
            var specs = new[] {
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 19.8705, Y1 = 0, G0 = 80, G1 = 80, I0 = 78.46, I1 = 77.99 },
                new PipeSpec { Name = "B", X0 = 17.5, Y0 = -8, X1 = 18.5, Y1 = 8, G0 = 80, G1 = 80, I0 = 78.6, I1 = 78.5 },
                new PipeSpec { Name = "C", X0 = 19.2, Y0 = -8, X1 = 19.6, Y1 = 8, G0 = 80, G1 = 80, I0 = 77.5, I1 = 77.4 },
                new PipeSpec { Name = "D", X0 = 0, Y0 = 1.2, X1 = 30, Y1 = 1.25, G0 = 80, G1 = 80, I0 = 77.6, I1 = 77.4 } };
            var calc = new BoQReport(); foreach (var p in specs) calc.SectionDebug.Add(Row(p));
            BoQSimplifiedTestV2Command.RunV2Engine(calc);

            // "Load": persisted fields only, in dictionary (shuffled) order, then sorted by CalcOrder.
            var loaded = calc.SectionDebug.Select((r, i) => PersistedCopy(r, i)).Reverse().ToList();
            var rerun = new BoQReport();
            rerun.SectionDebug.AddRange(loaded.OrderBy(r => r.CalcOrder));
            BoQSimplifiedTestV2Command.RunV2EngineCapture(rerun);

            double worst = 0;
            for (int i = 0; i < calc.SectionDebug.Count; i++)
            {
                var a = calc.SectionDebug[i]; var b = rerun.SectionDebug[i];
                var va = new[] { a.VExcavKU, a.VExcavKL, a.VExcavSP, a.VExcavGross, a.VBackfillKU, a.VBackfillKL, a.VBackfillSP, a.VBackfillGross,
                                 a.VBeddingNet, a.VSurroundNet, a.VBeddingGross, a.VSurroundGross };
                var vb = new[] { b.VExcavKU, b.VExcavKL, b.VExcavSP, b.VExcavGross, b.VBackfillKU, b.VBackfillKL, b.VBackfillSP, b.VBackfillGross,
                                 b.VBeddingNet, b.VSurroundNet, b.VBeddingGross, b.VSurroundGross };
                for (int k = 0; k < va.Length; k++) worst = Math.Max(worst, Math.Abs(va[k] - vb[k]));
            }
            Report(worst == 0, "S9 tüm hacimler bit bit aynı (12 hacim × 4 boru)", $"en büyük fark {worst:E2}");
        }

        private static void S10_ZeroLengthPipe()
        {
            Section("S10 — sıfır uzunluklu boru (düşü) kesit üretmez, hata da vermez");
            var (_, cap) = RunCapture(
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 20, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "D", X0 = 20, Y0 = 0, X1 = 20, Y1 = 0, G0 = 80, G1 = 80, I0 = 76.8, I1 = 75.5 });
            SectionCut c0 = null, c1 = null; string err = "";
            try
            {
                c0 = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 1, 0, TiePreference.Split, TiePreference.Split);
                c1 = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, 20, TiePreference.Split, TiePreference.Split);
            }
            catch (Exception ex) { err = ex.GetType().Name + ": " + ex.Message; }
            Report(err == "" && c0 == null && c1 != null, "S10 düşü borusu → null, komşusu normal", err);
        }

        private static void S6_OffGridInterpolation()
        {
            Section("S6 — ızgara dışı çakışmasız kesit");
            var (_, cap) = RunCapture(
                new PipeSpec { Name = "P1", X0 = 0, Y0 = 0, X1 = 40, Y1 = 0, G0 = 80, G1 = 79, I0 = 77, I1 = 76.6 });
            var a = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, 5.0, TiePreference.Split, TiePreference.Split);
            var b = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, 10.0, TiePreference.Split, TiePreference.Split);
            var m = BoQSimplifiedTestV2Command.BuildSectionCut(cap, 0, 7.5, TiePreference.Split, TiePreference.Split);
            Report(a.Kind == SectionStationKind.EngineStation && m.Kind == SectionStationKind.GrossAtChainage,
                   "S6 5.0 = motor istasyonu, 7.5 = brüt kesit", $"{a.Kind} / {m.Kind}");
            var ga = a.Regions.First(r => r.Role == SectionRegionRole.GrossOutline).Rings[0];
            var gb = b.Regions.First(r => r.Role == SectionRegionRole.GrossOutline).Rings[0];
            var gm = m.Regions.First(r => r.Role == SectionRegionRole.GrossOutline).Rings[0];
            double worst = 0;
            for (int k = 0; k < 4; k++)
                for (int j = 0; j < 2; j++)
                    worst = Math.Max(worst, Math.Abs(gm[k][j] - (ga[k][j] + gb[k][j]) * 0.5));
            Report(worst < 1e-9, "S6 köşe noktaları komşuların doğrusal ortası", $"en büyük fark {worst:E2}");
        }

        private static void P_Planner()
        {
            Section("P — kesit istasyonu planlayıcı");
            var none = new List<(double, double)>();
            var p1 = SectionStationPlanner.Plan(23.7, none, new SectionStationPlanner.Options { Interval = 5 });
            Report(p1.SequenceEqual(new[] { 0, 5, 10, 15, 20, 23.7 }), "P1 her 5 m + uçlar", string.Join("; ", p1));
            Report(Math.Abs(SectionStationPlanner.SnapInterval(4.3) - 4.5) < 1e-9, "P2 4.3 → 4.5 (0.5 m ızgara)", "");
            var p3 = SectionStationPlanner.Plan(20, new List<(double, double)> { (7.25, 9.75) },
                new SectionStationPlanner.Options { Interval = 5 });
            Report(p3.SequenceEqual(new[] { 0, 5, 7.25, 9.75, 10, 15, 20 }), "P3 çakışma bölgesi sınırları eklenir",
                   string.Join("; ", p3));
            var p3b = SectionStationPlanner.Plan(20, new List<(double, double)> { (7.25, 9.9) },
                new SectionStationPlanner.Options { Interval = 5 });
            Report(p3b.SequenceEqual(new[] { 0, 5, 7.25, 9.9, 15, 20 }), "P3b sınıra 0.1 m yakın düzenli kesit düşer, sınır kalır",
                   string.Join("; ", p3b));
            var p4 = SectionStationPlanner.Plan(20.1, none, new SectionStationPlanner.Options { Interval = 5 });
            Report(p4.SequenceEqual(new[] { 0, 5, 10, 15, 20.1 }), "P4 son kesit boru ucuna çok yakınsa uç kalır", string.Join("; ", p4));
            var p5 = SectionStationPlanner.Plan(20, new List<(double, double)> { (7.25, 9.75) },
                new SectionStationPlanner.Options { Interval = 0.5, OnlyClashZones = true });
            Report(p5.First() == 0 && p5.Last() == 20 && p5.Skip(1).Take(p5.Count - 2).All(d => d >= 7.25 && d <= 9.75),
                   "P5 yalnızca çakışma bölgesi (+ uçlar)", string.Join("; ", p5));

            // P6 — picked plan point → snapped local chainage (Tek kesit).
            double c1 = SectionStationPlanner.ChainageAt(0, 0, 10.3, 0, 3.3, 2, out double o1);
            double c2 = SectionStationPlanner.ChainageAt(0, 0, 10.3, 0, 10.2, -1, out double o2);
            double c3 = SectionStationPlanner.ChainageAt(0, 0, 10.3, 0, -5, 0, out double o3);
            double c4 = SectionStationPlanner.ChainageAt(10.3, 0, 0, 0, 3.3, 0, out _);   // reversed pipe: from its start
            Report(Math.Abs(c1 - 3.5) < 1e-9 && Math.Abs(o1 - 2) < 1e-9 && Math.Abs(c2 - 10.3) < 1e-9 && Math.Abs(o2 - 1) < 1e-9
                   && c3 == 0 && Math.Abs(o3 - 5) < 1e-9 && Math.Abs(c4 - 7.0) < 1e-9,
                   "P6 seçilen nokta → 0.5 m ızgarası / boru ucu", $"{c1}; {c2}; {c3}; {c4}");
        }

        // A catalog stack with every group: fixed + flexible layers (heights resolve to the group).
        private static List<TrenchStackLayer> TestStack() => new List<TrenchStackLayer>
        {
            new TrenchStackLayer { Group = TrenchStackGroup.Yataklama,  Kind = TrenchStackKind.Fixed,         LayerName = "İnce Kum",   ThicknessM = 0.05 },
            new TrenchStackLayer { Group = TrenchStackGroup.Yataklama,  Kind = TrenchStackKind.FillToSurface, LayerName = "Yatak Kumu" },
            new TrenchStackLayer { Group = TrenchStackGroup.BoruEtrafi, Kind = TrenchStackKind.UpToPipeTop,   LayerName = "Boru Etrafı" },
            new TrenchStackLayer { Group = TrenchStackGroup.BoruUstu,   Kind = TrenchStackKind.Fixed,         LayerName = "Koruma",     ThicknessM = 0.10 },
            new TrenchStackLayer { Group = TrenchStackGroup.BoruUstu,   Kind = TrenchStackKind.FillToSurface, LayerName = "Üst Kum" },
            new TrenchStackLayer { Group = TrenchStackGroup.GeriDolgu,  Kind = TrenchStackKind.Fixed,         LayerName = "Seçme Dolgu", ThicknessM = 0.30 },
            new TrenchStackLayer { Group = TrenchStackGroup.GeriDolgu,  Kind = TrenchStackKind.FillToSurface, LayerName = "Toprak" },
        };

        /// <summary>
        /// S11 — sub-layer bands: Σ band areas of a group = the group's owned area (bands tile the
        /// owned region, also after a clash deduction). L3 — banded hatches + table rows per band.
        /// </summary>
        private static void S11_SubLayers()
        {
            Section("S11 / L3 — alt katmanlar (bantlar)");
            var specs = new[]
            {
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 15, Y0 = -15, X1 = 15, Y1 = 15, G0 = 80, G1 = 80, I0 = 78.0, I1 = 77.9, Dn = 0.4, Od = 0.47 }
            };
            var report = new BoQReport();
            foreach (var p in specs) { var r = Row(p); r.TrenchStack = TestStack(); report.SectionDebug.Add(r); }
            var cap = BoQSimplifiedTestV2Command.RunV2EngineCapture(report);

            int n = 0, bad = 0; double worst = 0;
            var all = new List<SectionCut>();
            foreach (var st in cap.Stations[0].Where(s => s.ExcavPoly != null))
                foreach (var sc in SectionScenarios)
                {
                    var cut = BoQSimplifiedTestV2Command.BuildSectionCutAtStation(cap, 0, st, sc, sc);
                    all.Add(cut);
                    n++;
                    double Sum(SectionLayer l) => cut.SubBands.Where(b => b.Layer == l).Sum(b => b.Area);
                    double e = Math.Max(Math.Abs(Sum(SectionLayer.Yataklama) - cut.AreaYataklamaNet),
                               Math.Max(Math.Abs(Sum(SectionLayer.Gomlekleme) - cut.AreaGomleklemeNet),
                                        Math.Abs(Sum(SectionLayer.GeriDolgu) - cut.AreaGeriDolguNet)));
                    worst = Math.Max(worst, e);
                    if (e > 1e-4) bad++;
                }
            Report(n > 0 && bad == 0, "S11 Σ alt katman alanı = grubun sahip alanı (çakışmalı istasyonlar dahil)",
                   $"{n} kesit, kötü {bad}, en büyük fark {worst:F4} m²");

            var cutC = all.First(c => c.Clashes.Count > 0);
            var style = SectionStyle.Default();
            style.HatchSubLayers = true;
            style.SubLayers["Toprak"] = new SubLayerStyle { Name = "Toprak", Hatch = "EARTH", HatchScale = 0.02 };
            style.TableRows.First(r => r.Key == SectionTableRows.AltKatmanAlanlari).Visible = true;
            var slot = SectionLayoutEngine.BuildSlot(cutC, style, 0);
            var bandHatches = slot.Prims.Where(p => p.Kind == DrawPrimKind.Hatch && p.SubLayer != null).ToList();
            var groupHatches = slot.Prims.Where(p => p.Kind == DrawPrimKind.Hatch && p.SubLayer == null
                                                   && (p.Element == SectionElements.GeriDolgu || p.Element == SectionElements.Yataklama
                                                       || p.Element == SectionElements.Gomlekleme)).ToList();
            var earth = bandHatches.Where(p => p.SubLayer == "Toprak").Select(p => SectionLayoutEngine.HatchOf(style, p).Pattern).Distinct().ToList();
            int bandsWithHatch = cutC.SubBands.Count(b => !string.IsNullOrEmpty(SectionLayoutEngine.HatchOf(style, BandElement(b), b.LayerName).Pattern));
            bool tableHasBands = cutC.SubBands.All(b => slot.Prims.Any(p => p.Kind == DrawPrimKind.Text && p.Element == SectionElements.Tablo
                                                                         && p.Text == b.LayerName));
            Report(bandHatches.Count == bandsWithHatch && groupHatches.Count == 0 && earth.SequenceEqual(new[] { "EARTH" }) && tableHasBands,
                   "L3 bant taraması (alt katman kendi deseniyle) + tabloda her alt katman bir satır",
                   $"bant tarama {bandHatches.Count}/{bandsWithHatch}, grup tarama {groupHatches.Count}, Toprak={string.Join(",", earth)}, tablo {tableHasBands}");

            // L4 — comparison rows + shared datum per row.
            var cmp = new List<SectionCut>();
            var sts = cap.Stations[0].Where(s => (s.Clashes?.Count ?? 0) > 0).Take(2).ToList();
            for (int g = 0; g < sts.Count; g++)
                foreach (var sc in SectionScenarios)
                {
                    var c = BoQSimplifiedTestV2Command.BuildSectionCutAtStation(cap, 0, sts[g], sc, sc);
                    c.CompareGroup = g;
                    cmp.Add(c);
                }
            var st2 = SectionStyle.Default(); st2.Columns = 2;   // a comparison row never wraps
            var sheet = SectionLayoutEngine.Layout(cmp, st2);
            bool sameDatum = sheet.Sections.GroupBy(s => s.Cut.CompareGroup).All(gp => gp.Select(s => s.Datum).Distinct().Count() == 1);
            bool oneRowEach = sheet.Sections.GroupBy(s => s.Cut.CompareGroup).All(gp =>
                gp.Select(s => s.Prims.First(p => p.Element == SectionElements.KiyasCizgisi && p.Kind == DrawPrimKind.Polyline).Points[0][1])
                  .Distinct().Count() == 1);
            bool labelled = sheet.Sections.All(s => s.Prims.Any(p => p.Kind == DrawPrimKind.Text && p.Text == SectionLayoutEngine.ScenarioLabel(s.Cut.ExcavScenario)));
            Report(sheet.Sections.Count == cmp.Count && sameDatum && oneRowEach && labelled,
                   "L4 karşılaştırma: istasyon başına bir satır, ortak KK, her kesitte senaryo adı",
                   $"{sheet.Sections.Count} kesit, ortak KK {sameDatum}, tek satır {oneRowEach}, etiket {labelled}");

            var st3 = SectionStyle.Default(); st3.RowCommonDatum = true; st3.Columns = 20; st3.Ordering = SectionOrdering.Continuous;
            var mixed = new List<SectionCut> { all.First(), cutC };
            var sh3 = SectionLayoutEngine.Layout(mixed, st3);
            Report(sh3.Sections.Select(s => s.Datum).Distinct().Count() == 1 && sh3.Sections[0].Datum <= Math.Min(SectionLayoutEngine.DatumOf(mixed[0]), SectionLayoutEngine.DatumOf(mixed[1])) + 1e-9,
                   "L5 satırda ortak KK = en düşük kesitinki", string.Join("; ", sh3.Sections.Select(s => s.Datum)));
        }

        private static string BandElement(SectionSubBand b)
            => b.Layer == SectionLayer.Yataklama ? SectionElements.Yataklama
             : b.Layer == SectionLayer.Gomlekleme ? SectionElements.Gomlekleme : SectionElements.GeriDolgu;

        private static SectionDebugRow KRow(string net, string s, string e, double len, double i0, double i1)
            => new SectionDebugRow { SystemName = net, StartNodeName = s, EndNodeName = e, PipeName = s + "-" + e,
                                     Length2D = len, InvertStart = i0, InvertEnd = i1 };

        private static void K_KolChainage()
        {
            Section("K — kol (hat) kilometrajı");
            var rows = new List<SectionDebugRow>
            {
                KRow("N", "H1", "J",  20, 79,   78),     // 0  main line, upstream part
                KRow("N", "J",  "O",  10, 78,   77),     // 1  main line, down to the outlet O
                KRow("N", "H2", "J",   5, 78.5, 78),     // 2  short branch at J
                KRow("N", "J",  "H3",  8, 78,   78.6),   // 3  branch at J drawn AGAINST the flow (H3 → J)
                KRow("N", "H4", "H3",  4, 79,   78.6),   // 4  extends the H3 branch → that kol starts at H4
            };
            var kol = KolChainageService.Build(rows);

            Report(kol[0].KolName == "H1" && kol[1].KolName == "H1", "K1 çıkıştan en uzun hat: H1 kolu", $"{kol[0].KolName}/{kol[1].KolName}");
            Report(Math.Abs(KolChainageService.Global(kol[1], 10, 3) - 23) < 1e-9, "K2 J→O üzerinde yerel 3 m = genel 23 m",
                   $"{KolChainageService.Global(kol[1], 10, 3)}");
            Report(kol[2].KolName == "H2" && Math.Abs(kol[2].OffsetAtUpstream) < 1e-9, "K3 kısa kol: adı ilk baca (H2), 0'dan başlar", kol[2].KolName);
            Report(kol[3].KolName == "H4" && kol[4].KolName == "H4" && kol[3].KolIndex < kol[2].KolIndex,
                   "K4 bağlantı noktasından en uzun kol önce: H4 → H3 → J", $"{kol[3].KolName} #{kol[3].KolIndex} / H2 #{kol[2].KolIndex}");
            Report(!kol[3].StartIsUpstream && Math.Abs(KolChainageService.Global(kol[3], 8, 2) - 10) < 1e-9,
                   "K5 akışa ters çizilmiş boru: genel = 4 + (8 − 2) = 10", $"{KolChainageService.Global(kol[3], 8, 2)}");
            var local = KolChainageService.Build(rows, n => n == "N");
            Report(local.Count == 0, "K6 BASINCLI ⇒ yalnızca yerel", $"{local.Count}");
            Report(Enumerable.Range(0, rows.Count).All(kol.ContainsKey), "K7 her boru bir kola atanır", $"{kol.Count}/{rows.Count}");
        }
    }
}
