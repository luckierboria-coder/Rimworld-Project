using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T31 measurement-only census for shared eligibility primitives used by many WorkGivers.
    ///
    /// The goal is to identify platform-level repeated work after T30 showed cross-scanner
    /// candidate explosion. T31 does not cache or alter any gameplay result. It installs only
    /// when the optional RimMT.Diagnostics assembly is present, samples one in eight outer
    /// JobGiver_Work packages, and records inclusive timing / exact-repeat / cross-scanner
    /// reuse for generic eligibility primitives.
    ///
    /// Observed primitives:
    ///  - ForbidUtility.IsForbidden(... Pawn ...)
    ///  - ReservationUtility.CanReserve(...)
    ///  - ReservationUtility.CanReserveAndReach(...)
    ///
    /// Candidate facts are NOT Harmony-patched property getters. Instead, for Thing targets
    /// already observed by one of the primitives, a tiny package-local fingerprint is sampled
    /// to measure whether Spawned/MapHeld/PositionHeld/stackCount/HitPoints change during the
    /// package. This avoids turning trivial getters into global profiler detours.
    ///
    /// Timings are inclusive. CanReserveAndReach may include CanReserve / Reachability work and
    /// must not be arithmetically summed with child primitive timings.
    /// </summary>
    internal static class SharedEligibilityPrimitiveCensus093T31
    {
        internal const string FeatureId = "diagnostics.sharedEligibilityPrimitiveCensus";

        private const long SampleMask = 7L; // 1 / 8 outer packages.
        private const int MaxExactKeysPerPackage = 16384;
        private const int MaxTargetsPerPackage = 8192;
        private const int MaxCandidateFactsPerPackage = 8192;

        [ThreadStatic] private static SampleContext current;
        [ThreadStatic] private static WorkGiver_Scanner currentScanner;

        private static readonly Dictionary<MethodBase, PrimitiveKind> PrimitiveMethods =
            new Dictionary<MethodBase, PrimitiveKind>();

        private static bool diagnosticsPresent;
        private static bool installed;
        private static int primitivePatched;
        private static int forbidPatched;
        private static int reservePatched;
        private static int reserveReachPatched;
        private static int workGiverScopePatched;
        private static int installFailures;

        private static long packagesSeen;
        private static long sampledPackages;
        private static long sampledSlow20;
        private static long sampledSlow50;
        private static long parseBypass;
        private static long pawnMismatchBypass;
        private static long capacityBypass;
        private static long primitiveExceptions;

        private static readonly AggregateBucket All = new AggregateBucket();
        private static readonly AggregateBucket Slow20 = new AggregateBucket();

        internal static bool Sampling
        {
            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            get { return current != null; }
        }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            diagnosticsPresent = DiagnosticsPresent();
            if (!diagnosticsPresent)
            {
                Log.Message("[RimMT] T31 Shared Eligibility Primitive Census inactive: optional RimMT.Diagnostics is not loaded; no T31 measurement Harmony hooks installed.");
                return;
            }

            try
            {
                PatchPrimitiveTypes(harmony,
                    new string[] { "RimWorld.ForbidUtility", "Verse.ForbidUtility" },
                    "IsForbidden", PrimitiveKind.Forbidden);

                PatchPrimitiveTypes(harmony,
                    new string[] { "Verse.AI.ReservationUtility", "RimWorld.ReservationUtility", "Verse.ReservationUtility" },
                    "CanReserve", PrimitiveKind.CanReserve);

                PatchPrimitiveTypes(harmony,
                    new string[] { "Verse.AI.ReservationUtility", "RimWorld.ReservationUtility", "Verse.ReservationUtility" },
                    "CanReserveAndReach", PrimitiveKind.CanReserveAndReach);

                PatchWorkGiverScopes(harmony);
                installed = primitivePatched > 0;

                Log.Message("[RimMT] T31 Shared Eligibility Primitive Census installed=" + installed +
                    ", primitives=" + primitivePatched +
                    " [forbidden=" + forbidPatched +
                    ", canReserve=" + reservePatched +
                    ", canReserveAndReach=" + reserveReachPatched +
                    "], workGiverScopeMethods=" + workGiverScopePatched +
                    ". Measurement-only, diagnostics-gated, 1/8 package sampling; no gameplay result cache.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T31 Shared Eligibility Primitive Census failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void BeginPackage(Pawn pawn, long generation)
        {
            current = null;
            currentScanner = null;
            if (!installed) return;

            packagesSeen++;
            if ((generation & SampleMask) != 0L) return;

            current = new SampleContext(pawn, generation, Stopwatch.GetTimestamp());
            sampledPackages++;
        }

        internal static void EndPackage()
        {
            SampleContext context = current;
            current = null;
            currentScanner = null;
            if (context == null) return;

            long elapsedTicks = Stopwatch.GetTimestamp() - context.StartedTimestamp;
            if (elapsedTicks < 0L) elapsedTicks = 0L;
            double elapsedMs = elapsedTicks * 1000.0 / Stopwatch.Frequency;

            PackageSnapshot snapshot = context.Snapshot(elapsedMs);
            All.Add(snapshot);
            capacityBypass += snapshot.CapacityBypass;

            if (elapsedMs >= 20.0)
            {
                sampledSlow20++;
                Slow20.Add(snapshot);
            }
            if (elapsedMs >= 50.0)
                sampledSlow50++;
        }

        private static void PatchPrimitiveTypes(Harmony harmony, string[] typeNames, string methodName, PrimitiveKind kind)
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();

            for (int n = 0; n < typeNames.Length; n++)
            {
                Type type = AccessTools.TypeByName(typeNames[n]);
                if (type == null) continue;

                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                }
                catch
                {
                    installFailures++;
                    continue;
                }

                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != methodName || method.ReturnType != typeof(bool) ||
                        method.ContainsGenericParameters || !unique.Add(method))
                        continue;

                    try
                    {
                        PrimitiveMethods[method] = kind;
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(SharedEligibilityPrimitiveCensus093T31), nameof(PrimitivePrefix))
                            { priority = Priority.First + 10 },
                            postfix: new HarmonyMethod(typeof(SharedEligibilityPrimitiveCensus093T31), nameof(PrimitivePostfix))
                            { priority = Priority.Last - 10 });
                        primitivePatched++;
                        if (kind == PrimitiveKind.Forbidden) forbidPatched++;
                        else if (kind == PrimitiveKind.CanReserve) reservePatched++;
                        else if (kind == PrimitiveKind.CanReserveAndReach) reserveReachPatched++;
                    }
                    catch
                    {
                        PrimitiveMethods.Remove(method);
                        installFailures++;
                    }
                }
            }
        }

        private static void PatchWorkGiverScopes(Harmony harmony)
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            for (int ai = 0; ai < assemblies.Length; ai++)
            {
                Type[] types;
                try { types = assemblies[ai].GetTypes(); }
                catch (ReflectionTypeLoadException ex) { types = ex.Types; }
                catch { continue; }
                if (types == null) continue;

                for (int ti = 0; ti < types.Length; ti++)
                {
                    Type type = types[ti];
                    if (type == null || !typeof(WorkGiver).IsAssignableFrom(type)) continue;

                    MethodInfo[] methods;
                    try
                    {
                        methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Public |
                            BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                    }
                    catch { continue; }

                    for (int mi = 0; mi < methods.Length; mi++)
                    {
                        MethodInfo method = methods[mi];
                        if (method == null || method.IsAbstract || method.ContainsGenericParameters ||
                            !IsScannerScopeMethod(method.Name) || !unique.Add(method))
                            continue;

                        try
                        {
                            harmony.Patch(method,
                                prefix: new HarmonyMethod(typeof(SharedEligibilityPrimitiveCensus093T31), nameof(ScannerScopePrefix))
                                { priority = Priority.First + 15 },
                                finalizer: new HarmonyMethod(typeof(SharedEligibilityPrimitiveCensus093T31), nameof(ScannerScopeFinalizer))
                                { priority = Priority.Last - 15 });
                            workGiverScopePatched++;
                        }
                        catch
                        {
                            installFailures++;
                        }
                    }
                }
            }
        }

        private static bool IsScannerScopeMethod(string name)
        {
            return name == "HasJobOnThing" || name == "JobOnThing" ||
                name == "ShouldSkip" || name == "NonScanJob";
        }

        public static void ScannerScopePrefix(object __instance, ref ScannerScopeState __state)
        {
            __state = default(ScannerScopeState);
            if (current == null) return;

            WorkGiver_Scanner scanner = __instance as WorkGiver_Scanner;
            if (scanner == null) return;

            __state.Entered = true;
            __state.Previous = currentScanner;
            currentScanner = scanner;
        }

        public static Exception ScannerScopeFinalizer(Exception __exception, ScannerScopeState __state)
        {
            if (__state.Entered)
                currentScanner = __state.Previous;
            return __exception;
        }

        public static void PrimitivePrefix(MethodBase __originalMethod, object[] __args, ref PrimitiveCallState __state)
        {
            __state = default(PrimitiveCallState);

            SampleContext context = current;
            if (context == null || __originalMethod == null) return;

            PrimitiveKind kind;
            if (!PrimitiveMethods.TryGetValue(__originalMethod, out kind)) return;

            ParsedCall parsed;
            if (!TryParseCall(__args, out parsed))
            {
                parseBypass++;
                return;
            }

            if (parsed.Pawn == null || context.Pawn == null || !ReferenceEquals(parsed.Pawn, context.Pawn))
            {
                pawnMismatchBypass++;
                return;
            }

            WorkGiver_Scanner scanner = currentScanner;
            int shapeHash = ComputeArgsHash(__args);
            ExactKey key = new ExactKey(kind, __originalMethod, parsed.Pawn, parsed.Target, shapeHash);

            CallAdmission admission = context.BeginCall(kind, key, parsed.Target, scanner);
            if (!admission.Admitted) return;

            __state.Context = context;
            __state.Kind = kind;
            __state.Key = key;
            __state.StartedTimestamp = Stopwatch.GetTimestamp();
            __state.ExactRepeat = admission.ExactRepeat;
            __state.CrossScannerRepeat = admission.CrossScannerRepeat;
            __state.ScannerScoped = scanner != null;
            __state.Admitted = true;
        }

        public static void PrimitivePostfix(bool __result, PrimitiveCallState __state)
        {
            if (!__state.Admitted) return;

            SampleContext context = __state.Context;
            if (context == null || !ReferenceEquals(current, context)) return;

            long elapsed = Stopwatch.GetTimestamp() - __state.StartedTimestamp;
            if (elapsed < 0L) elapsed = 0L;

            try
            {
                context.EndCall(__state.Kind, __state.Key, __result, elapsed,
                    __state.ExactRepeat, __state.CrossScannerRepeat, __state.ScannerScoped);
            }
            catch
            {
                primitiveExceptions++;
            }
        }

        private static bool TryParseCall(object[] args, out ParsedCall parsed)
        {
            parsed = default(ParsedCall);
            if (args == null || args.Length == 0) return false;

            Pawn pawn = null;
            for (int i = 0; i < args.Length; i++)
            {
                Pawn p = args[i] as Pawn;
                if (p != null)
                {
                    pawn = p;
                    break;
                }
            }
            if (pawn == null) return false;

            TargetIdentity target = default(TargetIdentity);
            bool foundTarget = false;

            for (int i = 0; i < args.Length; i++)
            {
                object arg = args[i];
                if (arg == null || ReferenceEquals(arg, pawn)) continue;

                if (arg is LocalTargetInfo)
                {
                    LocalTargetInfo local = (LocalTargetInfo)arg;
                    if (!local.IsValid) continue;
                    target = TargetIdentity.From(local);
                    foundTarget = true;
                    break;
                }

                Thing thing = arg as Thing;
                if (thing != null)
                {
                    target = TargetIdentity.From(thing);
                    foundTarget = true;
                    break;
                }
            }

            if (!foundTarget)
            {
                for (int i = 0; i < args.Length; i++)
                {
                    object arg = args[i];
                    if (!(arg is IntVec3)) continue;
                    IntVec3 cell = (IntVec3)arg;
                    if (!cell.IsValid) continue;
                    target = TargetIdentity.From(cell);
                    foundTarget = true;
                    break;
                }
            }

            if (!foundTarget) return false;
            parsed = new ParsedCall(pawn, target);
            return true;
        }

        private static int ComputeArgsHash(object[] args)
        {
            unchecked
            {
                int hash = 17;
                if (args == null) return hash;

                for (int i = 0; i < args.Length; i++)
                {
                    object arg = args[i];
                    int part = 0;
                    if (arg != null)
                    {
                        Type type = arg.GetType();
                        if (type.IsValueType || arg is string)
                            part = arg.GetHashCode();
                        else
                            part = RuntimeHelpers.GetHashCode(arg);
                    }
                    hash = hash * 397 ^ part;
                }
                return hash;
            }
        }

        private static bool DiagnosticsPresent()
        {
            try
            {
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly == null) continue;
                    string name = assembly.GetName().Name;
                    if (string.Equals(name, "RimMT.Diagnostics", StringComparison.Ordinal))
                        return true;
                }
            }
            catch { }
            return false;
        }

        internal static string Summary()
        {
            return "T31 shared eligibility primitive census: installed=" + installed +
                ", diagnosticsPresent=" + diagnosticsPresent +
                ", patched[all/forbidden/canReserve/canReserveAndReach/workGiverScope]=" +
                primitivePatched + "/" + forbidPatched + "/" + reservePatched + "/" +
                reserveReachPatched + "/" + workGiverScopePatched +
                ", sample=1/8, packagesSeen=" + packagesSeen +
                ", sampledPackages=" + sampledPackages +
                ", sampledSlow>=20/50ms=" + sampledSlow20 + "/" + sampledSlow50 +
                ", bypass[parse/pawnMismatch/capacity]=" + parseBypass + "/" +
                pawnMismatchBypass + "/" + capacityBypass +
                ", observerExceptions=" + primitiveExceptions +
                ". ALL{" + All.Format() + "} SLOW20{" + Slow20.Format() + "}. " +
                "Measurement-only; timings are inclusive; CanReserveAndReach may include child primitive work. " +
                "No WorkGiver/mod special case, no result cache, no reservation mutation, no Job/state commit.";
        }

        internal struct ScannerScopeState
        {
            internal bool Entered;
            internal WorkGiver_Scanner Previous;
        }

        public struct PrimitiveCallState
        {
            internal SampleContext Context;
            internal PrimitiveKind Kind;
            internal ExactKey Key;
            internal long StartedTimestamp;
            internal bool ExactRepeat;
            internal bool CrossScannerRepeat;
            internal bool ScannerScoped;
            internal bool Admitted;
        }

        private enum PrimitiveKind
        {
            Forbidden = 0,
            CanReserve = 1,
            CanReserveAndReach = 2,
            Count = 3
        }

        private struct ParsedCall
        {
            internal readonly Pawn Pawn;
            internal readonly TargetIdentity Target;

            internal ParsedCall(Pawn pawn, TargetIdentity target)
            {
                Pawn = pawn;
                Target = target;
            }
        }

        private struct CallAdmission
        {
            internal readonly bool Admitted;
            internal readonly bool ExactRepeat;
            internal readonly bool CrossScannerRepeat;

            internal CallAdmission(bool admitted, bool exactRepeat, bool crossScannerRepeat)
            {
                Admitted = admitted;
                ExactRepeat = exactRepeat;
                CrossScannerRepeat = crossScannerRepeat;
            }
        }

        private sealed class SampleContext
        {
            internal readonly Pawn Pawn;
            internal readonly long Generation;
            internal readonly long StartedTimestamp;

            private readonly Dictionary<ExactKey, ExactEntry> exact =
                new Dictionary<ExactKey, ExactEntry>();
            private readonly Dictionary<TargetKey, TargetShapeEntry> targets =
                new Dictionary<TargetKey, TargetShapeEntry>();
            private readonly Dictionary<Thing, CandidateFact> candidateFacts =
                new Dictionary<Thing, CandidateFact>(ReferenceComparer<Thing>.Instance);
            private readonly PrimitiveStats[] stats =
                new PrimitiveStats[(int)PrimitiveKind.Count];

            private long factChecks;
            private long factRevisits;
            private long factMutations;
            private long localCapacityBypass;

            internal SampleContext(Pawn pawn, long generation, long startedTimestamp)
            {
                Pawn = pawn;
                Generation = generation;
                StartedTimestamp = startedTimestamp;
                for (int i = 0; i < stats.Length; i++)
                    stats[i] = new PrimitiveStats();
            }

            internal CallAdmission BeginCall(PrimitiveKind kind, ExactKey key, TargetIdentity target,
                WorkGiver_Scanner scanner)
            {
                PrimitiveStats s = stats[(int)kind];
                s.Calls++;
                if (scanner == null) s.UnscopedCalls++;
                else s.ScannerScopedCalls++;

                ExactEntry entry;
                bool repeat = exact.TryGetValue(key, out entry);
                bool cross = false;

                if (!repeat)
                {
                    if (exact.Count >= MaxExactKeysPerPackage)
                    {
                        localCapacityBypass++;
                        s.CapacityBypass++;
                        RecordCandidateFact(target);
                        RecordTargetShape(kind, target, key.ShapeHash);
                        return new CallAdmission(true, false, false);
                    }

                    entry = new ExactEntry();
                    if (scanner != null)
                    {
                        entry.HasScanner = true;
                        entry.FirstScanner = scanner;
                    }
                    exact.Add(key, entry);
                    s.UniqueKeys++;
                }
                else
                {
                    s.ExactRepeats++;

                    if (scanner != null)
                    {
                        if (!entry.HasScanner)
                        {
                            entry.HasScanner = true;
                            entry.FirstScanner = scanner;
                        }
                        else if (!ReferenceEquals(scanner, entry.FirstScanner))
                        {
                            if (!entry.MultiScanner)
                            {
                                entry.MultiScanner = true;
                                s.CrossScannerKeys++;
                            }
                            cross = true;
                        }
                        else if (entry.MultiScanner)
                        {
                            cross = true;
                        }
                    }

                    if (cross) s.CrossScannerRepeatCalls++;
                    exact[key] = entry;
                }

                RecordCandidateFact(target);
                RecordTargetShape(kind, target, key.ShapeHash);
                return new CallAdmission(true, repeat, cross);
            }

            internal void EndCall(PrimitiveKind kind, ExactKey key, bool result, long elapsedTicks,
                bool exactRepeat, bool crossScannerRepeat, bool scannerScoped)
            {
                PrimitiveStats s = stats[(int)kind];
                s.TotalTicks += elapsedTicks;
                if (scannerScoped) s.ScannerScopedTicks += elapsedTicks;
                else s.UnscopedTicks += elapsedTicks;

                if (exactRepeat)
                {
                    s.RepeatTicks += elapsedTicks;
                    if (crossScannerRepeat)
                        s.CrossScannerRepeatTicks += elapsedTicks;
                }

                if (result) s.TrueResults++;
                else s.FalseResults++;

                ExactEntry entry;
                if (!exact.TryGetValue(key, out entry))
                    return;

                if (!entry.HasResult)
                {
                    entry.HasResult = true;
                    entry.FirstResult = result;
                }
                else if (entry.FirstResult != result)
                {
                    s.ResultFlips++;
                    if (crossScannerRepeat) s.CrossScannerResultFlips++;
                    entry.FirstResult = result;
                }
                exact[key] = entry;
            }

            private void RecordTargetShape(PrimitiveKind kind, TargetIdentity target, int shapeHash)
            {
                TargetKey key = new TargetKey(kind, target);
                TargetShapeEntry entry;
                if (!targets.TryGetValue(key, out entry))
                {
                    if (targets.Count >= MaxTargetsPerPackage)
                    {
                        localCapacityBypass++;
                        stats[(int)kind].CapacityBypass++;
                        return;
                    }

                    entry = new TargetShapeEntry(shapeHash);
                    targets.Add(key, entry);
                    stats[(int)kind].UniqueTargets++;
                    return;
                }

                entry.Calls++;
                if (shapeHash != entry.FirstShapeHash)
                {
                    if (entry.ExtraShapes == null)
                        entry.ExtraShapes = new HashSet<int>();
                    if (entry.ExtraShapes.Add(shapeHash))
                    {
                        if (!entry.MultiShape)
                        {
                            entry.MultiShape = true;
                            stats[(int)kind].MultiShapeTargets++;
                        }
                        stats[(int)kind].ExtraShapeVariants++;
                    }
                }
                targets[key] = entry;
            }

            private void RecordCandidateFact(TargetIdentity target)
            {
                Thing thing = target.HasThing ? target.Thing : null;
                if (thing == null) return;

                factChecks++;
                CandidateFact old;
                if (!candidateFacts.TryGetValue(thing, out old))
                {
                    if (candidateFacts.Count >= MaxCandidateFactsPerPackage)
                    {
                        localCapacityBypass++;
                        return;
                    }

                    candidateFacts.Add(thing, CandidateFact.Capture(thing));
                    return;
                }

                factRevisits++;
                CandidateFact now = CandidateFact.Capture(thing);
                if (!old.Equals(now))
                {
                    factMutations++;
                    candidateFacts[thing] = now;
                }
            }

            internal PackageSnapshot Snapshot(double elapsedMs)
            {
                PrimitiveSnapshot[] primitive = new PrimitiveSnapshot[(int)PrimitiveKind.Count];
                for (int i = 0; i < primitive.Length; i++)
                    primitive[i] = stats[i].Snapshot();

                return new PackageSnapshot(
                    elapsedMs,
                    primitive,
                    factChecks,
                    factRevisits,
                    factMutations,
                    candidateFacts.Count,
                    localCapacityBypass);
            }
        }

        private sealed class PrimitiveStats
        {
            internal long Calls;
            internal long UniqueKeys;
            internal long ExactRepeats;
            internal long CrossScannerKeys;
            internal long CrossScannerRepeatCalls;
            internal long ScannerScopedCalls;
            internal long UnscopedCalls;
            internal long TotalTicks;
            internal long RepeatTicks;
            internal long CrossScannerRepeatTicks;
            internal long ScannerScopedTicks;
            internal long UnscopedTicks;
            internal long TrueResults;
            internal long FalseResults;
            internal long ResultFlips;
            internal long CrossScannerResultFlips;
            internal long UniqueTargets;
            internal long MultiShapeTargets;
            internal long ExtraShapeVariants;
            internal long CapacityBypass;

            internal PrimitiveSnapshot Snapshot()
            {
                return new PrimitiveSnapshot(
                    Calls, UniqueKeys, ExactRepeats, CrossScannerKeys, CrossScannerRepeatCalls,
                    ScannerScopedCalls, UnscopedCalls,
                    TotalTicks, RepeatTicks, CrossScannerRepeatTicks, ScannerScopedTicks, UnscopedTicks,
                    TrueResults, FalseResults, ResultFlips, CrossScannerResultFlips,
                    UniqueTargets, MultiShapeTargets, ExtraShapeVariants, CapacityBypass);
            }
        }

        private struct PrimitiveSnapshot
        {
            internal readonly long Calls;
            internal readonly long UniqueKeys;
            internal readonly long ExactRepeats;
            internal readonly long CrossScannerKeys;
            internal readonly long CrossScannerRepeatCalls;
            internal readonly long ScannerScopedCalls;
            internal readonly long UnscopedCalls;
            internal readonly long TotalTicks;
            internal readonly long RepeatTicks;
            internal readonly long CrossScannerRepeatTicks;
            internal readonly long ScannerScopedTicks;
            internal readonly long UnscopedTicks;
            internal readonly long TrueResults;
            internal readonly long FalseResults;
            internal readonly long ResultFlips;
            internal readonly long CrossScannerResultFlips;
            internal readonly long UniqueTargets;
            internal readonly long MultiShapeTargets;
            internal readonly long ExtraShapeVariants;
            internal readonly long CapacityBypass;

            internal PrimitiveSnapshot(long calls, long uniqueKeys, long exactRepeats,
                long crossScannerKeys, long crossScannerRepeatCalls,
                long scannerScopedCalls, long unscopedCalls,
                long totalTicks, long repeatTicks, long crossScannerRepeatTicks,
                long scannerScopedTicks, long unscopedTicks,
                long trueResults, long falseResults, long resultFlips, long crossScannerResultFlips,
                long uniqueTargets, long multiShapeTargets, long extraShapeVariants, long capacityBypass)
            {
                Calls = calls;
                UniqueKeys = uniqueKeys;
                ExactRepeats = exactRepeats;
                CrossScannerKeys = crossScannerKeys;
                CrossScannerRepeatCalls = crossScannerRepeatCalls;
                ScannerScopedCalls = scannerScopedCalls;
                UnscopedCalls = unscopedCalls;
                TotalTicks = totalTicks;
                RepeatTicks = repeatTicks;
                CrossScannerRepeatTicks = crossScannerRepeatTicks;
                ScannerScopedTicks = scannerScopedTicks;
                UnscopedTicks = unscopedTicks;
                TrueResults = trueResults;
                FalseResults = falseResults;
                ResultFlips = resultFlips;
                CrossScannerResultFlips = crossScannerResultFlips;
                UniqueTargets = uniqueTargets;
                MultiShapeTargets = multiShapeTargets;
                ExtraShapeVariants = extraShapeVariants;
                CapacityBypass = capacityBypass;
            }
        }

        private struct PackageSnapshot
        {
            internal readonly double ElapsedMs;
            internal readonly PrimitiveSnapshot[] Primitive;
            internal readonly long FactChecks;
            internal readonly long FactRevisits;
            internal readonly long FactMutations;
            internal readonly long UniqueFactThings;
            internal readonly long CapacityBypass;

            internal PackageSnapshot(double elapsedMs, PrimitiveSnapshot[] primitive,
                long factChecks, long factRevisits, long factMutations,
                long uniqueFactThings, long capacityBypass)
            {
                ElapsedMs = elapsedMs;
                Primitive = primitive;
                FactChecks = factChecks;
                FactRevisits = factRevisits;
                FactMutations = factMutations;
                UniqueFactThings = uniqueFactThings;
                CapacityBypass = capacityBypass;
            }
        }

        private sealed class AggregateBucket
        {
            private long packages;
            private double elapsedMs;
            private readonly AggregatePrimitive[] primitive =
                new AggregatePrimitive[(int)PrimitiveKind.Count];

            private long factChecks;
            private long factRevisits;
            private long factMutations;
            private long uniqueFactThings;
            private long localCapacityBypass;

            internal AggregateBucket()
            {
                for (int i = 0; i < primitive.Length; i++)
                    primitive[i] = new AggregatePrimitive();
            }

            internal void Add(PackageSnapshot snapshot)
            {
                packages++;
                elapsedMs += snapshot.ElapsedMs;
                for (int i = 0; i < primitive.Length; i++)
                    primitive[i].Add(snapshot.Primitive[i]);

                factChecks += snapshot.FactChecks;
                factRevisits += snapshot.FactRevisits;
                factMutations += snapshot.FactMutations;
                uniqueFactThings += snapshot.UniqueFactThings;
                localCapacityBypass += snapshot.CapacityBypass;
            }

            internal string Format()
            {
                double avgPackageMs = packages == 0L ? 0.0 : elapsedMs / packages;
                return "packages=" + packages +
                    ", avgPackageMs=" + avgPackageMs.ToString("F2") +
                    ", Forbidden{" + primitive[(int)PrimitiveKind.Forbidden].Format() + "}" +
                    ", CanReserve{" + primitive[(int)PrimitiveKind.CanReserve].Format() + "}" +
                    ", CanReserveAndReach{" + primitive[(int)PrimitiveKind.CanReserveAndReach].Format() + "}" +
                    ", candidateFacts[checks=" + factChecks +
                    ", uniqueThings=" + uniqueFactThings +
                    ", revisits=" + factRevisits +
                    "(" + Percent(factRevisits, factChecks) + ")" +
                    ", mutations=" + factMutations +
                    "(" + Percent(factMutations, factRevisits) + ")]" +
                    ", capacityBypass=" + localCapacityBypass;
            }
        }

        private sealed class AggregatePrimitive
        {
            private long calls;
            private long uniqueKeys;
            private long exactRepeats;
            private long crossScannerKeys;
            private long crossScannerRepeatCalls;
            private long scannerScopedCalls;
            private long unscopedCalls;
            private long totalTicks;
            private long repeatTicks;
            private long crossScannerRepeatTicks;
            private long scannerScopedTicks;
            private long unscopedTicks;
            private long trueResults;
            private long falseResults;
            private long resultFlips;
            private long crossScannerResultFlips;
            private long uniqueTargets;
            private long multiShapeTargets;
            private long extraShapeVariants;
            private long capBypass;

            internal void Add(PrimitiveSnapshot s)
            {
                calls += s.Calls;
                uniqueKeys += s.UniqueKeys;
                exactRepeats += s.ExactRepeats;
                crossScannerKeys += s.CrossScannerKeys;
                crossScannerRepeatCalls += s.CrossScannerRepeatCalls;
                scannerScopedCalls += s.ScannerScopedCalls;
                unscopedCalls += s.UnscopedCalls;
                totalTicks += s.TotalTicks;
                repeatTicks += s.RepeatTicks;
                crossScannerRepeatTicks += s.CrossScannerRepeatTicks;
                scannerScopedTicks += s.ScannerScopedTicks;
                unscopedTicks += s.UnscopedTicks;
                trueResults += s.TrueResults;
                falseResults += s.FalseResults;
                resultFlips += s.ResultFlips;
                crossScannerResultFlips += s.CrossScannerResultFlips;
                uniqueTargets += s.UniqueTargets;
                multiShapeTargets += s.MultiShapeTargets;
                extraShapeVariants += s.ExtraShapeVariants;
                capBypass += s.CapacityBypass;
            }

            internal string Format()
            {
                double totalMs = TicksToMs(totalTicks);
                double repeatMs = TicksToMs(repeatTicks);
                double crossMs = TicksToMs(crossScannerRepeatTicks);
                return "calls=" + calls +
                    ", uniqueKeys=" + uniqueKeys +
                    ", exactRepeats=" + exactRepeats +
                    "(" + Percent(exactRepeats, calls) + ")" +
                    ", crossScannerKeys=" + crossScannerKeys +
                    ", crossScannerRepeatCalls=" + crossScannerRepeatCalls +
                    "(" + Percent(crossScannerRepeatCalls, calls) + ")" +
                    ", scoped/unscoped=" + scannerScopedCalls + "/" + unscopedCalls +
                    ", totalMs=" + totalMs.ToString("F2") +
                    ", repeatMs=" + repeatMs.ToString("F2") +
                    "(" + PercentTicks(repeatTicks, totalTicks) + ")" +
                    ", crossScannerRepeatMs=" + crossMs.ToString("F2") +
                    "(" + PercentTicks(crossScannerRepeatTicks, totalTicks) + ")" +
                    ", scoped/unscopedMs=" + TicksToMs(scannerScopedTicks).ToString("F2") + "/" +
                    TicksToMs(unscopedTicks).ToString("F2") +
                    ", result[T/F]=" + trueResults + "/" + falseResults +
                    ", resultFlips=" + resultFlips +
                    ", crossScannerResultFlips=" + crossScannerResultFlips +
                    ", targets=" + uniqueTargets +
                    ", multiShapeTargets=" + multiShapeTargets +
                    ", extraShapeVariants=" + extraShapeVariants +
                    ", capBypass=" + capBypass;
            }
        }

        private sealed class ExactEntry
        {
            internal bool HasScanner;
            internal WorkGiver_Scanner FirstScanner;
            internal bool MultiScanner;
            internal bool HasResult;
            internal bool FirstResult;
        }

        private struct TargetShapeEntry
        {
            internal readonly int FirstShapeHash;
            internal int Calls;
            internal bool MultiShape;
            internal HashSet<int> ExtraShapes;

            internal TargetShapeEntry(int firstShapeHash)
            {
                FirstShapeHash = firstShapeHash;
                Calls = 1;
                MultiShape = false;
                ExtraShapes = null;
            }
        }

        private struct ExactKey : IEquatable<ExactKey>
        {
            internal readonly PrimitiveKind Kind;
            internal readonly MethodBase Method;
            internal readonly Pawn Pawn;
            internal readonly TargetIdentity Target;
            internal readonly int ShapeHash;

            internal ExactKey(PrimitiveKind kind, MethodBase method, Pawn pawn,
                TargetIdentity target, int shapeHash)
            {
                Kind = kind;
                Method = method;
                Pawn = pawn;
                Target = target;
                ShapeHash = shapeHash;
            }

            public bool Equals(ExactKey other)
            {
                return Kind == other.Kind &&
                    ReferenceEquals(Method, other.Method) &&
                    ReferenceEquals(Pawn, other.Pawn) &&
                    Target.Equals(other.Target) &&
                    ShapeHash == other.ShapeHash;
            }

            public override bool Equals(object obj)
            {
                return obj is ExactKey && Equals((ExactKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = (int)Kind;
                    h = h * 397 ^ (Method == null ? 0 : RuntimeHelpers.GetHashCode(Method));
                    h = h * 397 ^ (Pawn == null ? 0 : RuntimeHelpers.GetHashCode(Pawn));
                    h = h * 397 ^ Target.GetHashCode();
                    h = h * 397 ^ ShapeHash;
                    return h;
                }
            }
        }

        private struct TargetKey : IEquatable<TargetKey>
        {
            private readonly PrimitiveKind kind;
            private readonly TargetIdentity target;

            internal TargetKey(PrimitiveKind kind, TargetIdentity target)
            {
                this.kind = kind;
                this.target = target;
            }

            public bool Equals(TargetKey other)
            {
                return kind == other.kind && target.Equals(other.target);
            }

            public override bool Equals(object obj)
            {
                return obj is TargetKey && Equals((TargetKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked { return ((int)kind * 397) ^ target.GetHashCode(); }
            }
        }

        private struct TargetIdentity : IEquatable<TargetIdentity>
        {
            internal readonly bool HasThing;
            internal readonly Thing Thing;
            internal readonly IntVec3 Cell;

            private TargetIdentity(bool hasThing, Thing thing, IntVec3 cell)
            {
                HasThing = hasThing;
                Thing = thing;
                Cell = cell;
            }

            internal static TargetIdentity From(LocalTargetInfo target)
            {
                if (target.HasThing)
                    return new TargetIdentity(true, target.Thing, target.Cell);
                return new TargetIdentity(false, null, target.Cell);
            }

            internal static TargetIdentity From(Thing thing)
            {
                return new TargetIdentity(true, thing, thing == null ? IntVec3.Invalid : thing.PositionHeld);
            }

            internal static TargetIdentity From(IntVec3 cell)
            {
                return new TargetIdentity(false, null, cell);
            }

            public bool Equals(TargetIdentity other)
            {
                return HasThing == other.HasThing &&
                    (HasThing ? ReferenceEquals(Thing, other.Thing) : Cell == other.Cell);
            }

            public override bool Equals(object obj)
            {
                return obj is TargetIdentity && Equals((TargetIdentity)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return HasThing
                        ? (Thing == null ? 0 : RuntimeHelpers.GetHashCode(Thing))
                        : Cell.GetHashCode();
                }
            }
        }

        private struct CandidateFact : IEquatable<CandidateFact>
        {
            private readonly Map map;
            private readonly IntVec3 position;
            private readonly bool spawned;
            private readonly int stackCount;
            private readonly int hitPoints;

            private CandidateFact(Map map, IntVec3 position, bool spawned, int stackCount, int hitPoints)
            {
                this.map = map;
                this.position = position;
                this.spawned = spawned;
                this.stackCount = stackCount;
                this.hitPoints = hitPoints;
            }

            internal static CandidateFact Capture(Thing thing)
            {
                if (thing == null)
                    return new CandidateFact(null, IntVec3.Invalid, false, 0, 0);

                return new CandidateFact(
                    thing.MapHeld,
                    thing.PositionHeld,
                    thing.Spawned,
                    thing.stackCount,
                    thing.HitPoints);
            }

            public bool Equals(CandidateFact other)
            {
                return ReferenceEquals(map, other.map) &&
                    position == other.position &&
                    spawned == other.spawned &&
                    stackCount == other.stackCount &&
                    hitPoints == other.hitPoints;
            }

            public override bool Equals(object obj)
            {
                return obj is CandidateFact && Equals((CandidateFact)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = map == null ? 0 : RuntimeHelpers.GetHashCode(map);
                    h = h * 397 ^ position.GetHashCode();
                    h = h * 397 ^ (spawned ? 1 : 0);
                    h = h * 397 ^ stackCount;
                    h = h * 397 ^ hitPoints;
                    return h;
                }
            }
        }

        private sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            internal static readonly ReferenceComparer<T> Instance = new ReferenceComparer<T>();
            public bool Equals(T x, T y) { return ReferenceEquals(x, y); }
            public int GetHashCode(T obj) { return obj == null ? 0 : RuntimeHelpers.GetHashCode(obj); }
        }

        private static double TicksToMs(long ticks)
        {
            return ticks <= 0L ? 0.0 : ticks * 1000.0 / Stopwatch.Frequency;
        }

        private static string Percent(long numerator, long denominator)
        {
            if (denominator <= 0L) return "0.00%";
            return (numerator * 100.0 / denominator).ToString("F2") + "%";
        }

        private static string PercentTicks(long numerator, long denominator)
        {
            if (denominator <= 0L) return "0.00%";
            return (numerator * 100.0 / denominator).ToString("F2") + "%";
        }
    }
}
