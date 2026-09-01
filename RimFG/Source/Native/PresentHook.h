#pragma once

#include <cstdint>
#include <windows.h>
#include <d3d11.h>
#include <dxgi.h>

namespace RimFGPresent
{
    constexpr int MaxHudRects = 256;
    constexpr int MaxBatchFrames = 16;

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

    struct SharedFrameBatch
    {
        std::uint64_t batchId = 0;
        std::uint32_t frameIndex = 0;
        int width = 0;
        int height = 0;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        HWND sourceWindow = nullptr;
        int frameCount = 0;
        int realFrameIndex = -1;
        HANDLE handles[MaxBatchFrames]{};
    };

    // Called on Unity's own Present/render thread. It may record GPU work on the
    // Unity immediate context, but it must never block waiting for the presenter.
    using BackbufferGenerationCallback = bool(*)(ID3D11Texture2D* backBuffer, HWND sourceWindow);

    bool Initialize(ID3D11Device* unityDevice);
    void Shutdown();

    bool IsInstalled();
    bool HasUnitySwapChain();
    IDXGISwapChain* GetUnitySwapChain();

    void SetPresentMode(PresentMode mode);
    PresentMode GetPresentMode();
    void SetBackbufferGenerationCallback(BackbufferGenerationCallback callback);

    // The producer publishes a complete delayed interpolation batch. Textures are
    // shared keyed-mutex resources. Producer releases key 1; presenter consumes key
    // 1 and releases key 0. Handles remain valid until producer recreates resources.
    void PublishSharedFrameBatch(const SharedFrameBatch& batch);
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
