$ErrorActionPreference='Stop'

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

function Replace-OrThrow([string]$text,[string]$old,[string]$new,[string]$label){
  $text=$text.Replace("`r`n","`n")
  $old=$old.Replace("`r`n","`n")
  $new=$new.Replace("`r`n","`n")
  if(-not $text.Contains($old)){ throw "T35-B anchor missing: $label" }
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
# Production pair: behavior stays on the validated T35-A line. T35-B changes
# diagnostics overhead only; aggressiveScanner remains permanently excluded.
# ---------------------------------------------------------------------------
$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot '0.9.3-t35a-production-safety-split' '0.9.3-t35b-burst-diagnostics-pair' 'production version'
$boot=$boot.Replace('[RimMT] V0.9.3-T35-A Production Safety Split initialized.',
  '[RimMT] V0.9.3-T35-B Burst Diagnostics Pair initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('[RimMT] V0.9.3-T35-A Production Safety Split on-demand report',
  '[RimMT] V0.9.3-T35-B Burst Diagnostics Pair on-demand report')
$report=$report.Replace('V0.9.3-T35-A Production Safety Split; baseline',
  'V0.9.3-T35-B Burst Diagnostics Pair; baseline')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath=Join-Path $root 'RimMT/About/About.xml'
$about=Get-Content $aboutPath -Raw
$about=[regex]::Replace($about,'<name>.*?</name>','<name>RimMT V0.9.3-T35B Burst Diagnostics Pair</name>',1)
$about=[regex]::Replace($about,'(?s)<description>.*?</description>',
  '<description>RimMT T35-B for RimWorld 1.5. Production behavior remains on the validated T35-A safety line: no aggressive worker-validator route, strict no-wait worker policy, live main-thread authority for Reachability/reservations/validators/Job creation, and conservative scanner prefetch. T35-B pairs this production build with a redesigned low-overhead diagnostics companion whose high-frequency probes are armed only in bounded bursts after slow ticks.</description>')
Set-Content $aboutPath $about -Encoding UTF8

# ---------------------------------------------------------------------------
# Diagnostics v0.22: permanent hot-path probes are removed.
# Always-on: DoSingleTick envelope, DetermineNextJob envelope, Root_Play stall
# census. High-frequency Pawn/Pather/Map/World probes are dynamically installed
# for six ticks only after a >=100ms DoSingleTick, with 120-tick cooldown.
# Broad WorkGiver timing is not installed.
# ---------------------------------------------------------------------------
$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=$diag.Replace("`r`n","`n")
$diag=Replace-OrThrow $diag 'internal const string Version = "0.21.0";' 'internal const string Version = "0.22.0";' 'diagnostics version'

foreach($token in @(
  'Patch(harmony, AccessTools.Method(typeof(Pawn), "Tick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick")',
  'Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick")',
  'Patch(harmony, AccessTools.Method(typeof(World), "WorldTick")',
  'Patch(harmony, AccessTools.Method(typeof(Storyteller), "StorytellerTick")',
  'PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThingReachable"',
  'PatchNamedMethods(harmony, typeof(GenClosest), "ClosestThing_Global"',
  'PatchNamedMethods(harmony, typeof(Reachability), "CanReach"',
  'DiagnosticsV02.Apply(harmony);',
  'DiagnosticsV03.Apply(harmony);'
)){
  $diag=Remove-LineToken $diag $token
}

$diag=Replace-OrThrow $diag '                RootFrameStallCensusT35A.Apply(harmony);' @'
                BurstProbeControllerT35B.Initialize(harmony);
                RootFrameStallCensusT35A.Apply(harmony);
'@ 'burst controller install'

$diag=Replace-OrThrow $diag @'
        public static void TickPrefix()
        {
            DiagnosticsV02.OnTickBegin();
            DiagnosticsHub.BeginTick();
        }
'@ @'
        public static void TickPrefix()
        {
            BurstProbeControllerT35B.OnTickBegin();
            DiagnosticsV02.OnTickBegin();
            DiagnosticsHub.BeginTick();
        }
'@ 'tick prefix burst hook'

$diag=Replace-OrThrow $diag @'
        public static void TickPostfix()
        {
            DiagnosticsHub.EndTick();
            DiagnosticsV02.OnTickEnd();
        }
'@ @'
        public static void TickPostfix()
        {
            DiagnosticsHub.EndTick();
            DiagnosticsV02.OnTickEnd();
            BurstProbeControllerT35B.OnTickEnd();
        }
'@ 'tick postfix burst hook'

$diag=$diag.Replace('Optional diagnostics only; disable this mod for normal gameplay.',
  'Low-overhead burst diagnostics active; permanent high-frequency probes are not installed.')
Set-Content $diagPatchPath $diag -Encoding UTF8

$hubPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsHub.cs'
$hub=Get-Content $hubPath -Raw
$hub=$hub.Replace("`r`n","`n")
$old=@'
            int cadence = RimMTDiagnosticsSettings.SampleEveryTicks;
            bool periodic = cadence <= 1 || (currentTick >= 0 && currentTick % cadence == 0);
            bool burst = burstTicksRemaining > 0;
            if (burstTicksRemaining > 0) burstTicksRemaining--;
            deepActive = periodic || burst;
'@
$new=@'
            // T35-B: no periodic deep sampling. High-frequency Harmony probes do
            // not exist unless BurstProbeControllerT35B has armed a bounded burst.
            deepActive = BurstProbeControllerT35B.DeepActive;
'@
$hub=Replace-OrThrow $hub $old $new 'disable periodic deep sampling'
$hub=$hub.Replace(
  'if (us >= 50000L && burstTicksRemaining < RimMTDiagnosticsSettings.PostSpikeBurstTicks)' + "`n" +
  '                    burstTicksRemaining = RimMTDiagnosticsSettings.PostSpikeBurstTicks;' + "`n",
  '')
Set-Content $hubPath $hub -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$diagReport=Get-Content $diagReportPath -Raw
$diagReport=$diagReport.Replace("`r`n","`n")
$diagReport=Replace-OrThrow $diagReport '            sb.Append(DiagnosticsHub.BuildSummary());' @'
            sb.Append(DiagnosticsHub.BuildSummary());
            sb.AppendLine(BurstProbeControllerT35B.Summary());
'@ 'burst summary'
Set-Content $diagReportPath $diagReport -Encoding UTF8

$modPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/RimMTDiagnosticsMod.cs'
$mod=Get-Content $modPath -Raw
$mod=$mod.Replace('Standalone diagnostics companion v0.3. Disable the entire mod for normal gameplay when profiling is not needed.',
  'T35-B low-overhead diagnostics companion. High-frequency probes are normally absent and are armed only after >=100 ms game ticks.')
$mod=$mod.Replace('v0.3 adds per-DetermineNextJob WorkGiver correlation for >=20 ms calls and detailed HaulMerge/Pather Harmony authority audit.',
  'Burst policy: 6 detailed ticks after a >=100 ms tick, then probes are removed; re-arm cooldown is 120 game ticks.')
$mod=$mod.Replace('            list.Label("Deep sample cadence: every " + RimMTDiagnosticsSettings.SampleEveryTicks + " game ticks");',
  '            list.Label("T35-B burst mode: high-frequency probes arm only after >=100 ms game ticks.");')
$mod=$mod.Replace('v0.3 slow-DNJ correlation timestamps WorkGiver methods only while DetermineNextJob is active; this is diagnostics-only overhead.',
  'Broad WorkGiver method timing is disabled in T35-B. Slow DetermineNextJob envelopes remain available with near-zero idle overhead.')
Set-Content $modPath $mod -Encoding UTF8

$diagAboutPath=Join-Path $root 'RimMTDiagnostics/About/About.xml'
$diagAbout=Get-Content $diagAboutPath -Raw
$diagAbout=[regex]::Replace($diagAbout,'<name>.*?</name>','<name>RimMT Diagnostics v0.22.0 - T35B Burst</name>',1)
$diagAbout=[regex]::Replace($diagAbout,'(?s)<description>.*?</description>',
  '<description>Optional low-overhead diagnostics companion for RimMT T35-B. Only DoSingleTick, DetermineNextJob and Root_Play stall envelopes remain permanent. Pawn/Pather/Map/World and selected Pather-child probes are dynamically installed for six ticks after a >=100 ms game tick, then removed, with a 120-tick cooldown. Broad WorkGiver timing is disabled. The aggressiveScanner regression probe remains active in reports.</description>')
Set-Content $diagAboutPath $diagAbout -Encoding UTF8

# Hard guards.
$diag=Get-Content $diagPatchPath -Raw
foreach($forbidden in @(
  'Patch(harmony, AccessTools.Method(typeof(Pawn), "Tick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick")',
  'Patch(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick")',
  'Patch(harmony, AccessTools.Method(typeof(Map), "MapPostTick")',
  'Patch(harmony, AccessTools.Method(typeof(World), "WorldTick")',
  'Patch(harmony, AccessTools.Method(typeof(Storyteller), "StorytellerTick")',
  'DiagnosticsV02.Apply(harmony);',
  'DiagnosticsV03.Apply(harmony);'
)){
  if($diag.Contains($forbidden)){ throw "T35-B permanent hot-path probe leaked: $forbidden" }
}

$controllerPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/BurstProbeControllerT35B.cs'
if(-not (Test-Path $controllerPath)){ throw 'T35-B burst controller source missing' }
$controller=Get-Content $controllerPath -Raw
foreach($required in @(
  'private const long TriggerUs = 100000L;',
  'private const int BurstTicks = 6;',
  'private const int CooldownTicks = 120;',
  'RemoveHotProbes();',
  'WorkGiver broad timing is disabled'
)){
  if(-not $controller.Contains($required)){ throw "T35-B burst invariant missing: $required" }
}

$mainRoot=Join-Path $root 'RimMT/Source/RimMT'
foreach($token in @('parallel.aggressiveScanner','AggressiveParallelScanner093T34D')){
  $hit=Get-ChildItem $mainRoot -Recurse -Filter '*.cs' -File |
    Select-String -SimpleMatch $token | Select-Object -First 1
  if($hit){ throw "T35-B aggressiveScanner regression: $token at $($hit.Path):$($hit.LineNumber)" }
}

Write-Host 'Applied RimMT T35-B Burst Diagnostics Pair + Diagnostics v0.22.0.'
