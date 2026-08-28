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
    // V0.4.18.3 replacement consumer for AdaptiveGenClosestAssist V0.4.14.
    //
    // The worker-side PersistentMapSearchFabric itself was sound, but the old consumer keyed
    // membership state by the IList object. JobGiver frequently creates short-lived lists, so
    // identical member sets arrived under new objects and almost never reused the published
    // fabric snapshot.
    //
    // This version keys each map's supported source by an order-sensitive reference fingerprint.
    // A fingerprint hit is NEVER sufficient by itself: every member is compared by reference to
    // the retained source order before the sourceId is reused. Hash collisions therefore only
    // cause a miss, never an incorrect result. The first occurrence of a stable membership is
    // registered into PersistentMapSearchFabric; later temporary IList instances with exactly
    // the same members/order can reuse that worker-maintained spatial source.
    //
    // Stutter rule: EstimateCandidates executes before any live Reachability/validator call. If
    // more than MaxLiveChecks candidates can be in range, this layer does zero live checks and
    // falls straight through to Vanilla. V0.4.18.3 raises the proven-conservative 64 cap to 128
    // because sampled ReachProfile authority now removes most RegionTraverser work, but broad
    // no-result searches remain fail-closed.
    internal static class StableGenClosestAssist04183
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int MaxLiveChecks = 128;
        private const int MaxStableSourcesPerMap = 80;

        private static readonly ConditionalWeakTable<Map, StableMapState> MapStates =
            new ConditionalWeakTable<Map, StableMapState>();

        private static volatile bool compatibilityReady;
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
        private static long signatureLookups;
        private static long signatureHits;
        private static long signatureCollisions;
        private static long sourceCreates;
        private static long sourceCapBypasses;
        private static long registrationRejected;
        private static long fabricMisses;
        private static long broadQueryBypasses;
        private static long liveCapFallbacks;
        private static long staleFallbacks;
        private static long capturedMembers;
        private static long estimatedCandidatesTotal;
        private static long captureTicks;
        private static long captureTicksMax;
        private static long queryTicks;
        private static long queryTicksMax;
        private static long bucketVisits;
        private static long candidatesVisited;
        private static long candidatesAvoided;
        private static long reachabilityChecks;
        private static long validatorChecks;
        private static long failures;

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
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.18.3 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(StableGenClosestAssist04183), nameof(Prefix));
                prefix.priority = Priority.First + 100;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] parallel.jobPartition V0.4.18.3 stable spatial consumer installed. Supported temporary GenClosest lists are keyed by exact order-sensitive member signatures, then reused through PersistentMapSearchFabric after reference-by-reference validation. Queries estimated above 128 live candidates fall through before any live Reachability/validator work.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "stable spatial GenClosest patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.18.3 patch failed; Vanilla GenClosest remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
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

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
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

            Thing[] members;
            SourceFingerprint fingerprint;
            CaptureFailure captureFailure;
            long captureStart = Stopwatch.GetTimestamp();
            bool captured = TryCaptureMembersAndFingerprint(source, kind, count, map, out members, out fingerprint, out captureFailure);
            RecordElapsed(ref captureTicks, ref captureTicksMax, captureStart);
            if (!captured)
            {
                if (captureFailure == CaptureFailure.Mobile)
                    Interlocked.Increment(ref mobileSourceBypasses);
                else if (captureFailure == CaptureFailure.Unspawned)
                    Interlocked.Increment(ref unspawnedSourceBypasses);
                else
                    Interlocked.Increment(ref fallbackCalls);
                return true;
            }
            Interlocked.Add(ref capturedMembers, count);

            StableMapState mapState = MapStates.GetValue(map, delegate(Map m) { return new StableMapState(); });
            Interlocked.Increment(ref signatureLookups);
            SourceState state = mapState.FindExact(fingerprint, members, ref signatureCollisions);
            if (state == null)
            {
                if (mapState.SourceCount >= MaxStableSourcesPerMap)
                {
                    Interlocked.Increment(ref sourceCapBypasses);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                state = new SourceState(NextSourceId(), members);
                mapState.Add(fingerprint, state);
                Interlocked.Increment(ref sourceCreates);
                if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
                {
                    state.Disabled = true;
                    Interlocked.Increment(ref registrationRejected);
                    Interlocked.Increment(ref fallbackCalls);
                    return true;
                }

                // Publication is asynchronous. The current request stays Vanilla; later
                // ephemeral lists with identical membership can hit the same sourceId.
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

            Interlocked.Increment(ref signatureHits);
            if (state.Disabled)
            {
                Interlocked.Increment(ref fallbackCalls);
                return true;
            }

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
                RecordElapsed(ref queryTicks, ref queryTicksMax, queryStart);

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
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.18.3 runtime failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
            finally
            {
                assistDepth = 0;
            }
        }

        private static int NextSourceId()
        {
            int id = Interlocked.Increment(ref nextSourceId);
            if (id == 0)
                id = Interlocked.Increment(ref nextSourceId);
            return id;
        }

        private static bool TryCaptureMembersAndFingerprint(
            object source, SourceKind kind, int count, Map map,
            out Thing[] members, out SourceFingerprint fingerprint, out CaptureFailure failure)
        {
            members = new Thing[count];
            failure = CaptureFailure.None;
            unchecked
            {
                int hashA = 17;
                int hashB = 486187739;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = GetThingAt(source, kind, i);
                    if (thing == null)
                    {
                        fingerprint = default(SourceFingerprint);
                        failure = CaptureFailure.Invalid;
                        return false;
                    }
                    if (thing is Pawn)
                    {
                        fingerprint = default(SourceFingerprint);
                        failure = CaptureFailure.Mobile;
                        return false;
                    }
                    if (!thing.Spawned || thing.MapHeld != map)
                    {
                        fingerprint = default(SourceFingerprint);
                        failure = CaptureFailure.Unspawned;
                        return false;
                    }
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        fingerprint = default(SourceFingerprint);
                        failure = CaptureFailure.Invalid;
                        return false;
                    }

                    members[i] = thing;
                    int identity = RuntimeHelpers.GetHashCode(thing);
                    hashA = (hashA * 31) ^ identity;
                    hashB = (hashB * 16777619) ^ (identity + i * 397);
                }
                fingerprint = new SourceFingerprint(count, hashA, hashB);
                return true;
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

        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
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

        internal static string Summary()
        {
            long accelerated = Interlocked.Read(ref acceleratedCalls);
            long visited = Interlocked.Read(ref candidatesVisited);
            long avoided = Interlocked.Read(ref candidatesAvoided);
            long eligible = Interlocked.Read(ref eligibleCalls);
            long lookups = Interlocked.Read(ref signatureLookups);
            double avgVisited = accelerated <= 0 ? 0.0 : visited / (double)accelerated;
            double avgAvoided = accelerated <= 0 ? 0.0 : avoided / (double)accelerated;
            double avgEstimate = eligible <= 0 ? 0.0 : Interlocked.Read(ref estimatedCandidatesTotal) / (double)eligible;
            double hitRate = lookups <= 0 ? 0.0 : Interlocked.Read(ref signatureHits) * 100.0 / lookups;
            double avgCaptureUs = eligible <= 0 ? 0.0 :
                (Interlocked.Read(ref captureTicks) * 1000000.0 / Stopwatch.Frequency) / eligible;
            double maxCaptureUs = Interlocked.Read(ref captureTicksMax) * 1000000.0 / Stopwatch.Frequency;
            double avgQueryUs = accelerated <= 0 ? 0.0 :
                (Interlocked.Read(ref queryTicks) * 1000000.0 / Stopwatch.Frequency) / accelerated;
            double maxQueryUs = Interlocked.Read(ref queryTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Stable spatial GenClosest V0.4.18.3: compatibilityReady=" + compatibilityReady +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", eligible=" + eligible +
                ", accelerated=" + accelerated +
                ", acceleratedNoResult=" + Interlocked.Read(ref acceleratedNoResult) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypasses) +
                ", shapeBypass=" + Interlocked.Read(ref shapeBypasses) +
                ", smallSetBypass=" + Interlocked.Read(ref smallSetBypasses) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", mobileSourceBypass=" + Interlocked.Read(ref mobileSourceBypasses) +
                ", unspawnedSourceBypass=" + Interlocked.Read(ref unspawnedSourceBypasses) +
                ", signatureLookups=" + lookups +
                ", signatureHits=" + Interlocked.Read(ref signatureHits) +
                ", signatureHitRate=" + hitRate.ToString("F1") + "%" +
                ", signatureCollisions=" + Interlocked.Read(ref signatureCollisions) +
                ", sourceCreates=" + Interlocked.Read(ref sourceCreates) +
                ", sourceCapBypass=" + Interlocked.Read(ref sourceCapBypasses) +
                ", registrationRejected=" + Interlocked.Read(ref registrationRejected) +
                ", fabricMisses=" + Interlocked.Read(ref fabricMisses) +
                ", broadQueryBypass=" + Interlocked.Read(ref broadQueryBypasses) +
                ", liveCapFallback=" + Interlocked.Read(ref liveCapFallbacks) +
                ", staleFallback=" + Interlocked.Read(ref staleFallbacks) +
                ", maxLiveChecks=" + MaxLiveChecks +
                ", capturedMembers=" + Interlocked.Read(ref capturedMembers) +
                ", avgCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxCaptureUs=" + maxCaptureUs.ToString("F2") +
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
                ". Stable signatures are reference-validated before sourceId reuse; broad queries remain Vanilla-authoritative before live checks.";
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

        private struct SourceFingerprint : IEquatable<SourceFingerprint>
        {
            internal readonly int Count;
            internal readonly int HashA;
            internal readonly int HashB;

            internal SourceFingerprint(int count, int hashA, int hashB)
            {
                Count = count;
                HashA = hashA;
                HashB = hashB;
            }

            public bool Equals(SourceFingerprint other)
            {
                return Count == other.Count && HashA == other.HashA && HashB == other.HashB;
            }

            public override bool Equals(object obj)
            {
                return obj is SourceFingerprint && Equals((SourceFingerprint)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Count;
                    hash = (hash * 397) ^ HashA;
                    hash = (hash * 397) ^ HashB;
                    return hash;
                }
            }
        }

        private sealed class SourceState
        {
            internal readonly int SourceId;
            internal readonly Thing[] Members;
            internal bool Disabled;

            internal SourceState(int sourceId, Thing[] members)
            {
                SourceId = sourceId;
                Members = members;
            }
        }

        private sealed class StableMapState
        {
            private readonly Dictionary<SourceFingerprint, List<SourceState>> sources =
                new Dictionary<SourceFingerprint, List<SourceState>>();
            internal int SourceCount;

            internal SourceState FindExact(SourceFingerprint fingerprint, Thing[] members, ref long collisionCounter)
            {
                List<SourceState> bucket;
                if (!sources.TryGetValue(fingerprint, out bucket))
                    return null;

                for (int s = 0; s < bucket.Count; s++)
                {
                    SourceState state = bucket[s];
                    if (MembersEqual(state.Members, members))
                        return state;
                    Interlocked.Increment(ref collisionCounter);
                }
                return null;
            }

            internal void Add(SourceFingerprint fingerprint, SourceState state)
            {
                List<SourceState> bucket;
                if (!sources.TryGetValue(fingerprint, out bucket))
                {
                    bucket = new List<SourceState>(1);
                    sources.Add(fingerprint, bucket);
                }
                bucket.Add(state);
                SourceCount++;
            }

            private static bool MembersEqual(Thing[] a, Thing[] b)
            {
                if (a == null || b == null || a.Length != b.Length)
                    return false;
                for (int i = 0; i < a.Length; i++)
                    if (!ReferenceEquals(a[i], b[i]))
                        return false;
                return true;
            }
        }
    }
}
