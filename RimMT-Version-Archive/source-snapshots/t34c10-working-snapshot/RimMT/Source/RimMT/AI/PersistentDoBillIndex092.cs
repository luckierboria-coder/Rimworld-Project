using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// RC2-T2 persistent DoBill index, redesigned for Unified Lean.
    /// Stable membership is cached; inactive bill givers are removed by a live
    /// BillStack.AnyShouldDoNow readiness gate before expensive JobOnThing/ingredient search.
    /// Spawn/despawn changes are applied incrementally to already-built WorkGiver lists; a full
    /// cache clear is used only as a fail-closed recovery if an incremental mutation throws.
    /// Final usability, reservation, reachability and JobOnThing authority remain live.
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
        private static long mutationEvents;
        private static long incrementalAdds;
        private static long incrementalRemoves;
        private static long fallbackClears;
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

                Log.Message("[RimMT] Unified persistent DoBill index active: source=" + sourcePatched + ", shouldSkip=" + shouldSkipPatched + ". Stable membership is incrementally maintained; inactive bill givers are removed by live AnyShouldDoNow before expensive JobOnThing.");
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

            sourceLookups++;
            try
            {
                BillMapCache cache = Caches.GetValue(pawn.Map, delegate(Map m) { return new BillMapCache(); });
                List<Thing> things = cache.Get(giver, pawn.Map);
                if (things == null) return;

                sourceIndexHits++;
                int scanned = things.Count;
                int localActive = 0;
                int localInactive = 0;

                // With the observed workload ~95% of represented benches are inactive, so reserve
                // only a small active list up front instead of allocating capacity for the whole
                // membership set. List<T> still grows normally if a rare call has more active benches.
                List<Thing> active = null;
                for (int i = 0; i < things.Count; i++)
                {
                    Thing thing = things[i];
                    IBillGiver billGiver = thing as IBillGiver;
                    BillStack stack = billGiver == null ? null : billGiver.BillStack;

                    bool keep = stack == null || stack.AnyShouldDoNow;
                    if (keep)
                    {
                        localActive++;
                        if (active != null) active.Add(thing);
                        continue;
                    }

                    localInactive++;
                    if (active == null)
                    {
                        active = new List<Thing>(Math.Min(things.Count, 32));
                        for (int j = 0; j < i; j++) active.Add(things[j]);
                    }
                }

                readinessScans += scanned;
                activeReturned += localActive;
                inactiveFiltered += localInactive;
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
            shouldSkipCalls++;
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
                        shouldSkipContinue++;
                        return false;
                    }
                }

                __result = true;
                shouldSkipNoWork++;
                return false;
            }
            catch { return true; }
        }

        public static void ThingSpawnedPostfix(Thing __instance, Map map)
        {
            if (!(__instance is IBillGiver) || map == null) return;
            BillMapCache cache;
            if (!Caches.TryGetValue(map, out cache) || cache == null) return;

            mutationEvents++;
            try
            {
                incrementalAdds += cache.AddSpawned(__instance, map);
            }
            catch (Exception ex)
            {
                cache.Invalidate();
                fallbackClears++;
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] DoBill incremental spawn update failed closed to cache clear: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void ThingDeSpawnPrefix(Thing __instance)
        {
            if (!(__instance is IBillGiver)) return;
            Map map = null;
            try { map = __instance.Map; } catch { }
            if (map == null) return;

            BillMapCache cache;
            if (!Caches.TryGetValue(map, out cache) || cache == null) return;

            mutationEvents++;
            try
            {
                incrementalRemoves += cache.Remove(__instance);
            }
            catch (Exception ex)
            {
                cache.Invalidate();
                fallbackClears++;
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] DoBill incremental despawn update failed closed to cache clear: " + ex.GetType().Name + ": " + ex.Message);
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
            long scans = readinessScans;
            long filtered = inactiveFiltered;
            double filterRate = scans <= 0 ? 0.0 : filtered * 100.0 / scans;
            return "DoBill persistent index/readiness: sourceLookups=" + sourceLookups +
                ", sourceIndexHits=" + sourceIndexHits +
                ", readinessScans=" + scans +
                ", inactiveFiltered=" + filtered +
                ", filterRate=" + filterRate.ToString("F2") + "%" +
                ", activeReturned=" + activeReturned +
                ", rebuilds=" + indexRebuilds +
                ", mutationEvents=" + mutationEvents +
                ", incrementalAdds=" + incrementalAdds +
                ", incrementalRemoves=" + incrementalRemoves +
                ", fallbackClears=" + fallbackClears +
                ", shouldSkipCalls=" + shouldSkipCalls +
                ", shouldSkipNoWork=" + shouldSkipNoWork +
                ", shouldSkipContinue=" + shouldSkipContinue + ".";
        }

        private sealed class BillMapCache
        {
            private readonly Dictionary<WorkGiverDef, CacheEntry> byDef = new Dictionary<WorkGiverDef, CacheEntry>();

            internal List<Thing> Get(WorkGiver_DoBill giver, Map map)
            {
                CacheEntry entry;
                if (byDef.TryGetValue(giver.def, out entry) && entry != null && entry.Things != null)
                    return entry.Things;

                ThingRequest request = giver.PotentialWorkThingRequest;
                List<Thing> source = map.listerThings.ThingsMatching(request);
                List<Thing> cached = new List<Thing>(source == null ? 0 : Math.Min(source.Count, 64));
                if (source != null)
                {
                    for (int i = 0; i < source.Count; i++)
                    {
                        Thing thing = source[i];
                        if (thing == null || !thing.Spawned || thing.Map != map || !(thing is IBillGiver)) continue;
                        cached.Add(thing);
                    }
                }
                byDef[giver.def] = new CacheEntry(request, cached);
                indexRebuilds++;
                return cached;
            }

            internal int AddSpawned(Thing thing, Map map)
            {
                if (thing == null || map == null || !thing.Spawned || thing.Map != map || !(thing is IBillGiver)) return 0;
                int added = 0;
                foreach (KeyValuePair<WorkGiverDef, CacheEntry> pair in byDef)
                {
                    CacheEntry entry = pair.Value;
                    if (entry == null || entry.Things == null || !entry.Request.Accepts(thing)) continue;
                    if (entry.Things.Contains(thing)) continue;
                    entry.Things.Add(thing);
                    added++;
                }
                return added;
            }

            internal int Remove(Thing thing)
            {
                if (thing == null) return 0;
                int removed = 0;
                foreach (KeyValuePair<WorkGiverDef, CacheEntry> pair in byDef)
                {
                    List<Thing> list = pair.Value == null ? null : pair.Value.Things;
                    if (list == null) continue;
                    for (int i = list.Count - 1; i >= 0; i--)
                    {
                        if (!ReferenceEquals(list[i], thing)) continue;
                        list.RemoveAt(i);
                        removed++;
                    }
                }
                return removed;
            }

            internal void Invalidate() { byDef.Clear(); }
        }

        private sealed class CacheEntry
        {
            internal readonly ThingRequest Request;
            internal readonly List<Thing> Things;
            internal CacheEntry(ThingRequest request, List<Thing> things)
            {
                Request = request;
                Things = things;
            }
        }
    }
}
