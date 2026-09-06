$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T7 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T7 Carrier/Mech cheap-negative pruners
# - Keeps T6 HaulMerge behavior unchanged.
# - Adds only deterministic negatives for HaulToCarrier and HaulMechsToCharger inside the
#   existing S4 accelerated candidate loop after its 32ms tail threshold.
# - Any foreign Harmony patch on the exact vanilla HasJobOnThing disables that pruner.
# - Survivors still execute the original live validator, Reachability and Job construction.
# - Adds bounded slow-Determine WorkGiver evidence only inside existing T2 deep windows.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        [ThreadStatic] private static Candidate[] candidateScratch;
'@ @'
        [ThreadStatic] private static Candidate[] candidateScratch;
        [ThreadStatic] private static bool t7DetermineActive;
        [ThreadStatic] private static string t7DetermineTopWorkGiver;
        [ThreadStatic] private static int t7DetermineTopRejects;
'@ 'T7 bounded determine attribution state'

$s4 = Replace-OrThrow $s4 @'
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
            CarrierPrunerKind093T7 carrierPrunerKind;
            if (kept > 0 && CarrierMechCheapNegative093T7.TryPrepare(resolvedScanner, out carrierPrunerKind))
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    Candidate candidate = candidates[i];
                    if (CarrierMechCheapNegative093T7.Reject(carrierPrunerKind, traverseParms.pawn, candidate.Thing))
                        continue;
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ 'T7 carrier/mech candidate compaction before original validator'

$s4 = Replace-OrThrow $s4 @'
                string defName = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
                AddHeavyStat(HeavyWorkGivers, defName, rejects);
'@ @'
                string defName = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
                if (t7DetermineActive && rejects > t7DetermineTopRejects)
                {
                    t7DetermineTopRejects = rejects;
                    t7DetermineTopWorkGiver = defName;
                }
                AddHeavyStat(HeavyWorkGivers, defName, rejects);
'@ 'T7 retain most-rejecting heavy S4 WorkGiver inside sampled DetermineNextJob'

$s4 = Replace-OrThrow $s4 @'
        private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
'@ @'
        internal static void T7BeginDetermineAttribution()
        {
            t7DetermineActive = true;
            t7DetermineTopWorkGiver = null;
            t7DetermineTopRejects = 0;
        }

        internal static void T7EndDetermineAttribution(long startedTicks)
        {
            if (!t7DetermineActive) return;
            t7DetermineActive = false;
            if (startedTicks <= 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - startedTicks;
            if (elapsed <= 0L) return;
            double elapsedMs = elapsed * (1000.0 / Stopwatch.Frequency);
            if (elapsedMs < 20.0) return;
            CarrierMechCheapNegative093T7.RecordSlowDetermine(t7DetermineTopWorkGiver, t7DetermineTopRejects, elapsedMs);
        }

        private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
'@ 'T7 sampled DetermineNextJob attribution lifecycle'

Set-Content $s4Path $s4 -Encoding UTF8

$t2PatchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$t2Patch = Get-Content $t2PatchPath -Raw
$t2Patch = Replace-OrThrow $t2Patch @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(PhasePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
'@ @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(DeterminePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
'@ 'T7 use Determine-specific deep prefix'

$t2Patch = Replace-OrThrow $t2Patch @'
        public static void PawnPrefix(ref long __state)
'@ @'
        public static void DeterminePrefix(ref long __state)
        {
            if (!TailPawnAttribution093T2.DeepActive)
            {
                __state = 0L;
                return;
            }
            JobGiverSlowSearch0419S.T7BeginDetermineAttribution();
            __state = TailPawnAttribution093T2.BeginPhase();
        }

        public static void PawnPrefix(ref long __state)
'@ 'T7 bounded Determine attribution begin'

$t2Patch = Replace-OrThrow $t2Patch @'
        public static void DeterminePostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }
'@ @'
        public static void DeterminePostfix(long __state)
        {
            if (__state == 0L) return;
            JobGiverSlowSearch0419S.T7EndDetermineAttribution(__state);
            TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }
'@ 'T7 bounded Determine attribution end'
Set-Content $t2PatchPath $t2Patch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t6-commonsense-haulmerge-coexist";' 'internal const string Version = "0.9.3-t7-carrier-pruners";' 'T7 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T6 CommonSense HaulMerge Coexist initialized. Exact Common Sense monotonic CanStackWith postfix may coexist; all other foreign authority still fails open.' '[RimMT] V0.9.3-T7 Carrier Pruners initialized. T6 HaulMerge retained; S4-only authority-safe HaulToCarrier/HaulMechsToCharger cheap negatives enabled.' 'T7 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
'@ @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
            sb.AppendLine(CarrierMechCheapNegative093T7.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T7.SlowDetermineSummary());
'@ 'T7 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T6 CommonSense HaulMerge Coexist' 'V0.9.3-T7 Carrier Pruners' 'T7 report title'
$report = Replace-OrThrow $report 'T6 exact CommonSense monotonic-postfix coexistence; S4 early rescue=OFF;' 'T6 exact CommonSense monotonic-postfix coexistence; T7 S4-only carrier/mech cheap negatives + bounded slow-Determine WorkGiver evidence; S4 early rescue=OFF;' 'T7 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T6 CommonSense HaulMerge Coexist', 'V0.9.3-T7 Carrier Pruners')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T7: T6 HaulMerge retained; S4-only authority-safe HaulToCarrier/HaulMechsToCharger deterministic negative pruning plus bounded sampled slow-Determine WorkGiver evidence.'
