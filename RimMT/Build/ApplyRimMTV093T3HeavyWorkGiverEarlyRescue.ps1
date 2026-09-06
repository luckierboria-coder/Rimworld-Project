$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T3 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T3 Heavy WorkGiver Early Rescue
# Tail-first behavior change, deliberately narrow:
# - Generic S4 rescue remains at 32ms.
# - A WorkGiver must first prove itself heavy through >=2 prior heavy S4 calls and >=512
#   TRUE original-validator rejects before later calls may enter the same S4 path at 8ms.
# - Survivors still execute original validator + live CanReach; final Vanilla Job creation is unchanged.
# - No continuation, no source-size prediction, no worker wait.
# T3 also wires bounded FindPath timing into T2 deep windows; its source files are committed directly.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private const int TailRescueThresholdMs = 32;
        private const int MaxSourceCount = 16384;
'@ @'
        private const int TailRescueThresholdMs = 32;
        private const int EarlyKnownHeavyThresholdMs = 8;
        private const long EarlyKnownHeavyMinCalls = 2;
        private const long EarlyKnownHeavyMinRejects = 512;
        private const int MaxSourceCount = 16384;
'@ 'S4 T3 early-rescue constants'

$s4 = Replace-OrThrow $s4 @'
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);
'@ @'
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);
        private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyKnownHeavyThresholdMs / 1000L);
'@ 'S4 T3 early-rescue tick budget'

$s4 = Replace-OrThrow $s4 @'
        private static long heavyWorkGiverUnresolved;
'@ @'
        private static long heavyWorkGiverUnresolved;
        private static long earlyKnownChecks;
        private static long earlyKnownHits;
        private static long earlyKnownListAdmissions;
        private static long earlyKnownCustomAdmissions;
'@ 'S4 T3 early-rescue counters'

$s4 = Replace-OrThrow $s4 @'
            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ @'
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
'@ 'S4 T3 custom early rescue'

$s4 = Replace-OrThrow $s4 @'
            if (count < TailMinSourceCount) return true;
            if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ @'
            if (count < TailMinSourceCount) return true;
            long elapsedSmallList = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsedSmallList < TailRescueThresholdTicks)
            {
                if (elapsedSmallList < EarlyKnownHeavyThresholdTicks || !IsKnownHeavyWorkGiver(__6)) return true;
                earlyKnownListAdmissions++;
            }

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ 'S4 T3 list early rescue'

$s4 = Replace-OrThrow $s4 @'
        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
'@ @'
        private static bool IsKnownHeavyWorkGiver(Predicate<Thing> validator)
        {
            earlyKnownChecks++;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            if (scanner == null) return false;

            string key = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                ? scanner.GetType().FullName
                : scanner.def.defName;
            if (string.IsNullOrEmpty(key)) return false;

            HeavyValidatorStats stats;
            if (!HeavyWorkGivers.TryGetValue(key, out stats) || stats == null) return false;
            if (stats.Calls < EarlyKnownHeavyMinCalls || stats.Rejects < EarlyKnownHeavyMinRejects) return false;
            earlyKnownHits++;
            return true;
        }

        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
'@ 'S4 T3 known-heavy resolver'

$s4 = Replace-OrThrow $s4 @'
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
'@ @'
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", earlyKnownChecks=" + earlyKnownChecks +
                   ", earlyKnownHits=" + earlyKnownHits +
                   ", earlyKnownAdmissions=" + (earlyKnownListAdmissions + earlyKnownCustomAdmissions) +
                   " [list=" + earlyKnownListAdmissions + ", custom=" + earlyKnownCustomAdmissions + "]" +
                   ", earlyKnownPolicy=" + EarlyKnownHeavyThresholdMs + "ms/" + EarlyKnownHeavyMinCalls + "calls/" + EarlyKnownHeavyMinRejects + "rejects" +
'@ 'S4 T3 summary counters'

Set-Content $s4Path $s4 -Encoding UTF8

$t2Path = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$t2 = Get-Content $t2Path -Raw
$t2 = Replace-OrThrow $t2 @'
            deepActive = periodic || burst;
            if (!deepActive) return;

            deepTicks++;
'@ @'
            deepActive = periodic || burst;
            if (!deepActive) return;

            TailPathfinderAttribution093T3.BeginTick();
            deepTicks++;
'@ 'T3 FindPath begin deep window'
$t2 = Replace-OrThrow $t2 @'
        internal static void EndTick(long totalUs)
        {
            bool wasDeep = deepActive;
            deepActive = false;
'@ @'
        internal static void EndTick(long totalUs)
        {
            TailPathfinderAttribution093T3.EndTick(totalUs);
            bool wasDeep = deepActive;
            deepActive = false;
'@ 'T3 FindPath end deep window'
Set-Content $t2Path $t2 -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPawnPatches093T2.Apply(harmony);
'@ @'
            TailPawnPatches093T2.Apply(harmony);
            TailPathfinderPatches093T3.Apply(harmony);
'@ 'install T3 FindPath attribution patches'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t2-pawn-tail-attribution";' 'internal const string Version = "0.9.3-t3-heavy-workgiver-early-rescue";' 'T3 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T2 Pawn Tail Attribution initialized.' '[RimMT] V0.9.3-T3 Heavy WorkGiver Early Rescue initialized.' 'T3 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
'@ @'
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
'@ 'T3 FindPath report lines'
$report = Replace-OrThrow $report 'V0.9.3-T2 Pawn Tail Attribution' 'V0.9.3-T3 Heavy WorkGiver Early Rescue' 'T3 report title'
$report = Replace-OrThrow $report 'T0 histogram + T1 top-level phase/GC attribution + T2 bounded Pawn/AI deep attribution;' 'T0 histogram + T1 top-level + T2 bounded Pawn/AI + T3 bounded FindPath attribution; S4 proven-heavy early rescue=8ms;' 'T3 policy marker'
$report = Replace-OrThrow $report 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'S4 tail=32ms + proven-heavy early rescue=8ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'T3 S4 policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T2 Pawn Tail Attribution', 'V0.9.3-T3 Heavy WorkGiver Early Rescue')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T3: proven-heavy WorkGivers may use existing S4 rescue from 8ms after observed evidence; bounded FindPath attribution wired into T2 deep windows.'