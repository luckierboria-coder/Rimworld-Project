using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.10.2 refinement for the common 96-320 candidate range seen in the
    // post-Loadout-Compositing playtest. V0.4.10.1 already performs a stable
    // 16-cell ring partition. This second, lower-priority prefix only sees the
    // Thing[] produced by that validated path and refines medium sets into stable
    // 8-cell rings when the set has meaningful spatial spread.
    //
    // Candidate membership is NEVER changed. No validator, reachability,
    // reservation or Job logic runs here. Vanilla remains authoritative.
    internal static class MediumSetCandidateRefinement
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinCandidateCount = 96;
        private const int MaxCandidateCount = 320;
        private const int FineRingSize = 8;
        private const int MinimumUsefulMaxRing = 4;
        private const int MinimumFarCandidates = 16;

        private static long observedCalls;
        private static long arrayInputs;
        private static long mediumSets;
        private static long refinedSets;
        private static long clusteredSkips;
        private static long candidatesRefined;
        private static long maxCandidates;
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
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.10.2 medium-set refinement unavailable: target not found.");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(MediumSetCandidateRefinement), nameof(Prefix));
                // SingleCallCandidatePartition runs at Priority.First + 50. Run immediately
                // after it so we refine only the validated Thing[] that it produced.
                prefix.priority = Priority.First + 25;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.10.2 medium-set refinement installed: 96-320 candidate arrays may receive a stable 8-cell ring refinement; candidate membership and Vanilla authority are unchanged.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] parallel.jobPartition V0.4.10.2 medium-set refinement patch failed; V0.4.10.1 behavior remains intact. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            ref IEnumerable<Thing> customGlobalSearchSet,
            int searchRegionsMin,
            int searchRegionsMax,
            bool forceAllowGlobalSearch,
            RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions)
        {
            Interlocked.Increment(ref observedCalls);

            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || map == null || map.Disposed ||
                !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
                return;

            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
                return;

            Thing[] source = customGlobalSearchSet as Thing[];
            if (source == null)
                return;

            Interlocked.Increment(ref arrayInputs);
            int count = source.Length;
            if (count < MinCandidateCount || count > MaxCandidateCount)
                return;

            Interlocked.Increment(ref mediumSets);
            UpdateMax(ref maxCandidates, count);

            try
            {
                int[] ringKeys = new int[count];
                int maxRing = 0;
                int farCandidates = 0;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null)
                        return;

                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid || !pos.InBounds(map))
                        return;

                    int dx = Math.Abs(pos.x - root.x);
                    int dz = Math.Abs(pos.z - root.z);
                    int ring = Math.Max(dx, dz) / FineRingSize;
                    ringKeys[i] = ring;
                    if (ring > maxRing) maxRing = ring;
                    if (ring >= 2) farCandidates++;
                }

                // Avoid paying another allocation/partition pass for tightly clustered
                // sets where finer ordering cannot materially shrink Vanilla best-distance.
                if (maxRing < MinimumUsefulMaxRing || farCandidates < MinimumFarCandidates)
                {
                    Interlocked.Increment(ref clusteredSkips);
                    return;
                }

                int[] counts = new int[maxRing + 1];
                for (int i = 0; i < count; i++)
                    counts[ringKeys[i]]++;

                int[] offsets = new int[counts.Length];
                int sum = 0;
                for (int i = 0; i < counts.Length; i++)
                {
                    offsets[i] = sum;
                    sum += counts[i];
                }

                Thing[] result = new Thing[count];
                int[] write = new int[offsets.Length];
                Array.Copy(offsets, write, offsets.Length);
                for (int i = 0; i < count; i++)
                    result[write[ringKeys[i]]++] = source[i];

                customGlobalSearchSet = result;
                Interlocked.Increment(ref refinedSets);
                Interlocked.Add(ref candidatesRefined, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] V0.4.10.2 medium-set refinement failed for one call; V0.4.10.1/Vanilla path remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void UpdateMax(ref long field, int value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        internal static string Summary()
        {
            long refined = Interlocked.Read(ref refinedSets);
            long candidates = Interlocked.Read(ref candidatesRefined);
            double avg = refined <= 0 ? 0.0 : candidates / (double)refined;
            return "Medium-set refinement V0.4.10.2: observed=" + Interlocked.Read(ref observedCalls) +
                ", arrayInputs=" + Interlocked.Read(ref arrayInputs) +
                ", mediumSets=" + Interlocked.Read(ref mediumSets) +
                ", refined=" + refined +
                ", clusteredSkips=" + Interlocked.Read(ref clusteredSkips) +
                ", candidatesRefined=" + candidates +
                ", avgCandidates=" + avg.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidates) +
                ", ringSize=" + FineRingSize +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Candidate membership is unchanged; Vanilla Reachability/validator/final selection remains authoritative.";
        }
    }
}
