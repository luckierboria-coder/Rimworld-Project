using System;
using HarmonyLib;
using RimWorld;
using Verse;

namespace GrenadeOneUse15
{
    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    internal static class Patch_VerbLaunchProjectile_TryCastShot_ConsumeThrowable
    {
        private const string ThrownRulePackDefName = "Combat_RangedFire_Thrown";

        private static void Postfix(Verb_LaunchProjectile __instance, bool __result)
        {
            if (!__result || __instance == null) return;

            try
            {
                ThingWithComps source = __instance.EquipmentSource;
                if (!ShouldConsume(__instance, source)) return;
                ConsumeOne(source);
            }
            catch (Exception e)
            {
                Log.Error("[Grenade One-Use 1.5] throwable consumption failed: " + e);
            }
        }

        private static bool ShouldConsume(Verb_LaunchProjectile verb, ThingWithComps source)
        {
            if (source == null || source.Destroyed || source.stackCount <= 0) return false;
            if (source is Apparel) return false;

            VerbProperties props = verb.verbProps;
            if (props == null || props.rangedFireRulepack == null) return false;

            if (!string.Equals(props.rangedFireRulepack.defName, ThrownRulePackDefName, StringComparison.Ordinal))
                return false;

            Type verbClass = props.verbClass;
            if (verbClass != null && !typeof(Verb_LaunchProjectile).IsAssignableFrom(verbClass))
                return false;

            ThingDef projectile = null;
            try { projectile = verb.Projectile; }
            catch { projectile = props.defaultProjectile; }

            // V1.1: only grenade-like / area-effect thrown projectiles are consumed here.
            // Physical thrown weapons (javelins, throwing axes, throwing knives, etc.)
            // are intentionally excluded so Recoverable Throwables can own their lifecycle.
            if (IsGrenadeLikeProjectile(projectile))
                return true;

            return NameLooksDisposableGrenade(source.def?.defName) &&
                   (source.def?.tools == null || source.def.tools.Count == 0);
        }

        private static bool IsGrenadeLikeProjectile(ThingDef projectile)
        {
            if (projectile?.projectile == null)
                return false;

            Type projectileClass = projectile.thingClass;
            if (projectileClass != null && typeof(Projectile_Explosive).IsAssignableFrom(projectileClass))
                return true;

            return projectile.projectile.explosionRadius > 0.01f;
        }

        private static bool NameLooksDisposableGrenade(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string s = value.ToLowerInvariant();
            return s.Contains("grenade") ||
                   s.Contains("molotov") ||
                   s.Contains("dynamite") ||
                   s.Contains("bomb") ||
                   s.Contains("explosive") ||
                   s.Contains("smoke") ||
                   s.Contains("emp") ||
                   s.Contains("gas");
        }

        private static void ConsumeOne(ThingWithComps source)
        {
            if (source.stackCount > 1)
            {
                source.stackCount--;
                return;
            }

            if (source.ParentHolder is Pawn_EquipmentTracker equipmentTracker)
            {
                equipmentTracker.Remove(source);
            }
            else
            {
                ThingOwner owner = source.ParentHolder?.GetDirectlyHeldThings();
                owner?.Remove(source);
            }

            if (!source.Destroyed)
                source.Destroy(DestroyMode.Vanish);
        }
    }
}
