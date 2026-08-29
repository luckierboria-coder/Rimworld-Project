using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;

namespace RimMT
{
    internal static class JobGiverInfrastructureProfiler
    {
        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);
        private static long totalSamples;

        internal static void Reset()
        {
            Stats.Clear();
            totalSamples = 0L;
        }

        internal static long Begin(MethodBase method, object[] args)
        {
            if (!WorkGiverProfiler.DetailCaptureActive || !RimMTThreadGuard.IsMainThread)
                return 0L;

            JD2TailTrace.RecordInvocation(method, args);
            return Stopwatch.GetTimestamp();
        }

        internal static void Record(MethodBase method, long started)
        {
            if (started == 0L || method == null || !RimMTThreadGuard.IsMainThread)
                return;

            long elapsed = Stopwatch.GetTimestamp() - started;
            string phase = Classify(method);
            Stat stat;
            if (!Stats.TryGetValue(phase, out stat))
            {
                stat = new Stat();
                Stats.Add(phase, stat);
            }

            stat.Count++;
            stat.TotalTicks += elapsed;
            if (elapsed > stat.MaxTicks)
                stat.MaxTicks = elapsed;
            totalSamples++;
            WorkGiverProfiler.RecordInclusivePhase(phase, elapsed);
            JD2TailTrace.RecordInfrastructure(phase, elapsed);
        }

        internal static string Summary(int topN)
        {
            List<Entry> entries = new List<Entry>(Stats.Count);
            foreach (KeyValuePair<string, Stat> pair in Stats)
                entries.Add(new Entry(pair.Key, pair.Value));
            entries.Sort(delegate(Entry a, Entry b)
            {
                int byTotal = b.Stat.TotalTicks.CompareTo(a.Stat.TotalTicks);
                if (byTotal != 0) return byTotal;
                return b.Stat.MaxTicks.CompareTo(a.Stat.MaxTicks);
            });

            if (topN < 1) topN = 1;
            if (topN > entries.Count) topN = entries.Count;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("JobGiver infrastructure JD2: samples=").Append(totalSamples)
                .Append(", tracked=").Append(entries.Count)
                .Append(" (inclusive timings; nested phases can overlap; invocation shape is attached to SLOW traces)");

            for (int i = 0; i < topN; i++)
            {
                Entry entry = entries[i];
                double totalMs = entry.Stat.TotalTicks * 1000.0 / Stopwatch.Frequency;
                double avgMs = entry.Stat.Count == 0 ? 0.0 : totalMs / entry.Stat.Count;
                double maxMs = entry.Stat.MaxTicks * 1000.0 / Stopwatch.Frequency;
                sb.Append("\n  #").Append(i + 1).Append(' ').Append(entry.Name)
                    .Append(": calls=").Append(entry.Stat.Count)
                    .Append(", sampledTotalMs=").Append(totalMs.ToString("F1"))
                    .Append(", avgMs=").Append(avgMs.ToString("F3"))
                    .Append(", maxMs=").Append(maxMs.ToString("F3"));
            }
            return sb.ToString();
        }

        private static string Classify(MethodBase method)
        {
            Type type = method.DeclaringType;
            string typeName = type == null ? "<unknown>" : type.FullName;
            if (typeName == "Verse.GenClosest")
                return "GenClosest." + method.Name;
            if (typeName == "Verse.Reachability")
                return "Reachability." + method.Name;
            if (typeName == "Verse.RegionTraverser")
                return "RegionTraverser." + method.Name;
            if (typeName == "RimWorld.WorkGiver_Scanner")
                return "WorkGiver_Scanner." + method.Name;
            return typeName + "." + method.Name;
        }

        private sealed class Stat
        {
            internal long Count;
            internal long TotalTicks;
            internal long MaxTicks;
        }

        private struct Entry
        {
            internal readonly string Name;
            internal readonly Stat Stat;
            internal Entry(string name, Stat stat) { Name = name; Stat = stat; }
        }
    }
}
