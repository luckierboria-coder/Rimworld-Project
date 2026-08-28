using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JS1.1 Lean
    //
    // Keep the successful JS1 search semantics, but remove low-yield bool memoization.
    // Only HasJobOnThing is memoized, and its per-target cache is bucketed by
    // (method, WorkGiver instance, forced) so Pawn/Method/Giver are not hashed for every Thing.
    // The Pawn is implicit in the one synchronous TryIssueJobPackage lifetime.
    //
    // JS1 nearest-order reuse is intentionally preserved unchanged:
    // key = exact source IList identity + root + maxDistance + reachable-mode.
    // No cache survives the JobPackage boundary; no worker wait is introduced.
    internal static class JobPackageLocalSearch0419
    {
        internal const string FeatureId = "ai.jobPackageLocal";

        private const int MaxThingEntriesPerPackage = 8192;
        private const int MaxThingEntriesPerBucket = 4096;
        private const int MaxOrderEntriesPerPackage = 512;
        private const int WarmupVerifyHitsPerMethod = 4;
        private const int VerifyMask = 31; // 1/32 after warmup

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static readonly Dictionary<MethodBase, MethodParityState> MethodParity =
            new Dictionary<MethodBase, MethodParityState>();

        private static int patchedHasThingQueries;
        private static int patchFailures;
        private static bool scopePatched;
        private static bool globalHookPatched;
        private static bool reachableHookPatched;

        private static long packages;
        private static long nestedPackages;
        private static long thingObserved;
        private static long thingHits;
        private static long thingMisses;
        private static long thingStores;
        private static long thingVerifyRuns;
        private static long thingVerifyMatches;
        private static long thingMismatches;
        private static long thingCapBypass;
        private static long thingBucketCreates;
        private static long disabledMethodBypass;

        private static long orderObserved;
        private static long orderHits;
        private static long orderMisses;
        private static long orderStores;
        private static long orderMutationBypass;
        private static long orderCapBypass;

        private static long maxThingEntries;
        private static long maxThingBuckets;
        private static long maxThingBucketEntries;
        private static long maxOrderEntries;
        private static int mismatchLogs;

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

                if (jobGiver == null)
                {
                    FeatureGate.Suppress(FeatureId, "TryIssueJobPackage(Pawn, JobIssueParams) not found");
                    Log.Warning("[RimMT] V0.4.19-JS1.1 Lean unavailable: JobGiver_Work.TryIssueJobPackage target not found.");
                    return;
                }

                HarmonyMethod enter = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackagePrefix));
                enter.priority = Priority.First + 100;
                HarmonyMethod exit = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackageFinalizer));
                exit.priority = Priority.Last - 100;
                harmony.Patch(jobGiver, prefix: enter, finalizer: exit);
                scopePatched = true;

                PatchHasJobOnThingQueries(harmony);
                PatchNearestOrderHooks(harmony);

                Log.Message("[RimMT] V0.4.19-JS1.1 Lean active: scope=" + scopePatched +
                    ", HasJobOnThing=" + patchedHasThingQueries +
                    ", nearestHooks(global/reachable)=" + globalHookPatched + "/" + reachableHookPatched +
                    ". ShouldSkip and HasJobOnCell memoization are removed. HasJobOnThing uses per-method/giver buckets inside one TryIssueJobPackage; JS1 nearest-order semantics are unchanged. Vanilla retains JobOnThing/JobOnCell, Jobs, reservations and final selection.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JS1.1 Lean install failed; JS1/V0.4.18.2 behavior remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchHasJobOnThingQueries(Harmony harmony)
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            List<Type> allTypes = GenTypes.AllTypes;
            MethodInfo prefixMethod = AccessTools.Method(typeof(JobPackageLocalSearch0419), nameof(HasThingPrefix));
            MethodInfo postfixMethod = AccessTools.Method(typeof(JobPackageLocalSearch0419), nameof(HasThingPostfix));

            for (int i = 0; i < allTypes.Count; i++)
            {
                Type type = allTypes[i];
                if (type == null || !typeof(WorkGiver).IsAssignableFrom(type))
                    continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch
                {
                    continue;
                }

                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (!IsSupportedHasThing(method) || !unique.Add(method))
                        continue;

                    try
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First + 50 };
                        HarmonyMethod postfix = new HarmonyMethod(postfixMethod) { priority = Priority.Last - 50 };
                        harmony.Patch(method, prefix: prefix, postfix: postfix);
                        patchedHasThingQueries++;
                    }
                    catch (Exception ex)
                    {
                        patchFailures++;
                        if (patchFailures <= 8)
                            Log.Warning("[RimMT] V0.4.19-JS1.1 Lean skipped HasJobOnThing query " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static void PatchNearestOrderHooks(Harmony harmony)
        {
            MethodBase global = AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalPrefix));
            MethodBase reachable = AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalReachablePrefix));

            if (global != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(OrderPrefix));
                    prefix.priority = Priority.First + 150;
                    HarmonyMethod postfix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(OrderPostfix));
                    postfix.priority = Priority.Last - 150;
                    harmony.Patch(global, prefix: prefix, postfix: postfix);
                    globalHookPatched = true;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    Log.Warning("[RimMT] V0.4.19-JS1.1 Lean GlobalPrefix hook failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (reachable != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(OrderReachablePrefix));
                    prefix.priority = Priority.First + 150;
                    HarmonyMethod postfix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(OrderReachablePostfix));
                    postfix.priority = Priority.Last - 150;
                    harmony.Patch(reachable, prefix: prefix, postfix: postfix);
                    reachableHookPatched = true;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    Log.Warning("[RimMT] V0.4.19-JS1.1 Lean GlobalReachablePrefix hook failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        public static void JobPackagePrefix(Pawn __0, ref PackageScope __state)
        {
            __state = default(PackageScope);
            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = packageDepth == 0;
            packageDepth++;

            if (__state.Outermost)
            {
                current = new PackageContext(__0);
                __state.Context = current;
                Interlocked.Increment(ref packages);
            }
            else
            {
                __state.Context = current;
                Interlocked.Increment(ref nestedPackages);
            }
        }

        public static Exception JobPackageFinalizer(Exception __exception, PackageScope __state)
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
                    UpdateMax(ref maxThingEntries, context.TotalThingEntries);
                    UpdateMax(ref maxThingBuckets, context.ThingBuckets.Count);
                    UpdateMax(ref maxOrderEntries, context.Ordered.Count);
                }
                if (ReferenceEquals(current, context))
                    current = null;
            }

            return __exception;
        }

        public static bool HasThingPrefix(
            WorkGiver __instance,
            MethodBase __originalMethod,
            object[] __args,
            ref bool __result,
            ref HasThingState __state)
        {
            __state = default(HasThingState);
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || __instance == null || __originalMethod == null || __args == null)
                return true;

            Pawn pawn = __args.Length > 0 ? __args[0] as Pawn : null;
            Thing thing = __args.Length > 1 ? __args[1] as Thing : null;
            if (pawn == null || thing == null || context.Pawn == null || !ReferenceEquals(context.Pawn, pawn))
                return true;

            bool forced = __args.Length > 2 && __args[2] is bool && (bool)__args[2];
            Interlocked.Increment(ref thingObserved);

            MethodParityState parity = GetParityState(__originalMethod);
            if (parity.Disabled)
            {
                Interlocked.Increment(ref disabledMethodBypass);
                return true;
            }

            BucketKey bucketKey = new BucketKey(__originalMethod, __instance, forced);
            ThingBucket bucket;
            if (!context.ThingBuckets.TryGetValue(bucketKey, out bucket))
            {
                bucket = new ThingBucket();
                context.ThingBuckets.Add(bucketKey, bucket);
                Interlocked.Increment(ref thingBucketCreates);
            }

            bool cached;
            if (!bucket.Results.TryGetValue(thing, out cached))
            {
                Interlocked.Increment(ref thingMisses);
                __state.Context = context;
                __state.Bucket = bucket;
                __state.Thing = thing;
                __state.Method = __originalMethod;
                __state.Store = context.TotalThingEntries < MaxThingEntriesPerPackage &&
                    bucket.Results.Count < MaxThingEntriesPerBucket;
                if (!__state.Store)
                    Interlocked.Increment(ref thingCapBypass);
                return true;
            }

            Interlocked.Increment(ref thingHits);
            long methodHit = ++parity.Hits;
            bool verify = methodHit <= WarmupVerifyHitsPerMethod || (methodHit & VerifyMask) == 0;
            if (verify)
            {
                Interlocked.Increment(ref thingVerifyRuns);
                __state.Context = context;
                __state.Bucket = bucket;
                __state.Thing = thing;
                __state.Method = __originalMethod;
                __state.Parity = parity;
                __state.Verify = true;
                __state.Cached = cached;
                return true;
            }

            __result = cached;
            __state.AuthoritativeHit = true;
            return false;
        }

        public static void HasThingPostfix(bool __result, HasThingState __state)
        {
            if (__state.AuthoritativeHit)
                return;

            if (__state.Verify && __state.Parity != null)
            {
                if (__result == __state.Cached)
                {
                    __state.Parity.Matches++;
                    Interlocked.Increment(ref thingVerifyMatches);
                }
                else
                {
                    __state.Parity.Mismatches++;
                    __state.Parity.Disabled = true;
                    Interlocked.Increment(ref thingMismatches);
                    if (Interlocked.Increment(ref mismatchLogs) <= 8)
                    {
                        Log.Warning("[RimMT] V0.4.19-JS1.1 Lean HasJobOnThing parity mismatch; caching disabled for " +
                            __state.Method + ". cached=" + __state.Cached + ", live=" + __result +
                            ". Vanilla is authoritative for this sample and all future calls to that method.");
                    }
                }
                return;
            }

            if (__state.Store && __state.Context != null && __state.Bucket != null && __state.Thing != null &&
                ReferenceEquals(current, __state.Context))
            {
                if (!__state.Bucket.Results.ContainsKey(__state.Thing))
                {
                    __state.Bucket.Results.Add(__state.Thing, __result);
                    __state.Context.TotalThingEntries++;
                    UpdateMax(ref maxThingBucketEntries, __state.Bucket.Results.Count);
                    Interlocked.Increment(ref thingStores);
                }
            }
        }

        public static bool OrderPrefix(object[] __0, ref OrderState __state)
        {
            return OrderPrefixCore(__0, false, 0, 1, 2, 4, ref __state);
        }

        public static void OrderPostfix(object[] __0, OrderState __state)
        {
            OrderPostfixCore(__0, 1, __state);
        }

        public static bool OrderReachablePrefix(object[] __0, ref OrderState __state)
        {
            return OrderPrefixCore(__0, true, 0, 2, 5, 7, ref __state);
        }

        public static void OrderReachablePostfix(object[] __0, OrderState __state)
        {
            OrderPostfixCore(__0, 2, __state);
        }

        private static bool OrderPrefixCore(
            object[] args,
            bool reachable,
            int centerIndex,
            int setIndex,
            int maxDistanceIndex,
            int priorityIndex,
            ref OrderState state)
        {
            state = default(OrderState);
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || args == null || args.Length <= priorityIndex || args[priorityIndex] != null)
                return true;

            IList source = args[setIndex] as IList;
            if (source == null || source.Count < 64)
                return true;

            IntVec3 center;
            float maxDistance;
            try
            {
                center = (IntVec3)args[centerIndex];
                maxDistance = Convert.ToSingle(args[maxDistanceIndex]);
            }
            catch
            {
                return true;
            }

            Interlocked.Increment(ref orderObserved);
            OrderKey key = new OrderKey(source, center.x, center.z, maxDistance, reachable);
            OrderedEntry entry;
            if (context.Ordered.TryGetValue(key, out entry))
            {
                if (ValidateCheapSourceProbe(source, entry))
                {
                    args[setIndex] = entry.Ordered;
                    Interlocked.Increment(ref orderHits);
                    return false;
                }

                context.Ordered.Remove(key);
                Interlocked.Increment(ref orderMutationBypass);
            }

            Interlocked.Increment(ref orderMisses);
            if (context.Ordered.Count >= MaxOrderEntriesPerPackage)
            {
                Interlocked.Increment(ref orderCapBypass);
                return true;
            }

            object first;
            object middle;
            object last;
            if (!TryCaptureCheapSourceProbe(source, out first, out middle, out last))
                return true;

            state.Context = context;
            state.Key = key;
            state.Source = source;
            state.Count = source.Count;
            state.First = first;
            state.Middle = middle;
            state.Last = last;
            state.Store = true;
            return true;
        }

        private static void OrderPostfixCore(object[] args, int setIndex, OrderState state)
        {
            if (!state.Store || state.Context == null || !ReferenceEquals(current, state.Context) || args == null || args.Length <= setIndex)
                return;

            Thing[] ordered = args[setIndex] as Thing[];
            if (ordered == null || ReferenceEquals(ordered, state.Source))
                return;

            state.Context.Ordered[state.Key] = new OrderedEntry(
                state.Count,
                state.First,
                state.Middle,
                state.Last,
                ordered);
            Interlocked.Increment(ref orderStores);
        }

        private static bool IsSupportedHasThing(MethodInfo method)
        {
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != typeof(bool) ||
                !string.Equals(method.Name, "HasJobOnThing", StringComparison.Ordinal))
                return false;

            ParameterInfo[] p = method.GetParameters();
            return (p.Length == 2 || p.Length == 3) &&
                p[0].ParameterType == typeof(Pawn) &&
                typeof(Thing).IsAssignableFrom(p[1].ParameterType) &&
                (p.Length == 2 || p[2].ParameterType == typeof(bool));
        }

        private static MethodParityState GetParityState(MethodBase method)
        {
            MethodParityState state;
            if (!MethodParity.TryGetValue(method, out state))
            {
                state = new MethodParityState();
                MethodParity.Add(method, state);
            }
            return state;
        }

        private static bool TryCaptureCheapSourceProbe(IList source, out object first, out object middle, out object last)
        {
            first = null;
            middle = null;
            last = null;
            if (source == null)
                return false;

            try
            {
                int count = source.Count;
                if (count <= 0)
                    return true;
                first = source[0];
                middle = source[count >> 1];
                last = source[count - 1];
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool ValidateCheapSourceProbe(IList source, OrderedEntry entry)
        {
            if (source == null || entry == null || source.Count != entry.Count)
                return false;

            try
            {
                if (entry.Count <= 0)
                    return true;
                return ReferenceEquals(source[0], entry.First) &&
                    ReferenceEquals(source[entry.Count >> 1], entry.Middle) &&
                    ReferenceEquals(source[entry.Count - 1], entry.Last);
            }
            catch
            {
                return false;
            }
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

        internal static string Summary()
        {
            long observed = Interlocked.Read(ref thingObserved);
            long hits = Interlocked.Read(ref thingHits);
            long orderObs = Interlocked.Read(ref orderObserved);
            long orderHit = Interlocked.Read(ref orderHits);
            double thingHitPct = observed == 0 ? 0.0 : hits * 100.0 / observed;
            double orderHitPct = orderObs == 0 ? 0.0 : orderHit * 100.0 / orderObs;

            int disabled = 0;
            foreach (KeyValuePair<MethodBase, MethodParityState> pair in MethodParity)
            {
                if (pair.Value.Disabled)
                    disabled++;
            }

            return "JobPackage-local search V0.4.19-JS1.1 Lean: patched(scope/hasThing/global/reachable)=" +
                scopePatched + "/" + patchedHasThingQueries + "/" + globalHookPatched + "/" + reachableHookPatched +
                ", patchFailures=" + patchFailures +
                ", packages=" + Interlocked.Read(ref packages) +
                ", nested=" + Interlocked.Read(ref nestedPackages) +
                ", hasThingObserved=" + observed +
                ", hasThingHits=" + hits + " (" + thingHitPct.ToString("F1") + "%)" +
                ", hasThingMisses=" + Interlocked.Read(ref thingMisses) +
                ", hasThingStores=" + Interlocked.Read(ref thingStores) +
                ", verify=" + Interlocked.Read(ref thingVerifyRuns) + "/" + Interlocked.Read(ref thingVerifyMatches) +
                ", mismatches=" + Interlocked.Read(ref thingMismatches) +
                ", disabledMethods=" + disabled +
                ", disabledBypass=" + Interlocked.Read(ref disabledMethodBypass) +
                ", capBypass=" + Interlocked.Read(ref thingCapBypass) +
                ", bucketCreates=" + Interlocked.Read(ref thingBucketCreates) +
                ", maxThingEntries=" + Interlocked.Read(ref maxThingEntries) +
                ", maxBuckets=" + Interlocked.Read(ref maxThingBuckets) +
                ", maxBucketEntries=" + Interlocked.Read(ref maxThingBucketEntries) +
                ", orderObserved=" + orderObs +
                ", orderHits=" + orderHit + " (" + orderHitPct.ToString("F1") + "%)" +
                ", orderMisses=" + Interlocked.Read(ref orderMisses) +
                ", orderStores=" + Interlocked.Read(ref orderStores) +
                ", orderMutationBypass=" + Interlocked.Read(ref orderMutationBypass) +
                ", orderCapBypass=" + Interlocked.Read(ref orderCapBypass) +
                ", maxOrderEntries=" + Interlocked.Read(ref maxOrderEntries) +
                ". Lifetime is one synchronous JobPackage. Pawn is implicit in the package scope; Thing lookups are bucketed by method/giver/forced. JS1 nearest-order key semantics are unchanged.";
        }

        internal struct PackageScope
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal struct HasThingState
        {
            internal PackageContext Context;
            internal ThingBucket Bucket;
            internal Thing Thing;
            internal MethodBase Method;
            internal MethodParityState Parity;
            internal bool Store;
            internal bool Verify;
            internal bool Cached;
            internal bool AuthoritativeHit;
        }

        internal struct OrderState
        {
            internal PackageContext Context;
            internal OrderKey Key;
            internal IList Source;
            internal int Count;
            internal object First;
            internal object Middle;
            internal object Last;
            internal bool Store;
        }

        internal sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Dictionary<BucketKey, ThingBucket> ThingBuckets = new Dictionary<BucketKey, ThingBucket>(32);
            internal readonly Dictionary<OrderKey, OrderedEntry> Ordered = new Dictionary<OrderKey, OrderedEntry>(16);
            internal int TotalThingEntries;

            internal PackageContext(Pawn pawn)
            {
                Pawn = pawn;
            }
        }

        internal sealed class ThingBucket
        {
            internal readonly Dictionary<Thing, bool> Results =
                new Dictionary<Thing, bool>(ThingReferenceComparer.Instance);
        }

        internal sealed class MethodParityState
        {
            internal long Hits;
            internal long Matches;
            internal long Mismatches;
            internal bool Disabled;
        }

        internal sealed class OrderedEntry
        {
            internal readonly int Count;
            internal readonly object First;
            internal readonly object Middle;
            internal readonly object Last;
            internal readonly Thing[] Ordered;

            internal OrderedEntry(int count, object first, object middle, object last, Thing[] ordered)
            {
                Count = count;
                First = first;
                Middle = middle;
                Last = last;
                Ordered = ordered;
            }
        }

        internal struct BucketKey : IEquatable<BucketKey>
        {
            internal readonly MethodBase Method;
            internal readonly WorkGiver Giver;
            internal readonly bool Forced;

            internal BucketKey(MethodBase method, WorkGiver giver, bool forced)
            {
                Method = method;
                Giver = giver;
                Forced = forced;
            }

            public bool Equals(BucketKey other)
            {
                return ReferenceEquals(Method, other.Method) &&
                    ReferenceEquals(Giver, other.Giver) &&
                    Forced == other.Forced;
            }

            public override bool Equals(object obj)
            {
                return obj is BucketKey && Equals((BucketKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Method == null ? 0 : RuntimeHelpers.GetHashCode(Method);
                    hash = hash * 397 ^ (Giver == null ? 0 : RuntimeHelpers.GetHashCode(Giver));
                    hash = hash * 397 ^ (Forced ? 1 : 0);
                    return hash;
                }
            }
        }

        internal struct OrderKey : IEquatable<OrderKey>
        {
            internal readonly object Source;
            internal readonly int X;
            internal readonly int Z;
            internal readonly float MaxDistance;
            internal readonly bool Reachable;

            internal OrderKey(object source, int x, int z, float maxDistance, bool reachable)
            {
                Source = source;
                X = x;
                Z = z;
                MaxDistance = maxDistance;
                Reachable = reachable;
            }

            public bool Equals(OrderKey other)
            {
                return ReferenceEquals(Source, other.Source) &&
                    X == other.X && Z == other.Z &&
                    MaxDistance.Equals(other.MaxDistance) &&
                    Reachable == other.Reachable;
            }

            public override bool Equals(object obj)
            {
                return obj is OrderKey && Equals((OrderKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Source == null ? 0 : RuntimeHelpers.GetHashCode(Source);
                    hash = hash * 397 ^ X;
                    hash = hash * 397 ^ Z;
                    hash = hash * 397 ^ MaxDistance.GetHashCode();
                    hash = hash * 397 ^ (Reachable ? 1 : 0);
                    return hash;
                }
            }
        }

        private sealed class ThingReferenceComparer : IEqualityComparer<Thing>
        {
            internal static readonly ThingReferenceComparer Instance = new ThingReferenceComparer();

            public bool Equals(Thing x, Thing y)
            {
                return ReferenceEquals(x, y);
            }

            public int GetHashCode(Thing obj)
            {
                return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj);
            }
        }
    }
}
