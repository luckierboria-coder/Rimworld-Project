$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T24 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T24 Stutter First
# Production priorities from long-run T23 evidence:
# 1) one sampled Reach mismatch must not disable the whole runtime; quarantine only that package-local key;
# 2) automatic T15 WorkGiver and T18 Storyteller deep Harmony bursts are diagnostic-only and must not
#    contaminate production tail latency;
# 3) retain generic S4 at 32ms, but admit authority-safe targeted cheap-negative WorkGivers from 8ms,
#    where the cheap prefilter can actually remove candidates before the expensive live validator;
# 4) final Vanilla/mod authority is unchanged. No cross-package Job/reservation/priority result cache.

# -----------------------------------------------------------------------------
# T24-A: package-local Reach quarantine instead of whole-runtime fuse.
# -----------------------------------------------------------------------------
$corePath = 'RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$core = Get-Content $corePath -Raw

$core = Replace-OrThrow $core @'
        private static long reachMismatches;
        private static long reachMutationBypass;
'@ @'
        private static long reachMismatches;
        private static long reachLocalQuarantines;
        private static long reachLocalQuarantineBypass;
        private static long reachMutationBypass;
'@ 'T24 Reach local-quarantine counters'

$core = Replace-OrThrow $core @'
            if (!__runOriginal || context == null || depth <= 0 || reachRuntimeQuarantined ||
                __instance == null || context.Pawn == null || traverseParams.pawn == null ||
'@ @'
            if (!__runOriginal || context == null || depth <= 0 ||
                __instance == null || context.Pawn == null || traverseParams.pawn == null ||
'@ 'T24 remove whole-runtime Reach fuse from hot path'

$core = Replace-OrThrow $core @'
            ReachKey key = new ReachKey(__instance, start, dest, peMode, traverseParams);
            ReachEntry entry;
'@ @'
            ReachKey key = new ReachKey(__instance, start, dest, peMode, traverseParams);
            if (context.ReachQuarantined.Contains(key))
            {
                Interlocked.Increment(ref reachLocalQuarantineBypass);
                return true;
            }
            ReachEntry entry;
'@ 'T24 Reach local-quarantine check'

$core = Replace-OrThrow $core @'
                    context.ReachMemo.Clear();
                    context.ReachValidatedMatches = 0;
                    reachRuntimeQuarantined = true;
                    Interlocked.Increment(ref reachMismatches);
'@ @'
                    // T24: one divergent query shape fails open locally for the remainder of this
                    // synchronous JobGiver package. Other Reach keys remain available. Nothing is
                    // carried into another package/tick.
                    context.ReachMemo.Remove(__state.Key);
                    context.ReachQuarantined.Add(__state.Key);
                    context.ReachValidatedMatches = 0;
                    Interlocked.Increment(ref reachMismatches);
                    Interlocked.Increment(ref reachLocalQuarantines);
'@ 'T24 localize Reach parity mismatch'

$core = Replace-OrThrow $core @'
            context.ReachMemo.Clear();
            context.ReachValidatedMatches = 0;
            Interlocked.Increment(ref reachInvalidations);
'@ @'
            context.ReachMemo.Clear();
            context.ReachQuarantined.Clear();
            context.ReachValidatedMatches = 0;
            Interlocked.Increment(ref reachInvalidations);
'@ 'T24 clear local quarantine on live Reach cache invalidation'

$core = Replace-OrThrow $core @'
            internal readonly Dictionary<ReachKey, ReachEntry> ReachMemo =
                new Dictionary<ReachKey, ReachEntry>();
            internal long ReachHitSerial;
'@ @'
            internal readonly Dictionary<ReachKey, ReachEntry> ReachMemo =
                new Dictionary<ReachKey, ReachEntry>();
            internal readonly HashSet<ReachKey> ReachQuarantined = new HashSet<ReachKey>();
            internal long ReachHitSerial;
'@ 'T24 transaction-local Reach quarantine set'

$core = Replace-OrThrow $core @'
                ", mismatches=" + Interlocked.Read(ref reachMismatches) +
                ", runtimeQuarantined=" + reachRuntimeQuarantined +
                ", foreignResultBypass=" + Interlocked.Read(ref reachForeignResultBypass) +
'@ @'
                ", mismatches=" + Interlocked.Read(ref reachMismatches) +
                ", localQuarantines=" + Interlocked.Read(ref reachLocalQuarantines) +
                ", localQuarantineBypass=" + Interlocked.Read(ref reachLocalQuarantineBypass) +
                ", runtimeQuarantined=" + reachRuntimeQuarantined +
                ", foreignResultBypass=" + Interlocked.Read(ref reachForeignResultBypass) +
'@ 'T24 Reach summary counters'

Set-Content $corePath $core -Encoding UTF8

# -----------------------------------------------------------------------------
# T24-B: automatic heavyweight diagnostic bursts OFF in production.
# Classes/reporting remain compiled; they simply never self-request a capture.
# -----------------------------------------------------------------------------
$t15Path = 'RimMT/Source/RimMT/Diagnostics/WorkGiverDeepAttribution093T15.cs'
$t15 = Get-Content $t15Path -Raw
$t15 = Replace-OrThrow $t15 @'
            if (Volatile.Read(ref startAttempts) >= MaxStartAttempts)
                return;
            Interlocked.Exchange(ref requested, 1);
'@ @'
            // T24 production: retain the lightweight trigger counters only. The 438-method
            // detail Harmony burst is diagnostic-only and is no longer auto-requested.
            return;
'@ 'T24 disable automatic T15 detail burst'
$t15 = $t15.Replace('.Append(", requested=").Append(Volatile.Read(ref requested) != 0)',
                    '.Append(", autoCapture=OFF, requested=").Append(Volatile.Read(ref requested) != 0)')
Set-Content $t15Path $t15 -Encoding UTF8

$t18Path = 'RimMT/Source/RimMT/Diagnostics/StorytellerDeepAttribution093T18.cs'
$t18 = Get-Content $t18Path -Raw
$t18 = Replace-OrThrow $t18 @'
        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            Interlocked.Exchange(ref requested, 1);
        }
'@ @'
        internal static void Initialize()
        {
            if (Interlocked.Exchange(ref initialized, 1) != 0) return;
            // T24 production: deep Storyteller Harmony detours are diagnostic-only.
            // Top-level catastrophic timing remains available through the existing T1/T15 timer.
        }
'@ 'T24 disable automatic T18 Storyteller burst'
Set-Content $t18Path $t18 -Encoding UTF8

# -----------------------------------------------------------------------------
# T24-C: targeted-only early S4 admission.
# Generic/unknown WorkGivers remain at the proven 32ms threshold. Only a scanner with an existing
# deterministic cheap-negative prefilter AND an authority-safe HasJobOnThing/JobOnThing chain may
# enter the same nearest-first rescue at 8ms.
# -----------------------------------------------------------------------------
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private const int EarlyKnownHeavyThresholdMs = 8;
        private const long EarlyKnownHeavyMinCalls = 2;
'@ @'
        private const int EarlyKnownHeavyThresholdMs = 8;
        private const int TargetedEarlyThresholdMs = 8;
        private const long EarlyKnownHeavyMinCalls = 2;
'@ 'T24 targeted-early threshold constant'

$s4 = Replace-OrThrow $s4 @'
        private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyKnownHeavyThresholdMs / 1000L);
