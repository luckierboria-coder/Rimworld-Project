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
        private const int WindowSize = 32;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        [ThreadStatic] private static int jobScopeDepth;
        [ThreadStatic] private static Thing[] windowThings;
        [ThreadStatic] private static int[] windowDistSq;
        [ThreadStatic] private static float[] windowPriority;

        private static bool installed;
        private static int patchedGenClosestMethods;
        private static long largeSetCalls, prioritizedCalls, safeValidatorFirstCalls, conservativeCalls;
        private static long rescuedValidatorFirst, rescuedConservative, fallbackCalls, unsafePriorityFallback;
        private static long sourceItemsSeen, priorityCalls, validatorCalls, validatorRejected;
        private static long reachChecks, reachRejected, reachAvoidedByValidator, failures;

        static LargeSetTailRescue() { LongEventHandler.ExecuteWhenFinished(Install); }

        private static void Install()
        {
            if (installed) return;
            installed = true;
            try
            {
                MethodInfo job = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (job != null)
                    Harmony.Patch(job,
                        prefix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(JobScopePrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(JobScopeFinalizer)) { priority = Priority.Last });

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
                Log.Message("[RimMT] RC2-T2 validator-first tail rescue installed: minSource=" + MinSourceCount + ", window=" + WindowSize + ", patched=" + patchedGenClosestMethods + ". Safe Vanilla WorkGiver validators run before CanReach; unknown/mod validators preserve CanReach->validator order and fail closed.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 validator-first tail rescue install failed: " + ex.GetType().Name + ": " + ex.Message);
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

                Predicate<Thing> validator = GetDelegateArg<Predicate<Thing>>(ps, __args, "validator");
                bool validatorFirst = validator != null && IsSafeVanillaWorkValidator(validator);
                Interlocked.Increment(ref largeSetCalls);
                if (prioritized) Interlocked.Increment(ref prioritizedCalls);
                if (validatorFirst) Interlocked.Increment(ref safeValidatorFirstCalls); else Interlocked.Increment(ref conservativeCalls);

                PathEndMode peMode = GetArg<PathEndMode>(ps, __args, "peMode");
                TraverseParms traverseParms = GetArg<TraverseParms>(ps, __args, "traverseParams", "traverseParms");
                float maxDistance = GetFloatArg(ps, __args, "maxDistance", 9999f);
                float maxDistanceSq = maxDistance >= 99999f ? float.MaxValue : maxDistance * maxDistance;

                EnsureWindow();
                int count = 0;
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

                for (int i = 0; i < count; i++)
                {
                    Thing t = windowThings[i];
                    if (t == null) continue;
                    if (validatorFirst)
                    {
                        Interlocked.Increment(ref validatorCalls);
                        if (!validator(t))
                        {
                            Interlocked.Increment(ref validatorRejected);
                            Interlocked.Increment(ref reachAvoidedByValidator);
                            continue;
                        }
                        Interlocked.Increment(ref reachChecks);
                        if (!map.reachability.CanReach(root, t.SpawnedParentOrMe, peMode, traverseParms))
                        {
                            Interlocked.Increment(ref reachRejected);
                            continue;
                        }
                        __result = t;
                        Interlocked.Increment(ref rescuedValidatorFirst);
                        ClearWindow(count);
                        return false;
                    }

                    Interlocked.Increment(ref reachChecks);
                    if (!map.reachability.CanReach(root, t.SpawnedParentOrMe, peMode, traverseParms))
                    {
                        Interlocked.Increment(ref reachRejected);
                        continue;
                    }
                    if (validator != null)
                    {
                        Interlocked.Increment(ref validatorCalls);
                        if (!validator(t))
                        {
                            Interlocked.Increment(ref validatorRejected);
                            continue;
                        }
                    }
                    __result = t;
                    Interlocked.Increment(ref rescuedConservative);
                    ClearWindow(count);
                    return false;
                }

                Interlocked.Increment(ref fallbackCalls);
                ClearWindow(count);
                return true;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 4)
                    Log.Warning("[RimMT] RC2-T2 validator-first rescue failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static bool IsSafeVanillaWorkValidator(Delegate d)
        {
            if (d == null || d.Method == null || d.Method.DeclaringType == null) return false;
            try
            {
                if (d.Method.DeclaringType.Assembly != typeof(WorkGiver).Assembly) return false;
                string typeName = d.Method.DeclaringType.FullName ?? string.Empty;
                return typeName.IndexOf("WorkGiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       typeName.IndexOf("JobGiver", StringComparison.OrdinalIgnoreCase) >= 0 ||
                       ClosureContainsWorkContext(d.Target);
            }
            catch { return false; }
        }

        private static bool ClosureContainsWorkContext(object target)
        {
            if (target == null) return false;
            foreach (FieldInfo f in target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                Type ft = f.FieldType;
                if (typeof(WorkGiver).IsAssignableFrom(ft) || ft == typeof(Pawn)) return true;
                string n = ft.FullName ?? string.Empty;
                if (n.IndexOf("JobGiver", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool IsSafeWorkScannerPriority(Delegate d)
        {
            if (d == null || d.Target == null) return false;
            foreach (FieldInfo f in d.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (!typeof(WorkGiver_Scanner).IsAssignableFrom(f.FieldType)) continue;
                try { if (f.GetValue(d.Target) is WorkGiver_Scanner) return true; } catch { }
            }
            return false;
        }

        private static void EnsureWindow()
        {
            if (windowThings != null && windowThings.Length == WindowSize) return;
            windowThings = new Thing[WindowSize];
            windowDistSq = new int[WindowSize];
            windowPriority = new float[WindowSize];
        }

        private static void InsertCandidate(Thing thing, int distSq, float prio, bool prioritized, ref int count)
        {
            int insert = Math.Min(count, WindowSize);
            while (insert > 0 && BetterThan(prio, distSq, windowPriority[insert - 1], windowDistSq[insert - 1], prioritized))
            {
                if (insert < WindowSize)
                {
                    windowThings[insert] = windowThings[insert - 1];
                    windowDistSq[insert] = windowDistSq[insert - 1];
                    windowPriority[insert] = windowPriority[insert - 1];
                }
                insert--;
            }
            if (insert < WindowSize)
            {
                windowThings[insert] = thing;
                windowDistSq[insert] = distSq;
                windowPriority[insert] = prio;
                if (count < WindowSize) count++;
            }
        }

        private static bool BetterThan(float p1, int d1, float p2, int d2, bool prioritized)
        {
            if (!prioritized) return d1 < d2;
            if (p1 != p2) return p1 > p2;
            return d1 < d2;
        }

        private static void ClearWindow(int count) { for (int i = 0; i < count; i++) windowThings[i] = null; }

        private static T GetArg<T>(ParameterInfo[] ps, object[] args, params string[] names)
        {
            foreach (string name in names)
                for (int i = 0; i < ps.Length && i < args.Length; i++)
                    if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is T) return (T)args[i];
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (args[i] is T) return (T)args[i];
            return default(T);
        }

        private static IEnumerable<Thing> GetEnumerable(ParameterInfo[] ps, object[] args, string preferredName)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
                if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase) && args[i] is IEnumerable<Thing>) return (IEnumerable<Thing>)args[i];
            for (int i = 0; i < ps.Length && i < args.Length; i++) if (args[i] is IEnumerable<Thing>) return (IEnumerable<Thing>)args[i];
            return null;
        }

        private static T GetDelegateArg<T>(ParameterInfo[] ps, object[] args, string preferredName) where T : class
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
                if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase)) return args[i] as T;
            for (int i = 0; i < args.Length; i++) if (args[i] is T) return args[i] as T;
            return null;
        }

        private static bool GetBoolArg(ParameterInfo[] ps, object[] args, string name, bool fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
                if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is bool) return (bool)args[i];
            return fallback;
        }

        private static float GetFloatArg(ParameterInfo[] ps, object[] args, string name, float fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
                if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is float) return (float)args[i];
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
            Log.Message("[RimMT] RC2-T2 validator-first tail report: patched=" + patchedGenClosestMethods +
                ", minSource=" + MinSourceCount + ", window=" + WindowSize +
                ", large/prioritized=" + Interlocked.Read(ref largeSetCalls) + "/" + Interlocked.Read(ref prioritizedCalls) +
                ", safeValidatorFirst/conservative=" + Interlocked.Read(ref safeValidatorFirstCalls) + "/" + Interlocked.Read(ref conservativeCalls) +
                ", rescuedVF/conservative=" + Interlocked.Read(ref rescuedValidatorFirst) + "/" + Interlocked.Read(ref rescuedConservative) +
                ", fallback=" + Interlocked.Read(ref fallbackCalls) +
                ", unsafePriorityFallback=" + Interlocked.Read(ref unsafePriorityFallback) +
                ", sourceSeen=" + Interlocked.Read(ref sourceItemsSeen) + ", priorityCalls=" + Interlocked.Read(ref priorityCalls) +
                ", validatorCalls/rejected=" + Interlocked.Read(ref validatorCalls) + "/" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks/rejected=" + Interlocked.Read(ref reachChecks) + "/" + Interlocked.Read(ref reachRejected) +
                ", reachAvoidedByValidator=" + Interlocked.Read(ref reachAvoidedByValidator) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
