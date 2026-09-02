using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// Unified Lean S5.1 known-small Hybrid Tail Rescue.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class JobGiverHybridTailS51
    {
        private const int EarlyThresholdMs = 16;
        private const int KnownFastMax = 127;
        private const int MaxSourceCount = 16384;
        private static readonly long EarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyThresholdMs / 1000L);

        [ThreadStatic] private static Candidate[] scratch;
        private static int failureLogs;
        private static long observed;
        private static long knownSmall;
        private static long thresholdBypass;
        private static long accelerated;
        private static long acceleratedNull;
        private static long validatorRejected;
        private static long reachRejected;
        private static long failures;

        static JobGiverHybridTailS51()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Harmony harmony = new Harmony(RimMTBootstrap.HarmonyId);
                int count = 0;
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (!Supported(method)) continue;
                    HarmonyMethod route = new HarmonyMethod(typeof(JobGiverHybridTailS51), nameof(RoutePrefix)) { priority = Priority.First + 200 };
                    harmony.Patch(method, prefix: route);
                    count++;
                }
                Log.Message("[RimMT] Unified Lean S5.1 active on " + count + " ClosestThingReachable overload(s); known <=127 custom sets admit after 16ms.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Unified Lean S5.1 install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool Supported(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                   p[3].ParameterType == typeof(PathEndMode) && p[4].ParameterType == typeof(TraverseParms) &&
                   p[5].ParameterType == typeof(float) && p[6].ParameterType == typeof(Predicate<Thing>) &&
                   typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool RoutePrefix(IntVec3 __0, Map __1, PathEndMode __3, TraverseParms __4,
            float __5, Predicate<Thing> __6, IEnumerable<Thing> __7, ref Thing __result)
        {
            if (__7 == null || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            observed++;
            int knownCount;
            if (!TryKnownCount(__7, out knownCount) || knownCount <= 0 || knownCount > KnownFastMax || knownCount > MaxSourceCount)
                return true;
            knownSmall++;

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L || Stopwatch.GetTimestamp() - scopeStart < EarlyThresholdTicks)
            {
                thresholdBypass++;
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

        private static bool TryKnownCount(IEnumerable<Thing> source, out int count)
        {
            ICollection<Thing> generic = source as ICollection<Thing>;
            if (generic != null) { count = generic.Count; return true; }
            ICollection nongeneric = source as ICollection;
            if (nongeneric != null) { count = nongeneric.Count; return true; }
            count = -1;
            return false;
        }

        private static bool TryFast(IEnumerable<Thing> source, int knownCount, IntVec3 root, Map map,
            PathEndMode endMode, TraverseParms traverseParms, float maxDistance,
            Predicate<Thing> validator, ref Thing result)
        {
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

                if (sourceIndex != knownCount) return true;

                if (kept > 1) Array.Sort(local, 0, kept, CandidateComparer.Instance);
                for (int i = 0; i < kept; i++)
                {
                    Thing thing = local[i].Thing;
                    if (validator != null && !validator(thing))
                    {
                        validatorRejected++;
                        continue;
                    }
                    if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                    {
                        reachRejected++;
                        continue;
                    }
                    result = thing;
                    accelerated++;
                    return false;
                }

                result = null;
                accelerated++;
                acceleratedNull++;
                return false;
            }
            catch (Exception ex)
            {
                failures++;
                if (failureLogs++ < 4)
                    Log.Warning("[RimMT] Unified Lean S5.1 fast path failed closed to Vanilla: " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        internal static string Summary()
        {
            return "S5.1 tail rescue: observed=" + observed +
                ", knownSmall=" + knownSmall +
                ", thresholdBypass=" + thresholdBypass +
                ", accelerated=" + accelerated +
                ", acceleratedNull=" + acceleratedNull +
                ", validatorRejected=" + validatorRejected +
                ", reachRejected=" + reachRejected +
                ", failures=" + failures + ".";
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
                int d = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return d != 0 ? d : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
