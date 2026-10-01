using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    [StaticConstructorOnStartup]
    internal static class RimMTBootstrap
    {
        internal const string HarmonyId = "allen.rimmt";
        internal const string Version = "0.9.3-t34d2.3-clean-idle-fix";

        static RimMTBootstrap()
        {
            try
            {
                RimMTThreadGuard.InitializeMainThread();
                RimMTRuntime.Initialize();

                Harmony harmony = new Harmony(HarmonyId);
                TryPatchDispatcher(harmony);
                RimMTPatches.Apply(harmony);
            WorkGiverMergePartnerIndex093T4.Apply(harmony);
                PathGridInvalidation.ApplyBulkGuard(harmony);

                // T28 owns the single synchronous JobGiver_Work package boundary.
                // The old persistent-fabric GenClosest consumer is retired after repeated
                // runtime evidence of zero accelerations; Vanilla/Broad/T22 remain authoritative.
                JobSearchPackageContext093T28.Apply(harmony);
                CandidateFabric093T34A.Apply(harmony);
                ScannerParallelFabric093T34B.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                HaulWorkAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.9.3-T34D.2.3 Clean Idle Fix initialized. " +
                    "Retained: T34-B snapshot candidate planning, primitive-only T34-C classification, " +
                    "nearest-first search, S4 slow-search rescue, haul index, merge-negative filter, text cache and path-topology batching. " +
                    "Removed: T34-D live worker validators, T20/T21 validator and Reachability result replay, " +
                    "T22 result index, T32 reservation replay, T26 tick epoch, safety census, patch census and asset cleanup hooks. " +
                    "WorkGiver validators, Reachability and reservations now execute live on the main thread.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Consolidated Stable core initialization failed. RimMT will remain inert. " + ex);
            }
        }

        internal static bool DispatcherBridgePatched { get; private set; }

        private static void TryPatchDispatcher(Harmony harmony)
        {
            try
            {
                // T22 deliberately leaves TickManager.TickManagerUpdate alone. Simply More FPS owns
                // a transpiler there; draining at Root_Play.Update is a rendered-frame/main-thread
                // boundary and avoids competing with frame-budget/tick-loop rewriting.
                MethodBase update = AccessTools.Method(typeof(Root_Play), "Update");
                if (update == null)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "Root_Play.Update was not found");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePrefix))
                    { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePostfix))
                    { priority = Priority.Last };
                harmony.Patch(update, prefix: prefix, postfix: postfix);
                DispatcherBridgePatched = true;
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "Root_Play dispatcher bridge failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] Root_Play dispatcher bridge failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RootPlayUpdatePrefix(ref long __state)
        {
            __state = 0L;
            if (RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                __state = Stopwatch.GetTimestamp();
        }

        public static void RootPlayUpdatePostfix(long __state)
        {
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive &&
                FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                AdaptiveLoadBalancer.RecordButterFrameSlice(__state);

            RimMTRuntime.OnMainThreadFrame();
        }
    }
}










































