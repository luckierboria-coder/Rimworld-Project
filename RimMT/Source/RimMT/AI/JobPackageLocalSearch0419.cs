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
    // V0.4.19-JS1
    // Scope every cache to one synchronous JobGiver_Work.TryIssueJobPackage call.
    // No entry survives the package boundary and no worker wait is introduced.
    internal static class JobPackageLocalSearch0419
    {
        internal const string FeatureId = "ai.jobPackageLocal";

        private const int MaxWorkEntriesPerPackage = 16384;
        private const int MaxOrderEntriesPerPackage = 512;
        private const int WarmupVerifyHitsPerMethod = 4;
        private const int VerifyMask = 31; // 1/32 after warmup

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static readonly Dictionary<MethodBase, MethodParityState> MethodParity =
            new Dictionary<MethodBase, MethodParityState>();

        private static int patchedWorkQueries;
        private static int patchFailures;
        private static bool scopePatched;
        private static bool globalHookPatched;
        private static bool reachableHookPatched;

        private static long packages;
        private static long nestedPackages;
        private static long workObserved;
        private static long workHits;
        private static long workMisses;
        private static long workStores;
        private static long workVerifyRuns;
        private static long workVerifyMatches;
        private static long workMismatches;
        private static long workCapBypass;
        private static long shouldSkipHits;
        private static long hasThingHits;
        private static long hasCellHits;
        private static long disabledMethodBypass;

        private static long orderObserved;
        private static long orderHits;
        private static long orderMisses;
        private static long orderStores;
        private static long orderMutationBypass;
        private static long orderCapBypass;

        private static long maxWorkEntries;
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
                    Log.Warning("[RimMT] V0.4.19-JS1 unavailable: JobGiver_Work.TryIssueJobPackage target not found.");
                    return;
                }

                HarmonyMethod enter = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackagePrefix));
                enter.priority = Priority.First + 100;
                HarmonyMethod exit = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackageFinalizer));
                exit.priority = Priority.Last - 100;
                harmony.Patch(jobGiver, prefix: enter, finalizer: exit);
                scopePatched = true;

                PatchWorkQueries(harmony);
                PatchNearestOrderHooks(harmony);

                Log.Message("[RimMT] V0.4.19-JS1 JobPackage-local search active: scope=" + scopePatched +
                    ", workQueries=" + patchedWorkQueries +
                    ", nearestHooks(global/reachable)=" + globalHookPatched + "/" + reachableHookPatched +
                    ". Only ShouldSkip/HasJobOnThing/HasJobOnCell bool results are memoized. JobOnThing/JobOnCell, Jobs and reservations remain Vanilla-authoritative. Cache lifetime is one TryIssueJobPackage call; sampled mismatches fuse only the offending method.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JS1 install failed; V0.4.18.2 behavior remains. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchWorkQueries(Harmony harmony)
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            List<Type> allTypes = GenTypes.AllTypes;
            MethodInfo prefixMethod = AccessTools.Method(typeof(JobPackageLocalSearch0419), nameof(WorkQueryPrefix));
            MethodInfo postfixMethod = AccessTools.Method(typeof(JobPackageLocalSearch0419), nameof(WorkQueryPostfix));

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
                    QueryKind ignored;
                    if (!TryClassifyWorkQuery(method, out ignored) || !unique.Add(method))
                        continue;

                    try
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First + 50 };
                        HarmonyMethod postfix = new HarmonyMethod(postfixMethod) { priority = Priority.Last - 50 };
                        harmony.Patch(method, prefix: prefix, postfix: postfix);
                        patchedWorkQueries++;
                    }
                    catch (Exception ex)
                    {
                        patchFailures++;
                        if (patchFailures <= 8)
                            Log.Warning("[RimMT] V0.4.19-JS1 skipped WorkGiver query " + method + ": " + ex.GetType().Name + ": " + ex.Message);
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
                    Log.Warning("[RimMT] V0.4.19-JS1 GlobalPrefix hook failed: " + ex.GetType().Name + ": " + ex.Message);
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
                    Log.Warning("[RimMT] V0.4.19-JS1 GlobalReachablePrefix hook failed: " + ex.GetType().Name + ": " + ex.Message);
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
                    UpdateMax(ref maxWorkEntries, context.WorkResults.Count);
                    UpdateMax(ref maxOrderEntries, context.Ordered.Count);
                }
                if (ReferenceEquals(current, context))
                    current = null;
            }

            return __exception;
        }

        public static bool WorkQueryPrefix(
            WorkGiver __instance,
            MethodBase __originalMethod,
            object[] __args,
            ref bool __result,
            ref WorkQueryState __state)
        {
            __state = default(WorkQueryState);
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || __instance == null || __originalMethod == null || __args == null)
                return true;

            QueryKind kind;
            WorkQueryKey key;
            if (!TryBuildWorkKey(__instance, __originalMethod, __args, out kind, out key))
                return true;

            Interlocked.Increment(ref workObserved);

            MethodParityState parity = GetParityState(__originalMethod);
            if (parity.Disabled)
            {
                Interlocked.Increment(ref disabledMethodBypass);
                return true;
            }

            bool cached;
            if (!context.WorkResults.TryGetValue(key, out cached))
            {
                Interlocked.Increment(ref workMisses);
                __state.Context = context;
                __state.Key = key;
                __state.Store = context.WorkResults.Count < MaxWorkEntriesPerPackage;
                if (!__state.Store)
                    Interlocked.Increment(ref workCapBypass);
                return true;
            }

            Interlocked.Increment(ref workHits);
            if (kind == QueryKind.ShouldSkip) Interlocked.Increment(ref shouldSkipHits);
            else if (kind == QueryKind.HasThing) Interlocked.Increment(ref hasThingHits);
            else if (kind == QueryKind.HasCell) Interlocked.Increment(ref hasCellHits);

            long methodHit = ++parity.Hits;
            bool verify = methodHit <= WarmupVerifyHitsPerMethod || (methodHit & VerifyMask) == 0;
            if (verify)
            {
                Interlocked.Increment(ref workVerifyRuns);
                __state.Context = context;
                __state.Key = key;
                __state.Parity = parity;
                __state.Verify = true;
                __state.Cached = cached;
                return true;
            }

            __result = cached;
            __state.AuthoritativeHit = true;
            return false;
        }

        public static void WorkQueryPostfix(bool __result, WorkQueryState __state)
        {
            if (__state.AuthoritativeHit)
                return;

            if (__state.Verify && __state.Parity != null)
            {
                if (__result == __state.Cached)
                {
                    __state.Parity.Matches++;
                    Interlocked.Increment(ref workVerifyMatches);
                }
                else
                {
                    __state.Parity.Mismatches++;
                    __state.Parity.Disabled = true;
                    Interlocked.Increment(ref workMismatches);
                    if (Interlocked.Increment(ref mismatchLogs) <= 8)
                    {
                        Log.Warning("[RimMT] V0.4.19-JS1 bool-query parity mismatch; caching disabled for " +
                            __state.Key.Method + ". cached=" + __state.Cached + ", live=" + __result +
                            ". Vanilla is authoritative for this sample and all future calls to that method.");
                    }
                }
                return;
            }

            if (__state.Store && __state.Context != null && ReferenceEquals(current, __state.Context))
            {
                __state.Context.WorkResults[__state.Key] = __result;
                Interlocked.Increment(ref workStores);
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

        private static bool TryClassifyWorkQuery(MethodInfo method, out QueryKind kind)
        {
            kind = QueryKind.None;
            if (method == null || method.IsAbstract || method.ContainsGenericParameters || method.ReturnType != typeof(bool))
                return false;

            ParameterInfo[] p = method.GetParameters();
            if (p.Length == 0 || p[0].ParameterType != typeof(Pawn))
                return false;

            if (method.Name == "ShouldSkip")
            {
                if (p.Length == 1 || (p.Length == 2 && p[1].ParameterType == typeof(bool)))
                {
                    kind = QueryKind.ShouldSkip;
                    return true;
                }
                return false;
            }

            if (method.Name == "HasJobOnThing")
            {
                if ((p.Length == 2 || p.Length == 3) &&
                    typeof(Thing).IsAssignableFrom(p[1].ParameterType) &&
                    (p.Length == 2 || p[2].ParameterType == typeof(bool)))
                {
                    kind = QueryKind.HasThing;
                    return true;
                }
                return false;
            }

            if (method.Name == "HasJobOnCell")
            {
                if ((p.Length == 2 || p.Length == 3) &&
                    p[1].ParameterType == typeof(IntVec3) &&
                    (p.Length == 2 || p[2].ParameterType == typeof(bool)))
                {
                    kind = QueryKind.HasCell;
                    return true;
                }
            }

            return false;
        }

        private static bool TryBuildWorkKey(
            WorkGiver giver,
            MethodBase method,
            object[] args,
            out QueryKind kind,
            out WorkQueryKey key)
        {
            kind = QueryKind.None;
            key = default(WorkQueryKey);

            Pawn pawn = args.Length > 0 ? args[0] as Pawn : null;
            if (pawn == null || (current != null && current.Pawn != null && !ReferenceEquals(current.Pawn, pawn)))
                return false;

            bool forced = false;
            if (method.Name == "ShouldSkip")
            {
                kind = QueryKind.ShouldSkip;
                if (args.Length > 1 && args[1] is bool)
                    forced = (bool)args[1];
                key = new WorkQueryKey(method, giver, pawn, null, IntVec3.Invalid, forced, kind);
                return true;
            }

            if (method.Name == "HasJobOnThing")
            {
                Thing thing = args.Length > 1 ? args[1] as Thing : null;
                if (thing == null)
                    return false;
                kind = QueryKind.HasThing;
                if (args.Length > 2 && args[2] is bool)
                    forced = (bool)args[2];
                key = new WorkQueryKey(method, giver, pawn, thing, IntVec3.Invalid, forced, kind);
                return true;
            }

            if (method.Name == "HasJobOnCell")
            {
                if (args.Length <= 1 || !(args[1] is IntVec3))
                    return false;
                kind = QueryKind.HasCell;
                if (args.Length > 2 && args[2] is bool)
                    forced = (bool)args[2];
                key = new WorkQueryKey(method, giver, pawn, null, (IntVec3)args[1], forced, kind);
                return true;
            }

            return false;
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
            long observed = Interlocked.Read(ref workObserved);
            long hits = Interlocked.Read(ref workHits);
            long orderObs = Interlocked.Read(ref orderObserved);
            long orderHit = Interlocked.Read(ref orderHits);
            double workHitPct = observed == 0 ? 0.0 : hits * 100.0 / observed;
            double orderHitPct = orderObs == 0 ? 0.0 : orderHit * 100.0 / orderObs;

            int disabled = 0;
            foreach (KeyValuePair<MethodBase, MethodParityState> pair in MethodParity)
            {
                if (pair.Value.Disabled)
                    disabled++;
            }

            return "JobPackage-local search V0.4.19-JS1: patched(scope/work/global/reachable)=" +
                scopePatched + "/" + patchedWorkQueries + "/" + globalHookPatched + "/" + reachableHookPatched +
                ", patchFailures=" + patchFailures +
                ", packages=" + Interlocked.Read(ref packages) +
                ", nested=" + Interlocked.Read(ref nestedPackages) +
                ", workObserved=" + observed +
                ", workHits=" + hits + " (" + workHitPct.ToString("F1") + "%)" +
                ", workMisses=" + Interlocked.Read(ref workMisses) +
                ", workStores=" + Interlocked.Read(ref workStores) +
                ", verify=" + Interlocked.Read(ref workVerifyRuns) + "/" + Interlocked.Read(ref workVerifyMatches) +
                ", mismatches=" + Interlocked.Read(ref workMismatches) +
                ", disabledMethods=" + disabled +
                ", disabledBypass=" + Interlocked.Read(ref disabledMethodBypass) +
                ", capBypass=" + Interlocked.Read(ref workCapBypass) +
                ", hitsByKind(skip/thing/cell)=" + Interlocked.Read(ref shouldSkipHits) + "/" +
                    Interlocked.Read(ref hasThingHits) + "/" + Interlocked.Read(ref hasCellHits) +
                ", maxWorkEntries=" + Interlocked.Read(ref maxWorkEntries) +
                ", orderObserved=" + orderObs +
                ", orderHits=" + orderHit + " (" + orderHitPct.ToString("F1") + "%)" +
                ", orderMisses=" + Interlocked.Read(ref orderMisses) +
                ", orderStores=" + Interlocked.Read(ref orderStores) +
                ", orderMutationBypass=" + Interlocked.Read(ref orderMutationBypass) +
                ", orderCapBypass=" + Interlocked.Read(ref orderCapBypass) +
                ", maxOrderEntries=" + Interlocked.Read(ref maxOrderEntries) +
                ". Lifetime is one synchronous JobPackage; sampled mismatches fuse only the offending boolean method.";
        }

        internal struct PackageScope
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal struct WorkQueryState
        {
            internal PackageContext Context;
            internal WorkQueryKey Key;
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
            internal readonly Dictionary<WorkQueryKey, bool> WorkResults = new Dictionary<WorkQueryKey, bool>();
            internal readonly Dictionary<OrderKey, OrderedEntry> Ordered = new Dictionary<OrderKey, OrderedEntry>();

            internal PackageContext(Pawn pawn)
            {
                Pawn = pawn;
            }
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

        internal enum QueryKind : byte
        {
            None = 0,
            ShouldSkip = 1,
            HasThing = 2,
            HasCell = 3
        }

        internal struct WorkQueryKey : IEquatable<WorkQueryKey>
        {
            internal readonly MethodBase Method;
            internal readonly WorkGiver Giver;
            internal readonly Pawn Pawn;
            internal readonly Thing Thing;
            internal readonly IntVec3 Cell;
            internal readonly bool Forced;
            internal readonly QueryKind Kind;

            internal WorkQueryKey(MethodBase method, WorkGiver giver, Pawn pawn, Thing thing, IntVec3 cell, bool forced, QueryKind kind)
            {
                Method = method;
                Giver = giver;
                Pawn = pawn;
                Thing = thing;
                Cell = cell;
                Forced = forced;
                Kind = kind;
            }

            public bool Equals(WorkQueryKey other)
            {
                return ReferenceEquals(Method, other.Method) &&
                    ReferenceEquals(Giver, other.Giver) &&
                    ReferenceEquals(Pawn, other.Pawn) &&
                    ReferenceEquals(Thing, other.Thing) &&
                    Cell.Equals(other.Cell) &&
                    Forced == other.Forced &&
                    Kind == other.Kind;
            }

            public override bool Equals(object obj)
            {
                return obj is WorkQueryKey && Equals((WorkQueryKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Method == null ? 0 : RuntimeHelpers.GetHashCode(Method);
                    hash = hash * 397 ^ (Giver == null ? 0 : RuntimeHelpers.GetHashCode(Giver));
                    hash = hash * 397 ^ (Pawn == null ? 0 : RuntimeHelpers.GetHashCode(Pawn));
                    hash = hash * 397 ^ (Thing == null ? 0 : RuntimeHelpers.GetHashCode(Thing));
                    hash = hash * 397 ^ Cell.GetHashCode();
                    hash = hash * 397 ^ (Forced ? 1 : 0);
                    hash = hash * 397 ^ (int)Kind;
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
    }
}
