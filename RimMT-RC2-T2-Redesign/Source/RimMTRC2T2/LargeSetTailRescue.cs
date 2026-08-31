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

        private static bool installed;
        private static int patchedGenClosestMethods;
        private static long largeSetCalls;
        private static long rescuedCalls;
        private static long fallbackCalls;
        private static long skippedPriorityCalls;
        private static long sourceItemsSeen;
        private static long validatorCalls;
        private static long reachChecks;
        private static long failures;

        static LargeSetTailRescue()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

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

                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo m = methods[i];
                    if (m == null) continue;
                    if (m.Name != "ClosestThing_Global_Reachable" && m.Name != "ClosestThingReachable") continue;
                    if (m.ReturnType != typeof(Thing)) continue;
                    ParameterInfo[] ps = m.GetParameters();
                    bool hasMap = false;
                    bool hasRoot = false;
                    bool hasEnumerable = false;
                    for (int p = 0; p < ps.Length; p++)
                    {
                        Type pt = ps[p].ParameterType;
                        if (pt == typeof(Map)) hasMap = true;
                        else if (pt == typeof(IntVec3)) hasRoot = true;
                        else if (typeof(IEnumerable<Thing>).IsAssignableFrom(pt)) hasEnumerable = true;
                    }
                    if (!hasMap || !hasRoot || !hasEnumerable) continue;
                    Harmony.Patch(m, prefix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(GenClosestPrefix)) { priority = Priority.First });
                    patchedGenClosestMethods++;
                }

                HookReport();
                Log.Message("[RimMT] RC2-T2 large-set tail rescue installed: minSource=" + MinSourceCount + ", nearestWindow=" + WindowSize + ", patchedGenClosest=" + patchedGenClosestMethods + ". Only JobGiver_Work scope is eligible; no Stopwatch or time-threshold retuning is used.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 large-set tail rescue install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobScopePrefix()
        {
            jobScopeDepth++;
        }

        public static Exception JobScopeFinalizer(Exception __exception)
        {
            if (jobScopeDepth > 0) jobScopeDepth--;
            return __exception;
        }

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

                if (__originalMethod.Name == "ClosestThingReachable")
                {
                    bool forceGlobal = GetBoolArg(ps, __args, "forceGlobalSearch", false);
                    if (!forceGlobal) return true;
                }

                Func<Thing, float> priorityGetter = GetDelegateArg<Func<Thing, float>>(ps, __args, "priorityGetter");
                if (priorityGetter != null)
                {
                    Interlocked.Increment(ref skippedPriorityCalls);
                    return true;
                }

                ICollection<Thing> collection = searchSet as ICollection<Thing>;
                if (collection == null || collection.Count < MinSourceCount) return true;
                Interlocked.Increment(ref largeSetCalls);

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
                    int distSq = (t.Position - root).LengthHorizontalSquared;
                    if ((float)distSq > maxDistanceSq) continue;
                    InsertNearest(t, distSq, ref count);
                }

                for (int i = 0; i < count; i++)
                {
                    Thing t = windowThings[i];
                    if (t == null) continue;
                    bool valid = true;
                    if (validator != null)
                    {
                        Interlocked.Increment(ref validatorCalls);
                        valid = validator(t);
                    }
                    if (!valid) continue;

                    Interlocked.Increment(ref reachChecks);
                    if (!map.reachability.CanReach(root, t, peMode, traverseParms)) continue;

                    __result = t;
                    Interlocked.Increment(ref rescuedCalls);
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
                    Log.Warning("[RimMT] RC2-T2 large-set tail rescue failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static void EnsureWindow()
        {
            if (windowThings == null || windowThings.Length != WindowSize)
            {
                windowThings = new Thing[WindowSize];
                windowDistSq = new int[WindowSize];
            }
        }

        private static void InsertNearest(Thing thing, int distSq, ref int count)
        {
            int insert = count;
            if (insert > WindowSize) insert = WindowSize;
            while (insert > 0 && windowDistSq[insert - 1] > distSq)
            {
                if (insert < WindowSize)
                {
                    windowThings[insert] = windowThings[insert - 1];
                    windowDistSq[insert] = windowDistSq[insert - 1];
                }
                insert--;
            }

            if (insert < WindowSize)
            {
                windowThings[insert] = thing;
                windowDistSq[insert] = distSq;
                if (count < WindowSize) count++;
            }
        }

        private static void ClearWindow(int count)
        {
            for (int i = 0; i < count; i++) windowThings[i] = null;
        }

        private static T GetArg<T>(ParameterInfo[] ps, object[] args, params string[] names)
        {
            for (int n = 0; n < names.Length; n++)
            {
                for (int i = 0; i < ps.Length && i < args.Length; i++)
                {
                    if (!string.Equals(ps[i].Name, names[n], StringComparison.OrdinalIgnoreCase)) continue;
                    object v = args[i];
                    if (v is T) return (T)v;
                }
            }
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                object v = args[i];
                if (v is T) return (T)v;
            }
            return default(T);
        }

        private static IEnumerable<Thing> GetEnumerable(ParameterInfo[] ps, object[] args, string preferredName)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase))
                    return args[i] as IEnumerable<Thing>;
            }
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                IEnumerable<Thing> e = args[i] as IEnumerable<Thing>;
                if (e != null) return e;
            }
            return null;
        }

        private static T GetDelegateArg<T>(ParameterInfo[] ps, object[] args, string preferredName) where T : class
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                if (string.Equals(ps[i].Name, preferredName, StringComparison.OrdinalIgnoreCase)) return args[i] as T;
            }
            for (int i = 0; i < args.Length; i++)
            {
                T d = args[i] as T;
                if (d != null) return d;
            }
            return null;
        }

        private static bool GetBoolArg(ParameterInfo[] ps, object[] args, string name, bool fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is bool) return (bool)args[i];
            }
            return fallback;
        }

        private static float GetFloatArg(ParameterInfo[] ps, object[] args, string name, float fallback)
        {
            for (int i = 0; i < ps.Length && i < args.Length; i++)
            {
                if (string.Equals(ps[i].Name, name, StringComparison.OrdinalIgnoreCase) && args[i] is float) return (float)args[i];
            }
            return fallback;
        }

        private static void HookReport()
        {
            Type t = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
            MethodInfo report = t == null ? null : AccessTools.Method(t, "LogRuntimeReport");
            if (report != null)
                Harmony.Patch(report, postfix: new HarmonyMethod(typeof(LargeSetTailRescue), nameof(ReportPostfix)) { priority = Priority.Last });
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 large-set tail report: patched=" + patchedGenClosestMethods +
                ", minSource=" + MinSourceCount +
                ", window=" + WindowSize +
                ", largeCalls=" + Interlocked.Read(ref largeSetCalls) +
                ", rescued/fallback=" + Interlocked.Read(ref rescuedCalls) + "/" + Interlocked.Read(ref fallbackCalls) +
                ", skippedPriority=" + Interlocked.Read(ref skippedPriorityCalls) +
                ", sourceSeen=" + Interlocked.Read(ref sourceItemsSeen) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
