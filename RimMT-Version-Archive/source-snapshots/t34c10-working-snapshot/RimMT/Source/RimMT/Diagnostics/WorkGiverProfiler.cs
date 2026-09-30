using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using RimWorld;
using Verse;

namespace RimMT
{
    internal static class WorkGiverProfiler
    {
        private const int MaxSlowTraces = 8;
        private const int MaxPhasesPerSlowTrace = 10;
        private static readonly Dictionary<ProfileKey, Stat> Stats = new Dictionary<ProfileKey, Stat>();
        private static readonly List<SlowTrace> SlowTraces = new List<SlowTrace>();
        private static readonly long Threshold16Ticks = Math.Max(1L, Stopwatch.Frequency * 16L / 1000L);
        private static readonly long Threshold64Ticks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);
        private static readonly long Threshold128Ticks = Math.Max(1L, Stopwatch.Frequency * 128L / 1000L);

        private static long totalSamples;
        private static long totalJobPackages;
        private static long slowJobPackages;
        private static int targetJobPackages;
        private static int patchedMethods;
        private static int patchFailures;
        private static bool sessionActive;

        [ThreadStatic] private static int jobPackageDepth;
        [ThreadStatic] private static bool captureDetail;
        [ThreadStatic] private static Dictionary<string, long> currentInclusivePhases;
        [ThreadStatic] private static string currentPawn;

        internal struct JobPackageScope
        {
            internal long Started;
            internal bool Entered;
            internal bool Outermost;
        }

        internal static bool DetailCaptureActive { get { return captureDetail && sessionActive; } }

        internal static int PackagesRemaining
        {
            get
            {
                int remaining = targetJobPackages - (int)totalJobPackages;
                return remaining < 0 ? 0 : remaining;
            }
        }

        internal static void StartSession(int packageTarget, int patched, int failures)
        {
            Stats.Clear();
            SlowTraces.Clear();
            JobGiverInfrastructureProfiler.Reset();
            totalSamples = 0;
            totalJobPackages = 0;
            slowJobPackages = 0;
            targetJobPackages = Math.Max(1, packageTarget);
            patchedMethods = patched;
            patchFailures = failures;
            jobPackageDepth = 0;
            captureDetail = false;
            currentInclusivePhases = null;
            currentPawn = null;
            sessionActive = true;
        }

        internal static void StopSession()
        {
            sessionActive = false;
            captureDetail = false;
            jobPackageDepth = 0;
            currentInclusivePhases = null;
            currentPawn = null;
        }

        internal static JobPackageScope BeginJobPackage(Pawn pawn)
        {
            JobPackageScope state = default(JobPackageScope);
            if (!sessionActive || !RimMTThreadGuard.IsMainThread)
                return state;

            state.Entered = true;
            state.Outermost = jobPackageDepth == 0;
            jobPackageDepth++;
            if (!state.Outermost)
                return state;

            state.Started = Stopwatch.GetTimestamp();
            totalJobPackages++;
            captureDetail = true;
            currentInclusivePhases = new Dictionary<string, long>(StringComparer.Ordinal);
            currentPawn = pawn == null ? "<null>" : pawn.ToString();
            return state;
        }

        internal static void EndJobPackage(JobPackageScope state)
        {
            if (!state.Entered)
                return;

            if (state.Outermost && state.Started != 0L)
            {
                long elapsed = Stopwatch.GetTimestamp() - state.Started;
                if (elapsed >= Threshold64Ticks)
                {
                    slowJobPackages++;
                    SaveSlowTrace(elapsed);
                }
            }

            if (jobPackageDepth > 0)
                jobPackageDepth--;
            if (jobPackageDepth == 0)
            {
                captureDetail = false;
                currentInclusivePhases = null;
                currentPawn = null;
                if (sessionActive && totalJobPackages >= targetJobPackages)
                    WorkGiverDetailPatches.RequestStopCapture();
            }
        }

