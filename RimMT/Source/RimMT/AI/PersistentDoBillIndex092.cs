using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// RC2-T2 persistent DoBill index, redesigned for Unified Lean.
    /// The persistent index caches only stable membership facts (spawned IBillGiver instances),
    /// not transient ThingIsUsableBillGiver results. V0.9.2 tail refinement also performs a cheap
    /// live BillStack.AnyShouldDoNow gate per bench so inactive bill givers never reach expensive
    /// JobOnThing/ingredient search. Final usability, reservation, reachability and JobOnThing
    /// authority remain live and Vanilla-owned.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class PersistentDoBillIndex092
    {
        private static readonly ConditionalWeakTable<Map, BillMapCache> Caches = new ConditionalWeakTable<Map, BillMapCache>();
        private static bool sourcePatched;
        private static bool shouldSkipPatched;
        private static int failureLogs;

        private static long sourceLookups;
        private static long sourceIndexHits;
        private static long readinessScans;
        private static long inactiveFiltered;
        private static long activeReturned;
        private static long indexRebuilds;
        private static long invalidations;
        private static long shouldSkipCalls;
        private static long shouldSkipNoWork;
        private static long shouldSkipContinue;

        static PersistentDoBillIndex092()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                MethodInfo source = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.PotentialWorkThingsGlobal), new Type[] { typeof(Pawn) });
                if (source != null)
                {
                    harmony.Patch(source, postfix: new HarmonyMethod(typeof(PersistentDoBillIndex092), nameof(PotentialWorkThingsGlobalPostfix)) { priority = Priority.Last });
                    sourcePatched = true;
                }

                MethodInfo shouldSkip = AccessTools.Method(typeof(WorkGiver_DoBill), nameof(WorkGiver_DoBill.ShouldSkip), new Type[] { typeof(Pawn), typeof(bool) });
                if (shouldSkip != null && !HasUnsafeForeignPatch(shouldSkip))
                {
                    harmony.Patch(shouldSkip, prefix: new HarmonyMethod(typeof(PersistentDoBillIndex092), nameof(ShouldSkipPrefix)) { priority = Priority.First });
                    shouldSkipPatched = true;
                }

                MethodInfo spawn = AccessTools.Method(typeof(Thing), nameof(Thing.SpawnSetup), new Type[] { typeof(Map), typeof(bool) });
                MethodInfo despawn = AccessTools.Method(typeof(Thing), nameof(Thing.DeSpawn), new Type[] { typeof(DestroyMode) });
                if (spawn != null)
                    harmony.Patch(spawn, postfix: new HarmonyMethod(typeof(PersistentDoBillIndex092), nameof(ThingSpawnedPostfix)) { priority = Priority.Last });
                if (despawn != null)
                    harmony.Patch(despawn, prefix: new HarmonyMethod(typeof(PersistentDoBillIndex092), nameof(ThingDeSpawnPrefix)) { priority = Priority.First });

                Log.Message("[RimMT] Unified persistent DoBill index active: source=" + sourcePatched + ", shouldSkip=" + shouldSkipPatched + ". Stable membership is cached; inactive bill givers are removed by a live AnyShouldDoNow readiness gate before expensive JobOnThing.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] persistent DoBill index install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PotentialWorkThingsGlobalPostfix(WorkGiver_Scanner __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!sourcePatched || __result != null || __instance == null || pawn == null || pawn.Map == null) return;
            WorkGiver_DoBill giver = __instance as WorkGiver_DoBill;
            if (giver == null || giver.def == null) return;

            Interlocked.Increment(ref sourceLookups);
            try
            {
                BillMapCache cache = Caches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(giver, pawn.Map);
                if (things == null)
                    return;

                Interlocked.Increment(ref sourceIndexHits);
                Interlocked.Add(ref readinessScans, things.Count);

                // Do not allocate when every represented bench is currently active. Only once an
                // inactive BillStack is observed do we materialize a filtered list and copy the
                // already-validated prefix. Every exclusion is based on live main-thread state.
                List<Thing> active = null;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    IBillGiver billGiver = thing as IBillGiver;
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;

                    bool keep = stack == null || stack.AnyShouldDoNow;
                    if (keep)
                    {
                        Interlocked.Increment(ref activeReturned);
                        if (active != null) active.Add(thing);
                        continue;
                    }

                    Interlocked.Increment(ref inactiveFiltered);
                    if (active == null)
                    {
                        active = new List<Thing>(things.Count);
                        for (int j = 0; j < i; j++) active.Add(things[j]);
                    }
                }

                __result = active == null ? (IEnumerable<Thing>)things : active;
            }
            catch (Exception ex)
            {
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] DoBill source/readiness gate failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool ShouldSkipPrefix(WorkGiver_DoBill __instance, Pawn pawn, ref bool __result)
        {
            if (!shouldSkipPatched || __instance == null || pawn == null || pawn.Map == null || __instance.def == null) return true;
            Interlocked.Increment(ref shouldSkipCalls);
            try
            {
                BillMapCache cache = Caches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(__instance, pawn.Map);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    IBillGiver billGiver = thing as IBillGiver;
                    if (billGiver == null || ReferenceEquals(thing, pawn) || billGiver.BillStack == null) continue;
                    if (billGiver.BillStack.AnyShouldDoNow)
                    {
                        __result = false;
                        Interlocked.Increment(ref shouldSkipContinue);
                        return false;
                    }
                }

                __result = true;
                Interlocked.Increment(ref shouldSkipNoWork);
                return false;
            }
            catch { return true; }
        }

        public static void ThingSpawnedPostfix(Thing __instance, Map map)
        {
            if (__instance is IBillGiver && map != null) Invalidate(map);
        }

        public static void ThingDeSpawnPrefix(Thing __instance)
        {
            if (!(__instance is IBillGiver)) return;
            Map map = null;
            try { map = __instance.Map; } catch { }
            if (map != null) Invalidate(map);
        }

        private static void Invalidate(Map map)
        {
            BillMapCache cache;
            if (map != null && Caches.TryGetValue(map, out cache) && cache != null)
            {
                cache.Invalidate();
                Interlocked.Increment(ref invalidations);
            }
        }

        private static bool HasUnsafeForeignPatch(MethodBase target)
        {
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        internal static string Summary()
        {
            long scans = Interlocked.Read(ref readinessScans);
            long filtered = Interlocked.Read(ref inactiveFiltered);
            double filterRate = scans <= 0 ? 0.0 : filtered * 100.0 / scans;
            return "DoBill persistent index/readiness: sourceLookups=" + Interlocked.Read(ref sourceLookups) +
                ", sourceIndexHits=" + Interlocked.Read(ref sourceIndexHits) +
                ", readinessScans=" + scans +
                ", inactiveFiltered=" + filtered +
                ", filterRate=" + filterRate.ToString("F2") + "%" +
                ", activeReturned=" + Interlocked.Read(ref activeReturned) +
                ", rebuilds=" + Interlocked.Read(ref indexRebuilds) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", shouldSkipCalls=" + Interlocked.Read(ref shouldSkipCalls) +
                ", shouldSkipNoWork=" + Interlocked.Read(ref shouldSkipNoWork) +
                ", shouldSkipContinue=" + Interlocked.Read(ref shouldSkipContinue) + ".";
        }

        private sealed class BillMapCache
        {
            private readonly Dictionary<WorkGiverDef, List<Thing>> byDef = new Dictionary<WorkGiverDef, List<Thing>>();

            internal List<Thing> Get(WorkGiver_DoBill giver, Map map)
            {
                List<Thing> cached;
                if (byDef.TryGetValue(giver.def, out cached) && cached != null) return cached;

                List<Thing> source = map.listerThings.ThingsMatching(giver.PotentialWorkThingRequest);
                cached = new List<Thing>(source == null ? 0 : Math.Min(source.Count, 64));
                if (source != null)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        Thing thing = source[i];
                        if (thing == null || !thing.Spawned || thing.Map != map || !(thing is IBillGiver)) continue;
                        cached.Add(thing);
                    }
                }
                byDef[giver.def] = cached;
                Interlocked.Increment(ref indexRebuilds);
                return cached;
            }

            internal void Invalidate() { byDef.Clear(); }
        }
    }
}
