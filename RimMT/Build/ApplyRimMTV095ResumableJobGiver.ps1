$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "RimMT V0.9.5 Resumable JobGiver transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V0.9.5:
# - keep V0.9.3 stable core, V0.9.4 tail telemetry and acceleration-only GenClosest cold sleep;
# - remove the V0.9.4 HaulMerge cheap-negative experiment (runtime 53k checks / 0 rejects);
# - add recurring-hot, main-thread resumable validator slicing for exact JobGiver_Work thing scanners;
# - final Reachability, validator and Job authority remain live; ReachProfile stays pinned to V0.4.18.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long haulMergePrefilterCalls;
        private static long haulMergePrefilterRejected;
        private static long haulMergeAuthorityBypass;
        private static long actualValidatorCalls;
'@ @'
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
'@ 'remove V0.9.4 HaulMerge counters'

$s4 = Replace-OrThrow $s4 @'
            if (HaulMergeCheapNegative094.IsCandidate(resolvedScanner) && kept > 0)
            {
                if (!HaulMergeCheapNegative094.IsAuthoritySafe(resolvedScanner))
                {
                    haulMergeAuthorityBypass++;
                }
                else
                {
                    int write = 0;
                    for (int i = 0; i < kept; i++)
                    {
                        Candidate candidate = candidates[i];
                        haulMergePrefilterCalls++;
                        if (!HaulMergeCheapNegative094.Pass(traverseParms.pawn, candidate.Thing))
                        {
                            haulMergePrefilterRejected++;
                            continue;
                        }
                        candidates[write++] = candidate;
                    }
                    kept = write;
                }
            }

'@ '' 'remove V0.9.4 HaulMerge candidate compaction'

$s4 = Replace-OrThrow $s4 @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected + haulMergePrefilterRejected) +
'@ @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) +
'@ 'remove HaulMerge from truthful prefilter total'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", haulMergePrefilterCalls=" + haulMergePrefilterCalls +
                   ", haulMergePrefilterRejected=" + haulMergePrefilterRejected +
                   ", haulMergeAuthorityBypass=" + haulMergeAuthorityBypass +
                   ", failures=" + failures +
'@ @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ 'remove HaulMerge summary fields'

Set-Content $s4Path $s4 -Encoding UTF8

# Physically exclude the retired helper from the public DLL after its call sites have been removed.
$projPath = 'RimMT/Source/RimMT/RimMT.csproj'
$proj = Get-Content $projPath -Raw
$proj = Replace-OrThrow $proj @'
    <Compile Remove="AI\SingleCallCandidatePartition.cs" />
'@ @'
    <Compile Remove="AI\SingleCallCandidatePartition.cs" />
    <Compile Remove="AI\HaulMergeCheapNegative094.cs" />
'@ 'exclude retired HaulMerge helper'
Set-Content $projPath $proj -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag '[RimMT] V0.9.4 Tail Focus on-demand report' '[RimMT] V0.9.5 Resumable JobGiver on-demand report' 'V0.9.5 report title'
$diag = Replace-OrThrow $diag @'
            sb.AppendLine(JobGiverTailTelemetry094.Summary());
            sb.AppendLine(LargeSetTailRescue092.Summary());
'@ @'
            sb.AppendLine(JobGiverTailTelemetry094.Summary());
            sb.AppendLine(ResumableJobGiver095.Summary());
            sb.AppendLine(LargeSetTailRescue092.Summary());
'@ 'V0.9.5 resumable report line'
$diag = Replace-OrThrow $diag 'V0.9.4 Tail Focus; JobGiver tail buckets=ON; HaulMerge cheap-negative=authority-safe; GenClosest cold=acceleration-only;' 'V0.9.5 Resumable JobGiver; JobGiver tail buckets=ON; recurring-hot validator slicing=ON; HaulMerge cheap-negative=OFF; GenClosest cold=acceleration-only;' 'V0.9.5 production policy marker'
Set-Content $diagPath $diag -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.4-tail-focus";' 'internal const string Version = "0.9.5-resumable-jobgiver";' 'V0.9.5 bootstrap version'
$boot = Replace-OrThrow $boot @'
                JobGiverSlowSearch0419S.Apply(harmony);
                JobGiverTailTelemetry094.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                JobGiverSlowSearch0419S.Apply(harmony);
                JobGiverTailTelemetry094.Apply(harmony);
                ResumableJobGiver095.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'V0.9.5 resumable bootstrap wiring'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.4 Tail Focus initialized.' '[RimMT] V0.9.5 Resumable JobGiver initialized.' 'V0.9.5 startup label'
Set-Content $bootPath $boot -Encoding UTF8

$settingsPath = 'RimMT/Source/RimMT/Settings/RimMTMod.cs'
$settings = Get-Content $settingsPath -Raw
$settings = Replace-OrThrow $settings 'RimMT V0.9.4 Tail Focus — single DLL production build' 'RimMT V0.9.5 Resumable JobGiver — single DLL production build' 'V0.9.5 settings label'
Set-Content $settingsPath $settings -Encoding UTF8

Write-Host 'Applied RimMT V0.9.5 Resumable JobGiver: recurring-hot main-thread validator slicing, priority-preserving suspension, live final Reachability/validator authority; HaulMerge experiment retired; ReachProfile remains V0.4.18.'
