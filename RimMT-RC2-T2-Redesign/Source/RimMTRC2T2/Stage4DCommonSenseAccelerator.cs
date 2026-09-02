using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class Stage4DCommonSenseAccelerator
    {
        private static readonly Harmony H = new Harmony("allen.rimmt");
        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;

        private static Type settingsType;
        private static object scheduler;
        private static MethodInfo schedulerTryEnqueue;
        private static object schedulerPriority;
        private static string schedulerFeatureId = "parallel.jobPartition";

        private static readonly object Sync = new object();
        private static readonly Dictionary<string, CleaningCache> Cleaning = new Dictionary<string, CleaningCache>();
        private static readonly Dictionary<string, int[]> PathOrders = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, int[]> IngredientOrders = new Dictionary<string, int[]>();
        private static readonly Dictionary<string, OpportunityHint> OpportunityHints = new Dictionary<string, OpportunityHint>();

        private static long workerQueued, workerRejected, workerCompleted, workerExceptions;
        private static long cleanCalls, cleanHits, cleanPublished, cleanOriginalMsTicks;
        private static long pathCalls, pathHits, pathQueued, pathApplied;
        private static long ingCalls, ingHits, ingQueued, ingApplied;
        private static long oppCalls, oppHits, oppStrongSkips, oppQueued;
        private static long runtimeExceptions;

        private sealed class CleaningCache
        {
            public int Tick;
            public Filth[] Things;
            public int[] Order;
        }

        private sealed class OpportunityHint
        {
            public int Tick;
            public bool StrongSkip;
        }

        internal struct CleanState
        {
            public long Start;
            public string Key;
            public Pawn Pawn;
            public LocalTargetInfo Target;
        }

        internal struct OrderState
        {
            public string Key;
            public int[] X;
            public int[] Z;
            public int[] Ids;
            public int StarterX;
            public int StarterZ;
            public bool Valid;
        }

        internal struct IngredientState
        {
            public string Key;
            public int[] Ids;
            public float[] Potency;
            public int[] RotBucket;
            public bool Valid;
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

            Patch(AccessTools.Method(utility, "SelectAllFilth"), nameof(CleanPrefix), nameof(CleanPostfix));

            foreach (MethodInfo m in utility.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                if (m.Name == "OptimizePath")
                    Patch(m, nameof(PathPrefix), nameof(PathPostfix));

            Type ingSort = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestBillIngredientsInSet_AllowMix_CommonSensePatch");
            Patch(ingSort == null ? null : AccessTools.Method(ingSort, "DoSort"), nameof(IngredientPrefix), nameof(IngredientPostfix));

            Type opp = AccessTools.TypeByName("CommonSense.OpportunisticTasks");
            Patch(opp == null ? null : AccessTools.Method(opp, "Cleaning_Opportunity"), nameof(OpportunityPrefix), nameof(OpportunityPostfix));

            Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
            if (report != null)
                H.Patch(report, postfix: new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), nameof(Report)) { priority = Priority.Last });

            Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense Accelerator installed: cleaning-cache + worker path ordering + worker ingredient ordering + worker opportunity hints. Experimental assertive mode: per-call fallback only, no permanent module shutdown on ordinary exceptions. Scheduler bridge=" + (scheduler != null ? "RimMT.JobScheduler" : "unavailable") + ".");
        }

        private static void BindScheduler()
        {
            try
            {
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
                Log.Warning("[RimMT] Stage 4D scheduler bridge setup error: " + ex);
            }
        }

        private static void Patch(MethodBase m, string pre, string post)
        {
            if (m == null)
            {
                Log.Warning("[RimMT] Stage 4D target missing for " + pre + "; continuing with remaining Common Sense accelerators.");
                return;
            }
            try
            {
                H.Patch(m,
                    prefix: string.IsNullOrEmpty(pre) ? null : new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), pre) { priority = Priority.First },
                    postfix: string.IsNullOrEmpty(post) ? null : new HarmonyMethod(typeof(Stage4DCommonSenseAccelerator), post) { priority = Priority.Last });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D patch error on " + m + ": " + ex);
            }
        }

        private static bool QueueWorker(Action action)
        {
            if (action == null) return false;
            try
            {
                if (scheduler == null || schedulerTryEnqueue == null || schedulerPriority == null)
                    BindScheduler();
                if (scheduler == null || schedulerTryEnqueue == null || schedulerPriority == null)
                {
                    Interlocked.Increment(ref workerRejected);
                    return false;
                }
                Action wrapped = delegate
                {
                    try { action(); Interlocked.Increment(ref workerCompleted); }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref workerExceptions);
                        Log.Error("[RimMT] Stage 4D worker exception: " + ex);
                    }
                };
                bool ok = (bool)schedulerTryEnqueue.Invoke(scheduler, new object[] { schedulerFeatureId, schedulerPriority, wrapped });
                if (ok) Interlocked.Increment(ref workerQueued); else Interlocked.Increment(ref workerRejected);
                return ok;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerRejected);
                Log.Warning("[RimMT] Stage 4D worker enqueue error: " + ex.Message);
                return false;
            }
        }

        // ---------------- Cleaning candidate scan ----------------
        public static bool CleanPrefix(Pawn pawn, LocalTargetInfo target, int Limit, ref IEnumerable<Filth> __result, out CleanState __state)
        {
            __state = default(CleanState);
            Interlocked.Increment(ref cleanCalls);
            try
            {
                if (pawn == null || pawn.Map == null) return true;
                string key = CleaningKey(pawn, target, Limit);
                __state.Key = key; __state.Pawn = pawn; __state.Target = target; __state.Start = Stopwatch.GetTimestamp();
                CleaningCache c;
                lock (Sync) Cleaning.TryGetValue(key, out c);
                int now = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                if (c == null || c.Order == null || c.Things == null || now - c.Tick > 30) return true;

                List<Filth> valid = new List<Filth>(c.Order.Length);
                for (int i = 0; i < c.Order.Length; i++)
                {
                    int idx = c.Order[i];
                    if (idx < 0 || idx >= c.Things.Length) continue;
                    Filth f = c.Things[idx];
                    if (f == null || f.Destroyed || !f.Spawned || f.Map != pawn.Map || f.IsForbidden(pawn)) continue;
                    valid.Add(f);
                    if (Limit > 0 && valid.Count >= Limit) break;
                }
                __result = valid;
                Interlocked.Increment(ref cleanHits);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D cleaning cache call error; original Common Sense scan continues: " + ex);
                return true;
            }
        }

        public static void CleanPostfix(Pawn pawn, LocalTargetInfo target, int Limit, ref IEnumerable<Filth> __result, CleanState __state)
        {
            if (__state.Start == 0) return;
            try
            {
                Interlocked.Add(ref cleanOriginalMsTicks, Stopwatch.GetTimestamp() - __state.Start);
                if (__result == null || string.IsNullOrEmpty(__state.Key)) return;
                List<Filth> list = __result as List<Filth> ?? __result.ToList();
                __result = list;
                Filth[] refs = list.ToArray();
                int[] x = new int[refs.Length], z = new int[refs.Length];
                IntVec3 start = pawn.Position;
                for (int i = 0; i < refs.Length; i++) { x[i] = refs[i].Position.x; z[i] = refs[i].Position.z; }
                string key = __state.Key;
                QueueWorker(delegate
                {
                    int[] order = NearestOrder(x, z, start.x, start.z);
                    int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame; // publication timestamp only
                    lock (Sync) Cleaning[key] = new CleaningCache { Tick = tick, Things = refs, Order = order };
                    Interlocked.Increment(ref cleanPublished);
                });
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D cleaning publish error: " + ex);
            }
        }

        private static string CleaningKey(Pawn pawn, LocalTargetInfo target, int limit)
        {
            Room room = target.HasThing ? target.Thing.GetRoom() : target.Cell.GetRoom(pawn.Map);
            int roomId = room == null ? -1 : room.ID;
            return pawn.Map.GetHashCode() + ":" + roomId + ":" + pawn.thingIDNumber + ":" + limit;
        }

        // ---------------- Common Sense OptimizePath ----------------
        public static bool PathPrefix(object[] __args, out OrderState __state)
        {
            __state = default(OrderState);
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
                    IntVec3 c = locals != null ? locals[i].Cell : (t == null ? IntVec3.Invalid : t.Position);
                    x[i] = c.x; z[i] = c.z;
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
                __state = new OrderState { Key = key, X = x, Z = z, Ids = ids, StarterX = sx, StarterZ = sz, Valid = true };
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D path ordering error; Common Sense original continues: " + ex);
                return true;
            }
        }

        public static void PathPostfix(OrderState __state)
        {
            if (!__state.Valid || string.IsNullOrEmpty(__state.Key)) return;
            string key = __state.Key; int[] x = __state.X, z = __state.Z; int sx = __state.StarterX, sz = __state.StarterZ;
            if (QueueWorker(delegate { int[] o = NearestOrder(x, z, sx, sz); lock (Sync) PathOrders[key] = o; }))
                Interlocked.Increment(ref pathQueued);
        }

        // ---------------- Ingredient rot/potency sorting ----------------
        public static bool IngredientPrefix(List<Thing> availableThings, Bill bill, out IngredientState __state)
        {
            __state = default(IngredientState);
            Interlocked.Increment(ref ingCalls);
            try
            {
                if (availableThings == null || availableThings.Count < 4 || bill == null || bill.recipe == null) return true;
                int n = availableThings.Count;
                int[] ids = new int[n], rot = new int[n];
                float[] pot = new float[n];
                for (int i = 0; i < n; i++)
                {
                    Thing t = availableThings[i];
                    ids[i] = t == null ? 0 : t.thingIDNumber;
                    pot[i] = t == null ? 0f : t.GetStatValue(StatDefOf.MedicalPotency);
                    CompRottable r = t == null ? null : t.TryGetComp<CompRottable>();
                    rot[i] = r == null ? int.MaxValue : (int)((r.PropsRot.TicksToRotStart - r.RotProgress) / 2500f);
                }
                string key = bill.recipe.shortHash + ":" + Signature(ids, rot, null, 0, 0);
                int[] order;
                lock (Sync) IngredientOrders.TryGetValue(key, out order);
                if (order != null && order.Length == n)
                {
                    ApplyOrder(availableThings, order);
                    Interlocked.Increment(ref ingHits); Interlocked.Increment(ref ingApplied);
                    return false;
                }
                __state = new IngredientState { Key = key, Ids = ids, Potency = pot, RotBucket = rot, Valid = true };
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D ingredient ordering error; Common Sense original continues: " + ex);
                return true;
            }
        }

        public static void IngredientPostfix(IngredientState __state)
        {
            if (!__state.Valid) return;
            string key = __state.Key; float[] p = __state.Potency; int[] r = __state.RotBucket; int n = p.Length;
            if (QueueWorker(delegate
            {
                int[] o = Enumerable.Range(0, n).ToArray();
                Array.Sort(o, delegate(int a, int b)
                {
                    int pc = p[b].CompareTo(p[a]);
                    if (pc != 0) return pc;
                    return r[a].CompareTo(r[b]);
                });
                lock (Sync) IngredientOrders[key] = o;
            })) Interlocked.Increment(ref ingQueued);
        }

        // ---------------- Opportunistic cleaning path hint ----------------
        public static bool OpportunityPrefix(Job currJob, Pawn pawn, int Limit, ref Job __result)
        {
            Interlocked.Increment(ref oppCalls);
            try
            {
                string key; int sx, sz, bx, bz, tx, tz;
                if (!OpportunityGeometry(currJob, pawn, out key, out sx, out sz, out bx, out bz, out tx, out tz)) return true;
                OpportunityHint h; lock (Sync) OpportunityHints.TryGetValue(key, out h);
                int now = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                if (h != null && now - h.Tick <= 60)
                {
                    Interlocked.Increment(ref oppHits);
                    if (h.StrongSkip)
                    {
                        __result = null; Interlocked.Increment(ref oppStrongSkips); return false;
                    }
                    return true;
                }
                if (QueueWorker(delegate
                {
                    double stot = Math.Sqrt((sx - tx) * (long)(sx - tx) + (sz - tz) * (long)(sz - tz));
                    double stob = Math.Sqrt((sx - bx) * (long)(sx - bx) + (sz - bz) * (long)(sz - bz));
                    double btot = Math.Sqrt((bx - tx) * (long)(bx - tx) + (bz - tz) * (long)(bz - tz));
                    bool strong = stob > 20.0 && stob + btot > 0.0 && stot / (stob + btot) < 0.45;
                    int tick = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
                    lock (Sync) OpportunityHints[key] = new OpportunityHint { Tick = tick, StrongSkip = strong };
                })) Interlocked.Increment(ref oppQueued);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref runtimeExceptions);
                Log.Warning("[RimMT] Stage 4D opportunity hint error; Common Sense original continues: " + ex);
                return true;
            }
        }

        public static void OpportunityPostfix() { }

        private static bool OpportunityGeometry(Job job, Pawn pawn, out string key, out int sx, out int sz, out int bx, out int bz, out int tx, out int tz)
        {
            key = null; sx = sz = bx = bz = tx = tz = 0;
            if (job == null || pawn == null || pawn.Map == null || !job.targetA.IsValid) return false;
            Thing building = job.targetA.Thing;
            if (building == null) return false;
            Thing target = job.targetB.Thing;
            if (target == null && job.targetQueueB != null && job.targetQueueB.Count > 0) target = job.targetQueueB[0].Thing;
            if (target == null) return false;
            sx = pawn.Position.x; sz = pawn.Position.z; bx = building.Position.x; bz = building.Position.z; tx = target.Position.x; tz = target.Position.z;
            key = pawn.Map.GetHashCode() + ":" + pawn.thingIDNumber + ":" + sx + "," + sz + ":" + bx + "," + bz + ":" + tx + "," + tz;
            return true;
        }

        private static int[] NearestOrder(int[] x, int[] z, int sx, int sz)
        {
            int n = x.Length; int[] order = Enumerable.Range(0, n).ToArray();
            for (int i = 0; i < n; i++)
            {
                int best = i; long bestD = Dist(i == 0 ? sx : x[order[i - 1]], i == 0 ? sz : z[order[i - 1]], x[order[i]], z[order[i]]);
                for (int j = i + 1; j < n; j++)
                {
                    long d = Dist(i == 0 ? sx : x[order[i - 1]], i == 0 ? sz : z[order[i - 1]], x[order[j]], z[order[j]]);
                    if (d < bestD) { bestD = d; best = j; }
                }
                if (best != i) { int t = order[i]; order[i] = order[best]; order[best] = t; }
            }
            return order;
        }

        private static long Dist(int ax, int az, int bx, int bz) { long dx = ax - bx, dz = az - bz; return dx * dx + dz * dz; }

        private static void ApplyOrder(object listObj, int[] order)
        {
            IList<LocalTargetInfo> locals = listObj as IList<LocalTargetInfo>;
            if (locals != null)
            {
                LocalTargetInfo[] copy = locals.ToArray(); for (int i = 0; i < order.Length; i++) locals[i] = copy[order[i]]; return;
            }
            IList<ThingCount> counts = listObj as IList<ThingCount>;
            if (counts != null)
            {
                ThingCount[] copy = counts.ToArray(); for (int i = 0; i < order.Length; i++) counts[i] = copy[order[i]]; return;
            }
            IList<Thing> things = listObj as IList<Thing>;
            if (things != null)
            {
                Thing[] copy = things.ToArray(); for (int i = 0; i < order.Length; i++) things[i] = copy[order[i]];
            }
        }

        private static string Signature(int[] a, int[] b, int[] c, int x, int z)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                for (int i = 0; i < a.Length; i++) { h ^= a[i]; h *= 1099511628211L; if (b != null) { h ^= b[i]; h *= 1099511628211L; } if (c != null) { h ^= c[i]; h *= 1099511628211L; } }
                h ^= x; h *= 1099511628211L; h ^= z;
                return h.ToString("X16") + ":" + a.Length;
            }
        }

        public static void Report()
        {
            double cleanMs = Interlocked.Read(ref cleanOriginalMsTicks) * ToMs;
            Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense Accelerator report: worker queued/completed/rejected/errors=" + workerQueued + "/" + workerCompleted + "/" + workerRejected + "/" + workerExceptions +
                ", runtimeExceptions=" + runtimeExceptions +
                "; cleaning calls/hits/published=" + cleanCalls + "/" + cleanHits + "/" + cleanPublished + ", measuredOriginalMs=" + cleanMs.ToString("F2") +
                "; path calls/hits/queued/applied=" + pathCalls + "/" + pathHits + "/" + pathQueued + "/" + pathApplied +
                "; ingredient calls/hits/queued/applied=" + ingCalls + "/" + ingHits + "/" + ingQueued + "/" + ingApplied +
                "; opportunity calls/hits/queued/strongSkips=" + oppCalls + "/" + oppHits + "/" + oppQueued + "/" + oppStrongSkips +
                ". Worker tasks operate on primitive snapshots only; no main-thread wait is used.");
        }
    }
}
