$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T28 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

# T28 Unified Job Search Transaction
# - one TryIssueJobPackage Harmony lifecycle owner
# - T20/T21, T22 and GlobalNearest keep proven semantics but are coordinated by T28
# - DoBill false-only readiness proof moves into the shared package context
# - dead persistent-fabric GenClosest consumer is retired; no zero-yield worker/index churn
# - diagnostics v0.6 surfaces T28 and removes stale T27.4 labels

$bootPath='RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t27.8-job-search-foundation";' 'internal const string Version = "0.9.3-t28-unified-job-search-transaction";' 'bootstrap version'
$boot=Replace-OrThrow $boot @'
                AdaptiveGenClosestAssist.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
'@ @'
                // T28 owns the single synchronous JobGiver_Work package boundary.
                // The old persistent-fabric GenClosest consumer is retired after repeated
                // runtime evidence of zero accelerations; Vanilla/Broad/T22 remain authoritative.
                JobSearchPackageContext093T28.Apply(harmony);
                BroadGenClosestOrder0418.Apply(harmony);
                JobGiverGlobalNearest04181.Apply(harmony);
'@ 'bootstrap T28 package boundary / dead fabric consumer'
$boot=$boot.Replace('[RimMT] V0.9.3-T27.8 Job Search Foundation initialized.',
                    '[RimMT] V0.9.3-T28 Unified Job Search Transaction initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# T20/T21: stop installing a second package wrapper. T28 invokes the existing
# PackagePrefix/PackageFinalizer directly, preserving the validated internal state machine.
$t20Path='RimMT/Source/RimMT/AI/JobSearchTransaction093T20.cs'
$t20=Get-Content $t20Path -Raw
$oldT20=@'
                MethodBase package = AccessTools.Method(
                    typeof(JobGiver_Work),
                    "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package != null)
                {
                    harmony.Patch(package,
                        prefix: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(PackagePrefix))
                        { priority = Priority.First + 300 },
                        finalizer: new HarmonyMethod(typeof(JobSearchTransaction093T20), nameof(PackageFinalizer))
                        { priority = Priority.Last - 300 });
                    packagePatched = true;
                }

'@
$t20=Replace-OrThrow $t20 $oldT20 @'
                packagePatched = JobSearchPackageContext093T28.Installed;

'@ 'T20 package Harmony ownership'
$t20=$t20.Replace('T20 Foundation transaction core installed=', 'T20/T21 transaction core coordinated by T28; installed=')
Set-Content $t20Path $t20 -Encoding UTF8

# T22: same policy; package state is entered/exited by the T28 coordinator.
$t22Path='RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$t22=Get-Content $t22Path -Raw
$oldT22=@'
                MethodBase package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package != null)
                {
                    harmony.Patch(package,
                        prefix: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackagePrefix))
                        { priority = Priority.First + 200 },
                        finalizer: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackageFinalizer))
                        { priority = Priority.Last - 200 });
                    packagePatched = true;
                }

'@
$t22=Replace-OrThrow $t22 $oldT22 @'
                packagePatched = JobSearchPackageContext093T28.Installed;

'@ 'T22 package Harmony ownership'
$t22=$t22.Replace('T22 generic GenClosest transaction index installed=', 'T22 generic GenClosest index coordinated by T28; installed=')
Set-Content $t22Path $t22 -Encoding UTF8

# GlobalNearest: remove its duplicate package prefix/finalizer. T28 still calls the
# original JobGiverPrefix/Finalizer methods, so its exact plan lifetime/validation is unchanged.
$globalPath='RimMT/Source/RimMT/AI/JobGiverGlobalNearest04181.cs'
$global=Get-Content $globalPath -Raw
$oldGlobal=@'
                MethodBase jobGiver = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (jobGiver == null) return;

                harmony.Patch(jobGiver,
                    prefix: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverFinalizer)) { priority = Priority.Last });

'@
$global=Replace-OrThrow $global $oldGlobal @'
                if (!JobSearchPackageContext093T28.Installed) return;

'@ 'GlobalNearest package Harmony ownership'
$global=$global.Replace('Unified nearest-first + JS2 package-local search-plan reuse active:',
                        'T28-coordinated nearest-first + JS2 package-local search-plan reuse active:')
Set-Content $globalPath $global -Encoding UTF8

