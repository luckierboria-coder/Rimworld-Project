using System;
using HarmonyLib;
using Verse;

namespace BunkerEndpointLOSFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("allen.ra2bunker.endpointlosfix.1.5").PatchAll();
        }
    }

    internal static class BunkerLosUtility
    {
        private const string BunkerDefName = "Ra2_Bunker";

        internal static bool IsBunkerCell(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
                return false;

            var edifice = cell.GetEdifice(map);
            return edifice != null && edifice.def != null && edifice.def.defName == BunkerDefName;
        }

        internal static bool ShouldBlockStandard(IntVec3 start, IntVec3 end, Map map, int halfXOffset, int halfZOffset)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
                return false;

            // Any LOS whose endpoint is inside the bunker must remain valid:
            // bunker -> outside and outside -> bunker are both allowed.
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
            int dx4 = dx * 4;
            int dz4 = dz * 4;
            int adjustedDx = dx4 + halfXOffset * 2;
            int adjustedDz = dz4 + halfZOffset * 2;
            int error = adjustedDx / 2 - adjustedDz / 2;

            while (n > 1)
            {
                var c = new IntVec3(x, 0, z);
                if (c != start && c != end && IsBunkerCell(c, map))
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
                var c = new IntVec3(x, 0, z);

                if (endRect.Contains(c))
                    return false;

                if (!startRect.Contains(c) && c != start && c != end && IsBunkerCell(c, map))
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

    [HarmonyPatch(typeof(GenSight), nameof(GenSight.LineOfSight), new Type[]
    {
        typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool),
        typeof(Func<IntVec3, bool>), typeof(int), typeof(int)
    })]
    internal static class GenSight_LineOfSight_Standard_Patch
    {
        private static void Postfix(IntVec3 start, IntVec3 end, Map map, int halfXOffset, int halfZOffset, ref bool __result)
        {
            if (__result && BunkerLosUtility.ShouldBlockStandard(start, end, map, halfXOffset, halfZOffset))
                __result = false;
        }
    }

    [HarmonyPatch(typeof(GenSight), nameof(GenSight.LineOfSight), new Type[]
    {
        typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(CellRect), typeof(CellRect),
        typeof(Func<IntVec3, bool>)
    })]
    internal static class GenSight_LineOfSight_Rect_Patch
    {
        private static void Postfix(IntVec3 start, IntVec3 end, Map map, CellRect startRect, CellRect endRect, ref bool __result)
        {
            if (__result && BunkerLosUtility.ShouldBlockRect(start, end, map, startRect, endRect))
                __result = false;
        }
    }
}
