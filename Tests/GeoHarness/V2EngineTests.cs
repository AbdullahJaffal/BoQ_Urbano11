using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ;
using UrbanoMetraj.BoQ.Models;

namespace GeoHarness
{
    /// <summary>
    /// Tests on the REAL V2 trench engine (BoQSimplifiedTestV2Engine.cs, RunV2Engine):
    ///   E1  gross excavation of every pipe = its own trench prism (analytic reference)
    ///   E2  Σ over all pipes: Kazı KU = KL = SP (the overlap scenarios only redistribute)
    ///   E3  Σ over all pipes: Geri Dolgu KU = KL = SP, and total fill KU = KL = SP
    ///   E4  same datum: Σ Kazı − Σ fill = Σ pipe body, in every scenario
    /// </summary>
    internal static partial class Program
    {
        private sealed class PipeSpec
        {
            public string Name;
            public double X0, Y0, X1, Y1;   // plan
            public double G0, G1;           // ground (= Kazı = Dolgu level) at start / end
            public double I0, I1;           // inner invert at start / end
            public double Dn = 0.30, Od = 0.35, TrW = 0.83, Bed = 0.15, Sand = 0.30, Slope = 1.0 / 3.0;
        }

        private static SectionDebugRow Row(PipeSpec p)
        {
            double L = Math.Sqrt((p.X1 - p.X0) * (p.X1 - p.X0) + (p.Y1 - p.Y0) * (p.Y1 - p.Y0));
            double wall = (p.Od - p.Dn) / 2.0;
            double topBed = p.TrW + 2.0 * p.Bed * p.Slope;
            double hSur = p.Od + p.Sand;
            double topSur = topBed + 2.0 * hSur * p.Slope;
            double td0 = p.G0 - (p.I0 - wall - p.Bed), td1 = p.G1 - (p.I1 - wall - p.Bed);
            return new SectionDebugRow
            {
                SystemName = "T", PipeName = p.Name,
                StartNodeName = p.Name + ".s", EndNodeName = p.Name + ".e",
                StartNodeGuid = p.Name + ".s", EndNodeGuid = p.Name + ".e",
                StartX = p.X0, StartY = p.Y0, EndX = p.X1, EndY = p.Y1, Length2D = L,
                StartTerrainZ = p.G0, EndTerrainZ = p.G1, StartDolguZ = p.G0, EndDolguZ = p.G1,
                InvertStart = p.I0, InvertEnd = p.I1,
                DiameterMm = (int)Math.Round(p.Dn * 1000), PipeOuterDiamM = p.Od,
                PipeArea = Math.PI * p.Od * p.Od / 4.0,
                TrWidth = p.TrW, TrBedHeight = p.Bed, TrSandOverPipe = p.Sand, SlopeRatio = p.Slope,
                TopWidthBed = topBed, HSurround = hSur, BaseWidthSurr = topBed, TopWidthSurr = topSur,
                TrueDepthStart = Math.Max(0, td0), TrueDepthEnd = Math.Max(0, td1),
            };
        }

        // Exact gross trench prism of one pipe (same linear interpolation the engine uses).
        private static double PrismRef(SectionDebugRow s)
        {
            double wall = (s.PipeOuterDiamM - s.DiameterMm / 1000.0) / 2.0;
            double A(double f)
            {
                double zTop = s.StartTerrainZ + (s.EndTerrainZ - s.StartTerrainZ) * f;
                double inv  = s.InvertStart + (s.InvertEnd - s.InvertStart) * f;
                double D = Math.Max(0, zTop - (inv - wall - s.TrBedHeight));
                return D * (s.TrWidth + D * s.SlopeRatio);
            }
            const int N = 200; double acc = 0;
            for (int k = 0; k <= N; k++) acc += ((k == 0 || k == N) ? 1 : (k % 2 == 1 ? 4 : 2)) * A((double)k / N);
            return acc * (s.Length2D / N) / 3.0;
        }

