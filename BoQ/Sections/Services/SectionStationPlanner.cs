using System;
using System.Collections.Generic;
using System.Linq;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>
    /// Picks the local chainages to draw on one pipe (decisions 1 + 2): every interval
    /// metres from the pipe start on the 0.5 m engine grid, pipe start and end ALWAYS,
    /// optionally the clash-zone boundaries and a manual list. AutoCAD-free.
    /// </summary>
    public static class SectionStationPlanner
    {
        /// <summary>The engine's in-zone station spacing — every interval is a multiple of it.</summary>
        public const double Grid = 0.5;

        public static double SnapToGrid(double v) => Math.Round(v / Grid) * Grid;

        /// <summary>Nearest valid interval (≥ one grid step).</summary>
        public static double SnapInterval(double v) => Math.Max(Grid, SnapToGrid(v));

        public static bool IsOnGrid(double v) => Math.Abs(v / Grid - Math.Round(v / Grid)) < 1e-6;

        public sealed class Options
        {
            public double Interval              { get; set; } = 5.0;
            public bool   IncludeZoneBoundaries { get; set; } = true;
            /// <summary>Only sections inside / at the edges of clash zones (plus the pipe ends).</summary>
            public bool   OnlyClashZones        { get; set; }
            public List<double> Manual          { get; set; } = new List<double>();
            /// <summary>Two sections closer than this keep only the more important one (ends &gt; zone edges &gt; regular).</summary>
            public double MinGap                { get; set; } = 0.25;
        }

        public static List<double> Plan(double length, IList<(double Enter, double Exit)> zones, Options o)
        {
            o = o ?? new Options();
            zones = zones ?? new List<(double, double)>();
            double L = Math.Max(0, length);
            double step = SnapInterval(o.Interval);

            // priority: 2 = pipe end, 1 = zone boundary, 0 = regular / manual
            var pts = new Dictionary<long, (double D, int P)>();
            void Put(double d, int p)
            {
                if (d < -1e-9 || d > L + 1e-9) return;
                d = Math.Max(0, Math.Min(L, d));
                long key = (long)Math.Round(d * 1000.0);
                if (!pts.TryGetValue(key, out var cur) || cur.P < p) pts[key] = (d, p);
            }
            bool InZone(double d) => zones.Any(z => d >= z.Enter - 1e-6 && d <= z.Exit + 1e-6);

            Put(0, 2);
            Put(L, 2);
            for (long k = 1; k * step < L - 1e-6; k++)
            {
                double d = k * step;
                if (!o.OnlyClashZones || InZone(d)) Put(d, 0);
            }
            if (o.IncludeZoneBoundaries || o.OnlyClashZones)
                foreach (var z in zones) { Put(z.Enter, 1); Put(z.Exit, 1); }
            foreach (var m in o.Manual ?? new List<double>())
                Put(SnapToGrid(m), 0);

            var sorted = pts.Values.OrderBy(v => v.D).ToList();
            var kept = new List<(double D, int P)>();
            foreach (var v in sorted)
            {
                if (kept.Count > 0 && v.D - kept[kept.Count - 1].D < o.MinGap)
                {
                    if (v.P > kept[kept.Count - 1].P) kept[kept.Count - 1] = v;
                    continue;
                }
                kept.Add(v);
            }
            return kept.Select(v => v.D).ToList();
        }

        /// <summary>
        /// Picked plan point → local chainage on the pipe start (x0,y0) → end (x1,y1): the
        /// foot of the perpendicular, clamped to the pipe and snapped to the 0.5 m grid or to
        /// the pipe end, whichever is nearer (decision 1). <paramref name="offset"/> = plan
        /// distance from the point to the pipe axis.
        /// </summary>
        public static double ChainageAt(double x0, double y0, double x1, double y1, double px, double py, out double offset)
        {
            double dx = x1 - x0, dy = y1 - y0, L = Math.Sqrt(dx * dx + dy * dy);
            if (L < 1e-9) { offset = Math.Sqrt((px - x0) * (px - x0) + (py - y0) * (py - y0)); return 0; }
            double t = ((px - x0) * dx + (py - y0) * dy) / L;
            double tc = Math.Max(0, Math.Min(L, t));
            double fx = x0 + dx * tc / L, fy = y0 + dy * tc / L;
            offset = Math.Sqrt((px - fx) * (px - fx) + (py - fy) * (py - fy));
            double g = Math.Min(SnapToGrid(tc), Math.Floor(L / Grid + 1e-9) * Grid);
            return Math.Abs(L - tc) < Math.Abs(g - tc) ? L : g;
        }
    }
}
