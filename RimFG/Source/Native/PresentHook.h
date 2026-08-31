#pragma once

#include <cstdint>
#include <d3d11.h>
#include <dxgi.h>

namespace RimFGPresent
{
    enum class PresentMode : int
    {
        Disabled = 0,
        ImmediateValidation = 1,
        VSync2x = 2
    };

    struct HudRectPx
    {
        float x;
        float y;
        float width;
        float height;
    };

    bool Initialize(ID3D11Device* unityDevice);
    void Shutdown();

    bool IsInstalled();
    bool HasUnitySwapChain();
    IDXGISwapChain* GetUnitySwapChain();

    void SetPresentMode(PresentMode mode);
    PresentMode GetPresentMode();

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
