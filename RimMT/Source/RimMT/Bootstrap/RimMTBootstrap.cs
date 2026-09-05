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
        internal const string Version = "0.10.0-lean-mt-rebase";

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

                // Keep only production paths with demonstrated yield. The old generic
                // PersistentMapSearchFabric consumer and DoBill worker-tail lane are intentionally
                // not installed: long-run reports showed zero actual accelerations.
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
                JobGiverSlowSearch0419S.Apply(harmony);
                AggressiveReachabilityProfilesV17.Apply(harmony);
                LeanMTProductionGuard100.Apply(harmony);

                HaulWorkAccelerator.Apply(harmony);
                GlobalHaulAccelerator.Apply(harmony);

                Log.Message("[RimMT] V0.10.0 Lean MT Rebase initialized. Production policy: keep validated S5.1/S4/RC2/DoBill persistent index/CommonSense/ReachProfile/haul/text-cache/topology paths; " +
                    "generic zero-yield GenClosest consumer, DoBill worker-tail, Resumable JobGiver and Predictive Admission are not production paths. " +
                    "New main-thread ReachProfile capture work is pressure-capped; final game-state authority remains Vanilla/fail-closed.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Lean MT Rebase core initialization failed. RimMT will remain inert. " + ex);
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
                    return;
                }

                CompatibilityGuard.RegisterTarget("runtime.dispatcher", update);
                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePrefix)) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePostfix)) { priority = Priority.Last };
                harmony.Patch(update, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "dispatcher patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] dispatcher patch failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void TickManagerUpdatePrefix(ref long __state)
        {
            __state = 0L;
            if (RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                __state = Stopwatch.GetTimestamp();
        }

        public static void TickManagerUpdatePostfix(long __state)
        {
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                AdaptiveLoadBalancer.RecordButterFrameSlice(__state);

            RimMTRuntime.OnMainThreadFrame();
        }
    }
}
