using System;
using Verse;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized; private static bool compatibilityChecked; private static JobScheduler scheduler;
        internal static JobScheduler Scheduler { get { return scheduler; } }
        internal static bool Initialized { get { return initialized; } }
        internal static void Initialize()
        {
            if (initialized) return; initialized=true;
            int workers=Math.Max(1,Math.Min(Environment.ProcessorCount-1,8)); scheduler=new JobScheduler(workers,100000);
            FeatureGate.Register("runtime.scheduler",true,"Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher",true,"Worker-to-main-thread dispatcher");
            FeatureGate.Register("runtime.adaptiveBurst",true,"Pressure-aware scheduler that defers background work during tick spikes");
            FeatureGate.Register("diagnostics.selfTest",true,"Pure CPU worker self-test");
            FeatureGate.Register("diagnostics.hotPaths",true,"PathFinder / JobGiver / tick hot-path profiler");
            FeatureGate.Register("ui.textCache",true,"Text metric result cache");
            FeatureGate.Register("ui.overlayCache",true,"Visible Thing overlay scan cache");
            FeatureGate.Register("ai.reachNoCache",false,"Topology-aware short-lived negative reachability cache");
            FeatureGate.Register("ai.pathTopology",true,"PathGrid topology invalidation hooks for reachability generations");
            FeatureGate.Register("parallel.pawnTick",false,"Unsafe by default; not implemented");
            FeatureGate.Register("parallel.reservations",false,"Unsafe by default; not implemented");
            FeatureGate.Register("parallel.thingTick",false,"Whitelist module not implemented");
            ApplySettings(RimMTMod.Settings);
        }
        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings==null) return;
            FeatureGate.SetEnabled("runtime.adaptiveBurst",settings.AdaptiveBurst);
            FeatureGate.SetEnabled("diagnostics.hotPaths",settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("ui.textCache",settings.TextCache);
            FeatureGate.SetEnabled("ui.overlayCache",settings.OverlayCache);
            FeatureGate.SetEnabled("ai.reachNoCache",settings.ReachNoCache);
        }
        internal static void OnMainThreadFrame()
        {
            if (!initialized) return; MainThreadDispatcher.Drain(256);
            if (!compatibilityChecked && Current.ProgramState==ProgramState.Playing) { compatibilityChecked=true; CompatibilityGuard.RunBaselineScan(); RimMTDiagnostics.LogStartupReport(); }
        }
    }
}
