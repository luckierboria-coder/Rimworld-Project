$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T21 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T21 Foundation II: chain-aware Reachability transaction reuse.
# The expensive base result is memoized only inside one synchronous JobGiver_Work package.
# Every prefix still executes live before the low-priority replay prefix. Every postfix still
# executes live after a replay. A first-running RimMT postfix captures/verifies the base result
# before foreign postfixes transform it, so foreign final-result semantics remain authoritative.

$corePath = 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$core = Get-Content $corePath -Raw
$core = $core.Replace('T20 Foundation:', 'T21 Foundation II:')
$core = $core.Replace('T20 Foundation transaction core installed=', 'T21 Foundation II transaction core installed=')
$core = $core.Replace('T20 Foundation transaction install failed closed:', 'T21 Foundation II transaction install failed closed:')
$core = $core.Replace('T20 foundation transaction:', 'T21 foundation transaction:')
$core = $core.Replace('reachAuthoritativeSafe', 'reachChainAuthoritativeSafe')

$core = Replace-OrThrow $core @'
        private const int ReachWarmupMatches = 8;
        private const int ReachVerifyMask = 63;
'@ @'
        private const int ReachWarmupMatches = 8;
        private const int ReachVerifyMask = 63;
        // T21 chooses actual priorities around the already-installed chain at runtime.
        // These are only fail-safe defaults if the chain cannot be inspected.
        private const int ReachReplayPrefixFallbackPriority = -1000000;
        private const int ReachBasePostfixFallbackPriority = 1000000;
'@ 'T21 reach priority constants'

$core = Replace-OrThrow $core @'
        private static bool reachPatched;
        private static bool reachChainAuthoritativeSafe;
        private static int validatorMethodsPatched;
'@ @'
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
'@ 'T21 reach audit fields'

$core = Replace-OrThrow $core @'
                reachChainAuthoritativeSafe = !HasResultMutatingForeignPostfix(target);
                harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ReachPrefix))
                    { priority = Priority.Last - 300 },
                    finalizer: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(ReachFinalizer))
                    { priority = Priority.Last - 300 });
                reachPatched = true;
'@ @'
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
'@ 'T21 chain-aware reach patch install'

$core = Replace-OrThrow $core @'
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
'@ @'
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
                    context.ReachMemo.Clear();
                    context.ReachValidatedMatches = 0;
                    reachRuntimeQuarantined = true;
                    Interlocked.Increment(ref reachMismatches);
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
'@ 'T21 pre-foreign base capture and finalizer'

$core = Replace-OrThrow $core @'
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
'@ @'
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
'@ 'T21 reach chain audit'

$core = Replace-OrThrow $core @'
                ", reachPatched=" + reachPatched +
                ", reachChainAuthoritativeSafe=" + reachChainAuthoritativeSafe +
                ", packages=" + Interlocked.Read(ref packages) +
'@ @'
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
'@ 'T21 summary chain audit'

$core = Replace-OrThrow $core @'
        internal struct PackageState
        {
'@ @'
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
'@ 'T21 chain audit state'

Set-Content $corePath $core -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t20-foundation-transaction-core";' 'internal const string Version = "0.9.3-t21-foundation-reach-chain";' 'T21 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T20 Foundation Transaction Core initialized. Generic JobGiver package transaction, validator-negative memo and bounded package-local Reachability memo added below WorkGiver-specific logic. T18/T19 diagnostics retained; SMF dispatcher policy unchanged.' '[RimMT] V0.9.3-T21 Foundation II Reach Chain initialized. T20 generic validator transaction retained; package-local Reachability now memoizes only the pre-postfix base result while every foreign prefix/postfix remains live. T18/T19 diagnostics retained; SMF dispatcher policy unchanged.' 'T21 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T20 Foundation Transaction Core', 'V0.9.3-T21 Foundation II Reach Chain')
$report = Replace-OrThrow $report 'T20 adds one-package JobGiver transaction state, generic JobGiver_Work local-validator false-only memo with parity quarantine, and package-local exact CanReach memo that becomes shadow-only when a foreign postfix can mutate the result; no Job/JobOnThing/reservation/priority/cross-package result is cached;' 'T20 generic validator transaction retained; T21 makes package-local CanReach chain-aware by replaying only the pre-postfix base result after all live prefixes, while all foreign postfixes still execute live and remain final authority; any foreign transpiler/finalizer, __runOriginal-reading postfix, unknown ordering, parity mismatch or fingerprint mutation fails open; no Job/JobOnThing/reservation/priority/cross-package result is cached;' 'T21 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T20 Foundation Transaction Core', 'V0.9.3-T21 Foundation II Reach Chain')
    $about = $about.Replace('T18/T19 measured rescues and attribution remain. T20 adds a generic one-package JobGiver transaction core with false-only validator memo and bounded Reachability reuse/shadow validation; no cross-tick query result cache. T17 zero-hit HaulUrgently memo stays disabled.', 'T18/T19 measured rescues and attribution remain. T20 generic validator transaction remains; T21 upgrades package-local Reachability to pre-postfix base-result reuse so foreign prefix/postfix logic stays live. No cross-tick query result cache. T17 zero-hit HaulUrgently memo stays disabled.')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T21 Foundation II: chain-aware package-local Reachability base-result memo; all foreign prefixes/postfixes stay live; generic T20 validator memo retained; no WorkGiver-specific T21 fix.'
