using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
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
    /// Steady-state hits allocate nothing. If this lane repeatedly sees only non-bill-giver sets, it
    /// cold-sleeps and probes 1/64 eligible tails until useful membership reappears.
    /// </summary>
    internal static class DoBillTailFabric092
    {
        private const int MinCount = 16;
        private const int MaxCount = 127;
        private const int TailThresholdMs = 16;
        private const int MaxTrackedStates = 2048;
        private const int ColdAfterZeroYield = 256;
        private const int ColdProbeMask = 63; // 1/64 eligible tails while cold.
        private static readonly long TailThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailThresholdMs / 1000L);
        private static readonly Dictionary<SourceKey, SourceState> States = new Dictionary<SourceKey, SourceState>();
        private static int nextSourceId = 400000;
        private static bool patched;
        private static bool coldMode;
        private static int zeroYieldStreak;
        private static int coldProbeSerial;
        private static int failureLogs;

        private static long observed;
        private static long tailEligible;
        private static long thresholdBypass;
        private static long invalidQueryBypass;
        private static long definedRequestBypass;
        private static long regionPolicyBypass;
        private static long nonCollectionBypass;
        private static long smallSetBypass;
        private static long largeSetBypass;
        private static long registrations;
        private static long registrationRejected;
        private static long snapshotHits;
        private static long snapshotMisses;
        private static long accelerated;
        private static long broadBypass;
        private static long liveFallback;
        private static long liveChecks;
        private static long candidatesVisited;
        private static long stateResets;
        private static long invalidMemberBypass;
        private static long pawnBypass;
        private static long unspawnedBypass;
        private static long invalidPositionBypass;
        private static long countMismatchBypass;
        private static long coldEnters;
        private static long coldExits;
        private static long coldBypasses;
        private static long coldProbes;

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
                Log.Message("[RimMT] DoBill worker-tail fabric active with zero-yield cold sleep: repeated 16..127 static bill-giver sets can use worker-maintained spatial snapshots after a 16ms JobGiver tail threshold; 256 consecutive zero-yield eligible tails enter 1/64 probing until useful membership returns.");
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
            if (!patched || !FeatureGate.IsEnabled("parallel.jobPartition") || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || customGlobalSearchSet == null)
                return true;

            observed++;
            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L || Stopwatch.GetTimestamp() - scopeStart < TailThresholdTicks)
            {
                thresholdBypass++;
                return true;
            }

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map))
            {
                invalidQueryBypass++;
                return true;
            }
            if (!thingReq.IsUndefined)
            {
                definedRequestBypass++;
                return true;
            }
            if (traversableRegionTypes != RegionType.Set_Passable || ignoreEntirelyForbiddenRegions ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                regionPolicyBypass++;
                return true;
            }

            ICollection<Thing> collection = customGlobalSearchSet as ICollection<Thing>;
            if (collection == null) { nonCollectionBypass++; return true; }
            if (collection.Count < MinCount) { smallSetBypass++; return true; }
            if (collection.Count > MaxCount) { largeSetBypass++; return true; }

            tailEligible++;
            if (ShouldColdBypass())
                return true;

            try
            {
                int count = collection.Count;
                int hash;
                ValidationFailure failure;
                if (!TryValidateAndHash(collection, map, count, out hash, out failure))
                {
                    CountValidationFailure(failure);
                    RecordZeroYield();
                    return true;
                }

                SourceKey key = new SourceKey(map.uniqueID, count, hash);
                SourceState state;
                if (!States.TryGetValue(key, out state) || !MembershipMatches(collection, state.Members, count))
                {
                    Thing[] members;
                    if (!TryCaptureMembers(collection, map, count, out members))
                    {
                        invalidMemberBypass++;
                        RecordZeroYield();
                        return true;
                    }

                    if (States.Count >= MaxTrackedStates)
                    {
                        States.Clear();
                        stateResets++;
                    }

                    state = new SourceState { SourceId = ++nextSourceId, Members = members };
                    States[key] = state;
                    if (!PersistentMapSearchFabric.RegisterOrUpdateSource(map, state.SourceId, members))
                    {
                        registrationRejected++;
                        return true;
                    }
                    registrations++;
                    MarkUseful();
                    return true;
                }

                MarkUseful();
                PersistentMapSearchFabric.SourceSnapshot snapshot;
                if (!PersistentMapSearchFabric.TryGetSourceSnapshot(map, state.SourceId, out snapshot) || snapshot == null || snapshot.Count != count)
                {
                    snapshotMisses++;
                    return true;
                }
                snapshotHits++;

                int cap = DynamicLiveCap();
                int estimate = snapshot.EstimateCandidates(root, maxDistance, cap);
                if (estimate > cap)
                {
                    broadBypass++;
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
                    liveFallback++;
                    return true;
                }

                __result = chosen;
                accelerated++;
                liveChecks += reaches + validations;
                candidatesVisited += visited;
                return false;
            }
            catch (Exception ex)
            {
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] DoBill worker-tail fabric failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool ShouldColdBypass()
        {
            if (!coldMode) return false;
            int serial = ++coldProbeSerial;
            if ((serial & ColdProbeMask) == 0)
            {
                coldProbes++;
                return false;
            }
            coldBypasses++;
            return true;
        }

        private static void RecordZeroYield()
        {
            if (zeroYieldStreak < int.MaxValue) zeroYieldStreak++;
            if (coldMode || zeroYieldStreak < ColdAfterZeroYield) return;
            coldMode = true;
            coldProbeSerial = 0;
            coldEnters++;
        }

        private static void MarkUseful()
        {
            zeroYieldStreak = 0;
            if (!coldMode) return;
            coldMode = false;
            coldProbeSerial = 0;
            coldExits++;
        }

        private static bool TryValidateAndHash(ICollection<Thing> collection, Map map, int expectedCount, out int hash, out ValidationFailure failure)
        {
            hash = 17;
            failure = ValidationFailure.None;
            int seen = 0;
            foreach (Thing thing in collection)
            {
                if (seen++ >= expectedCount)
                {
                    failure = ValidationFailure.CountMismatch;
                    return false;
                }
                if (thing == null || !(thing is IBillGiver))
                {
                    failure = ValidationFailure.InvalidMember;
                    return false;
                }
                if (thing is Pawn)
                {
                    failure = ValidationFailure.Pawn;
                    return false;
                }
                if (!thing.Spawned || thing.MapHeld != map)
                {
                    failure = ValidationFailure.Unspawned;
                    return false;
                }

                IntVec3 pos = thing.Position;
                if (!pos.IsValid || !pos.InBounds(map))
                {
                    failure = ValidationFailure.InvalidPosition;
                    return false;
                }
                unchecked { hash = hash * 31 + thing.thingIDNumber; }
            }
            if (seen == expectedCount) return true;
            failure = ValidationFailure.CountMismatch;
            return false;
        }

        private static void CountValidationFailure(ValidationFailure failure)
        {
            switch (failure)
            {
                case ValidationFailure.Pawn: pawnBypass++; break;
                case ValidationFailure.Unspawned: unspawnedBypass++; break;
                case ValidationFailure.InvalidPosition: invalidPositionBypass++; break;
                case ValidationFailure.CountMismatch: countMismatchBypass++; break;
                default: invalidMemberBypass++; break;
            }
        }

        private static bool TryCaptureMembers(ICollection<Thing> collection, Map map, int expectedCount, out Thing[] members)
        {
            members = new Thing[expectedCount];
            int i = 0;
            foreach (Thing thing in collection)
            {
                if (i >= expectedCount || thing == null || !(thing is IBillGiver) || thing is Pawn ||
                    !thing.Spawned || thing.MapHeld != map)
                {
                    members = null;
                    return false;
                }
                IntVec3 pos = thing.Position;
                if (!pos.IsValid || !pos.InBounds(map))
                {
                    members = null;
                    return false;
                }
                members[i++] = thing;
            }
            if (i == expectedCount) return true;
            members = null;
            return false;
        }

        private static bool MembershipMatches(ICollection<Thing> collection, Thing[] members, int expectedCount)
        {
            if (members == null || members.Length != expectedCount || collection.Count != expectedCount) return false;
            int i = 0;
            foreach (Thing thing in collection)
            {
                if (i >= expectedCount || !ReferenceEquals(thing, members[i++])) return false;
            }
            return i == expectedCount;
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

        internal static string Summary()
        {
            return "DoBill worker-tail fabric: patched=" + patched +
                ", coldMode=" + coldMode +
                ", observed=" + observed +
                ", tailEligible=" + tailEligible +
                ", admissionBypass=[threshold=" + thresholdBypass +
                ", invalidQuery=" + invalidQueryBypass +
                ", definedRequest=" + definedRequestBypass +
                ", regionPolicy=" + regionPolicyBypass +
                ", nonCollection=" + nonCollectionBypass +
                ", smallSet=" + smallSetBypass +
                ", largeSet=" + largeSetBypass + "]" +
                ", registrations=" + registrations +
                ", registrationRejected=" + registrationRejected +
                ", snapshotHits=" + snapshotHits +
                ", snapshotMisses=" + snapshotMisses +
                ", accelerated=" + accelerated +
                ", broadBypass=" + broadBypass +
                ", liveFallback=" + liveFallback +
                ", validationBypass=[invalidMember=" + invalidMemberBypass +
                ", pawn=" + pawnBypass +
                ", unspawned=" + unspawnedBypass +
                ", invalidPosition=" + invalidPositionBypass +
                ", countMismatch=" + countMismatchBypass + "]" +
                ", zeroYieldStreak=" + zeroYieldStreak +
                ", coldEnters=" + coldEnters +
                ", coldExits=" + coldExits +
                ", coldBypasses=" + coldBypasses +
                ", coldProbes=" + coldProbes +
                ", liveChecks=" + liveChecks +
                ", candidatesVisited=" + candidatesVisited +
                ", retainedStates=" + States.Count +
                ", stateResets=" + stateResets +
                ", currentLiveCap=" + DynamicLiveCap() + ".";
        }

        private enum ValidationFailure { None, InvalidMember, Pawn, Unspawned, InvalidPosition, CountMismatch }

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
