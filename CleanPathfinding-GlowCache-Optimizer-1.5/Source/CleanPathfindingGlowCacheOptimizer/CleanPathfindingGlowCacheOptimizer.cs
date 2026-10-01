using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace Allen.CleanPathfindingGlowCacheOptimizer
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.cleanpathfinding.glowcache.optimizer";
        private const int CacheTicks = 30;

        private static readonly Dictionary<int, MapCache> Maps = new Dictionary<int, MapCache>();
        private static readonly int MainThreadId = Thread.CurrentThread.ManagedThreadId;

        private static MapCache lastMapCache;
        private static int lastMapId = int.MinValue;
        private static int lastObservedTick = -1;

        private static long lookups;
        private static long hits;
        private static long stores;
        private static long resets;
        private static long failures;
        private static bool installed;

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo target = FindGlowTarget();
                if (target == null)
                {
                    Log.Warning("[Clean Pathfinding Glow Cache Optimizer] Compatible Clean Pathfinding GameGlowAtFast target was not found; patch remains inert.");
                    return;
                }

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(Bootstrap), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Bootstrap), nameof(Postfix)) { priority = Priority.Last });

                installed = true;
                Log.Message("[Clean Pathfinding Glow Cache Optimizer] 30-tick per-map glow-cost cache installed.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[Clean Pathfinding Glow Cache Optimizer] Installation failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static MethodInfo FindGlowTarget()
        {
            Type utility = AccessTools.TypeByName("CleanPathfinding.CleanPathfindingUtility");
            if (utility == null)
                return null;

            MethodInfo exact = AccessTools.Method(utility, "<AdjustCosts>g__GameGlowAtFast|14_1");
            if (IsCompatible(exact))
                return exact;

            MethodInfo candidate = null;
            foreach (MethodInfo method in AccessTools.GetDeclaredMethods(utility))
            {
                if (method.Name.IndexOf("GameGlowAtFast", StringComparison.Ordinal) < 0 || !IsCompatible(method))
                    continue;

                if (candidate != null)
                    return null;

                candidate = method;
            }

            return candidate;
        }

        private static bool IsCompatible(MethodInfo method)
        {
            if (method == null || !method.IsStatic || method.ReturnType != typeof(float))
                return false;

            ParameterInfo[] parameters = method.GetParameters();
            return parameters.Length == 2 &&
                   parameters[0].ParameterType == typeof(Map) &&
                   parameters[1].ParameterType == typeof(int);
        }

        public static bool Prefix(Map __0, int __1, ref float __result, ref bool __state)
        {
            __state = true;

            if (Thread.CurrentThread.ManagedThreadId != MainThreadId)
                return true;

            Interlocked.Increment(ref lookups);

            try
            {
                if (__0 == null || __1 < 0 || __1 >= __0.cellIndices.NumGridCells)
                    return true;

                TickManager tickManager = Find.TickManager;
                int tick = tickManager != null ? tickManager.TicksGame : 0;

                if (lastObservedTick >= 0 && tick < lastObservedTick)
                    ResetAll();
                lastObservedTick = tick;

                int generation = tick / CacheTicks + 1;
                MapCache cache = GetMapCache(__0);
                if (cache.Generations[__1] != generation)
                    return true;

                __result = cache.Values[__1];
                __state = false;
                Interlocked.Increment(ref hits);
                return false;
            }
            catch
            {
                Interlocked.Increment(ref failures);
                return true;
            }
        }

        public static void Postfix(Map __0, int __1, float __result, bool __state)
        {
            if (!__state || Thread.CurrentThread.ManagedThreadId != MainThreadId || __0 == null || __1 < 0)
                return;

            try
            {
                int cellCount = __0.cellIndices.NumGridCells;
                if (__1 >= cellCount)
                    return;

                TickManager tickManager = Find.TickManager;
                int tick = tickManager != null ? tickManager.TicksGame : 0;
                MapCache cache = GetMapCache(__0);

                cache.Values[__1] = __result;
                cache.Generations[__1] = tick / CacheTicks + 1;
                Interlocked.Increment(ref stores);
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        private static MapCache GetMapCache(Map map)
        {
            int mapId = map.uniqueID;
            int cellCount = map.cellIndices.NumGridCells;

            if (mapId == lastMapId && lastMapCache != null && lastMapCache.CellCount == cellCount)
                return lastMapCache;

            MapCache cache;
            if (!Maps.TryGetValue(mapId, out cache) || cache.CellCount != cellCount)
            {
                cache = new MapCache(cellCount);
                Maps[mapId] = cache;
            }

            lastMapId = mapId;
            lastMapCache = cache;
            return cache;
        }

        private static void ResetAll()
        {
            Maps.Clear();
            lastMapCache = null;
            lastMapId = int.MinValue;
            Interlocked.Increment(ref resets);
        }

        private sealed class MapCache
        {
            internal readonly int CellCount;
            internal readonly int[] Generations;
            internal readonly float[] Values;

            internal MapCache(int cellCount)
            {
                CellCount = cellCount;
                Generations = new int[cellCount];
                Values = new float[cellCount];
            }
        }
    }
}