        private static void RunCase(string title, params PipeSpec[] specs)
        {
            Section("E — V2 engine: " + title);
            var report = new BoQReport();
            foreach (var p in specs) report.SectionDebug.Add(Row(p));
            BoQSimplifiedTestV2Command.RunV2Engine(report);
            var rows = report.SectionDebug;

            // E1 — gross excavation = prism, per pipe
            foreach (var r in rows)
            {
                double refV = PrismRef(r);
                ApproxRel($"E1 {r.PipeName}: Kazı brüt = prizma ({r.VExcavGross:F3} / {refV:F3})",
                          r.VExcavGross, refV, 1e-6);
            }

            // E2/E3 — scenario totals over all pipes
            double eKU = rows.Sum(r => r.VExcavKU), eKL = rows.Sum(r => r.VExcavKL), eSP = rows.Sum(r => r.VExcavSP);
            double bKU = rows.Sum(r => r.VBackfillKU), bKL = rows.Sum(r => r.VBackfillKL), bSP = rows.Sum(r => r.VBackfillSP);
            double sand = rows.Sum(r => r.VBeddingNet + r.VSurroundNet);
            ApproxRel($"E2 Σ Kazı KU = KL ({eKU:F3} / {eKL:F3})", eKU, eKL, 1e-6);
            ApproxRel($"E2 Σ Kazı KU = SP ({eKU:F3} / {eSP:F3})", eKU, eSP, 1e-6);
            ApproxRel($"E3 Σ Geri Dolgu KU = KL ({bKU:F3} / {bKL:F3})", bKU, bKL, 1e-6);
            ApproxRel($"E3 Σ Geri Dolgu KU = SP ({bKU:F3} / {bSP:F3})", bKU, bSP, 1e-6);
            ApproxRel($"E3 Σ toplam dolgu KU = SP ({bKU + sand:F3} / {bSP + sand:F3})", bKU + sand, bSP + sand, 1e-6);

            // E4 — same datum: Kazı − fill = pipe bodies (each scenario)
            double body = rows.Sum(r => r.PipeArea * r.Length2D);
            foreach (var (n, ex, bf) in new[] { ("KU", eKU, bKU), ("KL", eKL, bKL), ("SP", eSP, bSP) })
                ApproxAbs($"E4 {n}: Kazı − dolgu = gövde ({ex - bf - sand:F3} / {body:F3})", ex - bf - sand, body,
                          Math.Max(0.02, 2e-4 * ex));
        }

        private static void E_V2Engine()
        {
            // Single pipe, sloping ground and invert.
            RunCase("tek boru",
                new PipeSpec { Name = "P1", X0 = 0, Y0 = 0, X1 = 40, Y1 = 0, G0 = 80, G1 = 79, I0 = 77, I1 = 76.6 });

            // Crossings at 90° and 30°, different depths (bisector frame).
            RunCase("90° kesişme",
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 15, Y0 = -15, X1 = 15, Y1 = 15, G0 = 80, G1 = 80, I0 = 78.0, I1 = 77.9, Dn = 0.4, Od = 0.47 });
            RunCase("30° kesişme",
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 2, Y0 = -7.5, X1 = 28, Y1 = 7.5, G0 = 80, G1 = 80, I0 = 77.8, I1 = 77.6 });

            // Near-parallel pipes (parallel-zone path).
            RunCase("paralel yakın borular",
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 30, Y1 = 0, G0 = 80, G1 = 80, I0 = 77.0, I1 = 76.8 },
                new PipeSpec { Name = "B", X0 = 0, Y0 = 1.2, X1 = 30, Y1 = 1.25, G0 = 80, G1 = 80, I0 = 77.6, I1 = 77.4 });

            // Crossing right at a pipe end (A30→A31 case): B crosses A 0.4 m before A's end.
            RunCase("boru ucunda kesişme",
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 18.2426, Y1 = 0, G0 = 80, G1 = 79.95, I0 = 77.61, I1 = 77.25 },
                new PipeSpec { Name = "B", X0 = 17.84, Y0 = -10, X1 = 17.84, Y1 = 10, G0 = 80, G1 = 80, I0 = 78.2, I1 = 78.1 });

            // Two crossings close together on one pipe (Y25.2 case).
            RunCase("bir boruda iki kesişme",
                new PipeSpec { Name = "A", X0 = 0, Y0 = 0, X1 = 19.8705, Y1 = 0, G0 = 80, G1 = 80, I0 = 78.46, I1 = 77.99 },
                new PipeSpec { Name = "B", X0 = 17.5, Y0 = -8, X1 = 18.5, Y1 = 8, G0 = 80, G1 = 80, I0 = 78.6, I1 = 78.5 },
                new PipeSpec { Name = "C", X0 = 19.2, Y0 = -8, X1 = 19.6, Y1 = 8, G0 = 80, G1 = 80, I0 = 77.5, I1 = 77.4 });
        }

        private static void ApproxAbs(string label, double got, double want, double tol)
            => Report(Math.Abs(got - want) <= tol, label, $"got {got:F4}, want {want:F4}, tol {tol:F4}");
    }
}
