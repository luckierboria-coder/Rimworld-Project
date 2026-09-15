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
        private const BindingFlags AllMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        static Bootstrap()
        {
            Harmony harmony = new Harmony("allen.ra2bunker.walllos.v5.2");
            int installed = 0;
            int missing = 0;

            // Authoritative opacity rule: every Ra2_Bunker is opaque by default.
            // Both IntVec3.CanBeSeenOver and CanBeSeenOverFast eventually delegate here.
            MethodInfo buildingCanBeSeenOver = FindMethod(typeof(GenGrid), nameof(GenGrid.CanBeSeenOver), p =>
                p.Length == 1 && p[0].ParameterType == typeof(Building));

            if (TryPatch(harmony, buildingCanBeSeenOver,
                    prefix: AccessTools.Method(typeof(BuildingCanBeSeenOverPatch), nameof(BuildingCanBeSeenOverPatch.Prefix)),
                    label: "GenGrid.CanBeSeenOver(Building)"))
                installed++;
            else
                missing++;

            // Patch every RimWorld 1.5 GenSight.LineOfSight overload whose first
            // arguments are (IntVec3 start, IntVec3 end, Map map). The current
            // LOS endpoints define exactly which bunker(s), if any, may be ignored.
            List<MethodInfo> losMethods = FindMethods(typeof(GenSight), nameof(GenSight.LineOfSight), p =>
                p.Length >= 3 &&
                p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(IntVec3) &&
                p[2].ParameterType == typeof(Map));

            MethodInfo endpointPrefix = AccessTools.Method(typeof(CellEndpointScopePatch), nameof(CellEndpointScopePatch.Prefix));
            MethodInfo endpointFinalizer = AccessTools.Method(typeof(CellEndpointScopePatch), nameof(CellEndpointScopePatch.Finalizer));

            if (losMethods.Count == 0)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5.2] CRITICAL: no GenSight.LineOfSight overloads were found.");
                missing++;
            }
            else
            {
                for (int i = 0; i < losMethods.Count; i++)
                {
                    if (TryPatch(harmony, losMethods[i], endpointPrefix, finalizer: endpointFinalizer,
                            label: "GenSight.LineOfSight overload " + i))
                        installed++;
                    else
                        missing++;
                }
            }

            // CellCanSeeCell performs direct source.CanBeSeenOver/dest.CanBeSeenOver
            // checks BEFORE it calls GenSight, so it needs the same endpoint scope.
            MethodInfo cellCanSeeCell = FindMethod(typeof(ShootLeanUtility), nameof(ShootLeanUtility.CellCanSeeCell), p =>
                p.Length == 3 &&
                p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(IntVec3) &&
                p[2].ParameterType == typeof(Map));

            if (TryPatch(harmony, cellCanSeeCell, endpointPrefix, finalizer: endpointFinalizer,
                    label: "ShootLeanUtility.CellCanSeeCell"))
                installed++;
            else
                missing++;

            // LeanShootingSourcesFromTo also calls CanBeSeenOver directly for the
            // shooter/target neighborhood. Endpoint scoping here is required for a
            // 3x3 bunker to use its own footprint without making any other bunker clear.
            MethodInfo leanSources = FindMethod(typeof(ShootLeanUtility), nameof(ShootLeanUtility.LeanShootingSourcesFromTo), p =>
                p.Length >= 3 &&
                p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(IntVec3) &&
                p[2].ParameterType == typeof(Map));

            if (TryPatch(harmony, leanSources, endpointPrefix, finalizer: endpointFinalizer,
                    label: "ShootLeanUtility.LeanShootingSourcesFromTo"))
                installed++;
            else
                missing++;

            if (buildingCanBeSeenOver == null || losMethods.Count == 0 || cellCanSeeCell == null)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5.2] CRITICAL core hook missing; patch is not considered active.");
                return;
            }

            Log.Message("[Ra2Bunker Endpoint LOS Fix V5.2] Active. Installed hooks=" + installed +
                        ", missing optional hooks=" + missing +
                        ". Ra2_Bunker is opaque by default. Only a bunker physically occupying the current LOS source or destination cell is exempted for that call.");
        }

        private static MethodInfo FindMethod(Type type, string name, Func<ParameterInfo[], bool> predicate)
        {
            List<MethodInfo> methods = FindMethods(type, name, predicate);
            return methods.Count > 0 ? methods[0] : null;
        }

        private static List<MethodInfo> FindMethods(Type type, string name, Func<ParameterInfo[], bool> predicate)
        {
            List<MethodInfo> result = new List<MethodInfo>();
            MethodInfo[] methods = type.GetMethods(AllMethods);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != name)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (predicate(parameters))
                    result.Add(method);
            }

            return result;
        }

        private static bool TryPatch(Harmony harmony, MethodInfo original, MethodInfo prefix = null,
            MethodInfo postfix = null, MethodInfo finalizer = null, string label = null)
        {
            if (original == null)
            {
                Log.Warning("[Ra2Bunker Endpoint LOS Fix V5.2] Hook not found: " + (label ?? "unknown"));
                return false;
            }

            try
            {
                harmony.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(finalizer) : null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5.2] Failed to patch " + (label ?? original.Name) + ": " + ex);
                return false;
            }
        }
    }

    internal static class BunkerLosContext
    {
        private const string BunkerDefName = "Ra2_Bunker";

        [ThreadStatic]
        private static List<Building> exemptBunkers;

        internal struct ScopeState
        {
            public int Marker;
            public bool Active;
        }

        internal static bool IsBunker(Building building)
        {
            return building != null && building.def != null && building.def.defName == BunkerDefName;
        }

        internal static Building FindBunkerAt(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
                return null;

            // ThingGrid is authoritative for multi-cell occupied footprints. This is
            // intentionally not limited to GetEdifice(), because Ra2_Bunker keeps the
            // original partial fill category.
            List<Thing> things = map.thingGrid.ThingsListAtFast(cell);
            for (int i = 0; i < things.Count; i++)
            {
                Building building = things[i] as Building;
                if (IsBunker(building))
                    return building;
            }

            Building edifice = cell.GetEdifice(map);
            return IsBunker(edifice) ? edifice : null;
        }

        internal static bool IsExempt(Building bunker)
        {
            if (bunker == null || exemptBunkers == null)
                return false;

            for (int i = 0; i < exemptBunkers.Count; i++)
            {
                if (ReferenceEquals(exemptBunkers[i], bunker))
                    return true;
            }

            return false;
        }

        internal static ScopeState Push(Map map, IntVec3 firstCell, IntVec3 secondCell)
        {
            Building first = FindBunkerAt(firstCell, map);
            Building second = FindBunkerAt(secondCell, map);

            if (first == null && second == null)
                return default(ScopeState);

            if (exemptBunkers == null)
                exemptBunkers = new List<Building>(4);

            ScopeState state = new ScopeState
            {
                Marker = exemptBunkers.Count,
                Active = true
            };

            AddUnique(first);
            AddUnique(second);
            return state;
        }

        private static void AddUnique(Building bunker)
        {
            if (bunker == null)
                return;

            for (int i = 0; i < exemptBunkers.Count; i++)
            {
                if (ReferenceEquals(exemptBunkers[i], bunker))
                    return;
            }

            exemptBunkers.Add(bunker);
        }

        internal static void Pop(ScopeState state)
        {
            if (!state.Active || exemptBunkers == null)
                return;

            if (state.Marker < 0 || state.Marker > exemptBunkers.Count)
            {
                exemptBunkers.Clear();
                return;
            }

            int remove = exemptBunkers.Count - state.Marker;
            if (remove > 0)
                exemptBunkers.RemoveRange(state.Marker, remove);
        }
    }

    internal static class BuildingCanBeSeenOverPatch
    {
        public static bool Prefix(Building b, ref bool __result)
        {
            if (!BunkerLosContext.IsBunker(b))
                return true;

            __result = BunkerLosContext.IsExempt(b);
            return false;
        }
    }

    // Shared by GenSight.LineOfSight, CellCanSeeCell and LeanShootingSourcesFromTo.
    // __0/__1/__2 are deliberately positional so one patch method can cover every
    // compatible RimWorld 1.5 overload without depending on parameter names.
    internal static class CellEndpointScopePatch
    {
        public static void Prefix(IntVec3 __0, IntVec3 __1, Map __2, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(__2, __0, __1);
        }

        public static Exception Finalizer(Exception __exception, BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
            return __exception;
        }
    }
}
