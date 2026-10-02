using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
        public static void LogRuntimeReport()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("[RimMT] V0.9.3-T34D.2.3 Aggressive Lean V1 on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", mainThreadFrames=" + RimMTRuntime.MainThreadFrames);
            sb.AppendLine(RuntimeCompatibility.Summary());
            AppendScheduler(sb);
            sb.AppendLine(MainThreadDispatcher.Summary());
            AppendLoad(sb);
            sb.AppendLine(AggressiveParallelScanner093T34D.Summary());
            sb.AppendLine(JobSearchPackageContext093T28.Summary());
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(ReservationTransaction093T32A.Summary());
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Removed modules: T34-A/B/C, S4/S5.1, T22, T26, Clean Pathfinding experiments and resident attribution probes.");
            sb.AppendLine("--- Feature gates ---");
            foreach (KeyValuePair<string, FeatureGate.FeatureState> pair in FeatureGate.Snapshot())
            {
                FeatureGate.FeatureState state = pair.Value;
                sb.Append(" - ").Append(pair.Key).Append(": ")
                    .Append(state.Enabled && !state.Suppressed ? "ACTIVE" : "OFF");
                if (!string.IsNullOrEmpty(state.Reason)) sb.Append(" (").Append(state.Reason).Append(')');
                sb.AppendLine();
            }
            Log.Message(sb.ToString());
        }

        internal static string BuildCompactMonitorText()
        {
            StringBuilder sb = new StringBuilder(2048);
            AppendLoad(sb);
            sb.AppendLine(AggressiveParallelScanner093T34D.Summary());
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(ReservationTransaction093T32A.Summary());
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            return sb.ToString();
        }

        private static void AppendScheduler(StringBuilder sb)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null) return;
            sb.AppendLine("Scheduler: logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                ", workers=" + scheduler.WorkerCount +
                ", productionPending=" + scheduler.ProductionPending +
                ", productionTasks=" + scheduler.ProductionCompleted + "/" + scheduler.ProductionEnqueued +
                ", productionRejected=" + scheduler.ProductionRejected +
                ", productionFailures=" + scheduler.ProductionFailures +
                ", busyWorkerMs=" + scheduler.ProductionBusyMilliseconds.ToString("F2") +
                ", busyUtilization=" + scheduler.ProductionBusyUtilizationPercent.ToString("F2") + "%");
        }

        private static void AppendLoad(StringBuilder sb)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            int budget = scheduler == null ? 0 : AdaptiveLoadBalancer.BackgroundConcurrencyBudget(scheduler.WorkerCount);
            sb.AppendLine("Load pressure: " + AdaptiveLoadBalancer.Pressure +
                ", source=" + AdaptiveLoadBalancer.SampleSource +
                ", samples=" + AdaptiveLoadBalancer.SampleCount +
                ", EMAms=" + AdaptiveLoadBalancer.EmaTickMs.ToString("F3") +
                ", P95ms=" + AdaptiveLoadBalancer.Percentile95().ToString("F3") +
                ", slowRatio=" + (AdaptiveLoadBalancer.RollingSlowRatio * 100.0).ToString("F2") + "%" +
                ", backgroundBudget=" + budget);
        }

        public static void RunWorkerSelfTest()
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (!RimMTRuntime.Initialized || scheduler == null)
            {
                Log.Warning("[RimMT] Worker self-test cannot run because the scheduler is not initialized.");
                return;
            }

            const int end = 1000000;
            long total = 0L;
            object sync = new object();
            Stopwatch stopwatch = Stopwatch.StartNew();
            bool accepted = scheduler.ParallelFor("diagnostics.selfTest", 0, end, 25000,
                delegate(int from, int to)
                {
                    long local = 0L;
                    for (int i = from; i < to; i++) local += (i * 31) & 1023;
                    lock (sync) total += local;
                },
                delegate
                {
                    stopwatch.Stop();
                    Log.Message("[RimMT] Worker self-test passed: checksum=" + total +
                        ", elapsedMs=" + stopwatch.Elapsed.TotalMilliseconds.ToString("F2"));
                },
                JobPriority.High);
            if (!accepted)
                Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
        }
    }
}
