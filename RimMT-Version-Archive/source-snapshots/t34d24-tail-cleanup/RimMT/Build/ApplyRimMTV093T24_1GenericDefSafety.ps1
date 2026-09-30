$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T24.1 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Replace-Between-OrThrow {
    param([string]$Text,[string]$Start,[string]$End,[string]$Replacement,[string]$Label)
    $a = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($a -lt 0) { throw "RimMT V0.9.3-T24.1 start anchor not found: $Label" }
    $b = $Text.IndexOf($End, $a + $Start.Length, [System.StringComparison]::Ordinal)
    if ($b -lt 0) { throw "RimMT V0.9.3-T24.1 end anchor not found: $Label" }
    return $Text.Substring(0, $a) + $Replacement + $Text.Substring($b)
}

# RimMT V0.9.3-T24.1 Generic Def Safety
# Runtime evidence on RimWorld 1.5.4063/Mono showed that Harmony-patching methods on the closed
# generic DefDatabase<TraitDef> can contaminate shared generic code and route unrelated
# DefDatabase<ThingDef/WorldObjectDef/...>.Add calls through the TraitDef patched wrapper.
# The result is ArrayTypeMismatchException inside List<T>.Add and widespread static-constructor
# failure. T24.1 removes every closed-generic Harmony hook from the WorldRoot/WTL diagnostic path.
# Core T24 Reach/S4/GenClosest behavior is unchanged.

$rootPath = 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs'
$root = Get-Content $rootPath -Raw

$root = Replace-OrThrow $root `
    '                PatchTraitDefDatabaseSignals(harmony);' `
    '                // T24.1: closed-generic DefDatabase<TraitDef> Harmony hooks are forbidden on Mono. genericHarmony=OFF.' `
    'disable DefDatabase<TraitDef> Harmony signal hooks'

$newWtl = @'
        private static void PatchWorldTechLevel(Harmony harmony)
        {
            try
            {
                // T24.1: do not Harmony-patch WorldTechLevel.TechLevelDatabase<TraitDef> either.
                // Closed generic methods can share Mono/JIT code across T and are not a safe patch
                // boundary in this runtime. Keep only the non-generic global initializer timer.
                Type global = AccessTools.TypeByName("WorldTechLevel.DefTechLevels");
                if (global == null) return;
                wtlDetected = true;
                wtlNarrowAuthoritySafe = false;
                wtlEnsurePatched = false;
                wtlTraitInitialize = null;
                wtlTraitApplyOverrides = null;
                wtlTraitLevels = null;

                MethodInfo globalInit = AccessTools.Method(global, "Initialize");
                if (globalInit != null)
                {
                    harmony.Patch(globalInit,
                        prefix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WtlGlobalInitPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldRootAttribution093T22), nameof(WtlGlobalInitPostfix)));
                    wtlGlobalInitPatched = true;
                }
            }
            catch
            {
                wtlNarrowAuthoritySafe = false;
                installFailures++;
            }
        }

'@

$root = Replace-Between-OrThrow $root `
    '        private static void PatchWorldTechLevel(Harmony harmony)' `
    '        private static bool IsAuthoritySafeForNarrowWtl(MethodBase ensure)' `
    $newWtl `
    'replace closed-generic WTL Harmony patch with non-generic timing only'

$root = $root.Replace(
    'WorldTechLevel TraitDef narrow-rebuild guard detected=" + wtlDetected +`r`n                    ", authoritySafe=" + wtlNarrowAuthoritySafe + ".");',
    'WorldTechLevel detected=" + wtlDetected +`r`n                    ", closedGenericHarmony=OFF, globalInitTiming=" + wtlGlobalInitPatched + ".");')
$root = $root.Replace(
    'WorldTechLevel TraitDef narrow-rebuild guard detected=" + wtlDetected +`n                    ", authoritySafe=" + wtlNarrowAuthoritySafe + ".");',
    'WorldTechLevel detected=" + wtlDetected +`n                    ", closedGenericHarmony=OFF, globalInitTiming=" + wtlGlobalInitPatched + ".");')
Set-Content $rootPath $root -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot `
    'internal const string Version = "0.9.3-t24-stutter-first";' `
    'internal const string Version = "0.9.3-t24.1-generic-def-safety";' `
    'T24.1 bootstrap version'
$boot = $boot.Replace('[RimMT] V0.9.3-T24 Stutter First initialized.',
                      '[RimMT] V0.9.3-T24.1 Generic Def Safety initialized.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T24 Stutter First', 'V0.9.3-T24.1 Generic Def Safety')
$report = $report.Replace('T23 ReachProfile/WorldRoot retained; T24 localizes Reach parity quarantine, disables automatic deep-profiler bursts, and enables authority-safe targeted early S4 admission;',
    'T24 behavior retained; T24.1 disables all closed-generic DefDatabase<TraitDef>/TechLevelDatabase<TraitDef> Harmony hooks after Mono generic-sharing corruption was observed; non-generic WorldRoot timing remains;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T24 Stutter First', 'V0.9.3-T24.1 Generic Def Safety')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T24.1 Generic Def Safety: closed-generic DefDatabase<TraitDef> and TechLevelDatabase<TraitDef> Harmony hooks disabled; T24 production optimizations unchanged.'
