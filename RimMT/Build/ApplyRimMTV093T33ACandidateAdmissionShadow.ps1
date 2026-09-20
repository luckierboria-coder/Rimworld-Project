$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T33-A anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T33-A Candidate Admission / Rejection Fabric Shadow
# Applied on top of verified T32-C.1.
#
# This phase is deliberately measurement-only. It asks a new question that T21 package-local
# memoization cannot answer: when the same pawn + validator method + scanner + Thing reappears
# in a later JobGiver_Work package, does the previous negative remain negative under a
# conservative mutable-state envelope?
#
# No authoritative cross-package replay is enabled here.
# - no __result write
# - no skip-original
# - no Job/reservation/priority/reachability/candidate-order mutation
# - first false store captures cheap state only
# - first cross-package stable repeat lazily primes IsForbidden
# - later stable repeats are fully live shadow candidates
# - false=>false is a match; false=>true is a mismatch
# - bounded table; capacity clears fail open

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32c1-positive-canreserve-replay";' 'internal const string Version = "0.9.3-t33a-candidate-admission-shadow";' 'bootstrap version'
$boot=Replace-OrThrow $boot @'
                JobSearchPackageContext093T28.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ @'
                JobSearchPackageContext093T28.Apply(harmony);
                ReservationTransaction093T32A.Apply(harmony);
                CandidateAdmissionFabric093T33A.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
'@ 'T33-A apply'
$boot=$boot.Replace('[RimMT] V0.9.3-T32C.1 Positive CanReserve Replay initialized.',
                    '[RimMT] V0.9.3-T33A Candidate Admission Shadow initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32C.1 Positive CanReserve Replay','V0.9.3-T33A Candidate Admission Shadow')
$anchor='            sb.AppendLine(ReservationTransaction093T32A.Summary());'
if(-not $report.Contains($anchor)){ throw 'T33-A anchor missing: reservation summary' }
$report=$report.Replace($anchor,
  $anchor + [Environment]::NewLine + '            sb.AppendLine(CandidateAdmissionFabric093T33A.Summary());')
$report=$report.Replace(
  'no Job/JobOnThing/reservation/priority/cross-package result is cached;',
  'no Job/JobOnThing/reservation/priority authoritative cross-package result is cached; T33-A only shadows exact cross-package negative validator reuse;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T32C.1 Positive CanReserve Replay','V0.9.3-T33A Candidate Admission Shadow')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T33-A for RimWorld 1.5. T32-C.1 positive/negative CanReserve replay and T32-B.1/T21 remain unchanged. T33-A begins the Candidate Admission/Rejection Fabric with a bounded measurement-only cross-package shadow for exact negative JobGiver_Work validator keys: pawn + validator method + scanner + Thing. First live false stores cheap state only; the first later stable repeat lazily primes Forbidden state; later exact repeats with stable pawn/Thing fingerprints and Forbidden state still run fully live and are compared. T33-A never writes the validator result, never skips original, never creates a Job or reservation, and never changes candidate order. The data decides whether a future T33-B may safely reject recurring candidates before expensive HasJobOnThing.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.14.0";' 'internal const string Version = "0.15.0";' 'Diagnostics v0.15 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
            "RimMT.ReservationTransaction093T32A",
            "RimMT.JobSearchTransaction093T20",
'@ @'
            "RimMT.ReservationTransaction093T32A",
            "RimMT.CandidateAdmissionFabric093T33A",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T33-A reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.14','RimMT Diagnostics v0.15')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T33-A. v0.15 surfaces the Candidate Admission Shadow cross-package repeat density, conservative fingerprint stability, lazy Forbidden priming, replayable shadow candidates, false-to-true mismatches, age distribution and top scanner concentration alongside retained T32-C.1/T21 counters. T33-A remains measurement-only.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T33-A Candidate Admission Shadow + Diagnostics v0.15.'
