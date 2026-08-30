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
                AdaptiveGenClosestAssist.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);

                // S4 is built directly on the validated S1 baseline. S2 fanout and S3 learned
                // admission are absent. The only new behavior is elapsed-time Tail Rescue after
                // the current JobPackage is already over 32ms.
                JobPackageLocalSearch0419.Apply(harmony);

                // ReachProfile prediction/build behavior and the embedded rolling fuse are
                // unchanged from JS1.1R.
                AggressiveReachabilityProfiles.Apply(harmony);

                // V0.4.15 RegionHint remains retired due near-zero long-run yield.
                // ParallelRegionConnectivity.Apply(harmony); intentionally not installed.

                ParallelWorkPrefilter.Apply(harmony);
                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.4.19-JS1.1S4 Tail Rescue initialized from validated JS1.1S1. Ordinary JobPackages remain S1: >=256 ThingRequest ClosestThingReachable calls may use the validated fast path. Small/medium searches remain Vanilla until the current TryIssueJobPackage has already exceeded 32ms; only then may later >=16 ThingRequest searches and explicit custom IEnumerable<Thing> sets use nearest-first validator-first/live-CanReach rescue. S2 fanout16 and S3 learned admission are absent. JR1/JR1.1 remain retired and ReachProfile rolling-fuse behavior is unchanged.");
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