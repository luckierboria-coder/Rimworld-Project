using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Allen.CommonSensePerformance
{
    /// <summary>
    /// Queue ordering hot path used by both opportunistic cleaning and
    /// ingredient hauling. Preserves the source greedy nearest-neighbour order.
    /// This intentionally DOES NOT cache pawn cleaning speed or terrain factors
    /// across ticks: other mods and stat changes can invalidate those values.
    /// </summary>
    internal static class M4RouteOrdering
    {
        private static readonly Harmony Harmony = new Harmony("allen.commonsense.performance16.m4");
        private static bool localFaulted, countFaulted;

        internal static void TryInstall()
        {
            if (!CommonSensePerformanceMod.Options.RouteOrdering) return;
            try
            {
                Type utility = AccessTools.TypeByName("CommonSense.Utility");
                if (utility == null) return;

                MethodInfo local = AccessTools.Method(utility, "OptimizePath",
                    new[] { typeof(List<LocalTargetInfo>), typeof(Thing) });
                MethodInfo counts = AccessTools.Method(utility, "OptimizePath",
                    new[] { typeof(List<ThingCount>), typeof(Thing) });
                Install(local, AccessTools.Method(typeof(M4RouteOrdering), nameof(LocalPrefix)), "M4-CleanRoute");
                Install(counts, AccessTools.Method(typeof(M4RouteOrdering), nameof(CountPrefix)), "M4-MaterialRoute");
            }
            catch (Exception ex) { Log.Warning("[CS Perf M4] Install failure; upstream methods retained. " + ex); }
        }

        private static void Install(MethodInfo target, MethodInfo prefix, string label)
        {
            if (target == null || target.ReturnType != typeof(void) || prefix == null)
            {
                Log.Warning("[CS Perf "+label+"] Method signature mismatch; skipped."); return;
            }
            Patches p = HarmonyLib.Harmony.GetPatchInfo(target);
            if (p != null && (p.Prefixes.Count > 0 || p.Postfixes.Count > 0
                || p.Transpilers.Count > 0 || p.Finalizers.Count > 0))
            {
                Log.Warning("[CS Perf "+label+"] Existing Harmony patch; skipped."); return;
            }
            Harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.Message("[CS Perf "+label+"] Active.");
        }

        private static bool LocalPrefix(List<LocalTargetInfo> q, Thing Starter)
        {
            if (localFaulted) return true;
            try
            {
                int count = q.Count;
                if (count <= 1) return false;
                if (Starter != null)
                {
                    int dist = q[0].Cell.DistanceToSquared(Starter.Position), best = 0;
                    IntVec3 start = Starter.Position;
                    for (int i = 1; i < count; ++i)
                    {
                        int next = q[i].Cell.DistanceToSquared(start);
                        if (Math.Abs(next) < Math.Abs(dist)) { dist = next; best = i; }
                    }
                    if (best != 0) { LocalTargetInfo temp = q[best]; q[best] = q[0]; q[0] = temp; }
                }
                for (int i = 0; i < count - 1; ++i)
                {
                    IntVec3 start = q[i].Cell;
                    int dist = start.DistanceToSquared(q[i + 1].Cell), best = i + 1;
                    for (int j = i + 2; j < count; ++j)
                    {
                        int next = start.DistanceToSquared(q[j].Cell);
                        if (Math.Abs(next) < Math.Abs(dist)) { dist = next; best = j; }
                    }
                    if (best != i + 1)
                    { LocalTargetInfo temp = q[best]; q[best] = q[i + 1]; q[i + 1] = temp; }
                }
                return false;
            }
            catch (Exception ex)
            {
                localFaulted = true;
                Log.Warning("[CS Perf M4] Cleaning route fallback for session. " + ex);
                return true;
            }
        }

        private static bool CountPrefix(List<ThingCount> q, Thing Starter)
        {
            if (countFaulted) return true;
            try
            {
                int count = q.Count;
                if (count <= 1) return false;
                if (Starter != null)
                {
                    int dist = q[0].Thing.Position.DistanceToSquared(Starter.Position), best = 0;
                    IntVec3 start = Starter.Position;
                    for (int i = 1; i < count; ++i)
                    {
                        int next = q[i].Thing.Position.DistanceToSquared(start);
                        if (Math.Abs(next) < Math.Abs(dist)) { dist = next; best = i; }
                    }
                    if (best != 0) { ThingCount temp = q[best]; q[best] = q[0]; q[0] = temp; }
                }
                for (int i = 0; i < count - 1; ++i)
                {
                    IntVec3 start = q[i].Thing.Position;
                    int dist = start.DistanceToSquared(q[i + 1].Thing.Position), best = i + 1;
                    for (int j = i + 2; j < count; ++j)
                    {
                        int next = start.DistanceToSquared(q[j].Thing.Position);
                        if (Math.Abs(next) < Math.Abs(dist)) { dist = next; best = j; }
                    }
                    if (best != i + 1)
                    { ThingCount temp = q[best]; q[best] = q[i + 1]; q[i + 1] = temp; }
                }
                return false;
            }
            catch (Exception ex)
            {
                countFaulted = true;
                Log.Warning("[CS Perf M4] Ingredient route fallback for session. " + ex);
                return true;
            }
        }
    }
}