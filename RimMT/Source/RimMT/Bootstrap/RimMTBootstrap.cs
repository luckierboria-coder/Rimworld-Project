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
                WorkGiverDetailPatches.Initialize(harmony);
                AdaptiveGenClosestAssist.Apply(harmony);
                AggressiveReachabilityProfiles.Apply(harmony);
                ParallelRegionConnectivity.Apply(harmony);
                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.4.16.2 aggressive-parallel playtest initialized. Exact coexistence is enabled for VFE Phasing, Pathfinding Framework's diagnostics-only Reachability Postfix, and Hospitality's guest-area restriction Postfix. Hospitality remains downstream authority and may only tighten a profile true result to false for arrived guests outside GuestArea. Scheduler fan-out from V0.4.15 is retained. Per-Pawn immutable profiles are built on workers, sampled predictions may bypass live Reachability/RegionTraverser, and parity mismatches trigger per-profile cooldown plus a global fuse. Persistent map search, hauling accelerators, path shadow validation, Butter++ logical-tick barriers and Vanilla main-thread Job/reservation commits are retained.");
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
