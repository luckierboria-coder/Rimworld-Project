using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class Stage4DCommonSenseAccelerator
    {
        private static readonly Harmony H = new Harmony("allen.rimmt");
        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly object Sync = new object();

        private const string FeatureId = "parallel.commonSense";
        private const int CleaningTtl = 30;
        private const int IngredientExpandTtl = 60;
        private const int OpportunityNegativeTtl = 15;

        private static Type settingsType;
        private static object scheduler;
        private static MethodInfo schedulerTryEnqueue;
        private static object schedulerPriority;

        private static readonly Dictionary<string, CleaningCache> Cleaning = new Dictionary<string, CleaningCache>();
        private static readonly Dictionary<string, int[]> PathOrders = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, int[]> IngredientOrders = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, ExpandCache> IngredientExpand = new Dictionary<string, ExpandCache>();
        private static readonly Dictionary<string, NegativeHint> OpportunityNegative = new Dictionary<string, NegativeHint>();

        private static long workerQueued, workerRejected, workerCompleted, workerErrors;
        private static long runtimeErrors;
        private static long cleaningCalls, cleaningHits, cleaningPublished, cleaningMissTicks;
        private static long pathCalls, pathHits, pathQueued, pathApplied;
        private static long ingredientSortCalls, ingredientSortHits, ingredientSortQueued, ingredientSortApplied;
        private static long ingredientExpandCalls, ingredientExpandHits, ingredientExpandQueued, ingredientExpandApplied;
        private static long opportunityCalls, opportunityNegativeHits, opportunityQueued;

        private sealed class CleaningCache
        {
            internal int Tick;
            internal Filth[] Things;
            internal int[] Order;
        }

        private sealed class ExpandCache
        {
            internal int Tick;
            internal Thing[] Things;
            internal int[] Order;
        }

        private sealed class NegativeHint
        {
            internal int Tick;
            internal bool Skip;
        }

        internal struct CleaningState
        {
            internal long Start;
            internal string Key;
            internal int Tick;
            internal int StartX;
            internal int StartZ;
        }

        internal struct PathState
        {
            internal string Key;
            internal int[] X;
            internal int[] Z;
            internal int StartX;
            internal int StartZ;
            internal bool Valid;
        }

        internal struct IngredientSortState
        {
            internal string Key;
            internal float[] Potency;
            internal int[] Rot;
            internal bool Valid;
        }

        internal struct ExpandState
        {
            internal string Key;
            internal int Tick;
            internal HashSet<int> Before;
            internal bool Valid;
        }

        internal struct OpportunityState
        {
            internal string Key;
            internal int Tick;
            internal bool Valid;
        }

        static Stage4DCommonSenseAccelerator()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            Type utility = AccessTools.TypeByName("CommonSense.Utility");
            if (utility == null)
            {
                Log.Message("[RimMT] Stage 4D Common Sense Accelerator inactive: Common Sense not detected.");
                return;
            }

            settingsType = AccessTools.TypeByName("CommonSense.Settings");
            BindScheduler();

            Patch(AccessTools.Method(utility, "SelectAllFilth"), nameof(CleaningPrefix), nameof(CleaningPostfix));
            foreach (MethodInfo m in utility.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == "OptimizePath") Patch(m, nameof(PathPrefix), nameof(PathPostfix));

            Type sortType = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestBillIngredientsInSet_AllowMix_CommonSensePatch");
            Patch(sortType == null ? null : AccessTools.Method(sortType, "DoSort"), nameof(IngredientSortPrefix), nameof(IngredientSortPostfix));

            Type expandType = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestIngredientsHelper_CommonSensePatch");
            Patch(expandType == null ? null : AccessTools.Method(expandType, "PreProcess"), nameof(IngredientExpandPrefix), nameof(IngredientExpandPostfix));

            Type oppType = AccessTools.TypeByName("CommonSense.OpportunisticTasks");
            Patch(oppType == null ? null : AccessTools.Method(oppType, "Cleaning_Opportunity"), nameof(OpportunityPrefix), nameof(OpportunityPostfix));

            Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
            if (report != null)
                H.Patch(report, postfix: new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), nameof(Report)) { priority = Priority.Last });

            Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense Accelerator installed: cleaning candidate cache, exact worker path ordering, exact worker spoilage ordering, ingredient-expansion memo, opportunistic negative memo. Experimental assertive mode: errors are logged and only the current call falls back; ordinary errors do not permanently disable a submodule. Worker work uses primitive snapshots only; main thread never waits for workers.");
        }

        private static void BindScheduler()
        {
            try
            {
                Type featureGate = AccessTools.TypeByName("RimMT.FeatureGate");
                MethodInfo register = featureGate == null ? null : AccessTools.Method(featureGate, "Register");
                if (register != null)
                {
                    ParameterInfo[] rp = register.GetParameters();
                    object[] args = rp.Length == 3
                        ? new object[] { FeatureId, true, "Common Sense async accelerator" }
                        : null;
                    if (args != null)
                    {
                        try { register.Invoke(null, args); } catch { }
                    }
                }

                Type runtime = AccessTools.TypeByName("RimMT.RimMTRuntime");
                PropertyInfo p = runtime == null ? null : runtime.GetProperty("Scheduler", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
                scheduler = p == null ? null : p.GetValue(null, null);
                if (scheduler == null) return;
                schedulerTryEnqueue = scheduler.GetType().GetMethod("TryEnqueue", BindingFlags.Instance | BindingFlags.Public);
                ParameterInfo[] ps = schedulerTryEnqueue == null ? null : schedulerTryEnqueue.GetParameters();
                if (ps != null && ps.Length == 3 && ps[1].ParameterType.IsEnum)
                    schedulerPriority = Enum.Parse(ps[1].ParameterType, "Normal");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D scheduler bridge setup error: " + ex);
            }
        }

        private static bool QueueWorker(Action action)
        {
            if (action == null) return false;
            try
            {
                if (scheduler == null || schedulerTryEnqueue == null || schedulerPriority == null) BindScheduler();
                if (scheduler == null || schedulerTryEnqueue == null || schedulerPriority == null)
                {
                    Interlocked.Increment(ref workerRejected);
                    return false;
                }

                Action wrapped = delegate
                {
                    try
                    {
                        action();
                        Interlocked.Increment(ref workerCompleted);
                    }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref workerErrors);
                        Log.Error("[RimMT] Stage 4D worker exception: " + ex);
                    }
                };

                bool ok = (bool)schedulerTryEnqueue.Invoke(scheduler, new object[] { FeatureId, schedulerPriority, wrapped });
                if (ok) Interlocked.Increment(ref workerQueued); else Interlocked.Increment(ref workerRejected);
                return ok;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerRejected);
                Log.Warning("[RimMT] Stage 4D enqueue error: " + ex);
                return false;
            }
        }

        private static void Patch(MethodBase method, string prefix, string postfix)
        {
            if (method == null)
            {
                Log.Warning("[RimMT] Stage 4D target missing for " + prefix + "; remaining accelerators stay active.");
                return;
            }
            try
            {
                H.Patch(method,
                    prefix: new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), postfix) { priority = Priority.Last });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D patch error on " + method + ": " + ex);
            }
        }

        private static int TickNow()
        {
            return Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
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

        // --- 1. Cleaning candidate scan ---------------------------------------------------------
        public static bool CleaningPrefix(Pawn pawn, LocalTargetInfo target, int Limit, ref IEnumerable<Filth> __result, out CleaningState __state)
        {
            __state = default(CleaningState);
            Interlocked.Increment(ref cleaningCalls);
            try
            {
                if (pawn == null || pawn.Map == null) return true;
                Room room = target.HasThing ? target.Thing.GetRoom() : target.Cell.GetRoom(pawn.Map);
                if (room == null) return true;
                int tick = TickNow();
                string key = pawn.Map.GetHashCode() + ":" + room.ID + ":" + pawn.thingIDNumber + ":" + Limit;
                __state = new CleaningState { Start = Stopwatch.GetTimestamp(), Key = key, Tick = tick, StartX = pawn.Position.x, StartZ = pawn.Position.z };

                CleaningCache cache;
                lock (Sync) Cleaning.TryGetValue(key, out cache);
                if (cache == null || cache.Things == null || cache.Order == null || tick - cache.Tick > CleaningTtl) return true;

                WorkGiverDef cleanDef = DefDatabase<WorkGiverDef>.GetNamedSilentFail("CleanFilth");
                WorkGiver_Scanner scanner = cleanDef == null ? null : cleanDef.Worker as WorkGiver_Scanner;
                if (scanner == null) return true;

                List<Filth> result = new List<Filth>(cache.Order.Length);
                for (int i = 0; i < cache.Order.Length; i++)
                {
                    int idx = cache.Order[i];
                    if (idx < 0 || idx >= cache.Things.Length) continue;
                    Filth f = cache.Things[idx];
                    if (f == null || f.Destroyed || !f.Spawned || f.Map != pawn.Map || !f.Position.InAllowedArea(pawn)) continue;
                    if (!scanner.HasJobOnThing(pawn, f)) continue;
                    Room fr = f.GetRoom();
                    if (fr == null || (fr != room && !fr.IsDoorway)) continue;
                    result.Add(f);
                    if (Limit > 0 && result.Count >= Limit) break;
                }
                __result = result;
                Interlocked.Increment(ref cleaningHits);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D cleaning hit error; Common Sense original runs for this call: " + ex);
                return true;
            }
        }

        public static void CleaningPostfix(Pawn pawn, LocalTargetInfo target, int Limit, ref IEnumerable<Filth> __result, CleaningState __state)
        {
            if (__state.Start == 0) return;
            try
            {
                Interlocked.Add(ref cleaningMissTicks, Stopwatch.GetTimestamp() - __state.Start);
                if (__result == null || string.IsNullOrEmpty(__state.Key)) return;
                List<Filth> list = __result as List<Filth> ?? __result.ToList();
                __result = list;
                Filth[] refs = list.ToArray();
                int[] x = new int[refs.Length];
                int[] z = new int[refs.Length];
                for (int i = 0; i < refs.Length; i++) { x[i] = refs[i].Position.x; z[i] = refs[i].Position.z; }

                CleaningCache cache = new CleaningCache { Tick = __state.Tick, Things = refs, Order = null };
                lock (Sync) Cleaning[__state.Key] = cache;
                string key = __state.Key;
                int sx = __state.StartX, sz = __state.StartZ;
                if (QueueWorker(delegate
                {
                    int[] order = ExactNearestOrder(x, z, sx, sz);
                    lock (Sync)
                    {
                        CleaningCache c;
                        if (Cleaning.TryGetValue(key, out c) && c == cache) c.Order = order;
                    }
                    Interlocked.Increment(ref cleaningPublished);
                })) { }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D cleaning publish error: " + ex);
            }
        }

        // --- 2. Exact Common Sense path queue ordering ------------------------------------------
        public static bool PathPrefix(object[] __args, out PathState __state)
        {
            __state = default(PathState);
            Interlocked.Increment(ref pathCalls);
            try
            {
                if (__args == null || __args.Length == 0 || __args[0] == null) return true;
                IList<LocalTargetInfo> locals = __args[0] as IList<LocalTargetInfo>;
                IList<ThingCount> counts = __args[0] as IList<ThingCount>;
                if (locals == null && counts == null) return true;
                int n = locals != null ? locals.Count : counts.Count;
                if (n < 4) return true;

                int[] ids = new int[n], x = new int[n], z = new int[n];
                for (int i = 0; i < n; i++)
                {
                    Thing t = locals != null ? locals[i].Thing : counts[i].Thing;
                    ids[i] = t == null ? 0 : t.thingIDNumber;
                    IntVec3 cell = locals != null ? locals[i].Cell : (t == null ? IntVec3.Invalid : t.Position);
                    x[i] = cell.x; z[i] = cell.z;
                }
                Thing starter = __args.Length > 1 ? __args[1] as Thing : null;
                int sx = starter == null ? int.MinValue : starter.Position.x;
                int sz = starter == null ? int.MinValue : starter.Position.z;
                string key = Signature(ids, x, z, sx, sz);

                int[] order;
                lock (Sync) PathOrders.TryGetValue(key, out order);
                if (order != null && order.Length == n)
                {
                    ApplyOrder(__args[0], order);
                    Interlocked.Increment(ref pathHits);
                    Interlocked.Increment(ref pathApplied);
                    return false;
                }
                __state = new PathState { Key = key, X = x, Z = z, StartX = sx, StartZ = sz, Valid = true };
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D path hit error; original OptimizePath runs: " + ex);
                return true;
            }
        }

        public static void PathPostfix(PathState __state)
        {
            if (!__state.Valid) return;
            string key = __state.Key; int[] x = __state.X; int[] z = __state.Z; int sx = __state.StartX; int sz = __state.StartZ;
            if (QueueWorker(delegate
            {
                int[] order = ExactNearestOrder(x, z, sx, sz);
                lock (Sync) PathOrders[key] = order;
            })) Interlocked.Increment(ref pathQueued);
        }

        // --- 3a. Exact spoilage / potency ingredient ordering ----------------------------------
        public static bool IngredientSortPrefix(List<Thing> availableThings, Bill bill, out IngredientSortState __state)
        {
            __state = default(IngredientSortState);
            Interlocked.Increment(ref ingredientSortCalls);
            try
            {
                if (!ReadSetting("prefer_spoiling_ingredients", false) || availableThings == null || availableThings.Count < 4 || bill == null || bill.recipe == null || bill.recipe.addsHediff != null)
                    return true;

                int n = availableThings.Count;
                int[] ids = new int[n], rot = new int[n];
                float[] potency = new float[n];
                for (int i = 0; i < n; i++)
                {
                    Thing t = availableThings[i];
                    ids[i] = t == null ? 0 : t.thingIDNumber;
                    potency[i] = t == null ? 0f : t.GetStatValue(StatDefOf.MedicalPotency);
                    CompRottable r = t == null ? null : t.TryGetComp<CompRottable>();
                    rot[i] = r == null ? int.MaxValue : (int)(r.PropsRot.TicksToRotStart - r.RotProgress) / 2500;
                }
                string key = bill.recipe.shortHash + ":" + Signature(ids, rot, null, 0, 0);
                int[] order;
                lock (Sync) IngredientOrders.TryGetValue(key, out order);
                if (order != null && order.Length == n)
                {
                    ApplyOrder(availableThings, order);
                    Interlocked.Increment(ref ingredientSortHits);
                    Interlocked.Increment(ref ingredientSortApplied);
                    return false;
                }
                __state = new IngredientSortState { Key = key, Potency = potency, Rot = rot, Valid = true };
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D ingredient-sort hit error; Common Sense sort runs: " + ex);
                return true;
            }
        }

        public static void IngredientSortPostfix(IngredientSortState __state)
        {
            if (!__state.Valid) return;
            string key = __state.Key; float[] p = __state.Potency; int[] r = __state.Rot; int n = p.Length;
            if (QueueWorker(delegate
            {
                int[] order = Enumerable.Range(0, n).ToArray();
                Array.Sort(order, delegate(int a, int b)
                {
                    if (p[a] > p[b]) return -1;
                    if (p[a] < p[b]) return 1;
                    bool ar = r[a] != int.MaxValue, br = r[b] != int.MaxValue;
                    if (!ar) return !br ? 0 : 1;
                    if (!br) return -1;
                    return r[a] - r[b];
                });
                lock (Sync) IngredientOrders[key] = order;
            })) Interlocked.Increment(ref ingredientSortQueued);
        }

        // --- 3b. Common Sense storage-group ingredient expansion memo ---------------------------
        public static bool IngredientExpandPrefix(Pawn pawn, Predicate<Thing> baseValidator, bool billGiverIsPawn, List<Thing> newRelevantThings, HashSet<Thing> processedThings, out ExpandState __state)
        {
            __state = default(ExpandState);
            Interlocked.Increment(ref ingredientExpandCalls);
            try
            {
                if (!ReadSetting("prefer_spoiling_ingredients", false) || billGiverIsPawn || pawn == null || pawn.Map == null || baseValidator == null || newRelevantThings == null || processedThings == null)
                    return true;

                int tick = TickNow();
                int[] ids = newRelevantThings.Where(t => t != null).Select(t => t.thingIDNumber).ToArray();
                string key = pawn.Map.GetHashCode() + ":" + pawn.thingIDNumber + ":" + Signature(ids, null, null, processedThings.Count, 0);
                ExpandCache cache;
                lock (Sync) IngredientExpand.TryGetValue(key, out cache);
                if (cache != null && cache.Things != null && cache.Order != null && tick - cache.Tick <= IngredientExpandTtl)
                {
                    for (int oi = 0; oi < cache.Order.Length; oi++)
                    {
                        int idx = cache.Order[oi];
                        if (idx < 0 || idx >= cache.Things.Length) continue;
                        Thing t = cache.Things[idx];
                        if (t == null || t.Destroyed || !t.Spawned || t.Map != pawn.Map || t.def.IsMedicine || processedThings.Contains(t)) continue;
                        if (!baseValidator(t)) continue;
                        if (!pawn.CanReach(t, PathEndMode.OnCell, Danger.Deadly)) continue;
                        newRelevantThings.Add(t);
                        processedThings.Add(t);
                    }
                    Interlocked.Increment(ref ingredientExpandHits);
                    Interlocked.Increment(ref ingredientExpandApplied);
                    return false;
                }

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
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D ingredient-expand hit error; Common Sense expansion runs: " + ex);
                return true;
            }
        }

        public static void IngredientExpandPostfix(List<Thing> newRelevantThings, ExpandState __state)
        {
            if (!__state.Valid || newRelevantThings == null) return;
            try
            {
                Thing[] extras = newRelevantThings.Where(t => t != null && !__state.Before.Contains(t.thingIDNumber)).ToArray();
                ExpandCache cache = new ExpandCache { Tick = __state.Tick, Things = extras, Order = null };
                lock (Sync) IngredientExpand[__state.Key] = cache;
                int[] ids = extras.Select(t => t.thingIDNumber).ToArray();
                string key = __state.Key;
                if (QueueWorker(delegate
                {
                    int[] order = Enumerable.Range(0, ids.Length).OrderBy(i => ids[i]).ToArray();
                    lock (Sync)
                    {
                        ExpandCache c;
                        if (IngredientExpand.TryGetValue(key, out c) && c == cache) c.Order = order;
                    }
                })) Interlocked.Increment(ref ingredientExpandQueued);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D ingredient-expand publish error: " + ex);
            }
        }

        // --- 4. Opportunistic cleaning negative memo --------------------------------------------
        public static bool OpportunityPrefix(Job currJob, Pawn pawn, int Limit, ref Job __result, out OpportunityState __state)
        {
            __state = default(OpportunityState);
            Interlocked.Increment(ref opportunityCalls);
            try
            {
                if (currJob == null || pawn == null || pawn.Map == null || !currJob.targetA.IsValid) return true;
                Thing building = currJob.targetA.Thing;
                Thing target = currJob.targetB.Thing;
                if (target == null && currJob.targetQueueB != null && currJob.targetQueueB.Count > 0) target = currJob.targetQueueB[0].Thing;
                int tick = TickNow();
                string key = pawn.Map.GetHashCode() + ":" + pawn.thingIDNumber + ":" + currJob.def.shortHash + ":" + pawn.Position.x + "," + pawn.Position.z + ":" +
                    (building == null ? "-" : building.thingIDNumber + "@" + building.Position.x + "," + building.Position.z) + ":" +
                    (target == null ? "-" : target.thingIDNumber + "@" + target.Position.x + "," + target.Position.z) + ":" + Limit + ":" + ReadSetting("calculate_full_path", false);

                NegativeHint hint;
                lock (Sync) OpportunityNegative.TryGetValue(key, out hint);
                if (hint != null && hint.Skip && tick - hint.Tick <= OpportunityNegativeTtl)
                {
                    __result = null;
                    Interlocked.Increment(ref opportunityNegativeHits);
                    return false;
                }
                __state = new OpportunityState { Key = key, Tick = tick, Valid = true };
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeErrors);
                Log.Warning("[RimMT] Stage 4D opportunity hit error; Common Sense original runs: " + ex);
                return true;
            }
        }

        public static void OpportunityPostfix(Job __result, OpportunityState __state)
        {
            if (!__state.Valid || __result != null) return;
            string key = __state.Key; int tick = __state.Tick;
            if (QueueWorker(delegate
            {
                lock (Sync) OpportunityNegative[key] = new NegativeHint { Tick = tick, Skip = true };
            })) Interlocked.Increment(ref opportunityQueued);
        }

        private static int[] ExactNearestOrder(int[] x, int[] z, int sx, int sz)
        {
            int n = x.Length;
            int[] order = Enumerable.Range(0, n).ToArray();
            if (n == 0) return order;

            if (sx != int.MinValue)
            {
                int best = 0;
                long bestD = Dist(x[order[0]], z[order[0]], sx, sz);
                for (int i = 1; i < n; i++)
                {
                    long d = Dist(x[order[i]], z[order[i]], sx, sz);
                    if (Math.Abs(d) < Math.Abs(bestD)) { bestD = d; best = i; }
                }
                Swap(order, 0, best);
            }

            for (int i = 0; i < n - 1; i++)
            {
                int best = i + 1;
                long bestD = Dist(x[order[i]], z[order[i]], x[order[best]], z[order[best]]);
                for (int c = i + 2; c < n; c++)
                {
                    long d = Dist(x[order[i]], z[order[i]], x[order[c]], z[order[c]]);
                    if (Math.Abs(d) < Math.Abs(bestD)) { bestD = d; best = c; }
                }
                Swap(order, i + 1, best);
            }
            return order;
        }

        private static void Swap(int[] a, int x, int y) { if (x == y) return; int t = a[x]; a[x] = a[y]; a[y] = t; }
        private static long Dist(int ax, int az, int bx, int bz) { long dx = ax - bx, dz = az - bz; return dx * dx + dz * dz; }

        private static void ApplyOrder(object listObj, int[] order)
        {
            IList<LocalTargetInfo> locals = listObj as IList<LocalTargetInfo>;
            if (locals != null)
            {
                LocalTargetInfo[] copy = locals.ToArray();
                for (int i = 0; i < order.Length; i++) locals[i] = copy[order[i]];
                return;
            }
            IList<ThingCount> counts = listObj as IList<ThingCount>;
            if (counts != null)
            {
                ThingCount[] copy = counts.ToArray();
                for (int i = 0; i < order.Length; i++) counts[i] = copy[order[i]];
                return;
            }
            IList<Thing> things = listObj as IList<Thing>;
            if (things != null)
            {
                Thing[] copy = things.ToArray();
                for (int i = 0; i < order.Length; i++) things[i] = copy[order[i]];
            }
        }

        private static string Signature(int[] a, int[] b, int[] c, int x, int z)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                for (int i = 0; i < a.Length; i++)
                {
                    h ^= a[i]; h *= 1099511628211L;
                    if (b != null) { h ^= b[i]; h *= 1099511628211L; }
                    if (c != null) { h ^= c[i]; h *= 1099511628211L; }
                }
                h ^= x; h *= 1099511628211L; h ^= z;
                return h.ToString("X16") + ":" + a.Length;
            }
        }

        public static void Report()
        {
            Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense Accelerator report: worker queued/completed/rejected/errors=" +
                workerQueued + "/" + workerCompleted + "/" + workerRejected + "/" + workerErrors +
                ", runtimeErrors=" + runtimeErrors +
                "; cleaning calls/hits/published/missMs=" + cleaningCalls + "/" + cleaningHits + "/" + cleaningPublished + "/" + (cleaningMissTicks * ToMs).ToString("F2") +
                "; path calls/hits/queued/applied=" + pathCalls + "/" + pathHits + "/" + pathQueued + "/" + pathApplied +
                "; ingredientSort calls/hits/queued/applied=" + ingredientSortCalls + "/" + ingredientSortHits + "/" + ingredientSortQueued + "/" + ingredientSortApplied +
                "; ingredientExpand calls/hits/queued/applied=" + ingredientExpandCalls + "/" + ingredientExpandHits + "/" + ingredientExpandQueued + "/" + ingredientExpandApplied +
                "; opportunity calls/negativeHits/queued=" + opportunityCalls + "/" + opportunityNegativeHits + "/" + opportunityQueued +
                ". Main thread never waits for worker completion.");
        }
    }
}
