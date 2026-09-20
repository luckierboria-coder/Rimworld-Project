$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "A/B2 anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# A/B2 isolates the remaining GenClosest candidate layer on top of A/B1:
# - T22 GenClosest_Global_NewTemp authority is not installed.
# - GlobalNearest's Global/Global_Reachable candidate rewriting is not installed.
# - GlobalNearest's JobGiver scope lifetime remains active via T28 so BroadGenClosestOrder
#   continues to bypass JobGiver_Work calls. This prevents a replacement ordering path from
#   silently taking over when GlobalNearest is disabled.
# - T21/T32/DoBill/Haul and all other A/B1-retained production paths remain unchanged.

$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw

$boot=Replace-OrThrow $boot '                GenClosestTransactionIndex093T22.Apply(harmony);' '                // A/B2: T22 GenClosest_Global_NewTemp authority intentionally not installed.' 'T22 bootstrap install'
$boot=Replace-OrThrow $boot '                JobGiverGlobalNearest04181.Apply(harmony);' '                // A/B2: GlobalNearest candidate rewriting intentionally not installed; T28 keeps scope-only lifetime for BroadGenClosestOrder bypass.' 'GlobalNearest bootstrap install'

$boot=[regex]::Replace(
  $boot,
  'internal const string Version = "[^"]+";',
  'internal const string Version = "0.9.3-t32c1-ab2-genclosest-off";',
  1)

$boot=$boot.Replace(
  '[RimMT] V0.9.3-T32C.1 A/B1 initialized: S5.1 and S4 ClosestThingReachable authority disabled; all other T32-C.1 production paths retained.',
  '[RimMT] V0.9.3-T32C.1 A/B2 initialized: S5.1/S4 remain OFF; T22 authority and GlobalNearest candidate rewriting are OFF; GlobalNearest scope-only guard remains for BroadGenClosestOrder bypass; T21, T32-A/C.1, DoBill, haul and other production paths retained.')

Set-Content $bootPath $boot -Encoding UTF8

# T28 still owns the package and T21 lifecycle. Remove only T22 package-state entry/exit.
# Keep GlobalNearest scope entry/exit deliberately: its Harmony candidate-rewrite patches are
# absent, but InJobGiverScope must remain true so BroadGenClosestOrder does not take over.
$t28Path=Join-Path $root 'RimMT/Source/RimMT/AI/JobSearchPackageContext093T28.cs'
$t28=Get-Content $t28Path -Raw

$t28=Replace-OrThrow $t28 '            GenClosestTransactionIndex093T22.PackagePrefix(ref __state.T22);' '            // A/B2: T22 package-local index state intentionally not entered.' 'T28 T22 package prefix'
$t28=Replace-OrThrow $t28 '            __exception = GenClosestTransactionIndex093T22.PackageFinalizer(__exception, __state.T22);' '            // A/B2: T22 package-local index state was not entered.' 'T28 T22 package finalizer'

$t28=$t28.Replace(
  'One Harmony package wrapper now coordinates T20/T21, T22, GlobalNearest and shared false-only package state.',
  'A/B2 package wrapper coordinates T20/T21 plus shared false-only package state; T22 is OFF. GlobalNearest lifecycle is scope-only so BroadGenClosestOrder continues to bypass JobGiver calls.')

$t28=$t28.Replace(
  'Older transaction modules keep their proven internal semantics, but their package prefix/finalizer methods are invoked from this',
  'Retained transaction modules keep their proven internal semantics, while A/B2 intentionally leaves T22 out. Package prefix/finalizer methods are invoked from this')

Set-Content $t28Path $t28 -Encoding UTF8

# Make production report self-identifying. T22 summary remains visible and should stay all-zero,
# which is useful proof that the authority was not installed.
$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace(
  'V0.9.3-T32C.1 A/B1 S4+S5.1 OFF on-demand report',
  'V0.9.3-T32C.1 A/B2 GenClosest OFF on-demand report')
$report=$report.Replace(
  'S5.1=OFF(A/B1);',
  'S5.1=OFF(A/B1); T22=OFF(A/B2); GlobalNearest rewrite=OFF(A/B2, scope-only guard retained);')
Set-Content $reportPath $report -Encoding UTF8

# About metadata.
$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $about=Get-Content $aboutPath -Raw
  $about=[regex]::Replace(
    $about,
    '<name>.*?</name>',
    '<name>RimMT V0.9.3 T32-C.1 A/B2 - GenClosest Candidate Layer OFF</name>')
  $about=[regex]::Replace(
    $about,
    '(?s)<description>.*?</description>',
    '<description>RimMT T32-C.1 correctness-isolation A/B2 for RimWorld 1.5. It includes A/B1 with S5.1 and S4 disabled, and additionally disables T22 GenClosest_Global_NewTemp authority plus JobGiverGlobalNearest candidate rewriting. The GlobalNearest JobGiver scope marker is retained only so BroadGenClosestOrder still bypasses JobGiver_Work calls and cannot become a replacement candidate-ordering path. T21 validator/reach replay, T32-A/C.1 reservation replay, DoBill, haul and other production systems remain enabled. This build isolates the GenClosest candidate layer behind the mass-idle/false-NoJob regression.</description>')
  Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied A/B2: T22 authority OFF; GlobalNearest candidate rewriting OFF; GlobalNearest scope-only guard retained; A/B1 S4/S5.1 OFF retained.'
