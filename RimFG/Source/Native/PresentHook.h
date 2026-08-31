#pragma once

#include <cstdint>
#include <d3d11.h>
#include <dxgi.h>

namespace RimFGPresent
{
    struct HudRectPx
    {
        float x;
        float y;
        float width;
        float height;
    };

    // Installs one process-wide IDXGISwapChain::Present hook, but only treats
    // swapchains belonging to RimWorld/Unity as RimFG targets.
    bool Initialize(ID3D11Device* unityDevice);
    void Shutdown();

    bool IsInstalled();
    bool HasUnitySwapChain();
    IDXGISwapChain* GetUnitySwapChain(); // borrowed pointer; present thread only

    // Borrowed GPU texture pointer. RimFG.Native owns the resource lifetime and
    // clears this source before releasing/recreating frame-generation textures.
    // Tiny HUD metadata is copied into a lock-free fixed-size slot.
    void SetGeneratedFrameSource(
        ID3D11Texture2D* generatedFrame,
        int width,
        int height,
        const HudRectPx* hudRects,
        int hudRectCount,
        std::uint32_t frameIndex);

    void ClearGeneratedFrameSource();

    std::uint64_t GeneratedPresentCount();
    std::uint64_t SkippedPresentCount();
}
