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

        static RimMTBootstrap()
        {
            try
            {
                RimMTThreadGuard.InitializeMainThread();
                RimMTRuntime.Initialize();

                Harmony harmony = new Harmony(HarmonyId);
                TryPatchDispatcher(harmony);
                RimMTPatches.Apply(harmony);
                PathGridInvalidation.ApplyBulkGuard(harmony);
                PathSnapshotSafetyPatches.Apply(harmony);
                SafePathClassTelemetry0418.Apply(harmony);
                WorkGiverDetailPatches.Initialize(harmony);

                // V0.4.18.3.1 regression rollback:
                // - Do not install the V0.4.18.3 stable-spatial consumer. Its first runtime
                //   sample produced signature reuse but zero authoritative accelerations.
                // - Do not restore the low-yield V0.4.18.2 identity-keyed async plan.
                // Broad/JobGiver nearest ordering remains the measured synchronous layer.
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);

                // Restore the V0.4.18.2/V0.4.16 ReachProfile implementation. The V0.4.18.3
                // frame-budgeted capture pump dramatically increased cold profile misses in the
                // measured 390-mod workload, exposing live Vanilla Reachability/RegionTraverser
                // again. Sampled positive/negative authority, cooldown and the global fuse stay on.
                AggressiveReachabilityProfiles.Apply(harmony);

                // RegionHint remains retired: prior measurements showed zero acceleration while
                // consuming snapshot/worker work. ReachProfile remains the connectivity offload.
                ParallelWorkPrefilter.Apply(harmony);
                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.4.18.3.1 reachability regression rollback initialized. The V0.4.18.3 frame-budgeted ReachProfile capture pump and stable-spatial GenClosest consumer are disabled. V0.4.18.2/V0.4.16 synchronous ReachProfile capture with sampled positive/negative authority is restored; the zero-yield RegionHint and low-yield identity-keyed async candidate plan remain retired. Final WorkGiver predicates, reservations, Job commits, mutable Verse state and Unity state remain main-thread owned. Path worker remains shadow-only.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Core initialization failed. RimMT will remain inert. " + ex);
            }
        }

        private static void TryPatchDispatcher(Harmony harmony)
        {
            try
            {
                MethodBase update = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (update == null)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "TickManagerUpdate was not found");
                    Log.Warning("[RimMT] TickManagerUpdate was not found; main-thread dispatcher will not drain automatically.");
                    return;
                }

                CompatibilityGuard.RegisterTarget("runtime.dispatcher", update);

                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePrefix));
                prefix.priority = Priority.First;
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(update, prefix: prefix, postfix: postfix);

                Log.Message("[RimMT] runtime.dispatcher bracket installed on TickManager.TickManagerUpdate; Butter++ commits use TickManagerPatch._midTickStarted as the logical-tick boundary.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "dispatcher patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] runtime.dispatcher patch failed; worker runtime remains initialized but main-thread callbacks will not auto-drain. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void TickManagerUpdatePrefix(ref long __state)
        {
            __state = 0L;
            if (!RuntimeCompatibility.ButterPlusPlusActive)
                return;
            if (FeatureGate.IsEnabled("runtime.adaptiveBurst") || FeatureGate.IsEnabled("diagnostics.hotPaths"))
                __state = Stopwatch.GetTimestamp();
        }

        public static void TickManagerUpdatePostfix(long __state)
        {
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive)
            {
                if (FeatureGate.IsEnabled("diagnostics.hotPaths"))
                    HotPathProfiler.End("TickManager.TickManagerUpdate[ButterSlice]", __state);
                if (FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                    AdaptiveLoadBalancer.RecordButterFrameSlice(__state);
            }

            RimMTRuntime.OnMainThreadFrame();
        }
    }
}
