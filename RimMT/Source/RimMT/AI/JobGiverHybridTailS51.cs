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
    [StaticConstructorOnStartup]
    internal static class JobGiverHybridTailS51
    {
        private const int EarlyThresholdMs = 24;
        private const int S4ThresholdMs = 32;
        private const int KnownFastMax = 127;
        private const int MaxSourceCount = 16384;
        private static readonly long EarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyThresholdMs / 1000L);
        private static readonly long S4ThresholdTicks = Math.Max(1L, Stopwatch.Frequency * S4ThresholdMs / 1000L);

        [ThreadStatic] private static Candidate[] scratch;
        [ThreadStatic] private static bool restorePending;
        [ThreadStatic] private static float restoreMaxDistance;

        private static volatile bool patched;
        private static long observed;
        private static long knownSmallObserved;
        private static long knownSmallPre24;
        private static long knownSmallFast;
        private static long knownLargeObserved;
        private static long knownLargePre32;
        private static long knownLargeVanillaBypass;
        private static long unknownPassThrough;
        private static long emptyBypass;
        private static long tooLargeBypass;
        private static long sourceFaultBypass;
        private static long acceleratedFound;
        private static long acceleratedNoResult;
        private static long candidates;
        private static long examined;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long fastTicks;
        private static long maxFastTicks;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long maxSource;
        private static long bucket0To31;
        private static long bucket32To63;
        private static long bucket64To127;
        private static long bucket128To255;
        private static long bucket256Plus;
        private static long failures;
        private static long patchFailures;

        static JobGiverHybridTailS51()
        {
            try { Apply(new Harmony(RimMTBootstrap.HarmonyId)); }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] S5.1 Hybrid Tail Rescue static initialization failed. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void Apply(Harmony harmony)
        {
            int count = 0;
            MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (!Supported(method)) continue;
                HarmonyMethod route = new HarmonyMethod(typeof(JobGiverHybridTailS51), nameof(RoutePrefix));
                route.priority = Priority.First + 200;
                harmony.Patch(method, prefix: route);
                HarmonyMethod restore = new HarmonyMethod(typeof(JobGiverHybridTailS51), nameof(RestorePrefix));
                restore.priority = Priority.First + 99;
                harmony.Patch(method, prefix: restore);
                count++;
            }
            MethodBase runtimeReport = AccessTools.Method(typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogRuntimeReport));
            if (runtimeReport != null)
                harmony.Patch(runtimeReport, postfix: new HarmonyMethod(typeof(JobGiverHybridTailS51), nameof(ReportPostfix)));
            patched = count > 0;
            Log.Message("[RimMT] V0.4.19-JS1.1S5.1 Hybrid Tail Rescue installed on " + count + " GenClosest.ClosestThingReachable overload(s), using RimMT self Harmony owner. Known custom Count<=127 may accelerate after 24ms; known Count>=128 remains Vanilla after 32ms; unknown/lazy custom sets retain S4 behavior.");
        }

        private static bool Supported(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) && p[3].ParameterType == typeof(PathEndMode) && p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) && p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool RoutePrefix(IntVec3 __0, Map __1, PathEndMode __3, TraverseParms __4, ref float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
        {
            Interlocked.Increment(ref observed);
            restorePending = false;
            if (__7 == null || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;
            int knownCount;
            if (!TryKnownCount(__7, out knownCount))
            {
                Interlocked.Increment(ref unknownPassThrough);
                return true;
            }
            RecordBucket(knownCount);
            if (knownCount <= 0) { Interlocked.Increment(ref emptyBypass); return true; }
            if (knownCount > MaxSourceCount) { Interlocked.Increment(ref tooLargeBypass); return true; }
            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L) return true;
            long elapsed = Stopwatch.GetTimestamp() - scopeStart;
            if (knownCount <= KnownFastMax)
            {
                Interlocked.Increment(ref knownSmallObserved);
                if (elapsed < EarlyThresholdTicks)
                {
                    Interlocked.Increment(ref knownSmallPre24);
                    return true;
                }
                Map map = __1;
                Pawn pawn = __4.pawn;
                if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map || !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
                    return true;
                TraverseMode mode = __4.mode;
                if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
                    return true;
                return TryFast(__7, knownCount, __0, map, __3, __4, __5, __6, ref __result);
            }
            Interlocked.Increment(ref knownLargeObserved);
            if (elapsed < S4ThresholdTicks)
            {
                Interlocked.Increment(ref knownLargePre32);
                return true;
            }
            Interlocked.Increment(ref knownLargeVanillaBypass);
            restorePending = true;
            restoreMaxDistance = __5;
            __5 = -1f;
            return true;
        }

        public static void RestorePrefix(ref float __5)
        {
            if (!restorePending) return;
            __5 = restoreMaxDistance;
            restorePending = false;
        }

        private static bool TryKnownCount(IEnumerable<Thing> source, out int count)
        {
            ICollection<Thing> generic = source as ICollection<Thing>;
            if (generic != null) { count = generic.Count; return true; }
            ICollection nongeneric = source as ICollection;
            if (nongeneric != null) { count = nongeneric.Count; return true; }
            count = -1;
            return false;
        }

        private static bool TryFast(IEnumerable<Thing> source, int knownCount, IntVec3 root, Map map, PathEndMode endMode, TraverseParms traverseParms, float maxDistance, Predicate<Thing> validator, ref Thing result)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] local = EnsureScratch(Math.Max(knownCount, 16));
                double maxDistanceSq = (double)maxDistance * maxDistance;
                int kept = 0;
                int sourceIndex = 0;
                foreach (Thing thing in source)
                {
                    int index = sourceIndex++;
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distanceSq = dx * dx + dz * dz;
                    if (distanceSq > maxDistanceSq) continue;
                    if (kept >= local.Length) local = EnsureScratch(kept + 1);
                    local[kept++] = new Candidate(thing, distanceSq, index);
                }
                if (sourceIndex != knownCount)
                {
                    Interlocked.Increment(ref sourceFaultBypass);
                    return true;
                }
                long sortStart = Stopwatch.GetTimestamp();
                if (kept > 1) Array.Sort(local, 0, kept, CandidateComparer.Instance);
                long sortElapsed = Stopwatch.GetTimestamp() - sortStart;
                Interlocked.Add(ref sortTicks, sortElapsed);
                UpdateMax(ref maxSortTicks, sortElapsed);
                int localExamined = 0, localValidatorCalls = 0, localValidatorRejected = 0, localReachChecks = 0, localReachRejected = 0;
                for (int i = 0; i < kept; i++)
                {
                    Thing thing = local[i].Thing;
                    localExamined++;
                    if (validator != null)
                    {
                        localValidatorCalls++;
                        if (!validator(thing)) { localValidatorRejected++; continue; }
                    }
                    localReachChecks++;
                    if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                    {
                        localReachRejected++;
                        continue;
                    }
                    result = thing;
                    RecordFast(true, knownCount, localExamined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, started);
                    return false;
                }
                result = null;
                RecordFast(false, knownCount, localExamined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, started);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 8)
                    Log.Warning("[RimMT] S5.1 Hybrid Tail Rescue fast path failed; falling back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static Candidate[] EnsureScratch(int required)
        {
            Candidate[] current = scratch;
            if (current != null && current.Length >= required) return current;
            int capacity = current == null ? 128 : current.Length;
            while (capacity < required) capacity <<= 1;
            scratch = new Candidate[capacity];
            return scratch;
        }

        private static void RecordFast(bool found, int sourceCount, int localExamined, int localValidatorCalls, int localValidatorRejected, int localReachChecks, int localReachRejected, long started)
        {
            Interlocked.Increment(ref knownSmallFast);
            if (found) Interlocked.Increment(ref acceleratedFound); else Interlocked.Increment(ref acceleratedNoResult);
            Interlocked.Add(ref candidates, sourceCount);
            Interlocked.Add(ref examined, localExamined);
            Interlocked.Add(ref validatorCalls, localValidatorCalls);
            Interlocked.Add(ref validatorRejected, localValidatorRejected);
            Interlocked.Add(ref reachChecks, localReachChecks);
            Interlocked.Add(ref reachRejected, localReachRejected);
            UpdateMax(ref maxSource, sourceCount);
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref fastTicks, elapsed);
            UpdateMax(ref maxFastTicks, elapsed);
        }

        private static void RecordBucket(int count)
        {
            if (count <= 31) Interlocked.Increment(ref bucket0To31);
            else if (count <= 63) Interlocked.Increment(ref bucket32To63);
            else if (count <= 127) Interlocked.Increment(ref bucket64To127);
            else if (count <= 255) Interlocked.Increment(ref bucket128To255);
            else Interlocked.Increment(ref bucket256Plus);
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
        }

        public static void ReportPostfix() { Log.Message("[RimMT] S5.1 supplemental report: " + Summary()); }

        internal static string Summary()
        {
            long fast = Interlocked.Read(ref knownSmallFast);
            double avgFastMs = fast == 0 ? 0.0 : (Interlocked.Read(ref fastTicks) * 1000.0 / Stopwatch.Frequency) / fast;
            double maxFastMs = Interlocked.Read(ref maxFastTicks) * 1000.0 / Stopwatch.Frequency;
            double avgSortUs = fast == 0 ? 0.0 : (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / fast;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;
            double avgSource = fast == 0 ? 0.0 : Interlocked.Read(ref candidates) / (double)fast;
            double avgExamined = fast == 0 ? 0.0 : Interlocked.Read(ref examined) / (double)fast;
            return "JobGiver JS1.1S5.1 Hybrid Tail Rescue: patched=" + patched + ", owner=" + RimMTBootstrap.HarmonyId + ", knownSmallObserved=" + Interlocked.Read(ref knownSmallObserved) + ", knownSmallPre24=" + Interlocked.Read(ref knownSmallPre24) + ", knownSmallFast=" + fast + ", knownLargeObserved=" + Interlocked.Read(ref knownLargeObserved) + ", knownLargePre32=" + Interlocked.Read(ref knownLargePre32) + ", knownLargeVanillaBypass=" + Interlocked.Read(ref knownLargeVanillaBypass) + ", unknownPassThrough=" + Interlocked.Read(ref unknownPassThrough) + ", buckets<=31/32-63/64-127/128-255/256+=" + Interlocked.Read(ref bucket0To31) + "/" + Interlocked.Read(ref bucket32To63) + "/" + Interlocked.Read(ref bucket64To127) + "/" + Interlocked.Read(ref bucket128To255) + "/" + Interlocked.Read(ref bucket256Plus) + ", found/noResult=" + Interlocked.Read(ref acceleratedFound) + "/" + Interlocked.Read(ref acceleratedNoResult) + ", avgSource=" + avgSource.ToString("F1") + ", avgExamined=" + avgExamined.ToString("F1") + ", maxSource=" + Interlocked.Read(ref maxSource) + ", validatorCalls=" + Interlocked.Read(ref validatorCalls) + ", validatorRejected=" + Interlocked.Read(ref validatorRejected) + ", reachChecks=" + Interlocked.Read(ref reachChecks) + ", reachRejected=" + Interlocked.Read(ref reachRejected) + ", avg/maxFastMs=" + avgFastMs.ToString("F3") + "/" + maxFastMs.ToString("F3") + ", avg/maxSortUs=" + avgSortUs.ToString("F2") + "/" + maxSortUs.ToString("F2") + ", emptyBypass=" + Interlocked.Read(ref emptyBypass) + ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) + ", sourceFaultBypass=" + Interlocked.Read(ref sourceFaultBypass) + ", failures=" + Interlocked.Read(ref failures) + ", patchFailures=" + Interlocked.Read(ref patchFailures) + ".";
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;
            internal Candidate(Thing thing, long distanceSquared, int sourceIndex) { Thing = thing; DistanceSquared = distanceSquared; SourceIndex = sourceIndex; }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b)
            {
                int d = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return d != 0 ? d : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
