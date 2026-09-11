param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Path,[string]$Old,[string]$New,[string]$Label)
    $text = (Get-Content $Path -Raw).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    if (-not $text.Contains($oldNorm)) { throw "SMF15 patch anchor not found: $Label ($Path)" }
    $text = $text.Replace($oldNorm,$newNorm)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

# CameraDriver.PanToMapLocAndSize is new in 1.6. RimWorld 1.5 already has the same
# CameraPanner interpolation/completion primitive; call it directly so size + callback semantics stay intact.
$p = Join-Path $Root 'src/Smf.Mod/API/CameraApi.cs'
Replace-OrThrow $p @'
        PanCompletionCallback? callback = completion == null ? null : new PanCompletionCallback(completion);
        driver.PanToMapLocAndSize(position, size, duration, callback);
'@ @'
        PanCompletionCallback? callback = completion == null ? null : new PanCompletionCallback(completion);
        driver.panner.PanTo(
            new CameraPanner.Interpolant(driver.rootPos, driver.RootSize),
            new CameraPanner.Interpolant(new Vector3(position.x, driver.rootPos.y, position.z), size),
            duration,
            callback);
'@ 'CameraApi PanToMapLocAndSize -> CameraPanner.PanTo'

# Odyssey/gravship state does not exist in 1.5. AASB coverage is always eligible on that axis.
$p = Join-Path $Root 'src/Smf.Mod/Compatibility/AsAboveSoBelow/AsAboveSoBelow.cs'
Replace-OrThrow $p '            bool coverage = !WorldComponent_GravshipController.GravshipRenderInProgess;' '            bool coverage = true;' 'remove 1.6 gravship render guard'

# In 1.5 there is no world-background render mode. WorldRenderedNow is therefore the exact
# equivalent of 1.6 WorldSelected for deciding whether the colony map camera is selected.
$p = Join-Path $Root 'src/Smf.Mod/Rendering/RimWorldSceneOwner.cs'
$text = (Get-Content $p -Raw).Replace("`r`n", "`n")
if (($text.Split('WorldRendererUtility.WorldSelected').Count - 1) -ne 2) { throw 'Unexpected RimWorldSceneOwner WorldSelected count' }
$text = $text.Replace('WorldRendererUtility.WorldSelected', 'WorldRendererUtility.WorldRenderedNow')
[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($false))

# MapCoverageCapture: adapt the 1.6 map-draw envelope to the 1.5 Map.MapUpdate shape.
$p = Join-Path $Root 'src/Smf.Mod/Rendering/MapCoverageCapture.cs'
Replace-OrThrow $p @'
    private static readonly MethodInfo DrawStart = AccessTools.Method(typeof(GlobalRendererUtility), "UpdateGlobalShadersParams");
'@ @'
    // 1.5 has no GlobalRendererUtility call inside Map.MapUpdate. MapMeshDrawerUpdate_First is
    // the first view-dependent map-mesh operation in the normal draw block and is a stable 1.5 anchor.
    private static readonly MethodInfo DrawStart = AccessTools.Method(typeof(MapDrawer), "MapMeshDrawerUpdate_First");
'@ 'map draw start anchor'
Replace-OrThrow $p '            if (def.doNotUpdate || original.targetTexture == null || original.commandBufferCount != 0 ||' '            if (original.targetTexture == null || original.commandBufferCount != 0 ||' 'remove SubcameraDef.doNotUpdate (1.6 field)'
$text = (Get-Content $p -Raw).Replace("`r`n", "`n")
if (($text.Split('WorldRendererUtility.DrawingMap').Count - 1) -ne 2) { throw 'Unexpected MapCoverageCapture DrawingMap count' }
$text = $text.Replace('WorldRendererUtility.DrawingMap', '!WorldRendererUtility.WorldRenderedNow')
[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($false))
Replace-OrThrow $p @'
        Material edge = map.MapEdgeMaterial;
        if (!map.DrawMapClippers ||
            (edge != MapEdgeClipDrawer.ClipMat && edge != MapEdgeClipDrawer.ClipMatMetalhell) ||
            edge == null || edge.shader != ShaderDatabase.MetaOverlay || !edge.shader.isSupported ||
'@ @'
        // 1.5 always draws the native map clippers. Its material selection is the pre-1.6
        // implementation from MapEdgeClipDrawer.DrawClippers.
        Material edge = ModsConfig.AnomalyActive && Find.CurrentMap?.generatorDef == MapGeneratorDefOf.MetalHell
            ? MapEdgeClipDrawer.ClipMatMetalhell
            : MapEdgeClipDrawer.ClipMat;
        if ((edge != MapEdgeClipDrawer.ClipMat && edge != MapEdgeClipDrawer.ClipMatMetalhell) ||
            edge == null || edge.shader != ShaderDatabase.MetaOverlay || !edge.shader.isSupported ||
'@ '1.5 map edge material/clipper behavior'

# Camera ownership gates: 1.5 has no Odyssey cutscene/gravship mode. PlayerHasControl in 1.6
# is ScreenFader + gravship cutscene; only ScreenFader exists/has meaning in 1.5.
$p = Join-Path $Root 'src/Smf.Mod/Rendering/CameraControl/CameraOwnershipAdapter.cs'
Replace-OrThrow $p '            || !Current.Game.PlayerHasControl' '            || ScreenFader.IsFading()' 'PlayerHasControl -> ScreenFader'
$text = (Get-Content $p -Raw).Replace("`r`n", "`n")
if (($text.Split('WorldRendererUtility.WorldSelected').Count - 1) -ne 2) { throw 'Unexpected CameraOwnershipAdapter WorldSelected count' }
$text = $text.Replace('WorldRendererUtility.WorldSelected', 'WorldRendererUtility.WorldRenderedNow')
[IO.File]::WriteAllText($p, $text, [Text.UTF8Encoding]::new($false))
Replace-OrThrow $p @'
            && driver.config.autoPanSpeed == 0f
            && !driver.config.gravshipFreeCam;
'@ @'
            && driver.config.autoPanSpeed == 0f;
'@ 'remove gravshipFreeCam gate'
Replace-OrThrow $p @'
        if (ReadTextCapture()) flags |= MainFlags.TextCaptured;
        if (Find.WindowStack?.AnySearchWidgetFocused == true) flags |= MainFlags.SearchFocused;
        if (driver != null && Panner(driver).Moving) flags |= MainFlags.NativePan;
'@ @'
        if (ReadTextCapture()) flags |= MainFlags.TextCaptured;
        if (AnySearchWidgetFocused15()) flags |= MainFlags.SearchFocused;
        if (driver != null && Panner(driver).Moving) flags |= MainFlags.NativePan;
'@ '1.5 search focus helper call'
Replace-OrThrow $p @'
    private static void Publish()
    {
'@ @'
    private static bool AnySearchWidgetFocused15()
    {
        var stack = Find.WindowStack;
        if (stack == null) return false;
        foreach (Window window in stack.Windows)
        {
            var widget = window.CommonSearchWidget;
            if (widget != null && widget.CurrentlyFocused()) return true;
        }
        return false;
    }

    private static void Publish()
    {
'@ 'insert 1.5 search focus helper'

# 1.5 only has planet vs map rendering, so DrawingMap == !WorldRenderedNow.
$p = Join-Path $Root 'src/Smf.Mod/Rendering/HybridRuntime.cs'
Replace-OrThrow $p 'WorldRendererUtility.DrawingMap' '!WorldRendererUtility.WorldRenderedNow' 'HybridRuntime DrawingMap equivalent'

Write-Host 'Applied SimplyMoreFPS RimWorld 1.5 managed backport shims.'
