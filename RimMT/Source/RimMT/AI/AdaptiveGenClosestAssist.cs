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
    // V0.4.12 policy: stutter reduction is more important than average TPS.
    //
    // V0.4.11 proved that pressure-aware candidate ordering is safe, but its broad
    // main-thread materialization and foreground worker spin could add variance. 0.4.12
    // therefore becomes deliberately conservative:
    //   * Low pressure: never reorder.
    //   * Normal pressure: only very large counted lists are considered.
    //   * High/Critical pressure: large IList-backed candidate sets may be reordered.
    //   * Unknown IEnumerable/ICollection shapes are never materialized by RimMT.
    //   * No foreground worker, no SpinWait, no wait budget. The main thread never waits.
    //   * A hard micro-budget and cooldown abort the assist if RimMT itself becomes slow.
    //
    // Candidate membership is unchanged. Vanilla still owns Reachability.CanReach,
    // WorkGiver validators, reservations and final Job selection.
    internal static class AdaptiveGenClosestAssist
    {
        private const string FeatureId = "parallel.jobPartition";

        private const int ThresholdNormal = 512;
        private const int ThresholdHigh = 256;
        private const int ThresholdCritical = 192;

        private const int RingNormal = 16;
        private const int RingHigh = 12;
        private const int RingCritical = 8;

        // This feature is allowed to spend only a fraction of a millisecond before it
        // abandons the current assist and leaves Vanilla input untouched.
        private const double AssistBudgetMs = 0.75;
        private const double SlowTripMs = 1.00;
        private const double CooldownSeconds = 2.0;
        private const int BudgetCheckMask = 31; // check every 32 candidates

        private static volatile bool compatibilityReady;
        private static long suppressUntilTimestamp;

        [ThreadStatic]
        private static int assistDepth;

        [ThreadStatic]
        private static int[] ringKeysScratch;

        [ThreadStatic]
        private static int[] ringOffsetsScratch;

        private static long observedCalls;
        private static long supportedCalls;
        private static long reorderedCalls;
        private static long listInputs;
        private static long lowPressureBypasses;
        private static long normalThresholdBypasses;
        private static long highThresholdBypasses;
        private static long criticalThresholdBypasses;
        private static long nonListBypasses;
        private static long haulableBypasses;
        private static long unsupportedShapeFallbacks;
        private static long nullOrInvalidFallbacks;
        private static long cooldownBypasses;
        private static long reentrantBypasses;
        private static long budgetAborts;
        private static long slowTrips;
        private static long scratchGrowths;

        private static long pressureLowCalls;
        private static long pressureNormalCalls;
        private static long pressureHighCalls;
        private static long pressureCriticalCalls;

        private static long candidatesSeen;
        private static long candidatesReordered;
        private static long maxCandidateCount;
        private static long reorderTicksTotal;
        private static long reorderTicksMax;
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
                    FeatureGate.Suppress(FeatureId, "GenClosest.ClosestThingReachable target not found");
                    Log.Warning("[RimMT] parallel.jobPartition V0.4.12 unavailable: GenClosest.ClosestThingReachable target not found.");
                    return;
                }

                CompatibilityGuard.RegisterTarget(FeatureId, target);
                HarmonyMethod prefix = new HarmonyMethod(typeof(AdaptiveGenClosestAssist), nameof(Prefix));
                prefix.priority = Priority.First + 50;
                harmony.Patch(target, prefix: prefix);
                Log.Message("[RimMT] parallel.jobPartition V0.4.12 installed in stutter-first mode. Low-pressure calls stay Vanilla; unknown enumerables are never materialized; no foreground worker wait/spin is used; High/Critical large IList searches may receive bounded nearest-first ordering. Vanilla Reachability/validator/final selection remain authoritative.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "stutter-first GenClosest assist patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.12 patch failed; Vanilla candidate order remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            compatibilityReady = true;
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

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
            {
                Interlocked.Increment(ref nullOrInvalidFallbacks);
                return;
            }

            // Keep the whitelist narrow. Region-limited searches have different traversal
            // and termination semantics and are not touched by this feature.
            if (!thingReq.IsUndefined || traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || (!(searchRegionsMax < 0) && !forceAllowGlobalSearch))
            {
                Interlocked.Increment(ref unsupportedShapeFallbacks);
                return;
            }

            LoadPressure pressure = AdaptiveLoadBalancer.Pressure;
            RecordPressure(pressure);

            // Stutter-first: never add ordering work while the game is already smooth.
            if (pressure == LoadPressure.Low)
            {
                Interlocked.Increment(ref lowPressureBypasses);
                return;
            }

            long now = Stopwatch.GetTimestamp();
            if (now < Interlocked.Read(ref suppressUntilTimestamp))
            {
                Interlocked.Increment(ref cooldownBypasses);
                return;
            }

            // Only IList-backed inputs are cheap enough to inspect without materializing an
            // unknown iterator. Arrays and List<Thing> both satisfy this path.
            IList<Thing> things = customGlobalSearchSet as IList<Thing>;
            if (things == null)
            {
                Interlocked.Increment(ref nonListBypasses);
                return;
            }
            Interlocked.Increment(ref listInputs);

            int count;
            try
            {
                count = things.Count;
            }
            catch
            {
                Interlocked.Increment(ref nullOrInvalidFallbacks);
                return;
            }

            Interlocked.Add(ref candidatesSeen, count);
            UpdateMax(ref maxCandidateCount, count);

            int threshold = ThresholdFor(pressure);
            if (count < threshold)
            {
                RecordThresholdBypass(pressure);
                return;
            }

            // Exact hauling paths have their own stronger V0.4.6/V0.4.7 accelerators.
            List<Thing> haulables = map.listerHaulables == null ? null : map.listerHaulables.ThingsPotentiallyNeedingHauling();
            if (haulables != null && ReferenceEquals(customGlobalSearchSet, haulables))
            {
                Interlocked.Increment(ref haulableBypasses);
                return;
            }

            if (assistDepth != 0)
            {
                Interlocked.Increment(ref reentrantBypasses);
                return;
            }

            assistDepth = 1;
            long started = Stopwatch.GetTimestamp();
            try
            {
                Thing[] ordered;
                if (!TryBuildStableRingPartition(things, count, root, map, RingSizeFor(pressure), started, out ordered))
                    return;

                // Commit only after the bounded calculation is completely successful.
                customGlobalSearchSet = ordered;
                Interlocked.Increment(ref supportedCalls);
                Interlocked.Increment(ref reorderedCalls);
                Interlocked.Add(ref candidatesReordered, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
                Log.Warning("[RimMT] parallel.jobPartition V0.4.12 runtime failure; this call keeps Vanilla candidate order. " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                long elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref reorderTicksTotal, elapsed);
                UpdateMax(ref reorderTicksMax, elapsed);

                double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
                if (elapsedMs >= SlowTripMs)
                {
                    Interlocked.Increment(ref slowTrips);
                    long cooldownTicks = Math.Max(1L, (long)(Stopwatch.Frequency * CooldownSeconds));
                    Interlocked.Exchange(ref suppressUntilTimestamp, Stopwatch.GetTimestamp() + cooldownTicks);
                }

                assistDepth = 0;
            }
        }

        private static bool TryBuildStableRingPartition(
            IList<Thing> source,
            int count,
            IntVec3 root,
            Map map,
            int ringSize,
            long started,
            out Thing[] result)
        {
            result = null;
            if (count <= 0)
                return false;

            EnsureScratch(ref ringKeysScratch, count);
            int maxRing = 0;

            for (int i = 0; i < count; i++)
            {
                Thing thing = source[i];
                if (thing == null)
                {
                    Interlocked.Increment(ref nullOrInvalidFallbacks);
                    return false;
                }

                IntVec3 pos = thing.PositionHeld;
                if (!pos.IsValid || !pos.InBounds(map))
                {
                    Interlocked.Increment(ref nullOrInvalidFallbacks);
                    return false;
                }

                int dx = Math.Abs(pos.x - root.x);
                int dz = Math.Abs(pos.z - root.z);
                int ring = Math.Max(dx, dz) / ringSize;
                ringKeysScratch[i] = ring;
                if (ring > maxRing)
                    maxRing = ring;

                if ((i & BudgetCheckMask) == 0 && ExceededAssistBudget(started))
                {
                    Interlocked.Increment(ref budgetAborts);
                    return false;
                }
            }

            int ringCount = maxRing + 1;
            EnsureScratch(ref ringOffsetsScratch, ringCount);
            Array.Clear(ringOffsetsScratch, 0, ringCount);

            for (int i = 0; i < count; i++)
                ringOffsetsScratch[ringKeysScratch[i]]++;

            int sum = 0;
            for (int ring = 0; ring < ringCount; ring++)
            {
                int n = ringOffsetsScratch[ring];
                ringOffsetsScratch[ring] = sum;
                sum += n;
            }

            if (ExceededAssistBudget(started))
            {
                Interlocked.Increment(ref budgetAborts);
                return false;
            }

            Thing[] ordered = new Thing[count];
            for (int i = 0; i < count; i++)
            {
                int ring = ringKeysScratch[i];
                ordered[ringOffsetsScratch[ring]++] = source[i];

                if ((i & BudgetCheckMask) == 0 && ExceededAssistBudget(started))
                {
                    Interlocked.Increment(ref budgetAborts);
                    return false;
                }
            }

            result = ordered;
            return true;
        }

        private static bool ExceededAssistBudget(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            return elapsed * 1000.0 / Stopwatch.Frequency >= AssistBudgetMs;
        }

        private static void EnsureScratch(ref int[] buffer, int required)
        {
            if (buffer != null && buffer.Length >= required)
                return;

            int size = 64;
            while (size < required && size < 65536)
                size <<= 1;
            if (size < required)
                size = required;

            buffer = new int[size];
            Interlocked.Increment(ref scratchGrowths);
        }

        private static int ThresholdFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Critical: return ThresholdCritical;
                case LoadPressure.High: return ThresholdHigh;
                case LoadPressure.Normal: return ThresholdNormal;
                default: return int.MaxValue;
            }
        }

        private static int RingSizeFor(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Critical: return RingCritical;
                case LoadPressure.High: return RingHigh;
                default: return RingNormal;
            }
        }

        private static void RecordThresholdBypass(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Critical:
                    Interlocked.Increment(ref criticalThresholdBypasses);
                    break;
                case LoadPressure.High:
                    Interlocked.Increment(ref highThresholdBypasses);
                    break;
                default:
                    Interlocked.Increment(ref normalThresholdBypasses);
                    break;
            }
        }

        private static void RecordPressure(LoadPressure pressure)
        {
            switch (pressure)
            {
                case LoadPressure.Low:
                    Interlocked.Increment(ref pressureLowCalls);
                    break;
                case LoadPressure.High:
                    Interlocked.Increment(ref pressureHighCalls);
                    break;
                case LoadPressure.Critical:
                    Interlocked.Increment(ref pressureCriticalCalls);
                    break;
                default:
                    Interlocked.Increment(ref pressureNormalCalls);
                    break;
            }
        }

        private static void UpdateMax(ref long field, long value)
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
            long reordered = Interlocked.Read(ref reorderedCalls);
            long candidates = Interlocked.Read(ref candidatesReordered);
            double avgCandidates = reordered <= 0 ? 0.0 : candidates / (double)reordered;
            long totalTicks = Interlocked.Read(ref reorderTicksTotal);
            double avgUs = reordered <= 0 ? 0.0 : (totalTicks * 1000000.0 / Stopwatch.Frequency) / reordered;
            double maxUs = Interlocked.Read(ref reorderTicksMax) * 1000000.0 / Stopwatch.Frequency;
            LoadPressure current = AdaptiveLoadBalancer.Pressure;

            return "Adaptive GenClosest stutter guard V0.4.12: compatibilityReady=" + compatibilityReady +
                ", currentPressure=" + current +
                ", currentThreshold=" + (current == LoadPressure.Low ? "OFF" : ThresholdFor(current).ToString()) +
                ", currentRing=" + RingSizeFor(current) +
                ", observed=" + Interlocked.Read(ref observedCalls) +
                ", supported=" + Interlocked.Read(ref supportedCalls) +
                ", reordered=" + reordered +
                ", listInputs=" + Interlocked.Read(ref listInputs) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypasses) +
                ", lowPressureBypass=" + Interlocked.Read(ref lowPressureBypasses) +
                ", thresholdBypass(N/H/C)=" + Interlocked.Read(ref normalThresholdBypasses) + "/" +
                    Interlocked.Read(ref highThresholdBypasses) + "/" + Interlocked.Read(ref criticalThresholdBypasses) +
                ", haulableBypass=" + Interlocked.Read(ref haulableBypasses) +
                ", unsupportedShape=" + Interlocked.Read(ref unsupportedShapeFallbacks) +
                ", invalid=" + Interlocked.Read(ref nullOrInvalidFallbacks) +
                ", cooldownBypass=" + Interlocked.Read(ref cooldownBypasses) +
                ", reentrantBypass=" + Interlocked.Read(ref reentrantBypasses) +
                ", budgetAbort=" + Interlocked.Read(ref budgetAborts) +
                ", slowTrip=" + Interlocked.Read(ref slowTrips) +
                ", scratchGrowths=" + Interlocked.Read(ref scratchGrowths) +
                ", pressureCalls(L/N/H/C)=" + Interlocked.Read(ref pressureLowCalls) + "/" +
                    Interlocked.Read(ref pressureNormalCalls) + "/" + Interlocked.Read(ref pressureHighCalls) + "/" +
                    Interlocked.Read(ref pressureCriticalCalls) +
                ", candidatesSeen=" + Interlocked.Read(ref candidatesSeen) +
                ", candidatesReordered=" + candidates +
                ", avgCandidates=" + avgCandidates.ToString("F1") +
                ", maxCandidates=" + Interlocked.Read(ref maxCandidateCount) +
                ", avgAssistUs=" + avgUs.ToString("F2") +
                ", maxAssistUs=" + maxUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Stutter-first policy: no Low-pressure reordering, no unknown-enumerable materialization, no foreground worker wait/spin; candidate membership is unchanged and Vanilla GenClosest/Reachability/validator/final selection remains authoritative.";
        }
    }
}
