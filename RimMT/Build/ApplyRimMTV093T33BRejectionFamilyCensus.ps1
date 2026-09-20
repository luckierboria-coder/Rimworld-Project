$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T33-B anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$diagPatch='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$d=Get-Content $diagPatch -Raw
$d=Replace-OrThrow $d 'internal const string Version = "0.15.0";' 'internal const string Version = "0.16.0";' 'Diagnostics version'
$d=Replace-OrThrow $d @'
                CandidateRejectionCensusT33A.Apply(harmony);
'@ @'
                CandidateRejectionCensusT33A.Apply(harmony);
                CandidateRejectionFamilyCensusT33B.Apply(harmony);
'@ 'T33-B apply'
Set-Content $diagPatch $d -Encoding UTF8

$reportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$r=Get-Content $reportPath -Raw
$r=Replace-OrThrow $r @'
            sb.Append(CandidateRejectionCensusT33A.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ @'
            sb.Append(CandidateRejectionCensusT33A.BuildSummary());
            sb.Append(CandidateRejectionFamilyCensusT33B.BuildSummary());
            sb.AppendLine("------------------------------------------------------------");
'@ 'T33-B report summary'
Set-Content $reportPath $r -Encoding UTF8

$about='RimMTDiagnostics/About/About.xml'
if(Test-Path $about){
  $a=Get-Content $about -Raw
  $a=$a.Replace('RimMT Diagnostics v0.15','RimMT Diagnostics v0.16')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T32-C.1 / T33. v0.16 retains the T33-A 1/8 live validator rejection-signature census and adds T33-B Rejection Family Census. T33-B samples 1/64 live WorkGiver_Scanner.HasJobOnThing calls, performs one bounded direct-IL dependency scan per scanner method, and weights shared Verse/RimWorld/mod method-call and field-read families by actual HasJobOnThing reject/accept outcomes. Structural presence is discovery evidence only, not execution proof; T33-B invokes no discovered member and changes no gameplay result. Production T32-C.1 remains unchanged.</description>')
  Set-Content $about $a -Encoding UTF8
}

Write-Host 'Applied T33-B Rejection Family Census + Diagnostics v0.16.'
