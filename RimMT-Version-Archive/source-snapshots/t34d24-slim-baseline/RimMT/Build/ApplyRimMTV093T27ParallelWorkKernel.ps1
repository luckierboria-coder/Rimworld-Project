$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "T27 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T27 — Parallel Work Kernel
# Base: T26.1 Zero-Wait + FightFires.
# Bottom-level policy:
# - keep DoSingleTick zero-wait;
# - extend PersistentMapSearchFabric with worker-built distance/source-order plans;
# - classify WorkGivers as MainThread vs SnapshotParallel; FullParallel remains reserved/OFF;
# - worker code never dereferences Verse/Unity state;
# - main thread fully validates snapshot membership and positions before giving an ordered source
#   back to the original ClosestThingReachable call; Reachability/validator/reservation/Job commit remain Vanilla.

$fabricPath='RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs'
$fabric=Get-Content $fabricPath -Raw
$fabric=Replace-OrThrow $fabric 'internal static class PersistentMapSearchFabric' 'internal static partial class PersistentMapSearchFabric' 'make fabric partial'
$fabric=Replace-OrThrow $fabric 'internal sealed class SourceSnapshot' 'internal sealed partial class SourceSnapshot' 'make source snapshot partial'
Set-Content $fabricPath $fabric -Encoding UTF8

$runtimePath='RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$runtime=Replace-OrThrow $runtime @'
            FeatureGate.Register(SimulationEpochCoordinator093T26.FeatureId, true, "T26 DoSingleTick epoch + primitive-only bounded parallel simulation stage");
'@ @'
            FeatureGate.Register(SimulationEpochCoordinator093T26.FeatureId, true, "T26 DoSingleTick epoch boundary; T26.1 same-call consumer retired");
            FeatureGate.Register(ParallelWorkKernel093T27.FeatureId, true, "T27 speculative persistent-snapshot WorkGiver search plans; FullParallel behavior execution OFF");
'@ 'register T27 work kernel'
$runtime=Replace-OrThrow $runtime @'
            FeatureGate.SetEnabled(SimulationEpochCoordinator093T26.FeatureId, work);
'@ @'
            FeatureGate.SetEnabled(SimulationEpochCoordinator093T26.FeatureId, work);
            FeatureGate.SetEnabled(ParallelWorkKernel093T27.FeatureId, work);
'@ 'settings gate T27 work kernel'
Set-Content $runtimePath $runtime -Encoding UTF8

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t26.1-zero-wait-fightfires";' 'internal const string Version = "0.9.3-t27-parallel-work-kernel";' 'T27 version'
$boot=Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                ParallelWorkKernel093T27.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'install T27 work kernel'
$boot=$boot.Replace(
    '[RimMT] V0.9.3-T26.1 Zero-Wait + FightFires initialized. T26 epoch retained; low-ROI partition retired; simulation thread never waits for workers; FightFires negative compaction active only when authority-safe.',
    '[RimMT] V0.9.3-T27 Parallel Work Kernel initialized. T26.1 zero-wait retained; persistent snapshots now feed speculative worker-built WorkGiver search plans; FullParallel WorkGiver behavior execution remains OFF.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T26.1 Zero-Wait + FightFires','V0.9.3-T27 Parallel Work Kernel')
$report=Replace-OrThrow $report @'
            sb.AppendLine(SimulationEpochCoordinator093T26.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ @'
            sb.AppendLine(SimulationEpochCoordinator093T26.Summary());
            sb.AppendLine(ParallelWorkKernel093T27.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ 'T27 report summary'
$report=$report.Replace(
    'T26 DoSingleTick simulation epoch retained; T26.1 retires the low-ROI same-call candidate partition, enforces literal zero-wait on the simulation thread, and adds authority-safe exact-Vanilla FightFires negative compaction inside S4; T25 next-call async candidate cache/ThinkNode root attribution are absent;',
    'T26.1 zero-wait/FightFires behavior retained; T27 adds bottom-level WorkGiver parallel classification plus speculative persistent-snapshot distance/source-order plans. Workers never execute WorkGiver/validator/Reachability/reservation/Job code; FullParallel is reserved/OFF; T25 next-call async candidate cache/ThinkNode root attribution are absent;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T26.1 Zero-Wait + FightFires','V0.9.3-T27 Parallel Work Kernel')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27 Parallel Work Kernel: persistent snapshot distance plans + MainThread/SnapshotParallel classification; FullParallel remains OFF.'
