$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T8 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T8 T5 Carrier Pruners
# Clean rebase directly on T5. T6/T7 HaulMerge CommonSense coexistence is intentionally absent.
# Adds only deterministic negatives for HaulToCarrier and HaulMechsToCharger inside the
# existing S4 accelerated candidate loop after its 32ms tail threshold.
# Any foreign Harmony patch on exact HasJobOnThing OR JobOnThing disables that exact pruner.
# Survivors still execute original live validator, Reachability and Job construction.
# Adds bounded slow-Determine WorkGiver evidence only inside existing T2 deep windows.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        [ThreadStatic] private static Candidate[] candidateScratch;
'@ @'
        [ThreadStatic] private static Candidate[] candidateScratch;
        [ThreadStatic] private static bool t8DetermineActive;
        [ThreadStatic] private static string t8DetermineTopWorkGiver;
        [ThreadStatic] private static int t8DetermineTopRejects;
'@ 'T8 bounded determine attribution state'

$s4 = Replace-OrThrow $s4 @'
            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ @'
            CarrierPrunerKind093T8 carrierPrunerKind;
            if (kept > 0 && CarrierMechCheapNegative093T8.TryPrepare(resolvedScanner, out carrierPrunerKind))
            {
                int write = 0;
                for (int i = 0; i < kept; i++)
                {
                    Candidate candidate = candidates[i];
                    if (CarrierMechCheapNegative093T8.Reject(carrierPrunerKind, traverseParms.pawn, candidate.Thing))
                        continue;
                    candidates[write++] = candidate;
                }
                kept = write;
            }

            if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
            for (int i = 0; i < kept; i++)
'@ 'T8 carrier/mech candidate compaction before original validator'

$s4 = Replace-OrThrow $s4 @'
                string defName = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
                AddHeavyStat(HeavyWorkGivers, defName, rejects);
'@ @'
                string defName = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                    ? scanner.GetType().FullName
                    : scanner.def.defName;
                if (t8DetermineActive && rejects > t8DetermineTopRejects)
                {
                    t8DetermineTopRejects = rejects;
                    t8DetermineTopWorkGiver = defName;
                }
                AddHeavyStat(HeavyWorkGivers, defName, rejects);
'@ 'T8 retain most-rejecting heavy S4 WorkGiver inside sampled DetermineNextJob'

$s4 = Replace-OrThrow $s4 @'
        private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
'@ @'
        internal static void T8BeginDetermineAttribution()
        {
            t8DetermineActive = true;
            t8DetermineTopWorkGiver = null;
            t8DetermineTopRejects = 0;
        }

        internal static void T8EndDetermineAttribution(long startedTicks)
        {
            if (!t8DetermineActive) return;
            t8DetermineActive = false;
            if (startedTicks <= 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - startedTicks;
            if (elapsed <= 0L) return;
            double elapsedMs = elapsed * (1000.0 / Stopwatch.Frequency);
            if (elapsedMs < 20.0) return;
            CarrierMechCheapNegative093T8.RecordSlowDetermine(t8DetermineTopWorkGiver, t8DetermineTopRejects, elapsedMs);
        }

        private static void AddHeavyStat(Dictionary<string, HeavyValidatorStats> table, string key, int rejects)
'@ 'T8 sampled DetermineNextJob attribution lifecycle'

Set-Content $s4Path $s4 -Encoding UTF8

$t2PatchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$t2Patch = Get-Content $t2PatchPath -Raw
$t2Patch = Replace-OrThrow $t2Patch @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(PhasePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
'@ @'
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(DeterminePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
'@ 'T8 use Determine-specific deep prefix'

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
            JobGiverSlowSearch0419S.T8BeginDetermineAttribution();
            __state = TailPawnAttribution093T2.BeginPhase();
        }

        public static void PawnPrefix(ref long __state)
'@ 'T8 bounded Determine attribution begin'

$t2Patch = Replace-OrThrow $t2Patch @'
        public static void DeterminePostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }
'@ @'
        public static void DeterminePostfix(long __state)
        {
            if (__state == 0L) return;
            JobGiverSlowSearch0419S.T8EndDetermineAttribution(__state);
            TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }
'@ 'T8 bounded Determine attribution end'
Set-Content $t2PatchPath $t2Patch -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t5-haulmerge-patch-census";' 'internal const string Version = "0.9.3-t8-t5-carrier-pruners";' 'T8 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T5 HaulMerge Patch Census initialized. T4 behavior unchanged; census is measurement-only.' '[RimMT] V0.9.3-T8 T5 Carrier Pruners initialized. T5 baseline retained; T6 HaulMerge CommonSense coexistence is absent; S4-only authority-safe carrier/mech cheap negatives enabled.' 'T8 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
'@ @'
            sb.AppendLine(WorkGiverMergePartnerIndex093T4.Summary());
            sb.AppendLine(HaulMergePatchCensus093T5.DetailedSummary());
            sb.AppendLine(CarrierMechCheapNegative093T8.Summary());
            sb.AppendLine(CarrierMechCheapNegative093T8.SlowDetermineSummary());
'@ 'T8 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T5 HaulMerge Patch Census' 'V0.9.3-T8 T5 Carrier Pruners' 'T8 report title'
$report = Replace-OrThrow $report 'T5 Harmony authority census=measurement-only; S4 early rescue=OFF;' 'T5 Harmony authority census=measurement-only; T8 clean T5 rebase with S4-only carrier/mech cheap negatives + bounded slow-Determine WorkGiver evidence; T6 HaulMerge coexistence=ABSENT; S4 early rescue=OFF;' 'T8 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T5 HaulMerge Patch Census', 'V0.9.3-T8 T5 Carrier Pruners')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T8: clean T5 rebase; no T6 HaulMerge coexistence; S4-only authority-safe HaulToCarrier/HaulMechsToCharger deterministic negative pruning plus bounded sampled slow-Determine WorkGiver evidence.'
