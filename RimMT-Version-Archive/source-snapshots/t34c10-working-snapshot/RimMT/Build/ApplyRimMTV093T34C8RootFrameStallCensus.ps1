param()
$ErrorActionPreference='Stop'
$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n")
  $old=$old.Replace("`r`n","`n")
  $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T34-C.8 anchor missing: $label" }
  return $text.Replace($old,$new)
}

Copy-Item (Join-Path $PSScriptRoot 'Templates/RootFrameStallCensus093T34C8.cs.txt') `
  (Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RootFrameStallCensus093T34C8.cs') -Force

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t34c7-reuse-gated-classification-kernel' '0.9.3-t34c8-root-frame-stall-census' 'bootstrap version'
$anchor='                ScannerParallelFabric093T34B.Apply(harmony);'
$boot=Replace-OrThrow $boot $anchor ($anchor + [Environment]::NewLine +
  '                RootFrameStallCensus093T34C8.Apply(harmony);') 'root-frame census install'
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$anchor='            FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId, true, "T34-C primitive-only parallel candidate classification");'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.Register(RootFrameStallCensus093T34C8.FeatureId, true, "Measurement-only Root_Play.Update and outside-root stall census");') 'feature register'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T34C.7 Reuse-Gated Classification Kernel','V0.9.3-T34C.8 Root-Frame Stall Census')
$anchor='            sb.AppendLine(CandidateClassificationFabric093T34C.Summary());'
$report=Replace-OrThrow $report $anchor ($anchor + [Environment]::NewLine +
  '            sb.AppendLine(RootFrameStallCensus093T34C8.Summary());') 'production summary'
$report=$report.Replace(
  'T34-C.7 candidate classification=ACTIVE(reuse-gated SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + authority-safe Refuel/Refuel_Turret full-negative kernel + volatile-state revocation);',
  'T34-C.8 root-frame stall census=ACTIVE(measurement-only Root_Play.Update envelope + outside-root wall gaps + focus/pause/GC/tick correlation); T34-C.7 candidate classification=ACTIVE(reuse-gated SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + authority-safe Refuel/Refuel_Turret full-negative kernel + volatile-state revocation);')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T34C.8 Root-Frame Stall Census</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T34-C.8 for RimWorld 1.5. Adds a measurement-only Root_Play.Update stall census after gameplay evidence showed multi-second wall-clock gaps outside DoSingleTick. It separates time inside the managed play update from time between updates and records game ticks, focus, pause state, GC collections, managed memory and the live Harmony patch chain. T34-C.7 classification remains unchanged, no worker waits are introduced, and Vanilla retains final gameplay authority.</description>')
Set-Content $aboutPath $about -Encoding UTF8

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.19.7";' 'internal const string Version = "0.20.0";' 'diagnostics version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$anchor='            "RimMT.CandidateClassificationFabric093T34C",'
$diagReport=Replace-OrThrow $diagReport $anchor ('            "RimMT.RootFrameStallCensus093T34C8",' + [Environment]::NewLine + $anchor) 'reflection summary'
$anchor='            AuditOne(sb, AccessTools.Method(typeof(TickManager), "DoSingleTick"), "TickManager.DoSingleTick");'
$diagReport=Replace-OrThrow $diagReport $anchor ('            AuditOne(sb, AccessTools.Method(typeof(Root_Play), "Update"), "Root_Play.Update");' + [Environment]::NewLine + $anchor) 'Root_Play Harmony audit'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.20.0 - T34C.8 Root-Frame Stall Census</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T34-C.8. Reports Root_Play.Update and outside-root wall-clock stalls with tick, focus, pause, GC and Harmony-owner correlation, alongside the existing T34-C.7 candidate classification parity and yield counters.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

Write-Host 'Applied RimMT T34-C.8 Root-Frame Stall Census + Diagnostics v0.20.0.'
