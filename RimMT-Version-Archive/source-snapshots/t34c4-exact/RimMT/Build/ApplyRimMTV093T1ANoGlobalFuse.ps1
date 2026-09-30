$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T1A transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T1A No Global Fuse
# Performance-first policy: remove ReachProfile global soft cooldown / probation / hard fuse.
# Per-slot mismatch quarantine remains fail-closed and Vanilla-authoritative for the affected slot only.
# Sampling/parity validation remains active. No global mismatch density can disable ReachProfile authority.

$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach = Get-Content $reachPath -Raw

$reach = Replace-OrThrow $reach @'
            UpdateRollingFuseMode();
            if (reachFuseMode == ReachFuseMode.Cooldown)
            {
                cooldownLiveBypass++;
                return true;
            }
            if (reachFuseMode == ReachFuseMode.HardFused)
                return true;

'@ '' 'remove global fuse gate from ReachProfile prefix'

$reach = Replace-OrThrow $reach @'
                int validated = Volatile.Read(ref slot.ValidatedMatches);
                int serial = Interlocked.Increment(ref slot.PredictionSerial);
                bool probation = reachFuseMode == ReachFuseMode.Probation;
                bool sample = probation || validated < WarmupSamples || (serial & SampleMask) == 0;
                if (sample)
                {
                    __state = new ReachSampleState(true, predicted, slot, profile.RegionGeneration);
                    Interlocked.Increment(ref shadowSamples);
                    if (probation) probationForcedShadow++;
                    return true;
                }
'@ @'
                int validated = Volatile.Read(ref slot.ValidatedMatches);
                int serial = Interlocked.Increment(ref slot.PredictionSerial);
                bool sample = validated < WarmupSamples || (serial & SampleMask) == 0;
                if (sample)
                {
                    __state = new ReachSampleState(true, predicted, slot, profile.RegionGeneration);
                    Interlocked.Increment(ref shadowSamples);
                    return true;
                }
'@ 'remove probation sampling mode'

# String.Replace removes every identical clean-path observation in one call.
$reach = Replace-OrThrow $reach '                ObserveRollingSample(false, __state.Slot);' '' 'remove clean global fuse observations'
$reach = Replace-OrThrow $reach '            ObserveRollingSample(true, __state.Slot);' '' 'remove mismatch global fuse observation'

$reach = Replace-OrThrow $reach @'
                ", rollingMode=" + reachFuseMode +
                ", rollingSamples=" + rollingSamples +
                ", rollingMismatches=" + rollingMismatches +
                ", globalWindow=" + globalWindowMismatches + "/" + globalWindowCount +
                ", globalDistinctMismatchSlots=" + GlobalMismatchSlotCounts.Count +
                ", localOnlyFuseDeferrals=" + Interlocked.Read(ref localOnlyFuseDeferrals) +
                ", emergencyWindow=" + emergencyWindowMismatches + "/" + emergencyWindowCount +
                ", softFuses=" + softFuses +
                ", cooldownUntilFrame=" + cooldownUntilFrame +
                ", cooldownLiveBypass=" + cooldownLiveBypass +
                ", probationRemaining=" + probationRemaining +
                ", probationMatches=" + probationMatches +
                ", probationForcedShadow=" + probationForcedShadow +
                ", probationPasses=" + probationPasses +
                ", probationFailures=" + probationFailures +
                ", hardFuses=" + hardFuses +
'@ @'
                ", globalFuse=OFF" +
                ", localSlotPolicy=quarantine-only" +
'@ 'replace global fuse summary with explicit OFF policy'

$reach = Replace-OrThrow $reach 'Aggressive reachability profile V0.4.18 sliced/local-first/lease:' 'Aggressive reachability profile V0.4.18-T1A sliced/local-first/lease/no-global-fuse:' 'ReachProfile summary label'
$reach = Replace-OrThrow $reach '[RimMT] parallel.reachProfile V0.4.18 installed: topology capture is frame-sliced with finer budget checks; expired profiles require four clean forced-live lease probes; mismatch handling remains local-slot-first and emergency hard fuse remains 16/256.' '[RimMT] parallel.reachProfile V0.4.18-T1A installed: topology capture remains frame-sliced; expired profiles still require four clean forced-live lease probes; global soft/probation/hard fuse is disabled; mismatch handling is local-slot quarantine only.' 'ReachProfile install log no-fuse policy'

Set-Content $reachPath $reach -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag 'V0.9.3-T1 Tail Attribution' 'V0.9.3-T1A Tail Attribution No Global Fuse' 'T1A diagnostics title'
$diag = Replace-OrThrow $diag 'ReachProfile=V0.4.18 sliced topology + 4-probe lease + local-first fuse;' 'ReachProfile=V0.4.18-T1A sliced topology + 4-probe lease + local-slot quarantine; global fuse=OFF;' 'T1A production policy'
Set-Content $diagPath $diag -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t1-tail-attribution";' 'internal const string Version = "0.9.3-t1a-no-global-fuse";' 'T1A bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T1 Tail Attribution initialized.' '[RimMT] V0.9.3-T1A Tail Attribution No Global Fuse initialized.' 'T1A bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T1 Tail Attribution', 'V0.9.3-T1A Tail Attribution No Global Fuse')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T1A: ReachProfile global soft/probation/hard fuse disabled; local mismatch quarantine and Vanilla fallback preserved.'