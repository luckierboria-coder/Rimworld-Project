using System;
using System.Collections;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T18/T19 promotes the already-validated S4 nearest-first/validator-first semantics
    /// only for measured large mobile custom-source shapes. T18 handles the exact 13-arg
    /// ClosestThingReachable path with live CanReach. T19 adds the measured large mobile
    /// ClosestThing_Global path when priorityGetter is null. No jobs, validator results,
    /// reservations or reachability decisions are cached. Unsupported shapes and foreign
    /// GenClosest patches fail open to Vanilla.
    /// </summary>
    internal static class MobileSourceRescue093T18
    {
        internal const string FeatureId = "ai.mobileSourceRescue";
        private const int MinSourceCount = 96;
        private const int MaxSourceCount = 1024;

        [ThreadStatic] private static Candidate[] scratch;

        private static volatile bool installed;
        private static volatile bool globalInstalled;
        private static volatile bool enabled = true;
        private static volatile bool authoritySafe = true;
        private static volatile bool globalAuthoritySafe = true;
        private static MethodBase target;
        private static MethodBase globalTarget;

        private static long observed;
        private static long eligible;
        private static long accelerated;
        private static long acceleratedNull;
        private static long candidatesSeen;
        private static long candidatesWithinDistance;
        private static long validatorRejected;
        private static long reachRejected;
        private static long shapeBypass;
        private static long sizeBypass;
        private static long nonMobileBypass;
        private static long invalidMemberBypass;
        private static long foreignPatchBypass;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long maxSourceCount;

        private static long globalObserved;
        private static long globalEligible;
        private static long globalAccelerated;
        private static long globalAcceleratedNull;
        private static long globalCandidatesSeen;
        private static long globalCandidatesWithinDistance;
        private static long globalValidatorRejected;
        private static long globalShapeBypass;
        private static long globalSizeBypass;
        private static long globalNonMobileBypass;
        private static long globalInvalidMemberBypass;
        private static long globalForeignPatchBypass;
        private static long globalPriorityBypass;
        private static long globalSortTicks;
        private static long globalMaxSortTicks;
        private static long globalMaxSourceCount;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                target = AccessTools.Method(
                    typeof(GenClosest),
                    nameof(GenClosest.ClosestThingReachable),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(Map), typeof(ThingRequest), typeof(PathEndMode), typeof(TraverseParms),
                        typeof(float), typeof(Predicate<Thing>), typeof(System.Collections.Generic.IEnumerable<Thing>),
                        typeof(int), typeof(int), typeof(bool), typeof(RegionType), typeof(bool)
                    });
                if (target != null)
                {
                    authoritySafe = !HasForeignPatches(target);
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(MobileSourceRescue093T18), nameof(Prefix))
                        { priority = Priority.First + 250 });
                    installed = true;
                }
                else
                {
                    Log.Warning("[RimMT] T18 mobile-source rescue unavailable: exact ClosestThingReachable overload not found.");
                }

                globalTarget = AccessTools.Method(
                    typeof(GenClosest),
                    nameof(GenClosest.ClosestThing_Global),
                    new Type[]
                    {
                        typeof(IntVec3), typeof(IEnumerable), typeof(float), typeof(Predicate<Thing>), typeof(Func<Thing, float>)
                    });
                if (globalTarget != null)
                {
                    globalAuthoritySafe = !HasForeignPatches(globalTarget);
                    harmony.Patch(globalTarget,
                        prefix: new HarmonyMethod(typeof(MobileSourceRescue093T18), nameof(GlobalPrefix))
                        { priority = Priority.First + 240 });
                    globalInstalled = true;
                }
                else
                {
                    Log.Warning("[RimMT] T19 mobile-global rescue unavailable: exact ClosestThing_Global overload not found.");
                }

                Log.Message("[RimMT] T18/T19 mobile-source rescue installed. Reachable path=" + installed +
                    " authoritySafe=" + authoritySafe + "; Global path=" + globalInstalled +
                    " authoritySafe=" + globalAuthoritySafe +
                    ". Scope is JobGiver large Pawn-containing IList sources; original validator and live Reachability remain authoritative.");
            }
            catch (Exception ex)
            {
                installed = false;
                globalInstalled = false;
                failures++;
                Log.Warning("[RimMT] T18/T19 mobile-source rescue install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value) { enabled = value; }

        public static bool Prefix(object[] __args, ref Thing __result)
        {
            observed++;
            if (!enabled || !installed || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (!authoritySafe)
            {
                foreignPatchBypass++;
                return true;
            }

            if (__args == null || __args.Length < 13)
            {
                shapeBypass++;
                return true;
            }

            IntVec3 root;
            Map map;
            ThingRequest request;
            PathEndMode endMode;
            TraverseParms traverse;
            float maxDistance;
            Predicate<Thing> validator;
            IList source;
            int searchRegionsMin;
            int searchRegionsMax;
            bool forceAllowGlobalSearch;
            RegionType regionTypes;
            bool ignoreForbiddenRegions;

            try
            {
                root = (IntVec3)__args[0];
                map = __args[1] as Map;
                request = (ThingRequest)__args[2];
                endMode = (PathEndMode)__args[3];
                traverse = (TraverseParms)__args[4];
                maxDistance = Convert.ToSingle(__args[5]);
                validator = __args[6] as Predicate<Thing>;
                source = __args[7] as IList;
                searchRegionsMin = Convert.ToInt32(__args[8]);
                searchRegionsMax = Convert.ToInt32(__args[9]);
                forceAllowGlobalSearch = Convert.ToBoolean(__args[10]);
                regionTypes = (RegionType)__args[11];
                ignoreForbiddenRegions = Convert.ToBoolean(__args[12]);
            }
            catch
            {
                shapeBypass++;
                return true;
            }

            Pawn pawn = traverse.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !root.IsValid || !root.InBounds(map) || maxDistance <= 0f || float.IsNaN(maxDistance) ||
                !request.IsUndefined || source == null || searchRegionsMin != 0 ||
                (!(searchRegionsMax < 0) && !forceAllowGlobalSearch) ||
                regionTypes != RegionType.Set_Passable || ignoreForbiddenRegions)
            {
                shapeBypass++;
                return true;
            }

            TraverseMode mode = traverse.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
            {
                shapeBypass++;
                return true;
            }

            int count;
            try { count = source.Count; }
            catch { shapeBypass++; return true; }
            if (count < MinSourceCount || count > MaxSourceCount)
            {
                sizeBypass++;
                return true;
            }

            Candidate[] candidates = EnsureScratch(count);
            int kept = 0;
            bool hasPawn = false;
            double maxSq = (double)maxDistance * maxDistance;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !thing.Spawned || thing.Map != map)
                    {
                        invalidMemberBypass++;
                        return true;
                    }
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid || !pos.InBounds(map))
                    {
                        invalidMemberBypass++;
                        return true;
                    }
                    if (thing is Pawn) hasPawn = true;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq <= maxSq)
                        candidates[kept++] = new Candidate(thing, distSq, i);
                }
            }
            catch
            {
                invalidMemberBypass++;
                return true;
            }

            if (!hasPawn)
            {
                nonMobileBypass++;
                return true;
            }

            eligible++;
            candidatesSeen += count;
            candidatesWithinDistance += kept;
            UpdateMax(ref maxSourceCount, count);

            SortCandidates(candidates, kept, ref sortTicks, ref maxSortTicks);

            int localValidatorRejected = 0;
            int localReachRejected = 0;
            for (int i = 0; i < kept; i++)
            {
                Thing thing = candidates[i].Thing;
                if (validator != null && !validator(thing))
                {
                    localValidatorRejected++;
                    continue;
                }
                if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverse))
                {
                    localReachRejected++;
                    continue;
                }

                validatorRejected += localValidatorRejected;
                reachRejected += localReachRejected;
                __result = thing;
                accelerated++;
                return false;
            }

            validatorRejected += localValidatorRejected;
            reachRejected += localReachRejected;
            __result = null;
            accelerated++;
            acceleratedNull++;
            return false;
        }

        /// <summary>
        /// T19 Phase 2: exact ClosestThing_Global, only when priorityGetter is null. Sorting by
        /// distance then original source index makes the first validator-success exactly the same
        /// nearest/source-order winner as Vanilla's scan. Unspawned/haul-source cases fail open.
        /// </summary>
        public static bool GlobalPrefix(object[] __args, ref Thing __result)
        {
            globalObserved++;
            if (!enabled || !globalInstalled || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            if (!globalAuthoritySafe)
            {
                globalForeignPatchBypass++;
                return true;
            }

            if (__args == null || __args.Length < 5)
            {
                globalShapeBypass++;
                return true;
            }

            IntVec3 center;
            IList source;
            float maxDistance;
            Predicate<Thing> validator;
            Func<Thing, float> priorityGetter;
            try
            {
                center = (IntVec3)__args[0];
                source = __args[1] as IList;
                maxDistance = Convert.ToSingle(__args[2]);
                validator = __args[3] as Predicate<Thing>;
                priorityGetter = __args[4] as Func<Thing, float>;
            }
            catch
            {
                globalShapeBypass++;
                return true;
            }

            if (source == null || !center.IsValid || maxDistance <= 0f || float.IsNaN(maxDistance))
            {
                globalShapeBypass++;
                return true;
            }
            if (priorityGetter != null)
            {
                globalPriorityBypass++;
                return true;
            }

            int count;
            try { count = source.Count; }
            catch { globalShapeBypass++; return true; }
            if (count < MinSourceCount || count > MaxSourceCount)
            {
                globalSizeBypass++;
                return true;
            }

            Candidate[] candidates = EnsureScratch(count);
            int kept = 0;
            bool hasPawn = false;
            double maxSq = (double)maxDistance * maxDistance;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    // Vanilla ClosestThing_Global has special semantics for unspawned things in
                    // haulable inventories and optional IHaulSource contents. The public 5-arg
                    // overload does not request haul-source descent, but unspawned inventory
                    // members still matter. Fail open rather than approximate those semantics.
                    if (thing == null || !thing.Spawned)
                    {
                        globalInvalidMemberBypass++;
                        return true;
                    }
                    IntVec3 pos = thing.PositionHeld;
                    if (!pos.IsValid)
                    {
                        globalInvalidMemberBypass++;
                        return true;
                    }
                    if (thing is Pawn) hasPawn = true;
                    long dx = (long)pos.x - center.x;
                    long dz = (long)pos.z - center.z;
                    long distSq = dx * dx + dz * dz;
                    if (distSq <= maxSq)
                        candidates[kept++] = new Candidate(thing, distSq, i);
                }
            }
            catch
            {
                globalInvalidMemberBypass++;
                return true;
            }

            if (!hasPawn)
            {
                globalNonMobileBypass++;
                return true;
            }

            globalEligible++;
            globalCandidatesSeen += count;
            globalCandidatesWithinDistance += kept;
            UpdateMax(ref globalMaxSourceCount, count);
            SortCandidates(candidates, kept, ref globalSortTicks, ref globalMaxSortTicks);

            int localRejected = 0;
            for (int i = 0; i < kept; i++)
            {
                Thing thing = candidates[i].Thing;
                if (validator != null && !validator(thing))
                {
                    localRejected++;
                    continue;
                }

                globalValidatorRejected += localRejected;
                __result = thing;
                globalAccelerated++;
                return false;
            }

            globalValidatorRejected += localRejected;
            __result = null;
            globalAccelerated++;
            globalAcceleratedNull++;
            return false;
        }

        private static Candidate[] EnsureScratch(int count)
        {
            Candidate[] current = scratch;
            if (current == null || current.Length < count)
            {
                int size = 128;
                while (size < count) size <<= 1;
                current = new Candidate[size];
                scratch = current;
            }
            return current;
        }

        private static void SortCandidates(Candidate[] candidates, int count, ref long totalTicks, ref long maxTicks)
        {
            if (count <= 1) return;
            long started = Stopwatch.GetTimestamp();
            Array.Sort(candidates, 0, count, CandidateComparer.Instance);
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref totalTicks, elapsed);
            UpdateMax(ref maxTicks, elapsed);
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return false;
                return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
            }
            catch { return true; }
        }

        private static bool HasForeign(System.Collections.Generic.IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
        }

        internal static string Summary()
        {
            long e = Interlocked.Read(ref eligible);
            double avgSource = e == 0 ? 0.0 : Interlocked.Read(ref candidatesSeen) / (double)e;
            double avgKept = e == 0 ? 0.0 : Interlocked.Read(ref candidatesWithinDistance) / (double)e;
            double avgSortUs = e == 0 ? 0.0 : Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency / e;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            long ge = Interlocked.Read(ref globalEligible);
            double gAvgSource = ge == 0 ? 0.0 : Interlocked.Read(ref globalCandidatesSeen) / (double)ge;
            double gAvgKept = ge == 0 ? 0.0 : Interlocked.Read(ref globalCandidatesWithinDistance) / (double)ge;
            double gAvgSortUs = ge == 0 ? 0.0 : Interlocked.Read(ref globalSortTicks) * 1000000.0 / Stopwatch.Frequency / ge;
            double gMaxSortUs = Interlocked.Read(ref globalMaxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "T18 mobile-source rescue: installed=" + installed +
                   ", enabled=" + enabled +
                   ", authoritySafe=" + authoritySafe +
                   ", observed=" + Interlocked.Read(ref observed) +
                   ", eligible=" + e +
                   ", accelerated=" + Interlocked.Read(ref accelerated) +
                   ", acceleratedNull=" + Interlocked.Read(ref acceleratedNull) +
                   ", avgSourceCount=" + avgSource.ToString("F1") +
                   ", avgWithinDistance=" + avgKept.ToString("F1") +
                   ", maxSourceCount=" + Interlocked.Read(ref maxSourceCount) +
                   ", validatorRejected=" + Interlocked.Read(ref validatorRejected) +
                   ", reachRejected=" + Interlocked.Read(ref reachRejected) +
                   ", shapeBypass=" + Interlocked.Read(ref shapeBypass) +
                   ", sizeBypass=" + Interlocked.Read(ref sizeBypass) +
                   ", nonMobileBypass=" + Interlocked.Read(ref nonMobileBypass) +
                   ", invalidMemberBypass=" + Interlocked.Read(ref invalidMemberBypass) +
                   ", foreignPatchBypass=" + Interlocked.Read(ref foreignPatchBypass) +
                   ", avgSortUs=" + avgSortUs.ToString("F2") +
                   ", maxSortUs=" + maxSortUs.ToString("F2") +
                   ", failures=" + Interlocked.Read(ref failures) +
                   ". Scope=JobGiver_Work + exact 13-arg ClosestThingReachable + custom IList[96..1024] containing Pawn; stable distance/source-order; original validator and live CanReach remain final authority." +
                   Environment.NewLine +
                   "T19 mobile-global rescue: installed=" + globalInstalled +
                   ", enabled=" + enabled +
                   ", authoritySafe=" + globalAuthoritySafe +
                   ", observed=" + Interlocked.Read(ref globalObserved) +
                   ", eligible=" + ge +
                   ", accelerated=" + Interlocked.Read(ref globalAccelerated) +
                   ", acceleratedNull=" + Interlocked.Read(ref globalAcceleratedNull) +
                   ", avgSourceCount=" + gAvgSource.ToString("F1") +
                   ", avgWithinDistance=" + gAvgKept.ToString("F1") +
                   ", maxSourceCount=" + Interlocked.Read(ref globalMaxSourceCount) +
                   ", validatorRejected=" + Interlocked.Read(ref globalValidatorRejected) +
                   ", shapeBypass=" + Interlocked.Read(ref globalShapeBypass) +
                   ", sizeBypass=" + Interlocked.Read(ref globalSizeBypass) +
                   ", nonMobileBypass=" + Interlocked.Read(ref globalNonMobileBypass) +
                   ", invalidMemberBypass=" + Interlocked.Read(ref globalInvalidMemberBypass) +
                   ", priorityBypass=" + Interlocked.Read(ref globalPriorityBypass) +
                   ", foreignPatchBypass=" + Interlocked.Read(ref globalForeignPatchBypass) +
                   ", avgSortUs=" + gAvgSortUs.ToString("F2") +
                   ", maxSortUs=" + gMaxSortUs.ToString("F2") +
                   ". Scope=JobGiver_Work + exact 5-arg ClosestThing_Global + IList[96..1024] containing Pawn + priorityGetter=null + all members spawned; unsupported haul/inventory semantics fail open.";
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

        private sealed class CandidateComparer : System.Collections.Generic.IComparer<Candidate>
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
