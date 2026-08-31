using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    [StaticConstructorOnStartup]
    internal static class LargeSetTailRescue
    {
        private const string HarmonyId = "allen.rimmt";
        private const int MinSourceCount = 128;
        private const int StageSize = 32;
        private const int MaxWindowSize = 96;
        private const int MinReachableForExtension = 8;
        private const int ValidatorRejectPercentForExtension = 75;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        [ThreadStatic] private static int jobScopeDepth;
        [ThreadStatic] private static Thing[] windowThings;
        [ThreadStatic] private static int[] windowDistSq;
        [ThreadStatic] private static float[] windowPriority;

        private static bool installed;
        private static int patchedGenClosestMethods;
        private static long largeSetCalls;
        private static long prioritizedCalls;
        private static long rescuedCalls;
        private static long rescuedStage32;
        private static long rescuedStage64;
        private static long rescuedStage96;
        private static long extendedTo64;
        private static long extendedTo96;
        private static long earlyFallbackReachHeavy;
        private static long fallbackCalls;
        private static long unsafePriorityFallback;
        private static long sourceItemsSeen;
        private static long priorityCalls;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long failures;

        static LargeSetTailRescue() { LongEventHandler.ExecuteWhenFinished(Install); }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                MethodInfo job = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (job != null)
                {
                    Harmony.Patch(job,
                        prefix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(JobScopePrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(JobScopeFinalizer)) { priority = Priority.Last });
                }

                foreach (MethodInfo m in typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (m == null || (m.Name != "ClosestThing_Global_Reachable" && m.Name != "ClosestThingReachable") || m.ReturnType != typeof(Thing)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    bool hasMap = false, hasRoot = false, hasEnumerable = false;
                    foreach (ParameterInfo p in ps)
                    {
                        Type pt = p.ParameterType;
                        if (pt == typeof(Map)) hasMap = true;
                        else if (pt == typeof(IntVec3)) hasRoot = true;
                        else if (typeof(IEnumerable<Thing>).IsAssignableFrom(pt)) hasEnumerable = true;
                    }
                    if (!hasMap || !hasRoot || !hasEnumerable) continue;
                    Harmony.Patch(m, prefix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(GenClosestPrefix)) { priority = Priority.First });
                    patchedGenClosestMethods++;
                }
                HookReport();
                Log.Message("[RimMT] RC2-T2 adaptive large-set tail rescue installed: minSource=" + MinSourceCount + ", stages=32/64/96, patched=" + patchedGenClosestMethods + ". Stage extension requires validator-heavy rejection; reachability-heavy misses fail closed early to Vanilla.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 adaptive large-set tail rescue install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobScopePrefix() { jobScopeDepth++; }
        public static Exception JobScopeFinalizer(Exception __exception) { if (jobScopeDepth > 0) jobScopeDepth--; return __exception; }

        public static bool GenClosestPrefix(MethodBase __originalMethod, object[] __args, ref Thing __result)
        {
            if (jobScopeDepth <= 0 || __originalMethod == null || __args == null) return true;
            try
            {
                ParameterInfo[] ps = __originalMethod.GetParameters();
                Map map = GetArg<Map>(ps, __args, "map");
                IntVec3 root = GetArg<IntVec3>(ps, __args, "root", "center");
                IEnumerable<Thing> searchSet = GetEnumerable(ps, __args, __originalMethod.Name == "ClosestThingReachable" ? "customGlobalSearchSet" : "searchSet");
                if (map == null || searchSet == null) return true;
                if (__originalMethod.Name == "ClosestThingReachable" && !GetBoolArg(ps, __args, "forceGlobalSearch", GetBoolArg(ps, __args, "forceAllowGlobalSearch", false))) return true;
                if (GetBoolArg(ps, __args, "canLookInHaulableSources", false) || GetBoolArg(ps, __args, "lookInHaulSources", false)) return true;

                ICollection<Thing> collection = searchSet as ICollection<Thing>;
                if (collection == null || collection.Count < MinSourceCount) return true;

                Func<Thing, float> priorityGetter = GetDelegateArg<Func<Thing, float>>(ps, __args, "priorityGetter");
                bool prioritized = priorityGetter != null;
                if (prioritized && !IsSafeWorkScannerPriority(priorityGetter))
                {
                    Interlocked.Increment(ref unsafePriorityFallback);
                    return true;
                }

                Interlocked.Increment(ref largeSetCalls);
                if (prioritized) Interlocked.Increment(ref prioritizedCalls);

                PathEndMode peMode = GetArg<PathEndMode>(ps, __args, "peMode");
                TraverseParms traverseParms = GetArg<TraverseParms>(ps, __args, "traverseParams", "traverseParms");
                float maxDistance = GetFloatArg(ps, __args, "maxDistance", 9999f);
                Predicate<Thing> validator = GetDelegateArg<Predicate<Thing>>(ps, __args, "validator");
                EnsureWindow();
                int count = 0;
                float maxDistanceSq = maxDistance >= 99999f ? float.MaxValue : maxDistance * maxDistance;

                foreach (Thing t in searchSet)
                {
                    Interlocked.Increment(ref sourceItemsSeen);
                    if (t == null || !t.Spawned || t.Map != map) continue;
                    int distSq = (t.PositionHeld - root).LengthHorizontalSquared;
                    if ((float)distSq > maxDistanceSq) continue;
                    float prio = 0f;
                    if (prioritized)
                    {
                        Interlocked.Increment(ref priorityCalls);
                        prio = priorityGetter(t);
                    }
                    InsertCandidate(t, distSq, prio, prioritized, ref count);
                }

                int stageEnd = Math.Min(StageSize, count);
                int stageStart = 0;
                while (stageStart < stageEnd)
                {
                    int stageReachable = 0;
                    int stageValidatorRejected = 0;
                    int stageReachRejected = 0;

                    for (int i = stageStart; i < stageEnd; i++)
                    {
                        Thing t = windowThings[i];
                        if (t == null) continue;
                        Interlocked.Increment(ref reachChecks);
                        if (!map.reachability.CanReach(root, t.SpawnedParentOrMe, peMode, traverseParms))
                        {
                            stageReachRejected++;
                            Interlocked.Increment(ref reachRejected);
                            continue;
                        }

                        stageReachable++;
                        if (validator != null)
                        {
                            Interlocked.Increment(ref validatorCalls);
                            if (!validator(t))
                            {
                                stageValidatorRejected++;
                                Interlocked.Increment(ref validatorRejected);
                                continue;
                            }
                        }

                        __result = t;
                        Interlocked.Increment(ref rescuedCalls);
                        if (stageEnd <= 32) Interlocked.Increment(ref rescuedStage32);
                        else if (stageEnd <= 64) Interlocked.Increment(ref rescuedStage64);
                        else Interlocked.Increment(ref rescuedStage96);
                        ClearWindow(count);
                        return false;
                    }

                    if (stageEnd >= count || stageEnd >= MaxWindowSize) break;

                    bool validatorHeavy = validator != null &&
                        stageReachable >= MinReachableForExtension &&
                        stageValidatorRejected * 100 >= stageReachable * ValidatorRejectPercentForExtension;

                    if (!validatorHeavy)
                    {
                        if (stageReachRejected > stageValidatorRejected)
                            Interlocked.Increment(ref earlyFallbackReachHeavy);
                        break;
                    }

                    stageStart = stageEnd;
                    stageEnd = Math.Min(stageEnd + StageSize, count);
                    if (stageEnd <= 64) Interlocked.Increment(ref extendedTo64);
                    else Interlocked.Increment(ref extendedTo96);
                }

                Interlocked.Increment(ref fallbackCalls);
                ClearWindow(count);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 4) Log.Warning("[RimMT] RC2-T2 adaptive large-set rescue failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool IsSafeWorkScannerPriority(Delegate d)
        {
            if (d == null || d.Target == null) return false;
            Type t = d.Target.GetType();
            foreach (FieldInfo f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(WorkGiver_Scanner).IsAssignableFrom(f.FieldType)) continue;
                try { if (f.GetValue(d.Target) is WorkGiver_Scanner) return true; } catch { }
            }
            return false;
        }

        private static void EnsureWindow()
        {
            if (windowThings != null && windowThings.Length == MaxWindowSize) return;
            windowThings = new Thing[MaxWindowSize];
            windowDistSq = new int[MaxWindowSize];
            windowPriority = new float[MaxWindowSize];
        }

        private static void InsertCandidate(Thing thing, int distSq, float prio, bool prioritized, ref int count)
        {
            int insert = Math.Min(count, MaxWindowSize);
            while (insert > 0 && BetterThan(prio, distSq, windowPriority[insert - 1], windowDistSq[insert - 1], prioritized))
            {
                if (insert < MaxWindowSize)
                {
                    windowThings[insert] = windowThings[insert - 1];
                    windowDistSq[insert] = windowDistSq[insert - 1];
                    windowPriority[insert] = windowPriority[insert - 1];
                }
                insert--;
            }
            if (insert < MaxWindowSize)
            {
                windowThings[insert] = thing;
                windowDistSq[insert] = distSq;
                windowPriority[insert] = prio;
                if (count < MaxWindowSize) count++;
            }
        }

        private static bool BetterThan(float p1, int d1, float p2, int d2, bool prioritized)
        {
            if (!prioritized) return d1 < d2;
            if (p1 > p2) return true;
            if (p1 < p2) return false;
            return d1 < d2;
        }

        private static void ClearWindow(int count)
        {
            for (int i = 0; i < count; i++) windowThings[i] = null;
        }

        private static T GetArg<T>(ParameterInfo[] ps, object[] args, params string[] names)
        {
            foreach (string name in names) for (int i = 0; i < ps.Length && i < args.Length; i++) if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is T) return (T)args[i];
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (args[i] is T) return (T)args[i];
            return default(T);
        }

        private static IEnumerable<Thing> GetEnumerable(ParameterInfo[] ps, object[] args, string preferredName)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase)) { IEnumerable<Thing> e = args[i] as IEnumerable<Thing>; if (e != null) return e; }
            for (int i = 0; i < ps.Length && i < args.Length; i++) { IEnumerable<Thing> e = args[i] as IEnumerable<Thing>; if (e != null) return e; }
            return null;
        }

        private static T GetDelegateArg<T>(ParameterInfo[] ps, object[] args, string preferredName) where T : class
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase)) return args[i] as T;
            for (int i = 0; i < args.Length; i++) { T d = args[i] as T; if (d != null) return d; }
            return null;
        }

        private static bool GetBoolArg(ParameterInfo[] ps, object[] args, string name, bool fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is bool) return (bool)args[i];
            return fallback;
        }

        private static float GetFloatArg(ParameterInfo[] ps, object[] args, string name, float fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is float) return (float)args[i];
            return fallback;
        }

        private static void HookReport()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (report != null) Harmony.Patch(report, postfix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(ReportPostfix)) { priority = Priority.Last });
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 adaptive tail report: patched=" + patchedGenClosestMethods +
                ", minSource=" + MinSourceCount + ", stages=32/64/96" +
                ", large/prioritized=" + Interlocked.Read(ref largeSetCalls) + "/" + Interlocked.Read(ref prioritizedCalls) +
                ", rescued32/64/96=" + Interlocked.Read(ref rescuedStage32) + "/" + Interlocked.Read(ref rescuedStage64) + "/" + Interlocked.Read(ref rescuedStage96) +
                ", rescuedTotal/fallback=" + Interlocked.Read(ref rescuedCalls) + "/" + Interlocked.Read(ref fallbackCalls) +
                ", extend64/96=" + Interlocked.Read(ref extendedTo64) + "/" + Interlocked.Read(ref extendedTo96) +
                ", earlyFallbackReachHeavy=" + Interlocked.Read(ref earlyFallbackReachHeavy) +
                ", unsafePriorityFallback=" + Interlocked.Read(ref unsafePriorityFallback) +
                ", sourceSeen=" + Interlocked.Read(ref sourceItemsSeen) + ", priorityCalls=" + Interlocked.Read(ref priorityCalls) +
                ", validatorCalls/rejected=" + Interlocked.Read(ref validatorCalls) + "/" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks/rejected=" + Interlocked.Read(ref reachChecks) + "/" + Interlocked.Read(ref reachRejected) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
