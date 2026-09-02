using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.14: persistent-map-fabric consumer.
    //
    // Source membership/order is retained on the main thread while primitive positions are
    // maintained by PersistentMapSearchFabric workers. Broad predicted queries bypass RimMT.
    // Unified Lean also cold-sleeps this consumer after a long zero-yield interval: the Harmony
    // prefix stays installed, but only one call in 256 runs the full admission path until a valid
    // static source or real acceleration appears. This does not disable parallel.jobPartition for
    // other consumers and Vanilla remains authoritative on every cold bypass.
    internal static class AdaptiveGenClosestAssist
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int MaxLiveChecks = 64;
        private const long ColdAfterObservedWithoutUseful = 50000;
        private const int ColdProbeMask = 255; // 1/256 calls while cold.

        private static readonly ConditionalWeakTable<object, SourceState> States =
            new ConditionalWeakTable<object, SourceState>();

        private static volatile bool compatibilityReady;
        private static int coldModeValue;
        private static int nextSourceId;

        [ThreadStatic]
        private static int assistDepth;

        private static long observedCalls;
        private static long eligibleCalls;
        private static long acceleratedCalls;
        private static long acceleratedNoResult;
        private static long fallbackCalls;
        private static long nonListBypasses;
        private static long shapeBypasses;
        private static long smallSetBypasses;
        private static long haulableBypasses;
        private static long mobileSourceBypasses;
        private static long unspawnedSourceBypasses;
        private static long membershipHits;
        private static long membershipRefreshes;
        private static long membershipRefreshRejected;
        private static long fabricMisses;
        private static long broadQueryBypasses;
        private static long liveCapFallbacks;
        private static long staleFallbacks;
        private static long estimatedCandidatesTotal;
        private static long queryTicks;
        private static long queryTicksMax;
        private static long bucketVisits;
        private static long candidatesVisited;
        private static long candidatesAvoided;
        private static long reachabilityChecks;
        private static long validatorChecks;
        private static long failures;
        private static long lastUsefulObserved;
        private static long coldBypasses;
        private static long coldProbes;
        private static long coldEnters;
        private static long coldExits;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            PersistentMapSearchFabric.Apply(harmony);

            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(GenClosest),
                    nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(IEnumerable<Thing>), typeof(int), typeof(int),
                        typeof(bool), typeof(RegionType), typeof(bool)
                    });

                if (target == null)
                {
                    FeatureGate.Suppress(FeatureId, "GenClosest.ClosestThingReachable target not found");
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.14 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AdaptiveGenClosestAssist), nameof(Prefix));
                prefix.priority = Priority.First + 100;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] parallel.jobPartition V0.4.14 persistent-fabric consumer installed with zero-yield cold sleep. After 50k calls without a useful static source/acceleration it probes 1/256 calls until useful work reappears.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "persistent-fabric GenClosest patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.14 patch failed; Vanilla GenClosest remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
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
            long observedNow = Interlocked.Increment(ref observedCalls);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (ShouldColdBypass(observedNow))
                return true;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            if (!thingReq.IsUndefined ||
                traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref shapeBypasses);
                return true;
            }

            object source = customGlobalSearchSet;
            SourceKind kind;
            int count;
            if (!TryGetSourceShape(source, out kind, out count))
            {
                Interlocked.Increment(ref nonListBypasses);
                return true;
            }

            if (count < MinCandidateCount)
            {
                Interlocked.Increment(ref smallSetBypasses);
                return true;
            }

            IList<Thing> thingList = source as IList<Thing>;
            if (thingList != null)
            {
                try
                {
                    List<Thing> haulables = map.listerHaulables == null
                        ? null
                        : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                    if (haulables != null && ReferenceEquals(source, haulables))
                    {
                        Interlocked.Increment(ref haulableBypasses);
                        return true;
                    }
                }
                catch
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }
            }

            if (assistDepth != 0)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref eligibleCalls);
            SourceState state = States.GetValue(source, CreateState);
            if (state.MapId != map.uniqueID)
            {
                state.MapId = map.uniqueID;
                state.Members = null;
            }

            if (!MembershipMatches(source, kind, count, state.Members))
            {
                Thing[] members;
                CaptureFailure failure;
                if (!TryCaptureMembers(source, kind, count, map, out members, out failure))
                {
                    if (failure == CaptureFailure.Mobile)
                        Interlocked.Increment(ref mobileSourceBypasses);
                    else if (failure == CaptureFailure.Unspawned)
                        Interlocked.Increment(ref unspawnedSourceBypasses);
                    else
                        Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                state.Members = members;
                Interlocked.Increment(ref membershipRefreshes);
                if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
                {
                    Interlocked.Increment(ref membershipRefreshRejected);
                    return true;
                }

                MarkUseful(observedNow);
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref membershipHits);
            MarkUseful(observedNow);

            PersistentMapSearchFabric.SourceSnapshot snapshot;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, state.SourceId, out snapshot) ||
                snapshot == null || snapshot.Count != count)
            {
                Interlocked.Increment(ref fabricMisses);
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            int estimate = snapshot.EstimateCandidates(root, maxDistance, MaxLiveChecks);
            Interlocked.Add(ref estimatedCandidatesTotal, estimate);
            if (estimate > MaxLiveChecks)
            {
                Interlocked.Increment(ref broadQueryBypasses);
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            assistDepth = 1;
            try
            {
                long queryStart = Stopwatch.GetTimestamp();
                Thing chosen;
                int visited;
                int bucketsSeen;
                int reaches;
                int validations;
                bool staleDetected;
                bool ok = snapshot.TryFindClosest(
                    root, map, peMode, traverseParams, maxDistance, validator, MaxLiveChecks,
                    out chosen, out visited, out bucketsSeen, out reaches, out validations, out staleDetected);
                long elapsed = Stopwatch.GetTimestamp() - queryStart;
                Interlocked.Add(ref queryTicks, elapsed);
                UpdateMax(ref queryTicksMax, elapsed);

                if (!ok)
                {
                    if (staleDetected)
                        Interlocked.Increment(ref staleFallbacks);
                    else
                        Interlocked.Increment(ref liveCapFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                __result = chosen;
                Interlocked.Increment(ref acceleratedCalls);
                if (chosen == null)
                    Interlocked.Increment(ref acceleratedNoResult);
                Interlocked.Add(ref bucketVisits, bucketsSeen);
                Interlocked.Add(ref candidatesVisited, visited);
                Interlocked.Add(ref reachabilityChecks, reaches);
                Interlocked.Add(ref validatorChecks, validations);
                long avoided = count - visited;
                if (avoided > 0)
                    Interlocked.Add(ref candidatesAvoided, avoided);
                MarkUseful(observedNow);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.14 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                assistDepth = 0;
            }
        }

        private static bool ShouldColdBypass(long observedNow)
        {
            if (Volatile.Read(ref coldModeValue) == 0)
            {
                long useful = Interlocked.Read(ref lastUsefulObserved);
                if (observedNow - useful < ColdAfterObservedWithoutUseful)
                    return false;
                Volatile.Write(ref coldModeValue, 1);
                Interlocked.Increment(ref coldEnters);
            }

            if ((observedNow & ColdProbeMask) == 0)
            {
                Interlocked.Increment(ref coldProbes);
                return false;
            }

            Interlocked.Increment(ref coldBypasses);
            return true;
        }

        private static void MarkUseful(long observedNow)
        {
            Interlocked.Exchange(ref lastUsefulObserved, observedNow);
            if (Volatile.Read(ref coldModeValue) == 0) return;
            Volatile.Write(ref coldModeValue, 0);
            Interlocked.Increment(ref coldExits);
        }

        private static SourceState CreateState(object source)
        {
            int id = Interlocked.Increment(ref nextSourceId);
            if (id == 0)
                id = Interlocked.Increment(ref nextSourceId);
            return new SourceState { SourceId = id, MapId = int.MinValue };
        }

        private static bool MembershipMatches(object source, SourceKind kind, int count, Thing[] members)
        {
            if (members == null || members.Length != count)
                return false;

            for (int i = 0; i < count; i++)
            {
                Thing current = GetThingAt(source, kind, i);
                if (!ReferenceEquals(current, members[i]))
                    return false;
            }
            return true;
        }

        private static bool TryCaptureMembers(object source, SourceKind kind, int count, Map map, out Thing[] members, out CaptureFailure failure)
        {
            members = new Thing[count];
            failure = CaptureFailure.None;
            for (int i = 0; i < count; i++)
            {
                Thing thing = GetThingAt(source, kind, i);
                if (thing == null)
                {
                    failure = CaptureFailure.Invalid;
                    return false;
                }
                if (thing is Pawn)
                {
                    failure = CaptureFailure.Mobile;
                    return false;
                }
                if (!thing.Spawned || thing.MapHeld != map)
                {
                    failure = CaptureFailure.Unspawned;
                    return false;
                }
                IntVec3 pos = thing.Position;
                if (!pos.IsValid || !pos.InBounds(map))
                {
                    failure = CaptureFailure.Invalid;
                    return false;
                }
                members[i] = thing;
            }
            return true;
        }

        private static bool TryGetSourceShape(object source, out SourceKind kind, out int count)
        {
            IList<Thing> things = source as IList<Thing>;
            if (things != null)
            {
                kind = SourceKind.Thing;
                count = things.Count;
                return true;
            }

            IList<Pawn> pawns = source as IList<Pawn>;
            if (pawns != null)
            {
                kind = SourceKind.Pawn;
                count = pawns.Count;
                return true;
            }

            IList<Building> buildings = source as IList<Building>;
            if (buildings != null)
            {
                kind = SourceKind.Building;
                count = buildings.Count;
                return true;
            }

            kind = SourceKind.None;
            count = 0;
            return false;
        }

        private static Thing GetThingAt(object source, SourceKind kind, int index)
        {
            switch (kind)
            {
                case SourceKind.Thing:
                    return ((IList<Thing>)source)[index];
                case SourceKind.Pawn:
                    return ((IList<Pawn>)source)[index];
                case SourceKind.Building:
                    return ((IList<Building>)source)[index];
                default:
                    return null;
            }
        }

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            long avoided = Interlocked.Read(ref candidatesAvoided);
            long eligible = Interlocked.Read(ref eligibleCalls);
            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;
            double avgEstimate = eligible <= 0 ? 0.0 : Interlocked.Read(ref estimatedCandidatesTotal) / (double)eligible;
            double avgQueryUs = accelerated <= 0 ? 0.0 :
                (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / accelerated;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Persistent-fabric GenClosest V0.4.14: compatibilityReady=" + compatibilityReady +
                ", coldMode=" + (Volatile.Read(ref coldModeValue) != 0) +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", eligible=" + eligible +
                ", accelerated=" + accelerated +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", coldEnters=" + Interlocked.Read(ref coldEnters) +
                ", coldExits=" + Interlocked.Read(ref coldExits) +
                ", coldBypasses=" + Interlocked.Read(ref coldBypasses) +
                ", coldProbes=" + Interlocked.Read(ref coldProbes) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypasses) +
                ", shapeBypass=" + Interlocked.Read(ref shapeBypasses) +
                ", smallSetBypass=" + Interlocked.Read(ref smallSetBypasses) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", mobileSourceBypass=" + Interlocked.Read(ref mobileSourceBypasses) +
                ", unspawnedSourceBypass=" + Interlocked.Read(ref unspawnedSourceBypasses) +
                ", membershipHits=" + Interlocked.Read(ref membershipHits) +
                ", membershipRefreshes=" + Interlocked.Read(ref membershipRefreshes) +
                ", membershipRefreshRejected=" + Interlocked.Read(ref membershipRefreshRejected) +
                ", fabricMisses=" + Interlocked.Read(ref fabricMisses) +
                ", broadQueryBypass=" + Interlocked.Read(ref broadQueryBypasses) +
                ", liveCapFallback=" + Interlocked.Read(ref liveCapFallbacks) +
                ", staleFallback=" + Interlocked.Read(ref staleFallbacks) +
                ", maxLiveChecks=" + MaxLiveChecks +
                ", avgEstimate=" + avgEstimate.ToString("F1") +
                ", avgQueryUs=" + avgQueryUs.ToString("F2") +
                ", maxQueryUs=" + maxQueryUs.ToString("F2") +
                ", bucketVisits=" + Interlocked.Read(ref bucketVisits) +
                ", candidatesVisited=" + visited +
                ", avgCandidatesVisited=" + avgVisited.ToString("F1") +
                ", candidatesAvoided=" + avoided +
                ", avgCandidatesAvoided=" + avgAvoided.ToString("F1") +
                ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Cold sleep bypasses only this consumer; Vanilla remains authoritative for every bypass.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen)
                    break;
            }
        }

        private enum SourceKind
        {
            None,
            Thing,
            Pawn,
            Building
        }

        private enum CaptureFailure
        {
            None,
            Invalid,
            Mobile,
            Unspawned
        }

        private sealed class SourceState
        {
            internal int SourceId;
            internal int MapId;
            internal Thing[] Members;
        }
    }
}
