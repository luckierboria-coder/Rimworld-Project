using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT2.Diagnostics
{
    // Sampling is deliberately limited to the current thread and one in eight work searches.
    // The nested timers are inclusive; Reachability may be part of GenClosest time.
    internal static class JobSearchCensus
    {
        internal struct SearchCallState
        {
            internal long started;
            internal string giver;
            internal string previousGiver;
        }

        private sealed class GiverAggregate
        {
            internal long globalCalls;
            internal long globalTicks;
            internal long regionCalls;
            internal long regionTicks;
        }

        private sealed class CallerAggregate
        {
            internal long calls;
            internal long ticks;
            internal long maxTicks;
        }

        private struct CallerSample
        {
            internal string caller;
            internal long elapsedTicks;
        }

        private static readonly Harmony harmony = new Harmony("allen.rimmt2.jobSearchCensus");
        private static readonly Dictionary<string, GiverAggregate> giverTotals = new Dictionary<string, GiverAggregate>();
        // Stack walks are opt-in. Keep only the newest 128 callsite observations, not a lifetime-growing map.
        // The window budget resets on each existing 64-sample periodic report.
        private const int UnknownCallerStride = 32;
        private const int UnknownCallerWindowLimit = 16;
        private const int UnknownCallerRecentLimit = 128;
        private static readonly Queue<CallerSample> recentUnknownCallers = new Queue<CallerSample>();
        private static readonly Dictionary<Type, FieldInfo[]> closureFields = new Dictionary<Type, FieldInfo[]>();
        private static int unknownSeen;
        private static int unknownTraces;
        private static int unknownWindowTraces;
        private static bool installed;
        [ThreadStatic] private static bool active;
        [ThreadStatic] private static string currentGiver;
        [ThreadStatic] private static long globalTicks;
        [ThreadStatic] private static long regionTicks;
        [ThreadStatic] private static long reachTicks;
        [ThreadStatic] private static int globalCalls;
        [ThreadStatic] private static int regionCalls;
        [ThreadStatic] private static int reachCalls;

        private static int seen;
        private static long samples;
        private static long totalTicks;
        private static long totalGlobalTicks;
        private static long totalRegionTicks;
        private static long totalReachTicks;
        private static long totalGlobalCalls;
        private static long totalRegionCalls;
        private static long totalReachCalls;
        private static long slowOver10Ms;
        private static long slowOver50Ms;
        private static long slowOver100Ms;
        private static long maxTicks;

        internal static bool Active => active;

        internal static void Configure(bool enabled)
        {
            MainThreadGuard.RequireMainThread("JobSearchCensus.Configure");
            if (enabled == installed) return;
            if (!enabled)
            {
                harmony.UnpatchAll(harmony.Id);
                installed = false;
                return;
            }

            MethodInfo root = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
            MethodInfo region = AccessTools.Method(typeof(GenClosest), "ClosestThingReachable");
            MethodInfo reach = AccessTools.Method(typeof(Reachability), "CanReach",
                new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });
            if (root == null || region == null || reach == null)
            {
                Log.Error("[RimMT 2.0] job-search census target missing; sampler remains off.");
                return;
            }
            try
            {
                harmony.Patch(root,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchRootCensusPatch), "Prefix")),
                    finalizer: new HarmonyMethod(AccessTools.Method(typeof(JobSearchRootCensusPatch), "Finalizer")));
                harmony.Patch(region,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchRegionCensusPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchRegionCensusPatch), "Postfix")));
                harmony.Patch(reach,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchReachCensusPatch), "Prefix")),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchReachCensusPatch), "Postfix")));
                foreach (MethodInfo method in typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.Static))
                    if (method.Name == "ClosestThing_Global" || method.Name == "ClosestThing_Global_Reachable")
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchGlobalCensusPatch), "Prefix")),
                            postfix: new HarmonyMethod(AccessTools.Method(typeof(JobSearchGlobalCensusPatch), "Postfix")));
                installed = true;
            }
            catch (Exception exception)
            {
                harmony.UnpatchAll(harmony.Id);
                Log.Error("[RimMT 2.0] job-search census install failed: " + exception);
            }
        }

        internal static void Begin(ref long state)
        {
            state = 0;
            if (active || RimMT2Mod.Settings?.enableJobSearchCensus != true || (++seen & 7) != 0)
                return;
            active = true;
            globalTicks = regionTicks = reachTicks = 0;
            globalCalls = regionCalls = reachCalls = 0;
            state = Stopwatch.GetTimestamp();
        }

        internal static void End(long state)
        {
            if (state == 0)
                return;
            long elapsed = Stopwatch.GetTimestamp() - state;
            active = false;
            currentGiver = null;
            samples++;
            totalTicks += elapsed;
            totalGlobalTicks += globalTicks;
            totalRegionTicks += regionTicks;
            totalReachTicks += reachTicks;
            totalGlobalCalls += globalCalls;
            totalRegionCalls += regionCalls;
            totalReachCalls += reachCalls;
            long ten = Stopwatch.Frequency / 100;
            if (elapsed >= ten) slowOver10Ms++;
            if (elapsed >= ten * 5) slowOver50Ms++;
            if (elapsed >= ten * 10) slowOver100Ms++;
            if (elapsed > maxTicks) maxTicks = elapsed;
            if ((samples & 63) == 0)
            {
                LogNow("periodic");
                // Period-based budget allows later slow episodes to contribute without unlimited stack walks.
                unknownWindowTraces = 0;
            }
        }

        internal static SearchCallState StartCall(Predicate<Thing> validator)
        {
            if (!active) return default(SearchCallState);
            WorkGiver_Scanner scanner = FindScanner(validator?.Target, 0);
            return new SearchCallState
            {
                started = Stopwatch.GetTimestamp(),
                giver = scanner?.def?.defName ?? currentGiver ?? "unidentified"
            };
        }

        internal static SearchCallState StartNestedCall(Predicate<Thing> validator)
        {
            SearchCallState state = StartCall(validator);
            if (state.started != 0)
            {
                state.previousGiver = currentGiver;
                currentGiver = state.giver;
            }
            return state;
        }

        private static WorkGiver_Scanner FindScanner(object closure, int depth)
        {
            if (closure == null || depth > 2) return null;
            if (closure is WorkGiver_Scanner scanner) return scanner;
            Type type = closure.GetType();
            if (!type.Name.Contains("DisplayClass")) return null;
            if (!closureFields.TryGetValue(type, out FieldInfo[] fields))
            {
                fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                closureFields.Add(type, fields);
            }
            foreach (FieldInfo field in fields)
            {
                if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                    return field.GetValue(closure) as WorkGiver_Scanner;
                if (typeof(Predicate<Thing>).IsAssignableFrom(field.FieldType))
                {
                    WorkGiver_Scanner wrapped = FindScanner((field.GetValue(closure) as Predicate<Thing>)?.Target, depth + 1);
                    if (wrapped != null) return wrapped;
                }
                if (field.FieldType.Name.Contains("DisplayClass"))
                {
                    WorkGiver_Scanner nested = FindScanner(field.GetValue(closure), depth + 1);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        private static GiverAggregate GetAggregate(string giver)
        {
            if (!giverTotals.TryGetValue(giver, out GiverAggregate aggregate))
            {
                aggregate = new GiverAggregate();
                giverTotals.Add(giver, aggregate);
            }
            return aggregate;
        }

        private static void RecordUnknownCaller(SearchCallState state, long elapsed)
        {
            if (state.giver != "unidentified")
                return;
            unknownSeen++;
            if (unknownSeen % UnknownCallerStride != 0 || unknownWindowTraces >= UnknownCallerWindowLimit)
                return;
            unknownTraces++;
            unknownWindowTraces++;
            string caller = "unresolved";
            bool passedClosest = false;
            StackFrame[] frames = new StackTrace(1, false).GetFrames();
            if (frames != null)
            {
                for (int i = 0; i < frames.Length; i++)
                {
                    MethodBase method = frames[i].GetMethod();
                    if (method == null) continue;
                    Type type = method?.DeclaringType;
                    string name = type?.FullName ?? "";
                    if (name == "Verse.GenClosest" || method.Name.Contains("ClosestThing"))
                    {
                        passedClosest = true;
                        continue;
                    }
                    if (!passedClosest || name.StartsWith("RimMT2.") || name.StartsWith("MonoMod.") ||
                        name.StartsWith("System.") || name == "Glue")
                        continue;
                    caller = name + "." + method.Name;
                    break;
                }
            }
            recentUnknownCallers.Enqueue(new CallerSample { caller = caller, elapsedTicks = elapsed });
            if (recentUnknownCallers.Count > UnknownCallerRecentLimit)
                recentUnknownCallers.Dequeue();
        }

        internal static void AddGlobal(SearchCallState state)
        {
            if (state.started == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - state.started;
            currentGiver = state.previousGiver;
            globalTicks += elapsed;
            globalCalls++;
            GiverAggregate aggregate = GetAggregate(state.giver);
            aggregate.globalCalls++;
            aggregate.globalTicks += elapsed;
            RecordUnknownCaller(state, elapsed);
        }

        internal static void AddRegion(SearchCallState state)
        {
            if (state.started == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - state.started;
            currentGiver = state.previousGiver;
            regionTicks += elapsed;
            regionCalls++;
            GiverAggregate aggregate = GetAggregate(state.giver);
            aggregate.regionCalls++;
            aggregate.regionTicks += elapsed;
            RecordUnknownCaller(state, elapsed);
        }

        internal static void AddReach(long started)
        {
            if (started == 0) return;
            reachTicks += Stopwatch.GetTimestamp() - started;
            reachCalls++;
        }

        internal static void LogNow(string trigger)
        {
            double factor = 1000.0 / Stopwatch.Frequency;
            List<KeyValuePair<string, GiverAggregate>> top = new List<KeyValuePair<string, GiverAggregate>>(giverTotals);
            top.Sort((a, b) => (b.Value.globalTicks + b.Value.regionTicks).CompareTo(a.Value.globalTicks + a.Value.regionTicks));
            StringBuilder giverSummary = new StringBuilder();
            for (int i = 0; i < top.Count && i < 8; i++)
            {
                if (i > 0) giverSummary.Append(';');
                GiverAggregate value = top[i].Value;
                giverSummary.Append(top[i].Key).Append(':')
                    .Append(value.globalCalls).Append('/').Append((value.globalTicks * factor).ToString("F1"))
                    .Append(',').Append(value.regionCalls).Append('/').Append((value.regionTicks * factor).ToString("F1"));
            }
            // Build a report-only aggregate over the bounded rolling sample ring.
            Dictionary<string, CallerAggregate> recentTotals = new Dictionary<string, CallerAggregate>();
            foreach (CallerSample sample in recentUnknownCallers)
            {
                if (!recentTotals.TryGetValue(sample.caller, out CallerAggregate aggregate))
                {
                    aggregate = new CallerAggregate();
                    recentTotals.Add(sample.caller, aggregate);
                }
                aggregate.calls++;
                aggregate.ticks += sample.elapsedTicks;
                if (sample.elapsedTicks > aggregate.maxTicks) aggregate.maxTicks = sample.elapsedTicks;
            }
            List<KeyValuePair<string, CallerAggregate>> callerTop = new List<KeyValuePair<string, CallerAggregate>>(recentTotals);
            callerTop.Sort((a, b) => b.Value.ticks.CompareTo(a.Value.ticks));
            StringBuilder callerSummary = new StringBuilder();
            for (int i = 0; i < callerTop.Count && i < 8; i++)
            {
                if (i > 0) callerSummary.Append(';');
                CallerAggregate value = callerTop[i].Value;
                callerSummary.Append(callerTop[i].Key).Append(':')
                    .Append(value.calls).Append('/')
                    .Append((value.ticks * factor).ToString("F1")).Append('/')
                    .Append((value.maxTicks * factor).ToString("F1"));
            }
            Log.Message("[RimMT 2.0 M1] jobSearchCensus trigger=" + trigger +
                " enabled=" + (RimMT2Mod.Settings?.enableJobSearchCensus ?? false) +
                " seen=" + seen + " sampled=" + samples +
                " totalMs=" + (totalTicks * factor).ToString("F2") +
                " avgMs=" + (samples == 0 ? "0" : (totalTicks * factor / samples).ToString("F3")) +
                " maxMs=" + (maxTicks * factor).ToString("F2") +
                " over[10/50/100]ms=" + slowOver10Ms + "/" + slowOver50Ms + "/" + slowOver100Ms +
                " global[calls/ms]=" + totalGlobalCalls + "/" + (totalGlobalTicks * factor).ToString("F2") +
                " region[calls/ms]=" + totalRegionCalls + "/" + (totalRegionTicks * factor).ToString("F2") +
                " reach[calls/ms]=" + totalReachCalls + "/" + (totalReachTicks * factor).ToString("F2") +
                " giverTop[globalCalls/ms,regionCalls/ms]=" + giverSummary +
                " unknownCallerRecentTop[calls/ms/maxMs]=" + callerSummary +
                " unknown[seen/traces]=" + unknownSeen + "/" + unknownTraces +
                " unknown[windowTraces/recentSamples]=" + unknownWindowTraces + "/" + recentUnknownCallers.Count +
                " unknownCallerPolicy=stride32-window16-recent128" +
                " inclusiveTimers=true sampleRate=1/8");
        }
    }

    internal static class JobSearchRootCensusPatch
    {
        [HarmonyPrefix] private static void Prefix(ref long __state) => JobSearchCensus.Begin(ref __state);
        [HarmonyFinalizer] private static Exception Finalizer(Exception __exception, long __state)
        {
            JobSearchCensus.End(__state);
            return __exception;
        }
    }

    internal static class JobSearchGlobalCensusPatch
    {
        [HarmonyPrefix] private static void Prefix(Predicate<Thing> validator, ref JobSearchCensus.SearchCallState __state)
            => __state = JobSearchCensus.StartNestedCall(validator);
        [HarmonyPostfix] private static void Postfix(JobSearchCensus.SearchCallState __state)
            => JobSearchCensus.AddGlobal(__state);
    }

    internal static class JobSearchRegionCensusPatch
    {
        [HarmonyPrefix] private static void Prefix(Predicate<Thing> validator, ref JobSearchCensus.SearchCallState __state)
            => __state = JobSearchCensus.StartNestedCall(validator);
        [HarmonyPostfix] private static void Postfix(JobSearchCensus.SearchCallState __state)
            => JobSearchCensus.AddRegion(__state);
    }

    internal static class JobSearchReachCensusPatch
    {
        [HarmonyPrefix] private static void Prefix(ref long __state) { __state = JobSearchCensus.Active ? Stopwatch.GetTimestamp() : 0; }
        [HarmonyPostfix] private static void Postfix(long __state) => JobSearchCensus.AddReach(__state);
    }
}
