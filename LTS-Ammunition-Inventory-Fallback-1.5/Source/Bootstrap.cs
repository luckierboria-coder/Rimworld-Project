using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LTSAmmoInventoryFallback15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var h = new Harmony("local.ltsammo.inventoryfallback.1.5");
                PatchRegistry.Apply(h);
                Log.Message("[LTS Ammo Inventory Fallback 1.5] loaded (inventory fallback + kit mass limits 3/6/10 kg)");
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory Fallback 1.5] startup failed: " + e);
            }
        }
    }

    internal static class PatchRegistry
    {
        internal static readonly Type AmmoLogic = AccessTools.TypeByName("Ammunition.Logic.AmmoLogic");
        internal static readonly Type Settings = AccessTools.TypeByName("Ammunition.Settings.Settings");
        internal static readonly Type LoadKit = AccessTools.TypeByName("Ammunition.WorkGivers.WorkGiver_LoadKit");
        internal static readonly Type ToilsTake = AccessTools.TypeByName("Ammunition.Toils.Toils_Take");

        internal static void Apply(Harmony h)
        {
            if (AmmoLogic == null)
            {
                Log.Error("[LTS Ammo Inventory Fallback 1.5] LTS Ammunition not found");
                return;
            }

            var ammoCheck = AccessTools.Method(AmmoLogic, "AmmoCheck");
            h.Patch(ammoCheck, postfix: new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.AmmoCheckPostfix)));

            var warmup = AccessTools.Method(typeof(Verb), "WarmupComplete");
            if (warmup != null)
            {
                var p = new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.WarmupPrefix));
                p.priority = 200;
                h.Patch(warmup, prefix: p);
            }

            var next = AccessTools.Method(typeof(Verb), "TryCastNextBurstShot");
            if (next != null)
            {
                var p = new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.NextBurstShotPrefix));
                p.priority = 100;
                h.Patch(next, prefix: p);
            }

            var projectile = AccessTools.PropertyGetter(typeof(Verb_LaunchProjectile), "Projectile");
            if (projectile != null)
                h.Patch(projectile, postfix: new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.ProjectilePostfix)));

            if (LoadKit != null)
            {
                var skip = AccessTools.Method(LoadKit, "ShouldSkip");
                var scan = AccessTools.Method(LoadKit, "PotentialWorkThingsGlobal");
                var has = AccessTools.Method(LoadKit, "HasJobOnThing");
                var job = AccessTools.Method(LoadKit, "JobOnThing");
                var refill = new HarmonyMethod(typeof(InventoryKitRefill), nameof(InventoryKitRefill.RefillPrefix));

                if (skip != null)
                {
                    h.Patch(skip,
                        prefix: refill,
                        postfix: new HarmonyMethod(typeof(MassWorkGiver), nameof(MassWorkGiver.ShouldSkipPostfix)));
                }
                if (scan != null)
                {
                    h.Patch(scan,
                        prefix: refill,
                        postfix: new HarmonyMethod(typeof(MassWorkGiver), nameof(MassWorkGiver.PotentialWorkThingsGlobalPostfix)));
                }
                if (has != null)
                    h.Patch(has, postfix: new HarmonyMethod(typeof(MassWorkGiver), nameof(MassWorkGiver.HasJobOnThingPostfix)));
                if (job != null)
                    h.Patch(job, postfix: new HarmonyMethod(typeof(MassWorkGiver), nameof(MassWorkGiver.JobOnThingPostfix)));
            }

            if (ToilsTake != null)
            {
                var load = AccessTools.Method(ToilsTake, "LoadMagazine");
                var opportunistic = AccessTools.Method(ToilsTake, "OpportunisticLoadMagazine");
                if (load != null)
                    h.Patch(load, prefix: new HarmonyMethod(typeof(MassLoading), nameof(MassLoading.LoadMagazinePrefix)));
                if (opportunistic != null)
                    h.Patch(opportunistic, prefix: new HarmonyMethod(typeof(MassLoading), nameof(MassLoading.OpportunisticLoadMagazinePrefix)));
            }

            var loadSpawnedKit = AccessTools.Method(AmmoLogic, "LoadSpawnedKit");
            if (loadSpawnedKit != null)
                h.Patch(loadSpawnedKit, postfix: new HarmonyMethod(typeof(MassLoading), nameof(MassLoading.ClampGeneratedKitPostfix)));
        }
    }
}
