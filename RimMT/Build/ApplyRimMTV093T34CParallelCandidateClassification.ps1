$ErrorActionPreference='Stop'

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n")
  $old=$old.Replace("`r`n","`n")
  $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T34-C anchor missing: $label" }
  return $text.Replace($old,$new)
}

$template=Join-Path $PSScriptRoot 'Templates/CandidateClassificationFabric093T34C.cs.txt'
$classificationPath=Join-Path $root 'RimMT/Source/RimMT/AI/CandidateClassificationFabric093T34C.cs'
Copy-Item $template $classificationPath -Force

$scannerPath=Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs'
$scanner=Get-Content $scannerPath -Raw

$old=@'
            long liveStarted = Stopwatch.GetTimestamp();
            Thing chosen = null;
            int visited = 0;
            int reaches = 0;
            int validations = 0;
'@
$new=@'
            CandidateClassificationFabric093T34C.ClassificationPlan classificationPlan;
            bool classificationReady = CandidateClassificationFabric093T34C.TryGetOrSchedule(
                snapshot, entries, validator, root.x, root.z, out classificationPlan);

            long liveStarted = Stopwatch.GetTimestamp();
            Thing chosen = null;
            int visited = 0;
            int reaches = 0;
            int validations = 0;
            int classifiedRejected = 0;
'@
$scanner=Replace-OrThrow $scanner $old $new 'classification plan acquisition'

$old=@'
                    Thing thing = entries[i].Thing;
                    visited++;

                    reaches++;
'@
$new=@'
                    Thing thing = entries[i].Thing;
                    byte classificationReason = 0;
                    int classificationIndex = entries[i].SourceIndex;
                    if (classificationReady && !CandidateClassificationFabric093T34C.TryGetRejectReason(
                        classificationPlan, classificationIndex, out classificationReason))
                    {
                        CandidateClassificationFabric093T34C.Quarantine(snapshot, classificationPlan);
                        RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, liveStarted);
                        return true;
                    }
                    if (classificationReady && classificationReason != 0)
                    {
                        if (!CandidateClassificationFabric093T34C.ValidateReject(
                            classificationPlan, classificationIndex, thing))
                        {
                            CandidateClassificationFabric093T34C.Quarantine(
                                snapshot, classificationPlan);
                            RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, liveStarted);
                            return true;
                        }
                        classifiedRejected++;
                        continue;
                    }

                    visited++;

                    reaches++;
'@
$scanner=Replace-OrThrow $scanner $old $new 'classification rejection consumption'

$old=@'
            RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, liveStarted);
            __result = chosen;
'@
$new=@'
            RecordElapsed(ref mainLiveTicks, ref mainLiveTicksMax, liveStarted);
            if (classificationReady)
                CandidateClassificationFabric093T34C.NoteConsumed(
                    classificationPlan, classifiedRejected, within);
            __result = chosen;
'@
$scanner=Replace-OrThrow $scanner $old $new 'classification consumption telemetry'
Set-Content $scannerPath $scanner -Encoding UTF8

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t34b-scanner-parallel-fabric' '0.9.3-t34c2-source-indexed-classification-reuse' 'bootstrap version'
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$anchor='            FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId, true, "T34-B same-package scanner candidate parallel planning");'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.Register(CandidateClassificationFabric093T34C.FeatureId, true, "T34-C primitive-only parallel candidate classification");') 'runtime register'
$anchor='            FeatureGate.SetEnabled(ScannerParallelFabric093T34B.FeatureId, work);'
$runtime=Replace-OrThrow $runtime $anchor ($anchor + [Environment]::NewLine +
  '            FeatureGate.SetEnabled(CandidateClassificationFabric093T34C.FeatureId, work);') 'runtime gate'
Set-Content $runtimePath $runtime -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T34B Scanner Parallel Fabric','V0.9.3-T34C.2 Source-Indexed Classification Reuse')
$anchor='            sb.AppendLine(ScannerParallelFabric093T34B.Summary());'
$report=Replace-OrThrow $report $anchor ($anchor + [Environment]::NewLine +
  '            sb.AppendLine(CandidateClassificationFabric093T34C.Summary());') 'production summary'
$report=$report.Replace(
  'T34-B scanner parallel planning=ACTIVE(HIGH priority, same-package overlap); T34-A persistent candidate fabric remains synchronous fallback; fabric mutations are tick-coalesced + dirty-source-only rebuilt.',
  'T34-C.2 candidate classification=ACTIVE(root-independent SourceSnapshot+Kernel plans indexed by stable SourceIndex + live parity + trusted diagnostics coexistence); mutable stack/pawn/building facts=SHADOW; invalid/duplicate indices and unknown Harmony owners fail open; T34-B distance planning and T34-A synchronous fallback remain active.')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T34C.2 Source-Indexed Classification Reuse</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T34-C.2 for RimWorld 1.5. Reuses no-wait candidate classification across different pawn roots by keying each plan to the exact SourceSnapshot and validator kernel, with rejection and shadow bitmaps indexed by the distance plan stable SourceIndex. Every authoritative negative is rechecked live before Reachability or the original validator can be skipped. Invalid or duplicate indices, snapshot replacement, unknown Harmony owners, diagnostics transpilers/finalizers, missing, unfinished or parity-mismatched plans fail open. RimMT and external RimMT.Diagnostics measurement prefixes/postfixes remain trusted coexistence owners. Mutable stack fullness, pawn availability and building power remain shadow-only. T34-B and T34-A remain fallbacks; FullParallel remains HARD_OFF.</description>')
Set-Content $aboutPath $about -Encoding UTF8

$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.18.0";' 'internal const string Version = "0.19.2";' 'diagnostics version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$anchor='            "RimMT.ScannerParallelFabric093T34B",'
$diagReport=Replace-OrThrow $diagReport $anchor ('            "RimMT.CandidateClassificationFabric093T34C",' + [Environment]::NewLine + $anchor) 'diagnostics reflection summary'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.19.2 - T34C.2 Source-Indexed Reuse</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional diagnostics companion for RimMT T34-C.2. Its measurement-only prefixes/postfixes are recognized by the production classifier authority audit. Reports source-plan hits, cross-root reuse, stable-index validation, kernel resolution, trusted/foreign patch counts, authoritative rejection counts and live parity quarantine alongside T34-B scanner-plan counters.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

Write-Host 'Applied RimMT T34-C.2 Source-Indexed Classification Reuse + Diagnostics v0.19.2.'
