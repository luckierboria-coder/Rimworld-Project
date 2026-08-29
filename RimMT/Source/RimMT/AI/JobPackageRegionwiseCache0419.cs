using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JR1 aggressive JobGiver Regionwise cache.
    //
    // JD1 measured GenClosest.ClosestThingReachable -> RegionTraverser as the dominant source
    // of JobGiver stalls. RegionwiseBFSWorker repeatedly walks the same Region graph for many
    // WorkGivers during one synchronous TryIssueJobPackage even though root, Pawn traversal
    // policy, maxDistance and maxRegions are usually identical.
    //
    // JR1 builds that BFS Region order once per exact traversal key and reuses it for subsequent
    // RegionwiseBFSWorker calls. Candidate lists and WorkGiver validators are still evaluated
    // live on every search. No Job, reservation or final candidate is cached.
    internal static class JobPackageRegionwiseCache0419
    {
        internal const string FeatureId = "ai.jobRegionwise";

        private const int MaxTraversalEntriesPerPackage = 128;

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;
        [ThreadStatic] private static PackageContext pooledContext;

        private static MethodBase regionwiseTarget;
        private static bool scopePatched;
        private static bool regionwisePatched;
        private static int patchFailures;

        private static long packages;
        private static long observed;
        private static long accelerated;
        private static long cacheHits;
        private static long cacheMisses;
        private static long cacheStores;
        private static long cacheCapBypass;
        private static long buildFailures;
        private static long acceleratedFailures;
        private static long regionsBuilt;
        private static long regionsScanned;
        private static long candidatesScanned;
        private static long validatorCalls;
        private static long contextCreates;
        private static long contextReuse;
        private static long contextReturns;
        private static long maxEntries;
        private static long maxRegionsPerEntry;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodBase jobGiver = AccessTools.Method(
                    typeof(JobGiver_Work),
                    "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });

                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != "RegionwiseBFSWorker")
                        continue;
                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length == 13 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map))
                    {
                        regionwiseTarget = method;
                        break;
                    }
                }

                if (jobGiver == null || regionwiseTarget == null)
                {
                    FeatureGate.Suppress(FeatureId, "required JobGiver/RegionwiseBFSWorker target not found");
                    Log.Warning("[RimMT] V0.4.19-JR1 Regionwise cache unavailable: target not found.");
                    return;
                }

                HarmonyMethod scopePrefix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(JobPackagePrefix));
                scopePrefix.priority = Priority.First + 70;
                HarmonyMethod scopeFinalizer = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(JobPackageFinalizer));
                scopeFinalizer.priority = Priority.Last - 70;
                harmony.Patch(jobGiver, prefix: scopePrefix, finalizer: scopeFinalizer);
                scopePatched = true;

                HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(RegionwisePrefix));
                prefix.priority = Priority.First + 200;
                harmony.Patch(regionwiseTarget, prefix: prefix);
                regionwisePatched = true;

                Log.Message("[RimMT] V0.4.19-JR1 aggressive Regionwise cache installed. Inside JobGiver_Work, exact root/traversal/maxDistance/maxRegions BFS Region order is built once and reused; WorkGiver validators/candidates remain live.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JR1 Regionwise cache install failed; Vanilla Regionwise search remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobPackagePrefix(Pawn __0, ref ScopeState __state)
        {
            __state = default(ScopeState);
            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = packageDepth == 0;
            packageDepth++;
            if (__state.Outermost)
            {
                current = Acquire(__0);
                __state.Context = current;
                packages++;
            }
            else
            {
                __state.Context = current;
            }
        }

        public static Exception JobPackageFinalizer(Exception __exception, ScopeState __state)
        {
            if (!__state.Entered)
                return __exception;

            if (packageDepth > 0)
                packageDepth--;

            if (__state.Outermost)
            {
                PackageContext context = __state.Context;
                if (context != null && context.Traversals.Count > maxEntries)
                    maxEntries = context.Traversals.Count;
                if (ReferenceEquals(current, context))
                    current = null;
                Release(context);
            }
            return __exception;
        }

        // Generic __args is acceptable here: RegionwiseBFSWorker is called ~thousands, not
        // millions, of times. It also avoids binding this experimental accelerator to compiler
        // generated parameter names.
        public static bool RegionwisePrefix(object[] __args, ref Thing __result)
        {
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || __args == null || __args.Length < 13 ||
                !FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread)
                return true;

            observed++;

            try
            {
                IntVec3 root = (IntVec3)__args[0];
                Map map = __args[1] as Map;
                ThingRequest req = (ThingRequest)__args[2];
                PathEndMode peMode = (PathEndMode)__args[3];
                TraverseParms traverseParams = (TraverseParms)__args[4];
                Predicate<Thing> validator = __args[5] as Predicate<Thing>;
                Func<Thing, float> priorityGetter = __args[6] as Func<Thing, float>;
                int minRegions = Convert.ToInt32(__args[7]);
                int maxRegions = Convert.ToInt32(__args[8]);
                float maxDistance = Convert.ToSingle(__args[9]);
                RegionType traversableRegionTypes = (RegionType)__args[11];
                bool ignoreEntirelyForbiddenRegions = Convert.ToBoolean(__args[12]);

                Pawn pawn = traverseParams.pawn;
                if (map == null || pawn == null || context.Pawn == null || !ReferenceEquals(pawn, context.Pawn) ||
                    !root.IsValid || !root.InBounds(map) || maxRegions <= 0)
                    return true;

                if (traverseParams.mode == TraverseMode.PassAllDestroyableThings ||
                    traverseParams.mode == TraverseMode.PassAllDestroyableThingsNotWater ||
                    (!req.IsUndefined && !req.CanBeFoundInRegion))
                    return true;

                Region rootRegion = root.GetRegion(map, traversableRegionTypes);
                if (rootRegion == null)
                {
                    __args[10] = 0;
                    __result = null;
                    accelerated++;
                    return false;
                }

                TraversalKey key = new TraversalKey(
                    rootRegion,
                    root.x,
                    root.z,
                    new AggressiveReachabilityProfiles.TraverseKey(traverseParams),
                    maxRegions,
                    maxDistance,
                    traversableRegionTypes);

                TraversalEntry traversal;
                if (!context.Traversals.TryGetValue(key, out traversal))
                {
                    cacheMisses++;
                    if (context.Traversals.Count >= MaxTraversalEntriesPerPackage)
                    {
                        cacheCapBypass++;
                        return true;
                    }

                    traversal = BuildTraversal(root, rootRegion, traverseParams, maxRegions, maxDistance, traversableRegionTypes);
                    if (traversal == null)
                    {
                        buildFailures++;
                        return true;
                    }
                    context.Traversals.Add(key, traversal);
                    cacheStores++;
                    regionsBuilt += traversal.Regions.Length;
                    if (traversal.Regions.Length > maxRegionsPerEntry)
                        maxRegionsPerEntry = traversal.Regions.Length;
                }
                else
                {
                    cacheHits++;
                }

                int seen;
                Thing result = ScanTraversal(
                    traversal,
                    root,
                    req,
                    peMode,
                    traverseParams,
                    validator,
                    priorityGetter,
                    minRegions,
                    maxDistance,
                    ignoreEntirelyForbiddenRegions,
                    out seen);

                __args[10] = seen;
                __result = result;
                accelerated++;
                return false;
            }
            catch (Exception ex)
            {
                acceleratedFailures++;
                if (acceleratedFailures <= 8)
                    Log.Warning("[RimMT] V0.4.19-JR1 Regionwise accelerated search failed; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static TraversalEntry BuildTraversal(
            IntVec3 root,
            Region rootRegion,
            TraverseParms traverseParams,
            int maxRegions,
            float maxDistance,
            RegionType traversableRegionTypes)
        {
            List<Region> regions = new List<Region>(Math.Min(maxRegions, 32));
            float maxDistSquared = maxDistance * maxDistance;

            RegionEntryPredicate entry = delegate(Region from, Region to)
            {
                return to.Allows(traverseParams, false) &&
                    (maxDistance > 5000f || to.extentsClose.ClosestDistSquaredTo(root) < maxDistSquared);
            };

            RegionProcessor processor = delegate(Region region)
            {
                regions.Add(region);
                return false;
            };

            RegionTraverser.BreadthFirstTraverse(rootRegion, entry, processor, maxRegions, traversableRegionTypes);
            return new TraversalEntry(regions.ToArray());
        }

        private static Thing ScanTraversal(
            TraversalEntry traversal,
            IntVec3 root,
            ThingRequest req,
            PathEndMode peMode,
            TraverseParms traverseParams,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            int minRegions,
            float maxDistance,
            bool ignoreEntirelyForbiddenRegions,
            out int regionsSeen)
        {
            Thing closestThing = null;
            float closestDistSquared = 9999999f;
            float bestPriority = float.MinValue;
            float maxDistSquared = maxDistance * maxDistance;
            int seen = 0;
            Region[] regions = traversal.Regions;

            for (int r = 0; r < regions.Length; r++)
            {
                Region region = regions[r];
                if (region == null || !region.valid)
                    continue;

                if (RegionTraverser.ShouldCountRegion(region))
                    seen++;

                if (!region.IsDoorway && !region.Allows(traverseParams, true))
                {
                    if (seen >= minRegions && closestThing != null)
                        break;
                    continue;
                }

                if (!ignoreEntirelyForbiddenRegions || !region.IsForbiddenEntirely(traverseParams.pawn))
                {
                    List<Thing> list = region.ListerThings.ThingsMatching(req);
                    for (int i = 0; i < list.Count; i++)
                    {
                        Thing thing = list[i];
                        candidatesScanned++;
                        if (!ReachabilityWithinRegion.ThingFromRegionListerReachable(thing, region, peMode, traverseParams.pawn))
                            continue;

                        float priority = priorityGetter == null ? 0f : priorityGetter(thing);
                        if (priority < bestPriority)
                            continue;

                        float distanceSquared = (thing.Position - root).LengthHorizontalSquared;
                        if (priority == bestPriority && distanceSquared >= closestDistSquared)
                            continue;
                        if (distanceSquared >= maxDistSquared)
                            continue;

                        if (validator != null)
                        {
                            validatorCalls++;
                            if (!validator(thing))
                                continue;
                        }

                        closestThing = thing;
                        closestDistSquared = distanceSquared;
                        bestPriority = priority;
                    }
                }

                if (seen >= minRegions && closestThing != null)
                    break;
            }

            regionsSeen = seen;
            regionsScanned += Math.Min(regions.Length, Math.Max(0, seen));
            return closestThing;
        }

        private static PackageContext Acquire(Pawn pawn)
        {
            PackageContext context = pooledContext;
            if (context == null)
            {
                context = new PackageContext();
                contextCreates++;
            }
            else
            {
                pooledContext = null;
                contextReuse++;
            }
            context.Begin(pawn);
            return context;
        }

        private static void Release(PackageContext context)
        {
            if (context == null)
                return;
            context.End();
            if (pooledContext == null)
            {
                pooledContext = context;
                contextReturns++;
            }
        }

        internal static string Summary()
        {
            long obs = observed;
            long acc = accelerated;
            double accPct = obs == 0 ? 0.0 : acc * 100.0 / obs;
            long hitDenom = cacheHits + cacheMisses;
            double hitPct = hitDenom == 0 ? 0.0 : cacheHits * 100.0 / hitDenom;
            return "JobPackage Regionwise JR1: patched(scope/regionwise)=" + scopePatched + "/" + regionwisePatched +
                ", patchFailures=" + patchFailures +
                ", packages=" + packages +
                ", observed=" + obs +
                ", accelerated=" + acc + " (" + accPct.ToString("F1") + "%)" +
                ", traversalHit/miss=" + cacheHits + "/" + cacheMisses + " (" + hitPct.ToString("F1") + "% hit)" +
                ", traversalStores=" + cacheStores +
                ", capBypass=" + cacheCapBypass +
                ", buildFailures=" + buildFailures +
                ", acceleratedFailures=" + acceleratedFailures +
                ", regionsBuilt=" + regionsBuilt +
                ", regionsScanned=" + regionsScanned +
                ", candidatesScanned=" + candidatesScanned +
                ", validatorCalls=" + validatorCalls +
                ", context(create/reuse/return)=" + contextCreates + "/" + contextReuse + "/" + contextReturns +
                ", maxEntries=" + maxEntries +
                ", maxRegionsPerEntry=" + maxRegionsPerEntry +
                ". Aggressive performance mode: Region BFS order is reused inside one JobPackage; candidate validation remains live.";
        }

        internal struct ScopeState
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal sealed class PackageContext
        {
            internal Pawn Pawn;
            internal readonly Dictionary<TraversalKey, TraversalEntry> Traversals = new Dictionary<TraversalKey, TraversalEntry>(16);

            internal void Begin(Pawn pawn)
            {
                if (Pawn != null || Traversals.Count != 0)
                    End();
                Pawn = pawn;
            }

            internal void End()
            {
                Traversals.Clear();
                Pawn = null;
            }
        }

        internal sealed class TraversalEntry
        {
            internal readonly Region[] Regions;
            internal TraversalEntry(Region[] regions) { Regions = regions ?? new Region[0]; }
        }

        internal struct TraversalKey : IEquatable<TraversalKey>
        {
            internal readonly Region RootRegion;
            internal readonly int RootX;
            internal readonly int RootZ;
            internal readonly AggressiveReachabilityProfiles.TraverseKey Traverse;
            internal readonly int MaxRegions;
            internal readonly float MaxDistance;
            internal readonly RegionType RegionTypes;

            internal TraversalKey(Region rootRegion, int rootX, int rootZ,
                AggressiveReachabilityProfiles.TraverseKey traverse, int maxRegions,
                float maxDistance, RegionType regionTypes)
            {
                RootRegion = rootRegion;
                RootX = rootX;
                RootZ = rootZ;
                Traverse = traverse;
                MaxRegions = maxRegions;
                MaxDistance = maxDistance;
                RegionTypes = regionTypes;
            }

            public bool Equals(TraversalKey other)
            {
                return ReferenceEquals(RootRegion, other.RootRegion) &&
                    RootX == other.RootX && RootZ == other.RootZ &&
                    Traverse.Equals(other.Traverse) &&
                    MaxRegions == other.MaxRegions &&
                    MaxDistance.Equals(other.MaxDistance) &&
                    RegionTypes == other.RegionTypes;
            }

            public override bool Equals(object obj)
            {
                return obj is TraversalKey && Equals((TraversalKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = RootRegion == null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(RootRegion);
                    hash = hash * 397 ^ RootX;
                    hash = hash * 397 ^ RootZ;
                    hash = hash * 397 ^ Traverse.GetHashCode();
                    hash = hash * 397 ^ MaxRegions;
                    hash = hash * 397 ^ MaxDistance.GetHashCode();
                    hash = hash * 397 ^ (int)RegionTypes;
                    return hash;
                }
            }
        }
    }
}
