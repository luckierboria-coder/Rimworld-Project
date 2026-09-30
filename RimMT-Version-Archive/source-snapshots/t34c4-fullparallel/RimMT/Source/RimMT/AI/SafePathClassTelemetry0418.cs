using System;
using System.Collections.Concurrent;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.18 evidence layer for the future shadow -> safe-authoritative transition.
    // It does not alter path results. Each accepted shadow request is classified only from
    // already-supported request properties, then the final worker/Vanilla pair is counted by
    // class. This tells us which narrow request class is actually converging before any class
    // is allowed to become authoritative.
    internal static class SafePathClassTelemetry0418
    {
        private static readonly ConcurrentDictionary<int, PathClass> Classes =
            new ConcurrentDictionary<int, PathClass>();

        private static readonly Type RequestType = typeof(PathSnapshotWorker).GetNestedType("PathRequest", BindingFlags.NonPublic);
        private static readonly Type WorkerType = typeof(PathSnapshotWorker).GetNestedType("WorkerResult", BindingFlags.NonPublic);
        private static readonly Type VanillaType = typeof(PathSnapshotWorker).GetNestedType("VanillaResult", BindingFlags.NonPublic);

        private static readonly FieldInfo IdField = RequestType == null ? null : RequestType.GetField("Id", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo HasWorkerField = RequestType == null ? null : RequestType.GetField("HasWorker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo HasVanillaField = RequestType == null ? null : RequestType.GetField("HasVanilla", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkerField = RequestType == null ? null : RequestType.GetField("Worker", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VanillaField = RequestType == null ? null : RequestType.GetField("Vanilla", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly FieldInfo WorkerFoundField = WorkerType == null ? null : WorkerType.GetField("Found", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkerStaleField = WorkerType == null ? null : WorkerType.GetField("Stale", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkerNodeCountField = WorkerType == null ? null : WorkerType.GetField("NodeCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo WorkerHashField = WorkerType == null ? null : WorkerType.GetField("PathHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VanillaFoundField = VanillaType == null ? null : VanillaType.GetField("Found", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VanillaNodeCountField = VanillaType == null ? null : VanillaType.GetField("NodeCount", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo VanillaHashField = VanillaType == null ? null : VanillaType.GetField("PathHash", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        private static readonly ClassStats[] Stats =
        {
            new ClassStats(), new ClassStats(), new ClassStats(), new ClassStats(), new ClassStats(), new ClassStats()
        };

        private static long scheduledClassified;
        private static long finalizedClassified;
        private static long stalePairs;
        private static long missingClass;
        private static long reflectionFailures;
        private static long patchFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            if (RequestType == null || WorkerType == null || VanillaType == null ||
                IdField == null || HasWorkerField == null || HasVanillaField == null || WorkerField == null || VanillaField == null ||
                WorkerFoundField == null || WorkerStaleField == null || WorkerNodeCountField == null || WorkerHashField == null ||
                VanillaFoundField == null || VanillaNodeCountField == null || VanillaHashField == null)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] V0.4.18 SafePathClass telemetry unavailable: private path telemetry fields changed.");
                return;
            }

            try
            {
                MethodBase schedule = AccessTools.Method(typeof(PathSnapshotWorker), "TrySchedule");
                MethodBase finalize = AccessTools.Method(typeof(PathSnapshotWorker), "TryFinalize");
                if (schedule == null || finalize == null)
                {
                    Interlocked.Increment(ref patchFailures);
                    Log.Warning("[RimMT] V0.4.18 SafePathClass telemetry unavailable: TrySchedule/TryFinalize not found.");
                    return;
                }

                HarmonyMethod schedulePostfix = new HarmonyMethod(typeof(SafePathClassTelemetry0418), nameof(SchedulePostfix));
                schedulePostfix.priority = Priority.Last;
                HarmonyMethod finalizePostfix = new HarmonyMethod(typeof(SafePathClassTelemetry0418), nameof(FinalizePostfix));
                finalizePostfix.priority = Priority.Last;
                harmony.Patch(schedule, postfix: schedulePostfix);
                harmony.Patch(finalize, postfix: finalizePostfix);

                Log.Message("[RimMT] V0.4.18 SafePathClass telemetry active. Shadow pairs are grouped by ByPawn/NoPassClosedDoors, drafted state, and pawn presence; gameplay path authority is unchanged.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] V0.4.18 SafePathClass telemetry patch failed; path shadow behavior is unchanged. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void SchedulePostfix(object[] args, int __result)
        {
            if (__result == 0 || args == null || args.Length < 4)
                return;

            try
            {
                TraverseParms parms = (TraverseParms)args[2];
                Pawn pawn = parms.pawn;
                PathClass pathClass;
                if (pawn == null)
                    pathClass = PathClass.NoPawn;
                else if (parms.mode == TraverseMode.ByPawn)
                    pathClass = pawn.Drafted ? PathClass.ByPawnDrafted : PathClass.ByPawnNonDrafted;
                else if (parms.mode == TraverseMode.NoPassClosedDoors)
                    pathClass = pawn.Drafted ? PathClass.NoClosedDoorsDrafted : PathClass.NoClosedDoorsNonDrafted;
                else
                    pathClass = PathClass.Other;

                Classes[__result] = pathClass;
                Interlocked.Increment(ref scheduledClassified);
            }
            catch
            {
                Interlocked.Increment(ref reflectionFailures);
            }
        }

        public static void FinalizePostfix(object __0)
        {
            if (__0 == null)
                return;

            try
            {
                if (!(bool)HasWorkerField.GetValue(__0) || !(bool)HasVanillaField.GetValue(__0))
                    return;

                int id = (int)IdField.GetValue(__0);
                PathClass pathClass;
                if (!Classes.TryRemove(id, out pathClass))
                {
                    Interlocked.Increment(ref missingClass);
                    return;
                }

                object worker = WorkerField.GetValue(__0);
                object vanilla = VanillaField.GetValue(__0);
                if (worker == null || vanilla == null)
                {
                    Interlocked.Increment(ref reflectionFailures);
                    return;
                }

                if ((bool)WorkerStaleField.GetValue(worker))
                {
                    Interlocked.Increment(ref stalePairs);
                    return;
                }

                bool workerFound = (bool)WorkerFoundField.GetValue(worker);
                bool vanillaFound = (bool)VanillaFoundField.GetValue(vanilla);
                int workerNodes = (int)WorkerNodeCountField.GetValue(worker);
                int vanillaNodes = (int)VanillaNodeCountField.GetValue(vanilla);
                int workerHash = (int)WorkerHashField.GetValue(worker);
                int vanillaHash = (int)VanillaHashField.GetValue(vanilla);

                ClassStats stats = Stats[(int)pathClass];
                Interlocked.Increment(ref stats.Total);
                Interlocked.Increment(ref finalizedClassified);

                if (workerFound == vanillaFound)
                    Interlocked.Increment(ref stats.FoundParity);
                else
                    Interlocked.Increment(ref stats.FoundMismatch);

                bool exact = workerFound == vanillaFound;
                if (exact && workerFound)
                    exact = workerNodes == vanillaNodes && workerHash == vanillaHash;
                if (exact)
                    Interlocked.Increment(ref stats.ExactGeometry);
                else
                    Interlocked.Increment(ref stats.GeometryMismatch);
            }
            catch
            {
                Interlocked.Increment(ref reflectionFailures);
            }
        }

        internal static string Summary()
        {
            return "SafePathClass V0.4.18 telemetry: scheduled=" + Interlocked.Read(ref scheduledClassified) +
                ", finalized=" + Interlocked.Read(ref finalizedClassified) +
                ", stale=" + Interlocked.Read(ref stalePairs) +
                ", missingClass=" + Interlocked.Read(ref missingClass) +
                ", reflectionFailures=" + Interlocked.Read(ref reflectionFailures) +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                "\n  A0 ByPawn non-drafted: " + Format(Stats[(int)PathClass.ByPawnNonDrafted]) +
                "\n  A1 ByPawn drafted: " + Format(Stats[(int)PathClass.ByPawnDrafted]) +
                "\n  B0 NoPassClosedDoors non-drafted: " + Format(Stats[(int)PathClass.NoClosedDoorsNonDrafted]) +
                "\n  B1 NoPassClosedDoors drafted: " + Format(Stats[(int)PathClass.NoClosedDoorsDrafted]) +
                "\n  C NoPawn: " + Format(Stats[(int)PathClass.NoPawn]) +
                "\n  D Other: " + Format(Stats[(int)PathClass.Other]) +
                ". Telemetry only; no class is authoritative in V0.4.18.";
        }

        private static string Format(ClassStats stats)
        {
            long total = Interlocked.Read(ref stats.Total);
            long exact = Interlocked.Read(ref stats.ExactGeometry);
            double exactPct = total == 0 ? 0.0 : exact * 100.0 / total;
            return "total=" + total +
                ", foundParity=" + Interlocked.Read(ref stats.FoundParity) +
                ", foundMismatch=" + Interlocked.Read(ref stats.FoundMismatch) +
                ", exactGeometry=" + exact + " (" + exactPct.ToString("F1") + "%)" +
                ", geometryMismatch=" + Interlocked.Read(ref stats.GeometryMismatch);
        }

        private enum PathClass
        {
            ByPawnNonDrafted = 0,
            ByPawnDrafted = 1,
            NoClosedDoorsNonDrafted = 2,
            NoClosedDoorsDrafted = 3,
            NoPawn = 4,
            Other = 5
        }

        private sealed class ClassStats
        {
            internal long Total;
            internal long FoundParity;
            internal long FoundMismatch;
            internal long ExactGeometry;
            internal long GeometryMismatch;
        }
    }
}
