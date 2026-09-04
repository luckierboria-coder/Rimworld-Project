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

            // Apparel reloadable belts use a static projectile verb but are containers/launch platforms,
            // not the thrown physical item itself. Never consume apparel.
            if (source is Apparel) return false;

            VerbProperties props = verb.verbProps;
            if (props == null || props.rangedFireRulepack == null) return false;

            // This is the semantic marker used by Core grenades and mods inheriting/copying
            // vanilla grenade behavior. Guns, bows and launchers normally use other rulepacks.
            if (!string.Equals(props.rangedFireRulepack.defName, ThrownRulePackDefName, StringComparison.Ordinal))
                return false;

            Type verbClass = props.verbClass;
            if (verbClass != null && !typeof(Verb_LaunchProjectile).IsAssignableFrom(verbClass))
                return false;

            return true;
        }

        private static void ConsumeOne(ThingWithComps source)
        {
            // If another mod already allows throwable stacks, consume exactly one physical unit
            // and leave the remainder equipped. We deliberately do not alter stackLimit ourselves.
            if (source.stackCount > 1)
            {
                source.stackCount--;
                return;
            }

            // Remove from its holder before destruction so equipment/apparel/inventory trackers
            // remain internally consistent. The common case is Pawn_EquipmentTracker.
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
