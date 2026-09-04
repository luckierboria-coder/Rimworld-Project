$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "RimMT V0.9.4 Tail Focus transform anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

# V0.9.4 Tail Focus:
# 1) keep V0.9.3 consolidated stable core and ReachProfile V0.4.18 unchanged;
# 2) add authority-safe HaulMerge cheap-negative compaction inside the existing S4 rescue only;
# 3) make Persistent GenClosest cold sleep depend on actual acceleration, not mere membership refresh/hit;
# 4) lightweight JobGiver tail timing lives in JobGiverTailTelemetry094.cs and is wired separately.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long actualValidatorCalls;
'@ @'
        private static long targetedVisitSickRejected;
        private static long targetedPrefilterAuthorityBypass;
        private static long haulMergePrefilterCalls;
        private static long haulMergePrefilterRejected;
        private static long haulMergeAuthorityBypass;
        private static long actualValidatorCalls;
'@ 'V0.9.4 HaulMerge counters'

$s4 = Replace-OrThrow $s4 @'
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
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

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ 'V0.9.4 HaulMerge candidate compaction'

$s4 = Replace-OrThrow $s4 @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected) +
'@ @'
                   ", prefilterRejected=" + (penPrefilterRejected + targetedPrefilterRejected + haulMergePrefilterRejected) +
'@ 'V0.9.4 truthful total prefilter count'

$s4 = Replace-OrThrow $s4 @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", failures=" + failures +
'@ @'
                   ", targetedAuthorityBypass=" + targetedPrefilterAuthorityBypass +
                   ", haulMergePrefilterCalls=" + haulMergePrefilterCalls +
                   ", haulMergePrefilterRejected=" + haulMergePrefilterRejected +
                   ", haulMergeAuthorityBypass=" + haulMergeAuthorityBypass +
                   ", failures=" + failures +
'@ 'V0.9.4 HaulMerge telemetry summary'

Set-Content $s4Path $s4 -Encoding UTF8

$genPath = 'RimMT/Source/RimMT/AI/AdaptiveGenClosestAssist.cs'
$gen = Get-Content $genPath -Raw

$gen = Replace-OrThrow $gen @'
        private const long ColdAfterObservedWithoutUseful = 50000;
        private const int ColdProbeMask = 255; // 1/256 calls while cold.
'@ @'
        private const long ColdAfterObservedWithoutUseful = 16384;
        private const int ColdProbeMask = 511; // 1/512 calls while cold; only actual acceleration wakes it.
'@ 'V0.9.4 more aggressive GenClosest cold cadence'

$gen = Replace-OrThrow $gen @'
                MarkUseful(observedNow);
                Interlocked.Increment(ref fallbackCalls);
                return true;
'@ @'
                Interlocked.Increment(ref fallbackCalls);
                return true;
'@ 'V0.9.4 membership registration is not useful yield'

$gen = Replace-OrThrow $gen @'
            Interlocked.Increment(ref membershipHits);
            MarkUseful(observedNow);

            PersistentMapSearchFabric.SourceSnapshot snapshot;
'@ @'
            Interlocked.Increment(ref membershipHits);

            PersistentMapSearchFabric.SourceSnapshot snapshot;
'@ 'V0.9.4 membership hit is not useful yield'

$gen = Replace-OrThrow $gen @'
                Log.Message("[RimMT] parallel.jobPartition V0.4.14 persistent-fabric consumer installed with zero-yield cold sleep. After 50k calls without a useful static source/acceleration it probes 1/256 calls until useful work reappears.");
'@ @'
                Log.Message("[RimMT] parallel.jobPartition V0.4.14 persistent-fabric consumer installed with V0.9.4 acceleration-only cold sleep. After 16384 calls without an actual acceleration it probes 1/512 calls until acceleration reappears.");
'@ 'V0.9.4 GenClosest startup label'

$gen = Replace-OrThrow $gen @'
                ", coldMode=" + (Volatile.Read(ref coldModeValue) != 0) +
'@ @'
                ", coldMode=" + (Volatile.Read(ref coldModeValue) != 0) +
                ", yieldPolicy=acceleration-only" +
'@ 'V0.9.4 GenClosest yield policy telemetry'

Set-Content $genPath $gen -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'V0.9.3 Consolidated Stable;' 'V0.9.4 Tail Focus; JobGiver tail buckets=ON; HaulMerge cheap-negative=authority-safe; GenClosest cold=acceleration-only;' 'V0.9.4 production policy marker'
Set-Content $diagPath $diag -Encoding UTF8

Write-Host 'Applied RimMT V0.9.4 Tail Focus: lightweight JobGiver tail attribution + authority-safe HaulMerge pruning + acceleration-only GenClosest cold sleep; ReachProfile remains V0.4.18.'
