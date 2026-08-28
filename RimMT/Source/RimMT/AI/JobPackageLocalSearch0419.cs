using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.19-JS2
    //
    // JS1 proved that JobPackage-local reuse is the right lifetime, but telemetry also showed
    // that a broad WorkGiver bool dictionary is not: only 2.5% of bool probes hit while the
    // nearest-order cache hit 37.6%. JS2 therefore narrows bool memoization to HasJobOnThing
    // (8,608 / 9,712 JS1 bool hits) and turns nearest-order reuse into an explicit search plan.
    //
    // A search plan is keyed by the exact source IList identity + root cell for the lifetime of
    // ONE synchronous JobGiver_Work.TryIssueJobPackage call. The plan captures exact membership,
    // spawn state and positions, then builds one stable squared-distance order WITHOUT baking
    // maxDistance into the key. Later queries with different maxDistance values reuse the same
    // order and take a prefix. Exact membership/spawn/position is revalidated before every reuse.
    //
    // Safety contract:
    // - no state survives TryIssueJobPackage;
    // - no worker wait is introduced;
    // - no Job, reservation, JobOnThing/JobOnCell result or mutable Verse state is cached;
    // - HasJobOnThing cache hits are sampled against live Vanilla and fuse only that method;
    // - GenClosest validator/Reachability/final selection remain Vanilla-authoritative;
    // - priorityGetter != null and unsupported source shapes bypass JS2 completely.
    internal static class JobPackageLocalSearch0419
    {
        internal const string FeatureId = "ai.jobPackageLocal";

        private const int MaxThingEntriesPerPackage = 4096;
        private const int MaxPlansPerPackage = 64;
        private const int MaxPrefixesPerPlan = 16;
        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 16384;
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
        private static long disabledMethodBypass;
        private static long maxThingEntries;
        private static int mismatchLogs;

        private static long planObserved;
        private static long planObservedGlobal;
        private static long planObservedReachable;
        private static long planHits;
        private static long planBuilds;
        private static long planBuildRejected;
        private static long planStale;
        private static long planCapBypass;
        private static long sameDistanceHits;
        private static long crossDistanceHits;
        private static long prefixCacheHits;
        private static long prefixBuilds;
        private static long sourceCandidates;
        private static long sortedCandidates;
        private static long planBuildTicks;
        private static long planBuildTicksMax;
        private static long planValidateTicks;
        private static long planValidateTicksMax;
        private static long maxPlanSourceCount;
        private static long maxPlans;

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
                    Log.Warning("[RimMT] V0.4.19-JS2 unavailable: JobGiver_Work.TryIssueJobPackage target not found.");
                    return;
                }

                HarmonyMethod enter = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackagePrefix));
                enter.priority = Priority.First + 100;
                HarmonyMethod exit = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(JobPackageFinalizer));
                exit.priority = Priority.Last - 100;
                harmony.Patch(jobGiver, prefix: enter, finalizer: exit);
                scopePatched = true;

                PatchHasJobOnThingQueries(harmony);
                PatchNearestPlanHooks(harmony);

                Log.Message("[RimMT] V0.4.19-JS2 JobPackage search-plan reuse active: scope=" + scopePatched +
                    ", HasJobOnThing=" + patchedHasThingQueries +
                    ", nearestHooks(global/reachable)=" + globalHookPatched + "/" + reachableHookPatched +
                    ". ShouldSkip and HasJobOnCell memoization are removed. One exact source+root distance plan may serve multiple maxDistance queries inside one TryIssueJobPackage call; exact membership/spawn/position is revalidated before reuse. Vanilla retains predicates, Reachability, final selection, Jobs and reservations.");
            }
            catch (Exception ex)
            {
                patchFailures++;
                FeatureGate.Suppress(FeatureId, "installation failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] V0.4.19-JS2 install failed; V0.4.18.2 behavior remains. " + ex.GetType().Name + ": " + ex.Message);
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
                            Log.Warning("[RimMT] V0.4.19-JS2 skipped HasJobOnThing query " + method + ": " + ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }
        }

        private static void PatchNearestPlanHooks(Harmony harmony)
        {
            MethodBase global = AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalPrefix));
            MethodBase reachable = AccessTools.Method(typeof(JobGiverGlobalNearest04181), nameof(JobGiverGlobalNearest04181.GlobalReachablePrefix));

            if (global != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(PlanGlobalPrefix));
                    prefix.priority = Priority.First + 150;
                    harmony.Patch(global, prefix: prefix);
                    globalHookPatched = true;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    Log.Warning("[RimMT] V0.4.19-JS2 GlobalPrefix search-plan hook failed: " + ex.GetType().Name + ": " + ex.Message);
                }
            }

            if (reachable != null)
            {
                try
                {
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobPackageLocalSearch0419), nameof(PlanReachablePrefix));
                    prefix.priority = Priority.First + 150;
                    harmony.Patch(reachable, prefix: prefix);
                    reachableHookPatched = true;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    Log.Warning("[RimMT] V0.4.19-JS2 GlobalReachablePrefix search-plan hook failed: " + ex.GetType().Name + ": " + ex.Message);
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
                    UpdateMax(ref maxThingEntries, context.ThingResults.Count);
                    UpdateMax(ref maxPlans, context.Plans.Count);
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

            WorkThingKey key;
            if (!TryBuildThingKey(__instance, __originalMethod, __args, out key))
                return true;

            Interlocked.Increment(ref thingObserved);

            MethodParityState parity = GetParityState(__originalMethod);
            if (parity.Disabled)
            {
                Interlocked.Increment(ref disabledMethodBypass);
                return true;
            }

            bool cached;
            if (!context.ThingResults.TryGetValue(key, out cached))
            {
                Interlocked.Increment(ref thingMisses);
                __state.Context = context;
                __state.Key = key;
                __state.Store = context.ThingResults.Count < MaxThingEntriesPerPackage;
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
                        Log.Warning("[RimMT] V0.4.19-JS2 HasJobOnThing parity mismatch; caching disabled for " +
                            __state.Key.Method + ". cached=" + __state.Cached + ", live=" + __result +
                            ". Vanilla is authoritative for this sample and all future calls to that method.");
                    }
                }
                return;
            }

            if (__state.Store && __state.Context != null && ReferenceEquals(current, __state.Context))
            {
                __state.Context.ThingResults[__state.Key] = __result;
                Interlocked.Increment(ref thingStores);
            }
        }

        public static bool PlanGlobalPrefix(object[] __0)
        {
            return PlanPrefixCore(__0, false, 0, 1, 2, 4);
        }

        public static bool PlanReachablePrefix(object[] __0)
        {
            return PlanPrefixCore(__0, true, 0, 2, 5, 7);
        }

        private static bool PlanPrefixCore(
            object[] args,
            bool reachable,
            int centerIndex,
            int setIndex,
            int maxDistanceIndex,
            int priorityIndex)
        {
            PackageContext context = current;
            if (context == null || packageDepth <= 0 || args == null || args.Length <= priorityIndex || args[priorityIndex] != null)
                return true;

            IList source = args[setIndex] as IList;
            if (source == null)
                return true;

            int count;
            try { count = source.Count; }
            catch { return true; }

            if (count < MinSourceCount || count > MaxSourceCount)
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

            if (float.IsNaN(maxDistance))
                return true;

            Interlocked.Increment(ref planObserved);
            if (reachable) Interlocked.Increment(ref planObservedReachable);
            else Interlocked.Increment(ref planObservedGlobal);

            PlanKey key = new PlanKey(source, center.x, center.z);
            SearchPlan plan;
            bool existing = context.Plans.TryGetValue(key, out plan);

            if (existing)
            {
                long validateStart = Stopwatch.GetTimestamp();
                bool valid = ValidatePlan(source, plan);
                RecordElapsed(ref planValidateTicks, ref planValidateTicksMax, validateStart);
                if (!valid)
                {
                    context.Plans.Remove(key);
                    plan = null;
                    existing = false;
                    Interlocked.Increment(ref planStale);
                }
            }

            if (!existing)
            {
                if (context.Plans.Count >= MaxPlansPerPackage)
                {
                    Interlocked.Increment(ref planCapBypass);
                    return true;
                }

                long buildStart = Stopwatch.GetTimestamp();
                plan = BuildPlan(source, center, count);
                RecordElapsed(ref planBuildTicks, ref planBuildTicksMax, buildStart);
                if (plan == null)
                {
                    Interlocked.Increment(ref planBuildRejected);
                    return true;
                }

                context.Plans[key] = plan;
                Interlocked.Increment(ref planBuilds);
            }
            else
            {
                Interlocked.Increment(ref planHits);
            }

            bool prefixWasCached;
            Thing[] prefix = plan.GetPrefix(maxDistance, out prefixWasCached);
            if (prefix == null)
                return true;

            if (prefixWasCached)
                Interlocked.Increment(ref prefixCacheHits);
            else
                Interlocked.Increment(ref prefixBuilds);

            if (existing)
            {
                if (prefixWasCached) Interlocked.Increment(ref sameDistanceHits);
                else Interlocked.Increment(ref crossDistanceHits);
            }

            args[setIndex] = prefix;

            // Returning false skips only RimMT V0.4.18.1's synchronous reorder helper.
            // The original GenClosest call still runs immediately afterwards with the exact
            // in-range nearest-first prefix, and Vanilla retains validator/final authority.
            return false;
        }

        private static SearchPlan BuildPlan(IList source, IntVec3 center, int count)
        {
            Thing[] members = new Thing[count];
            bool[] spawned = new bool[count];
            int[] xs = new int[count];
            int[] zs = new int[count];
            Candidate[] candidates = new Candidate[count];
            int kept = 0;

            try
            {
                for (int i = 0; i < count; i++)
                {
                    object raw = source[i];
                    if (raw == null)
                        return null; // preserve Vanilla's unsupported/null behavior

                    Thing thing = raw as Thing;
                    if (thing == null)
                        return null;

                    members[i] = thing;
                    bool isSpawned = thing.Spawned;
                    spawned[i] = isSpawned;
                    if (!isSpawned)
                        continue;

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        return null;

                    xs[i] = pos.x;
                    zs[i] = pos.z;
                    long dx = (long)pos.x - center.x;
                    long dz = (long)pos.z - center.z;
                    long d2 = dx * dx + dz * dz;
                    candidates[kept++] = new Candidate(thing, d2, i);
                }
            }
            catch
            {
                return null;
            }

            if (kept > 1)
                Array.Sort(candidates, 0, kept, CandidateComparer.Instance);

            Thing[] ordered = new Thing[kept];
            long[] distances = new long[kept];
            for (int i = 0; i < kept; i++)
            {
                ordered[i] = candidates[i].Thing;
                distances[i] = candidates[i].DistanceSquared;
            }

            Interlocked.Add(ref sourceCandidates, count);
            Interlocked.Add(ref sortedCandidates, kept);
            UpdateMax(ref maxPlanSourceCount, count);

            return new SearchPlan(members, spawned, xs, zs, ordered, distances);
        }

        private static bool ValidatePlan(IList source, SearchPlan plan)
        {
            if (source == null || plan == null || source.Count != plan.Count)
                return false;

            try
            {
                for (int i = 0; i < plan.Count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !ReferenceEquals(thing, plan.Members[i]))
                        return false;

                    bool spawned = thing.Spawned;
                    if (spawned != plan.Spawned[i])
                        return false;

                    if (spawned)
                    {
                        IntVec3 pos = thing.Position;
                        if (!pos.IsValid || pos.x != plan.Xs[i] || pos.z != plan.Zs[i])
                            return false;
                    }
                }
            }
            catch
            {
                return false;
            }

            return true;
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

        private static bool TryBuildThingKey(
            WorkGiver giver,
            MethodBase method,
            object[] args,
            out WorkThingKey key)
        {
            key = default(WorkThingKey);
            Pawn pawn = args.Length > 0 ? args[0] as Pawn : null;
            Thing thing = args.Length > 1 ? args[1] as Thing : null;
            if (pawn == null || thing == null || (current != null && current.Pawn != null && !ReferenceEquals(current.Pawn, pawn)))
                return false;

            bool forced = args.Length > 2 && args[2] is bool && (bool)args[2];
            key = new WorkThingKey(method, giver, pawn, thing, forced);
            return true;
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

        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
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
            long pObserved = Interlocked.Read(ref planObserved);
            long pHits = Interlocked.Read(ref planHits);
            long pBuilds = Interlocked.Read(ref planBuilds);
            double thingHitPct = observed == 0 ? 0.0 : hits * 100.0 / observed;
            double planHitPct = pObserved == 0 ? 0.0 : pHits * 100.0 / pObserved;
            double avgBuildUs = pBuilds == 0 ? 0.0 :
                (Interlocked.Read(ref planBuildTicks) * 1000000.0 / Stopwatch.Frequency) / pBuilds;
            long validations = pHits + Interlocked.Read(ref planStale);
            double avgValidateUs = validations == 0 ? 0.0 :
                (Interlocked.Read(ref planValidateTicks) * 1000000.0 / Stopwatch.Frequency) / validations;

            int disabled = 0;
            foreach (KeyValuePair<MethodBase, MethodParityState> pair in MethodParity)
            {
                if (pair.Value.Disabled)
                    disabled++;
            }

            return "JobPackage search plan V0.4.19-JS2: patched(scope/hasThing/global/reachable)=" +
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
                ", thingCapBypass=" + Interlocked.Read(ref thingCapBypass) +
                ", maxThingEntries=" + Interlocked.Read(ref maxThingEntries) +
                ", planObserved=" + pObserved + " (global/reachable=" + Interlocked.Read(ref planObservedGlobal) + "/" + Interlocked.Read(ref planObservedReachable) + ")" +
                ", planHits=" + pHits + " (" + planHitPct.ToString("F1") + "%)" +
                ", planBuilds=" + pBuilds +
                ", planBuildRejected=" + Interlocked.Read(ref planBuildRejected) +
                ", planStale=" + Interlocked.Read(ref planStale) +
                ", planCapBypass=" + Interlocked.Read(ref planCapBypass) +
                ", sameDistanceHits=" + Interlocked.Read(ref sameDistanceHits) +
                ", crossDistanceHits=" + Interlocked.Read(ref crossDistanceHits) +
                ", prefixCacheHits=" + Interlocked.Read(ref prefixCacheHits) +
                ", prefixBuilds=" + Interlocked.Read(ref prefixBuilds) +
                ", sourceCandidates=" + Interlocked.Read(ref sourceCandidates) +
                ", sortedCandidates=" + Interlocked.Read(ref sortedCandidates) +
                ", maxPlanSource=" + Interlocked.Read(ref maxPlanSourceCount) +
                ", maxPlans=" + Interlocked.Read(ref maxPlans) +
                ", avgBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxBuildUs=" + (Interlocked.Read(ref planBuildTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ", avgValidateUs=" + avgValidateUs.ToString("F2") +
                ", maxValidateUs=" + (Interlocked.Read(ref planValidateTicksMax) * 1000000.0 / Stopwatch.Frequency).ToString("F2") +
                ". No ShouldSkip/HasJobOnCell memoization; one exact source+root distance plan serves multiple maxDistance prefixes only inside the current JobPackage.";
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
            internal WorkThingKey Key;
            internal MethodParityState Parity;
            internal bool Store;
            internal bool Verify;
            internal bool Cached;
            internal bool AuthoritativeHit;
        }

        internal sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Dictionary<WorkThingKey, bool> ThingResults = new Dictionary<WorkThingKey, bool>();
            internal readonly Dictionary<PlanKey, SearchPlan> Plans = new Dictionary<PlanKey, SearchPlan>();

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

        internal sealed class SearchPlan
        {
            internal readonly Thing[] Members;
            internal readonly bool[] Spawned;
            internal readonly int[] Xs;
            internal readonly int[] Zs;
            internal readonly Thing[] Sorted;
            internal readonly long[] Distances;
            internal readonly int Count;
            private readonly Dictionary<float, Thing[]> prefixes = new Dictionary<float, Thing[]>();

            internal SearchPlan(Thing[] members, bool[] spawned, int[] xs, int[] zs, Thing[] sorted, long[] distances)
            {
                Members = members;
                Spawned = spawned;
                Xs = xs;
                Zs = zs;
                Sorted = sorted;
                Distances = distances;
                Count = members.Length;
            }

            internal Thing[] GetPrefix(float maxDistance, out bool cached)
            {
                Thing[] result;
                if (prefixes.TryGetValue(maxDistance, out result))
                {
                    cached = true;
                    return result;
                }

                cached = false;
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                int lo = 0;
                int hi = Distances.Length;
                while (lo < hi)
                {
                    int mid = lo + ((hi - lo) >> 1);
                    if (Distances[mid] <= maxDistanceSquared)
                        lo = mid + 1;
                    else
                        hi = mid;
                }

                int kept = lo;
                if (kept == Sorted.Length)
                {
                    result = Sorted;
                }
                else
                {
                    result = new Thing[kept];
                    if (kept > 0)
                        Array.Copy(Sorted, 0, result, 0, kept);
                }

                if (prefixes.Count < MaxPrefixesPerPlan)
                    prefixes[maxDistance] = result;
                return result;
            }
        }

        internal struct WorkThingKey : IEquatable<WorkThingKey>
        {
            internal readonly MethodBase Method;
            internal readonly WorkGiver Giver;
            internal readonly Pawn Pawn;
            internal readonly Thing Thing;
            internal readonly bool Forced;

            internal WorkThingKey(MethodBase method, WorkGiver giver, Pawn pawn, Thing thing, bool forced)
            {
                Method = method;
                Giver = giver;
                Pawn = pawn;
                Thing = thing;
                Forced = forced;
            }

            public bool Equals(WorkThingKey other)
            {
                return ReferenceEquals(Method, other.Method) &&
                    ReferenceEquals(Giver, other.Giver) &&
                    ReferenceEquals(Pawn, other.Pawn) &&
                    ReferenceEquals(Thing, other.Thing) &&
                    Forced == other.Forced;
            }

            public override bool Equals(object obj)
            {
                return obj is WorkThingKey && Equals((WorkThingKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Method == null ? 0 : RuntimeHelpers.GetHashCode(Method);
                    hash = hash * 397 ^ (Giver == null ? 0 : RuntimeHelpers.GetHashCode(Giver));
                    hash = hash * 397 ^ (Pawn == null ? 0 : RuntimeHelpers.GetHashCode(Pawn));
                    hash = hash * 397 ^ (Thing == null ? 0 : RuntimeHelpers.GetHashCode(Thing));
                    hash = hash * 397 ^ (Forced ? 1 : 0);
                    return hash;
                }
            }
        }

        internal struct PlanKey : IEquatable<PlanKey>
        {
            internal readonly object Source;
            internal readonly int X;
            internal readonly int Z;

            internal PlanKey(object source, int x, int z)
            {
                Source = source;
                X = x;
                Z = z;
            }

            public bool Equals(PlanKey other)
            {
                return ReferenceEquals(Source, other.Source) && X == other.X && Z == other.Z;
            }

            public override bool Equals(object obj)
            {
                return obj is PlanKey && Equals((PlanKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Source == null ? 0 : RuntimeHelpers.GetHashCode(Source);
                    hash = hash * 397 ^ X;
                    hash = hash * 397 ^ Z;
                    return hash;
                }
            }
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
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
