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

                // JS1.2.1 Lean Hybrid keeps JS1.1 cache/store behavior plus the validated typed
                // HasJobOnThing prefix, last-bucket fast path and light context reuse.
                JobPackageLocalSearch0419.Apply(harmony);

                // JR1 directly targets the JD1-confirmed RegionTraverser hotspot without caching
                // traversal/search results: only repeated Region.Allows predicates inside one
                // synchronous JobPackage are memoized and parity sampled.
                JobPackageRegionAllows0419.Apply(harmony);

                AggressiveReachabilityProfiles.Apply(harmony);

                // V0.4.15 RegionHint remains retired. ReachProfile is the connectivity authority;
                // JR1 also replaces the lifetime mismatch fuse with rolling soft/hard safety.
                // ParallelRegionConnectivity.Apply(harmony); intentionally not installed.

                ParallelWorkPrefilter.Apply(harmony);
                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.4.19-JR1 initialized from JS1.2.1 Lean Hybrid. JD1 identified ClosestThingReachable -> RegionTraverser as the dominant JobGiver hotspot, so JR1 adds one-JobPackage Region.Allows memoization with live-first sampled parity. ReachProfile now uses a rolling soft fuse/cooldown/probation recovery plus an emergency hard fuse instead of lifetime mismatch accumulation. JS1 nearest-order, HasJobOnThing behavior, WorkPrefilter, haul accelerators, Path shadow and Vanilla final authority remain unchanged.");
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
