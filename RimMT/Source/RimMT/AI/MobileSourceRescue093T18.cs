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
    /// T18 promotes the already-validated S4 nearest-first/validator-first/live-CanReach rescue
    /// to the beginning of JobGiver_Work only for the measured large mobile custom-source shape.
    /// It does not cache jobs, validator results, reservations or reachability. Unsupported shapes,
    /// foreign GenClosest patches, invalid members and non-mobile sources fail open to Vanilla.
    /// </summary>
    internal static class MobileSourceRescue093T18
    {
        internal const string FeatureId = "ai.mobileSourceRescue";
        private const int MinSourceCount = 96;
        private const int MaxSourceCount = 1024;

        [ThreadStatic] private static Candidate[] scratch;

        private static volatile bool installed;
        private static volatile bool enabled = true;
        private static volatile bool authoritySafe = true;
        private static MethodBase target;
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
                if (target == null)
                {
                    Log.Warning("[RimMT] T18 mobile-source rescue unavailable: exact ClosestThingReachable overload not found.");
                    return;
                }

                authoritySafe = !HasForeignPatches(target);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(MobileSourceRescue093T18), nameof(Prefix))
                    { priority = Priority.First + 250 });
                installed = true;
                Log.Message("[RimMT] T18 mobile-source rescue installed. Early S4 semantics are limited to JobGiver custom IList sources >=96 containing Pawn candidates; Vanilla validator/live Reachability remain authoritative. authoritySafe=" + authoritySafe + ".");
            }
            catch (Exception ex)
            {
                installed = false;
                failures++;
                Log.Warning("[RimMT] T18 mobile-source rescue install failed closed: " + ex.GetType().Name + ": " + ex.Message);
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

            if (kept > 1)
            {
                long started = Stopwatch.GetTimestamp();
                Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long elapsed = Stopwatch.GetTimestamp() - started;
                Interlocked.Add(ref sortTicks, elapsed);
                UpdateMax(ref maxSortTicks, elapsed);
            }

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
                   ". Scope=JobGiver_Work + exact 13-arg ClosestThingReachable + custom IList[96..1024] containing Pawn; stable distance/source-order; original validator and live CanReach remain final authority.";
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
