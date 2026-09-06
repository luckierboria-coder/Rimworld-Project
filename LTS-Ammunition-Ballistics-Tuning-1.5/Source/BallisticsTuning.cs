using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Verse;

namespace LTSAmmoBallisticsTuning15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("allen.ltsammo.ballisticstuning.1.5");
                PatchRegistry.Apply(harmony);
                Log.Message("[LTS Ammo Ballistics Tuning 1.5] loaded");
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] startup failed: " + e);
            }
        }
    }

    internal static class PatchRegistry
    {
        internal static readonly Type AmmoLogicType = AccessTools.TypeByName("Ammunition.Logic.AmmoLogic");
        internal static readonly Type InventoryAmmoFallbackType = AccessTools.TypeByName("LTSAmmoInventoryFallback15.AmmoFallback");
        internal static readonly MethodInfo WeaponCanUseAmmoMethod = AmmoLogicType == null
            ? null
            : AccessTools.Method(AmmoLogicType, "WeaponDefCanUseAmmoDef");
        internal static readonly FieldInfo ShotAmmoField = InventoryAmmoFallbackType == null
            ? null
            : AccessTools.Field(InventoryAmmoFallbackType, "ShotAmmo");

        internal static void Apply(Harmony harmony)
        {
            if (AmmoLogicType == null)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] LTS Ammunition framework not found");
                return;
            }

            if (InventoryAmmoFallbackType == null || ShotAmmoField == null)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] LTS Inventory System Patch not found or incompatible");
                return;
            }

            var launch = typeof(Projectile).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Launch" && m.GetParameters().Length == 8);
            if (launch != null)
                harmony.Patch(launch, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ProjectileLaunchPostfix)));

            var damageGetter = AccessTools.PropertyGetter(typeof(Projectile), "DamageAmount");
            if (damageGetter != null)
                harmony.Patch(damageGetter, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.DamageAmountPostfix)));

            var armorGetter = AccessTools.PropertyGetter(typeof(Projectile), "ArmorPenetration");
            if (armorGetter != null)
                harmony.Patch(armorGetter, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ArmorPenetrationPostfix)));

            var bleedGetter = AccessTools.PropertyGetter(typeof(Hediff_Injury), "BleedRate");
            if (bleedGetter != null)
                harmony.Patch(bleedGetter, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.BleedRatePostfix)));
        }
    }

    internal enum WeaponAmmoClass
    {
        Ballistic,
        ShortArrow,
        RecurveArrow,
        GreatArrow,
        OtherArrow,
        Bolt
    }

    internal sealed class ShotContext
    {
        internal ThingDef AmmoDef;
        internal ThingDef WeaponDef;
        internal float DamageMultiplier;
    }

    internal static class AmmoClassifier
    {
        private static readonly HashSet<string> ShortArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_ShortArrow",
            "LTS_ShortFlameArrow"
        };

        private static readonly HashSet<string> RecurveArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_RecurveArrow",
            "LTS_RecurveFlameArrow"
        };

        private static readonly HashSet<string> GreatArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_GreatArrow",
            "LTS_GreatFlameArrow"
        };

        private static readonly string[] PolymerArrowDefs =
        {
            "LTS_PolymerBroadArrow",
            "LTS_PolymerBulletpointArrow",
            "LTS_PolymerIncendiaryArrow",
            "LTS_PolymerEMPArrow",
            "LTS_PolymerExplosiveArrow"
        };

        private static readonly Dictionary<ThingDef, WeaponAmmoClass> WeaponClassCache = new Dictionary<ThingDef, WeaponAmmoClass>();

        internal static float DamageMultiplier(ThingDef ammoDef, ThingDef weaponDef)
        {
            if (ammoDef != null)
            {
                string name = ammoDef.defName ?? string.Empty;
                if (ShortArrows.Contains(name)) return 0.90f;
                if (GreatArrows.Contains(name)) return 1.25f;
                if (RecurveArrows.Contains(name)) return 1.00f;
                if (name.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0) return 1.00f;
                if (name.IndexOf("Bolt", StringComparison.OrdinalIgnoreCase) >= 0) return 1.00f;
            }

            switch (ClassifyWeapon(weaponDef))
            {
                case WeaponAmmoClass.ShortArrow: return 0.90f;
                case WeaponAmmoClass.GreatArrow: return 1.25f;
                case WeaponAmmoClass.RecurveArrow:
                case WeaponAmmoClass.OtherArrow:
                case WeaponAmmoClass.Bolt:
                    return 1.00f;
                default:
                    return 1.50f;
            }
        }

        internal static bool IsRecurveWeapon(ThingDef weaponDef)
        {
            return weaponDef != null && ClassifyWeapon(weaponDef) == WeaponAmmoClass.RecurveArrow;
        }

        private static WeaponAmmoClass ClassifyWeapon(ThingDef weaponDef)
        {
            if (weaponDef == null) return WeaponAmmoClass.Ballistic;

            WeaponAmmoClass cached;
            if (WeaponClassCache.TryGetValue(weaponDef, out cached)) return cached;

            WeaponAmmoClass result = WeaponAmmoClass.Ballistic;
            if (CanUse(weaponDef, "LTS_ShortArrow") || CanUse(weaponDef, "LTS_ShortFlameArrow"))
                result = WeaponAmmoClass.ShortArrow;
            else if (CanUse(weaponDef, "LTS_RecurveArrow") || CanUse(weaponDef, "LTS_RecurveFlameArrow"))
                result = WeaponAmmoClass.RecurveArrow;
            else if (CanUse(weaponDef, "LTS_GreatArrow") || CanUse(weaponDef, "LTS_GreatFlameArrow"))
                result = WeaponAmmoClass.GreatArrow;
            else if (PolymerArrowDefs.Any(a => CanUse(weaponDef, a)))
                result = WeaponAmmoClass.OtherArrow;
            else if (CanUse(weaponDef, "LTS_RegularBolt"))
                result = WeaponAmmoClass.Bolt;

            WeaponClassCache[weaponDef] = result;
            return result;
        }

        private static bool CanUse(ThingDef weaponDef, string ammoDefName)
        {
            if (weaponDef == null || PatchRegistry.WeaponCanUseAmmoMethod == null) return false;
            ThingDef ammoDef = DefDatabase<ThingDef>.GetNamedSilentFail(ammoDefName);
            if (ammoDef == null) return false;

            try
            {
                object value = PatchRegistry.WeaponCanUseAmmoMethod.Invoke(null, new object[] { weaponDef, ammoDef });
                return value is bool && (bool)value;
            }
            catch
            {
                return false;
            }
        }
    }

    internal static class BallisticsRuntime
    {
        private static readonly ConditionalWeakTable<Projectile, ShotContext> ProjectileContexts = new ConditionalWeakTable<Projectile, ShotContext>();

        public static void ProjectileLaunchPostfix(Projectile __instance, Thing equipment)
        {
            if (__instance == null) return;

            try
            {
                Thing weapon = equipment;
                if (weapon == null && __instance.Launcher is Pawn pawn && pawn.equipment != null)
                {
                    ThingWithComps primary = pawn.equipment.Primary;
                    if (primary != null && (__instance.EquipmentDef == null || primary.def == __instance.EquipmentDef))
                        weapon = primary;
                }

                if (weapon == null) return;

                IDictionary shotAmmo = PatchRegistry.ShotAmmoField.GetValue(null) as IDictionary;
                if (shotAmmo == null || !shotAmmo.Contains(weapon)) return;

                ThingDef ammoDef = shotAmmo[weapon] as ThingDef;
                if (ammoDef == null) return;

                var context = new ShotContext
                {
                    AmmoDef = ammoDef,
                    WeaponDef = weapon.def,
                    DamageMultiplier = AmmoClassifier.DamageMultiplier(ammoDef, weapon.def)
                };

                ProjectileContexts.Remove(__instance);
                ProjectileContexts.Add(__instance, context);
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] projectile context capture failed: " + e);
            }
        }

        public static void DamageAmountPostfix(Projectile __instance, ref int __result)
        {
            ShotContext context;
            if (__instance == null || !ProjectileContexts.TryGetValue(__instance, out context)) return;
            if (context == null || Math.Abs(context.DamageMultiplier - 1f) < 0.0001f) return;

            __result = Math.Max(0, (int)Math.Round(__result * context.DamageMultiplier, MidpointRounding.AwayFromZero));
        }

        public static void ArmorPenetrationPostfix(Projectile __instance, ref float __result)
        {
            ShotContext context;
            if (__instance == null || !ProjectileContexts.TryGetValue(__instance, out context) || context == null) return;
            __result *= 1.10f;
        }

        public static void BleedRatePostfix(Hediff_Injury __instance, ref float __result)
        {
            if (__instance == null || __result <= 0f) return;
            if (__instance.def == null || __instance.def.defName != "Stab") return;
            if (!AmmoClassifier.IsRecurveWeapon(__instance.sourceDef)) return;
            __result *= 1.25f;
        }
    }
}
