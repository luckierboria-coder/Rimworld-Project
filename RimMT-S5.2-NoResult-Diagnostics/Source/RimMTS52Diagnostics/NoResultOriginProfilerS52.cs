using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTS52Diagnostics
{
    [StaticConstructorOnStartup]
    internal static class NoResultOriginProfilerS52
    {
        // IMPORTANT: RimMT's WorkGiver compatibility guard treats only the exact core
        // Harmony owner as trusted. Using a separate owner suppresses its own fast paths.
        private const string HarmonyId = "allen.rimmt";
        private const int MaxTrackedPawnOrigins = 8192;
        private const int MaxOrigins = 2048;
        private const int TopN = 15;

        private static readonly object Sync = new object();
        private static readonly Dictionary<PawnOriginKey, RepeatState> RepeatStates = new Dictionary<PawnOriginKey, RepeatState>();
        private static readonly Dictionary<OriginKey, OriginStat> OriginStats = new Dictionary<OriginKey, OriginStat>();
        private static readonly List<MethodBase> PatchedMethods = new List<MethodBase>();

        private static Harmony harmony;
        private static bool reportHookInstalled;
        private static bool campaignActive;
        private static bool overflowedPawnOrigins;
        private static bool overflowedOrigins;
        private static int patchFailures;
        private static long campaignStartedAtTimestamp;
        private static int campaignStartedAtTick;
        private static long totalCalls;
        private static long totalNegative;
        private static long totalPositive;
        private static long repeatNegative;
        private static long repeats1;
        private static long repeats5;
        private static long repeats30;
        private static long repeats60;
        private static long repeats250;

        static NoResultOriginProfilerS52()
        {
            try
            {
                harmony = new Harmony(HarmonyId);
                Type diagnosticsType = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnosticsType == null ? null : AccessTools.Method(diagnosticsType, "LogRuntimeReport");
                MethodInfo prefix = AccessTools.Method(typeof(NoResultOriginProfilerS52), nameof(RuntimeReportPrefix));
                MethodInfo postfix = AccessTools.Method(typeof(NoResultOriginProfilerS52), nameof(RuntimeReportPostfix));
                if (report == null || prefix == null || postfix == null)
                {
                    Log.Warning("[RimMT-S5.2] unavailable: RimMTDiagnostics.LogRuntimeReport not found.");
                    return;
                }

                harmony.Patch(report,
                    prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                reportHookInstalled = true;
                Log.Message("[RimMT-S5.2] profiler installed with trusted owner=allen.rimmt. Diagnostic-only; no JobGiver result is changed.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT-S5.2] profiler install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RuntimeReportPrefix()
        {
            if (!reportHookInstalled)
                return;

            lock (Sync)
            {
                if (campaignActive)
                    return;

                ResetCounters();
                EnsureWorkGiverPatches();
                campaignActive = PatchedMethods.Count > 0;
                campaignStartedAtTimestamp = Stopwatch.GetTimestamp();
                campaignStartedAtTick = CurrentTick();
                Log.Message("[RimMT-S5.2] ARMED: patchedMethods=" + PatchedMethods.Count +
                    ", patchFailures=" + patchFailures +
                    ", owner=" + HarmonyId +
                    ". Existing RimMT WorkGiver fast-path authority should remain eligible.");
            }
        }

        public static void RuntimeReportPostfix()
        {
            if (!reportHookInstalled)
                return;

            lock (Sync)
            {
                if (!campaignActive)
                {
                    Log.Message("[RimMT-S5.2] profiler inactive: patchedMethods=0, patchFailures=" + patchFailures + ".");
                    return;
                }

                if (totalCalls > 0)
                    Log.Message(BuildSummary(TopN));
            }
        }

        private static void EnsureWorkGiverPatches()
        {
            if (PatchedMethods.Count > 0)
                return;

            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            List<Type> allTypes = GenTypes.AllTypes;
            MethodInfo prefix = AccessTools.Method(typeof(NoResultOriginProfilerS52), nameof(WorkGiverPrefix));
            MethodInfo boolPostfix = AccessTools.Method(typeof(NoResultOriginProfilerS52), nameof(BoolPostfix));
            MethodInfo jobPostfix = AccessTools.Method(typeof(NoResultOriginProfilerS52), nameof(JobPostfix));

            for (int i = 0; i < allTypes.Count; i++)
            {
                Type type = allTypes[i];
                if (type == null || type.IsAbstract || !typeof(WorkGiver).IsAssignableFrom(type))
                    continue;

                MethodInfo[] methods;
                try { methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly); }
                catch { continue; }

                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (!IsCandidate(method) || !unique.Add(method))
                        continue;

                    try
                    {
                        MethodInfo selectedPostfix = method.ReturnType == typeof(bool) ? boolPostfix : jobPostfix;
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                            postfix: new HarmonyMethod(selectedPostfix) { priority = Priority.Last });
                        PatchedMethods.Add(method);
                    }
                    catch (Exception ex)
                    {
                        patchFailures++;
                        if (patchFailures <= 8)
                            Log.Warning("[RimMT-S5.2] skipped " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static bool IsCandidate(MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                return false;

            string name = method.Name;
            if (name != "ShouldSkip" && name != "NonScanJob" && name != "HasJobOnThing" &&
                name != "HasJobOnCell" && name != "JobOnThing" && name != "JobOnCell")
                return false;

            ParameterInfo[] parameters = method.GetParameters();
            if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Pawn))
                return false;

            return method.ReturnType == typeof(bool) || typeof(Job).IsAssignableFrom(method.ReturnType);
        }

        public static void WorkGiverPrefix(ref long __state)
        {
            __state = campaignActive ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void BoolPostfix(WorkGiver __instance, MethodBase __originalMethod, bool __result, object[] __args, long __state)
        {
            if (__state == 0L || !campaignActive || __instance == null || __originalMethod == null)
                return;
            Pawn pawn = ExtractPawn(__args);
            bool negative = __originalMethod.Name == "ShouldSkip" ? __result : !__result;
            Record(__instance, __originalMethod, pawn, negative, Stopwatch.GetTimestamp() - __state);
        }

        public static void JobPostfix(WorkGiver __instance, MethodBase __originalMethod, Job __result, object[] __args, long __state)
        {
            if (__state == 0L || !campaignActive || __instance == null || __originalMethod == null)
                return;
            Pawn pawn = ExtractPawn(__args);
            Record(__instance, __originalMethod, pawn, __result == null, Stopwatch.GetTimestamp() - __state);
        }

        private static Pawn ExtractPawn(object[] args)
        {
            return args != null && args.Length > 0 ? args[0] as Pawn : null;
        }

        private static void Record(WorkGiver giver, MethodBase method, Pawn pawn, bool negative, long elapsed)
        {
            lock (Sync)
            {
                totalCalls++;
                if (negative) totalNegative++; else totalPositive++;

                WorkGiverDef def = giver.def;
                Type type = giver.GetType();
                string assemblyName = type.Assembly == null ? "<unknown-assembly>" : type.Assembly.GetName().Name;
                OriginKey origin = new OriginKey(def, type, method.Name, assemblyName);

                OriginStat stat;
                if (!OriginStats.TryGetValue(origin, out stat))
                {
                    if (OriginStats.Count >= MaxOrigins)
                    {
                        overflowedOrigins = true;
                        return;
                    }
                    stat = new OriginStat();
                    OriginStats.Add(origin, stat);
                }

                stat.Calls++;
                stat.TotalTicks += elapsed;
                if (elapsed > stat.MaxTicks) stat.MaxTicks = elapsed;

                PawnOriginKey pawnOrigin = new PawnOriginKey(origin, pawn == null ? 0 : pawn.thingIDNumber);
                if (!negative)
                {
                    stat.Positive++;
                    RepeatStates.Remove(pawnOrigin);
                    return;
                }

                stat.Negative++;
                int tick = CurrentTick();
                RepeatState repeat;
                if (!RepeatStates.TryGetValue(pawnOrigin, out repeat))
                {
                    if (RepeatStates.Count >= MaxTrackedPawnOrigins)
                    {
                        overflowedPawnOrigins = true;
                        return;
                    }
                    repeat = new RepeatState { LastNegativeTick = tick, ConsecutiveNegative = 1 };
                    RepeatStates.Add(pawnOrigin, repeat);
                    if (stat.MaxConsecutiveNegative < 1) stat.MaxConsecutiveNegative = 1;
                    return;
                }

                int delta = tick - repeat.LastNegativeTick;
                if (delta < 0) delta = int.MaxValue;
                repeat.LastNegativeTick = tick;
                repeat.ConsecutiveNegative++;
                RepeatStates[pawnOrigin] = repeat;

                repeatNegative++;
                stat.RepeatedNegative++;
                if (delta != int.MaxValue)
                {
                    stat.RepeatIntervalTickSum += delta;
                    stat.RepeatIntervalSamples++;
                }
                if (repeat.ConsecutiveNegative > stat.MaxConsecutiveNegative)
                    stat.MaxConsecutiveNegative = repeat.ConsecutiveNegative;

                if (delta <= 1) { repeats1++; stat.Repeat1++; }
                if (delta <= 5) { repeats5++; stat.Repeat5++; }
                if (delta <= 30) { repeats30++; stat.Repeat30++; }
                if (delta <= 60) { repeats60++; stat.Repeat60++; }
                if (delta <= 250) { repeats250++; stat.Repeat250++; }
            }
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        private static void ResetCounters()
        {
            RepeatStates.Clear();
            OriginStats.Clear();
            overflowedPawnOrigins = false;
            overflowedOrigins = false;
            totalCalls = totalNegative = totalPositive = repeatNegative = 0L;
            repeats1 = repeats5 = repeats30 = repeats60 = repeats250 = 0L;
        }

        private static string BuildSummary(int topN)
        {
            List<OriginEntry> entries = new List<OriginEntry>(OriginStats.Count);
            foreach (KeyValuePair<OriginKey, OriginStat> pair in OriginStats)
                entries.Add(new OriginEntry(pair.Key, pair.Value));

            entries.Sort(delegate(OriginEntry a, OriginEntry b)
            {
                int byRepeat = b.Stat.RepeatedNegative.CompareTo(a.Stat.RepeatedNegative);
                if (byRepeat != 0) return byRepeat;
                int byNegative = b.Stat.Negative.CompareTo(a.Stat.Negative);
                if (byNegative != 0) return byNegative;
                return b.Stat.TotalTicks.CompareTo(a.Stat.TotalTicks);
            });

            if (topN > entries.Count) topN = entries.Count;
            int elapsedGameTicks = CurrentTick() - campaignStartedAtTick;
            double wallSeconds = campaignStartedAtTimestamp == 0L ? 0.0 :
                (Stopwatch.GetTimestamp() - campaignStartedAtTimestamp) / (double)Stopwatch.Frequency;
            double negRatio = totalCalls == 0 ? 0.0 : totalNegative * 100.0 / totalCalls;
            double repeatRatio = totalNegative == 0 ? 0.0 : repeatNegative * 100.0 / totalNegative;

            StringBuilder sb = new StringBuilder();
            sb.Append("[RimMT] S5.2 NoResult Origin report: sidecar=True, owner=").Append(HarmonyId)
                .Append(", patchedMethods=").Append(PatchedMethods.Count)
                .Append(", patchFailures=").Append(patchFailures)
                .Append(", gameTicks=").Append(elapsedGameTicks)
                .Append(", wallSec=").Append(wallSeconds.ToString("F1"))
                .Append(", calls=").Append(totalCalls)
                .Append(", negative/positive=").Append(totalNegative).Append('/').Append(totalPositive)
                .Append(", negativeRatio=").Append(negRatio.ToString("F1")).Append('%')
                .Append(", repeatedNegative=").Append(repeatNegative)
                .Append(", repeatedRatio=").Append(repeatRatio.ToString("F1")).Append('%')
                .Append(", repeatHeat<=1/5/30/60/250ticks=")
                .Append(repeats1).Append('/').Append(repeats5).Append('/').Append(repeats30).Append('/').Append(repeats60).Append('/').Append(repeats250)
                .Append(", trackedOrigins=").Append(OriginStats.Count)
                .Append(", trackedPawnOrigins=").Append(RepeatStates.Count)
                .Append(", overflow(origin/pawnOrigin)=").Append(overflowedOrigins).Append('/').Append(overflowedPawnOrigins);

            for (int i = 0; i < topN; i++)
            {
                OriginEntry entry = entries[i];
                OriginStat stat = entry.Stat;
                double totalMs = stat.TotalTicks * 1000.0 / Stopwatch.Frequency;
                double avgUs = stat.Calls == 0 ? 0.0 : stat.TotalTicks * 1000000.0 / Stopwatch.Frequency / stat.Calls;
                double maxMs = stat.MaxTicks * 1000.0 / Stopwatch.Frequency;
                double noResultRatio = stat.Calls == 0 ? 0.0 : stat.Negative * 100.0 / stat.Calls;
                double originRepeatRatio = stat.Negative == 0 ? 0.0 : stat.RepeatedNegative * 100.0 / stat.Negative;
                double avgInterval = stat.RepeatIntervalSamples == 0 ? 0.0 :
                    stat.RepeatIntervalTickSum / (double)stat.RepeatIntervalSamples;

                sb.Append("\n  #").Append(i + 1).Append(' ')
                    .Append(entry.Key.DefName).Append(" / ")
                    .Append(entry.Key.WorkerTypeName).Append('.').Append(entry.Key.Method)
                    .Append(" [asm=").Append(entry.Key.AssemblyName).Append(']')
                    .Append(": calls=").Append(stat.Calls)
                    .Append(", noResult=").Append(stat.Negative).Append(" (").Append(noResultRatio.ToString("F1")).Append("%)")
                    .Append(", repeated=").Append(stat.RepeatedNegative).Append(" (").Append(originRepeatRatio.ToString("F1")).Append("%)")
                    .Append(", repeat<=1/5/30/60/250=")
                    .Append(stat.Repeat1).Append('/').Append(stat.Repeat5).Append('/').Append(stat.Repeat30).Append('/').Append(stat.Repeat60).Append('/').Append(stat.Repeat250)
                    .Append(", avgRepeatIntervalTicks=").Append(avgInterval.ToString("F1"))
                    .Append(", maxConsecutive=").Append(stat.MaxConsecutiveNegative)
                    .Append(", totalMs=").Append(totalMs.ToString("F1"))
                    .Append(", avgUs=").Append(avgUs.ToString("F2"))
                    .Append(", maxMs=").Append(maxMs.ToString("F3"));
            }
            return sb.ToString();
        }

        private struct OriginKey : IEquatable<OriginKey>
        {
            internal readonly WorkGiverDef Def;
            internal readonly Type WorkerType;
            internal readonly string Method;
            internal readonly string AssemblyName;
            internal OriginKey(WorkGiverDef def, Type workerType, string method, string assemblyName)
            {
                Def = def;
                WorkerType = workerType;
                Method = method ?? "?";
                AssemblyName = assemblyName ?? "<unknown-assembly>";
            }
            internal string DefName { get { return Def == null ? "<no-def>" : Def.defName; } }
            internal string WorkerTypeName { get { return WorkerType == null ? "<null>" : WorkerType.FullName; } }
            public bool Equals(OriginKey other)
            {
                return ReferenceEquals(Def, other.Def) && WorkerType == other.WorkerType && Method == other.Method && AssemblyName == other.AssemblyName;
            }
            public override bool Equals(object obj) { return obj is OriginKey && Equals((OriginKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Def == null ? 0 : Def.GetHashCode();
                    hash = hash * 397 ^ (WorkerType == null ? 0 : WorkerType.GetHashCode());
                    hash = hash * 397 ^ Method.GetHashCode();
                    hash = hash * 397 ^ AssemblyName.GetHashCode();
                    return hash;
                }
            }
        }

        private struct PawnOriginKey : IEquatable<PawnOriginKey>
        {
            internal readonly OriginKey Origin;
            internal readonly int PawnId;
            internal PawnOriginKey(OriginKey origin, int pawnId) { Origin = origin; PawnId = pawnId; }
            public bool Equals(PawnOriginKey other) { return PawnId == other.PawnId && Origin.Equals(other.Origin); }
            public override bool Equals(object obj) { return obj is PawnOriginKey && Equals((PawnOriginKey)obj); }
            public override int GetHashCode() { unchecked { return Origin.GetHashCode() * 397 ^ PawnId; } }
        }

        private struct RepeatState
        {
            internal int LastNegativeTick;
            internal int ConsecutiveNegative;
        }

        private sealed class OriginStat
        {
            internal long Calls;
            internal long Negative;
            internal long Positive;
            internal long RepeatedNegative;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long Repeat1;
            internal long Repeat5;
            internal long Repeat30;
            internal long Repeat60;
            internal long Repeat250;
            internal long RepeatIntervalTickSum;
            internal long RepeatIntervalSamples;
            internal int MaxConsecutiveNegative;
        }

        private struct OriginEntry
        {
            internal readonly OriginKey Key;
            internal readonly OriginStat Stat;
            internal OriginEntry(OriginKey key, OriginStat stat) { Key = key; Stat = stat; }
        }
    }
}
