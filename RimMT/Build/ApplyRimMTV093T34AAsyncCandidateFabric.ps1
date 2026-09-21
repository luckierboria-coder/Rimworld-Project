$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T34-A anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t32c1-positive-canreserve-replay";' 'internal const string Version = "0.9.3-t34a-async-candidate-fabric";' 'bootstrap version'

if(-not $boot.Contains('CandidateFabric093T34A.Apply(harmony);')){
  $candidateAnchor='                JobSearchPackageContext093T28.Apply(harmony);'
  if(-not $boot.Contains($candidateAnchor)){ throw 'T34-A anchor missing: candidate fabric bootstrap' }
  $boot=$boot.Replace($candidateAnchor,
    $candidateAnchor + [Environment]::NewLine + '                CandidateFabric093T34A.Apply(harmony);')
}

$boot=[regex]::Replace($boot,'(?m)^\s*DoBillTailFabric092\.Apply\(harmony\);\r?\n','')
$boot=[regex]::Replace($boot,'(?m)^\s*AggressiveReachabilityProfilesV17\.Apply\(harmony\);\r?\n','')
$boot=$boot.Replace('[RimMT] V0.9.3-T32C.1 Positive CanReserve Replay initialized.',
                    '[RimMT] V0.9.3-T34A Async Candidate Fabric initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw

if(-not $runtime.Contains('FeatureGate.Register(CandidateFabric093T34A.FeatureId')){
  $runtime=Replace-OrThrow $runtime @'
            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");
'@ @'
            FeatureGate.Register("parallel.jobPartition", true, "Legacy synchronous candidate/search helpers retained for fallback paths");
            FeatureGate.Register(CandidateFabric093T34A.FeatureId, true, "T34-A no-wait worker-maintained candidate spatial fabric");
'@ 'candidate fabric feature gate'
}

if(-not $runtime.Contains('FeatureGate.SetEnabled(CandidateFabric093T34A.FeatureId, work);')){
  $runtime=Replace-OrThrow $runtime @'
            FeatureGate.SetEnabled("parallel.jobPartition", work);
'@ @'
            FeatureGate.SetEnabled("parallel.jobPartition", work);
            FeatureGate.SetEnabled(CandidateFabric093T34A.FeatureId, work);
'@ 'candidate fabric settings gate'
}

if(-not $runtime.Contains('CandidateFabric093T34A.MarkCompatibilityReady();')){
  $runtime=Replace-OrThrow $runtime @'
                GlobalHaulAccelerator.MarkCompatibilityReady();
'@ @'
                GlobalHaulAccelerator.MarkCompatibilityReady();
                CandidateFabric093T34A.MarkCompatibilityReady();
'@ 'candidate fabric compatibility ready'
}

$runtime=[regex]::Replace($runtime,'(?m)^\s*FeatureGate\.Register\(AggressiveReachabilityProfiles\.FeatureId[^\r\n]*\r?\n','')
$runtime=[regex]::Replace($runtime,'(?m)^\s*FeatureGate\.SetEnabled\(AggressiveReachabilityProfiles\.FeatureId[^\r\n]*\r?\n','')
$runtime=[regex]::Replace($runtime,'(?m)^\s*AggressiveReachabilityProfilesV17\.MarkCompatibilityReady\(\);\r?\n','')
Set-Content $runtimePath $runtime -Encoding UTF8

$fabricPath=Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs'
$fabric=Get-Content $fabricPath -Raw
$fabric=Replace-OrThrow $fabric 'private const string FeatureId = "parallel.jobPartition";' 'private const string FeatureId = CandidateFabric093T34A.FeatureId;' 'fabric feature id'
Set-Content $fabricPath $fabric -Encoding UTF8

$stagePath=Join-Path $root 'RimMT/Source/RimMT/AI/LargeSetTailRescue092.cs'
$stage=Get-Content $stagePath -Raw
$stage=Replace-OrThrow $stage @'
        static LargeSetTailRescue092()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }
