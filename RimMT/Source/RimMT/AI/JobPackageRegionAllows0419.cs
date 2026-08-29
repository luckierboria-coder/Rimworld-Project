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
    // V0.4.19-JR1
    //
    // JD1 AutoTrace showed that the dominant JobGiver cost is the
    // ClosestThingReachable -> RegionTraverser.BreadthFirstTraverse chain. Regionwise search
    // repeatedly calls Region.Allows for the same Pawn/TraverseParms/Region tuple dozens of
    // times inside one synchronous TryIssueJobPackage. This cache attacks only that repeated
    // permission predicate; it does NOT cache a traversal, a candidate, a Job, or a final search
    // result.
    //
    // Safety policy:
    //  - lifetime is exactly one outer JobGiver_Work.TryIssueJobPackage;
    //  - the first observation for every Region/key runs the fully patched live Region.Allows;
    //  - authoritative hits are sampled against live Region.Allows (first 4, then 1/32);
    //  - one parity mismatch suppresses this feature for the rest of the run;
    //  - foreign Prefix/Postfix/Finalizer owners on Region.Allows block authority entirely;
    //    foreign Transpilers are allowed because the first live observation naturally executes
    //    their transformed original body and that final value is what is memoized.
    internal static class JobPackageRegionAllows0419
    {
        internal const string FeatureId = "ai.jobRegionAllows";

        private const int MaxEntriesPerPackage = 4096;
        private const int MaxEntriesPerBucket = 2048;
        private const int WarmupVerifyHits = 4;
        private const int VerifyMask = 31;

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;
        [ThreadStatic] private static PackageContext pooledContext;

        private static MethodBase allowsTarget;
        private static bool scopePatched;
        private static bool allowsPatched;
        private static bool compatibilityReady;
        private static int patchFailures;

        private static long packages;
        private static long nested;
        private static long observed;
        private static long hits;
        private static long misses;
        private static long stores;
        private static long verifyRuns;
        private static long verifyMatches;
        private static long mismatches;
        private static long capBypass;
        private static long bucketCreates;
        private static long bucketFastHits;
        private static long authoritativeHits;
        private static long compatibilityBypass;
        private static long contextCreates;
        private static long contextReuse;
        private static long contextReturns;
        private static long maxEntries;
        private static long maxBuckets;
        private static long maxBucketEntries;

        private static long parityHitSerial;

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
                allowsTarget = AccessTools.Method(
                    typeof(Region),
                    nameof(Region.Allows),
                    new Type[] { typeof(TraverseParms), typeof(bool) });

                if (jobGiver == null || allowsTarget == null)
                {
                    FeatureGate.Suppress(FeatureId, "required JobGiver/Region.Allows target not found");
                    Log.Warning("[RimMT] V0.4.19-JR1 Region.Allows memo unavailable: required target not found.");
                    return;
                }

                HarmonyMethod scopePrefix = new HarmonyMethod(typeof(JobPackageRegionAllows0419), nameof(JobPackagePrefix));
                scopePrefix.priority = Priority.First + 80;
                HarmonyMethod scopeFinalizer = new HarmonyMethod(typeof(JobPackageRegionAllows0419), nameof(JobPackageFinalizer));
                scopeFinalizer.priority = Priority.Last - 80;
                harmony.Patch(jobGiver, prefix: scopePrefix, finalizer: scopeFinalizer);
                scopePatched = true;

                // Run after normal foreign prefixes so their ownership/side effects are respected.
                // CompatibilityReady later refuses authority if any foreign control prefix/postfix/
                // finalizer exists. Transpilers are intentionally allowed.
                HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageRegionAllows0419), nameof(AllowsPrefix));
                prefix.priority = Priority.Last;
                HarmonyMethod postfix = new HarmonyMethod(typeof(JobPackageRegionAllows0419), nameof(AllowsPostfix));
                postfix.priority = Priority.Last;
                harmony.Patch(allowsTarget, prefix: prefix, postfix: postfix);
                allowsPatched = true;

                Log.Message("[RimMT] V0.4.19-JR1 JobPackage Region.Allows memo installed: one synchronous JobPackage lifetime, live-first storage, sampled parity, no traversal/result/Job caching. Foreign transpilers are preserved; foreign control prefixes/postfixes/finalizers fail closed.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JR1 Region.Allows memo install failed; live Region.Allows remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            if (!FeatureGate.IsEnabled(FeatureId) || allowsTarget == null)
                return;

            Patches patches = Harmony.GetPatchInfo(allowsTarget);
            string blocker = FirstForeignControlOwner(patches);
            if (blocker != null)
            {
                FeatureGate.Suppress(FeatureId, "foreign Region.Allows control patch by '" + blocker + "'");
                Log.Warning("[RimMT] V0.4.19-JR1 Region.Allows memo disabled because foreign Prefix/Postfix/Finalizer owner '" + blocker + "' also controls Region.Allows. Foreign transpilers alone are safe and remain allowed.");
                return;
            }

            compatibilityReady = true;
        }

        private static string FirstForeignControlOwner(Patches patches)
        {
            if (patches == null)
                return null;

            string owner = FirstForeignOwner(patches.Prefixes);
            if (owner != null) return owner;
            owner = FirstForeignOwner(patches.Postfixes);
            if (owner != null) return owner;
            return FirstForeignOwner(patches.Finalizers);
        }

        private static string FirstForeignOwner(IList<Patch> patches)
        {
            if (patches == null)
                return null;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                string owner = patch == null ? null : patch.owner;
                if (!string.IsNullOrEmpty(owner) && owner != RimMTBootstrap.HarmonyId)
                    return owner;
            }
            return null;
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
                nested++;
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
                if (context != null)
                {
                    UpdateMax(ref maxEntries, context.TotalEntries);
                    UpdateMax(ref maxBuckets, context.Buckets.Count);
                    UpdateMax(ref maxBucketEntries, context.MaxBucketEntriesSeen);
                }
                if (ReferenceEquals(current, context))
                    current = null;
                Release(context);
            }
            return __exception;
        }

        public static bool AllowsPrefix(
            Region __instance,
            TraverseParms tp,
            bool isDestination,
            bool __runOriginal,
            ref bool __result,
            ref AllowsState __state)
        {
            __state = default(AllowsState);
            PackageContext context = current;
            if (!__runOriginal || context == null || packageDepth <= 0 || !compatibilityReady ||
                !FeatureGate.IsEnabled(FeatureId) || __instance == null)
            {
                if (context != null && packageDepth > 0 && !compatibilityReady)
                    compatibilityBypass++;
                return true;
            }

            Pawn pawn = tp.pawn;
            if (pawn == null || context.Pawn == null || !ReferenceEquals(pawn, context.Pawn))
                return true;

            observed++;
            AggressiveReachabilityProfiles.TraverseKey traverseKey = new AggressiveReachabilityProfiles.TraverseKey(tp);
            AllowsBucketKey key = new AllowsBucketKey(traverseKey, isDestination);
            bool created;
            RegionBucket bucket = context.GetBucket(key, out created);
            if (created)
                bucketCreates++;

            bool cached;
            if (!bucket.Results.TryGetValue(__instance, out cached))
            {
                misses++;
                __state.Context = context;
                __state.Bucket = bucket;
                __state.Region = __instance;
                __state.Store = context.TotalEntries < MaxEntriesPerPackage && bucket.Results.Count < MaxEntriesPerBucket;
                if (!__state.Store)
                    capBypass++;
                return true;
            }

            hits++;
            long serial = ++parityHitSerial;
            bool verify = serial <= WarmupVerifyHits || (serial & VerifyMask) == 0;
            if (verify)
            {
                verifyRuns++;
                __state.Context = context;
                __state.Bucket = bucket;
                __state.Region = __instance;
                __state.Verify = true;
                __state.Cached = cached;
                return true;
            }

            __result = cached;
            __state.AuthoritativeHit = true;
            authoritativeHits++;
            return false;
        }

        public static void AllowsPostfix(bool __result, AllowsState __state)
        {
            if (__state.AuthoritativeHit)
                return;

            if (__state.Verify)
            {
                if (__result == __state.Cached)
                {
                    verifyMatches++;
                }
                else
                {
                    mismatches++;
                    FeatureGate.Suppress(FeatureId, "JR1 Region.Allows package-local parity mismatch");
                    Log.Warning("[RimMT] V0.4.19-JR1 Region.Allows memo disabled after parity mismatch. cached=" + __state.Cached + ", live=" + __result + ". Live fully-patched Region.Allows is authoritative again.");
                }
                return;
            }

            if (__state.Store && __state.Context != null && __state.Bucket != null && __state.Region != null &&
                ReferenceEquals(current, __state.Context) && !__state.Bucket.Results.ContainsKey(__state.Region))
            {
                __state.Bucket.Results.Add(__state.Region, __result);
                __state.Context.TotalEntries++;
                if (__state.Bucket.Results.Count > __state.Context.MaxBucketEntriesSeen)
                    __state.Context.MaxBucketEntriesSeen = __state.Bucket.Results.Count;
                stores++;
            }
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

        private static void UpdateMax(ref long field, long value)
        {
            if (value > field)
                field = value;
        }

        internal static string Summary()
        {
            long obs = observed;
            long h = hits;
            double hitPct = obs == 0 ? 0.0 : h * 100.0 / obs;
            return "JobPackage Region.Allows JR1: patched(scope/allows)=" + scopePatched + "/" + allowsPatched +
                ", compatibilityReady=" + compatibilityReady +
                ", patchFailures=" + patchFailures +
                ", packages=" + packages +
                ", nested=" + nested +
                ", observed=" + obs +
                ", hits=" + h + " (" + hitPct.ToString("F1") + "%)" +
                ", misses=" + misses +
                ", stores=" + stores +
                ", authoritativeHits=" + authoritativeHits +
                ", verify=" + verifyRuns + "/" + verifyMatches +
                ", mismatches=" + mismatches +
                ", capBypass=" + capBypass +
                ", bucketCreates=" + bucketCreates +
                ", bucketFastHits=" + bucketFastHits +
                ", compatibilityBypass=" + compatibilityBypass +
                ", context(create/reuse/return)=" + contextCreates + "/" + contextReuse + "/" + contextReturns +
                ", maxEntries=" + maxEntries +
                ", maxBuckets=" + maxBuckets +
                ", maxBucketEntries=" + maxBucketEntries +
                ". Live-first one-JobPackage memo only; no Region traversal, candidate, Job, reservation or cross-package result is cached.";
        }

        internal struct ScopeState
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal struct AllowsState
        {
            internal PackageContext Context;
            internal RegionBucket Bucket;
            internal Region Region;
            internal bool Store;
            internal bool Verify;
            internal bool Cached;
            internal bool AuthoritativeHit;
        }

        internal sealed class PackageContext
        {
            internal Pawn Pawn;
            internal readonly Dictionary<AllowsBucketKey, RegionBucket> Buckets = new Dictionary<AllowsBucketKey, RegionBucket>(8);
            internal int TotalEntries;
            internal int MaxBucketEntriesSeen;

            private AllowsBucketKey lastKey;
            private RegionBucket lastBucket;
            private bool hasLast;

            internal void Begin(Pawn pawn)
            {
                if (Pawn != null || Buckets.Count != 0)
                    End();
                Pawn = pawn;
                TotalEntries = 0;
                MaxBucketEntriesSeen = 0;
                hasLast = false;
                lastBucket = null;
            }

            internal RegionBucket GetBucket(AllowsBucketKey key, out bool created)
            {
                if (hasLast && lastKey.Equals(key) && lastBucket != null)
                {
                    bucketFastHits++;
                    created = false;
                    return lastBucket;
                }

                RegionBucket bucket;
                if (!Buckets.TryGetValue(key, out bucket))
                {
                    bucket = new RegionBucket();
                    Buckets.Add(key, bucket);
                    created = true;
                }
                else
                {
                    created = false;
                }

                lastKey = key;
                lastBucket = bucket;
                hasLast = true;
                return bucket;
            }

            internal void End()
            {
                hasLast = false;
                lastBucket = null;
                // Only the small top-level bucket map is cleared. Inner Region dictionaries are
                // not synchronously cleared; dropping the references makes them GC-eligible.
                Buckets.Clear();
                Pawn = null;
                TotalEntries = 0;
                MaxBucketEntriesSeen = 0;
            }
        }

        internal sealed class RegionBucket
        {
            internal readonly Dictionary<Region, bool> Results =
                new Dictionary<Region, bool>(RegionReferenceComparer.Instance);
        }

        internal struct AllowsBucketKey : IEquatable<AllowsBucketKey>
        {
            internal readonly AggressiveReachabilityProfiles.TraverseKey Traverse;
            internal readonly bool IsDestination;

            internal AllowsBucketKey(AggressiveReachabilityProfiles.TraverseKey traverse, bool isDestination)
            {
                Traverse = traverse;
                IsDestination = isDestination;
            }

            public bool Equals(AllowsBucketKey other)
            {
                return Traverse.Equals(other.Traverse) && IsDestination == other.IsDestination;
            }

            public override bool Equals(object obj)
            {
                return obj is AllowsBucketKey && Equals((AllowsBucketKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return Traverse.GetHashCode() * 397 ^ (IsDestination ? 1 : 0);
                }
            }
        }

        private sealed class RegionReferenceComparer : IEqualityComparer<Region>
        {
            internal static readonly RegionReferenceComparer Instance = new RegionReferenceComparer();

            public bool Equals(Region x, Region y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(Region obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