'@ @'
        private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyKnownHeavyThresholdMs / 1000L);
        private static readonly long TargetedEarlyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TargetedEarlyThresholdMs / 1000L);
'@ 'T24 targeted-early tick budget'

$s4 = Replace-OrThrow $s4 @'
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
'@ @'
        private static long targetedPrefilterAuthorityBypass;
        private static long targetedEarlyChecks;
        private static long targetedEarlyHits;
        private static long targetedEarlyListAdmissions;
        private static long targetedEarlyCustomAdmissions;
        private static long targetedEarlyAuthorityBypass;
        private static long actualValidatorCalls;
'@ 'T24 targeted-early counters'

$s4 = Replace-OrThrow $s4 @'
            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ @'
            if (__7 != null)
            {
                long elapsedScope = Stopwatch.GetTimestamp() - scopeStart;
                if (elapsedScope < TailRescueThresholdTicks)
                {
                    if (elapsedScope < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6)) return true;
                    targetedEarlyCustomAdmissions++;
                }
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ 'T24 targeted-early custom admission'

$s4 = Replace-OrThrow $s4 @'
            if (count < TailMinSourceCount) return true;
            if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;

            tailEligible++;
'@ @'
            if (count < TailMinSourceCount) return true;
            long elapsedSmallList = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsedSmallList < TailRescueThresholdTicks)
            {
                if (elapsedSmallList < TargetedEarlyThresholdTicks || !CanUseTargetedEarly(__6)) return true;
                targetedEarlyListAdmissions++;
            }

            tailEligible++;
'@ 'T24 targeted-early list admission'

$s4 = Replace-OrThrow $s4 @'
        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
'@ @'
        private static bool CanUseTargetedEarly(Predicate<Thing> validator)
        {
            targetedEarlyChecks++;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            if (scanner == null) return false;
            if (ResolveTargetedPrefilter(scanner) == TargetedPrefilterKind.None) return false;
            if (!IsTargetedPrefilterAuthoritySafe(scanner))
            {
                targetedEarlyAuthorityBypass++;
                return false;
            }
            targetedEarlyHits++;
            return true;
        }

        private static TargetedPrefilterKind ResolveTargetedPrefilter(WorkGiver_Scanner scanner)
'@ 'T24 targeted-early authority helper'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", targetedEarly=" + (targetedEarlyListAdmissions + targetedEarlyCustomAdmissions) +
                   " [checks=" + targetedEarlyChecks + ", hits=" + targetedEarlyHits +
                   ", list=" + targetedEarlyListAdmissions + ", custom=" + targetedEarlyCustomAdmissions +
                   ", authorityBypass=" + targetedEarlyAuthorityBypass + "]" +
                   ", failures=" + failures +
'@ 'T24 targeted-early summary'

Set-Content $s4Path $s4 -Encoding UTF8

# -----------------------------------------------------------------------------
# Version/report labels.
# -----------------------------------------------------------------------------
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = [regex]::Replace($boot,
    'internal const string Version = "0\.9\.3-t23-[^"]+";',
    'internal const string Version = "0.9.3-t24-stutter-first";', 1)
$boot = $boot.Replace('[RimMT] V0.9.3-T23 Tail Containment initialized.',
                      '[RimMT] V0.9.3-T24 Stutter First initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T23 Tail Containment', 'V0.9.3-T24 Stutter First')
$report = $report.Replace('T23 adds WorldRoot attribution and frame-budgeted ReachProfile Region.Allows capture with stale/watchdog fail-open containment;',
    'T23 ReachProfile/WorldRoot retained; T24 localizes Reach parity quarantine, disables automatic deep-profiler bursts, and enables authority-safe targeted early S4 admission;')
$report = $report.Replace('S4 early rescue=OFF;', 'S4 generic early rescue=OFF + targeted-safe early rescue=8ms;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T23 Tail Containment', 'V0.9.3-T24 Stutter First')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T24 Stutter First: local Reach quarantine, automatic deep profilers off, authority-safe targeted S4 admission from 8ms; generic S4 remains 32ms.'