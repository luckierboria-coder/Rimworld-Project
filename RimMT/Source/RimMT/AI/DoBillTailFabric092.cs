using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// Worker-backed tail lane for repeated DoBill bench searches below the general >=96
    /// persistent-fabric threshold. Once a JobGiver package has already spent >=16 ms, stable
    /// 16..127 IBillGiver sets may register their membership/primitive positions with the existing
    /// PersistentMapSearchFabric worker. The main thread never waits: first sighting falls through
    /// to S5.1/Vanilla, later synchronized snapshots can prove the closest candidate with a dynamic
    /// live-check budget. Validator, Reachability and final Thing result remain live main-thread work.
    /// </summary>
    internal static class DoBillTailFabric092
    {
        private const int MinCount = 16;
        private const int MaxCount = 127;
        private const int TailThresholdMs = 16;
        private static readonly long TailThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailThresholdMs / 1000L);
        private static readonly Dictionary<SourceKey, SourceState> States = new Dictionary<SourceKey, SourceState>();
        private static int nextSourceId = 400000;
        private static bool patched;
        private static int failureLogs;

        private static long observed;
        private static long tailEligible;
        private static long registrations;
        private static long registrationRejected;
        private static long snapshotHits;
        private static long snapshotMisses;
        private static long accelerated;
        private static long broadBypass;
        private static long liveFallback;
        private static long liveChecks;
        private static long candidatesVisited;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool)
                    });
                if (target == null) return;

                HarmonyMethod prefix = new HarmonyMethod(typeof(DoBillTailFabric092), nameof(Prefix));
                prefix.priority = Priority.First + 220;
                harmony.Patch(target, prefix: prefix);
                patched = true;
                Log.Message("[RimMT] DoBill worker-tail fabric active: repeated 16..127 static bill-giver sets can use worker-maintained spatial snapshots after a 16ms JobGiver tail threshold; final validation/reachability stays live.");
            }
            catch (Exception ex)
            {
                patched = false;
                Log.Warning("[RimMT] DoBill worker-tail fabric install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool Prefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMin,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions,
            ref Thing __result)
        {
            Interlocked.Increment(ref observed);
            if (!patched || !FeatureGate.IsEnabled("parallel.jobPartition") || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || customGlobalSearchSet == null)
                return true;

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L || Stopwatch.GetTimestamp() - scopeStart < TailThresholdTicks)
                return true;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) ||
                !thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
                return true;

            ICollection<Thing> collection = customGlobalSearchSet as ICollection<Thing>;
            if (collection == null || collection.Count < MinCount || collection.Count > MaxCount)
                return true;

            Interlocked.Increment(ref tailEligible);
            try
            {
                int count = collection.Count;
                Thing[] members = new Thing[count];
                int hash = 17;
                int i = 0;
                foreach (Thing thing in collection)
                {
                    if (i >= count || thing == null || !(thing is IBillGiver) || thing is Pawn || !thing.Spawned || thing.MapHeld != map)
                        return true;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map)) return true;
                    members[i++] = thing;
                    unchecked { hash = hash * 31 + thing.thingIDNumber; }
                }
                if (i != count) return true;

                SourceKey key = new SourceKey(map.uniqueID, count, hash);
                SourceState state;
                if (!States.TryGetValue(key, out state) || !MembershipMatches(state.Members, members))
                {
                    state = new SourceState { SourceId = Interlocked.Increment(ref nextSourceId), Members = members };
                    States[key] = state;
                    if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
                    {
                        Interlocked.Increment(ref registrationRejected);
                        return true;
                    }
                    Interlocked.Increment(ref registrations);
                    return true; // never wait for worker publication
                }

                PersistentMapSearchFabric.SourceSnapshot snapshot;
                if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, state.SourceId, out snapshot) || snapshot == null || snapshot.Count != count)
                {
                    Interlocked.Increment(ref snapshotMisses);
                    return true;
                }
                Interlocked.Increment(ref snapshotHits);

                int cap = DynamicLiveCap();
                int estimate = snapshot.EstimateCandidates(root, maxDistance, cap);
                if (estimate > cap)
                {
                    Interlocked.Increment(ref broadBypass);
                    return true;
                }

                Thing chosen;
                int visited;
                int buckets;
                int reaches;
                int validations;
                bool stale;
                bool ok = snapshot.TryFindClosest(root, map, peMode, traverseParams, maxDistance, validator, cap,
                    out chosen, out visited, out buckets, out reaches, out validations, out stale);
                if (!ok)
                {
                    Interlocked.Increment(ref liveFallback);
                    return true;
                }

                __result = chosen;
                Interlocked.Increment(ref accelerated);
                Interlocked.Add(ref liveChecks, reaches + validations);
                Interlocked.Add(ref candidatesVisited, visited);
                return false;
            }
            catch (Exception ex)
            {
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] DoBill worker-tail fabric failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static int DynamicLiveCap()
        {
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: return 96;
                case LoadPressure.Normal: return 64;
                case LoadPressure.High: return 32;
                default: return 16;
            }
        }

        private static bool MembershipMatches(Thing[] a, Thing[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (!ReferenceEquals(a[i], b[i])) return false;
            return true;
        }

        internal static string Summary()
        {
            long hits = Interlocked.Read(ref snapshotHits);
            long accel = Interlocked.Read(ref accelerated);
            return "DoBill worker-tail fabric: patched=" + patched +
                ", observed=" + Interlocked.Read(ref observed) +
                ", tailEligible=" + Interlocked.Read(ref tailEligible) +
                ", registrations=" + Interlocked.Read(ref registrations) +
                ", registrationRejected=" + Interlocked.Read(ref registrationRejected) +
                ", snapshotHits=" + hits +
                ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) +
                ", accelerated=" + accel +
                ", broadBypass=" + Interlocked.Read(ref broadBypass) +
                ", liveFallback=" + Interlocked.Read(ref liveFallback) +
                ", liveChecks=" + Interlocked.Read(ref liveChecks) +
                ", candidatesVisited=" + Interlocked.Read(ref candidatesVisited) +
                ", currentLiveCap=" + DynamicLiveCap() + ".";
        }

        private sealed class SourceState
        {
            internal int SourceId;
            internal Thing[] Members;
        }

        private struct SourceKey : IEquatable<SourceKey>
        {
            private readonly int mapId;
            private readonly int count;
            private readonly int hash;
            internal SourceKey(int mapId, int count, int hash) { this.mapId = mapId; this.count = count; this.hash = hash; }
            public bool Equals(SourceKey other) { return mapId == other.mapId && count == other.count && hash == other.hash; }
            public override bool Equals(object obj) { return obj is SourceKey && Equals((SourceKey)obj); }
            public override int GetHashCode() { unchecked { return ((mapId * 397) ^ count) * 397 ^ hash; } }
        }
    }
}
