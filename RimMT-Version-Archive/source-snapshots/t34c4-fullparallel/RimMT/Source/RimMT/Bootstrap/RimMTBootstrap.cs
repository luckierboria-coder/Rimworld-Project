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
        internal const string Version = "0.9.3-t34d3.2.1-negative-yield-rollback";

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
            HaulMergePatchCensus093T5.Apply();
                PathGridInvalidation.ApplyBulkGuard(harmony);

                // T28 owns the single synchronous JobGiver_Work package boundary.
                // The old persistent-fabric GenClosest consumer is retired after repeated
                // runtime evidence of zero accelerations; Vanilla/Broad/T22 remain authoritative.
                JobSearchPackageContext093T28.Apply(harmony);
                CandidateFabric093T34A.Apply(harmony);
                ScannerParallelFabric093T34B.Apply(harmony);
                AggressiveParallelScanner093T34D.Apply(harmony);
                CleanPathfindingGlowCache093T34D3.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                // T27.3: retire T18/T19 mobile custom-source rescue. It changes validator/reach
                // visitation semantics for custom Pawn lists; Vanilla GenClosest stays authoritative.
                StorytellerDeepAttribution093T18.Initialize();
                JobSearchTransaction093T20.Apply(harmony);
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                // T27/T27.1 speculative source reordering is intentionally retired in T27.2.
                // Do not install ParallelWorkKernel093T27 on any production hot path.
                WorkGiverParallelSafety093T27_2.Initialize();
                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.9.3-T34D.2.2 DoBill Worker Retirement initialized. The measured D.2/D.2.1 live BillStack.AnyShouldDoNow worker path and its feature gate, batches, waits, scratch buffers and telemetry have been removed after a 70.1% worker-failure rate and 4.915ms average successful wait. DoBill retains the persistent membership index and package-local false readiness memo on the main thread. T34-D.1.3 Reachability and MapPawns guards, worker validator fast path and scanner quarantine remain active. T20/T21 main-thread transaction core retained; repeated package-local GenClosest_Global_NewTemp IList sources use a distance/source-order index; dispatcher drain moved from TickManagerUpdate to Root_Play.Update for SimplyMoreFPS coexistence; direct WorldTick boundary timing retained; WorldRoot attribution restored; ReachProfile Region.Allows capture moved out of CanReach into a bounded Root_Play frame queue. Single-DLL production mode: " +
                    "diagnostic hot-path probes, PathSnapshot shadow validation, SafePath telemetry and WorkPrefilter are not installed. " +
                    "T34-C.4 production paths are retained as the baseline; T34-D.2.2 removes the negative-yield DoBill live-worker experiment while retaining proven worker paths.");
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










































