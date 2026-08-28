using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class PathSnapshotSafetyPatches
    {
        // V0.4.17.2 produced only 37 paired validations in ~15 minutes. V0.4.18 raises the
        // evidence budget while keeping shadow pathing strictly non-authoritative. Low-load
        // frames sample more aggressively; High/Critical frames still admit no shadow work.
        private const int ValidationQuota = 512;
        private const int NormalSampleEveryEligible = 4;
        private const int LowSampleEveryEligible = 2;
        private const int NormalMaxValidationDistance = 96;
        private const int LowMaxValidationDistance = 128;
        private const int NormalMaxConcurrent = 1;
        private const int LowMaxConcurrent = 2;

        private static readonly Type RequestType = typeof(PathSnapshotWorker).GetNestedType("PathRequest", BindingFlags.NonPublic);
        private static readonly Type SnapshotType = typeof(PathSnapshotWorker).GetNestedType("PathSnapshot", BindingFlags.NonPublic);
        private static readonly Type WorkerResultType = typeof(PathSnapshotWorker).GetNestedType("WorkerResult", BindingFlags.NonPublic);

        private static readonly FieldInfo SnapshotField = RequestType == null ? null : RequestType.GetField("Snapshot", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkerField = RequestType == null ? null : RequestType.GetField("Worker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo HasWorkerField = RequestType == null ? null : RequestType.GetField("HasWorker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo GenerationField = SnapshotType == null ? null : SnapshotType.GetField("Generation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo StaleField = WorkerResultType == null ? null : WorkerResultType.GetField("Stale", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static long lateStaleCorrections;
        private static long probeFailures;
        private static long eligibleScheduleCalls;
        private static long lowPressureEligible;
        private static long normalPressureEligible;
        private static long cadenceSkipped;
        private static long distanceBudgetSkipped;
        private static long pressureSkipped;
        private static long concurrencySkipped;
        private static long quotaSkipped;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            ApplyScheduleBudget(harmony);
            ApplyFinalizeSafety(harmony);
        }

        private static void ApplyScheduleBudget(Harmony harmony)
        {
            MethodBase schedule = AccessTools.Method(typeof(PathSnapshotWorker), "TrySchedule");
            if (schedule == null)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot V0.4.18 schedule budget unavailable; legacy shadow scheduling remains.");
                return;
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(PathSnapshotSafetyPatches), nameof(ScheduleBudgetPrefix));
                prefix.priority = Priority.First;
                harmony.Patch(schedule, prefix: prefix);
                Log.Message("[RimMT] parallel.pathSnapshot V0.4.18 adaptive parity campaign active: quota=" + ValidationQuota +
                    ", low(sampleEvery=" + LowSampleEveryEligible + ", maxDistance=" + LowMaxValidationDistance + ", maxConcurrent=" + LowMaxConcurrent + ")" +
                    ", normal(sampleEvery=" + NormalSampleEveryEligible + ", maxDistance=" + NormalMaxValidationDistance + ", maxConcurrent=" + NormalMaxConcurrent + ")" +
                    ", high/critical admission=off. Vanilla pathing is never skipped.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot V0.4.18 schedule budget patch failed; legacy shadow scheduling remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void ApplyFinalizeSafety(Harmony harmony)
        {
            MethodBase finalize = AccessTools.Method(typeof(PathSnapshotWorker), "TryFinalize");
            if (finalize == null || RequestType == null || SnapshotField == null || WorkerField == null || HasWorkerField == null || GenerationField == null || StaleField == null)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot finalize-generation safety probe unavailable; existing worker-time stale check remains active.");
                return;
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(PathSnapshotSafetyPatches), nameof(FinalizePrefix));
                prefix.priority = Priority.First;
                harmony.Patch(finalize, prefix: prefix);
                Log.Message("[RimMT] parallel.pathSnapshot finalize-generation recheck active. Topology changes after worker completion are marked stale before paired validation finalizes.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot finalize-generation safety patch failed; existing worker-time stale check remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // This prefix only decides whether RimMT's shadow validation is worth the extra CPU.
        // Returning false skips TrySchedule itself, NOT Vanilla PathFinder.FindPath.
        public static bool ScheduleBudgetPrefix(object[] __args, ref int __result)
        {
            if (!FeatureGate.IsEnabled("parallel.pathSnapshot"))
                return true;

            if (PathSnapshotWorker.Completed >= ValidationQuota)
            {
                Interlocked.Increment(ref quotaSkipped);
                __result = 0;
                return false;
            }

            LoadPressure pressure = FeatureGate.IsEnabled("runtime.adaptiveBurst")
                ? AdaptiveLoadBalancer.Pressure
                : LoadPressure.Normal;
            if (pressure == LoadPressure.High || pressure == LoadPressure.Critical)
            {
                Interlocked.Increment(ref pressureSkipped);
                __result = 0;
                return false;
            }

            bool low = pressure == LoadPressure.Low;
            int maxConcurrent = low ? LowMaxConcurrent : NormalMaxConcurrent;
            if (PathSnapshotWorker.InFlight >= maxConcurrent)
            {
                Interlocked.Increment(ref concurrencySkipped);
                __result = 0;
                return false;
            }

            object[] pathArgs = __args != null && __args.Length > 1 ? __args[1] as object[] : null;
            if (pathArgs == null || pathArgs.Length < 4 || !(pathArgs[2] is TraverseParms))
                return true;

            try
            {
                IntVec3 start = (IntVec3)pathArgs[0];
                LocalTargetInfo dest = (LocalTargetInfo)pathArgs[1];
                if (start.IsValid && dest.IsValid)
                {
                    int dx = Math.Abs(start.x - dest.Cell.x);
                    int dz = Math.Abs(start.z - dest.Cell.z);
                    int maxDistance = low ? LowMaxValidationDistance : NormalMaxValidationDistance;
                    if (Math.Max(dx, dz) > maxDistance)
                    {
                        Interlocked.Increment(ref distanceBudgetSkipped);
                        __result = 0;
                        return false;
                    }
                }
            }
            catch
            {
                return true; // Let the original fail-closed argument checks own malformed requests.
            }

            long sequence = Interlocked.Increment(ref eligibleScheduleCalls);
            if (low)
                Interlocked.Increment(ref lowPressureEligible);
            else
                Interlocked.Increment(ref normalPressureEligible);

            int sampleEvery = low ? LowSampleEveryEligible : NormalSampleEveryEligible;
            if ((sequence % sampleEvery) != 0)
            {
                Interlocked.Increment(ref cadenceSkipped);
                __result = 0;
                return false;
            }

            return true;
        }

        // Use __args rather than naming the private nested PathRequest type in the Harmony signature.
        public static void FinalizePrefix(object[] __args)
        {
            object request = __args == null || __args.Length == 0 ? null : __args[0];
            if (request == null || !RimMTThreadGuard.IsMainThread)
                return;

            try
            {
                object hasWorkerValue = HasWorkerField.GetValue(request);
                if (!(hasWorkerValue is bool) || !(bool)hasWorkerValue)
                    return;

                object snapshot = SnapshotField.GetValue(request);
                if (snapshot == null)
                    return;

                int generation = (int)GenerationField.GetValue(snapshot);
                if (generation == ReachabilityNoCache.TopologyGeneration)
                    return;

                object worker = WorkerField.GetValue(request);
                if (worker == null)
                    return;

                bool alreadyStale = (bool)StaleField.GetValue(worker);
                if (alreadyStale)
                    return;

                StaleField.SetValue(worker, true);
                WorkerField.SetValue(request, worker);
                Interlocked.Increment(ref lateStaleCorrections);
            }
            catch
            {
                Interlocked.Increment(ref probeFailures);
            }
        }

        internal static string Summary()
        {
            bool validationComplete = PathSnapshotWorker.Completed >= ValidationQuota;
            return "Path shadow budget V0.4.18: quota=" + ValidationQuota +
                ", complete=" + validationComplete +
                ", low(sampleEvery=" + LowSampleEveryEligible + ",maxDistance=" + LowMaxValidationDistance + ",maxConcurrent=" + LowMaxConcurrent + ")" +
                ", normal(sampleEvery=" + NormalSampleEveryEligible + ",maxDistance=" + NormalMaxValidationDistance + ",maxConcurrent=" + NormalMaxConcurrent + ")" +
                ", eligible=" + Interlocked.Read(ref eligibleScheduleCalls) +
                ", lowEligible=" + Interlocked.Read(ref lowPressureEligible) +
                ", normalEligible=" + Interlocked.Read(ref normalPressureEligible) +
                ", cadenceSkipped=" + Interlocked.Read(ref cadenceSkipped) +
                ", distanceSkipped=" + Interlocked.Read(ref distanceBudgetSkipped) +
                ", pressureSkipped=" + Interlocked.Read(ref pressureSkipped) +
                ", concurrencySkipped=" + Interlocked.Read(ref concurrencySkipped) +
                ", quotaSkipped=" + Interlocked.Read(ref quotaSkipped) +
                "\nPath finalize generation recheck: lateStaleCorrections=" + Interlocked.Read(ref lateStaleCorrections) +
                ", probeFailures=" + Interlocked.Read(ref probeFailures);
        }
    }
}
