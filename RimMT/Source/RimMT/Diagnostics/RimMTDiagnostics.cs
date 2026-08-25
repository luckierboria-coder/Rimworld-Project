using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    public static class RimMTDiagnostics
    {
        internal static void LogStartupReport()
        {
            StringBuilder sb=new StringBuilder();
            sb.AppendLine("[RimMT] Compatibility / performance report");
            sb.AppendLine("Workers: "+(RimMTRuntime.Scheduler==null?0:RimMTRuntime.Scheduler.WorkerCount));
            sb.AppendLine("Policy: fail-closed / whitelist-only / vanilla commit");
            sb.AppendLine("Load pressure: "+AdaptiveLoadBalancer.Pressure+", EMA tick ms="+AdaptiveLoadBalancer.EmaTickMs.ToString("F3")+", P95 tick ms="+AdaptiveLoadBalancer.Percentile95().ToString("F3")+", spikes="+AdaptiveLoadBalancer.SpikeCount);
            Dictionary<string,FeatureGate.FeatureState> states=FeatureGate.Snapshot();
            foreach(KeyValuePair<string,FeatureGate.FeatureState> pair in states)
            {
                FeatureGate.FeatureState state=pair.Value; string status=state.Enabled&&!state.Suppressed?"ACTIVE":"OFF";
                sb.Append(" - ").Append(pair.Key).Append(": ").Append(status); if(!string.IsNullOrEmpty(state.Reason)) sb.Append(" (").Append(state.Reason).Append(")"); sb.AppendLine();
            }
            sb.AppendLine("Text cache: hits="+TextMetricCache.Hits+", misses="+TextMetricCache.Misses);
            sb.AppendLine("Overlay cache: sourceScans="+ThingOverlayCache.SourceScans+", cachedFrames="+ThingOverlayCache.CachedFrames);
            sb.AppendLine("Reach NO cache: hits="+ReachabilityNoCache.Hits+", stores="+ReachabilityNoCache.Stores+", topologyGen="+ReachabilityNoCache.TopologyGeneration);
            sb.AppendLine(HotPathProfiler.Summary("TickManager.DoSingleTick"));
            sb.AppendLine(HotPathProfiler.Summary("PathFinder.FindPath"));
            sb.AppendLine(HotPathProfiler.Summary("JobGiver_Work.TryIssueJobPackage"));
            foreach(string line in CompatibilityGuard.Report) sb.AppendLine(" * "+line);
            Log.Message(sb.ToString());
        }
        public static void RunWorkerSelfTest()
        {
            if(!RimMTRuntime.Initialized||RimMTRuntime.Scheduler==null) { Log.Warning("[RimMT] Worker self-test cannot run because the scheduler is not initialized."); return; }
            const int end=4000000; long total=0L; object totalSync=new object(); Stopwatch stopwatch=Stopwatch.StartNew();
            bool accepted=RimMTRuntime.Scheduler.ParallelFor("diagnostics.selfTest",0,end,50000,delegate(int from,int to){ long local=0L; for(int i=from;i<to;i++) local+=((long)i*31L)^(i>>3); lock(totalSync) total+=local; },delegate{ stopwatch.Stop(); Log.Message("[RimMT] Worker self-test passed: workers="+RimMTRuntime.Scheduler.WorkerCount+", elapsedMs="+stopwatch.ElapsedMilliseconds+", checksum="+total); },JobPriority.High);
            if(!accepted) Log.Warning("[RimMT] Worker self-test was not accepted by the bounded scheduler.");
        }
    }
}
