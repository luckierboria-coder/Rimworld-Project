using System;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace BPCAreaRestrictionSafety15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            var harmony = new Harmony("allen.bpc.arearestrictionsafety15");
            harmony.PatchAll();
            Log.Message("[BPC Area Safety 1.5] loaded. Area assignment validation + stale-area repair + Job OOB guard active.");
        }
    }

    internal static class AreaSafety
    {
        internal static bool IsValidForPawn(Area area, Pawn pawn, out string reason)
        {
            reason = null;
            if (area == null || pawn == null)
                return true;

            Map pawnMap = pawn.MapHeld;
            if (pawnMap == null)
                return true;

            try
            {
                if (area.areaManager == null)
                {
                    reason = "areaManager=null";
                    return false;
                }

                Map areaMap = area.areaManager.map;
                if (areaMap == null)
                {
                    reason = "areaMap=null";
                    return false;
                }

                if (areaMap != pawnMap)
                {
                    reason = "area belongs to a different map";
                    return false;
                }

                if (pawnMap.areaManager == null || pawnMap.areaManager.AllAreas == null ||
                    !pawnMap.areaManager.AllAreas.Contains(area))
                {
                    reason = "area is not registered in pawn current map AreaManager";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                reason = "validation exception: " + ex.GetType().Name;
                return false;
            }
        }

        internal static void WarnRepair(Pawn pawn, Area area, string source, string reason)
        {
            string pawnText = pawn != null ? pawn.ToStringSafe() : "<null>";
            string areaText = area != null ? (area.GetType().Name + "#" + area.ID) : "<null>";
            Log.Warning("[BPC Area Safety 1.5] repaired invalid area restriction at " + source +
                        ": pawn=" + pawnText + ", area=" + areaText + ", reason=" + reason +
                        ". Current-map restriction was reset to Unrestricted.");
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Setter)]
    internal static class AreaRestrictionSetterPatch
    {
        private static void Prefix(ref Area value, Pawn ___pawn)
        {
            if (value == null)
                return;

            if (!AreaSafety.IsValidForPawn(value, ___pawn, out string reason))
            {
                Area old = value;
                value = null;
                AreaSafety.WarnRepair(___pawn, old, "setter", reason);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.AreaRestrictionInPawnCurrentMap), MethodType.Getter)]
    internal static class AreaRestrictionGetterPatch
    {
        private static void Postfix(Pawn_PlayerSettings __instance, Pawn ___pawn, ref Area __result)
        {
            Area area = __result;
            if (area == null)
                return;

            if (!AreaSafety.IsValidForPawn(area, ___pawn, out string reason))
            {
                __result = null;
                __instance.AreaRestrictionInPawnCurrentMap = null;
                AreaSafety.WarnRepair(___pawn, area, "getter", reason);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_PlayerSettings), nameof(Pawn_PlayerSettings.EffectiveAreaRestrictionInPawnCurrentMap), MethodType.Getter)]
    internal static class EffectiveAreaRestrictionGetterPatch
    {
        private static void Postfix(Pawn_PlayerSettings __instance, Pawn ___pawn, ref Area __result)
        {
            Area area = __result;
            if (area == null)
                return;

            if (!AreaSafety.IsValidForPawn(area, ___pawn, out string reason))
            {
                __result = null;
                __instance.AreaRestrictionInPawnCurrentMap = null;
                AreaSafety.WarnRepair(___pawn, area, "effective-getter", reason);
            }
        }
    }

    [HarmonyPatch]
    internal static class JobIsTargetOutsideAreaPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(
                typeof(Job),
                "IsTargetOutsideArea",
                new[] { typeof(LocalTargetInfo), typeof(Area) });
        }

        private static bool Prefix(LocalTargetInfo target, Area zone, ref bool __result)
        {
            if (zone == null)
            {
                __result = false;
                return false;
            }

            Map map;
            try
            {
                map = zone.areaManager?.map;
            }
            catch
            {
                map = null;
            }

            if (map == null)
            {
                __result = false;
                return false;
            }

            IntVec3 cell = target.Cell;
            if (cell.IsValid && !cell.InBounds(map))
            {
                __result = false;
                return false;
            }

            return true;
        }
    }
}
