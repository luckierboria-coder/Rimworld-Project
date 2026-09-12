param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Path,[string]$Old,[string]$New,[string]$Label)
    $text = (Get-Content $Path -Raw).Replace("`r`n", "`n")
    $oldNorm = $Old.Replace("`r`n", "`n")
    $newNorm = $New.Replace("`r`n", "`n")
    if (-not $text.Contains($oldNorm)) { throw "SMF15 alpha5 patch anchor not found: $Label ($Path)" }
    $text = $text.Replace($oldNorm,$newNorm)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

$p = Join-Path $Root 'src/Smf.Mod/Rendering/GuiRasterCapture.cs'

# Upstream 0.3.12 treats any non-null RenderTexture.active at the first top-level
# Repaint as a fatal renderer invariant violation. That assumption is too strict for
# RimWorld 1.5 / Unity 2019 and for large mod stacks: another rendering owner may
# legitimately wrap an isolated top-level IMGUI pass in its own temporary target.
#
# Do not capture or redirect that foreign pass. It is safer to expire one detached
# source frame and retry on the next top-level Repaint than to steal a target whose
# lifetime/contents belong to Unity or another mod. Keep fail-closed behavior if the
# unexpected target is one of SMF's own generation textures; that would indicate a
# genuine leaked capture target rather than a foreign rendering pass.
Replace-OrThrow $p @'
            // The base copy must happen before any redirection; a custom top-level target is not supported.
            if (RenderTexture.active != null)
                throw new InvalidOperationException("First top-level Repaint does not target the original client framebuffer.");
'@ @'
            // Unity 2019 and modded render chains can legitimately enter an isolated top-level
            // IMGUI Repaint with a foreign RenderTexture bound. Never steal that target: skip
            // this detached source frame and retry on the next top-level Repaint.
            RenderTexture topLevelTarget = RenderTexture.active;
            if (topLevelTarget != null)
            {
                foreach (Generation generation in Generations.Values)
                {
                    if (topLevelTarget == generation.Hud || topLevelTarget == generation.World)
                        throw new InvalidOperationException("First top-level Repaint entered a leaked SMF capture target.");
                }
                return false;
            }
'@ 'foreign top-level Repaint target becomes per-frame skip'

Write-Host 'Applied SMF RW1.5 alpha5 GUI target compatibility: foreign top-level RenderTexture skips one detached frame; leaked SMF targets still fail closed.'
