using System;
using System.Linq;
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
                var h = new Harmony("local.ltsammo.inventorysystem.1.5");
                PatchRegistry.Apply(h);
                Log.Message("[LTS Ammo Inventory System 1.5] loaded");
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory System 1.5] startup failed: " + e);
            }
        }
    }

    internal static class PatchRegistry
    {
        internal static readonly Type AmmoLogic = AccessTools.TypeByName("Ammunition.Logic.AmmoLogic");
        internal static readonly Type Settings = AccessTools.TypeByName("Ammunition.Settings.Settings");
        internal static readonly Type LoadKit = AccessTools.TypeByName("Ammunition.WorkGivers.WorkGiver_LoadKit");
        internal static readonly Type KitType = AccessTools.TypeByName("Ammunition.Things.Kit");

        internal static void Apply(Harmony h)
        {
            if (AmmoLogic == null)
            {
                Log.Error("[LTS Ammo Inventory System 1.5] LTS Ammunition not found");
                return;
            }

            var ammoCheck = AccessTools.Method(AmmoLogic, "AmmoCheck");
            if (ammoCheck != null)
            {
                var pre = new HarmonyMethod(typeof(LegacyKitBypass), nameof(LegacyKitBypass.AmmoCheckPrefix));
                pre.priority = 200;
                h.Patch(ammoCheck,
                    prefix: pre,
                    postfix: new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.AmmoCheckPostfix)));
            }

            var loadSpawnedKit = AccessTools.Method(AmmoLogic, "LoadSpawnedKit");
            if (loadSpawnedKit != null)
                h.Patch(loadSpawnedKit,
                    prefix: new HarmonyMethod(typeof(LegacyKitBypass), nameof(LegacyKitBypass.LoadSpawnedKitPrefix)));

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
                h.Patch(projectile,
                    postfix: new HarmonyMethod(typeof(AmmoFallback), nameof(AmmoFallback.ProjectilePostfix)));

            if (LoadKit != null)
            {
                var skip = AccessTools.Method(LoadKit, "ShouldSkip");
                if (skip != null)
                    h.Patch(skip,
                        prefix: new HarmonyMethod(typeof(LegacyKitBypass), nameof(LegacyKitBypass.WorkGiverShouldSkipPrefix)));

                var potential = AccessTools.Method(LoadKit, "PotentialWorkThingsGlobal");
                if (potential != null)
                    h.Patch(potential,
                        prefix: new HarmonyMethod(typeof(LegacyKitBypass), nameof(LegacyKitBypass.WorkGiverPotentialPrefix)));
            }

            if (KitType != null)
            {
                var gizmos = AccessTools.Method(KitType, "GetWornGizmos");
                if (gizmos != null)
                    h.Patch(gizmos,
                        postfix: new HarmonyMethod(typeof(LegacyKitBypass), nameof(LegacyKitBypass.FilterKitGizmosPostfix)));
            }

            var generatePawn = typeof(PawnGenerator).GetMethods()
                .FirstOrDefault(m => m.Name == "GeneratePawn" && m.GetParameters().Length == 1);
            if (generatePawn != null)
            {
                var post = new HarmonyMethod(typeof(NpcAmmoGeneration), nameof(NpcAmmoGeneration.GeneratePawnPostfix));
                // Harmony postfixes execute from lower to higher priority. Priority.First makes this run
                // after LTS' normal-priority GeneratePawn postfix, so the kit's carry-capacity bonus is included.
                post.priority = Priority.First;
                h.Patch(generatePawn, postfix: post);
            }
        }
    }
}
