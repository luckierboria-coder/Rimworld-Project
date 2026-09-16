$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T26 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T26 Engine Parallel Epoch
#
# This release deliberately starts again from the T24.1 safety baseline. T25's low-hit
# AsyncJobCandidatePlan and ThinkNode attribution are not installed/compiled by this branch.
# T26 adds an SMF-inspired packet contract:
#   DoSingleTick main-thread epoch -> package-entry capture -> worker batch -> same-package consume.
# Workers receive only primitive arrays owned by RimMT. They never dereference Pawn/Thing/Map and
# never create Jobs/reservations or call CanReach. Every survivor remains live Vanilla authority.

# -----------------------------------------------------------------------------
# Scheduler: atomically submit an independent group so productionParallelBatches measures real
# multi-worker work instead of only ParallelFor. There is still no worker wait/join API.
# -----------------------------------------------------------------------------
$schedulerPath = 'RimMT/Source/RimMT/Scheduling/JobScheduler.cs'
$scheduler = Get-Content $schedulerPath -Raw
$scheduler = Replace-OrThrow $scheduler @'
        public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize, Action<int,int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
'@ @'
        public bool TryEnqueueBatch(string featureId, JobPriority priority, Action[] actions)
        {
            bool production = IsProductionFeature(featureId);
            if (actions == null || actions.Length == 0 || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
            {
                Interlocked.Increment(ref rejected);
                if (production) Interlocked.Increment(ref productionRejected);
                return false;
            }

            for (int i = 0; i < actions.Length; i++)
            {
                if (actions[i] == null)
                {
                    Interlocked.Increment(ref rejected);
                    if (production) Interlocked.Increment(ref productionRejected);
                    return false;
                }
            }

            int count = actions.Length;
            lock (enqueueSync)
            {
                if (!running || pending + count > maxPending || !FeatureGate.IsEnabled(featureId) || CircuitBreaker.IsOpen(featureId))
                {
                    Interlocked.Increment(ref rejected);
                    if (production) Interlocked.Increment(ref productionRejected);
                    return false;
                }

                int nowPending = Interlocked.Add(ref pending, count);
                Interlocked.Add(ref enqueued, count);
                Interlocked.Add(ref parallelBatchesEnqueued, count);
                UpdateHighWater(ref highWaterPending, nowPending);

                if (production)
                {
                    int productionNowPending = Interlocked.Add(ref productionPending, count);
                    Interlocked.Add(ref productionEnqueued, count);
                    Interlocked.Add(ref productionParallelBatches, count);
                    UpdateHighWater(ref productionHighWaterPending, productionNowPending);
                }

                for (int i = 0; i < count; i++)
                    EnqueueReserved(new WorkItem(featureId, actions[i], production), priority);
            }

            ReleaseWakeCredits(count);
            return true;
        }

        public bool ParallelFor(string featureId, int fromInclusive, int toExclusive, int batchSize, Action<int,int> body, Action onComplete = null, JobPriority priority = JobPriority.Normal)
'@ 'T26 scheduler grouped submission'
Set-Content $schedulerPath $scheduler -Encoding UTF8

# Source compile-safety/accessibility cleanup for T26 nested packet types.
$pwsPath = 'RimMT/Source/RimMT/AI/ParallelWorkSearch093T26.cs'
$pws = Get-Content $pwsPath -Raw
$pws = Replace-OrThrow $pws '        private enum PlanKind : byte' '        internal enum PlanKind : byte' 'T26 packet kind accessibility'
Set-Content $pwsPath $pws -Encoding UTF8

# -----------------------------------------------------------------------------
# Runtime feature gates + first-Playing compatibility audit.
# -----------------------------------------------------------------------------
$runtimePath = 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime = Get-Content $runtimePath -Raw
$runtime = Replace-OrThrow $runtime @'
            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");
            FeatureGate.Register(JobGiverSlowSearch0419S.FeatureId, true, "Validated slow-search tail rescue");
'@ @'
            FeatureGate.Register("parallel.jobPartition", true, "Persistent-map search fabric / candidate partition production path");
            FeatureGate.Register(SimulationEpochCoordinator093T26.FeatureId, true, "DoSingleTick simulation epoch validity boundary; main thread remains owner");
            FeatureGate.Register(ParallelWorkSearch093T26.FeatureId, true, "Same-epoch speculative Repair/Refuel primitive work packets");
            FeatureGate.Register(JobGiverSlowSearch0419S.FeatureId, true, "Validated slow-search tail rescue");
'@ 'T26 runtime feature registration'

$runtime = Replace-OrThrow $runtime @'
            FeatureGate.SetEnabled("parallel.jobPartition", work);
            FeatureGate.SetEnabled(JobGiverSlowSearch0419S.FeatureId, work);
'@ @'
            FeatureGate.SetEnabled("parallel.jobPartition", work);
            FeatureGate.SetEnabled(SimulationEpochCoordinator093T26.FeatureId, work);
            FeatureGate.SetEnabled(ParallelWorkSearch093T26.FeatureId, work);
            FeatureGate.SetEnabled(JobGiverSlowSearch0419S.FeatureId, work);
'@ 'T26 runtime settings gate'

$runtime = Replace-OrThrow $runtime @'
                AdaptiveGenClosestAssist.MarkCompatibilityReady();
                AggressiveReachabilityProfilesV17.MarkCompatibilityReady();
'@ @'
                AdaptiveGenClosestAssist.MarkCompatibilityReady();
                AggressiveReachabilityProfilesV17.MarkCompatibilityReady();
                ParallelWorkSearch093T26.MarkCompatibilityReady();
'@ 'T26 compatibility ready audit'
Set-Content $runtimePath $runtime -Encoding UTF8

# -----------------------------------------------------------------------------
# Bootstrap after T24.1: install epoch boundary and same-package parallel work search.
# -----------------------------------------------------------------------------
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t24.1-generic-def-safety";' 'internal const string Version = "0.9.3-t26-engine-parallel-epoch";' 'T26 bootstrap version'
$boot = Replace-OrThrow $boot @'
                JobSearchTransaction093T20.Apply(harmony);
                GenClosestTransactionIndex093T22.Apply(harmony);
'@ @'
                JobSearchTransaction093T20.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                ParallelWorkSearch093T26.Apply(harmony);
                GenClosestTransactionIndex093T22.Apply(harmony);
'@ 'T26 install after T20 transaction'
$boot = $boot.Replace('[RimMT] V0.9.3-T24.1 Generic Def Safety initialized.',
                      '[RimMT] V0.9.3-T26 Engine Parallel Epoch initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# -----------------------------------------------------------------------------
# Runtime report.
# -----------------------------------------------------------------------------
$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T26 Engine Parallel Epoch')
$report = Replace-OrThrow $report @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
'@ @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(SimulationEpochCoordinator093T26.Summary());
            sb.AppendLine(ParallelWorkSearch093T26.Summary());
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
'@ 'T26 report summaries'
$report = $report.Replace('T24 behavior retained; T24.1 disables all closed-generic DefDatabase<TraitDef>/TechLevelDatabase<TraitDef> Harmony hooks after Mono generic-sharing corruption was observed; non-generic WorldRoot timing remains;',
    'T24.1 safety retained; T26 adds an SMF-inspired DoSingleTick epoch contract plus same-package speculative Repair/Refuel POD worker batches. DoSingleTick, Verse state and final validation/commit remain main-thread-owned; worker packets are primitive-only and stale/not-ready packets fall through without waiting;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T24.1 Generic Def Safety', 'V0.9.3-T26 Engine Parallel Epoch')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T26 Engine Parallel Epoch: DoSingleTick epoch boundary + same-package speculative Repair/Refuel primitive worker batches; no wait, no Verse dereference on worker, T24.1 safety retained.'
