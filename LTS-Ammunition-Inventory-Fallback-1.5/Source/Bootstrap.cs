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
                Log.Message("[LTS Ammo Inventory Fallback 1.5] loaded");
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
                var refill = new HarmonyMethod(typeof(InventoryKitRefill), nameof(InventoryKitRefill.RefillPrefix));
                if (skip != null) h.Patch(skip, prefix: refill);
                if (scan != null) h.Patch(scan, prefix: refill);
            }
        }
    }
}
