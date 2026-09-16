$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T26.1 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Replace-Between-OrThrow {
    param([string]$Text,[string]$Start,[string]$End,[string]$Replacement,[string]$Label)
    $a = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($a -lt 0) { throw "RimMT V0.9.3-T26.1 start anchor not found: $Label" }
    $b = $Text.IndexOf($End, $a + $Start.Length, [System.StringComparison]::Ordinal)
    if ($b -lt 0) { throw "RimMT V0.9.3-T26.1 end anchor not found: $Label" }
    return $Text.Substring(0,$a) + $Replacement + $Text.Substring($b)
}

# RimMT V0.9.3-T26.1 Zero-Wait + FightFires
# Runtime evidence from T26:
# - SingleCallCandidatePartition: 201068 observed -> only 4 supported; 6991 dynamic enumerables materialized;
# - the bounded worker wait averaged ~558us and reached ~2.18ms despite a nominal 0.40ms budget;
# - FightFires dominated heavy WorkGiver evidence: 103/123 sampled >=20ms DetermineNextJob tails,
#   with 622k+ validator rejects in the long-run S4 counters.
# Policy:
# 1) keep DoSingleTick simulation epoch architecture, but remove the low-ROI partition consumer;
# 2) no worker wait/spin in the coordinator production kernel;
# 3) add exact-Vanilla, authority-safe FightFires cheap-negative compaction to S4;
# 4) all surviving fire candidates still execute the original live validator, CanReach/CanReserve,
#    FireIsBeingHandled and final JobOnThing on the main thread.

# -----------------------------------------------------------------------------
# A) Retire the low-ROI T26 candidate-partition consumer from production.
# -----------------------------------------------------------------------------
$projPath = 'RimMT/Source/RimMT/RimMT.csproj'
$proj = Get-Content $projPath -Raw
$proj = Replace-OrThrow $proj `
    '    <!-- T26: SingleCallCandidatePartition reactivated behind parallel.engineStage and >=8ms package-tail admission. -->' `
    '    <Compile Remove="AI\SingleCallCandidatePartition.cs" />' `
    'T26.1 re-exclude low-ROI candidate partition'
Set-Content $projPath $proj -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot `
    'internal const string Version = "0.9.3-t26-engine-parallel";' `
    'internal const string Version = "0.9.3-t26.1-zero-wait-fightfires";' `
    'T26.1 bootstrap version'
