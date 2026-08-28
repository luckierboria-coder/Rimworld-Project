using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    // V0.4.18.1: attack the measured JobGiver -> GenClosest.ClosestThing_Global hotspot directly.
    //
    // Vanilla ClosestThing_Global (when priorityGetter == null) only evaluates the validator for
    // a candidate whose squared distance is better than the best valid distance seen so far.
    // Therefore a stable nearest-first ordering is result-equivalent for a pure predicate and can
    // dramatically reduce expensive Reachability/HasJob validator calls after the first valid
    // nearby target is found. JobGiver validators are predicates by contract; RimMT keeps this
    // optimization scoped strictly inside JobGiver_Work.TryIssueJobPackage to avoid changing
    // validator call order for unrelated gameplay systems.
    //
    // Unspawned candidates and candidates outside maxDistance are omitted because Vanilla skips
    // them before invoking validator/priorityGetter. Equal-distance candidates retain source order.
    // No Verse/Unity objects are touched from worker threads and no main-thread worker wait exists.
    internal static class JobGiverGlobalNearest04181
    {
        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 16384;

        [ThreadStatic]
        private static int jobGiverDepth;

        private static volatile bool globalPatched;
        private static volatile bool reachablePatched;

        private static long jobGiverScopes;
        private static long globalObserved;
        private static long reachableObserved;
        private static long outsideScope;
        private static long priorityBypass;
        private static long nonListBypass;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long nullElementBypass;
        private static long typeBypass;
        private static long invalidPositionBypass;
        private static long reordered;
        private static long reorderedReachable;
        private static long sourceCandidates;
        private static long keptCandidates;
        private static long skippedUnspawned;
        private static long skippedOutOfRange;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long maxSourceCount;
        private static long failures;

        internal static bool InJobGiverScope
        {
            get { return jobGiverDepth > 0; }
        }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodBase jobGiver = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (jobGiver == null)
                {
                    Log.Warning("[RimMT] V0.4.18.1 JobGiver nearest-first unavailable: JobGiver_Work.TryIssueJobPackage not found.");
                    return;
                }

                HarmonyMethod enter = new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverPrefix));
                enter.priority = Priority.First;
                HarmonyMethod exit = new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverFinalizer));
                exit.priority = Priority.Last;
                harmony.Patch(jobGiver, prefix: enter, finalizer: exit);

                MethodBase[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodBase method = methods[i];
                    if (method == null)
                        continue;

                    ParameterInfo[] p = method.GetParameters();
                    if (string.Equals(method.Name, "ClosestThing_Global", StringComparison.Ordinal) && p.Length == 5 && p[0].ParameterType == typeof(IntVec3))
                    {
                        HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(GlobalPrefix));
                        prefix.priority = Priority.First + 75;
                        harmony.Patch(method, prefix: prefix);
                        globalPatched = true;
                    }
                    else if (string.Equals(method.Name, "ClosestThing_Global_Reachable", StringComparison.Ordinal) && p.Length == 8 && p[0].ParameterType == typeof(IntVec3))
                    {
                        HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(GlobalReachablePrefix));
                        prefix.priority = Priority.First + 75;
                        harmony.Patch(method, prefix: prefix);
                        reachablePatched = true;
                    }
                }

                Log.Message("[RimMT] V0.4.18.1 JobGiver global nearest-first active: ClosestThing_Global=" + globalPatched +
                    ", ClosestThing_Global_Reachable=" + reachablePatched +
                    ". Only priorityGetter=null calls inside JobGiver_Work are reordered; Vanilla validator/Reachability/final choice remains authoritative.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] V0.4.18.1 JobGiver global nearest-first patch failed; Vanilla search order remains. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobGiverPrefix()
        {
            jobGiverDepth++;
            Interlocked.Increment(ref jobGiverScopes);
        }

        public static Exception JobGiverFinalizer(Exception __exception)
        {
            if (jobGiverDepth > 0)
                jobGiverDepth--;
            return __exception;
        }

        public static void GlobalPrefix(object[] __args)
        {
            Interlocked.Increment(ref globalObserved);
            if (__args == null || __args.Length < 5)
                return;
            TryReorder(__args, 0, 1, 2, 4, false);
        }

        public static void GlobalReachablePrefix(object[] __args)
        {
            Interlocked.Increment(ref reachableObserved);
            if (__args == null || __args.Length < 8)
                return;
            TryReorder(__args, 0, 2, 5, 7, true);
        }

        private static void TryReorder(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex, bool reachable)
        {
            if (!InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
            {
                Interlocked.Increment(ref outsideScope);
                return;
            }

            // Priority search must inspect every candidate because a farther target may have a
            // higher priority. Distance-only ordering cannot safely prune that case.
            if (args[priorityIndex] != null)
            {
                Interlocked.Increment(ref priorityBypass);
                return;
            }

            IList source = args[setIndex] as IList;
            if (source == null)
            {
                Interlocked.Increment(ref nonListBypass);
                return;
            }

            int count = source.Count;
            if (count < MinSourceCount)
            {
                Interlocked.Increment(ref smallBypass);
                return;
            }
            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return;
            }

            try
            {
                IntVec3 center = (IntVec3)args[centerIndex];
                float maxDistance = Convert.ToSingle(args[maxDistanceIndex]);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                Candidate[] candidates = new Candidate[count];
                int kept = 0;
                long localUnspawned = 0;
                long localOutOfRange = 0;

                for (int i = 0; i < count; i++)
                {
                    object raw = source[i];
                    if (raw == null)
                    {
                        Interlocked.Increment(ref nullElementBypass);
                        return; // Preserve Vanilla's potential null failure semantics.
                    }

                    Thing thing = raw as Thing;
                    if (thing == null)
                    {
                        Interlocked.Increment(ref typeBypass);
                        return;
                    }

                    // Vanilla checks Spawned before distance/validator. Omitting this candidate
                    // is therefore semantically equivalent for the supported no-priority path.
                    if (!thing.Spawned)
                    {
                        localUnspawned++;
                        continue;
                    }

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                    {
                        Interlocked.Increment(ref invalidPositionBypass);
                        return;
                    }

                    long dx = (long)pos.x - center.x;
                    long dz = (long)pos.z - center.z;
                    long distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > maxDistanceSquared)
                    {
                        localOutOfRange++;
                        continue;
                    }

                    candidates[kept++] = new Candidate(thing, distanceSquared, i);
                }

                long started = Stopwatch.GetTimestamp();
                if (kept > 1)
                    Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long elapsed = Stopwatch.GetTimestamp() - started;

                Thing[] ordered = new Thing[kept];
                for (int i = 0; i < kept; i++)
                    ordered[i] = candidates[i].Thing;

                args[setIndex] = ordered;
                Interlocked.Increment(ref reordered);
                if (reachable)
                    Interlocked.Increment(ref reorderedReachable);
                Interlocked.Add(ref sourceCandidates, count);
                Interlocked.Add(ref keptCandidates, kept);
                Interlocked.Add(ref skippedUnspawned, localUnspawned);
                Interlocked.Add(ref skippedOutOfRange, localOutOfRange);
                Interlocked.Add(ref sortTicks, elapsed);
                UpdateMax(ref maxSortTicks, elapsed);
                UpdateMax(ref maxSourceCount, count);
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] V0.4.18.1 nearest-first reorder failed for one JobGiver query; Vanilla continues with the original arguments when possible. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen)
                    break;
            }
        }

        internal static string Summary()
        {
            long calls = Interlocked.Read(ref reordered);
            long source = Interlocked.Read(ref sourceCandidates);
            long kept = Interlocked.Read(ref keptCandidates);
            double avgSource = calls == 0 ? 0.0 : source / (double)calls;
            double avgKept = calls == 0 ? 0.0 : kept / (double)calls;
            double avgSortUs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / calls;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "JobGiver global nearest V0.4.18.1: patched(global/reachable)=" + globalPatched + "/" + reachablePatched +
                ", jobGiverScopes=" + Interlocked.Read(ref jobGiverScopes) +
                ", observed(global/reachable)=" + Interlocked.Read(ref globalObserved) + "/" + Interlocked.Read(ref reachableObserved) +
                ", reordered=" + calls +
                ", reorderedReachable=" + Interlocked.Read(ref reorderedReachable) +
                ", outsideScope=" + Interlocked.Read(ref outsideScope) +
                ", priorityBypass=" + Interlocked.Read(ref priorityBypass) +
                ", nonListBypass=" + Interlocked.Read(ref nonListBypass) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
                ", nullElementBypass=" + Interlocked.Read(ref nullElementBypass) +
                ", typeBypass=" + Interlocked.Read(ref typeBypass) +
                ", invalidPositionBypass=" + Interlocked.Read(ref invalidPositionBypass) +
                ", sourceCandidates=" + source +
                ", keptCandidates=" + kept +
                ", skippedUnspawned=" + Interlocked.Read(ref skippedUnspawned) +
                ", skippedOutOfRange=" + Interlocked.Read(ref skippedOutOfRange) +
                ", avgSource=" + avgSource.ToString("F1") +
                ", avgKept=" + avgKept.ToString("F1") +
                ", maxSource=" + Interlocked.Read(ref maxSourceCount) +
                ", avgSortUs=" + avgSortUs.ToString("F2") +
                ", maxSortUs=" + maxSortUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Scoped to JobGiver_Work and priorityGetter=null; nearest result/tie order remain Vanilla-equivalent for predicate-style validators.";
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;

            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();

            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
