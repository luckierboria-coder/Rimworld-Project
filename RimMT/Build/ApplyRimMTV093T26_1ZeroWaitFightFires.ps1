$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "T26.1 anchor missing: $Label" }
    return $Text.Replace($Old,$New)
}

# T26.1: keep the DoSingleTick epoch, retire the low-ROI same-call partition,
# enforce literal zero-wait, and compact exact-Vanilla FightFires negatives inside S4.

$projPath='RimMT/Source/RimMT/RimMT.csproj'
$proj=Get-Content $projPath -Raw
$proj=Replace-OrThrow $proj '    <!-- T26: SingleCallCandidatePartition reactivated behind parallel.engineStage and >=8ms package-tail admission. -->' '    <Compile Remove="AI\SingleCallCandidatePartition.cs" />' 're-exclude candidate partition'
Set-Content $projPath $proj -Encoding UTF8

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t26-engine-parallel";' 'internal const string Version = "0.9.3-t26.1-zero-wait-fightfires";' 'version'
$boot=Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                SingleCallCandidatePartition.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                SimulationEpochCoordinator093T26.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
'@ 'remove partition install'
$boot=$boot.Replace('[RimMT] V0.9.3-T26 Engine Parallel initialized. T24.1 safety retained; DoSingleTick epoch + primitive-only bounded parallel stage active; T25 next-call async cache absent.','[RimMT] V0.9.3-T26.1 Zero-Wait + FightFires initialized. T26 epoch retained; low-ROI partition retired; simulation thread never waits for workers; FightFires negative compaction active only when authority-safe.')
Set-Content $bootPath $boot -Encoding UTF8

$runtimePath='RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
$runtime=[regex]::Replace($runtime,'(?m)^\s*SingleCallCandidatePartition\.MarkCompatibilityReady\(\);\r?\n','',1)
Set-Content $runtimePath $runtime -Encoding UTF8

# Literal zero-wait guard: no SpinWait/Wait/Join/ParallelFor in the same-call kernel.
$epochPath='RimMT/Source/RimMT/Scheduling/SimulationEpochCoordinator093T26.cs'
$epoch=Get-Content $epochPath -Raw
$a=$epoch.IndexOf('        internal static bool TryComputeRingKeys(',[System.StringComparison]::Ordinal)
$b=$epoch.IndexOf('        internal static string Summary()',$a,[System.StringComparison]::Ordinal)
if($a -lt 0 -or $b -lt 0){ throw 'T26.1 cannot isolate TryComputeRingKeys' }
$newKernel=@'
        internal static bool TryComputeRingKeys(int rootX, int rootZ, int ringSize, int[] xs, int[] zs, int[] ringKeys)
        {
            // T26.1 hard zero-wait guard. The measured same-call partition had 4 useful calls
            // after thousands of admissions/materializations, so it is retired from production.
            Interlocked.Increment(ref kernelAttempts);
            Interlocked.Increment(ref kernelRejected);
            return false;
        }

'@
$epoch=$epoch.Substring(0,$a)+$newKernel+$epoch.Substring($b)
$epoch=$epoch.Replace('T26 engine parallel simulation:','T26.1 engine epoch / zero-wait simulation:')
$epoch=$epoch.Replace('. DoSingleTick remains on the Unity main thread; workers receive primitive arrays only; timeout never blocks the simulation thread.','. DoSingleTick remains on the Unity main thread; active same-call worker consumer=OFF; production simulation never waits/spins/joins workers.')
Set-Content $epochPath $epoch -Encoding UTF8

# FightFires is internal to Assembly-CSharp. Existing S4 authority-safety reflection remains the gate.
$s4Path='RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4=Get-Content $s4Path -Raw
$s4=Replace-OrThrow $s4 @'
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
'@ @'
        private static long targetedFeedHemogenRejected;
        private static long targetedVisitSickRejected;
        private static long targetedFightFiresRejected;
        private static long targetedPrefilterAuthorityBypass;
'@ 'FightFires counter'

$s4=Replace-OrThrow $s4 @'
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            return TargetedPrefilterKind.None;
'@ @'
            if (scanner.def != null && scanner.def.defName == "VisitSickPawn" && type == typeof(WorkGiver_VisitSickPawn))
                return TargetedPrefilterKind.VisitSickPawn;
            if (type.FullName == "RimWorld.WorkGiver_FightFires")
                return TargetedPrefilterKind.FightFires;
            return TargetedPrefilterKind.None;
'@ 'FightFires resolver'

$s4=Replace-OrThrow $s4 @'
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
                        return true;
                    }

                    if (worker.WorkTagIsDisabled(WorkTags.Firefighting)) return false;
                    if (!worker.Map.areaManager.Home[fire.Position]) return false;
                    return true;
                }

                return true;
'@ 'FightFires cheap negatives'

$s4=Replace-OrThrow $s4 @'
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

$s4=Replace-OrThrow $s4 @'
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected + "]" +
'@ @'
                   ", feedHemogen=" + targetedFeedHemogenRejected + ", visitSick=" + targetedVisitSickRejected +
                   ", fightFires=" + targetedFightFiresRejected + "]" +
'@ 'FightFires summary'

$s4=Replace-OrThrow $s4 'private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn }' 'private enum TargetedPrefilterKind { None, HaulCorpses, TakeEntityToHoldingPlatform, FeedHemogen, VisitSickPawn, FightFires }' 'FightFires enum'
Set-Content $s4Path $s4 -Encoding UTF8

$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T26 Engine Parallel','V0.9.3-T26.1 Zero-Wait + FightFires')
$report=[regex]::Replace($report,'(?m)^\s*sb\.AppendLine\(SingleCallCandidatePartition\.Summary\(\)\);\r?\n','',1)
$report=$report.Replace('T26 adds DoSingleTick simulation epochs and a primitive-only bounded parallel stage. Large custom WorkGiver partitions are admitted only after 8ms package time; worker timeout immediately falls back to serial without waiting; T25 next-call async candidate cache/ThinkNode root attribution are absent;','T26 DoSingleTick simulation epoch retained; T26.1 retires the low-ROI same-call candidate partition, enforces literal zero-wait on the simulation thread, and adds authority-safe exact-Vanilla FightFires negative compaction inside S4; T25 next-call async candidate cache/ThinkNode root attribution are absent;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T26 Engine Parallel','V0.9.3-T26.1 Zero-Wait + FightFires')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T26.1: T26 epoch retained; candidate partition retired; same-call worker wait removed; FightFires authority-safe negative compaction enabled.'
