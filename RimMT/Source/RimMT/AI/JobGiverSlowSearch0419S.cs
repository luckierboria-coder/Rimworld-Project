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
    // V0.4.19-JS1.1S2 Fanout Search
    //
    // JD2 showed that the residual JobGiver tail is no longer dominated by one huge source.
    // Slow packages repeatedly issue ~100 ClosestThingReachable calls whose individual sources
    // are usually only a few dozen Things, including many explicit custom search sets. S2 keeps
    // the validated S1 >=256 fast path, adds a conservative IList-backed custom-set path, and
    // switches later small/medium searches to the same validator-first/live-CanReach algorithm
    // once the current JobPackage has demonstrated search fanout.
    //
    // All decisions are synchronous and main-thread only. The original validator remains
    // authoritative and reachability is checked against the live map. Unsupported source shapes,
    // traverse modes, invalid contexts, and failures fall back to Vanilla.
    internal static class JobGiverSlowSearch0419S
    {
        internal const string FeatureId = "ai.jobSlowSearch";

        private const int LargeSearchThreshold = 256;
        private const int CustomSetThreshold = 32;
        private const int FanoutCallThreshold = 24;
        private const long FanoutCandidateVolumeThreshold = 1024;
        private const int FanoutMinSource = 16;
        private const int MaxSourceCount = 16384;

        [ThreadStatic] private static Candidate[] candidateScratch;
        [ThreadStatic] private static int packageCtrCalls;
        [ThreadStatic] private static long packageCandidateVolume;
        [ThreadStatic] private static bool packageFanoutActive;
        [ThreadStatic] private static bool packageActive;

        private static volatile bool enabled = true;
        private static volatile bool patched;

        private static long observed;
        private static long inJobGiverScope;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long customSetUnsupportedBypass;
        private static long customSetSmallBypass;
        private static long unsupportedTraverseBypass;
        private static long invalidContextBypass;
        private static long sourceFaultBypass;
        private static long accelerated;
        private static long acceleratedFound;
        private static long acceleratedNoResult;
        private static long staticLargeAccelerated;
        private static long customSetAccelerated;
        private static long fanoutAccelerated;
        private static long fanoutPackages;
        private static long fanoutByCalls;
        private static long fanoutByVolume;
        private static long source0To15;
        private static long source16To31;
        private static long source32To63;
        private static long source64To127;
        private static long source128To255;
        private static long source256To383;
        private static long source384To511;
        private static long source512To767;
        private static long source768Plus;
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
        private static long maxPackageCtrCalls;
        private static long maxPackageCandidateVolume;
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
                    Log.Message("[RimMT] V0.4.19-JS1.1S2 Fanout Search installed on " + patchedCount +
                        " GenClosest.ClosestThingReachable overload(s). Static >=" + LargeSearchThreshold +
                        " ThingRequest sources retain the S1 fast path; IList-backed custom sets >=" + CustomSetThreshold +
                        " and later >=" + FanoutMinSource + " sources after JobPackage fanout (calls>=" + FanoutCallThreshold +
                        " or candidateVolume>=" + FanoutCandidateVolumeThreshold +
                        ") may use stable nearest-first validator/live-CanReach search.");
                }
                else
                {
                    Log.Warning("[RimMT] V0.4.19-JS1.1S2 found no compatible ClosestThingReachable overload; JS1.1R behavior remains unchanged.");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                patched = false;
                Log.Warning("[RimMT] V0.4.19-JS1.1S2 slow-search patch failed; JS1.1R remains authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value)
        {
            enabled = value;
        }

        internal static void BeginJobPackage()
        {
            packageCtrCalls = 0;
            packageCandidateVolume = 0L;
            packageFanoutActive = false;
            packageActive = true;
        }

        internal static void EndJobPackage()
        {
            if (!packageActive)
                return;

            UpdateMax(ref maxPackageCtrCalls, packageCtrCalls);
            UpdateMax(ref maxPackageCandidateVolume, packageCandidateVolume);
            if (packageFanoutActive)
                Interlocked.Increment(ref fanoutPackages);

            packageCtrCalls = 0;
            packageCandidateVolume = 0L;
            packageFanoutActive = false;
            packageActive = false;
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

            IList<Thing> source;
            bool customSet = __7 != null;
            if (customSet)
            {
                source = __7 as IList<Thing>;
                if (source == null)
                {
                    Interlocked.Increment(ref customSetUnsupportedBypass);
                    return true;
                }
            }
            else
            {
                try
                {
                    source = map.listerThings.ThingsMatching(__2);
                }
                catch
                {
                    Interlocked.Increment(ref sourceFaultBypass);
                    return true;
                }
            }

            if (source == null)
            {
                Interlocked.Increment(ref sourceFaultBypass);
                return true;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                Interlocked.Increment(ref sourceFaultBypass);
                return true;
            }

            RecordSourceBucket(count);
            ObserveFanout(count);

            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return true;
            }

            bool staticLarge = !customSet && count >= LargeSearchThreshold;
            bool customEligible = customSet && count >= CustomSetThreshold;
            bool fanoutEligible = packageFanoutActive && count >= FanoutMinSource;

            if (!staticLarge && !customEligible && !fanoutEligible)
            {
                if (customSet)
                    Interlocked.Increment(ref customSetSmallBypass);
                else
                    Interlocked.Increment(ref smallBypass);
                return true;
            }

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
                    CommitAccelerationKind(staticLarge, customSet, fanoutEligible);
                    Interlocked.Increment(ref accelerated);
                    Interlocked.Increment(ref acceleratedFound);
                    CommitCounters(count, kept, examined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected);
                    RecordElapsed(started);
                    return false;
                }

                __result = null;
                CommitAccelerationKind(staticLarge, customSet, fanoutEligible);
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
                    Log.Warning("[RimMT] V0.4.19-JS1.1S2 accelerated search failed; this call falls back to Vanilla. " +
                        ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }
        }

        private static void ObserveFanout(int count)
        {
            if (!packageActive)
                return;

            packageCtrCalls++;
            packageCandidateVolume += count;
            if (packageFanoutActive)
                return;

            if (packageCtrCalls >= FanoutCallThreshold)
            {
                packageFanoutActive = true;
                Interlocked.Increment(ref fanoutByCalls);
            }
            else if (packageCandidateVolume >= FanoutCandidateVolumeThreshold)
            {
                packageFanoutActive = true;
                Interlocked.Increment(ref fanoutByVolume);
            }
        }

        private static void CommitAccelerationKind(bool staticLarge, bool customSet, bool fanoutEligible)
        {
            if (staticLarge)
                Interlocked.Increment(ref staticLargeAccelerated);
            if (customSet)
                Interlocked.Increment(ref customSetAccelerated);
            if (fanoutEligible && !staticLarge)
                Interlocked.Increment(ref fanoutAccelerated);
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
            else if (count <= 767)
                Interlocked.Increment(ref source512To767);
            else
                Interlocked.Increment(ref source768Plus);
        }

        private static Candidate[] EnsureScratch(int required)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required)
                return current;

            int capacity = 256;
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

            return "JobGiver slow-search JS1.1S2 Fanout: patched=" + patched +
                ", enabled=" + enabled +
                ", staticThreshold=" + LargeSearchThreshold +
                ", customThreshold=" + CustomSetThreshold +
                ", fanoutCalls/volume/minSource=" + FanoutCallThreshold + "/" + FanoutCandidateVolumeThreshold + "/" + FanoutMinSource +
                ", observed=" + Interlocked.Read(ref observed) +
                ", inScope=" + Interlocked.Read(ref inJobGiverScope) +
                ", accelerated=" + calls +
                ", staticLarge/custom/fanout=" + Interlocked.Read(ref staticLargeAccelerated) + "/" + Interlocked.Read(ref customSetAccelerated) + "/" + Interlocked.Read(ref fanoutAccelerated) +
                ", found/noResult=" + Interlocked.Read(ref acceleratedFound) + "/" + Interlocked.Read(ref acceleratedNoResult) +
                ", fanoutPackages=" + Interlocked.Read(ref fanoutPackages) +
                ", fanoutActivation(calls/volume)=" + Interlocked.Read(ref fanoutByCalls) + "/" + Interlocked.Read(ref fanoutByVolume) +
                ", maxPackageCtrCalls=" + Interlocked.Read(ref maxPackageCtrCalls) +
                ", maxPackageCandidateVolume=" + Interlocked.Read(ref maxPackageCandidateVolume) +
                ", sourceBuckets0-15/16-31/32-63/64-127/128-255/256-383/384-511/512-767/768+=" +
                    Interlocked.Read(ref source0To15) + "/" +
                    Interlocked.Read(ref source16To31) + "/" +
                    Interlocked.Read(ref source32To63) + "/" +
                    Interlocked.Read(ref source64To127) + "/" +
                    Interlocked.Read(ref source128To255) + "/" +
                    Interlocked.Read(ref source256To383) + "/" +
                    Interlocked.Read(ref source384To511) + "/" +
                    Interlocked.Read(ref source512To767) + "/" +
                    Interlocked.Read(ref source768Plus) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", customUnsupportedBypass=" + Interlocked.Read(ref customSetUnsupportedBypass) +
                ", customSmallBypass=" + Interlocked.Read(ref customSetSmallBypass) +
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
                ". S2 keeps S1 >=256 ThingRequest acceleration, adds IList custom-set >=32 acceleration, and switches later >=16 searches after per-JobPackage fanout; original validator and live CanReach remain authoritative.";
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
