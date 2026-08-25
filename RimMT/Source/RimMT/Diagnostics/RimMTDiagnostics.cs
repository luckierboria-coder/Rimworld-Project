using System.Collections.Generic;
using System.Text;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
        internal static void LogStartupReport()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("[RimMT] Compatibility report");
            sb.AppendLine("Workers: " + RimMTRuntime.Scheduler.WorkerCount);
            sb.AppendLine("Policy: fail-closed / whitelist-only / vanilla fallback");

            Dictionary<string, FeatureGate.FeatureState> states = FeatureGate.Snapshot();
            foreach (KeyValuePair<string, FeatureGate.FeatureState> pair in states)
            {
                FeatureGate.FeatureState state = pair.Value;
                string status = state.Enabled && !state.Suppressed ? "ACTIVE" : "OFF";
                sb.Append(" - ").Append(pair.Key).Append(": ").Append(status);
                if (!string.IsNullOrEmpty(state.Reason)) sb.Append(" (").Append(state.Reason).Append(")");
                sb.AppendLine();
            }

            foreach (string line in CompatibilityGuard.Report)
                sb.AppendLine(" * " + line);

            Log.Message(sb.ToString());
        }
    }
}
