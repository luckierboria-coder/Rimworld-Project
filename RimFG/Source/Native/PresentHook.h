#pragma once

#include <cstdint>
#include <d3d11.h>
#include <dxgi.h>

namespace RimFGPresent
{
    constexpr int MaxHudRects = 256;

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

    using BackbufferGenerationCallback = bool(*)(ID3D11Texture2D* backBuffer);
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
    std::uint64_t PresentFailureCount();
    std::uint64_t StalePredictionCount();
    std::uint64_t RingBusyDropCount();
    std::uint64_t CompositionFailureCount();
}
