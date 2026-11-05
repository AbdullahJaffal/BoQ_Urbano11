using System;
using System.Collections.Generic;
using System.Linq;
using UrbanoMetraj.BoQ.Models;

namespace UrbanoMetraj.BoQ.Sections
{
    /// <summary>A pipe's place on its kol (branch line).</summary>
    public sealed class KolAssignment
    {
        /// <summary>Name of the kol's first (most upstream) manhole — chainage 0 there.</summary>
        public string KolName          { get; set; }
        /// <summary>Kol chainage at this pipe's upstream end (m, 2-D).</summary>
        public double OffsetAtUpstream { get; set; }
        /// <summary>True when the pipe's start node is its upstream end.</summary>
        public bool   StartIsUpstream  { get; set; }
        /// <summary>Order of the kol within its network (0 = longest line from the outlet).</summary>
        public int    KolIndex         { get; set; }
    }

    /// <summary>
    /// Global (network-wide) chainage — decision 10, user definition 2026-10-29:
    /// from the outlet, take the longest line through the network; every line joining it
    /// starts at its connection point and again takes its longest line, and so on until
    /// the branched network is split into kol lines. Each kol is named after its first
    /// (most upstream) manhole, chainage 0 there, increasing downstream to its
    /// connection point / the outlet. AutoCAD-free.
    ///
    /// Flow direction per pipe: the end with the higher invert is upstream; equal inverts
    /// → start node upstream (as drawn). Outlets: nodes with no downstream pipe.
    /// Networks for which <c>isLocalOnly</c> is true (BASINCLI) get no assignment.
    /// </summary>
    public static class KolChainageService
    {
        public static double Global(KolAssignment a, double length, double local)
            => a == null ? double.NaN
             : a.StartIsUpstream ? a.OffsetAtUpstream + local : a.OffsetAtUpstream + (length - local);

        public static Dictionary<int, KolAssignment> Build(IList<SectionDebugRow> rows, Func<string, bool> isLocalOnly = null)
        {
            var result = new Dictionary<int, KolAssignment>();
            if (rows == null) return result;
            var byNet = Enumerable.Range(0, rows.Count).GroupBy(i => rows[i].SystemName ?? "", StringComparer.OrdinalIgnoreCase);
            foreach (var net in byNet)
            {
                if (isLocalOnly != null && isLocalOnly(net.Key)) continue;
                BuildNetwork(rows, net.ToList(), result);
            }
            return result;
        }

        private static void BuildNetwork(IList<SectionDebugRow> rows, List<int> pipes, Dictionary<int, KolAssignment> result)
        {
            string Key(string guid, string name) => !string.IsNullOrEmpty(guid) ? "G:" + guid : "N:" + (name ?? "");
            var up = new Dictionary<int, string>(); var down = new Dictionary<int, string>();
            var startUp = new Dictionary<int, bool>();
            var nodeName = new Dictionary<string, string>();
            foreach (int p in pipes)
            {
                var r = rows[p];
                string s = Key(r.StartNodeGuid, r.StartNodeName), e = Key(r.EndNodeGuid, r.EndNodeName);
                nodeName[s] = r.StartNodeName ?? ""; nodeName[e] = r.EndNodeName ?? "";
                bool su = !(r.InvertEnd > r.InvertStart + 1e-6);
                startUp[p] = su;
                up[p] = su ? s : e; down[p] = su ? e : s;
            }
            var incoming = new Dictionary<string, List<int>>();
            var hasOut   = new HashSet<string>();
            foreach (int p in pipes)
            {
                if (!incoming.TryGetValue(down[p], out var l)) incoming[down[p]] = l = new List<int>();
                l.Add(p);
                hasOut.Add(up[p]);
            }

            // Longest upstream length from each node (iterative DFS, cycle-safe).
            var longest = new Dictionary<string, double>();
            double Longest(string node)
            {
                if (longest.TryGetValue(node, out var v)) return v;
                var stack = new Stack<(string N, bool Expanded)>();
                var onStack = new HashSet<string>();
                stack.Push((node, false));
                while (stack.Count > 0)
                {
                    var (n, expanded) = stack.Pop();
                    if (longest.ContainsKey(n)) continue;
                    if (!expanded)
                    {
                        stack.Push((n, true)); onStack.Add(n);
                        if (incoming.TryGetValue(n, out var ins))
                            foreach (int p in ins)
                                if (!longest.ContainsKey(up[p]) && !onStack.Contains(up[p])) stack.Push((up[p], false));
                    }
                    else
                    {
                        double best = 0;
                        if (incoming.TryGetValue(n, out var ins))
                            foreach (int p in ins)
                                best = Math.Max(best, rows[p].Length2D + (longest.TryGetValue(up[p], out var u) ? u : 0));
                        longest[n] = best; onStack.Remove(n);
                    }
                }
                return longest[node];
            }

            var assigned = new HashSet<int>();
            int kolIndex = 0;
            var queue = new Queue<string>();

            void Trace(string start)
            {
                var path = new List<int>();   // downstream → upstream
                string cur = start;
                while (true)
                {
                    if (!incoming.TryGetValue(cur, out var ins)) break;
                    int pick = -1; double bestLen = -1;
                    foreach (int p in ins.OrderBy(q => nodeName[up[q]], StringComparer.Ordinal))
                    {
                        if (assigned.Contains(p)) continue;
                        double len = rows[p].Length2D + Longest(up[p]);
                        if (len > bestLen + 1e-9) { bestLen = len; pick = p; }
                    }
                    if (pick < 0) break;
                    path.Add(pick); assigned.Add(pick);
                    cur = up[pick];
                }
                if (path.Count == 0) return;

                string kolName = nodeName[cur];      // first (most upstream) manhole
                double off = 0;
                for (int k = path.Count - 1; k >= 0; k--)
                {
                    int p = path[k];
                    result[p] = new KolAssignment
                    {
                        KolName = kolName, OffsetAtUpstream = off, StartIsUpstream = startUp[p], KolIndex = kolIndex
                    };
                    off += rows[p].Length2D;
                }
                kolIndex++;
                // Branches join this kol at its downstream start node and at every node along it.
                queue.Enqueue(start);
                foreach (int p in path) queue.Enqueue(up[p]);
            }

            var outlets = incoming.Keys.Where(n => !hasOut.Contains(n))
                .OrderByDescending(Longest).ThenBy(n => nodeName[n], StringComparer.Ordinal).ToList();
            foreach (var o in outlets)
            {
                Trace(o);
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    while (incoming.TryGetValue(n, out var ins) && ins.Any(p => !assigned.Contains(p))) Trace(n);
                }
            }
            // Pipes not reachable from an outlet (loops / bad data): start from their own downstream node.
            foreach (int p in pipes.Where(q => !assigned.Contains(q)).ToList())
            {
                if (assigned.Contains(p)) continue;
                Trace(down[p]);
                while (queue.Count > 0)
                {
                    var n = queue.Dequeue();
                    while (incoming.TryGetValue(n, out var ins) && ins.Any(q => !assigned.Contains(q))) Trace(n);
                }
            }
        }
    }
}
