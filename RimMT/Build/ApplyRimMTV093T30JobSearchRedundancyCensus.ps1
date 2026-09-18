$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T30 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T30 Job Search Redundancy Census
# Pure measurement on top of T28:
# - no new RimWorld Harmony target
# - no WorkGiver/mod whitelist or named special case
# - every eighth outer package only, and only when RimMT.Diagnostics is loaded
# - records structural repetition already visible to T20/T21/T22/S4/GlobalNearest
# - no gameplay result or source membership is cached

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t28-unified-job-search-transaction";' 'internal const string Version = "0.9.3-t30-job-search-redundancy-census";' 'bootstrap version'
$boot=$boot.Replace('[RimMT] V0.9.3-T28 Unified Job Search Transaction initialized.',
                    '[RimMT] V0.9.3-T30 Job Search Redundancy Census initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# T28 owns the one package boundary; T30 piggybacks on that lifecycle without adding Harmony.
$ctxPath='RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs'
$ctx=Get-Content $ctxPath -Raw
$ctx=Replace-OrThrow $ctx @'
                current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
                __state.Shared = current;
                Interlocked.Increment(ref packages);
'@ @'
                current = new PackageContext(__0, generation, Stopwatch.GetTimestamp());
                __state.Shared = current;
                Interlocked.Increment(ref packages);
                JobSearchRedundancyCensus093T30.BeginPackage(__0, generation);
'@ 'T30 package begin'
$ctx=Replace-OrThrow $ctx @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                current = null;
                depth = 0;
'@ @'
                if (!ReferenceEquals(current, __state.Shared))
                    Interlocked.Increment(ref mismatchedExits);
                JobSearchRedundancyCensus093T30.EndPackage();
                current = null;
                depth = 0;
'@ 'T30 package end'
Set-Content $ctxPath $ctx -Encoding UTF8

# T20/T21 already see the exact validator and reach keys. T30 observes those identities only.
$t20Path='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw
$t20=Replace-OrThrow $t20 @'
            ValidatorKey key = new ValidatorKey(__originalMethod, scanner, thing);
            ValidatorNegativeEntry entry;
'@ @'
            ValidatorKey key = new ValidatorKey(__originalMethod, scanner, thing);
            if (JobSearchRedundancyCensus093T30.Sampling)
                JobSearchRedundancyCensus093T30.RecordValidator(key);
            ValidatorNegativeEntry entry;
'@ 'T30 validator census hook'
$reachKeyPattern='(?m)^(\s*ReachKey key = new ReachKey\([^\r\n]+\);\r?\n)'
if(-not [regex]::IsMatch($t20,$reachKeyPattern)){ throw 'T30 anchor missing: generated ReachKey construction' }
$t20=[regex]::Replace(
    $t20,
    $reachKeyPattern,
    [System.Text.RegularExpressions.MatchEvaluator]{
      param($m)
      $m.Groups[1].Value +
      '            if (JobSearchRedundancyCensus093T30.Sampling)' + [Environment]::NewLine +
      '                JobSearchRedundancyCensus093T30.RecordReach(key);' + [Environment]::NewLine
    },
    1)
Set-Content $t20Path $t20 -Encoding UTF8

# T22 observes repeated IList Global_NewTemp sources. Record identity/center before its own size gate.
$t22Path='RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$t22=Get-Content $t22Path -Raw
$t22=Replace-OrThrow $t22 @'
            int count = list.Count;
            if (count < MinSourceCount || count > MaxSourceCount)
'@ @'
            int count = list.Count;
            if (JobSearchRedundancyCensus093T30.Sampling)
                JobSearchRedundancyCensus093T30.RecordSource(
                    searchSet, center,
                    JobSearchRedundancyCensus093T30.SourceRoute.GlobalNewTemp,
                    count);
            if (count < MinSourceCount || count > MaxSourceCount)
'@ 'T30 T22 source census hook'
Set-Content $t22Path $t22 -Encoding UTF8

# GlobalNearest already owns Global / Global_Reachable source hooks; add route identity only.
$globalPath='RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs'
$global=Get-Content $globalPath -Raw
$global=Replace-OrThrow $global '                TryReorder(__args, 0, 1, 2, 4);' '                TryReorder(__args, 0, 1, 2, 4, JobSearchRedundancyCensus093T30.SourceRoute.Global);' 'Global route'
$global=Replace-OrThrow $global '                TryReorder(__args, 0, 2, 5, 7);' '                TryReorder(__args, 0, 2, 5, 7, JobSearchRedundancyCensus093T30.SourceRoute.GlobalReachable);' 'GlobalReachable route'
$global=Replace-OrThrow $global @'
        private static void TryReorder(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex)
'@ @'
        private static void TryReorder(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex,
            JobSearchRedundancyCensus093T30.SourceRoute censusRoute)
'@ 'TryReorder census route parameter'
$global=Replace-OrThrow $global @'
            if (float.IsNaN(maxDistance) || maxDistance < 0f) return;

            PackageContext context = current;
'@ @'
            if (float.IsNaN(maxDistance) || maxDistance < 0f) return;

            if (JobSearchRedundancyCensus093T30.Sampling)
                JobSearchRedundancyCensus093T30.RecordSource(source, center, censusRoute, count);

            PackageContext context = current;
'@ 'GlobalNearest source census'
Set-Content $globalPath $global -Encoding UTF8

# S4 sees ClosestThingReachable source identities. Never enumerate a custom source for T30.
$s4Path='RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4=Get-Content $s4Path -Raw
$s4=Replace-OrThrow $s4 @'
            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
'@ @'
            if (__7 != null)
            {
                if (JobSearchRedundancyCensus093T30.Sampling)
                    JobSearchRedundancyCensus093T30.RecordSource(
                        __7, __0,
                        JobSearchRedundancyCensus093T30.SourceRoute.ClosestReachableCustom,
                        -1);
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
'@ 'S4 custom source census'
$s4=Replace-OrThrow $s4 @'
            int count = source.Count;
            if (count > MaxSourceCount) return true;
'@ @'
            int count = source.Count;
            if (JobSearchRedundancyCensus093T30.Sampling)
                JobSearchRedundancyCensus093T30.RecordSource(
                    source, __0,
                    JobSearchRedundancyCensus093T30.SourceRoute.ClosestReachableLister,
                    count);
            if (count > MaxSourceCount) return true;
'@ 'S4 lister source census'
Set-Content $s4Path $s4 -Encoding UTF8

# Production report.
$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T30 Job Search Redundancy Census')
$reportAnchor='            sb.AppendLine(JobSearchPackageContext093T28.Summary());'
if(-not $report.Contains($reportAnchor)){ throw 'T30 anchor missing: production T28 summary' }
$report=$report.Replace($reportAnchor,
    $reportAnchor + [Environment]::NewLine + '            sb.AppendLine(JobSearchRedundancyCensus093T30.Summary());')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T28 Unified Job Search Transaction','V0.9.3-T30 Job Search Redundancy Census')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T30 for RimWorld 1.5. T28 unified Job Search transactions remain unchanged. With the optional Diagnostics companion loaded, T30 samples one in eight JobGiver_Work packages and measures structural repetition already visible to existing T20/T21/T22/S4/GlobalNearest hooks: validator Thing/scanner revisits, Reachability target/query-shape revisits, and search-source reuse across centers/routes. T30 adds no new RimWorld Harmony target, no WorkGiver/mod special case, and never caches gameplay results or source membership.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

# Diagnostics v0.8 surfaces T30 through reflection.
$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.6.0";' 'internal const string Version = "0.8.0";' 'Diagnostics v0.8 version'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchTransaction093T20",
'@ @'
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchRedundancyCensus093T30",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T30 reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.6','RimMT Diagnostics v0.8')
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional measurement-only diagnostics companion for RimMT T30. v0.8 enables and surfaces the sampled Job Search redundancy census alongside the existing T28 transaction/parity counters and bounded tail attribution.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T30 Job Search Redundancy Census + Diagnostics v0.8.'
