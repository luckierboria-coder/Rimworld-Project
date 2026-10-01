using System;
using HarmonyLib;
using Verse;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class RimMTBootstrap
    {
        internal const string HarmonyId = "allen.rimmt";
        internal const string Version = "0.9.3-t34d2.3-lean-live-v2.1";

        static RimMTBootstrap()
        {
            try
            {
                RimMTThreadGuard.InitializeMainThread();
                RimMTRuntime.Initialize();

                Harmony harmony = new Harmony(HarmonyId);
                RimMTPatches.Apply(harmony);
                WorkGiverMergePartnerIndex093T4.Apply(harmony);

                // T28 owns the single synchronous JobGiver_Work package boundary.
                // The old persistent-fabric GenClosest consumer is retired after repeated
                // runtime evidence of zero accelerations; Vanilla/Broad/T22 remain authoritative.
                JobSearchPackageContext093T28.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);

                Log.Message("[RimMT] V0.9.3-T34D.2.3 Lean Live V2.1 initialized. " +
                    "Retained: consolidated S4 slow-search rescue, DoBill/Common Sense/merge exact-negative filters and text cache. " +
                    "S5.1 is merged into S4; exact-type Train/Repair negatives run only under clean Harmony authority. " +
                    "Removed: all T34-A/B/C/D fabrics, worker scheduler, frame dispatcher, tick stopwatch, haul worker index, " +
                    "T20/T21/T22/T26/T32 result or epoch infrastructure and resident censuses. " +
                    "The shipped assembly is built from an explicit whitelist and has no background worker, per-frame hook, reachability cache or reservation cache.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Consolidated Stable core initialization failed. RimMT will remain inert. " + ex);
            }
        }

        internal static bool DispatcherBridgePatched { get { return false; } }
    }
}










































