using System;
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
                var update = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (update != null)
                {
                    CompatibilityGuard.RegisterTarget("runtime.dispatcher", update);
                    harmony.Patch(update, postfix: new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePostfix)));
                }
                else
                {
                    FeatureGate.Suppress("runtime.dispatcher", "TickManagerUpdate was not found");
                    Log.Warning("[RimMT] TickManagerUpdate was not found; main-thread dispatcher will not drain automatically.");
                }

                RimMTPatches.Apply(harmony);
                Log.Message("[RimMT] V0.4.1 playtest initialized. Adaptive burst profiling is active; unsafe Pawn/Thing/Reservation mutation remains on the main thread.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimMT] Initialization failed. RimMT will remain inert. " + ex);
            }
        }

        public static void TickManagerUpdatePostfix()
        {
            RimMTRuntime.OnMainThreadFrame();
        }
    }
}
