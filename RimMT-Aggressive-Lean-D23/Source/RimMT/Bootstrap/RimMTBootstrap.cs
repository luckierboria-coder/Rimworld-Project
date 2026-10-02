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
        internal const string Version = "0.9.3-t34d2.3-aggressive-lean-v1";

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
                JobSearchPackageContext093T28.Apply(harmony);
                AggressiveParallelScanner093T34D.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);

                Log.Message("[RimMT] V0.9.3-T34D.2.3 Aggressive Lean V1 initialized. " +
                    "Retained: T34-D, T21/T28/T32, DoBill, T4, Common Sense and text cache. " +
                    "Removed: T34-A/B/C, S4/S5.1, T22, T26 and resident attribution probes.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Aggressive Lean initialization failed closed. " + ex);
            }
        }

        internal static bool DispatcherBridgePatched { get; private set; }

        private static void TryPatchDispatcher(Harmony harmony)
        {
            try
            {
                MethodBase update = AccessTools.Method(typeof(Root_Play), "Update");
                if (update == null)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "Root_Play.Update was not found");
                    return;
                }
                harmony.Patch(update,
                    prefix: new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePostfix)) { priority = Priority.Last });
                DispatcherBridgePatched = true;
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "Root_Play dispatcher bridge failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] Root_Play dispatcher bridge failed closed: " + ex.GetType().Name + ": " + ex.Message);
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
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                AdaptiveLoadBalancer.RecordButterFrameSlice(__state);
            RimMTRuntime.OnMainThreadFrame();
        }
    }
}
