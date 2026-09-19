$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T31 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T31 Shared Eligibility Primitive Census
# Measurement-only on top of verified T28:
# - T30 heavy source/validator census is NOT inherited
# - optional Diagnostics assembly gates all new T31 Harmony hooks
# - sample 1/8 outer JobGiver_Work packages
# - generic WorkGiver scope attribution only; no mod/WorkGiver special cases
# - profiles ForbidUtility.IsForbidden, ReservationUtility.CanReserve,
#   ReservationUtility.CanReserveAndReach
# - exact-repeat result flips and candidate-fact mutations are measured
# - no gameplay result is cached or changed

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t28-unified-job-search-transaction";' 'internal const string Version = "0.9.3-t31-shared-eligibility-census";' 'bootstrap version'
$boot=Replace-OrThrow $boot @'
                JobSearchPackageContext093T28.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ @'
                JobSearchPackageContext093T28.Apply(harmony);
                SharedEligibilityPrimitiveCensus093T31.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ 'T31 diagnostics-gated apply'
$boot=$boot.Replace('[RimMT] V0.9.3-T28 Unified Job Search Transaction initialized.',
                    '[RimMT] V0.9.3-T31 Shared Eligibility Primitive Census initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# Piggyback on T28's one package lifecycle. No second TryIssueJobPackage Harmony wrapper.
$ctxPath='RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs'
$ctx=Get-Content $ctxPath -Raw
$ctx=Replace-OrThrow $ctx @'
                current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
                __state.Shared = current;
                Interlocked.Increment(ref packages);
'@ @'
                current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
                __state.Shared = current;
                Interlocked.Increment(ref packages);
                SharedEligibilityPrimitiveCensus093T31.BeginPackage(__0, generation);
'@ 'T31 package begin'
$ctx=Replace-OrThrow $ctx @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                current = null;
                depth = 0;
'@ @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                SharedEligibilityPrimitiveCensus093T31.EndPackage();
                current = null;
                depth = 0;
'@ 'T31 package end'
Set-Content $ctxPath $ctx -Encoding UTF8

# Production on-demand report surfaces T31 beside T28/T21/T22.
$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T31 Shared Eligibility Primitive Census')
$reportAnchor='            sb.AppendLine(JobSearchPackageContext093T28.Summary());'
if(-not $report.Contains($reportAnchor)){ throw 'T31 anchor missing: production T28 summary' }
$report=$report.Replace(
  $reportAnchor,
  $reportAnchor + [Environment]::NewLine +
    '            sb.AppendLine(SharedEligibilityPrimitiveCensus093T31.Summary());')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T31 Shared Eligibility Primitive Census')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T31 for RimWorld 1.5. The validated T28/T21/T22 production paths remain unchanged. When the optional Diagnostics companion is loaded, T31 samples one in eight JobGiver_Work packages and measures generic shared eligibility primitives across WorkGiver scanners: forbidden checks, reservation checks, combined reserve-and-reach checks, exact-repeat timing/result stability, and candidate-fact stability. T31 is measurement-only, contains no mod or WorkGiver special cases, and never caches or changes gameplay results.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

# Diagnostics v0.9 surfaces T31 through the existing reflection bridge.
$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.6.0";' 'internal const string Version = "0.9.0";' 'Diagnostics v0.9 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchTransaction093T20",
'@ @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.SharedEligibilityPrimitiveCensus093T31",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T31 reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.6','RimMT Diagnostics v0.9')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional measurement-only diagnostics companion for RimMT T31. v0.9 activates and surfaces the sampled Shared Eligibility Primitive Census alongside the existing T28 transaction/parity counters and bounded SlowDNJ attribution. Disable this companion when measuring pure production performance.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T31 Shared Eligibility Primitive Census + Diagnostics v0.9.'
