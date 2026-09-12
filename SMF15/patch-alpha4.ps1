param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Path,[string]$Old,[string]$New,[string]$Label)
    $text = (Get-Content $Path -Raw).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    if (-not $text.Contains($oldNorm)) { throw "SMF15 alpha4 patch anchor not found: $Label ($Path)" }
    $text = $text.Replace($oldNorm,$newNorm)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$p = Join-Path $Root 'src/Smf.Mod/Rendering/MapCoverageCapture.cs'

# RimWorld 1.6 split a per-frame MapComponentOnDraw hook from the component update path.
# RimWorld 1.5 has no MapComponentOnDraw; MapComponentUpdate(Map) is its per-frame
# MapComponent lifecycle and runs immediately after Map.MapUpdate's normal rendering block.
# The existing SMF prefix/finalizer only borrows/restores CameraDriver's view cache around
# component callbacks, so the same scope maps cleanly to the 1.5 lifecycle.
Replace-OrThrow $p @'
        harmony.Patch(
            AccessTools.Method(typeof(MapComponentUtility), "MapComponentOnDraw"),
            prefix: Hook(nameof(BeforeComponents)),
            finalizer: Hook(nameof(AfterComponents)));
'@ @'
        harmony.Patch(
            RequireTarget(typeof(MapComponentUtility), "MapComponentUpdate"),
            prefix: Hook(nameof(BeforeComponents)),
            finalizer: Hook(nameof(AfterComponents)));
'@ 'MapComponentOnDraw -> MapComponentUpdate'

# Resolve every unconditional vanilla Harmony target before mutating the patch chain. This turns
# any future API drift into a precise fail-closed error and prevents a half-installed session.
Replace-OrThrow $p @'
        harmony = new Harmony(owner + ".coverage");

        harmony.Patch(AccessTools.Method(typeof(Root_Play), "Update"), prefix: Hook(nameof(BeforeRoot)));
        harmony.Patch(
            AccessTools.Method(typeof(Map), "MapUpdate"),
'@ @'
        harmony = new Harmony(owner + ".coverage");

        MethodInfo rootUpdate = RequireTarget(typeof(Root_Play), "Update");
        MethodInfo mapUpdate = RequireTarget(typeof(Map), "MapUpdate");
        RequireTarget(typeof(MapDrawer), "MapMeshDrawerUpdate_First");
        RequireTarget(typeof(FleckManager), "FleckManagerDraw");

        harmony.Patch(rootUpdate, prefix: Hook(nameof(BeforeRoot)));
        harmony.Patch(
            mapUpdate,
'@ 'preflight coverage Harmony targets'

Replace-OrThrow $p @'
    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(MapCoverageCapture), name);
'@ @'
    private static MethodInfo RequireTarget(Type type, string name)
    {
        MethodInfo method = AccessTools.Method(type, name);
        if (method == null)
            throw new MissingMethodException("SimplyMoreFPS RW1.5 backport: required coverage target is absent: " + type.FullName + "." + name);
        return method;
    }

    private static HarmonyMethod Hook(string name) => new HarmonyMethod(typeof(MapCoverageCapture), name);
'@ 'insert fail-closed target resolver'

Write-Host 'Applied SMF RW1.5 alpha4 coverage lifecycle backport: MapComponentUpdate + preflighted Harmony targets.'
