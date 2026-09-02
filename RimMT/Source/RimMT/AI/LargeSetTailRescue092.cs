using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// RC2-T2 Stage3 consolidated for Unified Lean. S5.1 now owns validated <=127 known sets at
    /// the 16ms threshold, so this layer only rescues >=128 materialized sets. It examines a
    /// bounded best-32 window and falls back to Vanilla when the window cannot prove the result.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class LargeSetTailRescue092
    {
        private const int LargeSourceCount = 128;
        private const int MaxSourceCount = 16384;
        private const int WindowSize = 32;

        [ThreadStatic] private static Thing[] windowThings;
        [ThreadStatic] private static int[] windowDistSq;
        [ThreadStatic] private static float[] windowPriority;
        private static int failureLogs;

        static LargeSetTailRescue092()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                int patched = 0;
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.ReturnType != typeof(Thing)) continue;
                    if (method.Name != "ClosestThing_Global_Reachable" && method.Name != "ClosestThingReachable") continue;
                    ParameterInfo[] ps = method.GetParameters();
                    bool map = false, root = false, enumerable = false;
                    for (int p = 0; p < ps.Length; p++)
                    {
                        Type t = ps[p].ParameterType;
                        if (t == typeof(Map)) map = true;
                        else if (t == typeof(IntVec3)) root = true;
                        else if (typeof(IEnumerable<Thing>).IsAssignableFrom(t)) enumerable = true;
                    }
                    if (!map || !root || !enumerable) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(LargeSetTailRescue092), nameof(Prefix)) { priority = Priority.First + 250 });
                    patched++;
                }
                Log.Message("[RimMT] Unified RC2 Stage3 large-set rescue active on " + patched + " GenClosest overload(s); >=128 only, best-32 bounded proof, Vanilla fallback on uncertainty.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Unified Stage3 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool Prefix(MethodBase __originalMethod, object[] __args, ref Thing __result)
        {
            if (!JobGiverGlobalNearest04181.InJobGiverScope || __originalMethod == null || __args == null ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            try
            {
                ParameterInfo[] ps = __originalMethod.GetParameters();
                Map map = GetArg<Map>(ps, __args, "map");
                IntVec3 root = GetArg<IntVec3>(ps, __args, "root", "center");
                IEnumerable<Thing> searchSet = GetEnumerable(ps, __args,
                    __originalMethod.Name == "ClosestThingReachable" ? "customGlobalSearchSet" : "searchSet");
                if (map == null || searchSet == null) return true;

                if (__originalMethod.Name == "ClosestThingReachable" &&
                    !GetBoolArg(ps, __args, "forceGlobalSearch", GetBoolArg(ps, __args, "forceAllowGlobalSearch", false)))
                    return true;
                if (GetBoolArg(ps, __args, "canLookInHaulableSources", false) || GetBoolArg(ps, __args, "lookInHaulSources", false))
                    return true;

                ICollection<Thing> collection = searchSet as ICollection<Thing>;
                if (collection == null || collection.Count < LargeSourceCount || collection.Count > MaxSourceCount) return true;

                Predicate<Thing> validator = GetDelegateArg<Predicate<Thing>>(ps, __args, "validator");
                bool validatorFirst = validator != null && IsSafeVanillaWorkValidator(validator);
                Func<Thing, float> priorityGetter = GetDelegateArg<Func<Thing, float>>(ps, __args, "priorityGetter");
                bool prioritized = priorityGetter != null;
                if (prioritized && !IsSafeWorkScannerPriority(priorityGetter)) return true;

                PathEndMode peMode = GetArg<PathEndMode>(ps, __args, "peMode");
                TraverseParms traverseParms = GetArg<TraverseParms>(ps, __args, "traverseParams", "traverseParms");
                float maxDistance = GetFloatArg(ps, __args, "maxDistance", 9999f);
                float maxDistanceSq = maxDistance >= 99999f ? float.MaxValue : maxDistance * maxDistance;

                EnsureWindow();
                int count = 0;
                foreach (Thing thing in searchSet)
                {
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    int distSq = (thing.PositionHeld - root).LengthHorizontalSquared;
                    if ((float)distSq > maxDistanceSq) continue;
                    float priority = prioritized ? priorityGetter(thing) : 0f;
                    InsertCandidate(thing, distSq, priority, prioritized, ref count);
                }

                for (int i = 0; i < count; i++)
                {
                    Thing thing = windowThings[i];
                    if (thing == null) continue;

                    if (validatorFirst)
                    {
                        if (!validator(thing)) continue;
                        if (!map.reachability.CanReach(root, thing.SpawnedParentOrMe, peMode, traverseParms)) continue;
                    }
                    else
                    {
                        if (!map.reachability.CanReach(root, thing.SpawnedParentOrMe, peMode, traverseParms)) continue;
                        if (validator != null && !validator(thing)) continue;
                    }

                    __result = thing;
                    ClearWindow(count);
                    return false;
                }

                // The best-32 window did not prove a result. Vanilla must inspect the rest.
                ClearWindow(count);
                return true;
            }
            catch (Exception ex)
            {
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] Unified Stage3 failed closed for one call: " + ex.GetType().Name + ": " + ex.Message);
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
            FieldInfo[] fields = target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                Type t = fields[i].FieldType;
                if (typeof(WorkGiver).IsAssignableFrom(t) || t == typeof(Pawn)) return true;
                string n = t.FullName ?? string.Empty;
                if (n.IndexOf("JobGiver", StringComparison.OrdinalIgnoreCase) >= 0) return true;
            }
            return false;
        }

        private static bool IsSafeWorkScannerPriority(Delegate d)
        {
            if (d == null || d.Target == null) return false;
            FieldInfo[] fields = d.Target.GetType().GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < fields.Length; i++)
            {
                if (!typeof(WorkGiver_Scanner).IsAssignableFrom(fields[i].FieldType)) continue;
                try { if (fields[i].GetValue(d.Target) is WorkGiver_Scanner) return true; } catch { }
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

        private static void InsertCandidate(Thing thing, int distSq, float priority, bool prioritized, ref int count)
        {
            int insert = Math.Min(count, WindowSize);
            while (insert > 0 && BetterThan(priority, distSq, windowPriority[insert - 1], windowDistSq[insert - 1], prioritized))
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
                windowPriority[insert] = priority;
                if (count < WindowSize) count++;
            }
        }

        private static bool BetterThan(float p1, int d1, float p2, int d2, bool prioritized)
        {
            if (!prioritized) return d1 < d2;
            if (p1 != p2) return p1 > p2;
            return d1 < d2;
        }

        private static void ClearWindow(int count)
        {
            for (int i = 0; i < count; i++) windowThings[i] = null;
        }

        private static T GetArg<T>(ParameterInfo[] ps, object[] args, params string[] names)
        {
            for (int n = 0; n < names.Length; n++)
                for (int i = 0; i < ps.Length && i < args.Length; i++)
                    if (string.Equals(ps[i].Name, names[n], StringComparison.OrdinalIgnoreCase) && args[i] is T) return (T)args[i];
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
    }
}
