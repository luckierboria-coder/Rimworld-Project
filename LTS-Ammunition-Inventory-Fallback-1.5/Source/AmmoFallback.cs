using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LTSAmmoInventoryFallback15
{
    internal static class AmmoFallback
    {
        private static readonly MethodInfo CanUseAmmo = AccessTools.Method(PatchRegistry.AmmoLogic, "WeaponDefCanUseAmmoDef");
        private static readonly Dictionary<Thing, ThingDef> InventoryAmmo = new Dictionary<Thing, ThingDef>();
        private static readonly Dictionary<Thing, ThingDef> ShotAmmo = new Dictionary<Thing, ThingDef>();
        private static readonly HashSet<Thing> PaidBurst = new HashSet<Thing>();

        public static void WarmupPrefix(Verb __instance)
        {
            var weapon = __instance?.EquipmentSource;
            if (weapon == null) return;
            PaidBurst.Remove(weapon);
            ShotAmmo.Remove(weapon);
        }

        public static void AmmoCheckPostfix(Pawn pawn, Thing weapon, bool consumeAmmo, ref bool __result)
        {
            if (pawn == null || weapon == null) return;

            if (__result)
            {
                InventoryAmmo.Remove(weapon);
                return;
            }

            var carried = FindCompatible(pawn, weapon);
            if (carried != null)
            {
                InventoryAmmo[weapon] = carried.def;
                __result = true;
                return;
            }

            InventoryAmmo.Remove(weapon);

            if (!consumeAmmo && PaidBurst.Contains(weapon) && ShotAmmo.ContainsKey(weapon))
                __result = true;
        }

        public static void NextBurstShotPrefix(Verb __instance)
        {
            try
            {
                if (__instance == null || __instance.IsMeleeAttack) return;
                var weapon = __instance.EquipmentSource;
                var pawn = __instance.CasterPawn;
                if (weapon == null || pawn == null) return;
                if (!InventoryAmmo.TryGetValue(weapon, out var ammoDef) || ammoDef == null) return;

                ShotAmmo[weapon] = ammoDef;

                bool perBullet = GetBoolSetting("UseAmmoPerBullet", true);
                bool shouldConsume = pawn.IsColonist || GetBoolSetting("NpcUseAmmo", true);

                if (shouldConsume && (perBullet || !PaidBurst.Contains(weapon)))
                {
                    var stack = FindExact(pawn, ammoDef);
                    if (stack != null) ConsumeOne(stack);
                    if (!perBullet) PaidBurst.Add(weapon);
                }

                int extra = GetBurstCount(ammoDef) - 1;
                for (int i = 0; i < extra; i++)
                    Traverse.Create(__instance).Method("TryCastShot").GetValue();
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory Fallback 1.5] shot handling failed: " + e);
            }
        }

        public static void ProjectilePostfix(Verb_LaunchProjectile __instance, ref ThingDef __result)
        {
            var weapon = __instance?.EquipmentSource;
            if (weapon == null) return;

            ThingDef ammo;
            if (!ShotAmmo.TryGetValue(weapon, out ammo) && !InventoryAmmo.TryGetValue(weapon, out ammo)) return;
            var bullet = GetBulletDef(ammo);
            if (bullet != null) __result = bullet;
        }

        private static Thing FindCompatible(Pawn pawn, Thing weapon)
        {
            if (pawn.inventory?.innerContainer == null || CanUseAmmo == null) return null;
            foreach (var t in pawn.inventory.innerContainer)
            {
                if (t == null || t.Destroyed || t.stackCount <= 0) continue;
                try
                {
                    if ((bool)CanUseAmmo.Invoke(null, new object[] { weapon.def, t.def })) return t;
                }
                catch { }
            }
            return null;
        }

        private static Thing FindExact(Pawn pawn, ThingDef def)
        {
            if (pawn.inventory?.innerContainer == null) return null;
            foreach (var t in pawn.inventory.innerContainer)
                if (t != null && !t.Destroyed && t.stackCount > 0 && t.def == def) return t;
            return null;
        }

        private static void ConsumeOne(Thing stack)
        {
            var used = stack.SplitOff(1);
            used.Destroy(DestroyMode.Vanish);
        }

        private static bool GetBoolSetting(string name, bool fallback)
        {
            try
            {
                var p = AccessTools.Property(PatchRegistry.Settings, name);
                return p == null ? fallback : (bool)p.GetValue(null, null);
            }
            catch { return fallback; }
        }

        private static object AmmoExtension(ThingDef ammo)
        {
            if (ammo?.modExtensions == null) return null;
            foreach (var ext in ammo.modExtensions)
                if (ext != null && ext.GetType().FullName == "Ammunition.DefModExtensions.AmmunitionExtension") return ext;
            return null;
        }

        private static ThingDef GetBulletDef(ThingDef ammo)
        {
            var ext = AmmoExtension(ammo);
            var f = ext == null ? null : AccessTools.Field(ext.GetType(), "bulletDef");
            return f?.GetValue(ext) as ThingDef;
        }

        private static int GetBurstCount(ThingDef ammo)
        {
            var ext = AmmoExtension(ammo);
            var f = ext == null ? null : AccessTools.Field(ext.GetType(), "burstCount");
            if (f == null) return 1;
            try { return Math.Max(1, (int)f.GetValue(ext)); } catch { return 1; }
        }
    }
}
