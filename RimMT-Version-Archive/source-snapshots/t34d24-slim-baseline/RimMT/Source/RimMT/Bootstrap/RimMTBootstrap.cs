using System;
using HarmonyLib;
using Verse;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class RimMTBootstrap
    {
        internal const string HarmonyId = "allen.rimmt";
        internal const string Version = "0.9.3-t34d2.4-slim-baseline";
        internal static bool DispatcherBridgePatched { get { return false; } }

        static RimMTBootstrap()
        {
            try
            {
                RimMTThreadGuard.InitializeMainThread();
                RimMTRuntime.Initialize();

                Harmony harmony = new Harmony(HarmonyId);
                RimMTPatches.Apply(harmony);

                // Retain only filters and transactions with demonstrated production yield.
                WorkGiverMergePartnerIndex093T4.Apply(harmony);
                JobSearchPackageContext093T28.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                GenClosestTransactionIndex093T22.Apply(harmony);

                Log.Message("[RimMT] V0.9.3-T34D.2.4 Slim Baseline initialized. " +
                    "Retained: T4 merge-negative filter, S4 targeted slow-search filter, and the T20/T21/T22/T28/T32 package-local transaction path. " +
                    "Retired: T34 candidate fabrics, T34-D worker validators, T26 tick boundary, broad/global/haul GenClosest routers, worker scheduler, frame dispatcher, path-topology bookkeeping, deep attribution, and asset cleanup coalescing. " +
                    "Production search has one ClosestThingReachable owner and never waits for a worker.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] D.2.4 Slim Baseline initialization failed. RimMT will remain inert. " + ex);
            }
        }
    }
}
