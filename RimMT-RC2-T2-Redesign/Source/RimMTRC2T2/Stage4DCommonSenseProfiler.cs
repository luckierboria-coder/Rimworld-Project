using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class Stage4DCommonSenseProfiler
    {
        private static readonly Harmony H = new Harmony("allen.rimmt");
        private static readonly double ToMs = 1000.0 / Stopwatch.Frequency;
        private static readonly object Gate = new object();
        private static readonly Dictionary<MethodBase, Target> Targets = new Dictionary<MethodBase, Target>();
        private static readonly List<Target> Ordered = new List<Target>();
        private static long failures;
        private static bool installed;
        private static Type settingsType;

        private sealed class Target
        {
            public string Name;
            public string Module;
            public string ParallelClass;
            public MethodBase Method;
            public int SampleEvery;
            public long Seen;
            public long Samples;
            public long TotalTicks;
            public long MaxTicks;
        }

        public struct State
        {
            public Target Target;
            public long Start;
        }

        static Stage4DCommonSenseProfiler()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type utility = AccessTools.TypeByName("CommonSense.Utility");
                if (utility == null)
                {
                    Log.Message("[RimMT] Stage 4D Common Sense profiler inactive: Common Sense assembly not detected.");
                    return;
                }

                settingsType = AccessTools.TypeByName("CommonSense.Settings");

                // Cleaning / DoBill: strongest current suspects. Inclusive wrappers intentionally overlap;
                // the report states this and the optimizer phase will use the decomposition only as guidance.
                AddAll(utility, "SelectAllFilth", "cleaning.scan", "SnapshotSafe", 1);
                AddAll(utility, "AddFilthToQueue", "cleaning.queue", "WorkerCandidate", 1);
                AddAll(utility, "OptimizePath", "path.order", "WorkerCandidate", 1);
                AddAll(utility, "IncapableOfCleaning", "cleaning.gate", "MainThreadOnly", 16);

                Type opp = AccessTools.TypeByName("CommonSense.OpportunisticTasks");
                Add(opp, "MakeCleaningJob", "opportunity.clean", "SnapshotSafe", 1);
                Add(opp, "Cleaning_Opportunity", "opportunity.clean", "WorkerCandidate", 1);
                Add(opp, "Hauling_Opportunity", "opportunity.haul", "SnapshotSafe", 1);

                Type ingPre = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestIngredientsHelper_CommonSensePatch");
                Add(ingPre, "PreProcess", "ingredient.expand", "SnapshotSafe", 1);
                Type ingSort = AccessTools.TypeByName("CommonSense.IngredientPriority+WorkGiver_DoBill_TryFindBestBillIngredientsInSet_AllowMix_CommonSensePatch");
                Add(ingSort, "DoSort", "ingredient.rotSort", "WorkerCandidate", 1);
                Type foodOpt = AccessTools.TypeByName("CommonSense.IngredientPriority+FoodUtility_FoodOptimality");
                Add(foodOpt, "Postfix", "food.rotPriority", "SnapshotSafe", 8);

                Type unload = AccessTools.TypeByName("CommonSense.CompUnloadChecker");
                Add(unload, "GetFirstMarked", "unload.scan", "SnapshotSafe", 4);
                Add(unload, "GetChecker", "unload.comp", "MainThreadOnly", 16);

                Type wander = AccessTools.TypeByName("CommonSense.JobGiver_Wander_TryGiveJob_CommonSensePatch");
                Add(wander, "FindRoofedInHomeArea", "wander.safe", "WorkerCandidate", 1);
                Type wanderCell = AccessTools.TypeByName("CommonSense.RCellFinder_CanWanderToCell_CommonSensePatch");
                Add(wanderCell, "Postfix", "wander.polite", "SnapshotSafe", 16);

                Type visit = AccessTools.TypeByName("CommonSense.WorkGiver_VisitSickPawnPatches");
                if (visit != null)
                {
                    AddAllNonTrivial(visit, "visitSick", "SnapshotSafe", 8);
                }

                Type ingest = AccessTools.TypeByName("CommonSense.JobDriver_IngestPatches");
                if (ingest != null)
                {
                    AddAllNonTrivial(ingest, "ingest", "SnapshotSafe", 16);
                }

                Type social = AccessTools.TypeByName("CommonSense.JobDriver_SocialRelaxPatches");
                if (social != null)
                {
                    AddAllNonTrivial(social, "socialRelax", "SnapshotSafe", 16);
                }

                Type meditate = AccessTools.TypeByName("CommonSense.JobDriver_MeditatePatches");
                if (meditate != null)
                {
                    AddAllNonTrivial(meditate, "meditate", "SnapshotSafe", 16);
                }

                Type random = AccessTools.TypeByName("CommonSense.RandomIngredients");
                if (random != null)
                {
                    AddAllNonTrivial(random, "randomIngredients", "SnapshotSafe", 16);
                }

                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    H.Patch(report, postfix: new HarmonyMethod(typeof(Stage4DCommonSenseProfiler), nameof(ReportPostfix)) { priority = Priority.Last });

                installed = Ordered.Count > 0;
                Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense Performance Profiler installed: targets=" + Ordered.Count + ". Low-overhead sampled/inclusive telemetry only; Common Sense behavior is unchanged. Parallel classes: MainThreadOnly/SnapshotSafe/WorkerCandidate. No worker task is waited on by the main thread.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] Stage 4D Common Sense profiler failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Add(Type type, string methodName, string module, string parallelClass, int sampleEvery)
        {
            if (type == null) return;
            MethodInfo m = AccessTools.Method(type, methodName);
            if (m != null) Register(m, module + "." + methodName, module, parallelClass, sampleEvery);
        }

        private static void AddAll(Type type, string methodName, string module, string parallelClass, int sampleEvery)
        {
            if (type == null) return;
            MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.Name == methodName && m.DeclaringType == type)
                    Register(m, module + "." + methodName + (methods.Length > 1 ? "/" + m.GetParameters().Length : ""), module, parallelClass, sampleEvery);
            }
        }

        private static void AddAllNonTrivial(Type type, string module, string parallelClass, int sampleEvery)
        {
            MethodInfo[] methods = type.GetMethods(BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo m = methods[i];
                if (m.IsAbstract || m.IsGenericMethodDefinition || m.IsSpecialName) continue;
                string n = m.Name;
                if (n == "Prepare" || n == "TargetMethod" || n == "Transpiler" || n == "Finalizer") continue;
                Register(m, module + "." + n, module, parallelClass, sampleEvery);
            }
        }

        private static void Register(MethodInfo method, string name, string module, string parallelClass, int sampleEvery)
        {
            if (method == null || Targets.ContainsKey(method)) return;
            Target t = new Target
            {
                Name = name,
                Module = module,
                ParallelClass = parallelClass,
                Method = method,
                SampleEvery = Math.Max(1, sampleEvery)
            };
            try
            {
                H.Patch(method,
                    prefix: new HarmonyMethod(typeof(Stage4DCommonSenseProfiler), nameof(Prefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(Stage4DCommonSenseProfiler), nameof(Postfix)) { priority = Priority.Last });
                Targets[method] = t;
                Ordered.Add(t);
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        public static void Prefix(MethodBase __originalMethod, out State __state)
        {
            __state = default(State);
            try
            {
                Target t;
                if (__originalMethod == null || !Targets.TryGetValue(__originalMethod, out t)) return;
                long seen = Interlocked.Increment(ref t.Seen);
                if (t.SampleEvery > 1 && (seen % t.SampleEvery) != 0) return;
                Interlocked.Increment(ref t.Samples);
                __state.Target = t;
                __state.Start = Stopwatch.GetTimestamp();
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        public static void Postfix(State __state)
        {
            Target t = __state.Target;
            if (t == null || __state.Start == 0) return;
            try
            {
                long elapsed = Stopwatch.GetTimestamp() - __state.Start;
                Interlocked.Add(ref t.TotalTicks, elapsed);
                long oldMax;
                while (elapsed > (oldMax = Interlocked.Read(ref t.MaxTicks)))
                    if (Interlocked.CompareExchange(ref t.MaxTicks, elapsed, oldMax) == oldMax) break;
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        private static bool ReadSetting(string name, bool fallback)
        {
            try
            {
                FieldInfo f = settingsType == null ? null : AccessTools.Field(settingsType, name);
                return f != null && f.FieldType == typeof(bool) ? (bool)f.GetValue(null) : fallback;
            }
            catch { return fallback; }
        }

        public static void ReportPostfix()
        {
            if (!installed) return;
            try
            {
                List<Target> copy;
                lock (Gate) copy = new List<Target>(Ordered);
                copy.Sort(delegate(Target a, Target b) { return Interlocked.Read(ref b.TotalTicks).CompareTo(Interlocked.Read(ref a.TotalTicks)); });

                Log.Message("[RimMT] RC2-T2 Stage 4D Common Sense report: targets=" + copy.Count +
                    ", failures=" + Interlocked.Read(ref failures) +
                    ", settings(cleanBefore/advClean/haulAll/spoilIng/spoilMeals/safeWander/politeWander)=" +
                    ReadSetting("clean_before_work", false) + "/" + ReadSetting("adv_cleaning", false) + "/" + ReadSetting("adv_haul_all_ings", false) + "/" +
                    ReadSetting("prefer_spoiling_ingredients", false) + "/" + ReadSetting("prefer_spoiling_meals", false) + "/" +
                    ReadSetting("safe_wander", false) + "/" + ReadSetting("polite_wander", false) +
                    ". Timings are sampled and inclusive; multiply sampled totals only for ranking, not exact accounting.");

                int shown = Math.Min(16, copy.Count);
                for (int i = 0; i < shown; i++)
                {
                    Target t = copy[i];
                    long seen = Interlocked.Read(ref t.Seen);
                    long samples = Interlocked.Read(ref t.Samples);
                    long ticks = Interlocked.Read(ref t.TotalTicks);
                    long max = Interlocked.Read(ref t.MaxTicks);
                    double ms = ticks * ToMs;
                    double avgUs = samples == 0 ? 0.0 : ms * 1000.0 / samples;
                    double estMs = ms * t.SampleEvery;
                    Log.Message("[RimMT]   CS#" + (i + 1) + " " + t.Name +
                        ": class=" + t.ParallelClass +
                        ", seen/sampled=" + seen + "/" + samples +
                        ", sampleEvery=" + t.SampleEvery +
                        ", sampledMs=" + ms.ToString("F2") +
                        ", estMs=" + estMs.ToString("F2") +
                        ", avgUs=" + avgUs.ToString("F1") +
                        ", maxMs=" + (max * ToMs).ToString("F2") + ".");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] Stage 4D report failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }
    }
}
