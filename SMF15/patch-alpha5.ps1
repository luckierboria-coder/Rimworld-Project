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

# Upstream 0.3.12 assumes every top-level IMGUI Repaint participating in a detached
# frame enters with the ordinary client target. On RimWorld 1.5 / Unity 2019 and in
# large mod stacks, another rendering owner may wrap an isolated top-level pass in a
# temporary RenderTexture. The guard must run at the top-level BeginContext boundary,
# not only at BeginCapturedFrame, because a later top-level Repaint in the same Unity
# frame can occur while SMF already has TargetHeld=true.
#
# Foreign targets are never stolen or redirected: that top-level pass is left to its
# owner and SMF simply omits it from detached capture. The next source frame retries.
# If the unexpected target is one of SMF's own generation textures, keep fail-closed
# behavior because that indicates a real leaked target/ownership bug.
Replace-OrThrow $p @'
                if (!CaptureRouting || Capture == null || !Capture.Context.Equals(Context))
                    return;

                if (worldScope.Owner != null)
'@ @'
                if (!CaptureRouting || Capture == null || !Capture.Context.Equals(Context))
                    return;

                if (scope.TopLevel)
                {
                    RenderTexture topLevelTarget = RenderTexture.active;
                    if (topLevelTarget != null && (!TargetHeld || topLevelTarget != Capture.Hud))
                    {
                        foreach (Generation generation in Generations.Values)
                        {
                            if (topLevelTarget == generation.Hud || topLevelTarget == generation.World)
                                throw new InvalidOperationException("Top-level Repaint entered a leaked SMF capture target.");
                        }

                        // Foreign/custom full-screen or temporary target. Do not redirect it and
                        // do not fault the persistent session; the next source frame can capture.
                        return;
                    }
                }

                if (worldScope.Owner != null)
'@ 'top-level foreign target ownership guard'

# BeginAfter used to replay Camera+ edge markers after any top-level Repaint, even if
# BeginContext deliberately declined to redirect a foreign target. Restrict replay to
# a scope that SMF actually redirected so a skipped foreign pass is completely untouched.
Replace-OrThrow $p @'
            __state.BeginContext();
            if (Event.current != null && Event.current.type == EventType.Repaint && __state.ContextDepth <= 1)
            {
                __state.ReplayCameraPlusEdges();
            }
'@ @'
            __state.BeginContext();
            GuiScope activeScope = __state.PeekContext();
            if (Event.current != null && Event.current.type == EventType.Repaint && __state.ContextDepth <= 1 &&
                activeScope.Owner != null && activeScope.Redirected)
            {
                __state.ReplayCameraPlusEdges();
            }
'@ 'Camera+ replay only on SMF-redirected GUI scope'

# Once the BeginContext guard above has admitted a first top-level pass, a non-null
# target here can only be an internal ordering regression. Keep that invariant fatal,
# but remove the old message/semantics that treated every foreign target as fatal.
Replace-OrThrow $p @'
            // The base copy must happen before any redirection; a custom top-level target is not supported.
            if (RenderTexture.active != null)
                throw new InvalidOperationException("First top-level Repaint does not target the original client framebuffer.");
'@ @'
            if (RenderTexture.active != null)
                throw new InvalidOperationException("Capture start bypassed the top-level render-target ownership guard.");
'@ 'replace fatal foreign framebuffer assertion with internal guard invariant'

Write-Host 'Applied SMF RW1.5 alpha5 GUI target compatibility: foreign top-level RenderTextures are left to their owner, Camera+ replay is gated by SMF redirection, and leaked SMF targets still fail closed.'
