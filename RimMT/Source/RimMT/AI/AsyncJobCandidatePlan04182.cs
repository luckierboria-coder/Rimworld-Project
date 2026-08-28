using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    // V0.4.18.2: aggressive no-wait candidate-plan offload.
    //
    // The expensive JobGiver path repeatedly feeds large static Thing lists into
    // GenClosest. V0.4.18.1 proved stable nearest-first ordering but still performed
    // the capture/sort synchronously on the main thread. This layer moves the sort and
    // range pruning onto RimMT workers and reuses the published order on later identical
    // source/root queries.
    //
    // Safety contract:
    // - main thread captures Thing references + integer positions only;
    // - workers never dereference Thing/Map/Unity state;
    // - main thread NEVER waits for a plan;
    // - before a published plan is consumed, exact source membership and every captured
    //   position are revalidated on the main thread;
    // - any mismatch discards the plan and falls back to the existing synchronous path.
    //
    // This intentionally mirrors the useful part of RimThreaded's philosophy (persistent
    // worker ownership of repeatable CPU work) without allowing worker-side gameplay state
    // commits.
    internal static class AsyncJobCandidatePlan04182
    {
        private const string FeatureId = "parallel.jobPartition";
        private const int MinSourceCount = 128;
        private const int MaxSourceCount = 8192;
        private const long MaxPlanAgeFrames = 240;

        private static readonly ConditionalWeakTable<object, PlanState> States =
            new ConditionalWeakTable<object, PlanState>();

        private static long observed;
        private static long hits;
        private static long misses;
        private static long stale;
        private static long captureRejected;
        private static long scheduled;
        private static long scheduleRejected;
        private static long published;
        private static long workerFailures;
        private static long sourceCandidates;
        private static long orderedCandidates;
        private static long captureTicks;
        private static long captureTicksMax;
        private static long validateTicks;
        private static long validateTicksMax;
        private static long workerTicks;
        private static long workerTicksMax;
        private static long patchFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                HarmonyMethod beforeGlobal = new HarmonyMethod(typeof(AsyncJobCandidatePlan04182), nameof(BeforeGlobalPrefix));
                beforeGlobal.priority = Priority.First;
                HarmonyMethod beforeReachable = new HarmonyMethod(typeof(AsyncJobCandidatePlan04182), nameof(BeforeGlobalReachablePrefix));
                beforeReachable.priority = Priority.First;

                harmony.Patch(
                    AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalPrefix)),
                    prefix: beforeGlobal);
                harmony.Patch(
                    AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalReachablePrefix)),
                    prefix: beforeReachable);

                Log.Message("[RimMT] V0.4.18.2 async JobGiver candidate plans active. Large static GenClosest source lists are captured on the main thread, range-sorted on workers, and reused only after exact membership/position revalidation. The main thread never waits for a worker.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] V0.4.18.2 async candidate-plan hook failed; V0.4.18.1 synchronous nearest-first remains active. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        // Target is RimMT's own GlobalPrefix(object[] __args). Returning false suppresses
        // V0.4.18.1 synchronous sorting only when a validated worker plan has already been
        // substituted into the GenClosest argument list.
        public static bool BeforeGlobalPrefix(object[] __0)
        {
            return !TryUseOrSchedule(__0, 0, 1, 2);
        }

        public static bool BeforeGlobalReachablePrefix(object[] __0)
        {
            return !TryUseOrSchedule(__0, 0, 2, 5);
        }

        private static bool TryUseOrSchedule(object[] args, int centerIndex, int setIndex, int maxDistanceIndex)
        {
            Interlocked.Increment(ref observed);

            if (args == null || args.Length <= Math.Max(setIndex, maxDistanceIndex) ||
                !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing)
                return false;

            IList source = args[setIndex] as IList;
            if (source == null)
                return false;

            int count = source.Count;
            if (count < MinSourceCount || count > MaxSourceCount)
                return false;

            IntVec3 center;
            float maxDistance;
            try
            {
                center = (IntVec3)args[centerIndex];
                maxDistance = Convert.ToSingle(args[maxDistanceIndex]);
            }
            catch
            {
                return false;
            }

            object sourceKey = source;
            PlanState state = States.GetValue(sourceKey, delegate(object ignored) { return new PlanState(); });
            CandidatePlan plan = Volatile.Read(ref state.Published);
            if (plan != null && plan.Count == count && plan.CenterX == center.x && plan.CenterZ == center.z &&
                plan.MaxDistance.Equals(maxDistance) && RimMTRuntime.MainThreadFrames - plan.CaptureFrame <= MaxPlanAgeFrames)
            {
                long validateStart = Stopwatch.GetTimestamp();
                bool valid = ValidatePlan(source, plan);
                RecordElapsed(ref validateTicks, ref validateTicksMax, validateStart);
                if (valid)
                {
                    args[setIndex] = plan.Ordered;
                    Interlocked.Increment(ref hits);
                    return true;
                }

                Volatile.Write(ref state.Published, null);
                Interlocked.Increment(ref stale);
            }
            else
            {
                Interlocked.Increment(ref misses);
            }

            TrySchedule(state, source, center, maxDistance, count);
            return false;
        }

        private static bool ValidatePlan(IList source, CandidatePlan plan)
        {
            if (source == null || plan == null || source.Count != plan.Count)
                return false;

            for (int i = 0; i < plan.Count; i++)
            {
                Thing thing;
                try { thing = source[i] as Thing; }
                catch { return false; }

                if (!ReferenceEquals(thing, plan.Members[i]) || thing == null || thing is Pawn || !thing.Spawned)
                    return false;

                IntVec3 pos = thing.Position;
                if (!pos.IsValid || pos.x != plan.Xs[i] || pos.z != plan.Zs[i])
                    return false;
            }
            return true;
        }

        private static void TrySchedule(PlanState state, IList source, IntVec3 center, float maxDistance, int count)
        {
            if (state == null || source == null || Volatile.Read(ref state.WorkerScheduled) != 0)
                return;
            if (Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) != 0)
                return;

            long captureStart = Stopwatch.GetTimestamp();
            Thing[] members = new Thing[count];
            int[] xs = new int[count];
            int[] zs = new int[count];

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || thing is Pawn || !thing.Spawned)
                    {
                        Interlocked.Increment(ref captureRejected);
                        Volatile.Write(ref state.WorkerScheduled, 0);
                        return;
                    }

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                    {
                        Interlocked.Increment(ref captureRejected);
                        Volatile.Write(ref state.WorkerScheduled, 0);
                        return;
                    }

                    members[i] = thing;
                    xs[i] = pos.x;
                    zs[i] = pos.z;
                }
            }
            catch
            {
                Interlocked.Increment(ref captureRejected);
                Volatile.Write(ref state.WorkerScheduled, 0);
                return;
            }
            finally
            {
                RecordElapsed(ref captureTicks, ref captureTicksMax, captureStart);
            }

            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref scheduleRejected);
                return;
            }

            long captureFrame = RimMTRuntime.MainThreadFrames;
            bool accepted = scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate
            {
                BuildAndPublish(state, members, xs, zs, center.x, center.z, maxDistance, captureFrame);
            });

            if (!accepted)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref scheduleRejected);
                return;
            }

            Interlocked.Increment(ref scheduled);
            Interlocked.Add(ref sourceCandidates, count);
        }

        private static void BuildAndPublish(
            PlanState state, Thing[] members, int[] xs, int[] zs,
            int centerX, int centerZ, float maxDistance, long captureFrame)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                Candidate[] candidates = new Candidate[members.Length];
                int kept = 0;

                // Worker rule: references are opaque tokens. Only captured primitive coordinates
                // are read here; Thing is never dereferenced off-thread.
                for (int i = 0; i < members.Length; i++)
                {
                    long dx = (long)xs[i] - centerX;
                    long dz = (long)zs[i] - centerZ;
                    long d2 = dx * dx + dz * dz;
                    if (d2 > maxDistanceSquared)
                        continue;
                    candidates[kept++] = new Candidate(i, d2);
                }

                if (kept > 1)
                    Array.Sort(candidates, 0, kept, CandidateComparer.Instance);

                Thing[] ordered = new Thing[kept];
                for (int i = 0; i < kept; i++)
                    ordered[i] = members[candidates[i].Index];

                CandidatePlan plan = new CandidatePlan(
                    members, xs, zs, ordered, centerX, centerZ, maxDistance, captureFrame);
                Volatile.Write(ref state.Published, plan);
                Interlocked.Increment(ref published);
                Interlocked.Add(ref orderedCandidates, kept);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
            finally
            {
                RecordElapsed(ref workerTicks, ref workerTicksMax, started);
                Volatile.Write(ref state.WorkerScheduled, 0);
            }
        }

        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            long seen;
            while (elapsed > (seen = Interlocked.Read(ref max)))
            {
                if (Interlocked.CompareExchange(ref max, elapsed, seen) == seen)
                    break;
            }
        }

        internal static string Summary()
        {
            long s = Interlocked.Read(ref scheduled);
            long p = Interlocked.Read(ref published);
            long h = Interlocked.Read(ref hits);
            double avgSource = s == 0 ? 0.0 : Interlocked.Read(ref sourceCandidates) / (double)s;
            double avgOrdered = p == 0 ? 0.0 : Interlocked.Read(ref orderedCandidates) / (double)p;
            double avgCaptureUs = s == 0 ? 0.0 :
                (Interlocked.Read(ref captureTicks) * 1000000.0 / Stopwatch.Frequency) / Math.Max(1L, s);
            double avgValidateUs = h == 0 ? 0.0 :
                (Interlocked.Read(ref validateTicks) * 1000000.0 / Stopwatch.Frequency) / Math.Max(1L, h + Interlocked.Read(ref stale));
            double avgWorkerUs = p == 0 ? 0.0 :
                (Interlocked.Read(ref workerTicks) * 1000000.0 / Stopwatch.Frequency) / Math.Max(1L, p);

            return "Async JobGiver candidate plan V0.4.18.2: observed=" + Interlocked.Read(ref observed) +
                ", hits=" + h +
                ", misses=" + Interlocked.Read(ref misses) +
                ", stale=" + Interlocked.Read(ref stale) +
                ", captureRejected=" + Interlocked.Read(ref captureRejected) +
                ", scheduled=" + s +
                ", scheduleRejected=" + Interlocked.Read(ref scheduleRejected) +
                ", published=" + p +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ", avgSource=" + avgSource.ToString("F1") +
                ", avgOrdered=" + avgOrdered.ToString("F1") +
                ", avgCaptureUs=" + avgCaptureUs.ToString("F2") +
                ", maxCaptureUs=" + (Interlocked.Read(ref captureTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgValidateUs=" + avgValidateUs.ToString("F2") +
                ", maxValidateUs=" + (Interlocked.Read(ref validateTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgWorkerUs=" + avgWorkerUs.ToString("F2") +
                ", maxWorkerUs=" + (Interlocked.Read(ref workerTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                ". No-wait policy: cold/stale requests fall through; only revalidated published plans suppress synchronous sorting.";
        }

        private sealed class PlanState
        {
            internal int WorkerScheduled;
            internal CandidatePlan Published;
        }

        private sealed class CandidatePlan
        {
            internal readonly Thing[] Members;
            internal readonly int[] Xs;
            internal readonly int[] Zs;
            internal readonly Thing[] Ordered;
            internal readonly int Count;
            internal readonly int CenterX;
            internal readonly int CenterZ;
            internal readonly float MaxDistance;
            internal readonly long CaptureFrame;

            internal CandidatePlan(
                Thing[] members, int[] xs, int[] zs, Thing[] ordered,
                int centerX, int centerZ, float maxDistance, long captureFrame)
            {
                Members = members;
                Xs = xs;
                Zs = zs;
                Ordered = ordered;
                Count = members.Length;
                CenterX = centerX;
                CenterZ = centerZ;
                MaxDistance = maxDistance;
                CaptureFrame = captureFrame;
            }
        }

        private struct Candidate
        {
            internal readonly int Index;
            internal readonly long DistanceSquared;

            internal Candidate(int index, long distanceSquared)
            {
                Index = index;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();

            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.Index.CompareTo(b.Index);
            }
        }
    }
}
