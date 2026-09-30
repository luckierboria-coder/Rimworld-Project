using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T34-A production consumer for the persistent map search fabric.
    ///
    /// Design:
    /// - main thread captures only stable source membership and primitive positions;
    /// - worker threads maintain immutable spatial buckets through PersistentMapSearchFabric;
    /// - published snapshots are consumed only on later calls; the main thread never waits;
    /// - live Reachability + live validator remain on the main thread and determine the final result;
    /// - any unsupported shape, stale snapshot, membership change, foreign-patch suppression or
    ///   worker miss falls straight through to Vanilla.
    ///
    /// T34-A deliberately expands the retired V0.4.14 consumer in two ways:
    /// - supports ThingRequest-backed ListerThings sources as well as custom global IList sources;
    /// - removes the old 64-live-check ceiling, which caused broad real workloads to bypass RimMT.
    /// </summary>
    internal static class CandidateFabric093T34A
    {
        internal const string FeatureId = "parallel.candidateFabric";

        private const int MinCandidateCount = 48;
        private const int MaxCandidateCount = 16384;

        private static readonly ConditionalWeakTable<object, SourceState> States =
            new ConditionalWeakTable<object, SourceState>();

        private static volatile bool compatibilityReady;
        private static bool installed;
        private static int nextSourceId;

        [ThreadStatic] private static int assistDepth;

        private static long observedCalls;
        private static long inScopeCalls;
        private static long eligibleCalls;
        private static long customSourceCalls;
        private static long thingRequestSourceCalls;
        private static long acceleratedCalls;
        private static long acceleratedNoResult;
        private static long fallbackCalls;
        private static long shapeBypasses;
        private static long sourceShapeBypasses;
        private static long mobileSourceBypasses;
        private static long smallSourceBypasses;
        private static long largeSourceBypasses;
        private static long membershipHits;
        private static long membershipRefreshes;
        private static long membershipRefreshRejected;
        private static long fabricMisses;
        private static long staleFallbacks;
        private static long candidatesSourceTotal;
        private static long candidatesVisited;
        private static long candidatesAvoided;
        private static long bucketVisits;
        private static long reachabilityChecks;
        private static long validatorChecks;
        private static long failures;
        private static long patchFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            // The fabric owns only ThingGrid observation + worker snapshot publication.
            // CandidateFabric owns the gameplay-facing GenClosest prefix.
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
                    FeatureGate.Suppress(FeatureId, "GenClosest.ClosestThingReachable 13-arg target not found");
                    patchFailures++;
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(CandidateFabric093T34A), nameof(Prefix));
                // Run before legacy S4/S5.1/Stage3. If T34-A proves a complete result,
                // downstream boolean prefixes observe a skipped original. On every miss,
                // the legacy/Vanilla chain remains untouched.
                prefix.priority = Priority.First + 320;
                harmony.Patch(target, prefix: prefix);
                installed = true;

                Log.Message("[RimMT] T34-A Async Candidate Fabric installed. ThingRequest-backed and custom static candidate sources may use worker-maintained spatial buckets; main thread never waits and live Reachability/validator remain authoritative.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                installed = false;
                FeatureGate.Suppress(FeatureId, "T34-A candidate fabric install failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] T34-A candidate fabric failed closed: " + ex.GetType().Name + ": " + ex.Message);
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
            Interlocked.Increment(ref observedCalls);

            if (!installed || !compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !JobGiverGlobalNearest04181.InJobGiverScope || assistDepth != 0 ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            Interlocked.Increment(ref inScopeCalls);

            Pawn pawn = traverseParams.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !root.IsValid || !root.InBounds(map) || maxDistance <= 0f)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            TraverseMode mode = traverseParams.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors &&
                mode != TraverseMode.NoPassClosedDoors)
            {
                Interlocked.Increment(ref shapeBypasses);
                return true;
            }

            // Match the previously validated V0.4.14 global-search admission shape.
            // Region-bounded calls remain Vanilla because selecting the globally nearest
            // candidate can differ from Vanilla's bounded region traversal semantics.
            if (traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref shapeBypasses);
                return true;
            }

            object source;
            SourceKind kind;
            int count;

            if (customGlobalSearchSet != null)
            {
                // Custom global sources are only semantically isolated from ThingRequest
                // when the request itself is undefined, matching the retired proven consumer.
                if (!thingReq.IsUndefined ||
                    !TryGetSourceShape(customGlobalSearchSet, out kind, out count))
                {
                    Interlocked.Increment(ref sourceShapeBypasses);
                    return true;
                }

                source = customGlobalSearchSet;
                Interlocked.Increment(ref customSourceCalls);
            }
            else
            {
                List<Thing> things;
                try { things = map.listerThings.ThingsMatching(thingReq); }
                catch
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                if (things == null)
                {
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                source = things;
                kind = SourceKind.Thing;
                count = things.Count;
                Interlocked.Increment(ref thingRequestSourceCalls);
            }

            if (kind == SourceKind.Pawn)
            {
                Interlocked.Increment(ref mobileSourceBypasses);
                return true;
            }

            if (count < MinCandidateCount)
            {
                Interlocked.Increment(ref smallSourceBypasses);
                return true;
            }
            if (count > MaxCandidateCount)
            {
                Interlocked.Increment(ref largeSourceBypasses);
                return true;
            }

            Interlocked.Increment(ref eligibleCalls);
            Interlocked.Add(ref candidatesSourceTotal, count);

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
                    else
                        Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                state.Members = members;
                Interlocked.Increment(ref membershipRefreshes);
                if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
                {
                    Interlocked.Increment(ref membershipRefreshRejected);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                // First observation is publication-only. Never wait for the worker.
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref membershipHits);

            PersistentMapSearchFabric.SourceSnapshot snapshot;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, state.SourceId, out snapshot) ||
                snapshot == null || snapshot.Count != count)
            {
                Interlocked.Increment(ref fabricMisses);
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            assistDepth = 1;
            try
            {
                Thing chosen;
                int visited;
                int bucketsSeen;
                int reaches;
                int validations;
                bool staleDetected;

                // count+1 removes the retired 64-live-check cap while preserving the existing
                // SourceSnapshot contract. This path either completes the exact spatial search
                // or fails open; there is no worker wait and no approximate shortlist authority.
                bool ok = snapshot.TryFindClosest(
                    root, map, peMode, traverseParams, maxDistance, validator, count + 1,
                    out chosen, out visited, out bucketsSeen, out reaches, out validations, out staleDetected);

                if (!ok)
                {
                    if (staleDetected)
                        Interlocked.Increment(ref staleFallbacks);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                __result = chosen;
                Interlocked.Increment(ref acceleratedCalls);
                if (chosen == null)
                    Interlocked.Increment(ref acceleratedNoResult);
                Interlocked.Add(ref candidatesVisited, visited);
                Interlocked.Add(ref bucketVisits, bucketsSeen);
                Interlocked.Add(ref reachabilityChecks, reaches);
                Interlocked.Add(ref validatorChecks, validations);

                long avoided = count - visited;
                if (avoided > 0)
                    Interlocked.Add(ref candidatesAvoided, avoided);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] T34-A candidate fabric runtime failure; this call falls back to Vanilla. " +
                    ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                assistDepth = 0;
            }
        }

        private static SourceState CreateState(object ignored)
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

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing current = GetThingAt(source, kind, i);
                    if (!ReferenceEquals(current, members[i]))
                        return false;
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool TryCaptureMembers(
            object source, SourceKind kind, int count, Map map,
            out Thing[] members, out CaptureFailure failure)
        {
            members = new Thing[count];
            failure = CaptureFailure.None;

            try
            {
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
            catch
            {
                failure = CaptureFailure.Invalid;
                return false;
            }
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

            IList<Building> buildings = source as IList<Building>;
            if (buildings != null)
            {
                kind = SourceKind.Building;
                count = buildings.Count;
                return true;
            }

            IList<Pawn> pawns = source as IList<Pawn>;
            if (pawns != null)
            {
                kind = SourceKind.Pawn;
                count = pawns.Count;
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
                case SourceKind.Building:
                    return ((IList<Building>)source)[index];
                case SourceKind.Pawn:
                    return ((IList<Pawn>)source)[index];
                default:
                    return null;
            }
        }

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long eligible = Interlocked.Read(ref eligibleCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            long avoided = Interlocked.Read(ref candidatesAvoided);
            double avgSource = eligible <= 0 ? 0.0 :
                Interlocked.Read(ref candidatesSourceTotal) / (double)eligible;
            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;

            return "T34-A async candidate fabric: installed=" + installed +
                ", compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", inScope=" + Interlocked.Read(ref inScopeCalls) +
                ", eligible=" + eligible +
                ", source[thingRequest/custom]=" +
                Interlocked.Read(ref thingRequestSourceCalls) + "/" +
                Interlocked.Read(ref customSourceCalls) +
                ", accelerated=" + accelerated +
                ", acceleratedNull=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", bypass[shape/sourceShape/mobile/small/large]=" +
                Interlocked.Read(ref shapeBypasses) + "/" +
                Interlocked.Read(ref sourceShapeBypasses) + "/" +
                Interlocked.Read(ref mobileSourceBypasses) + "/" +
                Interlocked.Read(ref smallSourceBypasses) + "/" +
                Interlocked.Read(ref largeSourceBypasses) +
                ", membership[hits/refresh/rejected]=" +
                Interlocked.Read(ref membershipHits) + "/" +
                Interlocked.Read(ref membershipRefreshes) + "/" +
                Interlocked.Read(ref membershipRefreshRejected) +
                ", fabricMisses=" + Interlocked.Read(ref fabricMisses) +
                ", staleFallbacks=" + Interlocked.Read(ref staleFallbacks) +
                ", avgSource=" + avgSource.ToString("F1") +
                ", avgVisited=" + avgVisited.ToString("F1") +
                ", avgAvoided=" + avgAvoided.ToString("F1") +
                ", bucketVisits=" + Interlocked.Read(ref bucketVisits) +
                ", reachChecks=" + Interlocked.Read(ref reachabilityChecks) +
                ", validatorChecks=" + Interlocked.Read(ref validatorChecks) +
                ", failures=" + Interlocked.Read(ref failures) +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                ". No-wait: first/stale/missing snapshots fall through; workers never run live validators or Reachability.";
        }

        private sealed class SourceState
        {
            internal int SourceId;
            internal int MapId;
            internal Thing[] Members;
        }

        private enum SourceKind
        {
            None,
            Thing,
            Building,
            Pawn
        }

        private enum CaptureFailure
        {
            None,
            Invalid,
            Mobile,
            Unspawned
        }
    }
}
