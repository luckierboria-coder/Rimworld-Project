using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// The only retained Stage4D Common Sense optimization. Misses run Common Sense unchanged;
    /// hits replay only previously observed extras and revalidate live state, baseValidator and
    /// CanReach. V0.9.2 keeps only cheap aggregate counters for on-demand reporting.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class CommonSenseIngredientExpand092
    {
        private const int TtlTicks = 60;
        private const int MaxEntries = 4096;
        private static readonly Dictionary<ExpandKey, ExpandCache> Cache = new Dictionary<ExpandKey, ExpandCache>();
        private static Type settingsType;
        private static int failureLogs;
        private static bool installed;

        private static long eligibleCalls;
        private static long cacheHits;
        private static long cacheMisses;
        private static long extrasRevalidated;
        private static long extrasRejectedLive;
        private static long extrasReplayed;
        private static long publishes;

        internal struct ExpandState
        {
            internal ExpandKey Key;
            internal int Tick;
            internal HashSet<int> Before;
            internal bool Valid;
        }

        private sealed class ExpandCache
        {
            internal int Tick;
            internal Thing[] Extras;
        }

        static CommonSenseIngredientExpand092()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type utility = AccessTools.TypeByName("CommonSense.Utility");
                Type expandType = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestIngredientsHelper_CommonSensePatch");
                if (utility == null || expandType == null) return;

                settingsType = AccessTools.TypeByName("CommonSense.Settings");
                MethodInfo target = AccessTools.Method(expandType, "PreProcess");
                if (target == null) return;

                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(CommonSenseIngredientExpand092), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(CommonSenseIngredientExpand092), nameof(Postfix)) { priority = Priority.Last });
                installed = true;
                Log.Message("[RimMT] Unified Common Sense ingredient-expansion memo active; other Stage4D experimental caches are not installed.");
            }
            catch (Exception ex)
            {
                installed = false;
                Log.Warning("[RimMT] Common Sense ingredient memo install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool Prefix(Pawn pawn, Predicate<Thing> baseValidator, bool billGiverIsPawn,
            List<Thing> newRelevantThings, HashSet<Thing> processedThings, out ExpandState __state)
        {
            __state = default(ExpandState);
            try
            {
                if (!RimMTThreadGuard.IsMainThread || !ReadSetting("prefer_spoiling_ingredients", false) || billGiverIsPawn ||
                    pawn == null || pawn.Map == null || baseValidator == null || newRelevantThings == null || processedThings == null)
                    return true;

                eligibleCalls++;
                int tick = CurrentTick();
                ExpandKey key = BuildKey(pawn, newRelevantThings, processedThings.Count);
                ExpandCache cached;
                if (Cache.TryGetValue(key, out cached) && cached != null && cached.Extras != null && tick - cached.Tick <= TtlTicks)
                {
                    cacheHits++;
                    Thing[] extras = cached.Extras;
                    for (int i = 0; i < extras.Length; i++)
                    {
                        extrasRevalidated++;
                        Thing thing = extras[i];
                        if (thing == null || thing.Destroyed || !thing.Spawned || thing.Map != pawn.Map || thing.def.IsMedicine || processedThings.Contains(thing))
                        {
                            extrasRejectedLive++;
                            continue;
                        }
                        if (!baseValidator(thing))
                        {
                            extrasRejectedLive++;
                            continue;
                        }
                        if (!pawn.CanReach(thing, PathEndMode.OnCell, Danger.Deadly))
                        {
                            extrasRejectedLive++;
                            continue;
                        }
                        newRelevantThings.Add(thing);
                        processedThings.Add(thing);
                        extrasReplayed++;
                    }
                    return false;
                }

                cacheMisses++;
                __state = new ExpandState
                {
                    Key = key,
                    Tick = tick,
                    Before = new HashSet<int>(newRelevantThings.Where(t => t != null).Select(t => t.thingIDNumber)),
                    Valid = true
                };
                return true;
            }
            catch (Exception ex)
            {
                LogFailure("ingredient memo hit", ex);
                return true;
            }
        }

        public static void Postfix(List<Thing> newRelevantThings, ExpandState __state)
        {
            if (!__state.Valid || newRelevantThings == null) return;
            try
            {
                List<Thing> extras = new List<Thing>();
                for (int i = 0; i < newRelevantThings.Count; i++)
                {
                    Thing thing = newRelevantThings[i];
                    if (thing != null && !__state.Before.Contains(thing.thingIDNumber)) extras.Add(thing);
                }

                if (Cache.Count >= MaxEntries) Cache.Clear();
                Cache[__state.Key] = new ExpandCache { Tick = __state.Tick, Extras = extras.ToArray() };
                publishes++;
            }
            catch (Exception ex) { LogFailure("ingredient memo publish", ex); }
        }

        internal static string Summary()
        {
            long total = eligibleCalls;
            long hits = cacheHits;
            double hitRate = total <= 0 ? 0.0 : hits * 100.0 / total;
            return "CommonSense ingredient memo: installed=" + installed +
                ", eligibleCalls=" + total +
                ", hits=" + hits +
                ", misses=" + cacheMisses +
                ", hitRate=" + hitRate.ToString("F2") + "%" +
                ", extrasRevalidated=" + extrasRevalidated +
                ", extrasRejectedLive=" + extrasRejectedLive +
                ", extrasReplayed=" + extrasReplayed +
                ", publishes=" + publishes +
                ", entries=" + Cache.Count + ".";
        }

        private static ExpandKey BuildKey(Pawn pawn, List<Thing> relevant, int processedCount)
        {
            unchecked
            {
                int hash = 17;
                int count = 0;
                for (int i = 0; i < relevant.Count; i++)
                {
                    Thing thing = relevant[i];
                    if (thing == null) continue;
                    hash = hash * 31 + thing.thingIDNumber;
                    count++;
                }
                return new ExpandKey(pawn.Map, pawn.thingIDNumber, count, hash, processedCount);
            }
        }

        private static bool ReadSetting(string field, bool fallback)
        {
            try
            {
                FieldInfo f = settingsType == null ? null : AccessTools.Field(settingsType, field);
                return f != null && f.FieldType == typeof(bool) ? (bool)f.GetValue(null) : fallback;
            }
            catch { return fallback; }
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        private static void LogFailure(string where, Exception ex)
        {
            if (failureLogs++ < 4)
                Log.Warning("[RimMT] Common Sense " + where + " failed closed: " + ex.GetType().Name + ": " + ex.Message);
        }

        internal struct ExpandKey : IEquatable<ExpandKey>
        {
            private readonly Map map;
            private readonly int pawnId;
            private readonly int relevantCount;
            private readonly int relevantHash;
            private readonly int processedCount;

            internal ExpandKey(Map map, int pawnId, int relevantCount, int relevantHash, int processedCount)
            {
                this.map = map;
                this.pawnId = pawnId;
                this.relevantCount = relevantCount;
                this.relevantHash = relevantHash;
                this.processedCount = processedCount;
            }

            public bool Equals(ExpandKey other)
            {
                return ReferenceEquals(map, other.map) && pawnId == other.pawnId && relevantCount == other.relevantCount &&
                       relevantHash == other.relevantHash && processedCount == other.processedCount;
            }
            public override bool Equals(object obj) { return obj is ExpandKey && Equals((ExpandKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = map == null ? 0 : map.GetHashCode();
                    h = h * 397 ^ pawnId;
                    h = h * 397 ^ relevantCount;
                    h = h * 397 ^ relevantHash;
                    h = h * 397 ^ processedCount;
                    return h;
                }
            }
        }
    }
}
