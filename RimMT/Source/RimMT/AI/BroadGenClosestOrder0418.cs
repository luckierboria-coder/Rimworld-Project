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
    // V0.4.18 targets the broad-query gap exposed by the 15-minute V0.4.17.2 trace:
    // the persistent fabric was healthy, but queries estimated above 64 live checks were
    // deliberately handed straight back to Vanilla. Those broad calls were exactly where
    // JobGiver -> GenClosest spikes reached 100-150 ms.
    //
    // This layer does NOT replace GenClosest and does NOT evaluate Reachability, validators,
    // reservations, priorities, or Jobs. It only gives Vanilla the exact same candidates in
    // exact-distance-nearest-first order (stable by original source index). That lets Vanilla
    // establish a tight best-distance earlier, so its own later distance gates can reject far
    // candidates before expensive live checks.
    //
    // No worker is waited on here. Same-call worker waits merely move the stall; broad-query
    // ordering is intentionally a bounded main-thread primitive sort until a future async
    // candidate plan can be published ahead of demand.
    internal static class BroadGenClosestOrder0418
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int MaxCandidateCount = 8192;

        private static long observed;
        private static long runOriginalBypass;
        private static long shapeBypass;
        private static long nonListBypass;
        private static long smallSetBypass;
        private static long tooLargeBypass;
        private static long haulableBypass;
        private static long mobileBypass;
        private static long invalidBypass;
        private static long reordered;
        private static long candidatesReordered;
        private static long maxCandidates;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

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
                    Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering unavailable: target overload not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(BroadGenClosestOrder0418), nameof(Prefix));
                // AdaptiveGenClosestAssist is First+100. Run immediately after it: if the
                // V0.4.14 consumer completed the query, __runOriginal is false and we stay inert.
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);

                Log.Message("[RimMT] V0.4.18 broad GenClosest ordering installed. Large supported custom-global searches keep Vanilla authority but receive stable exact-distance nearest-first candidate order; no worker wait is introduced.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering patch failed; Vanilla ordering remains unchanged. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            ref IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions,
            bool __runOriginal)
        {
            Interlocked.Increment(ref observed);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref runOriginalBypass);
                return;
            }

            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || RimMTRuntime.MainThreadFrames <= 1)
                return;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref invalidBypass);
                return;
            }

            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref shapeBypass);
                return;
            }

            object source = customGlobalSearchSet;
            IList<Thing> things = source as IList<Thing>;
            IList<Building> buildings = source as IList<Building>;
            int count;
            SourceKind kind;
            if (things != null)
            {
                count = things.Count;
                kind = SourceKind.Thing;
            }
            else if (buildings != null)
            {
                count = buildings.Count;
                kind = SourceKind.Building;
            }
            else
            {
                Interlocked.Increment(ref nonListBypass);
                return;
            }

            if (count < MinCandidateCount)
            {
                Interlocked.Increment(ref smallSetBypass);
                return;
            }
            if (count > MaxCandidateCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return;
            }

            try
            {
                List<Thing> haulables = map.listerHaulables == null
                    ? null
                    : map.listerHaulables.ThingsPotentiallyNeedingHauling();
                if (haulables != null && ReferenceEquals(source, haulables))
                {
                    Interlocked.Increment(ref haulableBypass);
                    return;
                }

                Candidate[] candidates = new Candidate[count];
                for (int i = 0; i < count; i++)
                {
                    Thing thing = kind == SourceKind.Thing ? things[i] : buildings[i];
                    if (thing == null)
                    {
                        Interlocked.Increment(ref invalidBypass);
                        return;
                    }
                    if (thing is Pawn)
                    {
                        Interlocked.Increment(ref mobileBypass);
                        return;
                    }
                    if (!thing.Spawned || thing.MapHeld != map)
                    {
                        Interlocked.Increment(ref invalidBypass);
                        return;
                    }

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        Interlocked.Increment(ref invalidBypass);
                        return;
                    }

                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distanceSquared = dx * dx + dz * dz;
                    candidates[i] = new Candidate(thing, distanceSquared, i);
                }

                long started = Stopwatch.GetTimestamp();
                Array.Sort(candidates, CandidateComparer.Instance);
                long elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref sortTicks, elapsed);
                UpdateMax(ref maxSortTicks, elapsed);

                Thing[] ordered = new Thing[count];
                for (int i = 0; i < count; i++)
                    ordered[i] = candidates[i].Thing;

                customGlobalSearchSet = ordered;
                Interlocked.Increment(ref reordered);
                Interlocked.Add(ref candidatesReordered, count);
                UpdateMax(ref maxCandidates, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] V0.4.18 broad GenClosest ordering failed for one call; Vanilla keeps the original search semantics. " + ex.GetType().Name + ": " + ex.Message);
            }
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
            long sorted = Interlocked.Read(ref reordered);
            long candidates = Interlocked.Read(ref candidatesReordered);
            double avgCandidates = sorted == 0 ? 0.0 : candidates / (double)sorted;
            double avgSortUs = sorted == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / sorted;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "Broad GenClosest ordering V0.4.18: observed=" + Interlocked.Read(ref observed) +
                ", reordered=" + sorted +
                ", runOriginalBypass=" + Interlocked.Read(ref runOriginalBypass) +
                ", shapeBypass=" + Interlocked.Read(ref shapeBypass) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypass) +
                ", smallSetBypass=" + Interlocked.Read(ref smallSetBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypass) +
                ", mobileBypass=" + Interlocked.Read(ref mobileBypass) +
                ", invalidBypass=" + Interlocked.Read(ref invalidBypass) +
                ", candidatesReordered=" + candidates +
                ", avgCandidates=" + avgCandidates.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidates) +
                ", avgSortUs=" + avgSortUs.ToString("F2") +
                ", maxSortUs=" + maxSortUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Exact candidate membership is preserved; Vanilla validator/Reachability/final selection remains authoritative.";
        }

        private enum SourceKind
        {
            Thing,
            Building
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;

            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();

            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