'@ @'
        static LargeSetTailRescue092()
        {
            // T34-A: retired from default production after low bounded-proof yield.
            // Kept in source for controlled A/B only; no static self-install.
        }
'@ 'Stage3 static installer'
Set-Content $stagePath $stage -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T32C.1 Positive CanReserve Replay','V0.9.3-T34A Async Candidate Fabric')
$report=$report.Replace(
  'Persistent-fabric GenClosest consumer: RETIRED/OFF in T28 after sustained zero-acceleration evidence; fabric implementation retained in code but no runtime consumer is installed.',
  'T34-A persistent candidate fabric reactivated with ThingRequest-backed sources + uncapped live completion; first/missing/stale snapshots always fall through.')

if(-not $report.Contains('CandidateFabric093T34A.Summary()')){
  $report=Replace-OrThrow $report @'
            sb.AppendLine(PersistentMapSearchFabric.Summary());
'@ @'
            sb.AppendLine(CandidateFabric093T34A.Summary());
            sb.AppendLine(PersistentMapSearchFabric.Summary());
'@ 'production candidate summary'
}

$report=[regex]::Replace($report,'RC2 Stage3 >=128;','RC2 Stage3=OFF(T34-A);')
$report=[regex]::Replace($report,'ReachProfile=[^;]*;','ReachProfile=OFF(T34-A);')
$report=$report.Replace('DoBill=persistent incremental membership + T28 package-local false readiness proof;',
                        'DoBill=persistent incremental membership + T28 package-local false readiness proof; DoBillWorkerTail=OFF(T34-A);')
$report=$report.Replace('            sb.AppendLine(LargeSetTailRescue092.Summary());',
                        '            sb.AppendLine("Stage3 large-set rescue: RETIRED/OFF on T34-A production line.");')
$report=$report.Replace('            sb.AppendLine(DoBillTailFabric092.Summary());',
                        '            sb.AppendLine("DoBill worker-tail fabric: RETIRED/OFF on T34-A production line.");')
$report=$report.Replace('            sb.AppendLine(AggressiveReachabilityProfilesV17.Summary());',
                        '            sb.AppendLine("ReachProfile: RETIRED/OFF on T34-A production line.");')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=[regex]::Replace($a,'<name>.*?</name>','<name>RimMT V0.9.3-T34A Async Candidate Fabric</name>',1)
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T34-A for RimWorld 1.5. First production step of the no-wait async candidate architecture. A worker-maintained persistent spatial fabric now covers both ThingRequest-backed ListerThings sources and supported custom static candidate lists inside JobGiver_Work. First observations and stale/missing publications always fall through; the main thread never waits. Workers never run live validators, Reachability, reservations, Job creation or gameplay commits. Live Reachability and validators remain final authority. T21/T32 package-local replay remains retained; S4/S5.1 remain conservative tail fallbacks. ReachProfile, RC2 Stage3 and DoBill worker-tail are retired on this test line.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.14.0";' 'internal const string Version = "0.17.0";' 'Diagnostics v0.17'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
if(-not $dr.Contains('"RimMT.CandidateFabric093T34A"')){
  $dr=Replace-OrThrow $dr @'
        private static readonly string[] SummaryTypes = new string[]
        {
'@ @'
        private static readonly string[] SummaryTypes = new string[]
        {
            "RimMT.CandidateFabric093T34A",
            "RimMT.PersistentMapSearchFabric",
            "RimMT.GlobalHaulAccelerator",
'@ 'Diagnostics T34-A reflection summaries'
}
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout=Join-Path $root 'RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=[regex]::Replace($a,'<name>.*?</name>','<name>RimMT Diagnostics v0.17 - T34A Candidate Fabric</name>',1)
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T34-A. Adds reflection summaries for the T34-A candidate fabric, persistent map fabric and global haul worker path. It does not include the T33-A/T33-B census experiments. Disable this companion when measuring pure production feel; enable it only for attribution/report capture.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T34-A Async Candidate Fabric + production cleanup + Diagnostics v0.17.'
