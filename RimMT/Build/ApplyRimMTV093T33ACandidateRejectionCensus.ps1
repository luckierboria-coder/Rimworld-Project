$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T33-A anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$diagPatch='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$d=Get-Content $diagPatch -Raw
$d=Replace-OrThrow $d 'internal const string Version = "0.14.0";' 'internal const string Version = "0.15.0";' 'Diagnostics version'
$d=Replace-OrThrow $d @'
                DiagnosticsV02.Apply(harmony);
                DiagnosticsV03.Apply(harmony);
'@ @'
                DiagnosticsV02.Apply(harmony);
                DiagnosticsV03.Apply(harmony);
                CandidateRejectionCensusT33A.Apply(harmony);
'@ 'T33-A apply'
Set-Content $diagPatch $d -Encoding UTF8

$reportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$r=Get-Content $reportPath -Raw
$r=Replace-OrThrow $r @'
            sb.Append(DiagnosticsV02.BuildSummary());
            sb.Append(DiagnosticsV03.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ @'
            sb.Append(DiagnosticsV02.BuildSummary());
            sb.Append(DiagnosticsV03.BuildSummary());
            sb.Append(CandidateRejectionCensusT33A.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ 'T33-A report summary'
Set-Content $reportPath $r -Encoding UTF8

$about='RimMTDiagnostics/About/About.xml'
if(Test-Path $about){
  $a=Get-Content $about -Raw
  $a=$a.Replace('RimMT Diagnostics v0.14','RimMT Diagnostics v0.15')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-C.1 / T33-A. v0.15 adds a measurement-only Candidate Rejection Signature Census. It samples 1/8 live JobGiver_Work Validator(Thing) calls and correlates final accept/reject with shared primitive evidence: candidate Forbidden, exact-candidate CanReserve=false and exact-candidate CanReach=false. Accept-side negative counts are recorded explicitly to disqualify unsafe generic early-reject rules. No validator result is changed; production T32-C.1 behavior is unchanged.</description>')
  Set-Content $about $a -Encoding UTF8
}

Write-Host 'Applied T33-A Candidate Rejection Signature Census + Diagnostics v0.15.'
