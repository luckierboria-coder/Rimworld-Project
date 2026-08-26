using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using RimWorld;

namespace RimMT
{
    internal static class WorkGiverProfiler
    {
        private const int SampleEveryJobPackages = 32;
        private const int InitialDetailedPackages = 4;
        private const int SlowBurstPackages = 8;

        private static readonly Dictionary<ProfileKey, Stat> Stats = new Dictionary<ProfileKey, Stat>();
        private static readonly long Threshold16Ticks = Math.Max(1L, Stopwatch.Frequency * 16L / 1000L);
        private static readonly long Threshold64Ticks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);
        private static readonly long Threshold128Ticks = Math.Max(1L, Stopwatch.Frequency * 128L / 1000L);

        private static long totalSamples;
        private static long totalJobPackages;
        private static long sampledJobPackages;
        private static long slowJobPackages;
        private static int burstRemaining;
        private static int patchedMethods;
        private static int patchFailures;

        [ThreadStatic]
        private static int jobPackageDepth;

        [ThreadStatic]
        private static bool captureDetail;

        internal struct JobPackageScope
        {
            internal long Started;
            internal bool Entered;
            internal bool Outermost;
        }

        internal static JobPackageScope BeginJobPackage()
        {
            JobPackageScope state = default(JobPackageScope);
            if (!FeatureGate.IsEnabled("diagnostics.jobGiverDetail") || !RimMTThreadGuard.IsMainThread)
                return state;

            state.Entered = true;
            state.Outermost = jobPackageDepth == 0;
            jobPackageDepth++;

            if (!state.Outermost)
                return state;

            state.Started = Stopwatch.GetTimestamp();
            long sequence = ++totalJobPackages;
            bool sample = sequence <= InitialDetailedPackages || burstRemaining > 0 || (sequence % SampleEveryJobPackages) == 0;
            if (burstRemaining > 0)
                burstRemaining--;

            captureDetail = sample;
            if (sample)
                sampledJobPackages++;
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
                    if (burstRemaining < SlowBurstPackages)
                        burstRemaining = SlowBurstPackages;
                }
            }

            if (jobPackageDepth > 0)
                jobPackageDepth--;
            if (jobPackageDepth == 0)
                captureDetail = false;
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
        }

        internal static void NotePatchedMethod()
        {
            patchedMethods++;
        }

        internal static void NotePatchFailure()
        {
            patchFailures++;
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
            sb.Append("JobGiver detail V0.4.5.1: patchedMethods=").Append(patchedMethods)
                .Append(", patchFailures=").Append(patchFailures)
                .Append(", outerCalls=").Append(totalJobPackages)
                .Append(", sampledPackages=").Append(sampledJobPackages)
                .Append(", slowPackages>=64ms=").Append(slowJobPackages)
                .Append(", sampleEvery=").Append(SampleEveryJobPackages)
                .Append(", burstAfterSlow=").Append(SlowBurstPackages)
                .Append(", phaseSamples=").Append(totalSamples)
                .Append(", tracked=").Append(entries.Count);

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
            return sb.ToString();
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
    }
}
