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
    // V0.4.19-JS1.1S4 Tail Rescue
    //
    // S1 remains the baseline: normal ThingRequest-backed searches with >=256 candidates use the
    // validated nearest-first + original-validator + live-CanReach fast path. S2/S3 showed that
    // predicting future slow packages from fanout or learned shapes costs too much on ordinary work.
    // S4 therefore does not predict. Small/medium searches remain Vanilla until the *current*
    // TryIssueJobPackage has already consumed 32ms. Only then do later >=16 ThingRequest searches,
    // plus explicit custom IEnumerable<Thing> sets, enter the same fast path as an emergency tail
    // rescue. Custom sets are enumerated exactly once by RimMT after rescue activates; source order
    // is retained as the distance-tie order, and the original validator/live CanReach remain final.
    internal static class JobGiverSlowSearch0419S
    {
        internal const string FeatureId = "ai.jobSlowSearch";

        private const int LargeSearchThreshold = 256;
        private const int TailMinSourceCount = 16;
        private const int TailRescueThresholdMs = 32;
        private const int MaxSourceCount = 16384;
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);

        private const int KindStaticLarge = 0;
        private const int KindTailThing = 1;
        private const int KindTailCustom = 2;

        [ThreadStatic]
        private static Candidate[] candidateScratch;

        [ThreadStatic]
        private static long lastTailScopeStart;

        [ThreadStatic]
        private static bool tailPackageCounted;

        private static volatile bool enabled = true;
        private static volatile bool patched;

        private static long observed;
        private static long inJobGiverScope;
        private static long tailChecks;
        private static long preTailBypass;
        private static long customPreTailBypass;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long unsupportedTraverseBypass;
        private static long invalidContextBypass;
        private static long sourceFaultBypass;
        private static long accelerated;
        private static long staticLargeAccelerated;
        private static long tailThingAccelerated;
        private static long tailCustomAccelerated;
        private static long acceleratedFound;
        private static long acceleratedNoResult;
        private static long tailPackages;
        private static long tailActivationTicks;
        private static long maxTailActivationTicks;
        private static long customEnumerations;
        private static long customElements;
        private static long maxCustomSource;
        private static long source0To15;
        private static long source16To31;
        private static long source32To63;
        private static long source64To127;
        private static long source128To255;
        private static long source256To383;
        private static long source384To511;
        private static long source512Plus;
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
                    Log.Message("[RimMT] V0.4.19-JS1.1S4 Tail Rescue installed on " + patchedCount +
                        " GenClosest.ClosestThingReachable overload(s). S1 >=" + LargeSearchThreshold +
                        " ThingRequest acceleration remains active; after a live JobPackage exceeds " + TailRescueThresholdMs +
                        "ms, later >=" + TailMinSourceCount + " ThingRequest searches and explicit custom enumerables may use the rescue path.");
                }
                else
                {
                    Log.Warning("[RimMT] V0.4.19-JS1.1S4 Tail Rescue found no compatible ClosestThingReachable overload; JS1.1R behavior remains unchanged.");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                patched = false;
                Log.Warning("[RimMT] V0.4.19-JS1.1S4 Tail Rescue patch failed; JS1.1R remains authoritative. " +
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

        // __0..__7 bind by position so RimWorld parameter-name changes do not affect the patch.
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

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L)
            {
                Interlocked.Increment(ref invalidContextBypass);
                return true;
            }

            // Custom sets were completely bypassed in S1. S4 still leaves them untouched during
            // ordinary packages. Once the package has already crossed the tail threshold, however,
            // RimMT enumerates the supplied sequence once and preserves its source order for ties.
            if (__7 != null)
            {
                Interlocked.Increment(ref tailChecks);
                long elapsedTicks = Stopwatch.GetTimestamp() - scopeStart;
                if (elapsedTicks < TailRescueThresholdTicks)
                {
                    Interlocked.Increment(ref preTailBypass);
                    Interlocked.Increment(ref customPreTailBypass);
                    return true;
                }

                MarkTailPackage(scopeStart, elapsedTicks);
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, ref __result);
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
            RecordSourceBucket(count);

            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return true;
            }

            if (count >= LargeSearchThreshold)
                return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, KindStaticLarge, ref __result);

            if (count < TailMinSourceCount)
            {
                Interlocked.Increment(ref smallBypass);
                return true;
            }

            Interlocked.Increment(ref tailChecks);
            long elapsed = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsed < TailRescueThresholdTicks)
            {
                Interlocked.Increment(ref preTailBypass);
                return true;
            }

            MarkTailPackage(scopeStart, elapsed);
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, KindTailThing, ref __result);
        }

        private static void MarkTailPackage(long scopeStart, long elapsedTicks)
        {
            if (lastTailScopeStart != scopeStart)
            {
                lastTailScopeStart = scopeStart;
                tailPackageCounted = false;
            }

            if (tailPackageCounted)
                return;

            tailPackageCounted = true;
            Interlocked.Increment(ref tailPackages);
            Interlocked.Add(ref tailActivationTicks, elapsedTicks);
            UpdateMax(ref maxTailActivationTicks, elapsedTicks);
        }

        private static bool TryAccelerateList(
            List<Thing> source,
            int count,
            IntVec3 root,
            Map map,
            PathEndMode endMode,
            TraverseParms traverseParms,
            float maxDistance,
            Predicate<Thing> validator,
            int kind,
            ref Thing result)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] candidates = EnsureScratch(count, 0);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                int kept = 0;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != map)
                        continue;

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        continue;

                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > maxDistanceSquared)
                        continue;

                    candidates[kept++] = new Candidate(thing, distanceSquared, i);
                }

                return RunCandidates(candidates, kept, count, root, map, endMode, traverseParms, validator, kind, started, ref result);
            }
            catch (Exception ex)
            {
                return AccelerationFailure(ex);
            }
        }

        private static bool TryAccelerateCustom(
            IEnumerable<Thing> source,
            IntVec3 root,
            Map map,
            PathEndMode endMode,
            TraverseParms traverseParms,
            float maxDistance,
            Predicate<Thing> validator,
            ref Thing result)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] candidates = EnsureScratch(256, 0);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                int kept = 0;
                int sourceCount = 0;

                foreach (Thing thing in source)
                {
                    int sourceIndex = sourceCount++;
                    if (thing == null || !thing.Spawned || thing.Map != map)
                        continue;

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        continue;

                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distanceSquared = dx * dx + dz * dz;
                    if (distanceSquared > maxDistanceSquared)
                        continue;

                    if (kept >= candidates.Length)
                        candidates = EnsureScratch(kept + 1, kept);
                    candidates[kept++] = new Candidate(thing, distanceSquared, sourceIndex);
                }

                RecordSourceBucket(sourceCount);
                Interlocked.Increment(ref customEnumerations);
                Interlocked.Add(ref customElements, sourceCount);
                UpdateMax(ref maxCustomSource, sourceCount);

                return RunCandidates(candidates, kept, sourceCount, root, map, endMode, traverseParms, validator, KindTailCustom, started, ref result);
            }
            catch (Exception ex)
            {
                return AccelerationFailure(ex);
            }
        }

        private static bool RunCandidates(
            Candidate[] candidates,
            int kept,
            int sourceCount,
            IntVec3 root,
            Map map,
            PathEndMode endMode,
            TraverseParms traverseParms,
            Predicate<Thing> validator,
            int kind,
            long started,
            ref Thing result)
        {
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

                if (validator != null)
                {
                    localValidatorCalls++;
                    if (!validator(thing))
                    {
                        localValidatorRejected++;
                        continue;
                    }
                }

                localReachChecks++;
                if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                {
                    localReachRejected++;
                    continue;
                }

                result = thing;
                RecordCompletedAcceleration(kind, true, sourceCount, kept, examined,
                    localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, started);
                return false;
            }

            result = null;
            RecordCompletedAcceleration(kind, false, sourceCount, kept, examined,
                localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, started);
            return false;
        }

        private static void RecordCompletedAcceleration(
            int kind,
            bool found,
            int source,
            int kept,
            int examined,
            int validators,
            int validatorFalse,
            int reaches,
            int reachFalse,
            long started)
        {
            Interlocked.Increment(ref accelerated);
            if (kind == KindStaticLarge)
                Interlocked.Increment(ref staticLargeAccelerated);
            else if (kind == KindTailThing)
                Interlocked.Increment(ref tailThingAccelerated);
            else
                Interlocked.Increment(ref tailCustomAccelerated);

            if (found)
                Interlocked.Increment(ref acceleratedFound);
            else
                Interlocked.Increment(ref acceleratedNoResult);

            Interlocked.Add(ref sourceCandidates, source);
            Interlocked.Add(ref keptCandidates, kept);
            Interlocked.Add(ref examinedCandidates, examined);
            Interlocked.Add(ref validatorCalls, validators);
            Interlocked.Add(ref validatorRejected, validatorFalse);
            Interlocked.Add(ref reachChecks, reaches);
            Interlocked.Add(ref reachRejected, reachFalse);
            UpdateMax(ref maxSource, source);
            UpdateMax(ref maxExamined, examined);

            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref acceleratedTicks, elapsed);
            UpdateMax(ref maxAcceleratedTicks, elapsed);
        }

        private static bool AccelerationFailure(Exception ex)
        {
            Interlocked.Increment(ref failures);
            if (Interlocked.Read(ref failures) <= 8)
            {
                Log.Warning("[RimMT] V0.4.19-JS1.1S4 Tail Rescue accelerated search failed; this call falls back to Vanilla. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
            return true;
        }

        private static void RecordSourceBucket(int count)
        {
            if (count <= 15)
                Interlocked.Increment(ref source0To15);
            else if (count <= 31)
                Interlocked.Increment(ref source16To31);
            else if (count <= 63)
                Interlocked.Increment(ref source32To63);
            else if (count <= 127)
                Interlocked.Increment(ref source64To127);
            else if (count <= 255)
                Interlocked.Increment(ref source128To255);
            else if (count <= 383)
                Interlocked.Increment(ref source256To383);
            else if (count <= 511)
                Interlocked.Increment(ref source384To511);
            else
                Interlocked.Increment(ref source512Plus);
        }

        private static Candidate[] EnsureScratch(int required, int preserveCount)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required)
                return current;

            int capacity = 256;
            if (current != null && current.Length > capacity)
                capacity = current.Length;
            while (capacity < required && capacity < 65536)
                capacity <<= 1;
            if (capacity < required)
                capacity = required;

            Candidate[] next = new Candidate[capacity];
            if (current != null && preserveCount > 0)
                Array.Copy(current, next, Math.Min(preserveCount, current.Length));
            candidateScratch = next;
            return next;
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
            long packages = Interlocked.Read(ref tailPackages);
            double avgSource = calls == 0 ? 0.0 : source / (double)calls;
            double avgExamined = calls == 0 ? 0.0 : examined / (double)calls;
            double avgMs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref acceleratedTicks) * 1000.0 / Stopwatch.Frequency) / calls;
            double maxMs = Interlocked.Read(ref maxAcceleratedTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSortUs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / calls;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;
            double avgActivationMs = packages == 0 ? 0.0 :
                (Interlocked.Read(ref tailActivationTicks) * 1000.0 / Stopwatch.Frequency) / packages;
            double maxActivationMs = Interlocked.Read(ref maxTailActivationTicks) * 1000.0 / Stopwatch.Frequency;

            return "JobGiver slow-search JS1.1S4 Tail Rescue: patched=" + patched +
                ", enabled=" + enabled +
                ", staticThreshold=" + LargeSearchThreshold +
                ", tailThresholdMs=" + TailRescueThresholdMs +
                ", tailMinSource=" + TailMinSourceCount +
                ", observed=" + Interlocked.Read(ref observed) +
                ", inScope=" + Interlocked.Read(ref inJobGiverScope) +
                ", tailChecks=" + Interlocked.Read(ref tailChecks) +
                ", preTailBypass=" + Interlocked.Read(ref preTailBypass) +
                ", customPreTailBypass=" + Interlocked.Read(ref customPreTailBypass) +
                ", tailPackages=" + packages +
                ", avg/maxTailActivationMs=" + avgActivationMs.ToString("F2") + "/" + maxActivationMs.ToString("F2") +
                ", accelerated=" + calls +
                ", static/tailThing/tailCustom=" + Interlocked.Read(ref staticLargeAccelerated) + "/" +
                    Interlocked.Read(ref tailThingAccelerated) + "/" + Interlocked.Read(ref tailCustomAccelerated) +
                ", found/noResult=" + Interlocked.Read(ref acceleratedFound) + "/" + Interlocked.Read(ref acceleratedNoResult) +
                ", customEnumerations=" + Interlocked.Read(ref customEnumerations) +
                ", customElements=" + Interlocked.Read(ref customElements) +
                ", maxCustomSource=" + Interlocked.Read(ref maxCustomSource) +
                ", sourceBuckets0-15/16-31/32-63/64-127/128-255/256-383/384-511/512+=" +
                    Interlocked.Read(ref source0To15) + "/" +
                    Interlocked.Read(ref source16To31) + "/" +
                    Interlocked.Read(ref source32To63) + "/" +
                    Interlocked.Read(ref source64To127) + "/" +
                    Interlocked.Read(ref source128To255) + "/" +
                    Interlocked.Read(ref source256To383) + "/" +
                    Interlocked.Read(ref source384To511) + "/" +
                    Interlocked.Read(ref source512Plus) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
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
                ". Before 32ms, S4 is S1. After the current JobPackage is already in the tail, later small/medium ThingRequest and custom enumerable searches can be rescued; original validator and live CanReach remain authoritative.";
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