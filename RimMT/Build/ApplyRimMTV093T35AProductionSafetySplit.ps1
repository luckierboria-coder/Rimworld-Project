$ErrorActionPreference='Stop'

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n")
  $old=$old.Replace("`r`n","`n")
  $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T35-A anchor missing: $label" }
  return $text.Replace($old,$new)
}

function Remove-LineToken([string]$text,[string]$token){
  $text=$text.Replace("`r`n","`n")
  $lines=$text.Split("`n")
  $kept=New-Object System.Collections.Generic.List[string]
  foreach($line in $lines){
    if($line.Contains($token)){ continue }
    [void]$kept.Add($line)
  }
  return [string]::Join("`n",$kept)
}

# ---------------------------------------------------------------------------
# Production RimMT: T35-A is functionality-only.
# ---------------------------------------------------------------------------
$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t34c11-haul-eligibility-parallel-fabric' '0.9.3-t35a-production-safety-split' 'bootstrap version'
$boot=Remove-LineToken $boot 'RootFrameStallCensus093T34C8.Apply(harmony);'
$boot=$boot.Replace('[RimMT] V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility Fabric initialized.',
  '[RimMT] V0.9.3-T35-A Production Safety Split initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$runtime=Remove-LineToken $runtime 'FeatureGate.Register(RootFrameStallCensus093T34C8.FeatureId'
$runtime=Remove-LineToken $runtime 'FeatureGate.SetEnabled(RootFrameStallCensus093T34C8.FeatureId'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=Remove-LineToken $report 'RootFrameStallCensus093T34C8.Summary()'
$report=$report.Replace('[RimMT] V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility on-demand report',
  '[RimMT] V0.9.3-T35-A Production Safety Split on-demand report')
$report=$report.Replace('V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility; baseline',
  'V0.9.3-T35-A Production Safety Split; baseline')
$report=$report.Replace(
  'T34-C.8 root-frame stall census=ACTIVE(measurement-only Root_Play.Update envelope + outside-root wall gaps + focus/pause/GC/tick correlation); ',
  '')
Set-Content $reportPath $report -Encoding UTF8

$censusPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RootFrameStallCensus093T34C8.cs'
if(Test-Path $censusPath){ Remove-Item $censusPath -Force }

# T34-B prefetch was high-overhead in the user's production report:
# 58%+ worker plans were discarded after package close. Keep the proven no-wait
# snapshot planner, but prefetch only repeatedly-observed hot sources.
$scannerPath=Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs'
$scanner=Get-Content $scannerPath -Raw
$scanner=$scanner.Replace("`r`n","`n")
$scanner=Replace-OrThrow $scanner '        private const int PrefetchSourcesPerPackage = 12;' '        private const int PrefetchSourcesPerPackage = 4;' 'scanner prefetch width'
$scanner=Replace-OrThrow $scanner '        private const int MaxPackagePlans = 48;' '        private const int MaxPackagePlans = 16;' 'scanner package plan cap'
$scanner=Replace-OrThrow $scanner '        private const int MaxRootPlansPerHotSource = 8;' @'
        private const int MaxRootPlansPerHotSource = 8;
        private const int MinPrefetchHotScore = 3;
'@ 'scanner hot threshold'
$old=@'
                int take = Math.Min(PrefetchSourcesPerPackage, ordered.Count);
                for (int i = 0; i < take; i++)
                    selected.Add(ordered[i]);
'@
$new=@'
                int take = Math.Min(PrefetchSourcesPerPackage, ordered.Count);
                for (int i = 0; i < take; i++)
                {
                    HotSource candidate = ordered[i];
                    if (candidate == null || candidate.Score < MinPrefetchHotScore)
                        continue;
                    selected.Add(candidate);
                }
'@
$scanner=Replace-OrThrow $scanner $old $new 'scanner score-gated prefetch'
Set-Content $scannerPath $scanner -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T35A Production Safety Split</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T35-A for RimWorld 1.5. Production-only build. Retires the unsafe T34-D aggressive worker-validator route, keeps arbitrary WorkGiver validators on the main thread, preserves strict no-wait snapshot/primitive worker preprocessing, moves Root_Play stall measurement to the optional Diagnostics companion, and reduces speculative T34-B scanner prefetch after production telemetry showed high closed-package discard. Final Reachability, reservation, live validators and Job creation remain main-thread authoritative.</description>')
Set-Content $aboutPath $about -Encoding UTF8

# ---------------------------------------------------------------------------
# Diagnostics companion: owns measurement / attribution / regression checks.
# ---------------------------------------------------------------------------
$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=$diag.Replace("`r`n","`n")
$diag=[regex]::Replace($diag,'internal const string Version = "0\.20\.[0-9]+";','internal const string Version = "0.21.0";',1)
$diag=Remove-LineToken $diag 'MapPostTickComponentCensus.Apply(harmony);'
if(-not $diag.Contains('RootFrameStallCensusT35A.Apply(harmony);')){
  $diag=Replace-OrThrow $diag '                DiagnosticsV03.Apply(harmony);' @'
                DiagnosticsV03.Apply(harmony);
                RootFrameStallCensusT35A.Apply(harmony);
'@ 'diagnostics root-frame install'
}
Set-Content $diagPatchPath $diag -Encoding UTF8

# Preserve the safe-startup fix from C11 commit 38806e:
# do not patch every loaded WorkGiver method at diagnostics bootstrap.
$diagV02Path=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs'
$diagV02=Get-Content $diagV02Path -Raw
$diagV02=Remove-LineToken $diagV02 'PatchWorkGiverMethods(harmony);'
$diagV02=$diagV02.Replace('            AppendTop(sb, "WorkGiverSampled", WorkGivers);',
  '            sb.AppendLine("WorkGiverSampled=RETIRED; SlowDNJCorrelation is the WorkGiver timing path.");')
Set-Content $diagV02Path $diagV02 -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$diagReport=$diagReport.Replace("`r`n","`n")
if(-not $diagReport.Contains('RootFrameStallCensusT35A.Summary()')){
  $diagReport=Replace-OrThrow $diagReport '            sb.Append(MapPostTickComponentCensus.Summary());' @'
            sb.Append(MapPostTickComponentCensus.Summary());
            sb.Append(RootFrameStallCensusT35A.Summary());
            sb.Append(AggressiveScannerRegressionProbeT35A.Summary());
'@ 'diagnostics T35 summaries'
}
$diagReport=Remove-LineToken $diagReport '"RimMT.RootFrameStallCensus093T34C8",'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.21.0 - T35A Safety Diagnostics</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T35-A. Owns Root_Play/outside-root stall measurement and the aggressiveScanner regression probe, retains production-summary reflection, job-search attribution and Harmony audit, and does not enable the retired T34-D worker-validator route. Disable this companion for normal production performance testing.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

# ---------------------------------------------------------------------------
# Hard regression guards: the T34-D aggressive worker-validator route must never
# re-enter production. The user's log captured Reachability.CanReach from a T34-D
# worker validator, which could abort a JobGiver search before Vanilla fallback.
# ---------------------------------------------------------------------------
$mainRoot=Join-Path $root 'RimMT/Source/RimMT'
$forbiddenProduction=@(
  'parallel.aggressiveScanner',
  'AggressiveParallelScanner093T34D'
)
foreach($token in $forbiddenProduction){
  $hit=Get-ChildItem $mainRoot -Recurse -Filter '*.cs' -File |
    Select-String -SimpleMatch $token | Select-Object -First 1
  if($hit){ throw "T35-A retired aggressiveScanner route leaked into production: $token at $($hit.Path):$($hit.LineNumber)" }
}

if(Test-Path $censusPath){ throw 'T35-A measurement-only RootFrame census still exists in production source' }
if((Get-Content $bootPath -Raw).Contains('RootFrameStallCensus093T34C8')){
  throw 'T35-A production bootstrap still references RootFrameStallCensus093T34C8'
}
if((Get-Content $runtimePath -Raw).Contains('RootFrameStallCensus093T34C8')){
  throw 'T35-A production runtime still registers RootFrameStallCensus093T34C8'
}

# Scanner worker body remains snapshot-only and strictly no-wait.
$scanner=Get-Content $scannerPath -Raw
$buildStart=$scanner.IndexOf('        private static void BuildPlan(')
$buildEnd=$scanner.IndexOf('        private static long MakePlanKey',$buildStart)
if($buildStart -lt 0 -or $buildEnd -le $buildStart){ throw 'T35-A cannot isolate scanner worker body' }
$worker=$scanner.Substring($buildStart,$buildEnd-$buildStart)
foreach($token in @(
  'validator(',
  'CanReach',
  'Reachability',
  'CanReserve',
  'ReservationManager',
  'MapPawns',
  '.Wait(',
  '.Join(',
  'SpinWait',
  'Thread.Sleep('
)){
  if($worker.Contains($token)){ throw "T35-A scanner worker safety violation: $token" }
}

Write-Host 'Applied RimMT T35-A Production Safety Split + Diagnostics v0.21.0.'
