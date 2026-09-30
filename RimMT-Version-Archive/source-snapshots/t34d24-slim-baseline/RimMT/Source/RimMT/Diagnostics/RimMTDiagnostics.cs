using System.Collections.Generic;
using System.Text;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
        internal static void LogStartupReport()
        {
            LogRuntimeReport();
        }

        public static void LogRuntimeReport()
        {
            Log.Message(BuildReport());
        }

        internal static string BuildCompactMonitorText()
        {
            return BuildReport();
        }

        private static string BuildReport()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("[RimMT] V0.9.3-T34D.2.4 Slim Baseline on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState +
                ", logicalProcessors=" + RimMTRuntime.DetectedProcessorCount +
                ", workerScheduler=ABSENT, tickTimer=ABSENT, frameDispatcher=ABSENT");
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine(JobSearchPackageContext093T28.Summary());
            sb.AppendLine(ReservationTransaction093T32A.Summary());
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine("Retired modules: T34-A/B/C/D, T26, broad/global/haul GenClosest routers, persistent candidate fabrics, path-topology bookkeeping, deep attribution and asset cleanup coalescing.");
            sb.AppendLine("Policy: one ClosestThingReachable owner; package-local lifetime; Vanilla live validator/reachability authority; no worker wait.");

            sb.AppendLine("Feature gates:");
            Dictionary<string, FeatureGate.FeatureState> states = FeatureGate.Snapshot();
            foreach (KeyValuePair<string, FeatureGate.FeatureState> pair in states)
            {
                FeatureGate.FeatureState state = pair.Value;
                sb.Append(" - ").Append(pair.Key).Append(": ")
                    .Append(state.Enabled && !state.Suppressed ? "ACTIVE" : "OFF");
                if (!string.IsNullOrEmpty(state.Reason)) sb.Append(" (").Append(state.Reason).Append(')');
                sb.AppendLine();
            }
            return sb.ToString();
        }

        public static void RunWorkerSelfTest()
        {
            Log.Message("[RimMT] D.2.4 has no worker scheduler; worker self-test is retired.");
        }
    }
}
