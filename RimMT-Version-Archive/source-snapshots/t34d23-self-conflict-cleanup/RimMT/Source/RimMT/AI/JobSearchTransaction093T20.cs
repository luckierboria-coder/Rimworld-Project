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
    /// T21 Foundation II: one synchronous JobGiver_Work.TryIssueJobPackage is treated as a
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
        private const int AdaptiveEagerEnterMinStores = 128;
        private const int AdaptiveLazyReturnMinStores = 256;
        private const int AdaptiveEvidenceMaxStores = 4096;
        private const int ReachWarmupMatches = 8;
        private const int ReachVerifyMask = 63;
        // T21 chooses actual priorities around the already-installed chain at runtime.
        // These are only fail-safe defaults if the chain cannot be inspected.
        private const int ReachReplayPrefixFallbackPriority = -1000000;
        private const int ReachBasePostfixFallbackPriority = 1000000;

        [ThreadStatic] private static int depth;
        [ThreadStatic] private static TransactionContext current;

        private static readonly object TrustLock = new object();
        private static readonly Dictionary<ValidatorTrustKey, ValidatorTrustState> ValidatorTrust =
            new Dictionary<ValidatorTrustKey, ValidatorTrustState>();

        private static bool installed;
        private static bool packagePatched;
        private static bool reachPatched;
        private static bool reachChainAuthoritativeSafe;
        private static int reachReplayPrefixPriority = ReachReplayPrefixFallbackPriority;
        private static int reachBasePostfixPriority = ReachBasePostfixFallbackPriority;
        private static int reachForeignPrefixes;
        private static int reachForeignPostfixes;
        private static int reachForeignResultPostfixes;
        private static int reachRunOriginalPostfixes;
        private static int reachForeignTranspilers;
        private static int reachForeignFinalizers;
        private static bool reachChainAuditUnknown;
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
        private static long validatorLazyStores;
        private static long validatorLazyRepeatProbes;
        private static long validatorLazyPrimeSuccess;
        private static long validatorLazyPrimePositive;
        private static long validatorLazyPrimeUnstable;
        private static long validatorForbiddenReads;
        private static long validatorStoreForbiddenReadsAvoided;
        private static long validatorAdaptiveFirstRepeats;
        private static long validatorAdaptiveEagerStores;
        private static long validatorAdaptiveEagerCaptures;
        private static long validatorAdaptiveEagerFallbackLazy;
        private static long validatorAdaptiveSwitchToEager;
        private static long validatorAdaptiveSwitchToLazy;

        private static long reachObserved;
        private static long reachStores;
        private static long reachMemoCandidates;
        private static long reachAuthoritativeHits;
        private static long reachVerifyRuns;
        private static long reachVerifyMatches;
        private static long reachMismatches;
        private static long reachLocalQuarantines;
        private static long reachLocalQuarantineBypass;
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
                packagePatched = JobSearchPackageContext093T28.Installed;

                PatchJobGiverValidators(harmony);
                PatchReachability(harmony);
                installed = packagePatched && validatorMethodsPatched > 0;

                Log.Message("[RimMT] T21 Foundation II transaction core installed=" + installed +
                    ", package=" + packagePatched +
                    ", validatorMethods=" + validatorMethodsPatched +
                    ", reach=" + reachPatched +
                    ", reachChainAuthoritativeSafe=" + reachChainAuthoritativeSafe +
                    ". Lifetime=one synchronous JobGiver_Work package; negative-validator only; Vanilla live parity/quarantine remains authority.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T21 Foundation II transaction install failed closed: " +
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

                ReachChainAudit chain = InspectReachChain(target);
                reachForeignPrefixes = chain.ForeignPrefixes;
                reachForeignPostfixes = chain.ForeignPostfixes;
                reachForeignResultPostfixes = chain.ResultMutatingPostfixes;
                reachRunOriginalPostfixes = chain.RunOriginalPostfixes;
                reachForeignTranspilers = chain.ForeignTranspilers;
                reachForeignFinalizers = chain.ForeignFinalizers;
                reachChainAuditUnknown = chain.Unknown;

                // Run after every already-installed prefix so argument/result changes and prefix
                // side effects remain live. Capture before every already-installed postfix so the
                // memo contains only the base result, never a foreign-transformed final result.
                if (!chain.Unknown && chain.MinAnyPrefixPriority > int.MinValue)
                    reachReplayPrefixPriority = chain.MinAnyPrefixPriority - 1;
                else
                    reachReplayPrefixPriority = ReachReplayPrefixFallbackPriority;
                if (!chain.Unknown && chain.MaxAnyPostfixPriority < int.MaxValue)
                    reachBasePostfixPriority = chain.MaxAnyPostfixPriority + 1;
                else
                    reachBasePostfixPriority = ReachBasePostfixFallbackPriority;

                reachChainAuthoritativeSafe = !chain.Unknown && chain.PriorityRoom &&
                    chain.ForeignTranspilers == 0 && chain.ForeignFinalizers == 0 &&
                    chain.RunOriginalPostfixes == 0;

                HarmonyMethod reachPrefix = new HarmonyMethod(
                    typeof(JobSearchTransaction093T20), nameof(ReachPrefix))
                    { priority = reachReplayPrefixPriority, after = chain.ForeignPrefixOwners };
                HarmonyMethod reachBase = new HarmonyMethod(
                    typeof(JobSearchTransaction093T20), nameof(ReachBasePostfix))
                    { priority = reachBasePostfixPriority, before = chain.ForeignPostfixOwners };
                HarmonyMethod reachFinalizer = new HarmonyMethod(
                    typeof(JobSearchTransaction093T20), nameof(ReachFinalizer))
                    { priority = Priority.Last - 300 };

                harmony.Patch(target, prefix: reachPrefix, postfix: reachBase, finalizer: reachFinalizer);
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
            Thing __0,
            ref bool __result,
            ref ValidatorCallState __state)
        {
            __state = default(ValidatorCallState);
            // T34-D.1.2 worker fast path. Worker validators have no package-local T20
            // transaction context. Return before global counters, reflection and shared state.
            // Using __0 also avoids Harmony constructing a fresh object[] for every validator.
            if (!RimMTThreadGuard.IsMainThread)
                return true;
            Interlocked.Increment(ref validatorObserved);

            TransactionContext context = current;
            if (context == null || depth <= 0 || __originalMethod == null)
                return true;

            Thing thing = __0;
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
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            if (!entry.Fingerprint.MatchesCheap(thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            ValidatorTrustState trust = GetTrust(__originalMethod, scanner);
            if (!entry.RepeatObserved)
            {
                entry.RepeatObserved = true;
                context.ValidatorNegatives[key] = entry;
                trust.ObserveFirstRepeat();
                Interlocked.Increment(ref validatorAdaptiveFirstRepeats);
            }

            if (!entry.Primed)
            {
                bool forbiddenBefore;
                if (!TryReadForbidden(context.Pawn, thing, out forbiddenBefore))
                {
                    context.ValidatorNegatives.Remove(key);
                    Interlocked.Increment(ref validatorFingerprintBypass);
                    __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                    return true;
                }

                Interlocked.Increment(ref validatorLazyRepeatProbes);
                __state = new ValidatorCallState
                {
                    Context = context,
                    Key = key,
                    Scanner = scanner,
                    Thing = thing,
                    Prime = true,
                    PrimeFingerprint = entry.Fingerprint,
                    PrimeForbiddenBefore = forbiddenBefore
                };
                return true;
            }

            if (!entry.Fingerprint.MatchesForbidden(context.Pawn, thing))
            {
                context.ValidatorNegatives.Remove(key);
                Interlocked.Increment(ref validatorFingerprintBypass);
                __state = ValidatorCallState.ForStore(context, key, scanner, thing);
                return true;
            }

            Interlocked.Increment(ref validatorMemoCandidates);
            if (trust.Quarantined)
                return true;

            long serial = ++trust.HitSerial;
            bool verify = trust.ValidatedMatches < ValidatorWarmupMatches || (serial & ValidatorVerifyMask) == 0;
            if (verify)
            {
                Interlocked.Increment(ref validatorVerifyRuns);
                __state = ValidatorCallState.ForVerify(context, key, trust);
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

            if (__state.Prime)
            {
                if (__result)
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimePositive);
                    return;
                }

                Thing thing = __state.Thing;
                if (thing == null || !__state.PrimeFingerprint.MatchesCheap(thing))
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimeUnstable);
                    return;
                }

                bool forbiddenAfter;
                if (!TryReadForbidden(context.Pawn, thing, out forbiddenAfter) ||
                    forbiddenAfter != __state.PrimeForbiddenBefore)
                {
                    context.ValidatorNegatives.Remove(__state.Key);
                    Interlocked.Increment(ref validatorLazyPrimeUnstable);
                    return;
                }

                ValidatorNegativeEntry existing;
                if (context.ValidatorNegatives.TryGetValue(__state.Key, out existing) &&
                    existing.Fingerprint.MatchesCheap(thing))
                {
                    existing.Primed = true;
                    existing.Fingerprint = existing.Fingerprint.WithForbidden(forbiddenAfter);
                    context.ValidatorNegatives[__state.Key] = existing;
                    Interlocked.Increment(ref validatorLazyPrimeSuccess);
                }
                return;
            }

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
                ValidatorTrustState trust = GetTrust(__state.Key.Method, __state.Scanner);
                trust.ObserveStore();

                ThingFingerprint fingerprint = ThingFingerprint.CaptureCheap(__state.Thing);
                bool primed = false;

                if (trust.PreferEager)
                {
                    Interlocked.Increment(ref validatorAdaptiveEagerStores);
                    bool forbidden;
                    if (TryReadForbidden(context.Pawn, __state.Thing, out forbidden))
                    {
                        fingerprint = fingerprint.WithForbidden(forbidden);
                        primed = true;
                        Interlocked.Increment(ref validatorAdaptiveEagerCaptures);
                    }
                    else
                    {
                        Interlocked.Increment(ref validatorAdaptiveEagerFallbackLazy);
                        Interlocked.Increment(ref validatorLazyStores);
                    }
                }
                else
                {
                    Interlocked.Increment(ref validatorLazyStores);
                    Interlocked.Increment(ref validatorStoreForbiddenReadsAvoided);
                }

                context.ValidatorNegatives.Add(__state.Key,
                    new ValidatorNegativeEntry(fingerprint, primed));
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
            if (!RimMTThreadGuard.IsMainThread)
                return true;
            Interlocked.Increment(ref reachObserved);

            TransactionContext context = current;
            if (!__runOriginal || context == null || depth <= 0 ||
                __instance == null || context.Pawn == null || traverseParams.pawn == null ||
                !ReferenceEquals(traverseParams.pawn, context.Pawn))
                return true;

            if (!start.IsValid || !dest.IsValid || context.Pawn.Map == null || start != context.Pawn.Position)
                return true;

            ReachKey key = new ReachKey(__instance, start, dest, peMode, traverseParams);
            if (context.ReachQuarantined.Contains(key))
            {
                Interlocked.Increment(ref reachLocalQuarantineBypass);
                return true;
            }
            ReachEntry entry;
            if (!context.ReachMemo.TryGetValue(key, out entry))
            {
                __state = ReachCallState.ForStore(context, key, dest);
                return true;
            }

            if (!entry.Fingerprint.Matches(dest))
            {
                context.ReachMemo.Remove(key);
                Interlocked.Increment(ref reachMutationBypass);
                __state = ReachCallState.ForStore(context, key, dest);
                return true;
            }

            Interlocked.Increment(ref reachMemoCandidates);
            if (!reachChainAuthoritativeSafe)
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

        // Runs before every already-installed postfix. On live calls this is the raw/base
        // Reachability result. On memo hits the prefix has injected that same base result and
        // skipped only the expensive body; all foreign postfixes still run after this method.
        public static void ReachBasePostfix(bool __result, ref ReachCallState __state)
        {
            if (__state.AuthoritativeHit) return;
            TransactionContext context = __state.Context;
            if (context == null || !ReferenceEquals(current, context)) return;

            if (__state.Verify)
            {
                if (__result == __state.Cached)
                {
                    context.ReachValidatedMatches++;
                    Interlocked.Increment(ref reachVerifyMatches);
                }
                else
                {
                    // T24: one divergent query shape fails open locally for the remainder of this
                    // synchronous JobGiver package. Other Reach keys remain available. Nothing is
                    // carried into another package/tick.
                    context.ReachMemo.Remove(__state.Key);
                    context.ReachQuarantined.Add(__state.Key);
                    context.ReachValidatedMatches = 0;
                    Interlocked.Increment(ref reachMismatches);
                    Interlocked.Increment(ref reachLocalQuarantines);
                }
                return;
            }

            if (!__state.Store) return;
            if (context.ReachMemo.Count >= ReachCapacity)
            {
                Interlocked.Increment(ref reachCapacityBypass);
                return;
            }

            if (!context.ReachMemo.ContainsKey(__state.Key))
            {
                context.ReachMemo.Add(__state.Key,
                    new ReachEntry(__result, TargetFingerprint.Capture(__state.Dest)));
                Interlocked.Increment(ref reachStores);
            }
        }

        public static Exception ReachFinalizer(
            Exception __exception,
            ReachCallState __state)
        {
            if (__exception != null)
            {
                Interlocked.Increment(ref reachExceptions);
                TransactionContext context = __state.Context;
                if (context != null && ReferenceEquals(current, context))
                    context.ReachMemo.Remove(__state.Key);
            }
            return __exception;
        }

        public static void ReachCacheInvalidated()
        {
            TransactionContext context = current;
            if (context == null || depth <= 0) return;
            context.ReachMemo.Clear();
            context.ReachQuarantined.Clear();
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

        private static ReachChainAudit InspectReachChain(MethodBase method)
        {
            ReachChainAudit audit = new ReachChainAudit();
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return audit;

                HashSet<string> prefixOwners = new HashSet<string>();
                HashSet<string> postfixOwners = new HashSet<string>();

                foreach (Patch patch in info.Prefixes)
                {
                    if (patch == null) continue;
                    if (patch.priority < audit.MinAnyPrefixPriority) audit.MinAnyPrefixPriority = patch.priority;
                    if (string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                    audit.ForeignPrefixes++;
                    if (!string.IsNullOrEmpty(patch.owner)) prefixOwners.Add(patch.owner);
                }

                foreach (Patch patch in info.Postfixes)
                {
                    if (patch == null) continue;
                    if (patch.priority > audit.MaxAnyPostfixPriority) audit.MaxAnyPostfixPriority = patch.priority;
                    if (string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                    audit.ForeignPostfixes++;
                    if (!string.IsNullOrEmpty(patch.owner)) postfixOwners.Add(patch.owner);
                    if (PostfixMutatesResult(patch)) audit.ResultMutatingPostfixes++;
                    if (PatchReadsRunOriginal(patch)) audit.RunOriginalPostfixes++;
                }

                foreach (Patch patch in info.Transpilers)
                {
                    if (patch != null && !string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        audit.ForeignTranspilers++;
                }
                foreach (Patch patch in info.Finalizers)
                {
                    if (patch != null && !string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        audit.ForeignFinalizers++;
                }

                audit.ForeignPrefixOwners = new string[prefixOwners.Count];
                prefixOwners.CopyTo(audit.ForeignPrefixOwners);
                audit.ForeignPostfixOwners = new string[postfixOwners.Count];
                postfixOwners.CopyTo(audit.ForeignPostfixOwners);
                audit.PriorityRoom = audit.MinAnyPrefixPriority > int.MinValue &&
                    audit.MaxAnyPostfixPriority < int.MaxValue;
                return audit;
            }
            catch
            {
                audit.Unknown = true;
                audit.PriorityRoom = false;
                return audit;
            }
        }

        private static bool PostfixMutatesResult(Patch patch)
        {
            MethodInfo pm = patch == null ? null : patch.PatchMethod;
            if (pm == null) return true;
            if (pm.ReturnType == typeof(bool)) return true; // pass-through postfix
            ParameterInfo[] pars = pm.GetParameters();
            for (int i = 0; i < pars.Length; i++)
            {
                ParameterInfo p = pars[i];
                if (p.Name == "__result" && p.ParameterType.IsByRef &&
                    p.ParameterType.GetElementType() == typeof(bool))
                    return true;
            }
            return false;
        }

        private static bool PatchReadsRunOriginal(Patch patch)
        {
            MethodInfo pm = patch == null ? null : patch.PatchMethod;
            if (pm == null) return true;
            ParameterInfo[] pars = pm.GetParameters();
            for (int i = 0; i < pars.Length; i++)
                if (pars[i].Name == "__runOriginal") return true;
            return false;
        }

        private static void CountAdaptiveModes(out int lazy, out int eager)
        {
            lazy = 0;
            eager = 0;
            lock (TrustLock)
            {
                foreach (ValidatorTrustState state in ValidatorTrust.Values)
                {
                    if (state != null && state.PreferEager) eager++;
                    else lazy++;
                }
            }
        }

        internal static string Summary()
        {
            int adaptiveLazyModes;
            int adaptiveEagerModes;
            CountAdaptiveModes(out adaptiveLazyModes, out adaptiveEagerModes);
            return "T21 foundation transaction: installed=" + installed +
                ", packagePatched=" + packagePatched +
                ", validatorMethods=" + validatorMethodsPatched +
                ", validatorForeignSkipped=" + validatorMethodsSkippedForeign +
                ", reachPatched=" + reachPatched +
                ", reachChainAuthoritativeSafe=" + reachChainAuthoritativeSafe +
                ", reachChain[foreignPrefixes=" + reachForeignPrefixes +
                ", foreignPostfixes=" + reachForeignPostfixes +
                ", resultMutators=" + reachForeignResultPostfixes +
                ", runOriginalReaders=" + reachRunOriginalPostfixes +
                ", transpilers=" + reachForeignTranspilers +
                ", finalizers=" + reachForeignFinalizers +
                ", auditUnknown=" + reachChainAuditUnknown +
                ", replayPrefixPriority=" + reachReplayPrefixPriority +
                ", basePostfixPriority=" + reachBasePostfixPriority + "]" +
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
                ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) +
                ", lazy[stores=" + Interlocked.Read(ref validatorLazyStores) +
                ", repeatProbes=" + Interlocked.Read(ref validatorLazyRepeatProbes) +
                ", primeSuccess=" + Interlocked.Read(ref validatorLazyPrimeSuccess) +
                ", primePositive=" + Interlocked.Read(ref validatorLazyPrimePositive) +
                ", primeUnstable=" + Interlocked.Read(ref validatorLazyPrimeUnstable) +
                ", forbiddenReads=" + Interlocked.Read(ref validatorForbiddenReads) +
                ", storeForbiddenReadsAvoided=" + Interlocked.Read(ref validatorStoreForbiddenReadsAvoided) +
                ", adaptive[firstRepeats=" + Interlocked.Read(ref validatorAdaptiveFirstRepeats) +
                ", eagerStores=" + Interlocked.Read(ref validatorAdaptiveEagerStores) +
                ", eagerCaptures=" + Interlocked.Read(ref validatorAdaptiveEagerCaptures) +
                ", eagerFallbackLazy=" + Interlocked.Read(ref validatorAdaptiveEagerFallbackLazy) +
                ", switchToEager=" + Interlocked.Read(ref validatorAdaptiveSwitchToEager) +
                ", switchToLazy=" + Interlocked.Read(ref validatorAdaptiveSwitchToLazy) +
                ", modesLazy/Eager=" + adaptiveLazyModes + "/" + adaptiveEagerModes +
                ", enter>=1/8@128+trusted, return<1/16@256]]]" +
                ", reach[observed=" + Interlocked.Read(ref reachObserved) +
                ", stores=" + Interlocked.Read(ref reachStores) +
                ", memoCandidates=" + Interlocked.Read(ref reachMemoCandidates) +
                ", authoritativeHits=" + Interlocked.Read(ref reachAuthoritativeHits) +
                ", verify=" + Interlocked.Read(ref reachVerifyRuns) +
                ", matches=" + Interlocked.Read(ref reachVerifyMatches) +
                ", mismatches=" + Interlocked.Read(ref reachMismatches) +
                ", localQuarantines=" + Interlocked.Read(ref reachLocalQuarantines) +
                ", localQuarantineBypass=" + Interlocked.Read(ref reachLocalQuarantineBypass) +
                ", runtimeQuarantined=" + reachRuntimeQuarantined +
                ", foreignResultBypass=" + Interlocked.Read(ref reachForeignResultBypass) +
                ", mutationBypass=" + Interlocked.Read(ref reachMutationBypass) +
                ", invalidations=" + Interlocked.Read(ref reachInvalidations) +
                ", capBypass=" + Interlocked.Read(ref reachCapacityBypass) +
                ", exceptions=" + Interlocked.Read(ref reachExceptions) + "]" +
                ", installFailures=" + installFailures +
                ". One synchronous package only; validator caches false only; T32-B.1 adaptive fingerprint mode is keyed only by validator method+scanner trust state; no Job/JobOnThing/reservation/priority/cross-package result is cached.";
        }

        internal sealed class ReachChainAudit
        {
            internal int ForeignPrefixes;
            internal int ForeignPostfixes;
            internal int ResultMutatingPostfixes;
            internal int RunOriginalPostfixes;
            internal int ForeignTranspilers;
            internal int ForeignFinalizers;
            internal int MinAnyPrefixPriority = int.MaxValue;
            internal int MaxAnyPostfixPriority = int.MinValue;
            internal string[] ForeignPrefixOwners = new string[0];
            internal string[] ForeignPostfixOwners = new string[0];
            internal bool PriorityRoom = true;
            internal bool Unknown;
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
            internal bool Prime;
            internal bool PrimeForbiddenBefore;
            internal ThingFingerprint PrimeFingerprint;
            internal bool AuthoritativeHit;

            internal static ValidatorCallState ForStore(TransactionContext context, ValidatorKey key,
                WorkGiver_Scanner scanner, Thing thing)
            {
                return new ValidatorCallState
                {
                    Context = context, Key = key, Scanner = scanner, Thing = thing, Store = true
                };
            }

            internal static ValidatorCallState ForVerify(TransactionContext context, ValidatorKey key,
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

            internal static ReachCallState ForStore(TransactionContext context, ReachKey key, LocalTargetInfo dest)
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
            internal readonly HashSet<ReachKey> ReachQuarantined = new HashSet<ReachKey>();
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

            internal int AdaptiveStores;
            internal int AdaptiveFirstRepeats;
            internal bool PreferEager;

            internal void ObserveStore()
            {
                if (AdaptiveStores < int.MaxValue) AdaptiveStores++;
                EvaluateAdaptiveMode();
            }

            internal void ObserveFirstRepeat()
            {
                if (AdaptiveFirstRepeats < int.MaxValue) AdaptiveFirstRepeats++;
                EvaluateAdaptiveMode();
            }

            private void EvaluateAdaptiveMode()
            {
                if (AdaptiveStores >= AdaptiveEvidenceMaxStores)
                {
                    AdaptiveStores = (AdaptiveStores + 1) >> 1;
                    AdaptiveFirstRepeats = (AdaptiveFirstRepeats + 1) >> 1;
                }

                if (Quarantined) return;

                if (!PreferEager)
                {
                    if (ValidatedMatches < ValidatorWarmupMatches ||
                        AdaptiveStores < AdaptiveEagerEnterMinStores)
                        return;

                    // Enter Eager at >= 1/8 first-repeat rate.
                    if ((long)AdaptiveFirstRepeats * 8L >= AdaptiveStores)
                    {
                        PreferEager = true;
                        AdaptiveStores = 0;
                        AdaptiveFirstRepeats = 0;
                        Interlocked.Increment(ref validatorAdaptiveSwitchToEager);
                    }
                    return;
                }

                if (AdaptiveStores < AdaptiveLazyReturnMinStores)
                    return;

                // Return to Lazy below 1/16. Hysteresis avoids oscillation.
                if ((long)AdaptiveFirstRepeats * 16L < AdaptiveStores)
                {
                    PreferEager = false;
                    AdaptiveStores = 0;
                    AdaptiveFirstRepeats = 0;
                    Interlocked.Increment(ref validatorAdaptiveSwitchToLazy);
                }
            }
        }

        internal struct ValidatorNegativeEntry
        {
            internal ThingFingerprint Fingerprint;
            internal bool Primed;
            internal bool RepeatObserved;

            internal ValidatorNegativeEntry(ThingFingerprint fingerprint, bool primed)
            {
                Fingerprint = fingerprint;
                Primed = primed;
                RepeatObserved = false;
            }
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

        private static bool TryReadForbidden(Pawn pawn, Thing thing, out bool forbidden)
        {
            forbidden = false;
            if (pawn == null || thing == null) return false;
            try
            {
                forbidden = thing.IsForbidden(pawn);
                Interlocked.Increment(ref validatorForbiddenReads);
                return true;
            }
            catch
            {
                return false;
            }
        }

        internal struct ThingFingerprint
        {
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;
            internal readonly int HitPoints;
            internal readonly bool HasForbidden;
            internal readonly bool Forbidden;

            internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount,
                int hitPoints, bool hasForbidden, bool forbidden)
            {
                MapHeld = mapHeld;
                PositionHeld = positionHeld;
                Spawned = spawned;
                StackCount = stackCount;
                HitPoints = hitPoints;
                HasForbidden = hasForbidden;
                Forbidden = forbidden;
            }

            // Store path: deliberately cheap. No IsForbidden call here.
            internal static ThingFingerprint CaptureCheap(Thing thing)
            {
                if (thing == null)
                    return new ThingFingerprint(null, IntVec3.Invalid, false, 0, 0, false, false);

                return new ThingFingerprint(
                    thing.MapHeld,
                    thing.PositionHeld,
                    thing.Spawned,
                    thing.stackCount,
                    thing.HitPoints,
                    false,
                    false);
            }

            internal ThingFingerprint WithForbidden(bool forbidden)
            {
                return new ThingFingerprint(
                    MapHeld, PositionHeld, Spawned, StackCount, HitPoints, true, forbidden);
            }

            internal bool MatchesCheap(Thing thing)
            {
                return thing != null &&
                    ReferenceEquals(MapHeld, thing.MapHeld) &&
                    PositionHeld == thing.PositionHeld &&
                    Spawned == thing.Spawned &&
                    StackCount == thing.stackCount &&
                    HitPoints == thing.HitPoints;
            }

            internal bool MatchesForbidden(Pawn pawn, Thing thing)
            {
                if (!HasForbidden || !MatchesCheap(thing))
                    return false;

                bool currentForbidden;
                return TryReadForbidden(pawn, thing, out currentForbidden) &&
                    Forbidden == currentForbidden;
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






