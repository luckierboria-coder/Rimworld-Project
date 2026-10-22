using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimOPT
{
    // S4's validator-first tail and T34-B's source-indexed distance plan, confined to
    // one synchronous Work package. No old RimMT scheduler or cross-package result is reused.
    internal static class WorkSearchTailPlans
    {
        private const int MinimumSource = 16;
#if RIMWORLD17
        // A rescue that sorts a huge list can worsen the frame we are trying to save.
        private const int MaximumSource = 128;
#else
        private const int MaximumSource = 4096;
#endif
#if RIMWORLD17
        private const int MaximumPlans = 8;
#else
        private const int MaximumPlans = 24;
#endif
        private static readonly long TailThreshold = Math.Max(1L, Stopwatch.Frequency * 16L / 1000L);
        private static MethodInfo target = null;
        private static bool installed = false;
        private static bool chainChecked;
        private static bool chainSupported;
        private static long observed, admitted, planBuilds, planHits, invalidations;
        private static long validatorRejected, reachRejected, results, nullResults, failures;
        private static int reports;
        [ThreadStatic] private static long generation;
        [ThreadStatic] private static List<Plan> plans;

        internal static string Status => "tailPlans[installed=" + installed + ", chain=" + chainSupported +
            ", observed=" + observed + ", admitted=" + admitted + ", builds=" + planBuilds +
            ", hits=" + planHits + ", invalid=" + invalidations +
            ", validatorReject=" + validatorRejected + ", reachReject=" + reachRejected +
            ", result/null=" + results + "/" + nullResults + ", failures=" + failures + "]";

        internal static void Install(Harmony harmony)
        {
#if RIMWORLD17
            // 0.4.1 regression guard: translating GenClosest's native search to a
            // sorted global list can alter 1.7's priority and route semantics.
            // Retain historical source for audit, but install NO replacement hook.
            Log.Message("[RimOPT/1.7] GenClosest tail substitute retired pending 1.7 semantic equivalence proof.");
            return;
#else
            target = AccessTools.Method(typeof(GenClosest), nameof(GenClosest.ClosestThingReachable),
                new Type[] { typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode),
                    typeof(TraverseParms), typeof(float), typeof(Predicate<Thing>),
                    typeof(IEnumerable<Thing>), typeof(int), typeof(int), typeof(bool),
                    typeof(RegionType), typeof(bool), typeof(bool) });
            if (target == null) return;
#if RIMWORLD17
            // Harmony 3's runtime patch chain is authoritative. When modded
            // GenClosest has foreign prefixes/postfixes/IL changes, install no hook.
            // This avoids every-call no-op overhead on a known hot method.
            if (!ChainSupported())
            {
                Log.Message("[RimOPT/1.7] Tail plans inactive: modified GenClosest patch chain.");
                return;
            }
#endif
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(WorkSearchTailPlans), nameof(Prefix))
                { priority = Priority.Last - 200 });
            installed = true;
