using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class PathSnapshotSafetyPatches
    {
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

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            MethodBase finalize = AccessTools.Method(typeof(PathSnapshotWorker), "TryFinalize");
            if (finalize == null || RequestType == null || SnapshotField == null || WorkerField == null || HasWorkerField == null || GenerationField == null || StaleField == null)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot V0.4.5 finalize-generation safety probe unavailable; existing worker-time stale check remains active.");
                return;
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(PathSnapshotSafetyPatches), nameof(FinalizePrefix));
                prefix.priority = Priority.First;
                harmony.Patch(finalize, prefix: prefix);
                Log.Message("[RimMT] parallel.pathSnapshot V0.4.5 finalize-generation recheck active. Topology changes after worker completion are marked stale before paired validation finalizes.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref probeFailures);
                Log.Warning("[RimMT] parallel.pathSnapshot finalize-generation safety patch failed; existing worker-time stale check remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void FinalizePrefix(object request)
        {
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

                object worker = WorkerField.GetValue(request); // boxed WorkerResult struct
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
            return "Path finalize generation recheck V0.4.5: lateStaleCorrections=" + Interlocked.Read(ref lateStaleCorrections) +
                ", probeFailures=" + Interlocked.Read(ref probeFailures);
        }
    }
}
