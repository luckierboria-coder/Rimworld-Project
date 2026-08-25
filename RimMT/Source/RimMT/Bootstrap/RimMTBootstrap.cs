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
                var postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePostfix));
                if (update != null)
                    harmony.Patch(update, postfix: postfix);
                else
                    Log.Warning("[RimMT] TickManagerUpdate was not found; main-thread dispatcher will not drain automatically.");

                Log.Message("[RimMT] V0.2 foundation initialized. No Pawn/Thing/Reservation parallel ticking is enabled.");
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
