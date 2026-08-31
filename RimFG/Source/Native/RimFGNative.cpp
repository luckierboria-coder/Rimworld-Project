#include <algorithm>
#include <array>
#include <atomic>
#include <cmath>
#include <cstdint>
#include <cstring>

#include <d3d11.h>
#include <d3dcompiler.h>
#include <wrl/client.h>

#include "IUnityInterface.h"
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"
#include "PresentHook.h"
#include "OpticalFlowBackend.h"
#include "GpuBudget.h"

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

    struct MotionConstants
    {
        float imageShiftX;
        float imageShiftY;
        float zoomScale;
        float useResidualFlow;
        std::int32_t width;
        std::int32_t height;
        std::int32_t flowWidth;
        std::int32_t flowHeight;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch for FrameMetadata");
    static_assert(sizeof(HudRect) == 16, "Managed/native ABI mismatch for HudRect");
    static_assert(sizeof(MotionConstants) == 32, "D3D11 constant buffer alignment mismatch");
    static_assert(sizeof(RimFGPresent::HudRectPx) == sizeof(HudRect), "Present HUD ABI mismatch");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        std::int32_t hudCount = 0;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_device = nullptr;
    ComPtr<ID3D11DeviceContext> g_context;

    std::atomic<bool> g_enabled{false};
    std::atomic<bool> g_d3d11Ready{false};
    std::atomic<bool> g_hasGeneratedFrame{false};
    std::atomic<std::uint32_t> g_writeSequence{0};
    alignas(64) std::array<MetadataSlot, 2> g_slots{};

    ComPtr<ID3D11Texture2D> g_previousFrame;
    ComPtr<ID3D11Texture2D> g_currentFrame;
    ComPtr<ID3D11Texture2D> g_generatedFrame;
    ComPtr<ID3D11ShaderResourceView> g_previousSrv;
    ComPtr<ID3D11ShaderResourceView> g_currentSrv;
    ComPtr<ID3D11UnorderedAccessView> g_generatedUav;
    ComPtr<ID3D11ComputeShader> g_interpolateCs;
    ComPtr<ID3D11Buffer> g_motionConstants;

    RimFGFlow::Backend g_flowBackend;
    RimFGFlow::GpuBudget g_gpuBudget;

    bool g_haveHistory = false;
    bool g_havePreviousMetadata = false;
    FrameMetadata g_previousMetadata{};
    int g_frameWidth = 0;
    int g_frameHeight = 0;
    DXGI_FORMAT g_frameFormat = DXGI_FORMAT_UNKNOWN;

    constexpr const char* kInterpolateShader = R"HLSL(
Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame  : register(t1);
Texture2D<float2> ResidualFlow  : register(t2);
RWTexture2D<float4> OutputFrame : register(u0);

cbuffer Motion : register(b0)
{
    float2 ImageShiftPixels;
    float ZoomScale;
    float UseResidualFlow;
    int2 FrameSize;
    int2 FlowSize;
};

int2 ClampCoord(float2 p)
{
    int2 q = int2(round(p));
    return clamp(q, int2(0, 0), FrameSize - int2(1, 1));
}

float LumaDiff(float3 a, float3 b)
{
    float3 d = abs(a - b);
    return dot(d, float3(0.299, 0.587, 0.114));
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FrameSize.x || id.y >= (uint)FrameSize.y) return;

    float2 p = float2(id.xy);
    float2 center = (float2(FrameSize) - 1.0) * 0.5;
    float safeZoom = max(ZoomScale, 0.001);
    float midpointZoom = sqrt(safeZoom);

    float2 centered = p - center - ImageShiftPixels * 0.5;
    float2 prevCoord = center + centered / midpointZoom;
    float2 currCoord = center + centered * (safeZoom / midpointZoom) + ImageShiftPixels;

    if (UseResidualFlow > 0.5 && FlowSize.x > 0 && FlowSize.y > 0)
    {
        int2 fp = clamp(int2(id.xy / 2), int2(0, 0), FlowSize - int2(1, 1));
        float2 residual = ResidualFlow.Load(int3(fp, 0));
        prevCoord += residual * 0.5;
        currCoord -= residual * 0.5;
    }

    float4 a = PreviousFrame.Load(int3(ClampCoord(prevCoord), 0));
    float4 b = CurrentFrame.Load(int3(ClampCoord(currCoord), 0));
    float disagreement = LumaDiff(a.rgb, b.rgb);
    float currentBias = smoothstep(0.16, 0.48, disagreement) * 0.18;
    OutputFrame[id.xy] = lerp(a, b, 0.5 + currentBias);
}
)HLSL";

    void ReleaseFrameResources()
    {
        RimFGPresent::ClearGeneratedFrameSource();
        g_flowBackend.Shutdown();
        g_gpuBudget.Shutdown();
        g_previousSrv.Reset();
        g_currentSrv.Reset();
        g_generatedUav.Reset();
        g_previousFrame.Reset();
        g_currentFrame.Reset();
        g_generatedFrame.Reset();
        g_motionConstants.Reset();
        g_haveHistory = false;
        g_havePreviousMetadata = false;
        g_hasGeneratedFrame.store(false, std::memory_order_release);
        g_frameWidth = 0;
        g_frameHeight = 0;
        g_frameFormat = DXGI_FORMAT_UNKNOWN;
    }

    bool EnsureComputeShader()
    {
        if (g_interpolateCs && g_motionConstants) return true;
        if (!g_device) return false;

        ComPtr<ID3DBlob> bytecode;
        ComPtr<ID3DBlob> errors;
        const HRESULT hr = D3DCompile(kInterpolateShader, std::strlen(kInterpolateShader), "RimFG.CameraZoomFlowInterpolateCS", nullptr, nullptr, "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &bytecode, &errors);
        if (FAILED(hr) || !bytecode) return false;
        if (FAILED(g_device->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_interpolateCs))) return false;

        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(MotionConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        return SUCCEEDED(g_device->CreateBuffer(&cb, nullptr, &g_motionConstants));
    }

    bool EnsureFrameResources(ID3D11Texture2D* source)
    {
        if (!source) return false;

        ComPtr<ID3D11Device> sourceDevice;
        if (FAILED(source->GetDevice(&sourceDevice)) || !sourceDevice) return false;
        if (!g_device || g_device != sourceDevice.Get())
        {
            ReleaseFrameResources();
            g_context.Reset();
            g_device = sourceDevice.Get();
            g_device->GetImmediateContext(&g_context);
            g_d3d11Ready.store(g_context != nullptr, std::memory_order_release);
        }
        if (!g_device || !g_context) return false;

        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (!desc.Width || !desc.Height || desc.SampleDesc.Count != 1) return false;

        if (g_previousFrame && g_currentFrame && g_generatedFrame &&
            g_frameWidth == static_cast<int>(desc.Width) &&
            g_frameHeight == static_cast<int>(desc.Height) &&
            g_frameFormat == desc.Format)
            return true;

        ReleaseFrameResources();

        D3D11_TEXTURE2D_DESC history = desc;
        history.MipLevels = 1;
        history.ArraySize = 1;
        history.Usage = D3D11_USAGE_DEFAULT;
        history.CPUAccessFlags = 0;
        history.MiscFlags = 0;
        history.BindFlags = D3D11_BIND_SHADER_RESOURCE;

        if (FAILED(g_device->CreateTexture2D(&history, nullptr, &g_previousFrame))) return false;
        if (FAILED(g_device->CreateTexture2D(&history, nullptr, &g_currentFrame))) return false;

        D3D11_TEXTURE2D_DESC generated = history;
        generated.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&generated, nullptr, &g_generatedFrame))) return false;
        if (FAILED(g_device->CreateShaderResourceView(g_previousFrame.Get(), nullptr, &g_previousSrv))) return false;
        if (FAILED(g_device->CreateShaderResourceView(g_currentFrame.Get(), nullptr, &g_currentSrv))) return false;
        if (FAILED(g_device->CreateUnorderedAccessView(g_generatedFrame.Get(), nullptr, &g_generatedUav))) return false;
        if (!EnsureComputeShader()) return false;
        if (!g_flowBackend.Initialize(g_device, static_cast<int>(desc.Width), static_cast<int>(desc.Height))) return false;
        g_gpuBudget.Initialize(g_device);

        g_frameWidth = static_cast<int>(desc.Width);
        g_frameHeight = static_cast<int>(desc.Height);
        g_frameFormat = desc.Format;
        return true;
    }

    bool RefreshD3D11Device()
    {
        if (g_device && g_context)
        {
            g_d3d11Ready.store(true, std::memory_order_release);
            return true;
        }

        if (g_unityInterfaces)
        {
            auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();
            if (d3d11)
            {
                g_device = d3d11->GetDevice();
                if (g_device) g_device->GetImmediateContext(&g_context);
            }
        }

        if ((!g_device || !g_context) && RimFGPresent::HasUnitySwapChain())
        {
            IDXGISwapChain* chain = RimFGPresent::GetUnitySwapChain();
            if (chain)
            {
                ComPtr<ID3D11Device> capturedDevice;
                if (SUCCEEDED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(capturedDevice.GetAddressOf()))) && capturedDevice)
                {
                    g_device = capturedDevice.Get();
                    g_device->GetImmediateContext(&g_context);
                }
            }
        }

        const bool ready = g_device != nullptr && g_context != nullptr;
        g_d3d11Ready.store(ready, std::memory_order_release);
        return ready;
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

    RimFGFlow::MotionInput BuildMotionInput(const FrameMetadata& previous, const FrameMetadata& current)
    {
        RimFGFlow::MotionInput result{};
        result.width = g_frameWidth;
        result.height = g_frameHeight;
        result.zoomScale = 1.0f;
        if (current.orthographicSize <= 0.001f || previous.orthographicSize <= 0.001f || g_frameHeight <= 0) return result;

        const float ortho = (current.orthographicSize + previous.orthographicSize) * 0.5f;
        const float pixelsPerWorldUnit = static_cast<float>(g_frameHeight) / (2.0f * ortho);
        result.imageShiftX = -(current.cameraX - previous.cameraX) * pixelsPerWorldUnit;
        result.imageShiftY = (current.cameraZ - previous.cameraZ) * pixelsPerWorldUnit;
        result.zoomScale = previous.orthographicSize / current.orthographicSize;

        const float maxShift = static_cast<float>(std::max(g_frameWidth, g_frameHeight)) * 0.25f;
        if (std::fabs(result.imageShiftX) > maxShift || std::fabs(result.imageShiftY) > maxShift || result.zoomScale < 0.67f || result.zoomScale > 1.5f)
        {
            result.imageShiftX = 0.0f;
            result.imageShiftY = 0.0f;
            result.zoomScale = 1.0f;
        }
        return result;
    }

    bool UploadMotionConstants(const RimFGFlow::MotionInput& motion, bool useFlow)
    {
        if (!g_context || !g_motionConstants) return false;
        MotionConstants c{};
        c.imageShiftX = motion.imageShiftX;
        c.imageShiftY = motion.imageShiftY;
        c.zoomScale = motion.zoomScale;
        c.useResidualFlow = useFlow ? 1.0f : 0.0f;
        c.width = g_frameWidth;
        c.height = g_frameHeight;
        c.flowWidth = useFlow ? g_flowBackend.FlowWidth() : 0;
        c.flowHeight = useFlow ? g_flowBackend.FlowHeight() : 0;

        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_context->Map(g_motionConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return false;
        std::memcpy(mapped.pData, &c, sizeof(c));
        g_context->Unmap(g_motionConstants.Get(), 0);
        return true;
    }

    bool GenerateMidpointFrame(ID3D11Texture2D* source, const MetadataSlot& slot)
    {
        if (!source || slot.frame.abiVersion != kAbiVersion || !EnsureFrameResources(source)) return false;
        if (!g_context || !g_previousFrame || !g_currentFrame || !g_generatedFrame || !g_previousSrv || !g_currentSrv || !g_generatedUav || !g_interpolateCs || !g_motionConstants) return false;

        g_context->CopyResource(g_currentFrame.Get(), source);
        if (!g_haveHistory || !g_havePreviousMetadata)
        {
            g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
            g_haveHistory = true;
            g_previousMetadata = slot.frame;
            g_havePreviousMetadata = true;
            return false;
        }

        g_gpuBudget.Poll(g_context.Get());
        RimFGFlow::QualityTier tier = g_gpuBudget.Tier();

        // During telemetry warm-up there may be no completed GPU timestamps yet.
        // Do not classify that state as a hard bypass: camera-only interpolation is
        // deliberately cheap and is required to bootstrap the first generated frame.
        if (tier == RimFGFlow::QualityTier::Bypass && g_gpuBudget.EmaMilliseconds() <= 0.0)
            tier = RimFGFlow::QualityTier::CameraOnly;

        if (tier == RimFGFlow::QualityTier::Bypass)
        {
            g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
            g_previousMetadata = slot.frame;
            g_hasGeneratedFrame.store(false, std::memory_order_release);
            RimFGPresent::ClearGeneratedFrameSource();
            return false;
        }

        const RimFGFlow::MotionInput motion = BuildMotionInput(g_previousMetadata, slot.frame);
        g_gpuBudget.Begin(g_context.Get());
        bool useFlow = false;
        if (tier == RimFGFlow::QualityTier::ResidualFlow)
            useFlow = g_flowBackend.Dispatch(g_context.Get(), g_previousSrv.Get(), g_currentSrv.Get(), motion);

        if (!UploadMotionConstants(motion, useFlow))
        {
            g_gpuBudget.End(g_context.Get());
            return false;
        }

        ID3D11ShaderResourceView* srvs[3] = { g_previousSrv.Get(), g_currentSrv.Get(), useFlow ? g_flowBackend.FlowSrv() : nullptr };
        ID3D11UnorderedAccessView* uavs[1] = { g_generatedUav.Get() };
        ID3D11Buffer* cbs[1] = { g_motionConstants.Get() };
        g_context->CSSetShader(g_interpolateCs.Get(), nullptr, 0);
        g_context->CSSetShaderResources(0, 3, srvs);
        g_context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, cbs);
        g_context->Dispatch(static_cast<UINT>((g_frameWidth + 7) / 8), static_cast<UINT>((g_frameHeight + 7) / 8), 1);

        ID3D11ShaderResourceView* nullSrvs[3] = { nullptr, nullptr, nullptr };
        ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
        ID3D11Buffer* nullCbs[1] = { nullptr };
        g_context->CSSetShaderResources(0, 3, nullSrvs);
        g_context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, nullCbs);
        g_context->CSSetShader(nullptr, nullptr, 0);
        g_gpuBudget.End(g_context.Get());

        g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
        g_previousMetadata = slot.frame;
        g_hasGeneratedFrame.store(true, std::memory_order_release);
        return true;
    }

    void PublishGeneratedFrame(const MetadataSlot& slot)
    {
        if (!g_generatedFrame || !g_hasGeneratedFrame.load(std::memory_order_acquire)) return;
        std::array<RimFGPresent::HudRectPx, kMaxHudRects> rects{};
        const int count = std::max(0, std::min(slot.hudCount, kMaxHudRects));
        for (int i = 0; i < count; ++i)
            rects[static_cast<std::size_t>(i)] = { slot.hud[static_cast<std::size_t>(i)].x, slot.hud[static_cast<std::size_t>(i)].y, slot.hud[static_cast<std::size_t>(i)].width, slot.hud[static_cast<std::size_t>(i)].height };
        RimFGPresent::SetGeneratedFrameSource(g_generatedFrame.Get(), g_frameWidth, g_frameHeight, rects.data(), count, slot.frame.frameIndex);
    }

    bool GenerateFromBackbuffer(ID3D11Texture2D* backBuffer)
    {
        if (!g_enabled.load(std::memory_order_relaxed) || !backBuffer) return false;
        const MetadataSlot slot = ReadLatestSlot();
        if (slot.frame.abiVersion != kAbiVersion) return false;
        if (!GenerateMidpointFrame(backBuffer, slot)) return false;
        PublishGeneratedFrame(slot);
        return true;
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        // Kept as a stable ABI entrypoint for old managed builds. Generation now
        // occurs from the actual DXGI backbuffer inside the Present hook.
        (void)eventId;
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unityInterfaces = unityInterfaces;
    g_unityGraphics = unityInterfaces ? unityInterfaces->Get<IUnityGraphics>() : nullptr;
    if (g_unityGraphics)
    {
        g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
    RimFGPresent::SetBackbufferGenerationCallback(&GenerateFromBackbuffer);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics) g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_enabled.store(false, std::memory_order_release);
    RimFGPresent::SetBackbufferGenerationCallback(nullptr);
    RimFGPresent::Shutdown();
    ReleaseFrameResources();
    g_interpolateCs.Reset();
    g_context.Reset();
    g_d3d11Ready.store(false, std::memory_order_release);
    g_device = nullptr;
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API RimFG_GetRenderEventFunc()
{
    RimFGPresent::SetBackbufferGenerationCallback(&GenerateFromBackbuffer);
    return OnRenderEvent;
}

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetEnabled(int enabled)
{
    g_enabled.store(enabled != 0, std::memory_order_release);
    if (enabled)
        RimFGPresent::SetBackbufferGenerationCallback(&GenerateFromBackbuffer);
    else
        RimFGPresent::ClearGeneratedFrameSource();
}
extern "C" UNITY_INTERFACE_EXPORT int RimFG_IsD3D11Ready()
{
    if (!g_d3d11Ready.load(std::memory_order_acquire)) RefreshD3D11Device();
    return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0;
}
extern "C" UNITY_INTERFACE_EXPORT int RimFG_HasGeneratedFrame() { return g_hasGeneratedFrame.load(std::memory_order_acquire) ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetGpuQualityTier() { return static_cast<int>(g_gpuBudget.Tier()); }
extern "C" UNITY_INTERFACE_EXPORT double RimFG_GetGpuFrameGenerationMs() { return g_gpuBudget.EmaMilliseconds(); }

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SubmitFrameState(const FrameMetadata* metadata, const HudRect* rects, int count)
{
    if (!metadata || metadata->abiVersion != kAbiVersion) return;
    const std::uint32_t next = g_writeSequence.load(std::memory_order_relaxed) + 1u;
    MetadataSlot& slot = g_slots[next & 1u];
    slot.frame = *metadata;
    const int clamped = std::max(0, std::min(count, kMaxHudRects));
    slot.hudCount = clamped;
    if (rects && clamped > 0) std::memcpy(slot.hud.data(), rects, sizeof(HudRect) * static_cast<std::size_t>(clamped));
    g_writeSequence.store(next, std::memory_order_release);
}

// Legacy ABI. The DXGI backbuffer is now the authoritative source.
extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetSceneTexture(void*, int, int) {}
