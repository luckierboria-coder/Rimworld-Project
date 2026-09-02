using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// Lean form of JS1.1S4. ThingRequest-backed >=256 searches retain the validated fast path.
    /// Once the current JobPackage has already spent 32ms, later >=16 ThingRequest searches and
    /// explicit custom enumerables may use nearest-first validator-first/live-CanReach rescue.
    /// V0.9.2 records route totals plus validator identity only for genuinely heavy calls (>=64
    /// validator rejects), so diagnostics do not become a new hot path.
    /// </summary>
    internal static class JobGiverSlowSearch0419S
    {
        internal const string FeatureId = "ai.jobSlowSearch";
        private const int LargeSearchThreshold = 256;
        private const int TailMinSourceCount = 16;
        private const int TailRescueThresholdMs = 32;
        private const int MaxSourceCount = 16384;
        private const int HeavyRejectThreshold = 64;
        private const int MaxHeavyValidatorKeys = 24;
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);

        [ThreadStatic] private static Candidate[] candidateScratch;
        private static volatile bool enabled = true;
        private static volatile bool patched;
        private static int failureLogs;
        private static long observed;
        private static long staticLargeEligible;
        private static long tailEligible;
        private static long customTailEligible;
        private static long accelerated;
        private static long acceleratedNull;
        private static long validatorRejected;
        private static long reachRejected;
        private static long staticLargeValidatorRejected;
        private static long tailListValidatorRejected;
        private static long customTailValidatorRejected;
        private static long staticLargeReachRejected;
        private static long tailListReachRejected;
        private static long customTailReachRejected;
        private static long heavyValidatorCalls;
        private static long heavyValidatorRejects;
        private static long failures;
        private static readonly Dictionary<string, HeavyValidatorStats> HeavyValidators = new Dictionary<string, HeavyValidatorStats>();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int count = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!IsSupportedOverload(method)) continue;
                    harmony.Patch(method, prefix: new HarmonyMethod(typeof(JobGiverSlowSearch0419S), nameof(Prefix)) { priority = Priority.First + 100 });
                    count++;
                }
                patched = count > 0;
                Log.Message("[RimMT] Unified S4 slow-search rescue active on " + count + " ClosestThingReachable overload(s); heavy validator-tail attribution is thresholded and on-demand.");
            }
            catch (Exception ex)
            {
                patched = false;
                Log.Warning("[RimMT] Unified S4 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value) { enabled = value; }

        private static bool IsSupportedOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                   p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                   p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                   p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool Prefix(IntVec3 __0, Map __1, ThingRequest __2, PathEndMode __3, TraverseParms __4,
            float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
        {
            if (!enabled || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            observed++;
            Map map = __1;
            Pawn pawn = __4.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
                return true;

            TraverseMode mode = __4.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
                return true;

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L) return true;

            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }

            List<Thing> source;
            try { source = map.listerThings.ThingsMatching(__2); }
            catch { return true; }
            if (source == null) return true;

            int count = source.Count;
            if (count > MaxSourceCount) return true;
            if (count >= LargeSearchThreshold)
            {
                staticLargeEligible++;
                return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.StaticLarge, ref __result);
            }
            if (count < TailMinSourceCount) return true;
            if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
        }

        private static bool TryAccelerateList(List<Thing> source, int count, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, float maxDistance,
            Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            try
            {
                Candidate[] candidates = EnsureScratch(count, 0);
                double maxSq = (double)maxDistance * maxDistance;
                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq > maxSq) continue;
                    candidates[kept++] = new Candidate(thing, distSq, i);
                }
                return RunCandidates(candidates, kept, root, map, endMode, traverseParms, validator, route, ref result);
            }
            catch (Exception ex) { return Failure(ex); }
        }

        private static bool TryAccelerateCustom(IEnumerable<Thing> source, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, float maxDistance,
            Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            try
            {
                Candidate[] candidates = EnsureScratch(256, 0);
                double maxSq = (double)maxDistance * maxDistance;
                int kept = 0;
                int sourceCount = 0;
                foreach (Thing thing in source)
                {
                    int sourceIndex = sourceCount++;
                    if (sourceCount > MaxSourceCount) return true;
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq > maxSq) continue;
                    if (kept >= candidates.Length) candidates = EnsureScratch(kept + 1, kept);
                    candidates[kept++] = new Candidate(thing, distSq, sourceIndex);
                }
                if (sourceCount < TailMinSourceCount) return true;
                return RunCandidates(candidates, kept, root, map, endMode, traverseParms, validator, route, ref result);
            }
            catch (Exception ex) { return Failure(ex); }
        }

        private static bool RunCandidates(Candidate[] candidates, int kept, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, Predicate<Thing> validator, RescueRoute route, ref Thing result)
        {
            int localValidatorRejected = 0;
            int localReachRejected = 0;
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
            {
                Thing thing = candidates[i].Thing;
                if (validator != null && !validator(thing))
                {
                    localValidatorRejected++;
                    continue;
                }
                if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                {
                    localReachRejected++;
                    continue;
                }
                RecordRoute(route, localValidatorRejected, localReachRejected, validator);
                result = thing;
                accelerated++;
                return false;
            }
            RecordRoute(route, localValidatorRejected, localReachRejected, validator);
            result = null;
            accelerated++;
            acceleratedNull++;
            return false;
        }

        private static void RecordRoute(RescueRoute route, int validatorRejects, int reachRejects, Predicate<Thing> validator)
        {
            validatorRejected += validatorRejects;
            reachRejected += reachRejects;
            switch (route)
            {
                case RescueRoute.StaticLarge:
                    staticLargeValidatorRejected += validatorRejects;
                    staticLargeReachRejected += reachRejects;
                    break;
                case RescueRoute.TailList:
                    tailListValidatorRejected += validatorRejects;
                    tailListReachRejected += reachRejects;
                    break;
                default:
                    customTailValidatorRejected += validatorRejects;
                    customTailReachRejected += reachRejects;
                    break;
            }

            if (validatorRejects < HeavyRejectThreshold || validator == null) return;
            heavyValidatorCalls++;
            heavyValidatorRejects += validatorRejects;
            if (HeavyValidators.Count >= MaxHeavyValidatorKeys) return;

            try
            {
                MethodInfo method = validator.Method;
                string owner = method == null || method.DeclaringType == null ? "<unknown>" : method.DeclaringType.FullName;
                string name = method == null ? "<unknown>" : method.Name;
                string key = owner + "." + name;
                HeavyValidatorStats stats;
                if (!HeavyValidators.TryGetValue(key, out stats))
                {
                    stats = new HeavyValidatorStats();
                    HeavyValidators[key] = stats;
                }
                stats.Calls++;
                stats.Rejects += validatorRejects;
            }
            catch { }
        }

        private static Candidate[] EnsureScratch(int required, int preserveCount)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required) return current;
            int capacity = current == null ? 256 : Math.Max(256, current.Length);
            while (capacity < required && capacity < 65536) capacity <<= 1;
            if (capacity < required) capacity = required;
            Candidate[] next = new Candidate[capacity];
            if (current != null && preserveCount > 0) Array.Copy(current, next, Math.Min(preserveCount, current.Length));
            candidateScratch = next;
            return next;
        }

        private static bool Failure(Exception ex)
        {
            failures++;
            if (failureLogs++ < 4)
                Log.Warning("[RimMT] Unified S4 accelerated search failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
            return true;
        }

        internal static string Summary()
        {
            string heavy = BuildHeavyValidatorSummary();
            return "S4 slow-search: patched=" + patched + ", enabled=" + enabled +
                   ", observed=" + observed +
                   ", staticLargeEligible=" + staticLargeEligible +
                   ", tailEligible=" + tailEligible +
                   ", customTailEligible=" + customTailEligible +
                   ", accelerated=" + accelerated +
                   ", acceleratedNull=" + acceleratedNull +
                   ", validatorRejected=" + validatorRejected +
                   " [static=" + staticLargeValidatorRejected + ", tailList=" + tailListValidatorRejected + ", custom=" + customTailValidatorRejected + "]" +
                   ", reachRejected=" + reachRejected +
                   " [static=" + staticLargeReachRejected + ", tailList=" + tailListReachRejected + ", custom=" + customTailReachRejected + "]" +
                   ", heavyValidatorCalls=" + heavyValidatorCalls +
                   ", heavyValidatorRejects=" + heavyValidatorRejects +
                   ", heavyValidators=" + heavy +
                   ", failures=" + failures +
                   ", staticThreshold=" + LargeSearchThreshold + ", tailThresholdMs=" + TailRescueThresholdMs +
                   ", tailMinSource=" + TailMinSourceCount + ".";
        }

        private static string BuildHeavyValidatorSummary()
        {
            if (HeavyValidators.Count == 0) return "none";
            string bestKey = null;
            long bestRejects = -1;
            long bestCalls = 0;
            foreach (KeyValuePair<string, HeavyValidatorStats> pair in HeavyValidators)
            {
                HeavyValidatorStats stats = pair.Value;
                if (stats == null || stats.Rejects <= bestRejects) continue;
                bestKey = pair.Key;
                bestRejects = stats.Rejects;
                bestCalls = stats.Calls;
            }
            return bestKey == null ? "none" : bestKey + "(calls=" + bestCalls + ", rejects=" + bestRejects + ")";
        }

        private enum RescueRoute { StaticLarge, TailList, CustomTail }

        private sealed class HeavyValidatorStats
        {
            internal long Calls;
            internal long Rejects;
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;
            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing; DistanceSquared = distanceSquared; SourceIndex = sourceIndex;
            }
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
