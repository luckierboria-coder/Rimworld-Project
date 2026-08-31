#include <atomic>
#include <array>
#include <cstdint>
#include <cstring>

#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "PresentHook.h"

using Microsoft::WRL::ComPtr;

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
    static_assert(sizeof(RimFGPresent::HudRectPx) == sizeof(HudRect), "Present HUD ABI mismatch");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        std::int32_t hudCount = 0;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_device = nullptr; // Unity-owned.
    ComPtr<ID3D11DeviceContext> g_context;

    std::atomic<bool> g_enabled{false};
    std::atomic<bool> g_d3d11Ready{false};
    std::atomic<bool> g_hasGeneratedFrame{false};
    std::atomic<std::uint32_t> g_writeSequence{0};
    alignas(64) std::array<MetadataSlot, 2> g_slots{};

    std::atomic<ID3D11Texture2D*> g_pendingSceneTexture{nullptr};
    std::atomic<bool> g_sceneTextureUpdatePending{false};
    std::atomic<int> g_pendingWidth{0};
    std::atomic<int> g_pendingHeight{0};
    ComPtr<ID3D11Texture2D> g_sceneTexture;
    int g_sceneWidth = 0;
    int g_sceneHeight = 0;

    ComPtr<ID3D11Texture2D> g_previousFrame;
    ComPtr<ID3D11Texture2D> g_currentFrame;
    ComPtr<ID3D11Texture2D> g_generatedFrame;
    ComPtr<ID3D11ShaderResourceView> g_previousSrv;
    ComPtr<ID3D11ShaderResourceView> g_currentSrv;
    ComPtr<ID3D11UnorderedAccessView> g_generatedUav;
    ComPtr<ID3D11ComputeShader> g_interpolateCs;
    bool g_haveHistory = false;

    constexpr const char* kInterpolateShader = R"HLSL(
Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame  : register(t1);
RWTexture2D<float4> OutputFrame : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint width, height;
    OutputFrame.GetDimensions(width, height);
    if (id.x >= width || id.y >= height) return;

    float4 a = PreviousFrame.Load(int3(id.xy, 0));
    float4 b = CurrentFrame.Load(int3(id.xy, 0));
    OutputFrame[id.xy] = lerp(a, b, 0.5f);
}
)HLSL";

    void ReleaseFrameResources()
    {
        RimFGPresent::ClearGeneratedFrameSource();
        g_previousSrv.Reset();
        g_currentSrv.Reset();
        g_generatedUav.Reset();
        g_previousFrame.Reset();
        g_currentFrame.Reset();
        g_generatedFrame.Reset();
        g_haveHistory = false;
        g_hasGeneratedFrame.store(false, std::memory_order_release);
    }

    bool EnsureComputeShader()
    {
        if (g_interpolateCs)
            return true;
        if (!g_device)
            return false;

        ComPtr<ID3DBlob> bytecode;
        ComPtr<ID3DBlob> errors;
        const HRESULT compileHr = D3DCompile(
            kInterpolateShader,
            std::strlen(kInterpolateShader),
            "RimFG.InterpolateCS",
            nullptr,
            nullptr,
            "CSMain",
            "cs_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            &bytecode,
            &errors);
        if (FAILED(compileHr) || !bytecode)
            return false;

        return SUCCEEDED(g_device->CreateComputeShader(
            bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_interpolateCs));
    }

    bool CreateFrameResources(ID3D11Texture2D* source)
    {
        if (!source || !g_device)
            return false;

        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (desc.Width == 0 || desc.Height == 0 || desc.SampleDesc.Count != 1)
            return false;

        ReleaseFrameResources();

        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = 0;
        desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_previousFrame)))
            return false;
        if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_currentFrame)))
            return false;

        D3D11_TEXTURE2D_DESC generatedDesc = desc;
        generatedDesc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&generatedDesc, nullptr, &g_generatedFrame)))
            return false;

        if (FAILED(g_device->CreateShaderResourceView(g_previousFrame.Get(), nullptr, &g_previousSrv)))
            return false;
        if (FAILED(g_device->CreateShaderResourceView(g_currentFrame.Get(), nullptr, &g_currentSrv)))
            return false;
        if (FAILED(g_device->CreateUnorderedAccessView(g_generatedFrame.Get(), nullptr, &g_generatedUav)))
            return false;

        return EnsureComputeShader();
    }

    void AdoptPendingSceneTexture()
    {
        if (!g_sceneTextureUpdatePending.exchange(false, std::memory_order_acq_rel))
            return;

        ID3D11Texture2D* pending = g_pendingSceneTexture.exchange(nullptr, std::memory_order_acq_rel);
        g_sceneTexture.Reset();
        if (pending)
            g_sceneTexture.Attach(pending);

        g_sceneWidth = g_pendingWidth.load(std::memory_order_acquire);
        g_sceneHeight = g_pendingHeight.load(std::memory_order_acquire);
        ReleaseFrameResources();

        if (g_sceneTexture)
            CreateFrameResources(g_sceneTexture.Get());
    }

    void RefreshD3D11Device()
    {
        g_context.Reset();
        g_device = nullptr;
        g_d3d11Ready.store(false, std::memory_order_release);

        if (!g_unityInterfaces)
            return;

        auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (!d3d11)
            return;

        g_device = d3d11->GetDevice();
        if (g_device)
            g_device->GetImmediateContext(&g_context);
        g_d3d11Ready.store(g_device != nullptr && g_context != nullptr, std::memory_order_release);
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
            ReleaseFrameResources();
            g_sceneTexture.Reset();
            g_interpolateCs.Reset();
            g_context.Reset();
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

    bool GenerateMidpointFrame()
    {
        if (!g_sceneTexture || !g_context || !g_previousFrame || !g_currentFrame ||
            !g_generatedFrame || !g_previousSrv || !g_currentSrv || !g_generatedUav || !g_interpolateCs)
            return false;

        g_context->CopyResource(g_currentFrame.Get(), g_sceneTexture.Get());

        if (!g_haveHistory)
        {
            g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
            g_haveHistory = true;
            return false;
        }

        ID3D11ShaderResourceView* srvs[2] = { g_previousSrv.Get(), g_currentSrv.Get() };
        ID3D11UnorderedAccessView* uavs[1] = { g_generatedUav.Get() };
        g_context->CSSetShader(g_interpolateCs.Get(), nullptr, 0);
        g_context->CSSetShaderResources(0, 2, srvs);
        g_context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);

        const UINT groupsX = static_cast<UINT>((g_sceneWidth + 7) / 8);
        const UINT groupsY = static_cast<UINT>((g_sceneHeight + 7) / 8);
        g_context->Dispatch(groupsX, groupsY, 1);

        ID3D11ShaderResourceView* nullSrvs[2] = { nullptr, nullptr };
        ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
        g_context->CSSetShaderResources(0, 2, nullSrvs);
        g_context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        g_context->CSSetShader(nullptr, nullptr, 0);

        g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
        g_hasGeneratedFrame.store(true, std::memory_order_release);
        return true;
    }

    void PublishGeneratedFrame(const MetadataSlot& slot)
    {
        if (!g_generatedFrame || !g_hasGeneratedFrame.load(std::memory_order_acquire))
            return;

        std::array<RimFGPresent::HudRectPx, kMaxHudRects> presentRects{};
        const int count = slot.hudCount < 0 ? 0 : (slot.hudCount > kMaxHudRects ? kMaxHudRects : slot.hudCount);
        for (int i = 0; i < count; ++i)
        {
            presentRects[static_cast<std::size_t>(i)] = {
                slot.hud[static_cast<std::size_t>(i)].x,
                slot.hud[static_cast<std::size_t>(i)].y,
                slot.hud[static_cast<std::size_t>(i)].width,
                slot.hud[static_cast<std::size_t>(i)].height
            };
        }

        RimFGPresent::SetGeneratedFrameSource(
            g_generatedFrame.Get(),
            g_sceneWidth,
            g_sceneHeight,
            presentRects.data(),
            count,
            slot.frame.frameIndex);
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        if (eventId != 1 || !g_enabled.load(std::memory_order_relaxed))
            return;
        if (!g_d3d11Ready.load(std::memory_order_acquire) || !g_device || !g_context)
            return;

        AdoptPendingSceneTexture();
        const MetadataSlot slot = ReadLatestSlot();
        if (slot.frame.abiVersion != kAbiVersion)
            return;

        if (GenerateMidpointFrame())
            PublishGeneratedFrame(slot);
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
    RimFGPresent::Shutdown();
    ReleaseFrameResources();
    g_sceneTexture.Reset();
    g_interpolateCs.Reset();
    g_context.Reset();

    ID3D11Texture2D* pending = g_pendingSceneTexture.exchange(nullptr, std::memory_order_acq_rel);
    if (pending)
        pending->Release();

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
    if (enabled == 0)
        RimFGPresent::ClearGeneratedFrameSource();
}

