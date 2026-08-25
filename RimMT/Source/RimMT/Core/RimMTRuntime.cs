using System;
using System.Threading;
using Verse;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized;
        private static bool compatibilityChecked;
        private static JobScheduler scheduler;
        private static long mainThreadFrames;
        private static long butterMidTickDrainDeferrals;

        internal static JobScheduler Scheduler { get { return scheduler; } }
        internal static bool Initialized { get { return initialized; } }
        internal static long MainThreadFrames { get { return Interlocked.Read(ref mainThreadFrames); } }
        internal static long ButterMidTickDrainDeferrals { get { return Interlocked.Read(ref butterMidTickDrainDeferrals); } }

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;
            RuntimeCompatibility.Initialize();

            int workers = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));
            scheduler = new JobScheduler(workers, 100000);

            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher; AdaptiveTPS and Butter++ TickManagerUpdate coexistence supported");
            FeatureGate.Register("runtime.adaptiveBurst", true, "Pressure-aware scheduler; samples Butter++ TickManagerUpdate slices when Butter++ is active");
            FeatureGate.Register("diagnostics.selfTest", true, "Pure CPU worker self-test");
            FeatureGate.Register("diagnostics.hotPaths", true, "PathFinder / JobGiver / tick hot-path profiler");
            FeatureGate.Register("diagnostics.pathFinder", true, "PathFinder.FindPath overload probes");
            FeatureGate.Register("diagnostics.jobGiver", true, "JobGiver_Work.TryIssueJobPackage probes");
            FeatureGate.Register("ui.textCache", true, "Text metric result cache");
            FeatureGate.Register("ui.overlayCache", true, "Visible Thing overlay scan cache");
            FeatureGate.Register("ai.reachNoCache", false, "Topology-aware short-lived negative reachability cache");
            FeatureGate.Register("ai.pathTopology", true, "PathGrid topology invalidation hooks for reachability generations");
            FeatureGate.Register("parallel.pathSnapshot", true, "Worker-side immutable path A* parity validation; vanilla authoritative while production parity is tightened");
            FeatureGate.Register("parallel.jobScan", false, "JobGiver candidate snapshot scan; not implemented yet");
            FeatureGate.Register("parallel.pawnTick", false, "Unsafe by default; not implemented");
            FeatureGate.Register("parallel.reservations", false, "Unsafe by default; not implemented");
            FeatureGate.Register("parallel.thingTick", false, "Whitelist module not implemented");
            ApplySettings(RimMTMod.Settings);
        }

        internal static void ApplySettings(RimMTSettings settings)
        {
            if (!initialized || settings == null) return;
            FeatureGate.SetEnabled("runtime.adaptiveBurst", settings.AdaptiveBurst);
            FeatureGate.SetEnabled("diagnostics.hotPaths", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("diagnostics.pathFinder", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("diagnostics.jobGiver", settings.HotPathDiagnostics);
            FeatureGate.SetEnabled("ui.textCache", settings.TextCache);
            FeatureGate.SetEnabled("ui.overlayCache", settings.OverlayCache);
            FeatureGate.SetEnabled("ai.reachNoCache", settings.ReachNoCache);
            FeatureGate.SetEnabled("parallel.pathSnapshot", settings.PathSnapshotWorker);
        }

        internal static void OnMainThreadFrame()
        {
            if (!initialized) return;
            Interlocked.Increment(ref mainThreadFrames);

            bool logicalTickBoundary = true;
            if (RuntimeCompatibility.ButterPlusPlusActive)
            {
                if (!RuntimeCompatibility.ButterMidTickProbeAvailable)
                {
                    logicalTickBoundary = false;
                }
                else if (RuntimeCompatibility.IsButterMidTick())
                {
                    logicalTickBoundary = false;
                    Interlocked.Increment(ref butterMidTickDrainDeferrals);
                }
            }

            if (logicalTickBoundary && FeatureGate.IsEnabled("runtime.dispatcher"))
                MainThreadDispatcher.Drain(256);

            // Compatibility scanning and the first runtime report must also happen at a logical
            // tick boundary. Butter++ may return from TickManagerUpdate several times while one
            // DoSingleTick is still incomplete.
            if (!compatibilityChecked && logicalTickBoundary && Current.ProgramState == ProgramState.Playing)
            {
                compatibilityChecked = true;
                CompatibilityGuard.RunBaselineScan();
                RimMTDiagnostics.LogStartupReport();
            }
        }
    }
}
