param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n")
  $old=$old.Replace("`r`n","`n")
  $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T34-C.9 anchor missing: $label" }
  return $text.Replace($old,$new)
}

Copy-Item (Join-Path $PSScriptRoot 'Templates/DoBillParallelReadinessFabric093T34C9.cs.txt') `
  (Join-Path $root 'RimMT/Source/RimMT/AI/DoBillParallelReadinessFabric093T34C9.cs') -Force

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=$boot.Replace('0.9.3-t34c8-root-frame-stall-census','0.9.3-t34c9-parallel-dobill-readiness')
if(-not $boot.Contains('0.9.3-t34c9-parallel-dobill-readiness')){ throw 'T34-C.9 bootstrap version anchor missing' }
$anchor='                ScannerParallelFabric093T34B.Apply(harmony);'
if(-not $boot.Contains('DoBillParallelReadinessFabric093T34C9.Apply();')){
  $boot=Replace-OrThrow $boot $anchor ($anchor + [Environment]::NewLine +
    '                DoBillParallelReadinessFabric093T34C9.Apply();') 'fabric authority audit'
}
$boot=$boot.Replace('[RimMT] V0.9.3-T34B Scanner Parallel Fabric initialized.',
  '[RimMT] V0.9.3-T34C.9 Parallel DoBill Readiness Fabric initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$anchor='            FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId, true, "T34-C primitive-only parallel candidate classification");'
if(-not $runtime.Contains('FeatureGate.Register(DoBillParallelReadinessFabric093T34C9.FeatureId')){
  $runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
    '            FeatureGate.Register(DoBillParallelReadinessFabric093T34C9.FeatureId, true, "T34-C.9 primitive-only parallel DoBill readiness classification");') 'feature registration'
}
$anchor='            FeatureGate.SetEnabled(CandidateClassificationFabric093T34C.FeatureId, work);'
if(-not $runtime.Contains('FeatureGate.SetEnabled(DoBillParallelReadinessFabric093T34C9.FeatureId')){
  $runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
    '            FeatureGate.SetEnabled(DoBillParallelReadinessFabric093T34C9.FeatureId, work);') 'settings gate'
}
Set-Content $runtimePath $runtime -Encoding UTF8

$billPath=Join-Path $root 'RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=$bill.Replace("`r`n","`n")
$pattern='(?m)^(\s*)int localInactive = 0;\n\1List<Thing> active = null;'
$matches=([regex]::Matches($bill,$pattern)).Count
if($matches -ne 2){ throw "T34-C.9 expected 2 readiness loop anchors, found $matches" }
$bill=[regex]::Replace($bill,$pattern,{
  param($m)
  $i=$m.Groups[1].Value
  return $i+'int localInactive = 0;'+"`n"+
    $i+'DoBillParallelReadinessFabric093T34C9.ReadinessPlan readinessPlan;'+"`n"+
    $i+'bool hasReadinessPlan = DoBillParallelReadinessFabric093T34C9.TryGetOrSchedule(things, out readinessPlan);'+"`n"+
    $i+'List<Thing> active = null;'
})
$pattern='(?m)^(\s*)bool keep = stack == null \|\| PackageReadinessShouldDoNow\(stack\);'
$matches=([regex]::Matches($bill,$pattern)).Count
if($matches -ne 2){ throw "T34-C.9 expected 2 live readiness anchors, found $matches" }
$bill=[regex]::Replace($bill,$pattern,{
  param($m)
  $i=$m.Groups[1].Value
  return $i+'bool keep;'+"`n"+
    $i+'bool planned;'+"`n"+
    $i+'if (stack == null) keep = true;'+"`n"+
    $i+'else if (hasReadinessPlan && DoBillParallelReadinessFabric093T34C9.TryEvaluate('+"`n"+
    $i+'    things, readinessPlan, i, thing, stack, out planned)) keep = planned;'+"`n"+
    $i+'else keep = PackageReadinessShouldDoNow(stack);'
})
Set-Content $billPath $bill -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T34C.8 Root-Frame Stall Census','V0.9.3-T34C.9 Parallel DoBill Readiness Fabric')
$anchor='            sb.AppendLine(CandidateClassificationFabric093T34C.Summary());'
$report=Replace-OrThrow $report $anchor ($anchor + [Environment]::NewLine +
  '            sb.AppendLine(DoBillParallelReadinessFabric093T34C9.Summary());') 'production summary'
$report=$report.Replace(
  'T34-C.8 root-frame stall census=ACTIVE(measurement-only Root_Play.Update envelope + outside-root wall gaps + focus/pause/GC/tick correlation);',
  'T34-C.9 DoBill readiness=ACTIVE(primitive-only worker classification of exact simple Bill_Production stacks + live main-thread field authority + target-count/subclass/foreign-patch Vanilla fallback); T34-C.8 root-frame stall census=ACTIVE(measurement-only Root_Play.Update envelope + outside-root wall gaps + focus/pause/GC/tick correlation);')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T34C.9 Parallel DoBill Readiness Fabric</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T34-C.9 for RimWorld 1.5. Moves reusable DoBill bill-mode classification to the bounded worker scheduler. Workers see only primitive rows. Exact unpatched Forever and RepeatCount Bill_Production stacks are evaluated from current live fields on the main thread, avoiding expensive target-count product scans. Target-count bills, subclasses, foreign Harmony patches, stale membership and unfinished plans fall through to Vanilla with no worker wait.</description>')
Set-Content $aboutPath $about -Encoding UTF8

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.20.0";' 'internal const string Version = "0.20.1";' 'diagnostics version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$anchor='            "RimMT.RootFrameStallCensus093T34C8",'
$diagReport=Replace-OrThrow $diagReport $anchor ('            "RimMT.DoBillParallelReadinessFabric093T34C9",' + [Environment]::NewLine + $anchor) 'reflection summary'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.20.1 - T34C.9 Parallel DoBill Readiness</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T34-C.9. Reports worker plan yield, live simple-bill evaluations, Vanilla fallbacks, stale membership and worker failures alongside the C.8 root-frame stall census.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

Write-Host 'Applied RimMT T34-C.9 Parallel DoBill Readiness Fabric + Diagnostics v0.20.1.'
