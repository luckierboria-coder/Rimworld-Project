using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AllenVMFColonistBarContext
{
    /// <summary>
    /// A narrow post-filter on VMF's existing CheckRecacheEntries map selection.
    /// Map/group layout remains calculated by the vanilla ColonistBar and CBA.
    /// World-caravan vehicle maps are not filtered because their vehicles are unspawned.
    /// </summary>
    public sealed class ContextFilterMod : Mod
    {
        public ContextFilterMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type patchClass = AccessTools.TypeByName(
                    "VehicleMapFramework.VMF_HarmonyPatches.Patch_ColonistBar_CheckRecacheEntries");
                MethodInfo helper = patchClass == null ? null :
                    AccessTools.Method(patchClass, "ExcludeVehicleMaps");
                if (helper == null)
                {
                    Log.Warning("[VMFContextFilter] VMF's ExcludeVehicleMaps was not found; " +
                                "no patch installed. Check that Vehicle Map Framework 1.6.673 is enabled.");
                    return;
                }

                MethodInfo postfix = AccessTools.Method(
                    typeof(ContextFilterMod), nameof(AfterExcludeVehicleMaps));
                new Harmony("allen.vmf.colonistbar.contextfilter.rw16").Patch(
                    helper, postfix: new HarmonyMethod(postfix));
                Log.Message("[VMFContextFilter] v1.0 active. Colony-spawned vehicle pocket maps " +
                            "will be excluded at Colonist Bar recache; caravan maps remain unchanged.");
            }
            catch (Exception ex)
            {
                Log.Warning("[VMFContextFilter] Initialization failed: " + ex);
            }
        }

        private static int exclusionLogs;

        // Execute ONLY when the top bar is rebuilt; never inside pawn ticks or map pathfinding.
        private static void AfterExcludeVehicleMaps(ref IEnumerable<Map> __result)
        {
            if (__result != null)
                __result = FilterStationaryColonyVehicleMaps(__result);
        }

        private static IEnumerable<Map> FilterStationaryColonyVehicleMaps(IEnumerable<Map> source)
        {
            foreach (Map map in source)
            {
                if (map == null)
                {
                    yield return map;
                    continue;
                }

                bool hidden = false;
                try
                {
                    hidden = IsGroundColonyVehiclePocketMap(map);
                }
                catch (Exception ex)
                {
                    if (exclusionLogs < 3)
                        Log.Warning("[VMFContextFilter] Classification failed; preserving map " +
                                    map + ": " + ex);
                    exclusionLogs++;
                }

                if (hidden)
                {
                    if (exclusionLogs++ < 15)
                        Log.Message("[VMFContextFilter] Hidden colony vehicle pocket-map " + map);
                    continue;
                }
                yield return map;
            }
        }

        private static bool IsGroundColonyVehiclePocketMap(Map map)
        {
            // Follow nested vehicle pocket-map ownership back to the actual ground map.
            // No item is removed if there is any uncertainty or an unspawned vehicle:
            // that preserves all world-caravan representations.
            Map parentMap = map;
            bool foundVehiclePocketMap = false;
            for (int depth = 0; depth < 12; depth++)
            {
                object parent = parentMap.Parent;
                if (parent == null ||
                    parent.GetType().FullName != "VehicleMapFramework.MapParent_Vehicle")
                {
                    return foundVehiclePocketMap && parentMap.IsPlayerHome;
                }

                foundVehiclePocketMap = true;
                FieldInfo field = AccessTools.Field(parent.GetType(), "vehicle");
                Pawn vehicle = field == null ? null : field.GetValue(parent) as Pawn;
                if (vehicle == null || !vehicle.Spawned || vehicle.Map == null)
                    return false; // Vehicle is in a caravan, stashed, or not yet spawned.

                parentMap = vehicle.Map;
            }
            return false;
        }
    }
}
