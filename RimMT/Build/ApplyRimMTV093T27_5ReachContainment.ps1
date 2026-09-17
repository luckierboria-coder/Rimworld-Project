$ErrorActionPreference='Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if(-not $Text.Contains($Old)){ throw "T27.5 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27.5 — ReachProfile stutter containment + diagnostics v0.2 pairing.
# Production changes are intentionally narrow:
# - no new diagnostics are added to RimMT.dll;
# - Region.Allows capture checks budget after every region instead of every four;
# - a single >=2ms Region.Allows pair aborts/quarantines that slot for the existing short cooldown;
# - under Critical load, capture drain advances at most one region pair per frame;
# - Vanilla Reachability remains authoritative while capture is incomplete/quarantined.

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.4-diagnostics-split";' 'internal const string Version = "0.9.3-t27.5-reach-containment";' 'version'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T27.4 Diagnostics Split initialized. T27/T27.1 and T18/T19 source-reordering paths remain retired; T26.1 zero-wait retained; new diagnostics live only in optional allen.rimmt.diagnostics; FullParallel hard-OFF.',
    '[RimMT] V0.9.3-T27.5 Reach Containment initialized. T27/T27.1 and T18/T19 source-reordering remain retired; ReachProfile capture checks every region, aborts >=2ms capture pairs, and advances only one pair/frame under Critical load; diagnostics remain external-only; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$reachPath='RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach=Get-Content $reachPath -Raw
$reach=Replace-OrThrow $reach 'private const int CaptureCheckMask = 3;' 'private const int CaptureCheckMask = 0;' 'capture check every region'
$reach=Replace-OrThrow $reach 'private const int CaptureWatchdogMicroseconds = 5000;' 'private const int CaptureWatchdogMicroseconds = 2000;' 'capture watchdog 2ms'
$reach=Replace-OrThrow $reach 'private const long MaxCaptureQueueAgeFrames = 8;' 'private const long MaxCaptureQueueAgeFrames = 6;' 'capture queue age'

$reach=Replace-OrThrow $reach @'
            long watchdogTicks = Math.Max(1L,
                Stopwatch.Frequency * CaptureWatchdogMicroseconds / 1000000L);

            while (PendingProfileCaptures.Count != 0)
'@ @'
            long watchdogTicks = Math.Max(1L,
                Stopwatch.Frequency * CaptureWatchdogMicroseconds / 1000000L);
            int criticalPairBudget = AdaptiveLoadBalancer.Pressure == LoadPressure.Critical ? 1 : int.MaxValue;
            int pairsThisFrame = 0;

            while (PendingProfileCaptures.Count != 0)
'@ 'critical one-pair capture budget'

$reach=Replace-OrThrow $reach @'
                    long pairTicks = Stopwatch.GetTimestamp() - pairStart;
                    capture.Cursor++;

                    // We cannot pre-empt one foreign/Vanilla Region.Allows call, but once a single
'@ @'
                    long pairTicks = Stopwatch.GetTimestamp() - pairStart;
                    capture.Cursor++;
                    pairsThisFrame++;

                    // We cannot pre-empt one foreign/Vanilla Region.Allows call, but once a single
'@ 'capture pair accounting'

$reach=Replace-OrThrow $reach @'
                    if ((capture.Cursor & CaptureCheckMask) == 0 && BudgetSpent(globalStart, budgetTicks))
                    {
                        RecordProfileCaptureSlice(sliceStart);
                        return;
                    }
'@ @'
                    if (pairsThisFrame >= criticalPairBudget ||
                        ((capture.Cursor & CaptureCheckMask) == 0 && BudgetSpent(globalStart, budgetTicks)))
                    {
                        RecordProfileCaptureSlice(sliceStart);
                        return;
                    }
'@ 'critical capture early return'

$reach=$reach.Replace('Topology and Region.Allows profile capture are frame-budgeted on the main thread; workers consume primitive immutable arrays only.',
    'Topology and Region.Allows profile capture are frame-budgeted on the main thread; T27.5 checks every region, uses a 2ms single-pair watchdog and advances at most one pair/frame under Critical pressure; workers consume primitive immutable arrays only.')
Set-Content $reachPath $reach -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T27.5 Reach Containment')
$report=$report.Replace(
    'T27.4 retains the T27.2/T27.3 behavior resets, but removes the T27.3 Wait tracer from RimMT.dll. New profiling belongs to optional allen.rimmt.diagnostics; legacy observability remains only where older production paths still consume it. The WorkGiver safety registry remains audit-only; FullParallel stays hard-OFF;',
    'T27.5 retains the T27.2/T27.3 behavior resets and T27.4 diagnostics split. ReachProfile capture now checks budget after every region, aborts/quarantines a capture when one Region.Allows pair exceeds 2ms, and advances at most one pair per frame under Critical load. New profiling remains external-only; FullParallel stays hard-OFF;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
    $about=Get-Content $aboutPath -Raw
    $about=$about.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T27.5 Reach Containment')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.5 Reach Containment: diagnostics stay external; ReachProfile capture budget checks every region, 2ms watchdog, one pair/frame at Critical pressure.'