#endif
        }

        public static bool Prefix(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode,
            TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator,
            IEnumerable<Thing> customGlobalSearchSet, int searchRegionsMin, int searchRegionsMax,
            bool forceAllowGlobalSearch, RegionType traversableRegionTypes,
            bool ignoreEntirelyForbiddenRegions, bool lookInHaulSources, bool __runOriginal,
            ref Thing __result)
        {
            if (!__runOriginal || !PackageScope.Active || Current.ProgramState != ProgramState.Playing)
                return true;
            observed++;
            if (!ChainSupported()) return true;

            PackageScope.Context context = PackageScope.CurrentContext;
            IList<Thing> source = customGlobalSearchSet as IList<Thing>;
            string validatorOwner = validator?.Method?.DeclaringType?.FullName;
            if (context == null || source == null || source.Count < MinimumSource ||
                source.Count > MaximumSource || validator == null ||
                validatorOwner == null || !validatorOwner.StartsWith("RimWorld.JobGiver_Work+", StringComparison.Ordinal) ||
                !thingReq.IsUndefined || searchRegionsMin != 0 || searchRegionsMax != -1 ||
                forceAllowGlobalSearch ||
                traversableRegionTypes != RegionType.Set_Passable ||
                ignoreEntirelyForbiddenRegions || lookInHaulSources ||
                map == null || map.Disposed || traverseParams.pawn == null ||
                !ReferenceEquals(context.Pawn, traverseParams.pawn) ||
                !traverseParams.pawn.Spawned || traverseParams.pawn.Map != map ||
                root != traverseParams.pawn.Position || !root.InBounds(map) ||
                maxDistance <= 0f || float.IsNaN(maxDistance))
                return true;

#if RIMWORLD17
            // Never begin a sort because the package has already stalled.
            // Admit only substantial, bounded global sources on the first pass.
            // The exact-path substitute remains separately opt-in.
            if (source.Count < 32) return true;
#else
            // Historical 1.6 late rescue, unchanged.
            if (Stopwatch.GetTimestamp() - context.Started < TailThreshold)
                return true;
#endif

            Plan plan;
            try
            {
                plan = GetPlan(context.Generation, source, root, map);
            }
            catch (Exception exception)
            {
                failures++;
                if (failures <= 3)
                    Log.Warning("[RimOPT] work-search tail plan fell back: " + exception);
                return true;
            }
            if (plan == null) return true;
            admitted++;
            if ((admitted == 1 || (admitted & 255) == 0) && reports++ < 12)
                Log.Message("[RimOPT] " + Status);
            float maxDistanceSquared = maxDistance * maxDistance;
            for (int i = 0; i < plan.Candidates.Length; i++)
            {
                Candidate candidate = plan.Candidates[i];
                if (candidate.DistanceSquared > maxDistanceSquared) break;
                Thing thing = candidate.Thing;
                // An original validator exception must propagate exactly as it would in Vanilla.
                if (!validator(thing))
                {
                    validatorRejected++;
                    continue;
                }
                if (!map.reachability.CanReach(root, thing, peMode, traverseParams))
                {
                    reachRejected++;
                    continue;
                }
                __result = thing;
                results++;
                return false;
            }
            __result = null;
            nullResults++;
            return false;
        }

        private static bool ChainSupported()
        {
            if (chainChecked && (observed & 4095) != 0) return chainSupported;
            chainChecked = true;
            Patches patches = Harmony.GetPatchInfo(target);
#if RIMWORLD17
            // Ignore our own installed Prefix; foreign Harmony patches still
            // disable the substitute. This check runs again after installation.
            bool supported = patches == null ||
                (!HasForeign(patches.Prefixes) && !HasForeign(patches.Postfixes) &&
                 !HasForeign(patches.Transpilers) && !HasForeign(patches.Finalizers));
#else
            bool supported = patches != null && patches.Transpilers.Count == 0 &&
                patches.Finalizers.Count == 0;
#endif
            if (!supported && chainSupported)
                Log.Message("[RimOPT] work-search tail plan inactive: GenClosest has a transpiler or finalizer.");
            chainSupported = supported;
            return chainSupported;
        }

#if RIMWORLD17
        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
                if (patch != null && patch.owner != Bootstrap.HarmonyId) return true;
            return false;
        }
#endif

        private static Plan GetPlan(long currentGeneration, IList<Thing> source, IntVec3 root, Map map)
        {
            if (plans == null) plans = new List<Plan>(8);
            if (generation != currentGeneration)
            {
                generation = currentGeneration;
                plans.Clear();
            }
            for (int i = 0; i < plans.Count; i++)
            {
                Plan existing = plans[i];
                if (!ReferenceEquals(existing.Source, source) || existing.Root != root) continue;
                if (existing.Matches(map))
                {
                    planHits++;
                    return existing;
                }
                plans.RemoveAt(i);
                invalidations++;
                break;
            }
            if (plans.Count >= MaximumPlans) return null;
            int count = source.Count;
            Candidate[] candidates = new Candidate[count];
            for (int i = 0; i < count; i++)
            {
                Thing thing = source[i];
                if (thing == null || !thing.Spawned || thing.Map != map) return null;
                IntVec3 position = thing.PositionHeld;
                if (!position.IsValid) return null;
                candidates[i] = new Candidate(thing, position, i,
                    (root - position).LengthHorizontalSquared);
            }
            Array.Sort(candidates, (a, b) =>
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.SourceIndex.CompareTo(b.SourceIndex);
            });
            Plan built = new Plan(source, root, candidates);
            plans.Add(built);
            planBuilds++;
            return built;
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly IntVec3 Position;
            internal readonly int SourceIndex;
            internal readonly float DistanceSquared;
            internal Candidate(Thing thing, IntVec3 position, int sourceIndex, float distanceSquared)
            {
                Thing = thing;
                Position = position;
                SourceIndex = sourceIndex;
                DistanceSquared = distanceSquared;
            }
        }

        private sealed class Plan
        {
            internal readonly IList<Thing> Source;
            internal readonly IntVec3 Root;
            internal readonly Candidate[] Candidates;
            internal Plan(IList<Thing> source, IntVec3 root, Candidate[] candidates)
            {
                Source = source;
                Root = root;
                Candidates = candidates;
            }
            internal bool Matches(Map map)
            {
                if (Source.Count != Candidates.Length) return false;
                for (int i = 0; i < Candidates.Length; i++)
                {
                    Candidate candidate = Candidates[i];
                    Thing thing = Source[candidate.SourceIndex];
                    if (!ReferenceEquals(thing, candidate.Thing) || !thing.Spawned ||
                        thing.Map != map || thing.PositionHeld != candidate.Position)
                        return false;
                }
                return true;
            }
        }
    }
}
