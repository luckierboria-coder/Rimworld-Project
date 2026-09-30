$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.12 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.12 Predictive Admission
# Runtime evidence from V0.9.11:
# - 5/7 >20ms JobGiver tails and both >50ms tails occurred before recurring-hot admission
# - exactClosure contributed 0 >20ms tails; already-sliced segments contributed 0 >10ms tails
# Therefore this release changes admission only:
# 1) exact/safe/proven ListerThings scanners with >=256 candidates may slice on first encounter;
# 2) history admission advances from one >20ms sample to one >10ms sample (or two >5ms samples);
# 3) continuation ownership, source reconciliation, budgets, authority, exactClosure coverage and
#    ReachProfile V0.4.18 remain unchanged.

$resumePath = 'RimMT/Source/RimMT/AI/ResumableJobGiver095.cs'
$resume = Get-Content $resumePath -Raw

$resume = Replace-OrThrow $resume @'
        private const int MinSourceCount = 16;
        private const int MaxSourceCount = 8192;
'@ @'
        private const int MinSourceCount = 16;
        private const int PredictiveSourceCount = 256;
        private const int MaxSourceCount = 8192;
'@ 'predictive large-source threshold'

$resume = Replace-OrThrow $resume @'
        private static long hotAdmissions;
        private static long statesCreated;
'@ @'
        private static long hotAdmissions;
        private static long predictiveSourceProbes;
        private static long predictiveLargeAdmissions;
        private static long predictiveSmallBypass;
        private static long historyAdmissions;
        private static long continuationAdmissions;
        private static long statesCreated;
'@ 'predictive admission counters'

$oldAdmission = @'
            if (!hasPendingForScanner && !JobGiverTailTelemetry094.IsRecurringHot(scanner))
            {
                tailPathClass = TailClassPreAdmission;
                return true;
            }

            hotAdmissions++;
            if (!IsAuthoritySafe(__originalMethod))
            {
                authorityBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
            }

            IList<Thing> source;
            if (!TryGetStableSource(__1, __2, __7, out source))
            {
                if (__7 != null) customEnumerableBypass++;
                else unstableSourceBypass++;
                shapeBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
            }

            int count;
            try { count = source.Count; }
            catch
            {
                sourceInvalidations++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
            }
'@
$newAdmission = @'
            bool recurringHot = hasPendingForScanner || JobGiverTailTelemetry094.IsRecurringHot(scanner);
            bool predictiveAdmission = false;
            IList<Thing> source = null;
            int count = -1;

            // V0.9.11 showed that first-hit/pre-admission calls owned most visible long tails. For a
            // non-hot scanner, probe only the proven Vanilla ListerThings shape. This is just an IList
            // lookup + Count on the main thread; unknown/custom enumerable semantics remain untouched.
            if (!recurringHot)
            {
                if (__7 != null || __2.IsUndefined)
                {
                    tailPathClass = TailClassPreAdmission;
                    return true;
                }

                predictiveSourceProbes++;
                if (!TryGetStableSource(__1, __2, __7, out source))
                {
                    tailPathClass = TailClassPreAdmission;
                    return true;
                }

                try { count = source.Count; }
                catch
                {
                    sourceInvalidations++;
                    tailPathClass = TailClassSupportedUnsliced;
                    return true;
                }

                if (count < PredictiveSourceCount || count > MaxSourceCount)
                {
                    predictiveSmallBypass++;
                    tailPathClass = TailClassPreAdmission;
                    return true;
                }

                predictiveAdmission = true;
            }

            hotAdmissions++;
            if (hasPendingForScanner) continuationAdmissions++;
            else if (predictiveAdmission) predictiveLargeAdmissions++;
            else historyAdmissions++;

            if (!IsAuthoritySafe(__originalMethod))
            {
                authorityBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                return true;
            }

            if (source == null && !TryGetStableSource(__1, __2, __7, out source))
            {
                if (__7 != null) customEnumerableBypass++;
                else unstableSourceBypass++;
                shapeBypass++;
                tailPathClass = TailClassSupportedUnsliced;
                if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, true);
                return true;
            }

            if (count < 0)
            {
                try { count = source.Count; }
                catch
                {
                    sourceInvalidations++;
                    tailPathClass = TailClassSupportedUnsliced;
                    if (hasPendingForScanner) DropPendingContinuation(pawn, scanner, false, false);
                    return true;
                }
            }
'@
$resume = Replace-OrThrow $resume $oldAdmission $newAdmission 'predictive first-hit admission path'

$resume = Replace-OrThrow $resume @'
                   ", hotAdmissions=" + hotAdmissions +
                   ", activeStates=" + States.Count +
