using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    internal static class WorkScanProduction
    {
        private const int MinCandidates = 64;
        private const int MaxCandidates = 12000;
        private const int MaxCacheEntries = 256;

        [ThreadStatic]
        private static int jobGiverDepth;

        private static readonly object Sync = new object();
        private static readonly Dictionary<CacheKey, CacheEntry> Cache = new Dictionary<CacheKey, CacheEntry>();
        private static readonly Dictionary<int, long> MapGenerations = new Dictionary<int, long>();
        private static long globalGeneration;

        private static long observedGlobalScans;
        private static long eligibleScans;
        private static long cacheHits;
        private static long cacheMisses;
        private static long buildScheduled;
        private static long buildCompleted;
        private static long buildRejected;
        private static long staleEntries;
        private static long candidatesSnapshotted;
        private static long candidatesInspectedOnHit;
        private static long candidatesAvoidedOnHit;
        private static long resultNullHits;
        private static long resultThingHits;
        private static long rejectedOutsideJobGiver;
        private static long rejectedPriority;
        private static long rejectedHaulSources;
        private static long rejectedSearchSet;
        private static long rejectedCandidateCount;
        private static long rejectedDynamicThing;
        private static long rejectedMap;
        private static long workerFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            MethodBase closest = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global_NewTemp", new Type[]
            {
                typeof(IntVec3), typeof(IEnumerable), typeof(float), typeof(Predicate<Thing>), typeof(Func<Thing, float>), typeof(bool)
            });

            if (closest == null)
            {
                FeatureGate.Suppress("parallel.jobScan", "GenClosest.ClosestThing_Global_NewTemp was not found");
                Log.Warning("[RimMT] parallel.jobScan V0.4.6 disabled: GenClosest.ClosestThing_Global_NewTemp target not found.");
                return;
            }

            try
            {
                CompatibilityGuard.RegisterTarget("parallel.jobScan", closest);
                HarmonyMethod prefix = new HarmonyMethod(typeof(WorkScanProduction), nameof(ClosestGlobalPrefix));
                prefix.priority = Priority.First;
                harmony.Patch(closest, prefix: prefix);

                PatchListerInvalidation(harmony, "Add");
                PatchListerInvalidation(harmony, "Remove");
                MethodBase clear = AccessTools.Method(typeof(ListerThings), "Clear");
                if (clear != null)
                    harmony.Patch(clear, postfix: new HarmonyMethod(typeof(WorkScanProduction), nameof(ListerClearPostfix)));

                Type jobGiverWork = AccessTools.TypeByName("RimWorld.JobGiver_Work");
                MethodBase job = jobGiverWork == null ? null : AccessTools.Method(jobGiverWork, "TryIssueJobPackage");
                if (job != null)
                    harmony.Patch(job, finalizer: new HarmonyMethod(typeof(WorkScanProduction), nameof(JobGiverFinalizer)));

                Log.Message("[RimMT] parallel.jobScan V0.4.6 production fast-path installed for static non-prioritized global WorkGiver Thing scans. Cache hits skip Vanilla full-list distance scans; validators and job creation remain main-thread authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("parallel.jobScan", "production Work scan patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobScan V0.4.6 patch failed; Vanilla Work scanning remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchListerInvalidation(Harmony harmony, string methodName)
        {
            MethodBase target = AccessTools.Method(typeof(ListerThings), methodName, new Type[] { typeof(Thing) });
            if (target == null)
                return;
            HarmonyMethod postfix = new HarmonyMethod(typeof(WorkScanProduction), nameof(ListerMutationPostfix));
            harmony.Patch(target, postfix: postfix);
        }

        internal static void EnterJobGiver()
        {
            jobGiverDepth++;
        }

        public static Exception JobGiverFinalizer(Exception __exception)
        {
            if (jobGiverDepth > 0)
                jobGiverDepth--;
            return __exception;
        }

        public static void ListerMutationPostfix(ListerThings __instance, Thing t)
        {
            if (__instance == null || __instance.use != ListerThingsUse.Global)
                return;

            Map map = t == null ? null : t.MapHeld;
            if (map == null)
            {
                Interlocked.Increment(ref globalGeneration);
                return;
            }

            lock (Sync)
            {
                long value;
                MapGenerations.TryGetValue(map.uniqueID, out value);
                MapGenerations[map.uniqueID] = value + 1;
            }
        }

        public static void ListerClearPostfix(ListerThings __instance)
        {
            if (__instance != null && __instance.use == ListerThingsUse.Global)
                Interlocked.Increment(ref globalGeneration);
        }

        public static bool ClosestGlobalPrefix(
            IntVec3 center,
            IEnumerable searchSet,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool lookInHaulSources,
            ref Thing __result)
        {
            Interlocked.Increment(ref observedGlobalScans);

            if (!FeatureGate.IsEnabled("parallel.jobScan"))
                return true;
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || jobGiverDepth <= 0)
            {
                Interlocked.Increment(ref rejectedOutsideJobGiver);
                return true;
            }
            if (priorityGetter != null)
            {
                Interlocked.Increment(ref rejectedPriority);
                return true;
            }
            if (lookInHaulSources)
            {
                Interlocked.Increment(ref rejectedHaulSources);
                return true;
            }

            IList<Thing> list = searchSet as IList<Thing>;
            if (list == null)
            {
                Interlocked.Increment(ref rejectedSearchSet);
                return true;
            }
            if (list.Count < MinCandidates || list.Count > MaxCandidates)
            {
                Interlocked.Increment(ref rejectedCandidateCount);
                return true;
            }

            Map map;
            if (!TryResolveStaticMap(list, out map))
            {
                Interlocked.Increment(ref rejectedDynamicThing);
                return true;
            }
            if (map == null || map.Disposed)
            {
                Interlocked.Increment(ref rejectedMap);
                return true;
            }

            Interlocked.Increment(ref eligibleScans);
            long mapGeneration = ReadMapGeneration(map.uniqueID);
            long global = Interlocked.Read(ref globalGeneration);
            CacheKey key = new CacheKey(RuntimeHelpers.GetHashCode(list), map.uniqueID, center.x, center.z);

            CacheEntry entry;
            lock (Sync)
            {
                Cache.TryGetValue(key, out entry);
            }

            if (entry != null && entry.Ready && ReferenceEquals(entry.SearchSet.Target, list) &&
                entry.Count == list.Count && entry.MapGeneration == mapGeneration && entry.GlobalGeneration == global)
            {
                Interlocked.Increment(ref cacheHits);
                int inspected = 0;
                Candidate[] ordered = entry.Ordered;
                float maxDistanceSquared = maxDistance * maxDistance;
                for (int i = 0; i < ordered.Length; i++)
                {
                    Candidate candidate = ordered[i];
                    if (candidate.DistanceSquared > maxDistanceSquared)
                        break;

                    Thing thing = candidate.Thing;
                    if (thing == null || !thing.Spawned || thing.Map != map)
                    {
                        Interlocked.Increment(ref staleEntries);
                        ScheduleBuild(key, list, map, center, mapGeneration, global);
                        return true;
                    }

                    inspected++;
                    if (validator == null || validator(thing))
                    {
                        __result = thing;
                        Interlocked.Add(ref candidatesInspectedOnHit, inspected);
                        Interlocked.Add(ref candidatesAvoidedOnHit, Math.Max(0, list.Count - inspected));
                        Interlocked.Increment(ref resultThingHits);
                        return false;
                    }
                }

                __result = null;
                Interlocked.Add(ref candidatesInspectedOnHit, inspected);
                Interlocked.Add(ref candidatesAvoidedOnHit, Math.Max(0, list.Count - inspected));
                Interlocked.Increment(ref resultNullHits);
                return false;
            }

            Interlocked.Increment(ref cacheMisses);
            ScheduleBuild(key, list, map, center, mapGeneration, global);
            return true;
        }

        private static bool TryResolveStaticMap(IList<Thing> list, out Map map)
        {
            map = null;
            for (int i = 0; i < list.Count; i++)
            {
                Thing thing = list[i];
                if (thing == null)
                    continue;
                if (!(thing is Building) && !(thing is Plant) && !(thing is Filth))
                    return false;
                if (!thing.Spawned)
                    continue;
                Map thingMap = thing.Map;
                if (thingMap == null)
                    return false;
                if (map == null)
                    map = thingMap;
                else if (map != thingMap)
                    return false;
            }
            return map != null;
        }

        private static void ScheduleBuild(CacheKey key, IList<Thing> list, Map map, IntVec3 center, long mapGeneration, long global)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || !AdaptiveLoadBalancer.AllowBackground)
            {
                Interlocked.Increment(ref buildRejected);
                return;
            }

            CacheEntry pending;
            lock (Sync)
            {
                if (Cache.TryGetValue(key, out pending) && pending.Building && ReferenceEquals(pending.SearchSet.Target, list) &&
                    pending.Count == list.Count && pending.MapGeneration == mapGeneration && pending.GlobalGeneration == global)
                    return;
            }

            Candidate[] snapshot = new Candidate[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                Thing thing = list[i];
                if (thing == null || !thing.Spawned || thing.Map != map || (!(thing is Building) && !(thing is Plant) && !(thing is Filth)))
                {
                    Interlocked.Increment(ref buildRejected);
                    return;
                }
                IntVec3 pos = thing.Position;
                int dx = center.x - pos.x;
                int dz = center.z - pos.z;
                snapshot[i] = new Candidate(thing, (float)(dx * dx + dz * dz), i);
            }

            CacheEntry entry = new CacheEntry(new WeakReference(list), list.Count, map.uniqueID, mapGeneration, global, snapshot);
            lock (Sync)
            {
                if (Cache.Count >= MaxCacheEntries)
                    Cache.Clear();
                Cache[key] = entry;
            }

            Interlocked.Add(ref candidatesSnapshotted, snapshot.Length);
            bool accepted = scheduler.TryEnqueue("parallel.jobScan", JobPriority.Normal, delegate
            {
                try
                {
                    Array.Sort(snapshot, CandidateComparer.Instance);
                    entry.Ordered = snapshot;
                    entry.Ready = true;
                    entry.Building = false;
                    Interlocked.Increment(ref buildCompleted);
                }
                catch (Exception ex)
                {
                    entry.Building = false;
                    Interlocked.Increment(ref workerFailures);
                    CircuitBreaker.RecordFailure("parallel.jobScan", ex);
                }
            });

            if (accepted)
                Interlocked.Increment(ref buildScheduled);
            else
            {
                lock (Sync)
                {
                    CacheEntry current;
                    if (Cache.TryGetValue(key, out current) && ReferenceEquals(current, entry))
                        Cache.Remove(key);
                }
                Interlocked.Increment(ref buildRejected);
            }
        }

        private static long ReadMapGeneration(int mapId)
        {
            lock (Sync)
            {
                long value;
                return MapGenerations.TryGetValue(mapId, out value) ? value : 0L;
            }
        }

        internal static string Summary()
        {
            return "Work scan production V0.4.6: observedGlobal=" + Interlocked.Read(ref observedGlobalScans) +
                ", eligible=" + Interlocked.Read(ref eligibleScans) +
                ", cacheHits=" + Interlocked.Read(ref cacheHits) +
                ", cacheMisses=" + Interlocked.Read(ref cacheMisses) +
                ", builds=" + Interlocked.Read(ref buildCompleted) + "/" + Interlocked.Read(ref buildScheduled) +
                ", buildRejected=" + Interlocked.Read(ref buildRejected) +
                ", stale=" + Interlocked.Read(ref staleEntries) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                "\nWork scan main-thread reduction: snapshotted=" + Interlocked.Read(ref candidatesSnapshotted) +
                ", inspectedOnHit=" + Interlocked.Read(ref candidatesInspectedOnHit) +
                ", candidatesAvoidedOnHit=" + Interlocked.Read(ref candidatesAvoidedOnHit) +
                ", thingHits=" + Interlocked.Read(ref resultThingHits) +
                ", nullHits=" + Interlocked.Read(ref resultNullHits) +
                "\nWork scan rejects: outsideJobGiver=" + Interlocked.Read(ref rejectedOutsideJobGiver) +
                ", prioritized=" + Interlocked.Read(ref rejectedPriority) +
                ", haulSources=" + Interlocked.Read(ref rejectedHaulSources) +
                ", searchSet=" + Interlocked.Read(ref rejectedSearchSet) +
                ", candidateCount=" + Interlocked.Read(ref rejectedCandidateCount) +
                ", dynamicThing=" + Interlocked.Read(ref rejectedDynamicThing) +
                ", invalidMap=" + Interlocked.Read(ref rejectedMap);
        }

        private struct CacheKey : IEquatable<CacheKey>
        {
            private readonly int listId;
            private readonly int mapId;
            private readonly int x;
            private readonly int z;

            internal CacheKey(int listId, int mapId, int x, int z)
            {
                this.listId = listId;
                this.mapId = mapId;
                this.x = x;
                this.z = z;
            }

            public bool Equals(CacheKey other)
            {
                return listId == other.listId && mapId == other.mapId && x == other.x && z == other.z;
            }

            public override bool Equals(object obj)
            {
                return obj is CacheKey && Equals((CacheKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = listId;
                    hash = hash * 397 ^ mapId;
                    hash = hash * 397 ^ x;
                    hash = hash * 397 ^ z;
                    return hash;
                }
            }
        }

        private sealed class CacheEntry
        {
            internal readonly WeakReference SearchSet;
            internal readonly int Count;
            internal readonly int MapId;
            internal readonly long MapGeneration;
            internal readonly long GlobalGeneration;
            internal volatile bool Building;
            internal volatile bool Ready;
            internal Candidate[] Ordered;

            internal CacheEntry(WeakReference searchSet, int count, int mapId, long mapGeneration, long globalGeneration, Candidate[] ordered)
            {
                SearchSet = searchSet;
                Count = count;
                MapId = mapId;
                MapGeneration = mapGeneration;
                GlobalGeneration = globalGeneration;
                Ordered = ordered;
                Building = true;
            }
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly float DistanceSquared;
            internal readonly int OriginalIndex;

            internal Candidate(Thing thing, float distanceSquared, int originalIndex)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                OriginalIndex = originalIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();

            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                if (distance != 0)
                    return distance;
                return a.OriginalIndex.CompareTo(b.OriginalIndex);
            }
        }
    }
}
