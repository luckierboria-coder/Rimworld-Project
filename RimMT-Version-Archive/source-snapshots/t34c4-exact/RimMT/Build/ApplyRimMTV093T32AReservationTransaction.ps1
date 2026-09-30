$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T32-A anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T32-A Reservation Transaction
# Starts from verified T28 production baseline.
# - does NOT inherit T29/T30/T31 experiment code
# - package-local exact false-only ReservationManager.CanReserve memo
# - target fingerprint + ReservationManager mutation invalidation
# - warmup + sampled live parity; mismatch quarantines runtime
# - no positive replay, no reservation mutation, no Job/state commit

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t28-unified-job-search-transaction";' 'internal const string Version = "0.9.3-t32a-reservation-transaction";' 'bootstrap version'
$boot=Replace-OrThrow $boot @'
                JobSearchPackageContext093T28.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ @'
                JobSearchPackageContext093T28.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ 'T32-A apply'
$boot=$boot.Replace('[RimMT] V0.9.3-T28 Unified Job Search Transaction initialized.',
                    '[RimMT] V0.9.3-T32A Reservation Transaction initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# Piggyback on T28's single package lifecycle.
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
                ReservationTransaction093T32A.BeginPackage(__0, generation);
'@ 'T32-A package begin'
$ctx=Replace-OrThrow $ctx @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                current = null;
                depth = 0;
'@ @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                ReservationTransaction093T32A.EndPackage();
                current = null;
                depth = 0;
'@ 'T32-A package end'
Set-Content $ctxPath $ctx -Encoding UTF8

# Production report.
$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T32A Reservation Transaction')
$anchor='            sb.AppendLine(JobSearchPackageContext093T28.Summary());'
if(-not $report.Contains($anchor)){ throw 'T32-A anchor missing: T28 production summary' }
$report=$report.Replace($anchor,
  $anchor + [Environment]::NewLine + '            sb.AppendLine(ReservationTransaction093T32A.Summary());')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T32A Reservation Transaction')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T32-A for RimWorld 1.5. The verified T28/T21/T22 production paths remain intact. T32-A adds a bounded package-local false-only memo for exact ReservationManager.CanReserve queries by the current JobGiver_Work pawn. First observations stay live; repeated negatives use warmup and sampled parity before authoritative replay. Any ReservationManager reserve/release mutation clears the package memo; target mutation bypasses the entry; mismatches quarantine the runtime. No positive reservation result, Job, priority, reachability or cross-package result is cached.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

# Diagnostics v0.10 surfaces T32-A; no T31 primitive profiler is present.
$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.6.0";' 'internal const string Version = "0.10.0";' 'Diagnostics v0.10 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchTransaction093T20",
'@ @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.ReservationTransaction093T32A",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T32-A reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.6','RimMT Diagnostics v0.10')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-A. v0.10 surfaces Reservation Transaction parity, authority, mutation-invalidation and replay counters alongside existing bounded tail diagnostics. T30/T31 experimental profilers are not included.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T32-A Reservation Transaction + Diagnostics v0.10.'