'@ @'
                   ", hotAdmissions=" + hotAdmissions +
                   ", predictiveSourceProbes=" + predictiveSourceProbes +
                   ", predictiveLargeAdmissions=" + predictiveLargeAdmissions +
                   ", predictiveSmallBypass=" + predictiveSmallBypass +
                   ", historyAdmissions=" + historyAdmissions +
                   ", continuationAdmissions=" + continuationAdmissions +
                   ", activeStates=" + States.Count +
'@ 'predictive admission summary'

$resume = $resume.Replace('V0.9.11 recurring-hot JobGiver slow-tail-attributed continuation slicer.', 'V0.9.12 predictive-admission JobGiver continuation slicer.')
$resume = $resume.Replace('[RimMT] V0.9.11 Slow Tail Attribution installed on ', '[RimMT] V0.9.12 Predictive Admission installed on ')
$resume = $resume.Replace('[RimMT] V0.9.11 Slow Tail Attribution failed closed: ', '[RimMT] V0.9.12 Predictive Admission failed closed: ')
$resume = $resume.Replace('[RimMT] V0.9.11 resumable slice failed closed to Vanilla: ', '[RimMT] V0.9.12 resumable slice failed closed to Vanilla: ')
$resume = $resume.Replace('Resumable JobGiver V0.9.11:', 'Resumable JobGiver V0.9.12:')
Set-Content $resumePath $resume -Encoding UTF8

# Advance the history signal by one severity tier. A single >10ms sample is enough to flag a
# scanner as risky on later calls; isolated 5-10ms samples still require repetition.
$tailPath = 'RimMT/Source/RimMT/Diagnostics/JobGiverTailTelemetry094.cs'
$tail = Get-Content $tailPath -Raw
$tail = Replace-OrThrow $tail @'
            // One genuine >20ms call is sufficient evidence that this scanner can create a visible
            // tail. Otherwise require two >5ms calls so isolated medium spikes do not opt a scanner
            // into the resumable path permanently.
            return stat.Over20 > 0 || stat.Over5 >= 2;
'@ @'
            // V0.9.11 showed that waiting for the first >20ms call leaves the most visible first-hit
            // tails unprotected. One genuine >10ms call now promotes subsequent calls; isolated
            // 5-10ms samples still require repetition before permanent history admission.
            return stat.Over10 > 0 || stat.Over5 >= 2;
'@ 'history admission 20ms to 10ms'
$tail = $tail.Replace('JobGiver tail buckets V0.9.11:', 'JobGiver tail buckets V0.9.12:')
Set-Content $tailPath $tail -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = $boot.Replace('0.9.11-slow-tail-attribution','0.9.12-predictive-admission')
$boot = $boot.Replace('V0.9.11 Slow Tail Attribution initialized','V0.9.12 Predictive Admission initialized')
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = $diag.Replace('V0.9.11 Slow Tail Attribution on-demand report','V0.9.12 Predictive Admission on-demand report')
$diag = $diag.Replace('V0.9.11 Slow Tail Attribution;', 'V0.9.12 Predictive Admission; predictive first-hit >=256=ON; history admission=>10ms once or >5ms twice;')
Set-Content $diagPath $diag -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
if (Test-Path $settingsPath) {
    $settings = Get-Content $settingsPath -Raw
    $settings = $settings.Replace('V0.9.11 Slow Tail Attribution','V0.9.12 Predictive Admission')
    Set-Content $settingsPath $settings -Encoding UTF8
}

$aboutPath = 'RimMT/About/About.xml'
$about = Get-Content $aboutPath -Raw
$about = $about.Replace('RimMT V0.9.11 Slow Tail Attribution','RimMT V0.9.12 Predictive Admission')
$about = [regex]::Replace($about, '<description>.*?</description>', '<description>RimMT V0.9.12 Predictive Admission for RimWorld 1.5. Keeps the V0.9.10/V0.9.11 validated continuation, source reconciliation and tail-attribution architecture, but targets the remaining first-hit JobGiver spikes. Exact authority-safe non-prioritized ListerThings scanners with at least 256 candidates may enter bounded main-thread slicing on their first encounter instead of paying one long Vanilla scan first. History admission is also advanced to one observed &gt;10ms call or two &gt;5ms calls. Unknown/custom sources, exactClosure coverage, root-change invalidation, final Vanilla validator/reachability authority, ReachProfile V0.4.18 and other mature production paths remain unchanged.</description>', [System.Text.RegularExpressions.RegexOptions]::Singleline)
Set-Content $aboutPath $about -Encoding UTF8

Write-Host 'Applied RimMT V0.9.12 Predictive Admission: safe ListerThings sources >=256 may slice on first hit; history promotion advances to >10ms once or >5ms twice; continuation semantics and ReachProfile remain unchanged.'
