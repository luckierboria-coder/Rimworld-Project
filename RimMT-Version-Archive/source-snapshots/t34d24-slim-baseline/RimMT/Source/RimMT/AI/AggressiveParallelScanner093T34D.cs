using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T34-D.1 deliberately executes live WorkGiver validators on workers.
    /// This is the aggressive branch requested for finding the real scaling ceiling. Unlike the
    /// earlier snapshot fabrics, the caller waits for the current scan and consumes its result.
    /// </summary>
    internal static class AggressiveParallelScanner093T34D
    {
        internal const string FeatureId = "parallel.aggressiveScanner";

        private const int MinCandidates = 48;
        private const int MaxPartitions = 8;
        private const double SlowWaitQuarantineMs = 8.0;

        private static bool installed;
        private static long globalCalls;
        private static long reachableCalls;
        private static long candidates;
        private static long workerBatches;
        private static long mainThreadBatches;
        private static long validatorCalls;
        private static long reachabilityCalls;
        private static long acceptedCandidates;
        private static long enqueueFallbacks;
        private static long failures;
        private static long fallbackAfterFailure;
        private static long unresolvedScannerBypass;
        private static long defaultJobOnThingBypass;
        private static long quarantinedScannerBypass;
        private static long nestedReachabilityAborts;
        private static long mapPawnCacheAborts;
        private static long waitTicks;
        private static long maxWaitTicks;
        private static long slowWaitQuarantines;
        [ThreadStatic] private static int workerValidatorDepth;
        private static readonly ConcurrentDictionary<Type, byte> QuarantinedScanners =
            new ConcurrentDictionary<Type, byte>();

        internal static void Apply(Harmony harmony)
        {
            try
            {
                MethodBase global = AccessTools.Method(
                    typeof(GenClosest), "ClosestThing_Global_NewTemp",
                    new Type[]
                    {
                        typeof(IntVec3), typeof(IEnumerable), typeof(float),
                        typeof(Predicate<Thing>), typeof(Func<Thing, float>), typeof(bool)
                    });
                MethodBase reachable = AccessTools.Method(
                    typeof(GenClosest), "ClosestThing_Global_Reachable_NewTemp",
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(IEnumerable<Thing>), typeof(PathEndMode),
                        typeof(TraverseParms), typeof(float), typeof(Predicate<Thing>),
                        typeof(Func<Thing, float>), typeof(bool)
                    });
                MethodBase broadReachable = AccessTools.Method(
                    typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode),
                        typeof(TraverseParms), typeof(float), typeof(Predicate<Thing>),
                        typeof(IEnumerable<Thing>), typeof(int), typeof(int), typeof(bool),
                        typeof(RegionType), typeof(bool)
                    });
                MethodBase reachability = AccessTools.Method(
                    typeof(Reachability), nameof(Reachability.CanReach),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode),
                        typeof(TraverseParms)
                    });
                MethodBase spawnedColonyAnimals = AccessTools.PropertyGetter(
                    typeof(MapPawns), nameof(MapPawns.SpawnedColonyAnimals));
                if (global == null || reachable == null || broadReachable == null ||
                    reachability == null || spawnedColonyAnimals == null)
                    throw new MissingMethodException("GenClosest worker targets not found");

                HarmonyMethod globalPrefix = new HarmonyMethod(
                    typeof(AggressiveParallelScanner093T34D), nameof(GlobalPrefix));
                globalPrefix.priority = Priority.First + 600;
                HarmonyMethod reachablePrefix = new HarmonyMethod(
                    typeof(AggressiveParallelScanner093T34D), nameof(ReachablePrefix));
                reachablePrefix.priority = Priority.First + 600;
                HarmonyMethod broadReachablePrefix = new HarmonyMethod(
                    typeof(AggressiveParallelScanner093T34D), nameof(BroadReachablePrefix));
                broadReachablePrefix.priority = Priority.First + 600;
                HarmonyMethod reachabilityGuard = new HarmonyMethod(
                    typeof(AggressiveParallelScanner093T34D), nameof(ReachabilityWorkerGuardPrefix));
                reachabilityGuard.priority = Priority.First + 1000;
                HarmonyMethod mapPawnCacheGuard = new HarmonyMethod(
                    typeof(AggressiveParallelScanner093T34D), nameof(MapPawnCacheWorkerGuardPrefix));
                mapPawnCacheGuard.priority = Priority.First + 1000;

                harmony.Patch(global, prefix: globalPrefix);
                harmony.Patch(reachable, prefix: reachablePrefix);
                harmony.Patch(broadReachable, prefix: broadReachablePrefix);
                harmony.Patch(reachability, prefix: reachabilityGuard);
                harmony.Patch(spawnedColonyAnimals, prefix: mapPawnCacheGuard);
                installed = true;
                Log.Message("[RimMT] T34-D.1.3 map-pawn cache guard installed: overridden HasJobOnThing validators execute on workers without T20 bookkeeping; Reachability and MapPawns.SpawnedColonyAnimals are blocked on workers before shared state mutation and force scanner quarantine.");
            }
            catch (Exception ex)
            {
                installed = false;
                FeatureGate.Suppress(FeatureId, "T34-D install failed: " + ex.GetType().Name);
                Log.Error("[RimMT] T34-D aggressive parallel scanner install failed: " + ex);
            }
        }

        public static void ReachabilityWorkerGuardPrefix()
        {
            if (workerValidatorDepth <= 0)
                return;
            Interlocked.Increment(ref nestedReachabilityAborts);
            throw new WorkerUnsafeApiException("Reachability.CanReach entered from a T34-D worker validator");
        }

        public static void MapPawnCacheWorkerGuardPrefix()
        {
            if (workerValidatorDepth <= 0)
                return;
            Interlocked.Increment(ref mapPawnCacheAborts);
            throw new WorkerUnsafeApiException(
                "MapPawns.SpawnedColonyAnimals entered from a T34-D worker validator");
        }

        public static bool GlobalPrefix(
            IntVec3 center,
            IEnumerable searchSet,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool lookInHaulSources,
            ref Thing __result)
        {
            if (!CanRun() || lookInHaulSources)
                return true;

            IList<Thing> list = TryGetList(searchSet);
            if (list == null || list.Count < MinCandidates)
                return true;

            WorkGiver_Scanner scanner;
            if (!CanParallelizeValidator(validator, out scanner))
                return true;

            Interlocked.Increment(ref globalCalls);
            Thing result;
            if (!TryRun(list, center, null, PathEndMode.None, default(TraverseParms),
                maxDistance, validator, priorityGetter, false, scanner, out result))
                return true;
            __result = result;
            return false;
        }

        public static bool ReachablePrefix(
            IntVec3 center,
            Map map,
            IEnumerable<Thing> searchSet,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool canLookInHaulableSources,
            ref Thing __result)
        {
            if (!CanRun() || canLookInHaulableSources || map == null || map.Disposed)
                return true;

            IList<Thing> list = TryGetList(searchSet);
            if (list == null || list.Count < MinCandidates)
                return true;

            WorkGiver_Scanner scanner;
            if (!CanParallelizeValidator(validator, out scanner))
                return true;

            Interlocked.Increment(ref reachableCalls);
            Thing result;
            if (!TryRun(list, center, map, peMode, traverseParams,
                maxDistance, validator, priorityGetter, true, scanner, out result))
                return true;
            __result = result;
            return false;
        }

        public static bool BroadReachablePrefix(
            IntVec3 root,
            Map map,
            ThingRequest thingReq,
            PathEndMode peMode,
            TraverseParms traverseParams,
            float maxDistance,
            Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet,
            ref Thing __result)
        {
            if (!CanRun() || map == null || map.Disposed)
                return true;

            object source = customGlobalSearchSet;
            if (source == null)
            {
                try { source = map.listerThings.ThingsMatching(thingReq); }
                catch { return true; }
            }

            IList<Thing> list = TryGetList(source);
            if (list == null || list.Count < MinCandidates)
                return true;

            WorkGiver_Scanner scanner;
            if (!CanParallelizeValidator(validator, out scanner))
                return true;

            Interlocked.Increment(ref reachableCalls);
            Thing result;
            if (!TryRun(list, root, map, peMode, traverseParams,
                maxDistance, validator, null, true, scanner, out result))
                return true;
            __result = result;
            return false;
        }

        private static bool CanParallelizeValidator(
            Predicate<Thing> validator,
            out WorkGiver_Scanner scanner)
        {
            scanner = ResolveScanner(validator);
            if (scanner == null)
            {
                Interlocked.Increment(ref unresolvedScannerBypass);
                return false;
            }

            Type scannerType = scanner.GetType();
            if (QuarantinedScanners.ContainsKey(scannerType))
            {
                Interlocked.Increment(ref quarantinedScannerBypass);
                return false;
            }

            MethodInfo hasJob = AccessTools.Method(scannerType, "HasJobOnThing",
                new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
            if (hasJob == null || hasJob.DeclaringType == typeof(WorkGiver_Scanner))
            {
                // The base implementation calls JobOnThing. That creates Jobs and touches many
                // shared caches, as demonstrated by the first T34-D playtest failures.
                Interlocked.Increment(ref defaultJobOnThingBypass);
                return false;
            }
            return true;
        }

        private static WorkGiver_Scanner ResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null || validator.Target == null)
                return null;
            try
            {
                object target = validator.Target;
                FieldInfo[] fields = target.GetType().GetFields(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (!typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                        continue;
                    WorkGiver_Scanner scanner = field.GetValue(target) as WorkGiver_Scanner;
                    if (scanner != null)
                        return scanner;
                }
            }
            catch { }
            return null;
        }

        private static IList<Thing> TryGetList(object source)
        {
            IList<Thing> things = source as IList<Thing>;
            if (things != null)
                return things;

            IList<Pawn> pawns = source as IList<Pawn>;
            if (pawns != null)
            {
                Thing[] copied = new Thing[pawns.Count];
                for (int i = 0; i < copied.Length; i++) copied[i] = pawns[i];
                return copied;
            }

            IList<Building> buildings = source as IList<Building>;
            if (buildings != null)
            {
                Thing[] copied = new Thing[buildings.Count];
                for (int i = 0; i < copied.Length; i++) copied[i] = buildings[i];
                return copied;
            }

            IList<IAttackTarget> targets = source as IList<IAttackTarget>;
            if (targets != null)
            {
                Thing[] copied = new Thing[targets.Count];
                for (int i = 0; i < copied.Length; i++) copied[i] = targets[i] as Thing;
                return copied;
            }
            return null;
        }

        private static bool CanRun()
        {
            return installed && FeatureGate.IsEnabled(FeatureId) &&
                RimMTThreadGuard.IsMainThread && Current.ProgramState == ProgramState.Playing &&
                JobGiverGlobalNearest04181.InJobGiverScope && RimMTRuntime.Scheduler != null;
        }

        private static bool TryRun(
            IList<Thing> list,
            IntVec3 center,
            Map map,
            PathEndMode peMode,
            TraverseParms traverseParms,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool requireReachability,
            WorkGiver_Scanner scanner,
            out Thing chosen)
        {
            chosen = null;
            int count = list.Count;
            Interlocked.Add(ref candidates, count);

            CandidateResult[] results = new CandidateResult[count];
            int partitions = Math.Min(Math.Min(MaxPartitions, RimMTRuntime.Scheduler.WorkerCount),
                Math.Max(1, count / MinCandidates));
            int batchSize = (count + partitions - 1) / partitions;
            CountdownEvent done = new CountdownEvent(partitions);
            Exception firstFailure = null;

            for (int partition = 0; partition < partitions; partition++)
            {
                int from = partition * batchSize;
                int to = Math.Min(count, from + batchSize);
                Action action = delegate
                {
                    try
                    {
                        workerValidatorDepth++;
                        EvaluateRange(list, results, from, to, center, map, peMode,
                            traverseParms, maxDistance, validator, priorityGetter, requireReachability);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.CompareExchange(ref firstFailure, ex, null);
                    }
                    finally
                    {
                        if (workerValidatorDepth > 0)
                            workerValidatorDepth--;
                        done.Signal();
                    }
                };

                if (RimMTRuntime.Scheduler.TryEnqueue(FeatureId, JobPriority.High, action))
                {
                    Interlocked.Increment(ref workerBatches);
                }
                else
                {
                    Interlocked.Increment(ref enqueueFallbacks);
                    Interlocked.Increment(ref mainThreadBatches);
                    action();
                }
            }

            long waitStarted = Stopwatch.GetTimestamp();
            done.Wait();
            long elapsed = Stopwatch.GetTimestamp() - waitStarted;
            if (elapsed > 0)
            {
                Interlocked.Add(ref waitTicks, elapsed);
                UpdateMax(ref maxWaitTicks, elapsed);
            }
            done.Dispose();

            double elapsedMs = elapsed * 1000.0 / Stopwatch.Frequency;
            if (scanner != null && elapsedMs > SlowWaitQuarantineMs)
            {
                Type scannerType = scanner.GetType();
                if (QuarantinedScanners.TryAdd(scannerType, 0))
                    Interlocked.Increment(ref slowWaitQuarantines);
            }

            if (firstFailure != null)
            {
                Interlocked.Increment(ref failures);
                Interlocked.Increment(ref fallbackAfterFailure);
                if (scanner != null)
                    QuarantinedScanners.TryAdd(scanner.GetType(), 0);
                return false;
            }

            float closestDistance = 2.1474836E+09f;
            float bestPriority = float.MinValue;
            for (int i = 0; i < count; i++)
            {
                CandidateResult candidate = results[i];
                if (!candidate.Accepted)
                    continue;

                if (requireReachability)
                {
                    Interlocked.Increment(ref reachabilityCalls);
                    if (!map.reachability.CanReach(center, candidate.Thing.SpawnedParentOrMe,
                        peMode, traverseParms))
                        continue;
                }

                if (priorityGetter != null)
                {
                    float priority = priorityGetter(candidate.Thing);
                    if (priority < bestPriority ||
                        (Mathf.Approximately(priority, bestPriority) &&
                         candidate.DistanceSquared >= closestDistance))
                        continue;
                    bestPriority = priority;
                }
                else if (candidate.DistanceSquared >= closestDistance)
                {
                    continue;
                }

                chosen = candidate.Thing;
                closestDistance = candidate.DistanceSquared;
            }
            return true;
        }

        private static void EvaluateRange(
            IList<Thing> list,
            CandidateResult[] results,
            int from,
            int to,
            IntVec3 center,
            Map map,
            PathEndMode peMode,
            TraverseParms traverseParms,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool requireReachability)
        {
            float maxDistanceSquared = maxDistance * maxDistance;
            for (int i = from; i < to; i++)
            {
                Thing thing = list[i];
                if (thing == null)
                    continue;
                if (requireReachability)
                {
                    if (!thing.Spawned)
                        continue;
                }
                else if (!thing.Spawned && !HaulAIUtility.IsInHaulableInventory(thing))
                {
                    continue;
                }

                float distance = (center - thing.PositionHeld).LengthHorizontalSquared;
                if (distance > maxDistanceSquared)
                    continue;

                if (validator != null)
                {
                    Interlocked.Increment(ref validatorCalls);
                    if (!validator(thing))
                        continue;
                }

                results[i] = new CandidateResult(thing, distance);
                Interlocked.Increment(ref acceptedCandidates);
            }
        }

        private static void UpdateMax(ref long target, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref target)))
            {
                if (Interlocked.CompareExchange(ref target, value, seen) == seen)
                    break;
            }
        }

        internal static string Summary()
        {
            long calls = Interlocked.Read(ref globalCalls) + Interlocked.Read(ref reachableCalls);
            double averageWaitMs = calls == 0 ? 0.0 :
                Interlocked.Read(ref waitTicks) * 1000.0 / Stopwatch.Frequency / calls;
            double maximumWaitMs = Interlocked.Read(ref maxWaitTicks) * 1000.0 / Stopwatch.Frequency;
            return "T34-D.1.3 map-pawn cache guard: installed=" + installed +
                ", calls[global/reachable]=" + Interlocked.Read(ref globalCalls) + "/" +
                Interlocked.Read(ref reachableCalls) +
                ", candidates=" + Interlocked.Read(ref candidates) +
                ", batches[worker/main]=" + Interlocked.Read(ref workerBatches) + "/" +
                Interlocked.Read(ref mainThreadBatches) +
                ", live[validatorWorker/reachMain/accepted]=" + Interlocked.Read(ref validatorCalls) + "/" +
                Interlocked.Read(ref reachabilityCalls) + "/" + Interlocked.Read(ref acceptedCandidates) +
                ", enqueueFallbacks=" + Interlocked.Read(ref enqueueFallbacks) +
                ", failures/fallbacks=" + Interlocked.Read(ref failures) + "/" +
                Interlocked.Read(ref fallbackAfterFailure) +
                ", bypass[unresolved/defaultJobOnThing/quarantined]=" +
                Interlocked.Read(ref unresolvedScannerBypass) + "/" +
                Interlocked.Read(ref defaultJobOnThingBypass) + "/" +
                Interlocked.Read(ref quarantinedScannerBypass) +
                ", quarantinedTypes=" + QuarantinedScanners.Count +
                ", nestedReachabilityAborts=" + Interlocked.Read(ref nestedReachabilityAborts) +
                ", mapPawnCacheAborts=" + Interlocked.Read(ref mapPawnCacheAborts) +
                ", slowWaitQuarantines=" + Interlocked.Read(ref slowWaitQuarantines) +
                ", waitMs[avg/max]=" + averageWaitMs.ToString("F3") + "/" +
                maximumWaitMs.ToString("F3") +
                ", slowWaitLimitMs=" + SlowWaitQuarantineMs.ToString("F1") +
                ". Overridden WorkGiver validators execute on workers; Reachability and priority execute on the main thread; failed or slow-wait scanner types fall back and remain quarantined.";
        }

        private sealed class WorkerUnsafeApiException : InvalidOperationException
        {
            internal WorkerUnsafeApiException(string message) : base(message) { }
        }

        private struct CandidateResult
        {
            internal readonly Thing Thing;
            internal readonly float DistanceSquared;
            internal readonly bool Accepted;

            internal CandidateResult(Thing thing, float distanceSquared)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                Accepted = true;
            }
        }
    }
}
