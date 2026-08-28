using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
        private static long reportSerial;

        internal static void LogStartupReport()
        {
            LogReport("startup");
        }

        public static void LogRuntimeReport()
        {
            LogReport("runtime");
        }

        private static void LogReport(string kind)
        {
            long serial = Interlocked.Increment(ref reportSerial);
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[RimMT] Compatibility / performance report #" + serial + " [" + kind + "]");
            sb.AppendLine("ProgramState: " + Current.ProgramState + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
            sb.AppendLine(RuntimeCompatibility.Summary());

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            sb.AppendLine("CPU scheduler view: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                ", RimMTWorkers=" + (scheduler == null ? 0 : scheduler.WorkerCount) +
                ", workerCap=8 (V0.4.18.2 baseline behavior; P1 adds diagnostics only)");
            if (scheduler != null)
            {
                sb.AppendLine("Worker queue: pending=" + scheduler.Pending +
                    ", enqueued=" + scheduler.Enqueued +
                    ", completed=" + scheduler.Completed +
                    ", rejected=" + scheduler.Rejected +
                    ", failures=" + scheduler.Failures +
                    ", active=" + scheduler.ActiveWorkers + "/" + scheduler.WorkerCount +
                    ", peakActive=" + scheduler.PeakActiveWorkers +
                    ", highWater=" + scheduler.HighWaterPending +
                    ", wakeCredits=" + scheduler.WakeReleases +
                    ", multiWakeCalls=" + scheduler.MultiWakeCalls +
                    ", parallelBatches=" + scheduler.ParallelBatchesEnqueued +
                    ", timeoutPollClaims=" + scheduler.TimeoutPollClaims);
            }

            sb.AppendLine(MainThreadDispatcher.Summary());
            if (RuntimeCompatibility.ButterPlusPlusActive)
            {
                bool managerInProgress;
                bool managerReadable = RuntimeCompatibility.TryGetButterLogicalTickInProgress(out managerInProgress);
                bool tickListMidTick;
                bool tickListReadable = RuntimeCompatibility.TryGetButterTickListMidTick(out tickListMidTick);
                sb.AppendLine("Butter++ dispatcher barrier: logicalTickDrainDeferrals=" + RimMTRuntime.ButterLogicalTickDrainDeferrals +
                    ", probeFailureDrainDeferrals=" + RimMTRuntime.ButterProbeFailureDrainDeferrals +
                    ", managerProbeReadable=" + managerReadable +
                    ", managerInProgress=" + managerInProgress +
                    ", probe=" + RuntimeCompatibility.ButterProbeDescription +
                    ", tickListProbeReadable=" + tickListReadable +
                    ", tickListMidTick=" + tickListMidTick +
                    ", tickListProbe=" + RuntimeCompatibility.ButterTickListProbeDescription);
            }

            sb.AppendLine("Policy: V0.4.18.2 gameplay baseline + diagnostic-only tick/frame decomposition");
            sb.AppendLine("Load pressure: " + AdaptiveLoadBalancer.Pressure +
                ", sampleSource=" + AdaptiveLoadBalancer.SampleSource +
                ", EMA ms=" + AdaptiveLoadBalancer.EmaTickMs.ToString("F3") +
                ", P95 ms=" + AdaptiveLoadBalancer.Percentile95().ToString("F3") +
                ", spikes=" + AdaptiveLoadBalancer.SpikeCount +
                ", butterFrameSamples=" + AdaptiveLoadBalancer.ButterFrameSamples);

            Dictionary<string,FeatureGate.FeatureState> states = FeatureGate.Snapshot();
            foreach (KeyValuePair<string,FeatureGate.FeatureState> pair in states)
            {
                FeatureGate.FeatureState state = pair.Value;
                string status = state.Enabled && !state.Suppressed ? "ACTIVE" : "OFF";
                sb.Append(" - ").Append(pair.Key).Append(": ").Append(status);
                if (!string.IsNullOrEmpty(state.Reason))
                    sb.Append(" (").Append(state.Reason).Append(")");
                sb.AppendLine();
            }

            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Overlay cache: sourceScans=" + ThingOverlayCache.SourceScans + ", cachedFrames=" + ThingOverlayCache.CachedFrames);
            sb.AppendLine("Reach NO cache: hits=" + ReachabilityNoCache.Hits + ", stores=" + ReachabilityNoCache.Stores + ", topologyGen=" + ReachabilityNoCache.TopologyGeneration);
            sb.AppendLine(HaulWorkAccelerator.Summary());
            sb.AppendLine(GlobalHaulAccelerator.Summary());
            sb.AppendLine(PersistentMapSearchFabric.Summary());
            sb.AppendLine(AdaptiveGenClosestAssist.Summary());
            sb.AppendLine(BroadGenClosestOrder0418.Summary());
            sb.AppendLine(JobGiverGlobalNearest04181.Summary());
            sb.AppendLine(AsyncJobCandidatePlan04182.Summary());
            sb.AppendLine(AggressiveReachabilityProfiles.Summary());
            sb.AppendLine("Reach-profile V0.4.18.2 aggressive policy: sampled positive authority ENABLED. V0.4.18 force-positive-to-Vanilla guard is not installed; the native V0.4.16 warmup, shadow sampling, per-profile cooldown and global mismatch fuse remain active.");
            sb.AppendLine(ParallelRegionConnectivity.Summary());
            sb.AppendLine(ParallelWorkPrefilter.Summary());
            sb.AppendLine(PathGridInvalidation.Summary());
            sb.AppendLine(PathSnapshotSafetyPatches.Summary());
            sb.AppendLine(PathSnapshotWorker.Summary());
            sb.AppendLine(SafePathClassTelemetry0418.Summary());
            sb.AppendLine(HotPathProfiler.Summary("TickManager.DoSingleTick"));
            sb.AppendLine(TickLayerProfiler04182P1.Summary());
            if (RuntimeCompatibility.ButterPlusPlusActive)
                sb.AppendLine(HotPathProfiler.Summary("TickManager.TickManagerUpdate[ButterSlice]"));
            sb.AppendLine(HotPathProfiler.Summary("PathFinder.FindPath"));
            sb.AppendLine(HotPathProfiler.Summary("PathFinder.FindPath[pawn]"));
            sb.AppendLine(HotPathProfiler.Summary("PathFinder.FindPath[traverseParms]"));
            sb.AppendLine(HotPathProfiler.Summary("JobGiver_Work.TryIssueJobPackage"));
            sb.AppendLine(WorkGiverProfiler.Summary(12));
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(12));
            foreach (string line in CompatibilityGuard.Report)
                sb.AppendLine(" * " + line);
            Log.Message(sb.ToString());
        }

        public static void RunWorkerSelfTest()
        {
            if (!RimMTRuntime.Initialized || RimMTRuntime.Scheduler == null)
            {
                Log.Warning("[RimMT] Worker self-test cannot run because the scheduler is not initialized.");
                return;
            }

            const int end = 4000000;
            long total = 0L;
            object totalSync = new object();
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool accepted = RimMTRuntime.Scheduler.ParallelFor(
                "diagnostics.selfTest",
                0,
                end,
                50000,
                delegate(int from, int to)
                {
                    long local = 0L;
                    for (int i = from; i < to; i++)
                        local += ((long)i * 31L) ^ (i >> 3);
                    lock (totalSync)
                        total += local;
                },
                delegate
                {
                    stopwatch.Stop();
                    JobScheduler currentScheduler = RimMTRuntime.Scheduler;
                    Log.Message("[RimMT] Worker self-test passed: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                        ", workers=" + currentScheduler.WorkerCount +
                        ", elapsedMs=" + stopwatch.ElapsedMilliseconds +
                        ", checksum=" + total +
                        ", enqueued=" + currentScheduler.Enqueued +
                        ", completed=" + currentScheduler.Completed +
                        ", failures=" + currentScheduler.Failures +
                        ", peakActive=" + currentScheduler.PeakActiveWorkers +
                        ", highWater=" + currentScheduler.HighWaterPending +
                        ", multiWakeCalls=" + currentScheduler.MultiWakeCalls +
                        ", parallelBatches=" + currentScheduler.ParallelBatchesEnqueued);
                },
                JobPriority.High);

            if (!accepted)
                Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
        }
    }
}