extern "C" UNITY_INTERFACE_EXPORT int
RimFG_IsD3D11Ready()
{
    return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT int
RimFG_HasGeneratedFrame()
{
    return g_hasGeneratedFrame.load(std::memory_order_acquire) ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT void
RimFG_SubmitFrameState(const FrameMetadata* metadata, const HudRect* rects, int count)
{
    if (!metadata || metadata->abiVersion != kAbiVersion)
        return;

    const std::uint32_t next = g_writeSequence.load(std::memory_order_relaxed) + 1u;
    MetadataSlot& slot = g_slots[next & 1u];
    slot.frame = *metadata;

    const int clamped = count < 0 ? 0 : (count > kMaxHudRects ? kMaxHudRects : count);
    slot.hudCount = clamped;
    if (rects && clamped > 0)
        std::memcpy(slot.hud.data(), rects, sizeof(HudRect) * static_cast<std::size_t>(clamped));

    g_writeSequence.store(next, std::memory_order_release);
}

extern "C" UNITY_INTERFACE_EXPORT void
RimFG_SetSceneTexture(void* nativeTexture, int width, int height)
{
    ID3D11Texture2D* next = static_cast<ID3D11Texture2D*>(nativeTexture);
    if (next)
        next->AddRef();

    ID3D11Texture2D* oldPending = g_pendingSceneTexture.exchange(next, std::memory_order_acq_rel);
    if (oldPending)
        oldPending->Release();

    g_pendingWidth.store(width, std::memory_order_release);
    g_pendingHeight.store(height, std::memory_order_release);
    g_sceneTextureUpdatePending.store(true, std::memory_order_release);
}
