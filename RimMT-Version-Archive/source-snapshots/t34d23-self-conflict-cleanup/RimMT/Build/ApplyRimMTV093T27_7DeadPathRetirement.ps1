$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T27.7 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T27.7 Dead Path Retirement
# Production:
# - retire AggressiveReachabilityProfilesV17 Harmony installation after T27.6 proved >99.98% gate bypass and zero eligibility
# - allow only our measurement-only allen.rimmt.diagnostics Harmony owner in T4 authority census
# Diagnostics v0.4:
# - retire the duplicated v0.2 WorkGiver profiler; v0.3/0.4 SlowDNJCorrelation is the sole WorkGiver timer
# - widen per-DNJ method table and retain worst slow-DNJ records across the whole run

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.6-production-lean";' 'internal const string Version = "0.9.3-t27.7-dead-path-retirement";' 'version'
$boot=[regex]::Replace($boot,'(?m)^\s*AggressiveReachabilityProfilesV17\.Apply\(harmony\);\s*\r?\n','')
$boot=$boot.Replace(
  '[RimMT] V0.9.3-T27.6 Production Lean initialized. Legacy resident tail/Pawn/world diagnostic Harmony probes are not installed; dead DoBill worker-tail prefix retired; DoBill package-readiness experiment retired; diagnostics live in allen.rimmt.diagnostics; FullParallel hard-OFF.',
  '[RimMT] V0.9.3-T27.7 Dead Path Retirement initialized. ReachProfile production Harmony path retired after zero eligibility; T4 recognizes the measurement-only RimMT Diagnostics owner; legacy resident diagnostics remain external; FullParallel hard-OFF.')
Set-Content $bootPath $boot -Encoding UTF8

# T4 authority: diagnostics is our own observation-only owner. It must not disable the production
# optimization it is trying to measure. Unknown/third-party owners still fail open exactly as before.
$mergePath='RimMT/Source/RimMT/AI/WorkGiverMergePartnerIndex093T4.cs'
$merge=Get-Content $mergePath -Raw
$merge=Replace-OrThrow $merge @'
        private const string HarmonyOwner = "allen.rimmt";
'@ @'
        private const string HarmonyOwner = "allen.rimmt";
        private const string DiagnosticsHarmonyOwner = "allen.rimmt.diagnostics";
'@ 'T4 diagnostics owner constant'
$merge=Replace-OrThrow $merge @'
                if (patch == null) continue;
                if (!string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) return true;
'@ @'
                if (patch == null) continue;
                if (string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) continue;
                if (string.Equals(patch.owner, DiagnosticsHarmonyOwner, StringComparison.Ordinal)) continue;
                return true;
'@ 'T4 ignore measurement-only diagnostics owner'
Set-Content $mergePath $merge -Encoding UTF8

# Diagnostics v0.4: v0.2 WorkGiver aggregate timing is superseded by SlowDNJCorrelation.
$v02Path='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV02.cs'
$v02=Get-Content $v02Path -Raw
$v02=[regex]::Replace($v02,'(?m)^\s*PatchWorkGiverMethods\(harmony\);\s*\r?\n','')
$v02=Replace-OrThrow $v02 @'
            AppendTop(sb, "WorkGiverSampled", WorkGivers);
'@ @'
            sb.AppendLine("WorkGiverSampled=RETIRED in Diagnostics v0.4; SlowDNJCorrelation is the sole WorkGiver timing path.");
'@ 'v0.2 WorkGiver summary retirement'
Set-Content $v02Path $v02 -Encoding UTF8

# Diagnostics v0.4: preserve worst slow DNJ records, not just the most recent ring, and reduce <other>.
$v03Path='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsV03.cs'
$v03=Get-Content $v03Path -Raw
$v03=$v03.Replace('private const int MaxMethodsPerDetermine = 96;','private const int MaxMethodsPerDetermine = 256;')
$v03=$v03.Replace('private const int TopMethodsPerBurst = 12;','private const int TopMethodsPerBurst = 16;')
$v03=Replace-OrThrow $v03 @'
        private static readonly SlowDetermineRecord[] Recent = new SlowDetermineRecord[RecentCapacity];
'@ @'
        private static readonly SlowDetermineRecord[] Recent = new SlowDetermineRecord[RecentCapacity];
        private static readonly List<SlowDetermineRecord> Worst = new List<SlowDetermineRecord>(16);
'@ 'Worst storage'
$v03=Replace-OrThrow $v03 @'
            Recent[recentPos] = new SlowDetermineRecord(tick, pawn, totalUs, resultJob, source, top);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
'@ @'
            SlowDetermineRecord record = new SlowDetermineRecord(tick, pawn, totalUs, resultJob, source, top);
            Recent[recentPos] = record;
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
            AddWorst(record);
'@ 'Worst insertion'
$v03=Replace-OrThrow $v03 @'
            return sb.ToString();
        }

        internal static void Reset()
'@ @'
            sb.AppendLine("WorstSlowDNJ=");
            if (Worst.Count == 0) sb.AppendLine("none");
            else
            {
                for (int i = 0; i < Worst.Count; i++) AppendRecord(sb, Worst[i]);
            }
            return sb.ToString();
        }

        private static void AddWorst(SlowDetermineRecord record)
        {
            if (Worst.Count < 16)
            {
                Worst.Add(record);
                Worst.Sort((a,b) => b.TotalUs.CompareTo(a.TotalUs));
                return;
            }
            if (record.TotalUs <= Worst[Worst.Count - 1].TotalUs) return;
            Worst[Worst.Count - 1] = record;
            Worst.Sort((a,b) => b.TotalUs.CompareTo(a.TotalUs));
        }

        private static void AppendRecord(StringBuilder sb, SlowDetermineRecord e)
        {
            sb.Append(" - tick=").Append(e.Tick)
              .Append(", pawn=").Append(e.Pawn)
              .Append(", totalMs=").Append((e.TotalUs / 1000.0).ToString("F2"))
              .Append(", result=").Append(e.ResultJob)
              .Append(", source=").Append(e.Source)
              .Append(", top=").Append(string.IsNullOrEmpty(e.TopMethods) ? "none" : e.TopMethods)
              .AppendLine();
        }

        internal static void Reset()
'@ 'Worst output helpers'
$v03=Replace-OrThrow $v03 @'
            Array.Clear(Recent, 0, Recent.Length);
            recentPos = recentCount = 0;
'@ @'
            Array.Clear(Recent, 0, Recent.Length);
            Worst.Clear();
            recentPos = recentCount = 0;
'@ 'Worst reset'
Set-Content $v03Path $v03 -Encoding UTF8

$patchesPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$patches=Get-Content $patchesPath -Raw
$patches=Replace-OrThrow $patches 'internal const string Version = "0.3.0";' 'internal const string Version = "0.4.0";' 'Diagnostics version'
Set-Content $patchesPath $patches -Encoding UTF8

$reportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('            "RimMT.AggressiveReachabilityProfilesV17",' + "`r`n",'')
$report=$report.Replace('            "RimMT.AggressiveReachabilityProfilesV17",' + "`n",'')
Set-Content $reportPath $report -Encoding UTF8

$diagAboutPath='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAboutPath){
  $about=Get-Content $diagAboutPath -Raw
  $about=$about.Replace('RimMT Diagnostics v0.3','RimMT Diagnostics v0.4')
  Set-Content $diagAboutPath $about -Encoding UTF8
}
$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=$about.Replace('V0.9.3-T27.6 Production Lean','V0.9.3-T27.7 Dead Path Retirement')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T27.7 Dead Path Retirement + Diagnostics v0.4.'