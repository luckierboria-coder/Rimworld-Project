using System;
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
    /// <summary>
    /// T28 bottom-layer owner for one synchronous JobGiver_Work.TryIssueJobPackage transaction.
    ///
    /// This class owns the single Harmony package boundary. Older transaction modules keep their
    /// proven internal semantics, but their package prefix/finalizer methods are invoked from this
    /// coordinator instead of each installing another Harmony wrapper on the same hot method.
    ///
    /// The shared context is main-thread only and dies at the outer package boundary. It exposes
    /// false-only BillStack readiness proof and bounded module-local state slots. It never stores
    /// Jobs, reservations, priorities, validator positives, live Reachability results across a
    /// package, or any cross-pawn/cross-tick gameplay decision.
    /// </summary>
    internal static class JobSearchPackageContext093T28
    {
        internal const string FeatureId = "ai.jobSearchPackageContext";

        private const int MaxModuleStates = 24;
        private const int MaxGenericNegativeDomains = 16;
        private const int MaxGenericNegativesPerDomain = 4096;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static PackageContext current;

        private static bool installed;
        private static int installFailures;
        private static long nextGeneration;
        private static long packages;
        private static long nestedPackages;
        private static long maxDepth;
        private static long mismatchedExits;
        private static long moduleStateCreates;
        private static long moduleStateCapacityBypass;
        private static long billNegativeHits;
        private static long billNegativeStores;
        private static long genericNegativeHits;
        private static long genericNegativeStores;
        private static long genericNegativeCapacityBypass;

        internal static bool Installed { get { return installed; } }
        internal static bool InScope { get { return depth > 0 && current != null; } }
        internal static Pawn CurrentPawn { get { return InScope ? current.Pawn : null; } }
        internal static Map CurrentMap { get { return InScope ? current.Map : null; } }
        internal static long CurrentGeneration { get { return InScope ? current.Generation : 0L; } }
        internal static long CurrentScopeStartTicks { get { return InScope ? current.StartedTimestamp : 0L; } }
        internal static long PackageCount { get { return Interlocked.Read(ref packages); } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(
                    typeof(JobGiver_Work),
                    "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package == null)
                {
                    Log.Warning("[RimMT] T28 unified Job Search package boundary unavailable; legacy modules stay fail-closed.");
                    return;
                }

                harmony.Patch(package,
                    prefix: new HarmonyMethod(typeof(JobSearchPackageContext093T28), nameof(PackagePrefix))
                    { priority = Priority.First + 400 },
                    finalizer: new HarmonyMethod(typeof(JobSearchPackageContext093T28), nameof(PackageFinalizer))
                    { priority = Priority.Last - 400 });

                installed = true;
                Log.Message("[RimMT] T28 unified Job Search transaction boundary installed. One Harmony package wrapper now coordinates T20/T21, T22, GlobalNearest and shared false-only package state.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T28 unified Job Search boundary failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PackagePrefix(Pawn __0, ref ScopeState __state)
        {
            __state = default(ScopeState);
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = depth == 0;
            depth++;
            UpdateMax(ref maxDepth, depth);

            if (__state.Outermost)
            {
                long generation = Interlocked.Increment(ref nextGeneration);
                current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
                __state.Shared = current;
                Interlocked.Increment(ref packages);
            }
            else
            {
                __state.Shared = current;
                Interlocked.Increment(ref nestedPackages);
            }

            // Preserve the already-validated module semantics, but run their package lifecycle
            // through this single bottom-layer Harmony wrapper in the same logical nesting.
            JobSearchTransaction093T20.PackagePrefix(__0, ref __state.T20);
            GenClosestTransactionIndex093T22.PackagePrefix(ref __state.T22);
            JobGiverGlobalNearest04181.JobGiverPrefix(__0);
            __state.GlobalNearestEntered = true;
        }

        public static Exception PackageFinalizer(Exception __exception, ScopeState __state)
        {
            if (!__state.Entered) return __exception;

            // Reverse the logical enter order. Each legacy finalizer only tears down its own
            // package-local state; no gameplay result is changed here.
            if (__state.GlobalNearestEntered)
                __exception = JobGiverGlobalNearest04181.JobGiverFinalizer(__exception);
            __exception = GenClosestTransactionIndex093T22.PackageFinalizer(__exception, __state.T22);
            __exception = JobSearchTransaction093T20.PackageFinalizer(__exception, __state.T20);

            if (depth > 0) depth--;
            if (__state.Outermost)
            {
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                current = null;
                depth = 0;
            }
            return __exception;
        }

        internal static T GetOrCreateModuleState<T>(object key, Func<T> factory) where T : class
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || key == null || factory == null) return null;

            object value;
            if (context.ModuleStates.TryGetValue(key, out value))
                return value as T;

            if (context.ModuleStates.Count >= MaxModuleStates)
            {
                Interlocked.Increment(ref moduleStateCapacityBypass);
                return null;
            }

            T created = factory();
            if (created == null) return null;
            context.ModuleStates[key] = created;
            Interlocked.Increment(ref moduleStateCreates);
            return created;
        }

        internal static T GetModuleState<T>(object key) where T : class
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || key == null) return null;
            object value;
            return context.ModuleStates.TryGetValue(key, out value) ? value as T : null;
        }

        internal static bool IsBillStackKnownInactive(BillStack stack)
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || stack == null) return false;
            if (!context.InactiveBillStacks.Contains(stack)) return false;
            Interlocked.Increment(ref billNegativeHits);
            return true;
        }

        internal static void MarkBillStackInactive(BillStack stack)
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || stack == null) return;
            if (context.InactiveBillStacks.Add(stack))
                Interlocked.Increment(ref billNegativeStores);
        }

        // Generic reference-key false-only memo for future bottom-layer predicates. It is
        // intentionally package-local and bounded. Callers must use a stable domain token and
        // a reference-identity key whose false result is invariant for this synchronous package.
        internal static bool TryGetNegative(object domain, object key)
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || domain == null || key == null) return false;

            HashSet<object> set;
            if (!context.GenericNegatives.TryGetValue(domain, out set) || set == null)
                return false;
            if (!set.Contains(key)) return false;
            Interlocked.Increment(ref genericNegativeHits);
            return true;
        }

        internal static void StoreNegative(object domain, object key)
        {
            PackageContext context = current;
            if (context == null || depth <= 0 || domain == null || key == null) return;

            HashSet<object> set;
            if (!context.GenericNegatives.TryGetValue(domain, out set))
            {
                if (context.GenericNegatives.Count >= MaxGenericNegativeDomains)
                {
                    Interlocked.Increment(ref genericNegativeCapacityBypass);
                    return;
                }
                set = new HashSet<object>(ReferenceEqualityComparer.Instance);
                context.GenericNegatives.Add(domain, set);
            }

            if (set.Count >= MaxGenericNegativesPerDomain)
            {
                Interlocked.Increment(ref genericNegativeCapacityBypass);
                return;
            }
            if (set.Add(key))
                Interlocked.Increment(ref genericNegativeStores);
        }

        internal static string Summary()
        {
            return "T28 unified Job Search package context: installed=" + installed +
                ", packages=" + Interlocked.Read(ref packages) +
                ", nested=" + Interlocked.Read(ref nestedPackages) +
                ", maxDepth=" + Interlocked.Read(ref maxDepth) +
                ", mismatchedExits=" + Interlocked.Read(ref mismatchedExits) +
                ", moduleStateCreates=" + Interlocked.Read(ref moduleStateCreates) +
                ", moduleStateCapBypass=" + Interlocked.Read(ref moduleStateCapacityBypass) +
                ", billNegative[hits/stores]=" + Interlocked.Read(ref billNegativeHits) + "/" +
                Interlocked.Read(ref billNegativeStores) +
                ", genericNegative[hits/stores/capBypass]=" + Interlocked.Read(ref genericNegativeHits) + "/" +
                Interlocked.Read(ref genericNegativeStores) + "/" +
                Interlocked.Read(ref genericNegativeCapacityBypass) +
                ", currentGeneration=" + CurrentGeneration +
                ". One synchronous TryIssueJobPackage boundary only; no Job/reservation/priority/cross-package result is cached.";
        }

        private static void UpdateMax(ref long field, long value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        internal struct ScopeState
        {
            internal bool Entered;
            internal bool Outermost;
            internal bool GlobalNearestEntered;
            internal PackageContext Shared;
            internal JobSearchTransaction093T20.PackageState T20;
            internal GenClosestTransactionIndex093T22.PackageState T22;
        }

        internal sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Map Map;
            internal readonly IntVec3 StartPosition;
            internal readonly long Generation;
            internal readonly long StartedTimestamp;
            internal readonly Dictionary<object, object> ModuleStates =
                new Dictionary<object, object>(ReferenceEqualityComparer.Instance);
            internal readonly HashSet<BillStack> InactiveBillStacks = new HashSet<BillStack>();
            internal readonly Dictionary<object, HashSet<object>> GenericNegatives =
                new Dictionary<object, HashSet<object>>(ReferenceEqualityComparer.Instance);

            internal PackageContext(Pawn pawn, long generation, long startedTimestamp)
            {
                Pawn = pawn;
                Map = pawn == null ? null : pawn.Map;
                StartPosition = pawn == null ? IntVec3.Invalid : pawn.Position;
                Generation = generation;
                StartedTimestamp = startedTimestamp;
            }
        }

        private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
        {
            internal static readonly ReferenceEqualityComparer Instance = new ReferenceEqualityComparer();
            public new bool Equals(object x, object y) { return ReferenceEquals(x, y); }
            public int GetHashCode(object obj) { return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj); }
        }
    }
}
