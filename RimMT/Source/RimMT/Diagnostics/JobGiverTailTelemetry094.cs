using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// V0.9.4 lightweight tail attribution for JobGiver_Work search calls.
    /// Every supported GenClosest call inside one synchronous JobGiver_Work package gets only
    /// two Stopwatch timestamps. Reflection/WorkGiver resolution happens only when the call
    /// itself already exceeded 2ms. No per-candidate timing, allocations or realtime logging.
    /// </summary>
    internal static class JobGiverTailTelemetry094
    {
        private const int MaxKeys = 32;
        private const int TopKeys = 8;
        private static readonly long T2 = Math.Max(1L, Stopwatch.Frequency * 2L / 1000L);
        private static readonly long T5 = Math.Max(1L, Stopwatch.Frequency * 5L / 1000L);
        private static readonly long T10 = Math.Max(1L, Stopwatch.Frequency * 10L / 1000L);
        private static readonly long T20 = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);
        private static readonly long T50 = Math.Max(1L, Stopwatch.Frequency * 50L / 1000L);

        private static bool patched;
        private static long timedCalls;
        private static long over2;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long over50;
        private static long unresolved;
        private static long maxTicks;
        private static readonly Dictionary<string, TailStats> Stats = new Dictionary<string, TailStats>();
        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int count = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method)) continue;
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverTailTelemetry094), nameof(Prefix)) { priority = Priority.First + 125 };
                    HarmonyMethod postfix = new HarmonyMethod(typeof(JobGiverTailTelemetry094), nameof(Postfix)) { priority = Priority.Last };
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    count++;
                }
                patched = count > 0;
                Log.Message("[RimMT] V0.9.4 lightweight JobGiver tail buckets installed on " + count + " ClosestThingReachable overload(s); WorkGiver reflection is deferred until a call exceeds 2ms.");
            }
            catch (Exception ex)
            {
                patched = false;
                Log.Warning("[RimMT] V0.9.4 JobGiver tail buckets failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                   p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                   p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                   p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static void Prefix(ref long __state)
        {
            __state = 0L;
            if (!JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;
            __state = Stopwatch.GetTimestamp();
        }

        public static void Postfix(Predicate<Thing> __6, long __state)
        {
            if (__state == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - __state;
            timedCalls++;
            if (elapsed > maxTicks) maxTicks = elapsed;
            if (elapsed < T2) return;

            over2++;
            if (elapsed >= T5) over5++;
            if (elapsed >= T10) over10++;
            if (elapsed >= T20) over20++;
            if (elapsed >= T50) over50++;

            WorkGiver_Scanner scanner = TryResolveScanner(__6);
            if (scanner == null)
            {
                unresolved++;
                return;
            }

            string key = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                ? scanner.GetType().FullName
                : scanner.def.defName;
            if (string.IsNullOrEmpty(key)) key = "<unknown>";

            TailStats stat;
            if (!Stats.TryGetValue(key, out stat))
            {
                if (Stats.Count >= MaxKeys) return;
                stat = new TailStats();
                Stats[key] = stat;
            }
            stat.Over2++;
            if (elapsed >= T5) stat.Over5++;
            if (elapsed >= T10) stat.Over10++;
            if (elapsed >= T20) stat.Over20++;
            if (elapsed >= T50) stat.Over50++;
            if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;
        }

        private static WorkGiver_Scanner TryResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null) return null;
            try
            {
                object target = validator.Target;
                if (target == null) return null;
                Type targetType = target.GetType();
                FieldInfo scannerField;
                if (!ScannerFieldCache.TryGetValue(targetType, out scannerField))
                {
                    scannerField = ResolveScannerField(targetType);
                    ScannerFieldCache[targetType] = scannerField;
                }
                return scannerField == null ? null : scannerField.GetValue(target) as WorkGiver_Scanner;
            }
            catch { return null; }
        }

        private static FieldInfo ResolveScannerField(Type targetType)
        {
            if (targetType == null) return null;
            FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo fallback = null;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType)) return field;
                if (fallback == null && typeof(WorkGiver).IsAssignableFrom(field.FieldType)) fallback = field;
            }
            return fallback;
        }

        internal static string Summary()
        {
            List<KeyValuePair<string, TailStats>> list = new List<KeyValuePair<string, TailStats>>(Stats);
            list.Sort(delegate(KeyValuePair<string, TailStats> a, KeyValuePair<string, TailStats> b)
            {
                int c = b.Value.Over50.CompareTo(a.Value.Over50); if (c != 0) return c;
                c = b.Value.Over20.CompareTo(a.Value.Over20); if (c != 0) return c;
                c = b.Value.Over10.CompareTo(a.Value.Over10); if (c != 0) return c;
                c = b.Value.Over5.CompareTo(a.Value.Over5); if (c != 0) return c;
                c = b.Value.Over2.CompareTo(a.Value.Over2); if (c != 0) return c;
                return b.Value.MaxTicks.CompareTo(a.Value.MaxTicks);
            });

            List<string> parts = new List<string>();
            int take = Math.Min(TopKeys, list.Count);
            for (int i = 0; i < take; i++)
            {
                TailStats s = list[i].Value;
                double maxUs = s.MaxTicks * 1000000.0 / Stopwatch.Frequency;
                parts.Add(list[i].Key + "(>2ms=" + s.Over2 + ", >5ms=" + s.Over5 +
                    ", >10ms=" + s.Over10 + ", >20ms=" + s.Over20 + ", >50ms=" + s.Over50 +
                    ", maxUs=" + maxUs.ToString("F1") + ")");
            }

            double globalMaxUs = maxTicks * 1000000.0 / Stopwatch.Frequency;
            return "JobGiver tail buckets V0.9.4: patched=" + patched +
                   ", timedCalls=" + timedCalls +
                   ", >2ms=" + over2 + ", >5ms=" + over5 + ", >10ms=" + over10 +
                   ", >20ms=" + over20 + ", >50ms=" + over50 +
                   ", unresolved=" + unresolved +
                   ", maxUs=" + globalMaxUs.ToString("F1") +
                   ", top=" + (parts.Count == 0 ? "<none>" : string.Join("; ", parts.ToArray()));
        }

        private sealed class TailStats
        {
            internal long Over2;
            internal long Over5;
            internal long Over10;
            internal long Over20;
            internal long Over50;
            internal long MaxTicks;
        }
    }
}