$boot = Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                SingleCallCandidatePartition.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'remove T26 partition install'
$boot = $boot.Replace(
    '[RimMT] V0.9.3-T26 Engine Parallel initialized. T24.1 safety retained; DoSingleTick epoch + primitive-only bounded parallel stage active; T25 next-call async cache absent.',
    '[RimMT] V0.9.3-T26.1 Zero-Wait + FightFires initialized. T26 epoch retained; low-ROI partition consumer retired; simulation thread never waits for workers; FightFires targeted negative compaction active when authority-safe.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath = 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime = Get-Content $runtimePath -Raw
$runtime = $runtime.Replace('                SingleCallCandidatePartition.MarkCompatibilityReady();' + [Environment]::NewLine, '')
$runtime = $runtime.Replace('                SingleCallCandidatePartition.MarkCompatibilityReady();`r`n', '')
Set-Content $runtimePath $runtime -Encoding UTF8

# -----------------------------------------------------------------------------
# B) Make the T26 coordinator contract literally zero-wait. No active production consumer uses
#    this kernel in T26.1; retaining a fail-fast stub prevents accidental reintroduction of a
#    SpinWait/Wait-based same-call dependency before a future speculative consumer is designed.
# -----------------------------------------------------------------------------
$epochPath = 'RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs'
$epoch = Get-Content $epochPath -Raw
$epoch = $epoch.Replace(
    '    /// First production consumer: the large custom WorkGiver candidate partition. It may split`r`n    /// pure distance/ring-key calculation across several workers. The main thread waits only for a`r`n    /// very small bounded budget. On timeout it immediately computes the keys itself while workers`r`n    /// finish into a private throw-away buffer. There is no unbounded worker wait.',
    '    /// T26.1 keeps the DoSingleTick epoch boundary but retires the first low-ROI partition consumer.`r`n    /// Production simulation never spin-waits or blocks on a worker. Future consumers must be`r`n    /// speculative/no-wait: publish only if already ready when the main thread naturally reaches them.')

$newKernel = @'
        /// <summary>
        /// T26.1 hard zero-wait guard. The old same-call ring-key consumer is retired because its
        /// admission/materialization cost dwarfed its four successful uses in the measured run.
        /// A future engine-stage consumer must dispatch speculatively before demand and poll only;
        /// the simulation thread is never allowed to wait, spin, sleep or join a worker.
        /// </summary>
        internal static bool TryComputeRingKeys(int rootX, int rootZ, int ringSize, int[] xs, int[] zs, int[] ringKeys)
        {
            Interlocked.Increment(ref kernelAttempts);
            Interlocked.Increment(ref kernelRejected);
            return false;
        }

'@
$epoch = Replace-Between-OrThrow $epoch `
    '        /// <summary>`r`n        /// Computes Chebyshev ring keys' `
    '        internal static string Summary()' `
    $newKernel `
    'replace T26 wait/spin kernel with T26.1 zero-wait guard'
$epoch = $epoch.Replace('T26 engine parallel simulation:', 'T26.1 engine epoch / zero-wait simulation:')
$epoch = $epoch.Replace(
    '. DoSingleTick remains on the Unity main thread; workers receive primitive arrays only; timeout never blocks the simulation thread.',
    '. DoSingleTick remains on the Unity main thread; active same-call worker consumer=OFF; production simulation never waits/spins/joins workers.')
Set-Content $epochPath $epoch -Encoding UTF8

# -----------------------------------------------------------------------------
# C) FightFires targeted cheap-negative compaction inside the existing authority-safe S4 path.
# WorkGiver_FightFires is internal, so identify only the exact runtime type name. Authority safety
# is still checked by the existing reflection walk across HasJobOnThing/JobOnThing before any skip.
# -----------------------------------------------------------------------------
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
'@ @'
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedFightFiresRejected;
        private static long targetedPrefilterAuthorityBypass;
'@ 'FightFires targeted counter'

$s4 = Replace-OrThrow $s4 @'
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            return TargetedPrefilterKind.None;
'@ @'
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            // WorkGiver_FightFires is internal to Assembly-CSharp. Exact full-name equality avoids
            // accidentally applying Vanilla assumptions to a derived or replacement scanner.
            if (type.FullName == "RimWorld.WorkGiver_FightFires")
                return TargetedPrefilterKind.FightFires;
            return TargetedPrefilterKind.None;
'@ 'FightFires targeted resolver'

$s4 = Replace-OrThrow $s4 @'
                if (kind == TargetedPrefilterKind.VisitSickPawn)
                {
                    Pawn sick = thing as Pawn;
                    if (sick == null || worker == null) return false;
                    if (!sick.IsColonist || sick.IsSlave || worker.IsSlave || worker.RaceProps == null ||
                        !worker.RaceProps.Humanlike || sick.Dead || ReferenceEquals(worker, sick) ||
                        !sick.InBed() || !sick.Awake() || sick.IsForbidden(worker))
                        return false;
                    if (sick.needs == null || sick.needs.joy == null || sick.needs.joy.CurCategory > JoyCategory.VeryLow)
                        return false;
                    if (!InteractionUtility.CanReceiveInteraction(sick)) return false;
                    if (sick.needs.food != null && sick.needs.food.Starving) return false;
                    if (sick.needs.rest != null && sick.needs.rest.CurLevel <= 0.33f) return false;
                    return true;
                }

                return true;
'@ @'
                if (kind == TargetedPrefilterKind.VisitSickPawn)
                {
                    Pawn sick = thing as Pawn;
                    if (sick == null || worker == null) return false;
                    if (!sick.IsColonist || sick.IsSlave || worker.IsSlave || worker.RaceProps == null ||
                        !worker.RaceProps.Humanlike || sick.Dead || ReferenceEquals(worker, sick) ||
                        !sick.InBed() || !sick.Awake() || sick.IsForbidden(worker))
                        return false;
                    if (sick.needs == null || sick.needs.joy == null || sick.needs.joy.CurCategory > JoyCategory.VeryLow)
                        return false;
                    if (!InteractionUtility.CanReceiveInteraction(sick)) return false;
                    if (sick.needs.food != null && sick.needs.food.Starving) return false;
                    if (sick.needs.rest != null && sick.needs.rest.CurLevel <= 0.33f) return false;
                    return true;
                }

                if (kind == TargetedPrefilterKind.FightFires)
                {
                    Fire fire = thing as Fire;
                    if (fire == null || worker == null || worker.Map == null) return false;
                    if (!fire.Spawned || fire.Map != worker.Map || !fire.Position.IsValid) return false;

                    Pawn burningPawn = fire.parent as Pawn;
                    if (burningPawn != null)
                    {
                        if (ReferenceEquals(burningPawn, worker)) return false;

                        Faction workerFaction = worker.Faction;
                        Faction workerHost = worker.HostFaction;
                        Faction parentFaction = burningPawn.Faction;
                        Faction parentHost = burningPawn.HostFaction;
                        bool related = parentFaction != null && parentFaction == workerFaction;
                        if (!related && parentHost != null)
                            related = parentHost == workerFaction || parentHost == workerHost;
                        if (!related) return false;

                        if (!worker.Map.areaManager.Home[fire.Position])
                        {
                            IntVec3 a = worker.Position;
                            IntVec3 b = burningPawn.Position;
                            int manhattan = Math.Abs(a.x - b.x) + Math.Abs(a.z - b.z);
                            if (manhattan > 15) return false;
                        }
                        // Live Vanilla validator still performs CanReach, CanReserve and handled-fire logic.
                        return true;
                    }

                    // Vanilla rejects every ordinary/non-pawn fire outside Home before reservation
                    // and handled-fire checks. This was the dominant high-fanout false path in T26.
                    if (worker.WorkTagIsDisabled(WorkTags.Firefighting)) return false;
                    if (!worker.Map.areaManager.Home[fire.Position]) return false;
                    return true;
                }

                return true;
'@ 'FightFires exact-Vanilla cheap negatives'

$s4 = Replace-OrThrow $s4 @'
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else if (targetedKind == TargetedPrefilterKind.TakeEntityToHoldingPlatform) targetedHoldingPlatformRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FeedHemogen) targetedFeedHemogenRejected++;
                            else targetedVisitSickRejected++;
