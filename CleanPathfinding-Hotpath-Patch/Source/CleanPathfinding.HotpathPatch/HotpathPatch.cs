using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Allen.CleanPathfindingHotpathPatch
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "allen.cleanpathfinding.hotpathpatch";
        private const string OldGlowHarmonyId = "allen.cleanpathfinding.glowcache.optimizer";
        private static readonly FieldInfo LoggingField = AccessTools.Field(
            typeof(CleanPathfinding.ModSettings_CleanPathfinding), "logging");
        private static readonly FieldInfo PathFinderMapField = AccessTools.Field(typeof(PathFinder), "map");
        internal static readonly FieldInfo RoofArrayField = AccessTools.Field(typeof(RoofGrid), "roofGrid");
        internal static bool RedirectInstalled;

        static Bootstrap()
        {
            try
            {
                ForceLoggingOff();
                RemoveOldGlowDetour();
                LongEventHandler.ExecuteWhenFinished(RemoveOldGlowDetour);

                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo findPath = AccessTools.Method(typeof(PathFinder), nameof(PathFinder.FindPath),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(LocalTargetInfo), typeof(TraverseParms),
                        typeof(PathEndMode), typeof(PathFinderCostTuning)
                    });
                if (findPath == null)
                    throw new MissingMethodException("PathFinder.FindPath target was not found");

                HarmonyMethod prefix = new HarmonyMethod(typeof(Bootstrap), nameof(FindPathPrefix))
                    { priority = Priority.First };
                HarmonyMethod finalizer = new HarmonyMethod(typeof(Bootstrap), nameof(FindPathFinalizer))
                    { priority = Priority.Last };
                HarmonyMethod transpiler = new HarmonyMethod(typeof(Bootstrap), nameof(FindPathTranspiler))
                    { priority = Priority.Last };
                transpiler.after = new[] { "Owlchemist.CleanPathfinding", "VFEInsectoidsMod" };
                harmony.Patch(findPath, prefix: prefix, transpiler: transpiler, finalizer: finalizer);

                MethodInfo writeSettings = AccessTools.Method(typeof(CleanPathfinding.Mod_CleanPathfinding), "WriteSettings");
                if (writeSettings != null)
                    harmony.Patch(writeSettings,
                        prefix: new HarmonyMethod(typeof(Bootstrap), nameof(ForceLoggingOff))
                            { priority = Priority.First });

                Log.Message("[Clean Pathfinding Hotpath Patch] V1 active: redirect=" + RedirectInstalled +
                    ", direct per-path context, logging disabled, old Glow Cache detour removed.");
            }
            catch (Exception ex)
            {
                Log.Error("[Clean Pathfinding Hotpath Patch] initialization failed closed: " + ex);
            }
        }

        public static void ForceLoggingOff()
        {
            try
            {
                if (LoggingField != null)
                    LoggingField.SetValue(null, false);
            }
            catch { }
        }

        private static void RemoveOldGlowDetour()
        {
            try { new Harmony(OldGlowHarmonyId).UnpatchAll(OldGlowHarmonyId); }
            catch { }
        }

        public static void FindPathPrefix(PathFinder __instance, TraverseParms traverseParms, ref PathContext __state)
        {
            __state = FastCosts.Current;
            Pawn pawn = traverseParms.pawn;
            Map map = null;
            try
            {
                if (__instance != null && PathFinderMapField != null)
                    map = PathFinderMapField.GetValue(__instance) as Map;
            }
            catch { }
            if (map == null && pawn != null)
                map = pawn.Map;
            FastCosts.Current = PathContext.Create(map, pawn);
        }

        public static Exception FindPathFinalizer(Exception __exception, PathContext __state)
        {
            FastCosts.Current = __state;
            return __exception;
        }

        public static IEnumerable<CodeInstruction> FindPathTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> original = new List<CodeInstruction>(instructions);
            MethodInfo oldAdjust = AccessTools.Method(typeof(CleanPathfinding.CleanPathfindingUtility),
                nameof(CleanPathfinding.CleanPathfindingUtility.AdjustCosts));
            MethodInfo fastAdjust = AccessTools.Method(typeof(FastCosts), nameof(FastCosts.AdjustCosts));
            int matches = 0;

            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction code = original[i];
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
                    Equals(code.operand, oldAdjust))
                    matches++;
            }

            if (matches != 1 || oldAdjust == null || fastAdjust == null)
            {
                Log.Warning("[Clean Pathfinding Hotpath Patch] expected one AdjustCosts call but found " +
                    matches + "; original PathFinder IL retained.");
                return original;
            }

            for (int i = 0; i < original.Count; i++)
            {
                CodeInstruction code = original[i];
                if ((code.opcode == OpCodes.Call || code.opcode == OpCodes.Callvirt) &&
                    Equals(code.operand, oldAdjust))
                {
                    code.opcode = OpCodes.Call;
                    code.operand = fastAdjust;
                }
            }
            RedirectInstalled = true;
            return original;
        }
    }

    internal sealed class PathContext
    {
        internal Map Map;
        internal Pawn Pawn;
        internal bool RevertTerrain;
        internal int[] DoorCosts;
        internal RoofDef[] Roofs;
        internal float SkyGlow;
        internal bool FactorLight;
        internal int DarknessPenalty;
        internal bool PawnIsPlayer;
        internal bool TerrainCacheValid;
        internal ushort LastTerrainHash;
        internal int LastTerrainAdjustment;

        internal static PathContext Create(Map map, Pawn pawn)
        {
            if (map == null)
                return null;

            PathContext context = new PathContext
            {
                Map = map,
                Pawn = pawn,
                SkyGlow = map.skyManager == null ? 0f : map.skyManager.CurSkyGlow,
                FactorLight = CleanPathfinding.ModSettings_CleanPathfinding.factorLight,
                DarknessPenalty = CleanPathfinding.ModSettings_CleanPathfinding.darknessPenalty
            };

            try
            {
                if (map.roofGrid != null && Bootstrap.RoofArrayField != null)
                    context.Roofs = Bootstrap.RoofArrayField.GetValue(map.roofGrid) as RoofDef[];
            }
            catch { }

            if (CleanPathfinding.ModSettings_CleanPathfinding.doorPathing)
            {
                CleanPathfinding.MapComponent_DoorPathing comp;
                if (CleanPathfinding.DoorPathingUtility.compCache.TryGetValue(map.uniqueID, out comp) && comp != null)
                    context.DoorCosts = comp.doorCostGrid;
            }

            context.PawnIsPlayer = pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer;
            context.RevertTerrain = ShouldRevert(pawn);
            return context;
        }

        private static bool ShouldRevert(Pawn pawn)
        {
            if (pawn == null)
                return false;
            Faction faction = pawn.Faction;
            if (faction == null || pawn.def == null || pawn.def.race == null ||
                pawn.def.race.intelligence == Intelligence.Animal)
                return true;
            if (!faction.IsPlayer && faction.HostileTo(Faction.OfPlayer))
                return true;
            if (CleanPathfinding.ModSettings_CleanPathfinding.factorCarryingPawn &&
                pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null &&
                pawn.carryTracker.CarriedThing.def.category == ThingCategory.Pawn)
                return true;
            return CleanPathfinding.ModSettings_CleanPathfinding.factorBleeding &&
                pawn.health != null && pawn.health.hediffSet != null &&
                pawn.health.hediffSet.BleedRateTotal > 0.1f;
        }
    }

    internal static class FastCosts
    {
        [ThreadStatic] internal static PathContext Current;

        public static float AdjustCosts(Pawn pawn, TerrainDef def, float cost, Map map, int index)
        {
            PathContext context = Current;
            if (context == null || context.Map != map || context.Pawn != pawn)
                context = PathContext.Create(map, pawn);

            if (pawn == null || context == null)
                return cost < 0f ? 0f : cost;

            if (!context.RevertTerrain)
            {
                int[] doorCosts = context.DoorCosts;
                if (doorCosts != null && (uint)index < (uint)doorCosts.Length)
                {
                    int doorCost = doorCosts[index];
                    if (doorCost < 0)
                        return cost < 0f ? 0f : cost;
                    cost += doorCost;
                }

                if (context.FactorLight && context.DarknessPenalty != 0 &&
                    GlowAt(context, map, index) < 0.3f)
                    cost += context.DarknessPenalty;
            }
            else if (def != null)
            {
                int terrainAdjustment = 0;
                if (context.TerrainCacheValid && context.LastTerrainHash == def.shortHash)
                    terrainAdjustment = context.LastTerrainAdjustment;
                else
                {
                    CleanPathfinding.CleanPathfindingUtility.terrainCache.TryGetValue(
                        def.shortHash, out terrainAdjustment);
                    if (terrainAdjustment > 0 && context.PawnIsPlayer)
                        terrainAdjustment = 0;
                    context.LastTerrainHash = def.shortHash;
                    context.LastTerrainAdjustment = terrainAdjustment;
                    context.TerrainCacheValid = true;
                }
                cost += terrainAdjustment;
            }

            return cost < 0f ? 0f : cost;
        }

        private static float GlowAt(PathContext context, Map map, int index)
        {
            RoofDef[] roofs = context.Roofs;
            bool unroofed = roofs != null && (uint)index < (uint)roofs.Length
                ? roofs[index] == null
                : map.roofGrid != null && !map.roofGrid.Roofed(index);
            if (unroofed && context.SkyGlow >= 1f)
                return 1f;

            Color32 color = map.glowGrid.VisualGlowAt(index);
            if (color.a == byte.MaxValue)
                return 1f;
            return (color.r + color.g + color.b) * 0.0047058823529412f;
        }
    }
}
