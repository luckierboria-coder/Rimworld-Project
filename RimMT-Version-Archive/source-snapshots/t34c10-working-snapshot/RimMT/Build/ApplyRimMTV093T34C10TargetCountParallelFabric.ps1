param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n"); $old=$old.Replace("`r`n","`n"); $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T34-C.10 anchor missing: $label" }
  return $text.Replace($old,$new)
}

Copy-Item (Join-Path $PSScriptRoot 'Templates/TargetCountParallelFabric093T34C10.cs.txt') `
  (Join-Path $root 'RimMT/Source/RimMT/AI/TargetCountParallelFabric093T34C10.cs') -Force

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t34c9-parallel-dobill-readiness' '0.9.3-t34c10-target-count-parallel-fabric' 'bootstrap version'
$anchor='                DoBillParallelReadinessFabric093T34C9.Apply();'
$boot=Replace-OrThrow $boot $anchor ($anchor + [Environment]::NewLine +
  '                TargetCountParallelFabric093T34C10.Apply(harmony);') 'target-count install'
$boot=$boot.Replace('[RimMT] V0.9.3-T34C.9 Parallel DoBill Readiness Fabric initialized.',
  '[RimMT] V0.9.3-T34C.10 Target-count Parallel Fabric initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$anchor='            FeatureGate.Register(DoBillParallelReadinessFabric093T34C9.FeatureId, true, "T34-C.9 primitive-only parallel DoBill readiness classification");'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.Register(TargetCountParallelFabric093T34C10.FeatureId, true, "T34-C.10 same-tick primitive target-count product aggregation");') 'feature registration'
$anchor='            FeatureGate.SetEnabled(DoBillParallelReadinessFabric093T34C9.FeatureId, work);'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.SetEnabled(TargetCountParallelFabric093T34C10.FeatureId, work);') 'settings gate'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T34C.9 Parallel DoBill Readiness Fabric','V0.9.3-T34C.10 Target-count Parallel Fabric')
$anchor='            sb.AppendLine(DoBillParallelReadinessFabric093T34C9.Summary());'
$report=Replace-OrThrow $report $anchor ($anchor + [Environment]::NewLine +
  '            sb.AppendLine(TargetCountParallelFabric093T34C10.Summary());') 'production summary'
$report=$report.Replace(
  'T34-C.9 DoBill readiness=ACTIVE(primitive-only worker classification of exact simple Bill_Production stacks + live main-thread field authority + target-count/subclass/foreign-patch Vanilla fallback);',
  'T34-C.10 target-count products=ACTIVE(same-tick primitive visibility aggregation + membership/fog generation gate + live carried count + custom/complex Vanilla fallback); T34-C.9 DoBill readiness=ACTIVE(primitive-only worker classification of exact simple Bill_Production stacks + live main-thread field authority + target-count/subclass/foreign-patch Vanilla fallback);')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T34C.10 Target-count Parallel Fabric</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T34-C.10 for RimWorld 1.5. Adds same-tick no-wait worker aggregation for supported default TargetCount product scans. The main thread captures primitive visibility facts; workers count them; publication is accepted only while the game tick and map membership/fog generation remain unchanged. Carried products are counted live. Custom counters, minified products, complex filters, stale plans and foreign Harmony patches fall through to Vanilla.</description>')
Set-Content $aboutPath $about -Encoding UTF8

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.20.1";' 'internal const string Version = "0.20.2";' 'diagnostics version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$anchor='            "RimMT.DoBillParallelReadinessFabric093T34C9",'
$diagReport=Replace-OrThrow $diagReport $anchor ('            "RimMT.TargetCountParallelFabric093T34C10",' + [Environment]::NewLine + $anchor) 'reflection summary'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.20.2 - T34C.10 Target-count Parallel Fabric</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T34-C.10. Reports eligible product counts, same-tick worker plan hits, stale tick/generation rejection, captured candidates, membership/fog changes and worker failures.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

Write-Host 'Applied RimMT T34-C.10 Target-count Parallel Fabric + Diagnostics v0.20.2.'