'@ @'
                            if (targetedKind == TargetedPrefilterKind.HaulCorpses) targetedHaulCorpsesRejected++;
                            else if (targetedKind == TargetedPrefilterKind.TakeEntityToHoldingPlatform) targetedHoldingPlatformRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FeedHemogen) targetedFeedHemogenRejected++;
                            else if (targetedKind == TargetedPrefilterKind.VisitSickPawn) targetedVisitSickRejected++;
                            else if (targetedKind == TargetedPrefilterKind.FightFires) targetedFightFiresRejected++;
'@ 'FightFires reject attribution'

$s4 = Replace-OrThrow $s4 @'
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected + "]" +
'@ @'
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected +
                   ", fightFires=" + targetedFightFiresRejected + "]" +
'@ 'FightFires summary counter'

$s4 = Replace-OrThrow $s4 `
    'private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn }' `
    'private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn, FightFires }' `
    'FightFires targeted enum'
Set-Content $s4Path $s4 -Encoding UTF8

# -----------------------------------------------------------------------------
# D) Version/report labels.
# -----------------------------------------------------------------------------
$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T26 Engine Parallel', 'V0.9.3-T26.1 Zero-Wait + FightFires')
$report = $report.Replace('            sb.AppendLine(SingleCallCandidatePartition.Summary());' + [Environment]::NewLine, '')
$report = $report.Replace('            sb.AppendLine(SingleCallCandidatePartition.Summary());`r`n', '')
$report = $report.Replace(
    'T26 adds DoSingleTick simulation epochs and a primitive-only bounded parallel stage. Large custom WorkGiver partitions are admitted only after 8ms package time; worker timeout immediately falls back to serial without waiting; T25 next-call async candidate cache/ThinkNode root attribution are absent;',
    'T26 DoSingleTick simulation epoch retained; T26.1 retires the low-ROI same-call candidate partition, enforces a literal zero-wait simulation-thread contract, and adds authority-safe exact-Vanilla FightFires negative compaction inside S4; T25 next-call async candidate cache/ThinkNode root attribution are absent;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T26 Engine Parallel', 'V0.9.3-T26.1 Zero-Wait + FightFires')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T26.1 Zero-Wait + FightFires: T26 epoch retained; low-ROI partition removed; no simulation-thread worker wait/spin; exact authority-safe FightFires negative compaction added to S4.'
