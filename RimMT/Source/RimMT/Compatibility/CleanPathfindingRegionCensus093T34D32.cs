using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class CleanPathfindingRegionCensus093T34D32
    {
        private const int SampleMask = 63;
        private static readonly MethodStats Best = new MethodStats("best");
        private static readonly MethodStats Precise = new MethodStats("precise");
        private static readonly MethodStats Distance = new MethodStats("distance");
        private static bool installed;
        private static int installFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null || AccessTools.TypeByName("CleanPathfinding.CleanPathfindingUtility") == null) return;
            try
            {
                Patch(harmony, "GetRegionBestDistances", nameof(BestPrefix), nameof(BestPostfix));
                Patch(harmony, "GetPreciseRegionLinkDistances", nameof(PrecisePrefix), nameof(PrecisePostfix));
                Patch(harmony, "GetRegionDistance", nameof(DistancePrefix), nameof(DistancePostfix));
                installed = true;
                Log.Message("[RimMT] T34-D.3.2 Clean Pathfinding region cost census installed (1/64 timing sample).");
            }
            catch (Exception ex)
            {
                installFailures++;
                Log.Warning("[RimMT] T34-D.3.2 region cost census failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Patch(Harmony harmony, string targetName, string prefixName, string postfixName)
        {
            MethodInfo target = AccessTools.Method(typeof(RegionCostCalculator), targetName);
            if (target == null) throw new MissingMethodException(typeof(RegionCostCalculator).FullName, targetName);
            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(CleanPathfindingRegionCensus093T34D32), prefixName) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(CleanPathfindingRegionCensus093T34D32), postfixName) { priority = Priority.Last });
        }

        public static void BestPrefix(ref long __state) { Begin(Best, ref __state); }
        public static void BestPostfix(long __state) { End(Best, __state); }
        public static void PrecisePrefix(ref long __state) { Begin(Precise, ref __state); }
        public static void PrecisePostfix(long __state) { End(Precise, __state); }
        public static void DistancePrefix(ref long __state) { Begin(Distance, ref __state); }
        public static void DistancePostfix(long __state) { End(Distance, __state); }

        private static void Begin(MethodStats stats, ref long state)
        {
            state = 0L;
            if (!RimMTThreadGuard.IsMainThread) return;
            long call = Interlocked.Increment(ref stats.Calls);
            if ((call & SampleMask) == 0) state = Stopwatch.GetTimestamp();
        }

        private static void End(MethodStats stats, long state)
        {
            if (state == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - state;
            Interlocked.Increment(ref stats.Samples);
            Interlocked.Add(ref stats.TotalTicks, elapsed);
            long current = Interlocked.Read(ref stats.MaxTicks);
            while (elapsed > current)
            {
                long observed = Interlocked.CompareExchange(ref stats.MaxTicks, elapsed, current);
                if (observed == current) break;
                current = observed;
            }
        }

        internal static string Summary()
        {
            return "T34-D.3.2 Clean Pathfinding region census: installed=" + installed +
                ", " + Format(Best) + ", " + Format(Precise) + ", " + Format(Distance) +
                ", installFailures=" + installFailures + ". Timing sample=1/64; behavior unchanged.";
        }

        private static string Format(MethodStats stats)
        {
            long samples = Interlocked.Read(ref stats.Samples);
            long total = Interlocked.Read(ref stats.TotalTicks);
            double avgUs = samples == 0 ? 0.0 : total * 1000000.0 / Stopwatch.Frequency / samples;
            double maxMs = Interlocked.Read(ref stats.MaxTicks) * 1000.0 / Stopwatch.Frequency;
            return stats.Name + "[calls/samples/avgUs/maxMs]=" + Interlocked.Read(ref stats.Calls) + "/" + samples +
                "/" + avgUs.ToString("F2") + "/" + maxMs.ToString("F3");
        }

        private sealed class MethodStats
        {
            internal readonly string Name;
            internal long Calls;
            internal long Samples;
            internal long TotalTicks;
            internal long MaxTicks;
            internal MethodStats(string name) { Name = name; }
        }
    }
}
