using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Allen.AnimalPenDistrictRecovery15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.animalpendistrictrecovery15";
        private const int FullRepairCooldownTicks = 600;
        private static readonly Dictionary<int, int> LastFullRepairTickByMap = new Dictionary<int, int>();
        private static readonly MethodInfo NotifyWalkabilityChanged = AccessTools.Method(
            typeof(RegionDirtyer), "Notify_WalkabilityChanged", new[] { typeof(IntVec3), typeof(bool) });

        static Bootstrap()
        {
            try
            {
                MethodInfo target = AccessTools.Method(
                    typeof(AnimalPenConnectedDistrictsCalculator),
                    nameof(AnimalPenConnectedDistrictsCalculator.CalculateConnectedDistricts));
                new Harmony(HarmonyId).Patch(target,
                    prefix: new HarmonyMethod(typeof(Bootstrap), nameof(CalculatePrefix)));
                Log.Message("[Animal Pen District Recovery 1.5] Installed null-District recovery boundary.");
            }
            catch (Exception ex)
            {
                Log.Error("[Animal Pen District Recovery 1.5] Bootstrap failed: " + ex);
            }
        }

        public static bool CalculatePrefix(
            AnimalPenConnectedDistrictsCalculator __instance,
            IntVec3 position,
            Map map,
            ref List<District> __result)
        {
            if (map == null || !position.InBounds(map))
            {
                __result = new List<District>();
                return false;
            }

            District district = position.GetDistrict(map);
            if (district != null)
            {
                return true;
            }

            Region before = map.regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(position);
            string beforeState = DescribeRegion(before);

            // First invalidate only the affected cell and its adjacent regions.
            // This normally repairs a stale valid Region whose District was lost.
            if (map.regionAndRoomUpdater.Enabled)
            {
                try
                {
                    if (NotifyWalkabilityChanged != null)
                    {
                        NotifyWalkabilityChanged.Invoke(map.regionDirtyer, new object[] { position, true });
                        map.regionAndRoomUpdater.TryRebuildDirtyRegionsAndRooms();
                        __instance.Reset();
                        district = position.GetDistrict(map);
                        if (district != null)
                        {
                            Log.Warning("[Animal Pen District Recovery 1.5] Repaired null District with a targeted rebuild at " +
                                position + " on map " + map.uniqueID + ". Before=" + beforeState);
                            return true;
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[Animal Pen District Recovery 1.5] Targeted recovery failed at " + position +
                        " on map " + map.uniqueID + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            int lastTick;
            bool cooldownExpired = !LastFullRepairTickByMap.TryGetValue(map.uniqueID, out lastTick) ||
                tick < lastTick || tick - lastTick >= FullRepairCooldownTicks;

            if (map.regionAndRoomUpdater.Enabled && cooldownExpired)
            {
                LastFullRepairTickByMap[map.uniqueID] = tick;
                try
                {
                    map.regionAndRoomUpdater.RebuildAllRegionsAndRooms();
                    __instance.Reset();
                    district = position.GetDistrict(map);
                    if (district != null)
                    {
                        Log.Warning("[Animal Pen District Recovery 1.5] Repaired null District with one full region rebuild at " +
                            position + " on map " + map.uniqueID + ". Before=" + beforeState);
                        return true;
                    }
                }
                catch (Exception ex)
                {
                    Log.Error("[Animal Pen District Recovery 1.5] Full recovery failed at " + position +
                        " on map " + map.uniqueID + ": " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            // The vanilla method uses the null District as a Dictionary key and throws.
            // Returning no connected districts is the conservative result until the map
            // topology becomes valid; the cooldown prevents rebuild and log storms.
            __result = new List<District>();
            if (cooldownExpired)
            {
                Log.Error("[Animal Pen District Recovery 1.5] District is still null after recovery at " +
                    position + " on map " + map.uniqueID + ". Region=" + beforeState +
                    ", updaterEnabled=" + map.regionAndRoomUpdater.Enabled +
                    ". Returning an empty pen district set for this call.");
            }
            return false;
        }

        private static string DescribeRegion(Region region)
        {
            if (region == null)
            {
                return "null";
            }
            return "id=" + region.id + ", valid=" + region.valid +
                ", type=" + region.type + ", district=" +
                (region.District == null ? "null" : region.District.ID.ToString());
        }
    }
}
