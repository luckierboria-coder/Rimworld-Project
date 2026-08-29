using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JS1.1S: selective accelerator for the measured JobGiver tail-latency path.
    //
    // JD1 showed the dominant slow-package stack is:
    // JobGiver_Work -> GenClosest.ClosestThingReachable -> RegionTraverser.BreadthFirstTraverse.
    // JS1.1R already makes ordinary packages fast, so this module does NOT touch small searches.
    // Only JobGiver-owned ClosestThingReachable calls whose live ThingRequest source has at least
    // LargeSearchThreshold candidates are replaced by a stable nearest-first candidate scan.
    // Each candidate still runs the original validator on the main thread and the final reachability
    // decision is live map.reachability.CanReach. No Job, reservation or mutable Verse state is cached.
    //
    // This is intentionally performance-first. It changes the search algorithm for the large-source
    // tail only; normal JobPackage behavior remains exactly JS1.1R.
    internal static class JobGiverSlowSearch0419S
    {
        internal const string FeatureId = "ai.jobSlowSearch";

        private const int LargeSearchThreshold = 512;
        private const int MaxSourceCount = 16384;

        [ThreadStatic]
        private static Candidate[] candidateScratch;

        private static volatile bool enabled = true;
        private static volatile bool patched;

        private static long observed;
        private static long inJobGiverScope;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long customSetBypass;
        private static long unsupportedTraverseBypass;
        private static long invalidContextBypass;
        private static long sourceFaultBypass;
        private static long accelerated;
        private static long acceleratedFound;
        private static long acceleratedNoResult;
        private static long source512To1023;
        private static long source1024Plus;
        private static long sourceCandidates;
        private static long keptCandidates;
        private static long examinedCandidates;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long acceleratedTicks;
        private static long maxAcceleratedTicks;
        private static long maxSource;
        private static long maxExamined;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int patchedCount = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method))
                        continue;

                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverSlowSearch0419S), nameof(Prefix));
                    prefix.priority = Priority.First + 100;
                    harmony.Patch(method, prefix: prefix);
                    patchedCount++;
                }

                patched = patchedCount > 0;
                if (patched)
                {
                    Log.Message("[RimMT] V0.4.19-JS1.1S selective slow-search accelerator installed on " + patchedCount +
                        " GenClosest.ClosestThingReachable overload(s). Only JobGiver searches with >=" + LargeSearchThreshold +
                        " live ThingRequest candidates are replaced; validator and live CanReach remain main-thread authoritative.");
                }
                else
                {
                    Log.Warning("[RimMT] V0.4.19-JS1.1S slow-search accelerator found no compatible ClosestThingReachable overload; JS1.1R behavior remains unchanged.");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                patched = false;
                Log.Warning("[RimMT] V0.4.19-JS1.1S slow-search patch failed; JS1.1R remains authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value)
        {
            enabled = value;
        }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) ||
                !string.Equals(method.Name, "ClosestThingReachable", StringComparison.Ordinal))
                return false;

            ParameterInfo[] p = method.GetParameters();
            if (p.Length < 8)
                return false;

            return p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(Map) &&
                p[2].ParameterType == typeof(ThingRequest) &&
                p[3].ParameterType == typeof(PathEndMode) &&
                p[4].ParameterType == typeof(TraverseParms) &&
                p[5].ParameterType == typeof(float) &&
                p[6].ParameterType == typeof(Predicate<Thing>) &&
                typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        // __0..__7 deliberately bind by position so the patch does not depend on RimWorld parameter names.
        public static bool Prefix(
            IntVec3 __0,
            Map __1,
            ThingRequest __2,
            PathEndMode __3,
            TraverseParms __4,
            float __5,
            Predicate<Thing> __6,
            IEnumerable<Thing> __7,
            ref Thing __result)
        {
            Interlocked.Increment(ref observed);

            if (!enabled || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            Interlocked.Increment(ref inJobGiverScope);

            // Explicit custom sets may have ordering/membership semantics supplied by another mod.
            // Keep those on Vanilla; the selective path is for normal ThingRequest-backed scans.
            if (__7 != null)
            {
                Interlocked.Increment(ref customSetBypass);
                return true;
            }

            Map map = __1;
            Pawn pawn = __4.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
            {
                Interlocked.Increment(ref invalidContextBypass);
                return true;
            }

            TraverseMode mode = __4.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
            {
                Interlocked.Increment(ref unsupportedTraverseBypass);
                return true;
            }

            List<Thing> source;
            try
            {
                source = map.listerThings.ThingsMatching(__2);
            }
            catch
            {
                Interlocked.Increment(ref sourceFaultBypass);
                return true;
            }

            if (source == null)
            {
                Interlocked.Increment(ref sourceFaultBypass);
                return true;
            }

            int count = source.Count;
            if (count < LargeSearchThreshold)
            {
                Interlocked.Increment(ref smallBypass);
                return true;
            }
            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return true;
            }

            if (count < 1024)
                Interlocked.Increment(ref source512To1023);
            else
                Interlocked.Increment(ref source1024Plus);

            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] candidates = EnsureScratch(count);
                double maxDistanceSquared = (double)__5 * __5;
                int kept = 0;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != map)
                        continue;

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        continue;

                    long dx = (long)pos.x - __0.x;
                    long dz = (long)pos.z - __0.z;
                    long distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > maxDistanceSquared)
                        continue;

                    candidates[kept++] = new Candidate(thing, distanceSquared, i);
                }

                long sortStarted = Stopwatch.GetTimestamp();
                if (kept > 1)
                    Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long sortElapsed = Stopwatch.GetTimestamp() - sortStarted;
                Interlocked.Add(ref sortTicks, sortElapsed);
                UpdateMax(ref maxSortTicks, sortElapsed);

                int examined = 0;
                int localValidatorCalls = 0;
                int localValidatorRejected = 0;
                int localReachChecks = 0;
                int localReachRejected = 0;

                for (int i = 0; i < kept; i++)
                {
                    Thing thing = candidates[i].Thing;
                    examined++;

                    if (__6 != null)
                    {
                        localValidatorCalls++;
                        if (!__6(thing))
                        {
                            localValidatorRejected++;
                            continue;
                        }
                    }

                    localReachChecks++;
                    if (!map.reachability.CanReach(__0, new LocalTargetInfo(thing), __3, __4))
                    {
                        localReachRejected++;
                        continue;
                    }

                    __result = thing;
                    Interlocked.Increment(ref accelerated);
                    Interlocked.Increment(ref acceleratedFound);
                    CommitCounters(count, kept, examined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected);
                    RecordElapsed(started);
                    return false;
                }

                __result = null;
                Interlocked.Increment(ref accelerated);
                Interlocked.Increment(ref acceleratedNoResult);
                CommitCounters(count, kept, examined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected);
                RecordElapsed(started);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 8)
                {
                    Log.Warning("[RimMT] V0.4.19-JS1.1S accelerated large search failed; this call falls back to Vanilla. " +
                        ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }
        }

        private static Candidate[] EnsureScratch(int required)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required)
                return current;

            int capacity = 512;
            while (capacity < required && capacity < MaxSourceCount)
                capacity <<= 1;
            if (capacity < required)
                capacity = required;

            current = new Candidate[capacity];
            candidateScratch = current;
            return current;
        }

        private static void CommitCounters(int source, int kept, int examined, int validators, int validatorFalse, int reaches, int reachFalse)
        {
            Interlocked.Add(ref sourceCandidates, source);
            Interlocked.Add(ref keptCandidates, kept);
            Interlocked.Add(ref examinedCandidates, examined);
            Interlocked.Add(ref validatorCalls, validators);
            Interlocked.Add(ref validatorRejected, validatorFalse);
            Interlocked.Add(ref reachChecks, reaches);
            Interlocked.Add(ref reachRejected, reachFalse);
            UpdateMax(ref maxSource, source);
            UpdateMax(ref maxExamined, examined);
        }

        private static void RecordElapsed(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref acceleratedTicks, elapsed);
            UpdateMax(ref maxAcceleratedTicks, elapsed);
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
            long calls = Interlocked.Read(ref accelerated);
            long source = Interlocked.Read(ref sourceCandidates);
            long examined = Interlocked.Read(ref examinedCandidates);
            double avgSource = calls == 0 ? 0.0 : source / (double)calls;
            double avgExamined = calls == 0 ? 0.0 : examined / (double)calls;
            double avgMs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref acceleratedTicks) * 1000.0 / Stopwatch.Frequency) / calls;
            double maxMs = Interlocked.Read(ref maxAcceleratedTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSortUs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / calls;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "JobGiver slow-search JS1.1S: patched=" + patched +
                ", enabled=" + enabled +
                ", threshold=" + LargeSearchThreshold +
                ", observed=" + Interlocked.Read(ref observed) +
                ", inScope=" + Interlocked.Read(ref inJobGiverScope) +
                ", accelerated=" + calls +
                ", found/noResult=" + Interlocked.Read(ref acceleratedFound) + "/" + Interlocked.Read(ref acceleratedNoResult) +
                ", source512-1023/source1024+=" + Interlocked.Read(ref source512To1023) + "/" + Interlocked.Read(ref source1024Plus) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
                ", customSetBypass=" + Interlocked.Read(ref customSetBypass) +
                ", unsupportedTraverseBypass=" + Interlocked.Read(ref unsupportedTraverseBypass) +
                ", invalidContextBypass=" + Interlocked.Read(ref invalidContextBypass) +
                ", sourceFaultBypass=" + Interlocked.Read(ref sourceFaultBypass) +
                ", sourceCandidates=" + source +
                ", keptCandidates=" + Interlocked.Read(ref keptCandidates) +
                ", examinedCandidates=" + examined +
                ", avgSource=" + avgSource.ToString("F1") +
                ", avgExamined=" + avgExamined.ToString("F1") +
                ", maxSource=" + Interlocked.Read(ref maxSource) +
                ", maxExamined=" + Interlocked.Read(ref maxExamined) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", validatorRejected=" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", reachRejected=" + Interlocked.Read(ref reachRejected) +
                ", avgAcceleratedMs=" + avgMs.ToString("F3") +
                ", maxAcceleratedMs=" + maxMs.ToString("F3") +
                ", avgSortUs=" + avgSortUs.ToString("F2") +
                ", maxSortUs=" + maxSortUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Large ThingRequest-backed JobGiver searches use stable nearest-first live candidates + original validator + live CanReach; small searches remain JS1.1R/Vanilla.";
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
