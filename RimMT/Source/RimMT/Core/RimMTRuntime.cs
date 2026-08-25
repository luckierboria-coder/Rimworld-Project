using System;
using Verse;

namespace RimMT
{
    internal static class RimMTRuntime
    {
        private static bool initialized;
        private static bool compatibilityChecked;
        private static JobScheduler scheduler;

        internal static JobScheduler Scheduler => scheduler;

        internal static void Initialize()
        {
            if (initialized) return;
            initialized = true;

            int workers = Math.Max(1, Math.Min(Environment.ProcessorCount - 1, 8));
            scheduler = new JobScheduler(workers, 100000);
            FeatureGate.Register("runtime.scheduler", true, "Core bounded worker scheduler");
            FeatureGate.Register("runtime.dispatcher", true, "Worker-to-main-thread dispatcher");
            FeatureGate.Register("parallel.pawnTick", false, "Unsafe by default; experimental module not implemented");
            FeatureGate.Register("parallel.reservations", false, "Unsafe by default; never parallelized in V0.2");
            FeatureGate.Register("parallel.thingTick", false, "Whitelist module not implemented yet");
        }

        internal static void OnMainThreadFrame()
        {
            if (!initialized) return;
            MainThreadDispatcher.Drain(256);

            if (!compatibilityChecked && Current.ProgramState == ProgramState.Playing)
            {
                compatibilityChecked = true;
                CompatibilityGuard.RunBaselineScan();
                RimMTDiagnostics.LogStartupReport();
            }
        }
    }
}
