using System;
using System.Diagnostics;
using System.Threading;
using Verse;

namespace RimMT2.Diagnostics
{
    internal static class StatusReporter
    {
        internal static void LogNow(string trigger)
        {
            WorkerFabric fabric = RuntimeController.Fabric;
            double frequency = Stopwatch.Frequency;
            double workerMs = Interlocked.Read(ref FabricMetrics.WorkerTicks) * 1000.0 / frequency;
            double commitMs = Interlocked.Read(ref FabricMetrics.CommitTicks) * 1000.0 / frequency;
            Log.Message(
                "[RimMT 2.0 M1.1.5] status trigger=" + trigger +
                " engineWorkers=" + (fabric?.WorkerCount ?? 0) +
                " spatialEnabled=" + (RimMT2Mod.Settings?.enableSpatialProof ?? false) +
                " pending[work/commit]=" + (fabric?.PendingWork ?? 0) + "/" + (fabric?.PendingCommits ?? 0) +
                " submitted=" + Interlocked.Read(ref FabricMetrics.Submitted) +
                " rejected=" + Interlocked.Read(ref FabricMetrics.QueueRejected) +
                " worker[completed/failed/ms]=" + Interlocked.Read(ref FabricMetrics.WorkerCompleted) + "/" +
                    Interlocked.Read(ref FabricMetrics.WorkerFailed) + "/" + workerMs.ToString("F3") +
                " commit[ok/stale/failed/ms]=" + Interlocked.Read(ref FabricMetrics.CommitSucceeded) + "/" +
                    Interlocked.Read(ref FabricMetrics.CommitDiscarded) + "/" +
                    Interlocked.Read(ref FabricMetrics.CommitFailed) + "/" + commitMs.ToString("F3") +
                " missileGirl=" + Compatibility.MissileGirlCompatibility.Active +
                " policy=snapshot-only");
        }
    }
}
