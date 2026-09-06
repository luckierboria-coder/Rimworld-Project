$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T4 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T4 HaulMerge Partner Index
# T3 evidence showed that moving proven-heavy S4 admission from 32ms to 8ms did not remove the
# expensive live validator work. Restore generic S4 admission to the V0.9.3 32ms behavior.
# T4 instead patches WorkGiver_Merge.JobOnThing with a package-local necessary-condition index:
# impossible merge candidates return null before HaulAI/reservation/full storage-group scans;
# every survivor still runs the original JobOnThing.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
            if (__7 != null)
            {
                long elapsedScope = Stopwatch.GetTimestamp() - scopeStart;
                if (elapsedScope < TailRescueThresholdTicks)
                {
                    if (elapsedScope < EarlyKnownHeavyThresholdTicks || !IsKnownHeavyWorkGiver(__6)) return true;
                    earlyKnownCustomAdmissions++;
                }
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ @'
            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ 'restore T2 custom 32ms admission'

$s4 = Replace-OrThrow $s4 @'
            if (count < TailMinSourceCount) return true;
            long elapsedSmallList = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsedSmallList < TailRescueThresholdTicks)
            {
                if (elapsedSmallList < EarlyKnownHeavyThresholdTicks || !IsKnownHeavyWorkGiver(__6)) return true;
                earlyKnownListAdmissions++;
            }

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ @'
            if (count < TailMinSourceCount) return true;
            if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ 'restore T2 list 32ms admission'

$s4 = Replace-OrThrow $s4 @'
                   ", earlyKnownPolicy=" + EarlyKnownHeavyThresholdMs + "ms/" + EarlyKnownHeavyMinCalls + "calls/" + EarlyKnownHeavyMinRejects + "rejects" +
'@ @'
                   ", earlyKnownPolicy=OFF" +
'@ 'T4 early-rescue summary disabled'
Set-Content $s4Path $s4 -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPathfinderPatches093T3.Apply(harmony);
'@ @'
            TailPathfinderPatches093T3.Apply(harmony);
            WorkGiverMergePartnerIndex093T4.Apply(harmony);
'@ 'install T4 HaulMerge partner index'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t3-heavy-workgiver-early-rescue";' 'internal const string Version = "0.9.3-t4-haulmerge-partner-index";' 'T4 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T3 Heavy WorkGiver Early Rescue initialized.' '[RimMT] V0.9.3-T4 HaulMerge Partner Index initialized.' 'T4 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
'@ @'
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
'@ 'T4 report line'
$report = Replace-OrThrow $report 'V0.9.3-T3 Heavy WorkGiver Early Rescue' 'V0.9.3-T4 HaulMerge Partner Index' 'T4 report title'
$report = Replace-OrThrow $report 'T0 histogram + T1 top-level + T2 bounded Pawn/AI + T3 bounded FindPath attribution; S4 proven-heavy early rescue=8ms;' 'T0 histogram + T1 top-level + T2 bounded Pawn/AI + T3 bounded FindPath attribution; T4 package-local HaulMerge partner index; S4 early rescue=OFF;' 'T4 policy marker'
$report = Replace-OrThrow $report 'S4 tail=32ms + proven-heavy early rescue=8ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'S4 tail=32ms + proven-heavy early rescue=OFF + true-validator attribution + authority-safe corpse/holding/feed/visit pruners; HaulMerge partner-index negative proof active;' 'T4 S4 policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T3 Heavy WorkGiver Early Rescue', 'V0.9.3-T4 HaulMerge Partner Index')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T4: T3 8ms early admission reverted; package-local authority-safe HaulMerge partner index enabled; survivors remain Vanilla JobOnThing.'
