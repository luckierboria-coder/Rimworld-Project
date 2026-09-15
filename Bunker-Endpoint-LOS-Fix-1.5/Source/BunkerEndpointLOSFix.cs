using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace BunkerEndpointLOSFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("allen.ra2bunker.walllos.v3");

                MethodInfo standard = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new Type[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool),
                    typeof(Func<IntVec3, bool>), typeof(int), typeof(int)
                });

                MethodInfo rect = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new Type[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(CellRect), typeof(CellRect),
                    typeof(Func<IntVec3, bool>), typeof(bool)
                });

                if (standard == null || rect == null)
                {
                    Log.Error("[Ra2Bunker Endpoint LOS Fix V3] GenSight overload lookup failed; no LOS patches were installed.");
                    return;
                }

                harmony.Patch(
                    standard,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GenSight_LineOfSight_Standard_Patch), nameof(GenSight_LineOfSight_Standard_Patch.Postfix))));

                harmony.Patch(
                    rect,
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(GenSight_LineOfSight_Rect_Patch), nameof(GenSight_LineOfSight_Rect_Patch.Postfix))));

                Log.Message("[Ra2Bunker Endpoint LOS Fix V3] Active. Exact RimWorld 1.5 GenSight overloads patched; bunker endpoints remain exempt while outside-to-outside rays are blocked by Ra2_Bunker cells.");
            }
            catch (Exception ex)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V3] Failed to install LOS patches: " + ex);
            }
        }
    }

    internal static class BunkerLosUtility
    {
        private const string BunkerDefName = "Ra2_Bunker";

        internal static bool IsBunkerCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
                return false;

            Building edifice = cell.GetEdifice(map);
            return edifice != null && edifice.def != null && edifice.def.defName == BunkerDefName;
        }

        internal static bool ShouldBlockStandard(IntVec3 start, IntVec3 end, Map map, int halfXOffset, int halfZOffset)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
                return false;

            // Endpoint exemption is intentional:
            // a bunker occupant can fire outward, and outside shooters can target the bunker itself.
            if (IsBunkerCell(start, map) || IsBunkerCell(end, map))
                return false;

            bool sideOnEqual = start.x != end.x ? start.x < end.x : start.z < end.z;
            int dx = Math.Abs(end.x - start.x);
            int dz = Math.Abs(end.z - start.z);
            int x = start.x;
            int z = start.z;
            int n = 1 + dx + dz;
            int xInc = end.x > start.x ? 1 : -1;
            int zInc = end.z > start.z ? 1 : -1;
            int adjustedDx = dx * 4 + halfXOffset * 2;
            int adjustedDz = dz * 4 + halfZOffset * 2;
            int error = adjustedDx / 2 - adjustedDz / 2;

            while (n > 1)
            {
                IntVec3 cell = new IntVec3(x, 0, z);
                if (cell != start && cell != end && IsBunkerCell(cell, map))
                    return true;

                if (error > 0 || (error == 0 && sideOnEqual))
                {
                    x += xInc;
                    error -= adjustedDz;
                }
                else
                {
                    z += zInc;
                    error += adjustedDx;
                }

                n--;
            }

            return false;
        }

        internal static bool ShouldBlockRect(IntVec3 start, IntVec3 end, Map map, CellRect startRect, CellRect endRect)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
                return false;

            if (IsBunkerCell(start, map) || IsBunkerCell(end, map))
                return false;

            bool sideOnEqual = start.x != end.x ? start.x < end.x : start.z < end.z;
            int dx = Math.Abs(end.x - start.x);
            int dz = Math.Abs(end.z - start.z);
            int x = start.x;
            int z = start.z;
            int n = 1 + dx + dz;
            int xInc = end.x > start.x ? 1 : -1;
            int zInc = end.z > start.z ? 1 : -1;
            int error = dx - dz;
            int dx2 = dx * 2;
            int dz2 = dz * 2;

            while (n > 1)
            {
                IntVec3 cell = new IntVec3(x, 0, z);

                if (endRect.Contains(cell))
                    return false;

                if (!startRect.Contains(cell) && cell != start && cell != end && IsBunkerCell(cell, map))
                    return true;

                if (error > 0 || (error == 0 && sideOnEqual))
                {
                    x += xInc;
                    error -= dz2;
                }
                else
                {
                    z += zInc;
                    error += dx2;
                }

                n--;
            }

            return false;
        }
    }

    internal static class GenSight_LineOfSight_Standard_Patch
    {
        public static void Postfix(IntVec3 start, IntVec3 end, Map map, int halfXOffset, int halfZOffset, ref bool __result)
        {
            if (__result && BunkerLosUtility.ShouldBlockStandard(start, end, map, halfXOffset, halfZOffset))
                __result = false;
        }
    }

    internal static class GenSight_LineOfSight_Rect_Patch
    {
        public static void Postfix(IntVec3 start, IntVec3 end, Map map, CellRect startRect, CellRect endRect, ref bool __result)
        {
            if (__result && BunkerLosUtility.ShouldBlockRect(start, end, map, startRect, endRect))
                __result = false;
        }
    }
}
