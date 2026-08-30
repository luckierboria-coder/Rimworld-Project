using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JS1.1S5 Hybrid Tail Rescue
    //
    // CD1 showed that known small/medium custom search sets benefit strongly from the validator-first
    // fast path, while known >=128 custom sets regress because full materialization/sorting becomes
    // more expensive than Vanilla's Regionwise short-circuiting. S5 therefore routes only custom
    // sets whose Count is available without enumeration:
    //   - known Count 1..127: may use the fast path after 24ms in the current JobPackage;
    //   - known Count >=128: stay Vanilla, including after S4's 32ms rescue point;
    //   - unknown/lazy IEnumerable: leave untouched for S4's existing 32ms Tail Rescue.
    //
    // ThingRequest behavior is not changed by this layer.
    [StaticConstructorOnStartup]
    internal static class JobGiverHybridTailS5
    {
        private const string HarmonyId = "allen.rimmt.s5hybrid";
        private const int EarlyCustomThresholdMs = 24;
        private const int S4TailThresholdMs = 32;
        private const int KnownFastMax = 127;
        private static readonly long EarlyCustomThresholdTicks =
            Math.Max(1L, Stopwatch.Frequency * EarlyCustomThresholdMs / 1000L);
        private static readonly long S4TailThresholdTicks =
            Math.Max(1L, Stopwatch.Frequency * S4TailThresholdMs / 1000L);

        [ThreadStatic]
        private static Candidate[] candidateScratch;

        [ThreadStatic]
        private static bool restoreLargeBypass;

        [ThreadStatic]
        private static float restoreMaxDistance;

        private static volatile bool patched;

        private static long customObserved;
        private static long unknownCountPassThrough;
        private static long knownEmptyPassThrough;
        private static long knownSmallObserved;
        private static long knownSmallPre24Bypass;
        private static long knownSmallFast;
        private static long knownSmallFound;
        private static long knownSmallNoResult;
        private static long knownLargeObserved;
        private static long knownLargePre32PassThrough;
        private static long knownLargeVanillaBypass;
        private static long source0To31;
        private static long source32To63;
        private static long source64To127;
        private static long source128To255;
        private static long source256Plus;
        private static long sourceCandidates;
        private static long keptCandidates;
        private static long examinedCandidates;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long fastTicks;
        private static long maxFastTicks;
        private static long failures;
        private static long patchFailures;

        static JobGiverHybridTailS5()
        {
            try
            {
                Apply(new Harmony(HarmonyId));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] S5 Hybrid Tail Rescue static initialization failed. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Apply(Harmony harmony)
        {
            int patchedCount = 0;
            try
            {
                MethodInfo[] methods = typeof(GenClosest).GetMethods(
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method))
                        continue;

                    HarmonyMethod route = new HarmonyMethod(
                        typeof(JobGiverHybridTailS5), nameof(RoutePrefix));
                    route.priority = Priority.First + 200;

                    HarmonyMethod restore = new HarmonyMethod(
                        typeof(JobGiverHybridTailS5), nameof(RestorePrefix));
                    restore.priority = Priority.First + 99;

                    harmony.Patch(method, prefix: route);
                    harmony.Patch(method, prefix: restore);
                    patchedCount++;
                }

                MethodBase runtimeReport = AccessTools.Method(
                    typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogRuntimeReport));
                if (runtimeReport != null)
                {
                    harmony.Patch(runtimeReport,
                        postfix: new HarmonyMethod(typeof(JobGiverHybridTailS5), nameof(ReportPostfix)));
                }

                patched = patchedCount > 0;
                if (patched)
                {
                    Log.Message("[RimMT] V0.4.19-JS1.1S5 Hybrid Tail Rescue installed on " +
                        patchedCount + " GenClosest.ClosestThingReachable overload(s). " +
                        "Known custom Count<=127 may accelerate after 24ms; known Count>=128 is " +
                        "kept Vanilla; unknown/lazy custom sets retain S4 32ms behavior.");
                }
                else
                {
                    Log.Warning("[RimMT] S5 Hybrid Tail Rescue found no compatible " +
                        "ClosestThingReachable overload; S4 remains authoritative.");
                }
            }
            catch (Exception ex)
            {
                patched = false;
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] S5 Hybrid Tail Rescue patch failed; S4 remains authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
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

        public static bool RoutePrefix(
            IntVec3 __0,
            Map __1,
            ThingRequest __2,
            PathEndMode __3,
            TraverseParms __4,
            ref float __5,
            Predicate<Thing> __6,
            IEnumerable<Thing> __7,
            ref Thing __result)
        {
            restoreLargeBypass = false;

            if (__7 == null)
                return true;

            RimMTSettings settings = RimMTMod.Settings;
            if (settings == null || !settings.WorkScanAcceleration ||
                !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing)
                return true;

            Interlocked.Increment(ref customObserved);

            int knownCount;
            if (!TryKnownCount(__7, out knownCount))
            {
                Interlocked.Increment(ref unknownCountPassThrough);
                return true;
            }

            RecordKnownBucket(knownCount);

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L)
                return true;

            long elapsedTicks = Stopwatch.GetTimestamp() - scopeStart;

            if (knownCount <= 0)
            {
                Interlocked.Increment(ref knownEmptyPassThrough);
                return true;
            }

            if (knownCount >= 128)
            {
                Interlocked.Increment(ref knownLargeObserved);

                if (elapsedTicks < S4TailThresholdTicks)
                {
                    Interlocked.Increment(ref knownLargePre32PassThrough);
                    return true;
                }

                restoreLargeBypass = true;
                restoreMaxDistance = __5;
                __5 = -1f;
                Interlocked.Increment(ref knownLargeVanillaBypass);
                return true;
            }

            Interlocked.Increment(ref knownSmallObserved);

            if (elapsedTicks < EarlyCustomThresholdTicks)
            {
                Interlocked.Increment(ref knownSmallPre24Bypass);
                return true;
            }

            Map map = __1;
            Pawn pawn = __4.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
                return true;

            TraverseMode mode = __4.mode;
            if (mode != TraverseMode.ByPawn &&
                mode != TraverseMode.PassDoors &&
                mode != TraverseMode.NoPassClosedDoors)
                return true;

            return TryAccelerateKnownCustom(
                __7, knownCount, __0, map, __3, __4, __5, __6, ref __result);
        }

        public static void RestorePrefix(ref float __5)
        {
            if (!restoreLargeBypass)
                return;

            __5 = restoreMaxDistance;
            restoreLargeBypass = false;
        }

        private static bool TryKnownCount(IEnumerable<Thing> source, out int count)
        {
            ICollection<Thing> generic = source as ICollection<Thing>;
            if (generic != null)
            {
                count = generic.Count;
                return true;
            }

            ICollection nongeneric = source as ICollection;
            if (nongeneric != null)
            {
                count = nongeneric.Count;
                return true;
            }

            count = -1;
            return false;
        }

        private static bool TryAccelerateKnownCustom(
            IEnumerable<Thing> source,
            int knownCount,
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
                Candidate[] candidates = EnsureScratch(Math.Max(knownCount, 16), 0);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                int kept = 0;
                int sourceIndex = 0;

                foreach (Thing thing in source)
                {
                    int index = sourceIndex++;
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

                    candidates[kept++] = new Candidate(thing, distanceSquared, index);
                }

                if (sourceIndex > KnownFastMax)
                    return true;

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
                    if (!map.reachability.CanReach(
                        root, new LocalTargetInfo(thing), endMode, traverseParms))
                    {
                        localReachRejected++;
                        continue;
                    }

                    result = thing;
                    RecordFast(
                        true, sourceIndex, kept, examined,
                        localValidatorCalls, localValidatorRejected,
                        localReachChecks, localReachRejected, started);
                    return false;
                }

                result = null;
                RecordFast(
                    false, sourceIndex, kept, examined,
                    localValidatorCalls, localValidatorRejected,
                    localReachChecks, localReachRejected, started);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 8)
                {
                    Log.Warning("[RimMT] S5 known-custom fast path failed; this call falls back to S4/Vanilla. " +
                        ex.GetType().Name + ": " + ex.Message);
                }
                return true;
            }
        }

        private static void RecordFast(
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
            Interlocked.Increment(ref knownSmallFast);
            if (found)
                Interlocked.Increment(ref knownSmallFound);
            else
                Interlocked.Increment(ref knownSmallNoResult);

            Interlocked.Add(ref sourceCandidates, source);
            Interlocked.Add(ref keptCandidates, kept);
            Interlocked.Add(ref examinedCandidates, examined);
            Interlocked.Add(ref validatorCalls, validators);
            Interlocked.Add(ref validatorRejected, validatorFalse);
            Interlocked.Add(ref reachChecks, reaches);
            Interlocked.Add(ref reachRejected, reachFalse);

            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref fastTicks, elapsed);
            UpdateMax(ref maxFastTicks, elapsed);
        }

        private static void RecordKnownBucket(int count)
        {
            if (count <= 31)
                Interlocked.Increment(ref source0To31);
            else if (count <= 63)
                Interlocked.Increment(ref source32To63);
            else if (count <= 127)
                Interlocked.Increment(ref source64To127);
            else if (count <= 255)
                Interlocked.Increment(ref source128To255);
            else
                Interlocked.Increment(ref source256Plus);
        }

        private static Candidate[] EnsureScratch(int required, int preserveCount)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required)
                return current;

            int capacity = 16;
            if (current != null && current.Length > capacity)
                capacity = current.Length;

            while (capacity < required && capacity < 1024)
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

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] S5 supplemental report: " + Summary());
        }

        internal static string Summary()
        {
            long fast = Interlocked.Read(ref knownSmallFast);
            double avgFastMs = fast == 0 ? 0.0 :
                (Interlocked.Read(ref fastTicks) * 1000.0 / Stopwatch.Frequency) / fast;
            double maxFastMs =
                Interlocked.Read(ref maxFastTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSortUs = fast == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / fast;
            double maxSortUs =
                Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "JobGiver JS1.1S5 Hybrid Tail Rescue: patched=" + patched +
                ", earlyCustomThresholdMs=" + EarlyCustomThresholdMs +
                ", knownFastMax=" + KnownFastMax +
                ", customObserved=" + Interlocked.Read(ref customObserved) +
                ", unknownPassThrough=" + Interlocked.Read(ref unknownCountPassThrough) +
                ", knownEmptyPassThrough=" + Interlocked.Read(ref knownEmptyPassThrough) +
                ", knownSmallObserved=" + Interlocked.Read(ref knownSmallObserved) +
                ", knownSmallPre24=" + Interlocked.Read(ref knownSmallPre24Bypass) +
                ", knownSmallFast=" + fast +
                ", found/noResult=" + Interlocked.Read(ref knownSmallFound) + "/" +
                    Interlocked.Read(ref knownSmallNoResult) +
                ", knownLargeObserved=" + Interlocked.Read(ref knownLargeObserved) +
                ", knownLargePre32=" + Interlocked.Read(ref knownLargePre32PassThrough) +
                ", knownLargeVanillaBypass=" + Interlocked.Read(ref knownLargeVanillaBypass) +
                ", knownBuckets<=31/32-63/64-127/128-255/256+=" +
                    Interlocked.Read(ref source0To31) + "/" +
                    Interlocked.Read(ref source32To63) + "/" +
                    Interlocked.Read(ref source64To127) + "/" +
                    Interlocked.Read(ref source128To255) + "/" +
                    Interlocked.Read(ref source256Plus) +
                ", sourceCandidates=" + Interlocked.Read(ref sourceCandidates) +
                ", keptCandidates=" + Interlocked.Read(ref keptCandidates) +
                ", examinedCandidates=" + Interlocked.Read(ref examinedCandidates) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", validatorRejected=" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", reachRejected=" + Interlocked.Read(ref reachRejected) +
                ", avg/maxFastMs=" + avgFastMs.ToString("F3") + "/" + maxFastMs.ToString("F3") +
                ", avg/maxSortUs=" + avgSortUs.ToString("F2") + "/" + maxSortUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                ". ThingRequest and unknown/lazy custom behavior remain S4; CD1 diagnostic hooks are absent.";
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
                return distance != 0
                    ? distance
                    : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
