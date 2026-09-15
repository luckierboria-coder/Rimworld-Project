using System;
using System.Collections.Generic;
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
                Harmony harmony = new Harmony("allen.ra2bunker.walllos.v4");

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

                MethodInfo cellCanSeeCell = AccessTools.Method(typeof(ShootLeanUtility), nameof(ShootLeanUtility.CellCanSeeCell), new Type[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map)
                });

                if (standard == null || rect == null || cellCanSeeCell == null)
                {
                    Log.Error("[Ra2Bunker Endpoint LOS Fix V4] Required RimWorld 1.5 LOS method lookup failed; patches were not installed.");
                    return;
                }

                harmony.Patch(
                    standard,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GenSight_LineOfSight_Standard_Patch), nameof(GenSight_LineOfSight_Standard_Patch.Prefix))));

                harmony.Patch(
                    rect,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(GenSight_LineOfSight_Rect_Patch), nameof(GenSight_LineOfSight_Rect_Patch.Prefix))));

                harmony.Patch(
                    cellCanSeeCell,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(ShootLeanUtility_CellCanSeeCell_Patch), nameof(ShootLeanUtility_CellCanSeeCell_Patch.Prefix))));

                Log.Message("[Ra2Bunker Endpoint LOS Fix V4] Active. Ra2_Bunker remains a true full LOS blocker; only rays whose source or destination is that bunker receive a 3x3 endpoint exemption.");
            }
            catch (Exception ex)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V4] Failed to install LOS patches: " + ex);
            }
        }
    }

    internal static class BunkerLosUtility
    {
        private const string BunkerDefName = "Ra2_Bunker";

        internal static Building FindBunkerAt(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
                return null;

            Building edifice = cell.GetEdifice(map);
            if (IsBunker(edifice))
                return edifice;

            // Defensive fallback. ThingGrid is authoritative for occupied cells even if
            // another mod changes edifice registration semantics.
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Building building = things[i] as Building;
                if (IsBunker(building))
                    return building;
            }

            return null;
        }

        private static bool IsBunker(Building building)
        {
            return building != null && building.def != null && building.def.defName == BunkerDefName;
        }

        private static bool IsEndpointBunkerCell(IntVec3 cell, Map map, Building startBunker, Building endBunker)
        {
            Building atCell = FindBunkerAt(cell, map);
            return atCell != null && (ReferenceEquals(atCell, startBunker) || ReferenceEquals(atCell, endBunker));
        }

        internal static bool HasBunkerEndpoint(IntVec3 start, IntVec3 end, Map map, out Building startBunker, out Building endBunker)
        {
            startBunker = FindBunkerAt(start, map);
            endBunker = FindBunkerAt(end, map);
            return startBunker != null || endBunker != null;
        }

        internal static bool EndpointAwareStandard(
            IntVec3 start,
            IntVec3 end,
            Map map,
            bool skipFirstCell,
            Func<IntVec3, bool> validator,
            int halfXOffset,
            int halfZOffset,
            Building startBunker,
            Building endBunker)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
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
                if (!skipFirstCell || cell != start)
                {
                    bool endpointBunkerCell = IsEndpointBunkerCell(cell, map, startBunker, endBunker);
                    if (!endpointBunkerCell && !cell.CanBeSeenOverFast(map))
                        return false;

                    if (validator != null && !validator(cell))
                        return false;
                }

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

            return true;
        }

        internal static bool EndpointAwareRect(
            IntVec3 start,
            IntVec3 end,
            Map map,
            CellRect startRect,
            CellRect endRect,
            Func<IntVec3, bool> validator,
            bool forLeaning,
            Building startBunker,
            Building endBunker)
        {
            if (map == null || !start.InBounds(map) || !end.InBounds(map))
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
                    return true;

                if (!startRect.Contains(cell))
                {
                    bool endpointBunkerCell = IsEndpointBunkerCell(cell, map, startBunker, endBunker);
                    if (!endpointBunkerCell && !cell.CanBeSeenOverFast(map))
                        return false;

                    if (validator != null && !validator(cell))
                        return false;
                }

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

            return true;
        }
    }

    internal static class GenSight_LineOfSight_Standard_Patch
    {
        public static bool Prefix(
            IntVec3 start,
            IntVec3 end,
            Map map,
            bool skipFirstCell,
            Func<IntVec3, bool> validator,
            int halfXOffset,
            int halfZOffset,
            ref bool __result)
        {
            Building startBunker;
            Building endBunker;
            if (!BunkerLosUtility.HasBunkerEndpoint(start, end, map, out startBunker, out endBunker))
                return true;

            __result = BunkerLosUtility.EndpointAwareStandard(
                start, end, map, skipFirstCell, validator, halfXOffset, halfZOffset, startBunker, endBunker);
            return false;
        }
    }

    internal static class GenSight_LineOfSight_Rect_Patch
    {
        public static bool Prefix(
            IntVec3 start,
            IntVec3 end,
            Map map,
            CellRect startRect,
            CellRect endRect,
            Func<IntVec3, bool> validator,
            bool forLeaning,
            ref bool __result)
        {
            Building startBunker;
            Building endBunker;
            if (!BunkerLosUtility.HasBunkerEndpoint(start, end, map, out startBunker, out endBunker))
                return true;

            __result = BunkerLosUtility.EndpointAwareRect(
                start, end, map, startRect, endRect, validator, forLeaning, startBunker, endBunker);
            return false;
        }
    }

    internal static class ShootLeanUtility_CellCanSeeCell_Patch
    {
        public static bool Prefix(IntVec3 source, IntVec3 dest, Map map, ref bool __result)
        {
            Building sourceBunker;
            Building destBunker;
            if (!BunkerLosUtility.HasBunkerEndpoint(source, dest, map, out sourceBunker, out destBunker))
                return true;

            // Vanilla CellCanSeeCell immediately rejects a full-fill source/destination.
            // For a bunker endpoint, use a direct endpoint-aware LOS instead. This keeps
            // the bunker opaque to everyone else and prevents lean logic from peeking
            // through/around its own 3x3 footprint.
            __result = BunkerLosUtility.EndpointAwareStandard(
                source, dest, map, true, null, 0, 0, sourceBunker, destBunker);
            return false;
        }
    }
}
