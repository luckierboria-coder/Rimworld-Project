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

    // Called once for every real RimWorld frame. It captures the new real frame,
    // advances temporal history and prepares motion/flow state for prediction.
    using BackbufferGenerationCallback = bool(*)(ID3D11Texture2D* backBuffer);

    // Called by the independent presenter for every generated frame. fraction is
    // the requested temporal position after the latest real frame, normally in
    // the open interval (0,1). The returned texture is owned by RimFG.Native and
    // may be reused by the next callback invocation.
    using PredictionGenerationCallback = ID3D11Texture2D*(*)(float fraction);

    bool Initialize(ID3D11Device* unityDevice);
    void Shutdown();

    bool IsInstalled();
    bool HasUnitySwapChain();
    IDXGISwapChain* GetUnitySwapChain();

    void SetPresentMode(PresentMode mode);
    PresentMode GetPresentMode();
    void SetBackbufferGenerationCallback(BackbufferGenerationCallback callback);
    void SetPredictionGenerationCallback(PredictionGenerationCallback callback);

    void SetGeneratedFrameSource(
        ID3D11Texture2D* generatedFrame,
        int width,
        int height,
        const HudRectPx* hudRects,
        int hudRectCount,
        std::uint32_t frameIndex);

    void ClearGeneratedFrameSource();

    void SetTargetOutputFps(int fps);
    int GetTargetOutputFps();
    double EstimatedBaseFps();
    double EstimatedOutputFps();
    bool PresenterReady();
    int MonitorRefreshHz();

    std::uint64_t RealPresentCount();
    std::uint64_t GeneratedPresentCount();
    std::uint64_t SkippedPresentCount();
    std::uint64_t FrameLatencyTimeoutCount();
}