        internal static long Begin()
        {
            if (!captureDetail || !RimMTThreadGuard.IsMainThread)
                return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void Record(WorkGiver giver, MethodBase method, long started)
        {
            if (started == 0L || giver == null || method == null || !RimMTThreadGuard.IsMainThread)
                return;

            long elapsed = Stopwatch.GetTimestamp() - started;
            ProfileKey key = new ProfileKey(giver.def, giver.GetType(), method.Name);
            Stat stat;
            if (!Stats.TryGetValue(key, out stat))
            {
                stat = new Stat();
                Stats.Add(key, stat);
            }

            stat.Count++;
            stat.TotalTicks += elapsed;
            if (elapsed > stat.MaxTicks)
                stat.MaxTicks = elapsed;
            if (elapsed >= Threshold16Ticks) stat.Over16Ms++;
            if (elapsed >= Threshold64Ticks) stat.Over64Ms++;
            if (elapsed >= Threshold128Ticks) stat.Over128Ms++;
            totalSamples++;

            string phase = key.DefName + "/" + key.WorkerTypeName + "." + key.Phase;
            RecordInclusivePhase(phase, elapsed);
        }

        internal static void RecordInclusivePhase(string phase, long elapsedTicks)
        {
            if (!captureDetail || currentInclusivePhases == null || string.IsNullOrEmpty(phase) || elapsedTicks <= 0L)
                return;
            long existing;
            currentInclusivePhases.TryGetValue(phase, out existing);
            currentInclusivePhases[phase] = existing + elapsedTicks;
        }

        internal static string Summary(int topN)
        {
            List<Entry> entries = new List<Entry>(Stats.Count);
            foreach (KeyValuePair<ProfileKey, Stat> pair in Stats)
                entries.Add(new Entry(pair.Key, pair.Value));

            entries.Sort(delegate(Entry a, Entry b)
            {
                int byTotal = b.Stat.TotalTicks.CompareTo(a.Stat.TotalTicks);
                if (byTotal != 0) return byTotal;
                return b.Stat.MaxTicks.CompareTo(a.Stat.MaxTicks);
            });

            if (topN < 1) topN = 1;
            if (topN > entries.Count) topN = entries.Count;

            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            sb.Append("JobGiver detail V0.4.8: active=").Append(sessionActive)
                .Append(", patchedMethods=").Append(patchedMethods)
                .Append(", patchFailures=").Append(patchFailures)
                .Append(", outerCalls=").Append(totalJobPackages)
                .Append('/').Append(targetJobPackages)
                .Append(", slowPackages>=64ms=").Append(slowJobPackages)
                .Append(", phaseSamples=").Append(totalSamples)
                .Append(", tracked=").Append(entries.Count)
                .Append(", slowTracesKept=").Append(SlowTraces.Count);

            for (int i = 0; i < topN; i++)
            {
                Entry entry = entries[i];
                Stat stat = entry.Stat;
                double totalMs = stat.TotalTicks * 1000.0 / Stopwatch.Frequency;
                double avgMs = stat.Count <= 0 ? 0.0 : totalMs / stat.Count;
                double maxMs = stat.MaxTicks * 1000.0 / Stopwatch.Frequency;
                sb.Append("\n  #").Append(i + 1).Append(' ')
                    .Append(entry.Key.DefName).Append(" / ")
                    .Append(entry.Key.WorkerTypeName).Append(" [")
                    .Append(entry.Key.Phase).Append("]")
                    .Append(": calls=").Append(stat.Count)
                    .Append(", sampledTotalMs=").Append(totalMs.ToString("F1"))
                    .Append(", avgMs=").Append(avgMs.ToString("F3"))
                    .Append(", maxMs=").Append(maxMs.ToString("F3"))
                    .Append(", >=16ms=").Append(stat.Over16Ms)
                    .Append(", >=64ms=").Append(stat.Over64Ms)
                    .Append(", >=128ms=").Append(stat.Over128Ms);
            }

            for (int i = 0; i < SlowTraces.Count; i++)
            {
                SlowTrace trace = SlowTraces[i];
                sb.Append("\n  SLOW#").Append(i + 1)
                    .Append(": totalMs=").Append((trace.ElapsedTicks * 1000.0 / Stopwatch.Frequency).ToString("F3"))
                    .Append(", pawn=").Append(trace.Pawn)
                    .Append(", topInclusivePhases=");
                for (int p = 0; p < trace.Phases.Count; p++)
                {
                    if (p > 0) sb.Append(" | ");
                    PhaseEntry phase = trace.Phases[p];
                    sb.Append(phase.Name).Append('=')
                        .Append((phase.Ticks * 1000.0 / Stopwatch.Frequency).ToString("F3")).Append("ms");
                }
            }
            return sb.ToString();
        }

        private static void SaveSlowTrace(long elapsed)
        {
            List<PhaseEntry> phases = new List<PhaseEntry>();
            if (currentInclusivePhases != null)
            {
                foreach (KeyValuePair<string, long> pair in currentInclusivePhases)
                    phases.Add(new PhaseEntry(pair.Key, pair.Value));
                phases.Sort(delegate(PhaseEntry a, PhaseEntry b) { return b.Ticks.CompareTo(a.Ticks); });
                if (phases.Count > MaxPhasesPerSlowTrace)
                    phases.RemoveRange(MaxPhasesPerSlowTrace, phases.Count - MaxPhasesPerSlowTrace);
            }

            SlowTrace trace = new SlowTrace(elapsed, currentPawn ?? "<unknown>", phases);
            SlowTraces.Add(trace);
            SlowTraces.Sort(delegate(SlowTrace a, SlowTrace b) { return b.ElapsedTicks.CompareTo(a.ElapsedTicks); });
            if (SlowTraces.Count > MaxSlowTraces)
                SlowTraces.RemoveRange(MaxSlowTraces, SlowTraces.Count - MaxSlowTraces);
        }

        private struct ProfileKey : IEquatable<ProfileKey>
        {
            internal readonly WorkGiverDef Def;
            internal readonly Type WorkerType;
            internal readonly string Phase;

            internal ProfileKey(WorkGiverDef def, Type workerType, string phase)
            {
                Def = def;
                WorkerType = workerType;
                Phase = phase ?? "?";
            }

            internal string DefName { get { return Def == null ? "<no-def>" : Def.defName; } }
            internal string WorkerTypeName { get { return WorkerType == null ? "<null>" : WorkerType.FullName; } }

            public bool Equals(ProfileKey other)
            {
                return ReferenceEquals(Def, other.Def) && WorkerType == other.WorkerType && Phase == other.Phase;
            }

            public override bool Equals(object obj)
            {
                return obj is ProfileKey && Equals((ProfileKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Def == null ? 0 : Def.GetHashCode();
                    hash = hash * 397 ^ (WorkerType == null ? 0 : WorkerType.GetHashCode());
                    hash = hash * 397 ^ Phase.GetHashCode();
                    return hash;
                }
            }
        }

        private sealed class Stat
        {
            internal long Count;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long Over16Ms;
            internal long Over64Ms;
            internal long Over128Ms;
        }

        private struct Entry
        {
            internal readonly ProfileKey Key;
            internal readonly Stat Stat;
            internal Entry(ProfileKey key, Stat stat) { Key = key; Stat = stat; }
        }

        private struct PhaseEntry
        {
            internal readonly string Name;
            internal readonly long Ticks;
            internal PhaseEntry(string name, long ticks) { Name = name; Ticks = ticks; }
        }

        private sealed class SlowTrace
        {
            internal readonly long ElapsedTicks;
            internal readonly string Pawn;
            internal readonly List<PhaseEntry> Phases;
            internal SlowTrace(long elapsedTicks, string pawn, List<PhaseEntry> phases)
            {
                ElapsedTicks = elapsedTicks;
                Pawn = pawn;
                Phases = phases;
            }
        }
    }
}
