#include <atomic>
#include <array>
#include <cstdint>
#include <cstring>

#include <d3d11.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

namespace
{
    constexpr std::uint32_t kAbiVersion = 1;
    constexpr int kMaxHudRects = 8;

#pragma pack(push, 4)
    struct FrameMetadata
    {
        std::uint32_t abiVersion;
        std::uint32_t frameIndex;
        std::int32_t screenWidth;
        std::int32_t screenHeight;
        float cameraX;
        float cameraY;
        float cameraZ;
        float orthographicSize;
        float unscaledDeltaTime;
        std::int32_t paused;
        std::int32_t gameSpeed;
        std::int32_t hudRectCount;
    };

    struct HudRect
    {
        float x;
        float y;
        float width;
        float height;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch for FrameMetadata");
    static_assert(sizeof(HudRect) == 16, "Managed/native ABI mismatch for HudRect");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        std::int32_t hudCount = 0;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_device = nullptr; // Owned by Unity; do not Release.

    std::atomic<bool> g_enabled{false};
    std::atomic<bool> g_d3d11Ready{false};
    std::atomic<std::uint32_t> g_writeSequence{0};

    alignas(64) std::array<MetadataSlot, 2> g_slots{};

    void RefreshD3D11Device()
    {
        g_device = nullptr;
        g_d3d11Ready.store(false, std::memory_order_release);

        if (!g_unityInterfaces)
            return;

        auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (!d3d11)
            return;

        g_device = d3d11->GetDevice();
        g_d3d11Ready.store(g_device != nullptr, std::memory_order_release);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        switch (eventType)
        {
        case kUnityGfxDeviceEventInitialize:
        case kUnityGfxDeviceEventAfterReset:
            RefreshD3D11Device();
            break;
        case kUnityGfxDeviceEventBeforeReset:
        case kUnityGfxDeviceEventShutdown:
            g_d3d11Ready.store(false, std::memory_order_release);
            g_device = nullptr;
            break;
        default:
            break;
        }
    }

    MetadataSlot ReadLatestSlot()
    {
        const std::uint32_t seq = g_writeSequence.load(std::memory_order_acquire);
        return g_slots[seq & 1u];
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        if (eventId != 1 || !g_enabled.load(std::memory_order_relaxed))
            return;

        if (!g_d3d11Ready.load(std::memory_order_acquire) || !g_device)
            return;

        const MetadataSlot slot = ReadLatestSlot();
        if (slot.frame.abiVersion != kAbiVersion)
            return;

        // V0.1 bootstrap intentionally performs no CPU framebuffer work.
        // Next milestone binds GPU-resident frame textures and dispatches:
        //  1) HUD mask compute pass
        //  2) camera-aware warp / optical flow
        //  3) interpolation + composite
        //  4) independent Present scheduler/hook
        (void)slot;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unityInterfaces = unityInterfaces;
    g_unityGraphics = unityInterfaces ? unityInterfaces->Get<IUnityGraphics>() : nullptr;

    if (g_unityGraphics)
    {
        g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
UnityPluginUnload()
{
    if (g_unityGraphics)
        g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);

    g_enabled.store(false, std::memory_order_release);
    g_d3d11Ready.store(false, std::memory_order_release);
    g_device = nullptr;
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API
RimFG_GetRenderEventFunc()
{
    return OnRenderEvent;
}

extern "C" UNITY_INTERFACE_EXPORT void
RimFG_SetEnabled(int enabled)
{
    g_enabled.store(enabled != 0, std::memory_order_release);
}

extern "C" UNITY_INTERFACE_EXPORT int
RimFG_IsD3D11Ready()
{
    return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT void
RimFG_SubmitFrameState(const FrameMetadata* metadata, const HudRect* rects, int count)
{
    if (!metadata || metadata->abiVersion != kAbiVersion)
        return;

    const std::uint32_t next = g_writeSequence.load(std::memory_order_relaxed) + 1u;
    MetadataSlot& slot = g_slots[next & 1u];

    slot.frame = *metadata;

    int clamped = count;
    if (clamped < 0) clamped = 0;
    if (clamped > kMaxHudRects) clamped = kMaxHudRects;
    if (!rects) clamped = 0;

    if (clamped > 0)
        std::memcpy(slot.hud.data(), rects, sizeof(HudRect) * static_cast<std::size_t>(clamped));

    slot.hudCount = clamped;
    slot.frame.hudRectCount = clamped;

    // Publish only after metadata + mask rectangles have been copied.
    g_writeSequence.store(next, std::memory_order_release);
}
