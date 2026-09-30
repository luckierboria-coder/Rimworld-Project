param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n"); $old=$old.Replace("`r`n","`n"); $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T34-C.11 anchor missing: $label" }
  return $text.Replace($old,$new)
}

Copy-Item (Join-Path $PSScriptRoot 'Templates/HaulToInventoryParallelEligibility093T34C11.cs.txt') `
  (Join-Path $root 'RimMT/Source/RimMT/AI/HaulToInventoryParallelEligibility093T34C11.cs') -Force
Copy-Item (Join-Path $PSScriptRoot 'Templates/MapPostTickComponentCensus0203.cs.txt') `
  (Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/MapPostTickComponentCensus.cs') -Force

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t34c10-target-count-parallel-fabric' '0.9.3-t34c11-haul-eligibility-parallel-fabric' 'bootstrap version'
$anchor='                TargetCountParallelFabric093T34C10.Apply(harmony);'
$boot=Replace-OrThrow $boot $anchor ($anchor + [Environment]::NewLine +
  '                HaulToInventoryParallelEligibility093T34C11.Apply(harmony);') 'C11 install'
$boot=$boot.Replace('[RimMT] V0.9.3-T34C.10 Target-count Parallel Fabric initialized.',
  '[RimMT] V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility Fabric initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$anchor='            FeatureGate.Register(TargetCountParallelFabric093T34C10.FeatureId, true, "T34-C.10 same-tick primitive target-count product aggregation");'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.Register(HaulToInventoryParallelEligibility093T34C11.FeatureId, true, "T34-C.11 PUAH primitive storage eligibility classification");') 'feature registration'
$anchor='            FeatureGate.SetEnabled(TargetCountParallelFabric093T34C10.FeatureId, work);'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.SetEnabled(HaulToInventoryParallelEligibility093T34C11.FeatureId, work);') 'settings gate'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('[RimMT] V0.9.3-T34C.10 Target-count Parallel Fabric on-demand report',
  '[RimMT] V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility on-demand report')
$report=$report.Replace('V0.9.3-T34C.10 Target-count Parallel Fabric; baseline',
  'V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility; baseline')
$anchor='            sb.AppendLine(TargetCountParallelFabric093T34C10.Summary());'
$report=Replace-OrThrow $report $anchor ($anchor + [Environment]::NewLine +
  '            sb.AppendLine(HaulToInventoryParallelEligibility093T34C11.Summary());') 'production summary'
$report=$report.Replace('T34-C.10 target-count products=ACTIVE(',
  'T34-C.11 PUAH storage eligibility=ACTIVE(bounded primitive capture + worker negative compilation + live authoritative proof + no wait); T34-C.10 target-count products=ACTIVE(')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T34C.11 Haul-to-Inventory Parallel Eligibility</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T34-C.11 for RimWorld 1.5. Adds a targeted Pick Up And Haul storage-eligibility fabric. Small main-thread slices capture primitive candidate IDs, priorities and destination-acceptance bytes; workers compile structural negatives; every rejection is proved again against live storage settings before the expensive original storage-cell search may be skipped. Reservations, forbidden state, reachability, Job creation and final storage selection remain live and Vanilla-authoritative. T34-C.10 target-count aggregation remains active.</description>')
Set-Content $aboutPath $about -Encoding UTF8

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.20.2";' 'internal const string Version = "0.20.3";' 'diagnostics version'
$anchor='                DiagnosticsV03.Apply(harmony);'
$diag=Replace-OrThrow $diag $anchor ($anchor + [Environment]::NewLine +
  '                MapPostTickComponentCensus.Apply(harmony);') 'MapPostTick census install'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$anchor='            sb.Append(DiagnosticsV03.BuildSummary());'
$diagReport=Replace-OrThrow $diagReport $anchor ($anchor + [Environment]::NewLine +
  '            sb.Append(MapPostTickComponentCensus.Summary());') 'MapPostTick census report'
$anchor='            "RimMT.TargetCountParallelFabric093T34C10",'
$diagReport=Replace-OrThrow $diagReport $anchor ('            "RimMT.HaulToInventoryParallelEligibility093T34C11",' + [Environment]::NewLine + $anchor) 'C11 reflection summary'
$anchor='            AuditNamed(sb, typeof(GenClosest), "ClosestThing_Global", "GenClosest.ClosestThing_Global");'
$addition=@'


            Type pua = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
            if (pua != null)
            {
                AuditOne(sb, AccessTools.Method(pua, "PotentialWorkThingsGlobal"), "PickUpAndHaul.WorkGiver_HaulToInventory.PotentialWorkThingsGlobal");
                AuditOne(sb, AccessTools.Method(pua, "HasJobOnThing"), "PickUpAndHaul.WorkGiver_HaulToInventory.HasJobOnThing");
            }
'@
$diagReport=Replace-OrThrow $diagReport $anchor ($anchor + $addition) 'PUAH Harmony audit'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.20.3 - T34C.11 Haul Eligibility</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T34-C.11. Reports Pick Up And Haul capture, worker plans, live-proved rejections and revocations, retains T34-C.10 counters, and adds per-MapComponent plus Staggered Raids MapPostTick postfix timing for multi-second MapPostTick spikes.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

Write-Host 'Applied RimMT T34-C.11 Haul-to-Inventory Parallel Eligibility + Diagnostics v0.20.3.'
