$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T26 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Replace-Between-OrThrow {
    param([string]$Text,[string]$Start,[string]$End,[string]$Replacement,[string]$Label)
    $a = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($a -lt 0) { throw "RimMT V0.9.3-T26 start anchor not found: $Label" }
    $b = $Text.IndexOf($End, $a + $Start.Length, [System.StringComparison]::Ordinal)
    if ($b -lt 0) { throw "RimMT V0.9.3-T26 end anchor not found: $Label" }
    return $Text.Substring(0, $a) + $Replacement + $Text.Substring($b)
}

# RimMT V0.9.3-T26 Engine-Level Parallel Simulation Framework
# Base: T24.1. T25 async next-call candidate cache and hot ThinkNode attribution are intentionally absent.
# T26 adds a DoSingleTick simulation epoch plus a primitive-only parallel stage. The first production
# consumer is the old single-call custom WorkGiver partition, rewritten so it never waits without a
# strict budget: worker timeout immediately falls back to the existing serial computation while late
# workers finish only into a private unpublished array.

# Compile the deliberately reactivated T26 consumer.
$projPath = 'RimMT/Source/RimMT/RimMT.csproj'
$proj = Get-Content $projPath -Raw
$proj = Replace-OrThrow $proj `
    '    <Compile Remove="AI\SingleCallCandidatePartition.cs" />' `
    '    <!-- T26: SingleCallCandidatePartition reactivated behind parallel.engineStage and >=8ms package-tail admission. -->' `
    'reactivate SingleCallCandidatePartition'
Set-Content $projPath $proj -Encoding UTF8

# Harden old V0.4.10 candidate partition into a T26 engine-stage consumer.
$partPath = 'RimMT/Source/RimMT/AI/SingleCallCandidatePartition.cs'
$part = Get-Content $partPath -Raw
$part = Replace-OrThrow $part 'private const string FeatureId = "parallel.jobPartition";' 'private const string FeatureId = "parallel.engineStage";' 'isolate T26 feature gate'
$part = Replace-OrThrow $part @'
        private const int MinCandidateCount = 96;
        private const int WorkerAssistMinCount = 512;
        private const int RingSize = 16;
        private const double WorkerAssistBudgetMs = 0.20;
'@ @'
        private const int MinCandidateCount = 256;
        private const int WorkerAssistMinCount = 256;
        private const int RingSize = 16;
        private static readonly long TailAdmissionTicks = Math.Max(1L, Stopwatch.Frequency * 8L / 1000L);
'@ 'T26 large-tail admission constants'

$part = Replace-OrThrow $part @'
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
'@ @'
            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            // T26 is a stutter-tail stage, not a permanent tax on ordinary WorkGiver scans.
            long packageStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (packageStart <= 0L || Stopwatch.GetTimestamp() - packageStart < TailAdmissionTicks)
                return;

            if (map == null || map.Disposed || !root.IsValid || !root.InBounds(map) || customGlobalSearchSet == null)
'@ 'T26 package-tail admission'

$newWorker = @'
        private static bool TryWorkerRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] ringKeys)
        {
            Interlocked.Increment(ref workerAssistAttempts);
            bool completed = SimulationEpochCoordinator093T26.TryComputeRingKeys(rootX, rootZ, RingSize, xs, zs, ringKeys);
            if (completed)
                Interlocked.Increment(ref workerAssistCompleted);
            else
                Interlocked.Increment(ref workerAssistTimeouts);
            return completed;
        }

'@
$part = Replace-Between-OrThrow $part `
    '        private static bool TryWorkerRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] ringKeys)' `
    '        private static void ComputeRingKeys(int rootX, int rootZ, int[] xs, int[] zs, int[] ringKeys)' `
    $newWorker `
    'replace blocking worker assist with T26 bounded engine stage'
$part = $part.Replace('parallel.jobPartition V0.4.10', 'T26 parallel.engineStage candidate partition')
$part = $part.Replace('workerWaits=', 'workerFallbacks=')
Set-Content $partPath $part -Encoding UTF8

# Feature registration/settings + compatibility readiness.
$runtimePath = 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime = Get-Content $runtimePath -Raw
$runtime = Replace-OrThrow $runtime `
    '            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");' `
    '            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");`r`n            FeatureGate.Register(SimulationEpochCoordinator093T26.FeatureId, true, "T26 DoSingleTick epoch + primitive-only bounded parallel simulation stage");' `
    'register T26 engine stage'
$runtime = Replace-OrThrow $runtime `
    '            FeatureGate.SetEnabled("parallel.jobPartition", work);' `
    '            FeatureGate.SetEnabled("parallel.jobPartition", work);`r`n            FeatureGate.SetEnabled(SimulationEpochCoordinator093T26.FeatureId, work);' `
    'settings gate T26 engine stage'
$runtime = Replace-OrThrow $runtime `
    '                AdaptiveGenClosestAssist.MarkCompatibilityReady();' `
    '                AdaptiveGenClosestAssist.MarkCompatibilityReady();`r`n                SingleCallCandidatePartition.MarkCompatibilityReady();' `
    'T26 partition compatibility ready'
Set-Content $runtimePath $runtime -Encoding UTF8

# Install on top of T24.1. T25 is not in this build chain.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot `
    'internal const string Version = "0.9.3-t24.1-generic-def-safety";' `
    'internal const string Version = "0.9.3-t26-engine-parallel";' `
    'T26 bootstrap version'
$boot = Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                SingleCallCandidatePartition.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'T26 simulation epoch + parallel partition installs'
$boot = $boot.Replace('[RimMT] V0.9.3-T24.1 Generic Def Safety initialized.',
    '[RimMT] V0.9.3-T26 Engine Parallel initialized. T24.1 safety retained; DoSingleTick epoch + primitive-only bounded parallel stage active; T25 next-call async cache absent.')
Set-Content $bootPath $boot -Encoding UTF8

# Runtime report.
$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T26 Engine Parallel')
$report = Replace-OrThrow $report @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(SimulationEpochCoordinator093T26.Summary());
            sb.AppendLine(SingleCallCandidatePartition.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
'@ 'T26 report summaries'
$report = $report.Replace(
    'T24 behavior retained; T24.1 disables all closed-generic DefDatabase<TraitDef>/TechLevelDatabase<TraitDef> Harmony hooks after Mono generic-sharing corruption was observed; non-generic WorldRoot timing remains;',
    'T24.1 generic-Def safety retained; T26 adds DoSingleTick simulation epochs and a primitive-only bounded parallel stage. Large custom WorkGiver partitions are admitted only after 8ms package time; worker timeout immediately falls back to serial without waiting; T25 next-call async candidate cache/ThinkNode root attribution are absent;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T26 Engine Parallel')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T26 Engine Parallel: T24.1 base; T25 absent; DoSingleTick epoch coordinator + >=8ms-tail primitive ParallelFor candidate stage; bounded wait with immediate serial fallback and no late-result publication.'
