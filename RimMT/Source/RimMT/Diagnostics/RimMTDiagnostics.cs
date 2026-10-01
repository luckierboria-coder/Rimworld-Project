using System.Text;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
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
            sb.AppendLine("[RimMT] V0.9.3-T34D.2.3 Lean Live V2.1 on-demand report");
            sb.AppendLine("ProgramState=" + Current.ProgramState + ", logicalProcessors=" + RimMTRuntime.DetectedProcessorCount);
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
            sb.AppendLine(JobSearchPackageContext093T28.Summary());
            sb.AppendLine(JobGiverSlowSearch0419S.Summary());
            sb.AppendLine(PersistentDoBillIndex092.Summary());
            sb.AppendLine(CommonSenseIngredientExpand092.Summary());
            sb.AppendLine("Policy: synchronous main-thread exact-negative filters only; no scheduler, dispatcher, tick sampler, reachability cache, reservation cache, T34 fabric, Clean Pathfinding hook or resident profiler.");
            return sb.ToString();
        }
    }
}