# DoBill: move the false-only BillStack proof into the shared bottom-layer context.
$billPath='RimMT/Source/RimMT/AI/PersistentDoBillIndex092.cs'
$bill=Get-Content $billPath -Raw
$bill=$bill.Replace('        [ThreadStatic] private static long readinessScopeStamp;' + [Environment]::NewLine, '')
$bill=$bill.Replace('        [ThreadStatic] private static HashSet<BillStack> inactiveStacksInPackage;' + [Environment]::NewLine, '')
# Handle LF checkout as well.
$bill=$bill.Replace("        [ThreadStatic] private static long readinessScopeStamp;`n", '')
$bill=$bill.Replace("        [ThreadStatic] private static HashSet<BillStack> inactiveStacksInPackage;`n", '')

$helperPattern='(?s)        private static bool PackageReadinessShouldDoNow\(BillStack stack\).*?\n        private static bool HasUnsafeForeignPatch'
if(-not [regex]::IsMatch($bill,$helperPattern)){ throw 'T28 anchor missing: DoBill readiness helper' }
$newHelper=@'
        private static bool PackageReadinessShouldDoNow(BillStack stack)
        {
            if (stack == null) return true;
            if (!JobSearchPackageContext093T28.InScope)
            {
                readinessActualChecks++;
                return stack.AnyShouldDoNow;
            }

            if (JobSearchPackageContext093T28.IsBillStackKnownInactive(stack))
            {
                readinessFalseMemoHits++;
                return false;
            }

            readinessActualChecks++;
            bool active = stack.AnyShouldDoNow;
            if (!active)
            {
                JobSearchPackageContext093T28.MarkBillStackInactive(stack);
                readinessFalseMemoStores++;
            }
            return active;
        }

        private static bool HasUnsafeForeignPatch
'@
$bill=[regex]::Replace($bill,$helperPattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $newHelper },1)
Set-Content $billPath $bill -Encoding UTF8

# Production on-demand report: T28 becomes explicit and the dead fabric consumer no longer
# advertises an active optimization path.
$reportPath='RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T27.4 Diagnostics Split','V0.9.3-T28 Unified Job Search Transaction')
$report=$report.Replace('V0.9.3-T27.8 Job Search Foundation','V0.9.3-T28 Unified Job Search Transaction')
if($report.Contains('            sb.AppendLine(JobSearchTransaction093T20.Summary());')){
  $report=$report.Replace('            sb.AppendLine(JobSearchTransaction093T20.Summary());',
                          "            sb.AppendLine(JobSearchPackageContext093T28.Summary());`n            sb.AppendLine(JobSearchTransaction093T20.Summary());")
}
if($report.Contains('            sb.AppendLine(AdaptiveGenClosestAssist.Summary());')){
  $report=$report.Replace('            sb.AppendLine(AdaptiveGenClosestAssist.Summary());',
                          '            sb.AppendLine("Persistent-fabric GenClosest consumer: RETIRED/OFF in T28 after sustained zero-acceleration evidence; fabric implementation retained in code but no runtime consumer is installed.");')
}
$report=$report.Replace('DoBill=persistent incremental membership + live readiness + zero-yield-sleeping worker-tail fabric;',
                        'DoBill=persistent incremental membership + T28 package-local false readiness proof;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath='RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=$a.Replace('V0.9.3-T27.8 Job Search Foundation','V0.9.3-T28 Unified Job Search Transaction')
  $a=$a.Replace('V0.9.3-T27.7 Dead Path Retirement','V0.9.3-T28 Unified Job Search Transaction')
  Set-Content $aboutPath $a -Encoding UTF8
}

# Diagnostics v0.6: surface the shared transaction summary and eliminate the stale report label.
$diagPatchPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.5.0";' 'internal const string Version = "0.6.0";' 'Diagnostics version 0.6'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath='RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
$dr=Replace-OrThrow $dr @'
        private static readonly string[] SummaryTypes = new string[]
        {
            "RimMT.JobSearchTransaction093T20",
'@ @'
        private static readonly string[] SummaryTypes = new string[]
        {
            "RimMT.JobSearchPackageContext093T28",
            "RimMT.JobSearchTransaction093T20",
'@ 'Diagnostics T28 reflection bridge'
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout='RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=$a.Replace('RimMT Diagnostics v0.5','RimMT Diagnostics v0.6')
  $a=$a.Replace('RimMT Diagnostics v0.4','RimMT Diagnostics v0.6')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T28 Unified Job Search Transaction + Diagnostics v0.6.'
