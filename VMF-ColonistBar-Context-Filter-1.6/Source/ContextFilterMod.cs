using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AllenVMFColonistBarContext
{
    /// <summary>
    /// Targets the vanilla ColonistBar map-enumeration site, not the optional
    /// Level.Sensitive VMF ExcludeVehicleMaps helper.  The VMF helper may be
    /// unpatched when VMF dynamic patch settings disable sensitive patches.
    /// All actual world caravan items are left intact.
    /// </summary>
    public sealed class ContextFilterMod : Mod
    {
        private static int callsLogged;
        private static int hiddenLogged;
        private static int errorsLogged;
        private static bool transpilerMatched;

        public ContextFilterMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                MethodInfo target = AccessTools.Method(typeof(ColonistBar), "CheckRecacheEntries");
                if (target == null)
                {
                    Log.Error("[VMFContextFilter] v1.1: vanilla ColonistBar.CheckRecacheEntries not found. No patch installed.");
                    return;
                }

                var harmony = new Harmony("allen.vmf.colonistbar.contextfilter.rw16");
                harmony.Patch(target, transpiler: new HarmonyMethod(
                    AccessTools.Method(typeof(ContextFilterMod), nameof(FilterMapsAtVanillaRecache))));
                Log.Message("[VMFContextFilter] v1.1 DIRECT vanilla ColonistBar map filter installed; " +
                            "VMF pocket maps on a home colony are suppressed, world caravans unchanged." +
                            " IL map-site matched=" + transpilerMatched);
            }
            catch (Exception ex)
            {
                Log.Error("[VMFContextFilter] v1.1 patch initialization failed: " + ex);
            }
        }

        // VMF's original transpiler injects a filter after the same Find.Maps call.
        // Their filter and ours compose whichever Harmony applies first.
        private static IEnumerable<CodeInstruction> FilterMapsAtVanillaRecache(
            IEnumerable<CodeInstruction> source)
        {
            var getMaps = AccessTools.PropertyGetter(typeof(Find), nameof(Find.Maps));
            var filter = AccessTools.Method(typeof(ContextFilterMod), nameof(ExcludeColonyVehicleMaps));
            int matches = 0;
            foreach (var instruction in source)
            {
                yield return instruction;
                if (getMaps != null && instruction.Calls(getMaps))
                {
                    yield return new CodeInstruction(OpCodes.Call, filter);
                    matches++;
                }
            }
            transpilerMatched = matches > 0;
            if (matches != 1)
                Log.Error("[VMFContextFilter] v1.1: expected 1 Find.Maps call, found " + matches +
                          ". Bar filter cannot be guaranteed; no unrelated layout was modified.");
        }

        private static IEnumerable<Map> ExcludeColonyVehicleMaps(IEnumerable<Map> maps)
        {
            if (maps == null) return maps;
            if (callsLogged++ < 3)
                Log.Message("[VMFContextFilter] v1.1 vanilla recache map filter invoked.");
            return IterateFilteredMaps(maps);
        }

        private static IEnumerable<Map> IterateFilteredMaps(IEnumerable<Map> maps)
        {
            foreach (Map map in maps)
            {
                if (map == null)
                {
                    yield return map;
                    continue;
                }

                bool exclude = false;
                try
                {
                    exclude = IsColonyVehiclePocketMap(map);
                }
                catch (Exception ex)
                {
                    if (errorsLogged++ < 3)
                        Log.Warning("[VMFContextFilter] v1.1: map classification error; " +
                                    "keeping map " + map + ": " + ex);
                }
                if (exclude)
                {
                    if (hiddenLogged++ < 20)
                        Log.Message("[VMFContextFilter] v1.1 hidden colony vehicle map " + map +
                                    " (its empty group will not be created)");
                    continue;
                }

                yield return map;
            }
        }

        private static bool IsColonyVehiclePocketMap(Map map)
        {
            Map current = map;
            bool isVehicleMap = false;
            for (int i = 0; i < 12; i++)
            {
                object parent = current.Parent;
                if (parent == null || parent.GetType().FullName !=
                    "VehicleMapFramework.MapParent_Vehicle")
                    return isVehicleMap && current.IsPlayerHome;

                isVehicleMap = true;
                var vehicleField = AccessTools.Field(parent.GetType(), "vehicle");
                Pawn vehicle = vehicleField?.GetValue(parent) as Pawn;
                if (vehicle == null || !vehicle.Spawned || vehicle.Map == null)
                    return false; // World-caravan or stashed vehicle: keep map/group.

                current = vehicle.Map;
            }
            return false;
        }
    }
}
