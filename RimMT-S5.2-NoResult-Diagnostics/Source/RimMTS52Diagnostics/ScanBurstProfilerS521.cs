using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTS52Diagnostics
{
    [StaticConstructorOnStartup]
    internal static class ScanBurstProfilerS521
    {
        private const string HarmonyId = "allen.rimmt";
        private const int TopN = 15;
        private const int MaxOrigins = 2048;
        private const int MaxActiveBursts = 8192;

        private static readonly object Sync = new object();
        private static readonly Dictionary<OriginKey, OriginStat> Stats = new Dictionary<OriginKey, OriginStat>();
        private static readonly Dictionary<PawnOriginKey, BurstState> Active = new Dictionary<PawnOriginKey, BurstState>();
        private static readonly Dictionary<PawnOriginKey, RepeatState> Repeat = new Dictionary<PawnOriginKey, RepeatState>();
        private static readonly List<MethodBase> Patched = new List<MethodBase>();

        private static Harmony harmony;
        private static bool installed;
        private static bool armed;
        private static int patchFailures;
        private static bool overflowOrigins;
        private static bool overflowActive;
        private static int firstObservedTick = -1;
        private static long firstObservedTimestamp;
        private static long rawCalls;
        private static long totalBursts;
        private static long noResultBursts;
        private static long positiveBursts;
        private static long repeatedNoResultBursts;
        private static long repeat1;
        private static long repeat5;
        private static long repeat30;
        private static long repeat60;
        private static long repeat250;

        static ScanBurstProfilerS521()
        {
            try
            {
                harmony = new Harmony(HarmonyId);
                Type diagnosticsType = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnosticsType == null ? null : AccessTools.Method(diagnosticsType, "LogRuntimeReport");
                MethodInfo reportPostfix = AccessTools.Method(typeof(ScanBurstProfilerS521), nameof(RuntimeReportPostfix));
                if (report == null || reportPostfix == null)
                {
                    Log.Warning("[RimMT-S5.2.1] Scan-Burst profiler unavailable: RimMTDiagnostics.LogRuntimeReport not found.");
                    return;
                }

                harmony.Patch(report, postfix: new HarmonyMethod(reportPostfix) { priority = Priority.Last });
                installed = true;
                LongEventHandler.ExecuteWhenFinished(delegate
                {
                    try { Arm(); }
                    catch (Exception ex) { Log.Warning("[RimMT-S5.2.1] auto-arm failed: " + ex.GetType().Name + ": " + ex.Message); }
                });
                Log.Message("[RimMT-S5.2.1] Scan-Burst profiler installed with trusted owner=allen.rimmt. Bursts are same-pawn/same-origin/same-game-tick aggregates; multiple scans in one tick may merge, so burst counts are conservative lower bounds.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT-S5.2.1] install failed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void Arm()
        {
            if (!installed || armed)
                return;

            lock (Sync)
            {
                if (armed)
                    return;
                EnsurePatches();
                armed = Patched.Count > 0;
                Log.Message("[RimMT-S5.2.1] ARMED: patchedMethods=" + Patched.Count + ", patchFailures=" + patchFailures + ", owner=" + HarmonyId + ". First observed WorkGiver call will establish campaign tick zero.");
            }
        }

        private static void EnsurePatches()
        {
            if (Patched.Count > 0)
                return;

            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            MethodInfo prefix = AccessTools.Method(typeof(ScanBurstProfilerS521), nameof(CallPrefix));
            MethodInfo boolPostfix = AccessTools.Method(typeof(ScanBurstProfilerS521), nameof(BoolPostfix));
            MethodInfo jobPostfix = AccessTools.Method(typeof(ScanBurstProfilerS521), nameof(JobPostfix));
            List<Type> allTypes = GenTypes.AllTypes;

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
                        MethodInfo postfix = method.ReturnType == typeof(bool) ? boolPostfix : jobPostfix;
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(prefix) { priority = Priority.First },
                            postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                        Patched.Add(method);
                    }
                    catch (Exception ex)
                    {
                        patchFailures++;
                        if (patchFailures <= 8)
                            Log.Warning("[RimMT-S5.2.1] skipped " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static bool IsCandidate(MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters)
                return false;
            string n = method.Name;
            if (n != "HasJobOnThing" && n != "HasJobOnCell" && n != "JobOnThing" && n != "JobOnCell")
                return false;
            ParameterInfo[] p = method.GetParameters();
            if (p.Length == 0 || p[0].ParameterType != typeof(Pawn))
                return false;
            return method.ReturnType == typeof(bool) || typeof(Job).IsAssignableFrom(method.ReturnType);
        }

        public static void CallPrefix(ref long __state)
        {
            __state = armed ? Stopwatch.GetTimestamp() : 0L;
        }

        public static void BoolPostfix(WorkGiver __instance, MethodBase __originalMethod, bool __result, object[] __args, long __state)
        {
            if (__state == 0L || !armed || __instance == null || __originalMethod == null)
                return;
            Pawn pawn = ExtractPawn(__args);
            Record(__instance, __originalMethod, pawn, __result, Stopwatch.GetTimestamp() - __state);
        }

        public static void JobPostfix(WorkGiver __instance, MethodBase __originalMethod, Job __result, object[] __args, long __state)
        {
            if (__state == 0L || !armed || __instance == null || __originalMethod == null)
                return;
            Pawn pawn = ExtractPawn(__args);
            Record(__instance, __originalMethod, pawn, __result != null, Stopwatch.GetTimestamp() - __state);
        }

        private static Pawn ExtractPawn(object[] args)
        {
            return args != null && args.Length > 0 ? args[0] as Pawn : null;
        }

        private static void Record(WorkGiver giver, MethodBase method, Pawn pawn, bool positive, long elapsed)
        {
            if (pawn == null)
                return;

            lock (Sync)
            {
                int tick = CurrentTick();
                if (firstObservedTick < 0)
                {
                    firstObservedTick = tick;
                    firstObservedTimestamp = Stopwatch.GetTimestamp();
                }

                rawCalls++;
                Type type = giver.GetType();
                WorkGiverDef def = giver.def;
                string assemblyName = type.Assembly == null ? "<unknown-assembly>" : type.Assembly.GetName().Name;
                OriginKey origin = new OriginKey(def, type, method.Name, assemblyName);

                OriginStat stat;
                if (!Stats.TryGetValue(origin, out stat))
                {
                    if (Stats.Count >= MaxOrigins)
                    {
                        overflowOrigins = true;
                        return;
                    }
                    stat = new OriginStat();
                    Stats.Add(origin, stat);
                }
                stat.RawCalls++;
                stat.RawTicks += elapsed;

                PawnOriginKey key = new PawnOriginKey(origin, pawn.thingIDNumber);
                BurstState burst;
                if (!Active.TryGetValue(key, out burst))
                {
                    if (Active.Count >= MaxActiveBursts)
                    {
                        overflowActive = true;
                        return;
                    }
                    burst = NewBurst(tick);
                }
                else if (burst.Tick != tick)
                {
                    FinalizeBurst(key, burst, stat);
                    burst = NewBurst(tick);
                }

                burst.Calls++;
                burst.ElapsedTicks += elapsed;
                if (positive)
                    burst.Positive = true;
                Active[key] = burst;
            }
        }

        private static BurstState NewBurst(int tick)
        {
            BurstState b = new BurstState();
            b.Tick = tick;
            b.Calls = 0;
            b.ElapsedTicks = 0L;
            b.Positive = false;
            return b;
        }

        private static void FinalizeBurst(PawnOriginKey key, BurstState burst, OriginStat stat)
        {
            totalBursts++;
            stat.Bursts++;
            stat.TotalBurstCalls += burst.Calls;
            stat.TotalBurstTicks += burst.ElapsedTicks;
            if (burst.Calls > stat.MaxCallsPerBurst) stat.MaxCallsPerBurst = burst.Calls;
            if (burst.ElapsedTicks > stat.MaxBurstTicks) stat.MaxBurstTicks = burst.ElapsedTicks;

            if (burst.Positive)
            {
                positiveBursts++;
                stat.PositiveBursts++;
                Repeat.Remove(key);
                return;
            }

            noResultBursts++;
            stat.NoResultBursts++;
            RepeatState r;
            if (!Repeat.TryGetValue(key, out r))
            {
                r.LastNoResultTick = burst.Tick;
                r.Consecutive = 1;
                Repeat[key] = r;
                if (stat.MaxConsecutiveNoResult < 1) stat.MaxConsecutiveNoResult = 1;
                return;
            }

            int delta = burst.Tick - r.LastNoResultTick;
            if (delta < 0) delta = int.MaxValue;
            r.LastNoResultTick = burst.Tick;
            r.Consecutive++;
            Repeat[key] = r;

            repeatedNoResultBursts++;
            stat.RepeatedNoResultBursts++;
            if (delta != int.MaxValue)
            {
                stat.RepeatIntervalSum += delta;
                stat.RepeatIntervalSamples++;
            }
            if (r.Consecutive > stat.MaxConsecutiveNoResult)
                stat.MaxConsecutiveNoResult = r.Consecutive;

            if (delta <= 1) { repeat1++; stat.Repeat1++; }
            if (delta <= 5) { repeat5++; stat.Repeat5++; }
            if (delta <= 30) { repeat30++; stat.Repeat30++; }
            if (delta <= 60) { repeat60++; stat.Repeat60++; }
            if (delta <= 250) { repeat250++; stat.Repeat250++; }
        }

        public static void RuntimeReportPostfix()
        {
            if (!installed || !armed)
                return;

            lock (Sync)
            {
                FlushActive();
                Log.Message(BuildSummary());
            }
        }

        private static void FlushActive()
        {
            if (Active.Count == 0)
                return;
            List<KeyValuePair<PawnOriginKey, BurstState>> pending = new List<KeyValuePair<PawnOriginKey, BurstState>>(Active);
            Active.Clear();
            for (int i = 0; i < pending.Count; i++)
            {
                OriginStat stat;
                if (Stats.TryGetValue(pending[i].Key.Origin, out stat))
                    FinalizeBurst(pending[i].Key, pending[i].Value, stat);
            }
        }

        private static string BuildSummary()
        {
            List<Entry> entries = new List<Entry>(Stats.Count);
            foreach (KeyValuePair<OriginKey, OriginStat> pair in Stats)
                entries.Add(new Entry(pair.Key, pair.Value));

            entries.Sort(delegate(Entry a, Entry b)
            {
                int byTime = b.Stat.TotalBurstTicks.CompareTo(a.Stat.TotalBurstTicks);
                if (byTime != 0) return byTime;
                int byNoResult = b.Stat.NoResultBursts.CompareTo(a.Stat.NoResultBursts);
                if (byNoResult != 0) return byNoResult;
                return b.Stat.RawCalls.CompareTo(a.Stat.RawCalls);
            });

            int top = Math.Min(TopN, entries.Count);
            double wallSec = firstObservedTimestamp == 0L ? 0.0 : (Stopwatch.GetTimestamp() - firstObservedTimestamp) / (double)Stopwatch.Frequency;
            int gameTicks = firstObservedTick < 0 ? 0 : CurrentTick() - firstObservedTick;
            double noResultRatio = totalBursts == 0 ? 0.0 : noResultBursts * 100.0 / totalBursts;
            double repeatRatio = noResultBursts == 0 ? 0.0 : repeatedNoResultBursts * 100.0 / noResultBursts;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("[RimMT] S5.2.1 Scan-Burst report: approximate=True, owner=").Append(HarmonyId)
                .Append(", patchedMethods=").Append(Patched.Count)
                .Append(", patchFailures=").Append(patchFailures)
                .Append(", gameTicks=").Append(gameTicks)
                .Append(", wallSec=").Append(wallSec.ToString("F1"))
                .Append(", rawCalls=").Append(rawCalls)
                .Append(", bursts=").Append(totalBursts)
                .Append(", noResult/positiveBursts=").Append(noResultBursts).Append('/').Append(positiveBursts)
                .Append(", noResultRatio=").Append(noResultRatio.ToString("F1")).Append('%')
                .Append(", repeatedNoResultBursts=").Append(repeatedNoResultBursts)
                .Append(", repeatedRatio=").Append(repeatRatio.ToString("F1")).Append('%')
                .Append(", repeatHeat<=1/5/30/60/250ticks=").Append(repeat1).Append('/').Append(repeat5).Append('/').Append(repeat30).Append('/').Append(repeat60).Append('/').Append(repeat250)
                .Append(", trackedOrigins=").Append(Stats.Count)
                .Append(", trackedPawnOrigins=").Append(Repeat.Count)
                .Append(", overflow(origin/active)=").Append(overflowOrigins).Append('/').Append(overflowActive)
                .Append(", note=same-pawn+same-origin+same-tick calls are merged; multiple full scans inside one tick can merge, so burst count is a conservative lower bound.");

            for (int i = 0; i < top; i++)
            {
                Entry e = entries[i];
                OriginStat s = e.Stat;
                double totalMs = s.TotalBurstTicks * 1000.0 / Stopwatch.Frequency;
                double avgBurstUs = s.Bursts == 0 ? 0.0 : s.TotalBurstTicks * 1000000.0 / Stopwatch.Frequency / s.Bursts;
                double maxBurstMs = s.MaxBurstTicks * 1000.0 / Stopwatch.Frequency;
                double avgCalls = s.Bursts == 0 ? 0.0 : s.TotalBurstCalls / (double)s.Bursts;
                double nr = s.Bursts == 0 ? 0.0 : s.NoResultBursts * 100.0 / s.Bursts;
                double rr = s.NoResultBursts == 0 ? 0.0 : s.RepeatedNoResultBursts * 100.0 / s.NoResultBursts;
                double avgInterval = s.RepeatIntervalSamples == 0 ? 0.0 : s.RepeatIntervalSum / (double)s.RepeatIntervalSamples;

                sb.Append("\n  #").Append(i + 1).Append(' ')
                    .Append(e.Key.DefName).Append(" / ").Append(e.Key.WorkerTypeName).Append('.').Append(e.Key.Method)
                    .Append(" [asm=").Append(e.Key.AssemblyName).Append(']')
                    .Append(": rawCalls=").Append(s.RawCalls)
                    .Append(", bursts=").Append(s.Bursts)
                    .Append(", noResult=").Append(s.NoResultBursts).Append(" (").Append(nr.ToString("F1")).Append("%)")
                    .Append(", repeatedNoResult=").Append(s.RepeatedNoResultBursts).Append(" (").Append(rr.ToString("F1")).Append("%)")
                    .Append(", repeat<=1/5/30/60/250=").Append(s.Repeat1).Append('/').Append(s.Repeat5).Append('/').Append(s.Repeat30).Append('/').Append(s.Repeat60).Append('/').Append(s.Repeat250)
                    .Append(", avgRepeatIntervalTicks=").Append(avgInterval.ToString("F1"))
                    .Append(", avgCallsPerBurst=").Append(avgCalls.ToString("F1"))
                    .Append(", maxCallsPerBurst=").Append(s.MaxCallsPerBurst)
                    .Append(", maxConsecutiveNoResult=").Append(s.MaxConsecutiveNoResult)
                    .Append(", totalMs=").Append(totalMs.ToString("F1"))
                    .Append(", avgBurstUs=").Append(avgBurstUs.ToString("F2"))
                    .Append(", maxBurstMs=").Append(maxBurstMs.ToString("F3"));
            }
            return sb.ToString();
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        private struct OriginKey : IEquatable<OriginKey>
        {
            internal readonly WorkGiverDef Def;
            internal readonly Type WorkerType;
            internal readonly string Method;
            internal readonly string AssemblyName;
            internal OriginKey(WorkGiverDef def, Type workerType, string method, string assemblyName)
            {
                Def = def; WorkerType = workerType; Method = method ?? "?"; AssemblyName = assemblyName ?? "<unknown-assembly>";
            }
            internal string DefName { get { return Def == null ? "<no-def>" : Def.defName; } }
            internal string WorkerTypeName { get { return WorkerType == null ? "<null>" : WorkerType.FullName; } }
            public bool Equals(OriginKey other) { return ReferenceEquals(Def, other.Def) && WorkerType == other.WorkerType && Method == other.Method && AssemblyName == other.AssemblyName; }
            public override bool Equals(object obj) { return obj is OriginKey && Equals((OriginKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Def == null ? 0 : Def.GetHashCode();
                    h = h * 397 ^ (WorkerType == null ? 0 : WorkerType.GetHashCode());
                    h = h * 397 ^ Method.GetHashCode();
                    h = h * 397 ^ AssemblyName.GetHashCode();
                    return h;
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

        private struct BurstState
        {
            internal int Tick;
            internal int Calls;
            internal long ElapsedTicks;
            internal bool Positive;
        }

        private struct RepeatState
        {
            internal int LastNoResultTick;
            internal int Consecutive;
        }

        private sealed class OriginStat
        {
            internal long RawCalls;
            internal long RawTicks;
            internal long Bursts;
            internal long NoResultBursts;
            internal long PositiveBursts;
            internal long RepeatedNoResultBursts;
            internal long TotalBurstCalls;
            internal long TotalBurstTicks;
            internal long MaxBurstTicks;
            internal int MaxCallsPerBurst;
            internal int MaxConsecutiveNoResult;
            internal long Repeat1;
            internal long Repeat5;
            internal long Repeat30;
            internal long Repeat60;
            internal long Repeat250;
            internal long RepeatIntervalSum;
            internal long RepeatIntervalSamples;
        }

        private struct Entry
        {
            internal readonly OriginKey Key;
            internal readonly OriginStat Stat;
            internal Entry(OriginKey key, OriginStat stat) { Key = key; Stat = stat; }
        }
    }
}
