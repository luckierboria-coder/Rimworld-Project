using System;
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
    /// <summary>
    /// T20 Foundation: one synchronous JobGiver_Work.TryIssueJobPackage is treated as a
    /// transaction. The transaction owns all memoized query state and is destroyed at the
    /// outer package boundary. Nothing survives to another pawn/package/tick.
    ///
    /// Production paths:
    ///  - generic JobGiver_Work local Validator(Thing) negative memo. Only false is reusable;
    ///    positives are always live. Each scanner/method pair must pass repeated live parity
    ///    checks before authoritative skips are allowed, and one mismatch quarantines it.
    ///  - exact Reachability.CanReach package-local memo only when no foreign postfix can mutate
    ///    __result. Foreign prefixes still execute before this low-priority prefix; cached hits
    ///    therefore preserve their live admission/argument changes. Result-mutating postfixes
    ///    force shadow-only mode.
    ///
    /// No Job, JobOnThing, reservation, priority, path, validator-positive or cross-package
    /// result is cached. Capacity overflow and every uncertain shape fail open to Vanilla.
    /// </summary>
    internal static class JobSearchTransaction093T20
    {
        internal const string FeatureId = "ai.jobSearchTransaction";

        private const int ValidatorCapacity = 8192;
        private const int ReachCapacity = 8192;
        private const int ValidatorWarmupMatches = 12;
        private const int ValidatorVerifyMask = 63; // 1/64 after trust.
        private const int ReachWarmupMatches = 8;
        private const int ReachVerifyMask = 63;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static TransactionContext current;

        private static readonly object TrustLock = new object();
        private static readonly Dictionary<ValidatorTrustKey, ValidatorTrustState> ValidatorTrust =
            new Dictionary<ValidatorTrustKey, ValidatorTrustState>();

        private static bool installed;
        private static bool packagePatched;
        private static bool reachPatched;
        private static bool reachAuthoritativeSafe;
        private static int validatorMethodsPatched;
        private static int validatorMethodsSkippedForeign;
        private static int installFailures;

        private static long packages;
        private static long nestedPackages;
        private static long validatorObserved;
        private static long validatorNegativeStores;
        private static long validatorMemoCandidates;
        private static long validatorAuthoritativeHits;
        private static long validatorVerifyRuns;
        private static long validatorVerifyMatches;
        private static long validatorMismatches;
        private static long validatorQuarantines;
        private static long validatorFingerprintBypass;
        private static long validatorCapacityBypass;
        private static long validatorScannerResolveBypass;
        private static long validatorPositiveLive;

        private static long reachObserved;
        private static long reachStores;
        private static long reachMemoCandidates;
        private static long reachAuthoritativeHits;
        private static long reachVerifyRuns;
        private static long reachVerifyMatches;
        private static long reachMismatches;
        private static long reachMutationBypass;
        private static long reachCapacityBypass;
        private static long reachForeignResultBypass;
        private static long reachInvalidations;
        private static long reachExceptions;
        private static volatile bool reachRuntimeQuarantined;

        private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors =
            new Dictionary<Type, ScannerAccessor>();
        private static readonly object ScannerAccessorLock = new object();

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            try
            {
                MethodBase package = AccessTools.Method(
                    typeof(JobGiver_Work),
                    "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package != null)
                {
                    harmony.Patch(package,
                        prefix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(PackagePrefix))
                        { priority = Priority.First + 300 },
                        finalizer: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(PackageFinalizer))
                        { priority = Priority.Last - 300 });
                    packagePatched = true;
                }

                PatchJobGiverValidators(harmony);
                PatchReachability(harmony);
                installed = packagePatched && validatorMethodsPatched > 0;

                Log.Message("[RimMT] T20 Foundation transaction core installed=" + installed +
                    ", package=" + packagePatched +
                    ", validatorMethods=" + validatorMethodsPatched +
                    ", reach=" + reachPatched +
                    ", reachAuthoritativeSafe=" + reachAuthoritativeSafe +
                    ". Lifetime=one synchronous JobGiver_Work package; negative-validator only; Vanilla live parity/quarantine remains authority.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T20 Foundation transaction install failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchJobGiverValidators(Harmony harmony)
        {
            List<Type> nested = new List<Type>();
            CollectNestedTypes(typeof(JobGiver_Work), nested);
            HashSet<MethodBase> unique = new HashSet<MethodBase>();

            for (int i = 0; i < nested.Count; i++)
            {
                Type type = nested[i];
                MethodInfo[] methods;
                try
                {
                    methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static |
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                }
                catch { continue; }

                for (int m = 0; m < methods.Length; m++)
                {
                    MethodInfo method = methods[m];
                    if (method == null || method.ReturnType != typeof(bool) ||
                        method.Name.IndexOf("Validator", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    ParameterInfo[] p = method.GetParameters();
                    if (p.Length != 1 || !typeof(Thing).IsAssignableFrom(p[0].ParameterType))
                        continue;
                    if (!unique.Add(method)) continue;

                    if (HasForeignPatches(method))
                    {
                        validatorMethodsSkippedForeign++;
                        continue;
                    }

                    try
                    {
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ValidatorPrefix))
                            { priority = Priority.First + 250 },
                            postfix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ValidatorPostfix))
                            { priority = Priority.Last - 250 });
                        validatorMethodsPatched++;
                    }
                    catch
                    {
                        installFailures++;
                    }
                }
            }
        }

        private static void CollectNestedTypes(Type parent, List<Type> output)
        {
            Type[] nested;
            try { nested = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic); }
            catch { return; }
            for (int i = 0; i < nested.Length; i++)
            {
                Type type = nested[i];
                if (type == null) continue;
                output.Add(type);
                CollectNestedTypes(type, output);
            }
        }

        private static void PatchReachability(Harmony harmony)
        {
            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(Reachability),
                    nameof(Reachability.CanReach),
                    new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });
                if (target == null) return;

                reachAuthoritativeSafe = !HasResultMutatingForeignPostfix(target);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ReachPrefix))
                    { priority = Priority.Last - 300 },
                    finalizer: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ReachFinalizer))
                    { priority = Priority.Last - 300 });
                reachPatched = true;

                MethodBase clear = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCache));
                MethodBase clearPawn = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCacheFor), new Type[] { typeof(Pawn) });
                MethodBase clearHostile = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCacheForHostile), new Type[] { typeof(Thing) });
                HarmonyMethod invalidate = new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ReachCacheInvalidated))
                    { priority = Priority.Last };
                if (clear != null) harmony.Patch(clear, postfix: invalidate);
                if (clearPawn != null) harmony.Patch(clearPawn, postfix: invalidate);
                if (clearHostile != null) harmony.Patch(clearHostile, postfix: invalidate);
            }
            catch
            {
                reachPatched = false;
                installFailures++;
            }
        }

        public static void PackagePrefix(Pawn __0, ref PackageState __state)
        {
            __state = default(PackageState);
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = depth == 0;
            depth++;

            if (__state.Outermost)
            {
                TransactionContext context = new TransactionContext(__0);
                current = context;
                __state.Context = context;
                Interlocked.Increment(ref packages);
            }
            else
            {
                __state.Context = current;
                Interlocked.Increment(ref nestedPackages);
            }
        }

        public static Exception PackageFinalizer(Exception __exception, PackageState __state)
        {
            if (!__state.Entered) return __exception;
            if (depth > 0) depth--;
            if (__state.Outermost)
            {
                if (ReferenceEquals(current, __state.Context)) current = null;
            }
            return __exception;
        }

        public static bool ValidatorPrefix(
            object __instance,
            MethodBase __originalMethod,
            object[] __args,
            ref bool __result,
            ref ValidatorCallState __state)
        {
            __state = default(ValidatorCallState);
            Interlocked.Increment(ref validatorObserved);

            TransactionContext context = current;
            if (context == null || depth <= 0 || __originalMethod == null || __args == null || __args.Length != 1)
                return true;

            Thing thing = __args[0] as Thing;
            if (thing == null || context.Pawn == null)
                return true;

            WorkGiver_Scanner scanner = ResolveScanner(__instance);
            if (scanner == null)
            {
                Interlocked.Increment(ref validatorScannerResolveBypass);
                return true;
            }

            ValidatorKey key = new ValidatorKey(__originalMethod, scanner, thing);
            ValidatorNegativeEntry entry;
            if (!context.ValidatorNegatives.TryGetValue(key, out entry))
            {
                __state = ValidatorCallState.Store(context, key, scanner, thing);
                return true;
            }

            if (!entry.Fingerprint.Matches(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.Store(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);
            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
            if (trust.Quarantined)
                return true;

            long serial = ++trust.HitSerial;
            bool verify = trust.ValidatedMatches < ValidatorWarmupMatches || (serial & ValidatorVerifyMask) == 0;
            if (verify)
            {
                Interlocked.Increment(ref validatorVerifyRuns);
                __state = ValidatorCallState.Verify(context, key, trust);
                return true;
            }

            __result = false;
            Interlocked.Increment(ref validatorAuthoritativeHits);
            __state.AuthoritativeHit = true;
            return false;
        }

        public static void ValidatorPostfix(bool __result, ValidatorCallState __state)
        {
            if (__state.AuthoritativeHit) return;
            TransactionContext context = __state.Context;
            if (context == null || !ReferenceEquals(current, context)) return;

            if (__state.Verify && __state.Trust != null)
            {
                if (!__result)
                {
                    __state.Trust.ValidatedMatches++;
                    Interlocked.Increment(ref validatorVerifyMatches);
                }
                else
                {
                    __state.Trust.Quarantined = true;
                    __state.Trust.ValidatedMatches = 0;
                    context.ValidatorNegatives.Clear();
                    Interlocked.Increment(ref validatorMismatches);
                    Interlocked.Increment(ref validatorQuarantines);
                }
                return;
            }

            if (__result)
            {
                Interlocked.Increment(ref validatorPositiveLive);
                return;
            }

            if (!__state.Store || __state.Thing == null) return;
            if (context.ValidatorNegatives.Count >= ValidatorCapacity)
            {
                Interlocked.Increment(ref validatorCapacityBypass);
                return;
            }

            if (!context.ValidatorNegatives.ContainsKey(__state.Key))
            {
                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(ThingFingerprint.Capture(context.Pawn, __state.Thing)));
                Interlocked.Increment(ref validatorNegativeStores);
            }
        }

        public static bool ReachPrefix(
            Reachability __instance,
            IntVec3 start,
            LocalTargetInfo dest,
            PathEndMode peMode,
            TraverseParms traverseParams,
            bool __runOriginal,
            ref bool __result,
            ref ReachCallState __state)
        {
            __state = default(ReachCallState);
            Interlocked.Increment(ref reachObserved);

            TransactionContext context = current;
            if (!__runOriginal || context == null || depth <= 0 || reachRuntimeQuarantined ||
                __instance == null || context.Pawn == null || traverseParams.pawn == null ||
                !ReferenceEquals(traverseParams.pawn, context.Pawn))
                return true;

            if (!start.IsValid || !dest.IsValid || context.Pawn.Map == null || start != context.Pawn.Position)
                return true;

            ReachKey key = new ReachKey(__instance, start, dest, peMode, traverseParams);
            ReachEntry entry;
            if (!context.ReachMemo.TryGetValue(key, out entry))
            {
                __state = ReachCallState.Store(context, key, dest);
                return true;
            }

            if (!entry.Fingerprint.Matches(dest))
            {
                context.ReachMemo.Remove(key);
                Interlocked.Increment(ref reachMutationBypass);
                __state = ReachCallState.Store(context, key, dest);
                return true;
            }

            Interlocked.Increment(ref reachMemoCandidates);
            if (!reachAuthoritativeSafe)
            {
                Interlocked.Increment(ref reachForeignResultBypass);
                __state = ReachCallState.VerifyOnly(context, key, entry.Result, dest);
                Interlocked.Increment(ref reachVerifyRuns);
                return true;
            }

            context.ReachHitSerial++;
            bool verify = context.ReachValidatedMatches < ReachWarmupMatches ||
                (context.ReachHitSerial & ReachVerifyMask) == 0;
            if (verify)
            {
                Interlocked.Increment(ref reachVerifyRuns);
                __state = ReachCallState.VerifyOnly(context, key, entry.Result, dest);
                return true;
            }

            __result = entry.Result;
            __state = ReachCallState.Authoritative(context, key, entry.Result, dest);
            Interlocked.Increment(ref reachAuthoritativeHits);
            return false;
        }

        public static Exception ReachFinalizer(
            Exception __exception,
            bool __result,
            ReachCallState __state)
        {
            if (__exception != null)
            {
                Interlocked.Increment(ref reachExceptions);
                return __exception;
            }

            TransactionContext context = __state.Context;
            if (context == null || !ReferenceEquals(current, context)) return __exception;

            if (__state.Verify)
            {
                if (__result == __state.Cached)
                {
                    context.ReachValidatedMatches++;
                    Interlocked.Increment(ref reachVerifyMatches);
                }
                else
                {
                    context.ReachMemo.Clear();
                    context.ReachValidatedMatches = 0;
                    reachRuntimeQuarantined = true;
                    Interlocked.Increment(ref reachMismatches);
                }
                return __exception;
            }

            if (__state.AuthoritativeHit) return __exception;
            if (!__state.Store) return __exception;

            if (context.ReachMemo.Count >= ReachCapacity)
            {
                Interlocked.Increment(ref reachCapacityBypass);
                return __exception;
            }

            if (!context.ReachMemo.ContainsKey(__state.Key))
            {
                context.ReachMemo.Add(__state.Key,
                    new ReachEntry(__result, TargetFingerprint.Capture(__state.Dest)));
                Interlocked.Increment(ref reachStores);
            }
            return __exception;
        }

        public static void ReachCacheInvalidated()
        {
            TransactionContext context = current;
            if (context == null || depth <= 0) return;
            context.ReachMemo.Clear();
            context.ReachValidatedMatches = 0;
            Interlocked.Increment(ref reachInvalidations);
        }

        private static WorkGiver_Scanner ResolveScanner(object closure)
        {
            if (closure == null) return null;
            Type type = closure.GetType();
            ScannerAccessor accessor;
            lock (ScannerAccessorLock)
            {
                if (!ScannerAccessors.TryGetValue(type, out accessor))
                {
                    accessor = ScannerAccessor.Build(type);
                    ScannerAccessors[type] = accessor;
                }
            }
            return accessor == null ? null : accessor.Read(closure);
        }

        private static ValidatorTrustState GetTrust(MethodBase method, WorkGiver_Scanner scanner)
        {
            ValidatorTrustKey key = new ValidatorTrustKey(method, scanner);
            lock (TrustLock)
            {
                ValidatorTrustState state;
                if (!ValidatorTrust.TryGetValue(key, out state))
                {
                    state = new ValidatorTrustState();
                    ValidatorTrust.Add(key, state);
                }
                return state;
            }
        }

        private static bool HasForeignPatches(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return false;
                return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) ||
                    HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
            }
            catch { return true; }
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        private static bool HasResultMutatingForeignPostfix(MethodBase method)
        {
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return false;
                foreach (Patch patch in info.Postfixes)
                {
                    if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        continue;
                    MethodInfo pm = patch.PatchMethod;
                    if (pm == null) return true;
                    ParameterInfo[] pars = pm.GetParameters();
                    for (int i = 0; i < pars.Length; i++)
                    {
                        ParameterInfo p = pars[i];
                        if (p.Name == "__result" && p.ParameterType.IsByRef &&
                            p.ParameterType.GetElementType() == typeof(bool))
                            return true;
                    }
                }
                return false;
            }
            catch { return true; }
        }

        internal static string Summary()
        {
            return "T20 foundation transaction: installed=" + installed +
                ", packagePatched=" + packagePatched +
                ", validatorMethods=" + validatorMethodsPatched +
                ", validatorForeignSkipped=" + validatorMethodsSkippedForeign +
                ", reachPatched=" + reachPatched +
                ", reachAuthoritativeSafe=" + reachAuthoritativeSafe +
                ", packages=" + Interlocked.Read(ref packages) +
                ", nested=" + Interlocked.Read(ref nestedPackages) +
                ", validator[observed=" + Interlocked.Read(ref validatorObserved) +
                ", stores=" + Interlocked.Read(ref validatorNegativeStores) +
                ", memoCandidates=" + Interlocked.Read(ref validatorMemoCandidates) +
                ", authoritativeHits=" + Interlocked.Read(ref validatorAuthoritativeHits) +
                ", verify=" + Interlocked.Read(ref validatorVerifyRuns) +
                ", matches=" + Interlocked.Read(ref validatorVerifyMatches) +
                ", mismatches=" + Interlocked.Read(ref validatorMismatches) +
                ", quarantines=" + Interlocked.Read(ref validatorQuarantines) +
                ", fingerprintBypass=" + Interlocked.Read(ref validatorFingerprintBypass) +
                ", capBypass=" + Interlocked.Read(ref validatorCapacityBypass) +
                ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) +
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) + "]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
                ", stores=" + Interlocked.Read(ref reachStores) +
                ", memoCandidates=" + Interlocked.Read(ref reachMemoCandidates) +
                ", authoritativeHits=" + Interlocked.Read(ref reachAuthoritativeHits) +
                ", verify=" + Interlocked.Read(ref reachVerifyRuns) +
                ", matches=" + Interlocked.Read(ref reachVerifyMatches) +
                ", mismatches=" + Interlocked.Read(ref reachMismatches) +
                ", runtimeQuarantined=" + reachRuntimeQuarantined +
                ", foreignResultBypass=" + Interlocked.Read(ref reachForeignResultBypass) +
                ", mutationBypass=" + Interlocked.Read(ref reachMutationBypass) +
                ", invalidations=" + Interlocked.Read(ref reachInvalidations) +
                ", capBypass=" + Interlocked.Read(ref reachCapacityBypass) +
                ", exceptions=" + Interlocked.Read(ref reachExceptions) + "]" +
                ", installFailures=" + installFailures +
                ". One synchronous package only; validator caches false only; no Job/Reservation/priority/cross-tick cache.";
        }

        internal struct PackageState
        {
            internal bool Entered;
            internal bool Outermost;
            internal TransactionContext Context;
        }

        internal struct ValidatorCallState
        {
            internal TransactionContext Context;
            internal ValidatorKey Key;
            internal WorkGiver_Scanner Scanner;
            internal Thing Thing;
            internal ValidatorTrustState Trust;
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;

            internal static ValidatorCallState Store(TransactionContext context, ValidatorKey key,
                WorkGiver_Scanner scanner, Thing thing)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Scanner = scanner, Thing = thing, Store = true
                };
            }

            internal static ValidatorCallState Verify(TransactionContext context, ValidatorKey key,
                ValidatorTrustState trust)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Trust = trust, Verify = true
                };
            }
        }

        internal struct ReachCallState
        {
            internal TransactionContext Context;
            internal ReachKey Key;
            internal LocalTargetInfo Dest;
            internal bool Cached;
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;

            internal static ReachCallState Store(TransactionContext context, ReachKey key, LocalTargetInfo dest)
            {
                return new ReachCallState { Context = context, Key = key, Dest = dest, Store = true };
            }

            internal static ReachCallState VerifyOnly(TransactionContext context, ReachKey key, bool cached, LocalTargetInfo dest)
            {
                return new ReachCallState { Context = context, Key = key, Dest = dest, Cached = cached, Verify = true };
            }

            internal static ReachCallState Authoritative(TransactionContext context, ReachKey key, bool cached, LocalTargetInfo dest)
            {
                return new ReachCallState { Context = context, Key = key, Dest = dest, Cached = cached, AuthoritativeHit = true };
            }
        }

        internal sealed class TransactionContext
        {
            internal readonly Pawn Pawn;
            internal readonly Map Map;
            internal readonly IntVec3 StartPosition;
            internal readonly Dictionary<ValidatorKey, ValidatorNegativeEntry> ValidatorNegatives =
                new Dictionary<ValidatorKey, ValidatorNegativeEntry>();
            internal readonly Dictionary<ReachKey, ReachEntry> ReachMemo =
                new Dictionary<ReachKey, ReachEntry>();
            internal long ReachHitSerial;
            internal int ReachValidatedMatches;

            internal TransactionContext(Pawn pawn)
            {
                Pawn = pawn;
                Map = pawn == null ? null : pawn.Map;
                StartPosition = pawn == null ? IntVec3.Invalid : pawn.Position;
            }
        }

        internal sealed class ValidatorTrustState
        {
            internal long HitSerial;
            internal int ValidatedMatches;
            internal bool Quarantined;
        }

        internal struct ValidatorNegativeEntry
        {
            internal readonly ThingFingerprint Fingerprint;
            internal ValidatorNegativeEntry(ThingFingerprint fingerprint) { Fingerprint = fingerprint; }
        }

        internal struct ReachEntry
        {
            internal readonly bool Result;
            internal readonly TargetFingerprint Fingerprint;
            internal ReachEntry(bool result, TargetFingerprint fingerprint)
            {
                Result = result; Fingerprint = fingerprint;
            }
        }

        internal struct ValidatorKey : IEquatable<ValidatorKey>
        {
            internal readonly MethodBase Method;
            internal readonly WorkGiver_Scanner Scanner;
            internal readonly Thing Thing;
            internal ValidatorKey(MethodBase method, WorkGiver_Scanner scanner, Thing thing)
            {
                Method = method; Scanner = scanner; Thing = thing;
            }
            public bool Equals(ValidatorKey other)
            {
                return ReferenceEquals(Method, other.Method) && ReferenceEquals(Scanner, other.Scanner) &&
                    ReferenceEquals(Thing, other.Thing);
            }
            public override bool Equals(object obj) { return obj is ValidatorKey && Equals((ValidatorKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = RuntimeHelpers.GetHashCode(Method);
                    h = h * 397 ^ RuntimeHelpers.GetHashCode(Scanner);
                    h = h * 397 ^ RuntimeHelpers.GetHashCode(Thing);
                    return h;
                }
            }
        }

        internal struct ValidatorTrustKey : IEquatable<ValidatorTrustKey>
        {
            internal readonly MethodBase Method;
            internal readonly WorkGiver_Scanner Scanner;
            internal ValidatorTrustKey(MethodBase method, WorkGiver_Scanner scanner)
            {
                Method = method; Scanner = scanner;
            }
            public bool Equals(ValidatorTrustKey other)
            {
                return ReferenceEquals(Method, other.Method) && ReferenceEquals(Scanner, other.Scanner);
            }
            public override bool Equals(object obj) { return obj is ValidatorTrustKey && Equals((ValidatorTrustKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    return RuntimeHelpers.GetHashCode(Method) * 397 ^ RuntimeHelpers.GetHashCode(Scanner);
                }
            }
        }

        internal struct ReachKey : IEquatable<ReachKey>
        {
            internal readonly Reachability Reachability;
            internal readonly IntVec3 Start;
            internal readonly bool HasThing;
            internal readonly Thing Thing;
            internal readonly IntVec3 Cell;
            internal readonly PathEndMode EndMode;
            internal readonly TraverseParms Traverse;

            internal ReachKey(Reachability reachability, IntVec3 start, LocalTargetInfo dest,
                PathEndMode endMode, TraverseParms traverse)
            {
                Reachability = reachability;
                Start = start;
                HasThing = dest.HasThing;
                Thing = dest.HasThing ? dest.Thing : null;
                Cell = dest.Cell;
                EndMode = endMode;
                Traverse = traverse;
            }

            public bool Equals(ReachKey other)
            {
                return ReferenceEquals(Reachability, other.Reachability) && Start == other.Start &&
                    HasThing == other.HasThing && ReferenceEquals(Thing, other.Thing) && Cell == other.Cell &&
                    EndMode == other.EndMode && Traverse.Equals(other.Traverse);
            }
            public override bool Equals(object obj) { return obj is ReachKey && Equals((ReachKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int h = RuntimeHelpers.GetHashCode(Reachability);
                    h = h * 397 ^ Start.GetHashCode();
                    h = h * 397 ^ (HasThing ? RuntimeHelpers.GetHashCode(Thing) : Cell.GetHashCode());
                    h = h * 397 ^ (int)EndMode;
                    h = h * 397 ^ Traverse.GetHashCode();
                    return h;
                }
            }
        }

        internal struct ThingFingerprint
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly bool Forbidden;

            internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount,
                int hitPoints, bool forbidden)
            {
                MapHeld = mapHeld; PositionHeld = positionHeld; Spawned = spawned;
                StackCount = stackCount; HitPoints = hitPoints; Forbidden = forbidden;
            }

            internal static ThingFingerprint Capture(Pawn pawn, Thing thing)
            {
                bool forbidden = false;
                try { forbidden = pawn != null && thing.IsForbidden(pawn); } catch { }
                return new ThingFingerprint(thing.MapHeld, thing.PositionHeld, thing.Spawned,
                    thing.stackCount, thing.HitPoints, forbidden);
            }

            internal bool Matches(Pawn pawn, Thing thing)
            {
                if (thing == null || !ReferenceEquals(MapHeld, thing.MapHeld) || PositionHeld != thing.PositionHeld ||
                    Spawned != thing.Spawned || StackCount != thing.stackCount || HitPoints != thing.HitPoints)
                    return false;
                try { return Forbidden == (pawn != null && thing.IsForbidden(pawn)); }
                catch { return false; }
            }
        }

        internal struct TargetFingerprint
        {
            internal readonly bool HasThing;
            internal readonly Thing Thing;
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly IntVec3 Cell;

            internal TargetFingerprint(bool hasThing, Thing thing, Map mapHeld, IntVec3 positionHeld,
                bool spawned, IntVec3 cell)
            {
                HasThing = hasThing; Thing = thing; MapHeld = mapHeld; PositionHeld = positionHeld;
                Spawned = spawned; Cell = cell;
            }

            internal static TargetFingerprint Capture(LocalTargetInfo target)
            {
                if (!target.HasThing)
                    return new TargetFingerprint(false, null, null, IntVec3.Invalid, false, target.Cell);
                Thing thing = target.Thing;
                return new TargetFingerprint(true, thing, thing == null ? null : thing.MapHeld,
                    thing == null ? IntVec3.Invalid : thing.PositionHeld, thing != null && thing.Spawned, target.Cell);
            }

            internal bool Matches(LocalTargetInfo target)
            {
                if (HasThing != target.HasThing || Cell != target.Cell) return false;
                if (!HasThing) return true;
                Thing thing = target.Thing;
                return ReferenceEquals(Thing, thing) && thing != null && ReferenceEquals(MapHeld, thing.MapHeld) &&
                    PositionHeld == thing.PositionHeld && Spawned == thing.Spawned;
            }
        }

        private sealed class ScannerAccessor
        {
            private readonly FieldInfo[] path;
            private ScannerAccessor(FieldInfo[] path) { this.path = path; }

            internal WorkGiver_Scanner Read(object root)
            {
                object value = root;
                try
                {
                    for (int i = 0; i < path.Length; i++)
                    {
                        if (value == null) return null;
                        value = path[i].GetValue(value);
                    }
                    return value as WorkGiver_Scanner;
                }
                catch { return null; }
            }

            internal static ScannerAccessor Build(Type root)
            {
                List<FieldInfo> path = new List<FieldInfo>();
                HashSet<Type> visited = new HashSet<Type>();
                if (Find(root, 0, path, visited)) return new ScannerAccessor(path.ToArray());
                return null;
            }

            private static bool Find(Type type, int depth, List<FieldInfo> path, HashSet<Type> visited)
            {
                if (type == null || depth > 3 || visited.Contains(type)) return false;
                visited.Add(type);
                FieldInfo[] fields;
                try { fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic); }
                catch { return false; }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null) continue;
                    if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType))
                    {
                        path.Add(field);
                        return true;
                    }
                }

                for (int i = 0; i < fields.Length; i++)
                {
                    FieldInfo field = fields[i];
                    if (field == null || field.FieldType.IsPrimitive || field.FieldType == typeof(string) ||
                        field.FieldType.IsEnum || field.FieldType.IsPointer)
                        continue;
                    path.Add(field);
                    if (Find(field.FieldType, depth + 1, path, visited)) return true;
                    path.RemoveAt(path.Count - 1);
                }
                return false;
            }
        }
    }
}
