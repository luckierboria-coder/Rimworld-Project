using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JR1.1 learned Regionwise cache.
    //
    // JR1's first implementation built a complete Region BFS on a cache miss and then scanned
    // the candidates again. That could make a cold key more expensive than Vanilla, especially
    // when Vanilla would have stopped after finding a valid candidate in only a few regions.
    //
    // JR1.1 instead lets the first call execute Vanilla exactly once. While Vanilla's own
    // RegionTraverser is running, RimMT wraps only that RegionProcessor and records the Region
    // order. If Vanilla's processor asks RegionTraverser to stop, the wrapper preserves the
    // original result state but keeps traversing without invoking the original processor again;
    // this learns the remaining Region order without re-running validators/candidate scans.
    // Subsequent identical traversal shapes reuse the learned Region order and execute the live
    // request/validator over it, removing repeated RegionTraverser work.
    //
    // No Job, reservation, final candidate, WorkGiver result, or cross-package search result is
    // cached. The lifetime remains one synchronous JobGiver_Work.TryIssueJobPackage.
    internal static class JobPackageRegionwiseCache0419
    {
        internal const string FeatureId = "ai.jobRegionwise";

        private const int MaxTraversalEntriesPerPackage = 128;
        private const int MaxDestinationAllowsEntriesPerPackage = 4096;

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;
        [ThreadStatic] private static PackageContext pooledContext;
        [ThreadStatic] private static CaptureState pendingCapture;

        private static MethodBase regionwiseTarget;
        private static MethodBase breadthFirstTarget;
        private static bool scopePatched;
        private static bool regionwisePatched;
        private static bool breadthFirstPatched;
        private static int patchFailures;

        private static long packages;
        private static long observed;
        private static long accelerated;
        private static long cacheHits;
        private static long cacheMisses;
        private static long captureArmed;
        private static long captureStores;
        private static long captureSkippedNested;
        private static long cacheCapBypass;
        private static long captureFailures;
        private static long acceleratedFailures;
        private static long regionsCaptured;
        private static long extraRegionsAfterVanillaStop;
        private static long regionsScanned;
        private static long candidatesScanned;
        private static long validatorCalls;
        private static long destinationAllowsHits;
        private static long destinationAllowsMisses;
        private static long destinationAllowsCapBypass;
        private static long forbiddenHits;
        private static long forbiddenMisses;
        private static long contextCreates;
        private static long contextReuse;
        private static long contextReturns;
        private static long maxEntries;
        private static long maxRegionsPerEntry;
        private static long maxDestinationAllowsEntries;

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

                breadthFirstTarget = AccessTools.Method(
                    typeof(RegionTraverser),
                    nameof(RegionTraverser.BreadthFirstTraverse),
                    new Type[] { typeof(Region), typeof(RegionEntryPredicate), typeof(RegionProcessor), typeof(int), typeof(RegionType) });

                if (jobGiver == null || regionwiseTarget == null || breadthFirstTarget == null)
                {
                    FeatureGate.Suppress(FeatureId, "required JobGiver/RegionwiseBFSWorker/BreadthFirstTraverse target not found");
                    Log.Warning("[RimMT] V0.4.19-JR1.1 Regionwise cache unavailable: required target not found.");
                    return;
                }

                HarmonyMethod scopePrefix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(JobPackagePrefix));
                scopePrefix.priority = Priority.First + 70;
                HarmonyMethod scopeFinalizer = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(JobPackageFinalizer));
                scopeFinalizer.priority = Priority.Last - 70;
                harmony.Patch(jobGiver, prefix: scopePrefix, finalizer: scopeFinalizer);
                scopePatched = true;

                HarmonyMethod regionwisePrefix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(RegionwisePrefix));
                regionwisePrefix.priority = Priority.First + 200;
                HarmonyMethod regionwisePostfix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(RegionwisePostfix));
                regionwisePostfix.priority = Priority.Last - 200;
                HarmonyMethod regionwiseFinalizer = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(RegionwiseFinalizer));
                regionwiseFinalizer.priority = Priority.Last - 200;
                harmony.Patch(regionwiseTarget, prefix: regionwisePrefix, postfix: regionwisePostfix, finalizer: regionwiseFinalizer);
                regionwisePatched = true;

                HarmonyMethod bfsPrefix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(BreadthFirstPrefix));
                bfsPrefix.priority = Priority.First + 250;
                HarmonyMethod bfsPostfix = new HarmonyMethod(typeof(JobPackageRegionwiseCache0419), nameof(BreadthFirstPostfix));
                bfsPostfix.priority = Priority.Last - 250;
                harmony.Patch(breadthFirstTarget, prefix: bfsPrefix, postfix: bfsPostfix);
                breadthFirstPatched = true;

                Log.Message("[RimMT] V0.4.19-JR1.1 learned Regionwise cache installed. Cold keys run Vanilla once while RimMT records the real BFS order; hot keys skip RegionTraverser and rescan live candidates/validators over that learned order. No global Region.Allows detour is used.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JR1.1 Regionwise cache install failed; Vanilla Regionwise search remains. " + ex.GetType().Name + ": " + ex.Message);
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
                if (pendingCapture != null && ReferenceEquals(pendingCapture.Context, __state.Context))
                    pendingCapture = null;

                PackageContext context = __state.Context;
                if (context != null)
                {
                    if (context.Traversals.Count > maxEntries)
                        maxEntries = context.Traversals.Count;
                    if (context.DestinationAllows.Count > maxDestinationAllowsEntries)
                        maxDestinationAllowsEntries = context.DestinationAllows.Count;
                }
                if (ReferenceEquals(current, context))
                    current = null;
                Release(context);
            }
            return __exception;
        }

        // On a hot key, skip Vanilla RegionTraverser and scan live candidates over the learned
        // Region order. On a cold key, arm capture and let Vanilla execute once.
        public static bool RegionwisePrefix(object[] __args, ref Thing __result, ref RegionwiseCallState __state)
        {
            __state = default(RegionwiseCallState);
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
                    return true; // Let Vanilla preserve exact edge/error semantics for cold/null-root cases.

                AggressiveReachabilityProfiles.TraverseKey traverseKey = new AggressiveReachabilityProfiles.TraverseKey(traverseParams);
                TraversalKey key = new TraversalKey(
                    rootRegion,
                    root.x,
                    root.z,
                    traverseKey,
                    maxRegions,
                    maxDistance,
                    traversableRegionTypes);

                TraversalEntry traversal;
                if (context.Traversals.TryGetValue(key, out traversal))
                {
                    cacheHits++;
                    int seen;
                    Thing result = ScanTraversal(
                        context,
                        traversal,
                        traverseKey,
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

                cacheMisses++;
                if (context.Traversals.Count >= MaxTraversalEntriesPerPackage)
                {
                    cacheCapBypass++;
                    return true;
                }

                // A nested Regionwise call while another cold call is being learned stays fully
                // Vanilla. This avoids corrupting capture state from validators that themselves
                // perform reachability/work searches.
                if (pendingCapture != null)
                {
                    captureSkippedNested++;
                    return true;
                }

                CaptureState capture = new CaptureState(
                    context,
                    key,
                    rootRegion,
                    maxRegions,
                    traversableRegionTypes);
                pendingCapture = capture;
                __state.Capture = capture;
                captureArmed++;
                return true;
            }
            catch (Exception ex)
            {
                acceleratedFailures++;
                if (acceleratedFailures <= 8)
                    Log.Warning("[RimMT] V0.4.19-JR1.1 Regionwise hot-path setup failed; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        public static void RegionwisePostfix(RegionwiseCallState __state)
        {
            ClearPendingCapture(__state.Capture);
        }

        public static Exception RegionwiseFinalizer(Exception __exception, RegionwiseCallState __state)
        {
            if (__exception != null && __state.Capture != null)
                captureFailures++;
            ClearPendingCapture(__state.Capture);
            return __exception;
        }

        private static void ClearPendingCapture(CaptureState capture)
        {
            if (capture != null && ReferenceEquals(pendingCapture, capture))
                pendingCapture = null;
        }

        // This hook only wraps the specific outer BreadthFirstTraverse invoked by the armed
        // RegionwiseBFSWorker. Nested Reachability/RegionTraverser calls made by validators see
        // capture.Attached=true and remain untouched.
        public static void BreadthFirstPrefix(
            Region __0,
            RegionEntryPredicate __1,
            ref RegionProcessor __2,
            int __3,
            RegionType __4,
            ref BreadthFirstState __state)
        {
            __state = default(BreadthFirstState);
            CaptureState capture = pendingCapture;
            if (capture == null || capture.Attached || __2 == null ||
                !ReferenceEquals(__0, capture.RootRegion) || __3 != capture.MaxRegions || __4 != capture.RegionTypes)
                return;

            RegionProcessor original = __2;
            capture.Attached = true;
            __state.Capture = capture;
            __state.Attached = true;

            __2 = delegate(Region region)
            {
                capture.Regions.Add(region);

                if (!capture.OriginalStopped)
                {
                    bool stop = original(region);
                    if (stop)
                    {
                        capture.OriginalStopped = true;
                        capture.VanillaStopRegionCount = capture.Regions.Count;
                    }
                    return false; // Learn the rest of the BFS order without more validator work.
                }

                capture.ExtraRegions++;
                return false;
            };
        }

        public static void BreadthFirstPostfix(BreadthFirstState __state)
        {
            if (!__state.Attached || __state.Capture == null)
                return;

            CaptureState capture = __state.Capture;
            capture.Completed = true;

            PackageContext context = capture.Context;
            if (context == null || !ReferenceEquals(current, context) || context.Traversals.ContainsKey(capture.Key))
                return;

            try
            {
                Region[] learned = capture.Regions.ToArray();
                TraversalEntry entry = new TraversalEntry(learned);
                context.Traversals.Add(capture.Key, entry);
                captureStores++;
                regionsCaptured += learned.Length;
                extraRegionsAfterVanillaStop += capture.ExtraRegions;
                if (learned.Length > maxRegionsPerEntry)
                    maxRegionsPerEntry = learned.Length;
            }
            catch
            {
                captureFailures++;
            }
        }

        private static Thing ScanTraversal(
            PackageContext context,
            TraversalEntry traversal,
            AggressiveReachabilityProfiles.TraverseKey traverseKey,
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
            int scanned = 0;
            Region[] regions = traversal.Regions;

            for (int r = 0; r < regions.Length; r++)
            {
                Region region = regions[r];
                if (region == null || !region.valid)
                    continue;

                scanned++;
                if (RegionTraverser.ShouldCountRegion(region))
                    seen++;

                if (!region.IsDoorway && !GetDestinationAllows(context, region, traverseKey, traverseParams))
                {
                    if (seen >= minRegions && closestThing != null)
                        break;
                    continue;
                }

                if (!ignoreEntirelyForbiddenRegions || !GetForbidden(context, region, traverseParams.pawn))
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
            regionsScanned += scanned;
            return closestThing;
        }

        private static bool GetDestinationAllows(
            PackageContext context,
            Region region,
            AggressiveReachabilityProfiles.TraverseKey traverseKey,
            TraverseParms traverseParams)
        {
            RegionAllowsKey key = new RegionAllowsKey(region, traverseKey);
            bool value;
            if (context.DestinationAllows.TryGetValue(key, out value))
            {
                destinationAllowsHits++;
                return value;
            }

            destinationAllowsMisses++;
            value = region.Allows(traverseParams, true);
            if (context.DestinationAllows.Count < MaxDestinationAllowsEntriesPerPackage)
                context.DestinationAllows.Add(key, value);
            else
                destinationAllowsCapBypass++;
            return value;
        }

        private static bool GetForbidden(PackageContext context, Region region, Pawn pawn)
        {
            bool value;
            if (context.Forbidden.TryGetValue(region, out value))
            {
                forbiddenHits++;
                return value;
            }

            forbiddenMisses++;
            value = region.IsForbiddenEntirely(pawn);
            context.Forbidden[region] = value;
            return value;
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
            long allowDenom = destinationAllowsHits + destinationAllowsMisses;
            double allowHitPct = allowDenom == 0 ? 0.0 : destinationAllowsHits * 100.0 / allowDenom;

            return "JobPackage Regionwise JR1.1 Learned: patched(scope/regionwise/bfs)=" + scopePatched + "/" + regionwisePatched + "/" + breadthFirstPatched +
                ", patchFailures=" + patchFailures +
                ", packages=" + packages +
                ", observed=" + obs +
                ", accelerated=" + acc + " (" + accPct.ToString("F1") + "%)" +
                ", traversalHit/miss=" + cacheHits + "/" + cacheMisses + " (" + hitPct.ToString("F1") + "% hit)" +
                ", captureArmed/stored=" + captureArmed + "/" + captureStores +
                ", captureSkippedNested=" + captureSkippedNested +
                ", capBypass=" + cacheCapBypass +
                ", captureFailures=" + captureFailures +
                ", acceleratedFailures=" + acceleratedFailures +
                ", regionsCaptured=" + regionsCaptured +
                ", extraRegionsAfterVanillaStop=" + extraRegionsAfterVanillaStop +
                ", regionsScanned=" + regionsScanned +
                ", candidatesScanned=" + candidatesScanned +
                ", validatorCalls=" + validatorCalls +
                ", destinationAllowsHit/miss=" + destinationAllowsHits + "/" + destinationAllowsMisses + " (" + allowHitPct.ToString("F1") + "% hit)" +
                ", destinationAllowsCapBypass=" + destinationAllowsCapBypass +
                ", forbiddenHit/miss=" + forbiddenHits + "/" + forbiddenMisses +
                ", context(create/reuse/return)=" + contextCreates + "/" + contextReuse + "/" + contextReturns +
                ", maxEntries=" + maxEntries +
                ", maxRegionsPerEntry=" + maxRegionsPerEntry +
                ", maxDestinationAllowsEntries=" + maxDestinationAllowsEntries +
                ". Cold keys execute Vanilla once and are learned in-flight; hot keys reuse Region order only. No global Region.Allows Harmony detour.";
        }

        internal struct ScopeState
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal struct RegionwiseCallState
        {
            internal CaptureState Capture;
        }

        internal struct BreadthFirstState
        {
            internal bool Attached;
            internal CaptureState Capture;
        }

        internal sealed class CaptureState
        {
            internal readonly PackageContext Context;
            internal readonly TraversalKey Key;
            internal readonly Region RootRegion;
            internal readonly int MaxRegions;
            internal readonly RegionType RegionTypes;
            internal readonly List<Region> Regions;
            internal bool Attached;
            internal bool Completed;
            internal bool OriginalStopped;
            internal int VanillaStopRegionCount;
            internal int ExtraRegions;

            internal CaptureState(PackageContext context, TraversalKey key, Region rootRegion, int maxRegions, RegionType regionTypes)
            {
                Context = context;
                Key = key;
                RootRegion = rootRegion;
                MaxRegions = maxRegions;
                RegionTypes = regionTypes;
                Regions = new List<Region>(Math.Min(Math.Max(maxRegions, 4), 64));
            }
        }

        internal sealed class PackageContext
        {
            internal Pawn Pawn;
            internal readonly Dictionary<TraversalKey, TraversalEntry> Traversals = new Dictionary<TraversalKey, TraversalEntry>(16);
            internal readonly Dictionary<RegionAllowsKey, bool> DestinationAllows = new Dictionary<RegionAllowsKey, bool>(128);
            internal readonly Dictionary<Region, bool> Forbidden = new Dictionary<Region, bool>(RegionReferenceComparer.Instance);

            internal void Begin(Pawn pawn)
            {
                if (Pawn != null || Traversals.Count != 0 || DestinationAllows.Count != 0 || Forbidden.Count != 0)
                    End();
                Pawn = pawn;
            }

            internal void End()
            {
                Traversals.Clear();
                DestinationAllows.Clear();
                Forbidden.Clear();
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
                    int hash = RootRegion == null ? 0 : RuntimeHelpers.GetHashCode(RootRegion);
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

        internal struct RegionAllowsKey : IEquatable<RegionAllowsKey>
        {
            internal readonly Region Region;
            internal readonly AggressiveReachabilityProfiles.TraverseKey Traverse;

            internal RegionAllowsKey(Region region, AggressiveReachabilityProfiles.TraverseKey traverse)
            {
                Region = region;
                Traverse = traverse;
            }

            public bool Equals(RegionAllowsKey other)
            {
                return ReferenceEquals(Region, other.Region) && Traverse.Equals(other.Traverse);
            }

            public override bool Equals(object obj)
            {
                return obj is RegionAllowsKey && Equals((RegionAllowsKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Region == null ? 0 : RuntimeHelpers.GetHashCode(Region);
                    return hash * 397 ^ Traverse.GetHashCode();
                }
            }
        }

        private sealed class RegionReferenceComparer : IEqualityComparer<Region>
        {
            internal static readonly RegionReferenceComparer Instance = new RegionReferenceComparer();
            public bool Equals(Region x, Region y) { return ReferenceEquals(x, y); }
            public int GetHashCode(Region obj) { return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
