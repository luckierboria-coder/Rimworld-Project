#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <mutex>

#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi.h>
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
    using Clock = std::chrono::steady_clock;
    constexpr std::uint32_t kAbiVersion = 1;
    constexpr int kMaxHudRects = RimFGPresent::MaxHudRects;
    constexpr int kMaxBatchFrames = RimFGPresent::MaxBatchFrames;
    constexpr int kSharedBatchSetCount = 2;
    constexpr std::size_t kMetadataSlotCount = 4;

    enum class NativeStage : int
    {
        Idle = 0,
        BackbufferSeen = 1,
        HistoryPrimed = 2,
        Generated = 3,
        DuplicateFallback = 4,
        ErrorNoDevice = -1,
        ErrorBadBackbuffer = -2,
        ErrorHistoryTexture = -3,
        ErrorShader = -4,
        ErrorOutputTexture = -5,
        ErrorMotionConstants = -6,
        ErrorSharedTexture = -7
    };

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
        float predictionFraction;
        float pad0;
        float pad1;
        float pad2;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch for FrameMetadata");
    static_assert(sizeof(HudRect) == 16, "Managed/native HUD ABI mismatch");
    static_assert(sizeof(MotionConstants) == 48, "D3D11 constant buffer alignment mismatch");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        std::int32_t hudCount = 0;
    };

    struct SharedTextureSlot
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> keyedMutex;
        HANDLE sharedHandle = nullptr;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_device = nullptr;
    ComPtr<ID3D11DeviceContext> g_context;

    std::atomic<bool> g_enabled{false};
    std::atomic<bool> g_d3d11Ready{false};
    std::atomic<bool> g_hasGeneratedFrame{false};
    std::atomic<int> g_nativeStage{static_cast<int>(NativeStage::Idle)};
    std::atomic<std::uint32_t> g_writeSequence{0};
    alignas(64) std::array<MetadataSlot, kMetadataSlotCount> g_slots{};

    ComPtr<ID3D11Texture2D> g_previousFrame;
    ComPtr<ID3D11Texture2D> g_currentFrame;
    ComPtr<ID3D11Texture2D> g_generatedFrame;
    ComPtr<ID3D11ShaderResourceView> g_previousSrv;
    ComPtr<ID3D11ShaderResourceView> g_currentSrv;
    ComPtr<ID3D11UnorderedAccessView> g_generatedUav;
    ComPtr<ID3D11ComputeShader> g_interpolateCs;
    ComPtr<ID3D11Buffer> g_motionConstants;

    std::array<std::array<SharedTextureSlot, kMaxBatchFrames>, kSharedBatchSetCount> g_sharedSets{};
    int g_sharedWidth = 0;
    int g_sharedHeight = 0;
    DXGI_FORMAT g_sharedFormat = DXGI_FORMAT_UNKNOWN;
    int g_nextSharedSet = 0;
    std::uint64_t g_nextBatchId = 0;

    RimFGFlow::Backend g_flowBackend;
    RimFGFlow::GpuBudget g_gpuBudget;
    bool g_flowReady = false;
    bool g_computeReady = false;
    bool g_haveHistory = false;
    bool g_havePreviousMetadata = false;
    FrameMetadata g_previousMetadata{};
    int g_frameWidth = 0;
    int g_frameHeight = 0;
    DXGI_FORMAT g_frameFormat = DXGI_FORMAT_UNKNOWN;
    Clock::time_point g_lastCaptureAt{};

    // Only Unity's render/Present thread records D3D11 commands now. The presenter
    // never enters this mutex or this immediate context.
    std::mutex g_resourceMutex;

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
    float PredictionFraction;
    float3 Padding;
};
int2 ClampCoord(float2 p)
{
    int2 q = int2(round(p));
    return clamp(q, int2(0, 0), FrameSize - int2(1, 1));
}
[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= (uint)FrameSize.x || id.y >= (uint)FrameSize.y) return;
    float2 p = float2(id.xy);
    float2 center = (float2(FrameSize) - 1.0) * 0.5;

    // This is a buffered interpolation pass. PredictionFraction is how far we
    // move BACKWARD from Current toward Previous: 1 = previous-like, 0 = current.
    float f = saturate(PredictionFraction);
    float safeZoom = max(ZoomScale, 0.001);
    float interpolatedZoom = pow(safeZoom, f);
    float2 sourceCoord = center + (p - center - ImageShiftPixels * f) / interpolatedZoom;
    if (UseResidualFlow > 0.5 && FlowSize.x > 0 && FlowSize.y > 0)
    {
        int2 fp = clamp(int2(id.xy / 2), int2(0, 0), FlowSize - int2(1, 1));
        float2 residual = ResidualFlow.Load(int3(fp, 0));
        sourceCoord -= residual * f;
    }
    OutputFrame[id.xy] = CurrentFrame.Load(int3(ClampCoord(sourceCoord), 0));
}
)HLSL";

    MetadataSlot ReadLatestSlot()
    {
        MetadataSlot result{};
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            const std::uint32_t before = g_writeSequence.load(std::memory_order_acquire);
            result = g_slots[before % kMetadataSlotCount];
            const std::uint32_t after = g_writeSequence.load(std::memory_order_acquire);
            if (before == after) return result;
        }
        const std::uint32_t seq = g_writeSequence.load(std::memory_order_acquire);
        return g_slots[seq % kMetadataSlotCount];
    }

    void ReleaseSharedResources()
    {
        for (auto& set : g_sharedSets)
        {
            for (SharedTextureSlot& slot : set)
            {
                slot.keyedMutex.Reset();
                slot.texture.Reset();
                slot.sharedHandle = nullptr;
            }
        }
        g_sharedWidth = g_sharedHeight = 0;
        g_sharedFormat = DXGI_FORMAT_UNKNOWN;
        g_nextSharedSet = 0;
    }

    void ReleaseFrameResourcesUnlocked()
    {
        RimFGPresent::ClearGeneratedFrameSource();
        ReleaseSharedResources();
        g_flowBackend.Shutdown();
        g_gpuBudget.Shutdown();
        g_flowReady = false;
        g_computeReady = false;
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
        g_lastCaptureAt = Clock::time_point{};
    }

    bool EnsureComputeShader()
    {
        if (g_interpolateCs && g_motionConstants) return true;
        if (!g_device) return false;
        ComPtr<ID3DBlob> bytecode;
        ComPtr<ID3DBlob> errors;
        const HRESULT hr = D3DCompile(kInterpolateShader, std::strlen(kInterpolateShader), "RimFG.BufferedInterpolationCS",
            nullptr, nullptr, "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &bytecode, &errors);
        if (FAILED(hr) || !bytecode)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorShader), std::memory_order_release);
            return false;
        }
        if (FAILED(g_device->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_interpolateCs)))
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorShader), std::memory_order_release);
            return false;
        }
        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(MotionConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(g_device->CreateBuffer(&cb, nullptr, &g_motionConstants)))
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorMotionConstants), std::memory_order_release);
            return false;
        }
        return true;
    }

    bool EnsureSharedResources(const D3D11_TEXTURE2D_DESC& sourceDesc)
    {
        if (g_sharedWidth == static_cast<int>(sourceDesc.Width) &&
            g_sharedHeight == static_cast<int>(sourceDesc.Height) &&
            g_sharedFormat == sourceDesc.Format && g_sharedSets[0][0].texture)
            return true;

        ReleaseSharedResources();
        D3D11_TEXTURE2D_DESC desc{};
        desc.Width = sourceDesc.Width;
        desc.Height = sourceDesc.Height;
        desc.MipLevels = 1;
        desc.ArraySize = 1;
        desc.Format = sourceDesc.Format;
        desc.SampleDesc.Count = 1;
        desc.Usage = D3D11_USAGE_DEFAULT;
        desc.BindFlags = 0;
        desc.CPUAccessFlags = 0;
        desc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;

        for (auto& set : g_sharedSets)
        {
            for (SharedTextureSlot& slot : set)
            {
                if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &slot.texture)) || !slot.texture ||
                    FAILED(slot.texture.As(&slot.keyedMutex)) || !slot.keyedMutex)
                {
                    ReleaseSharedResources();
                    g_nativeStage.store(static_cast<int>(NativeStage::ErrorSharedTexture), std::memory_order_release);
                    return false;
                }
                ComPtr<IDXGIResource> resource;
                if (FAILED(slot.texture.As(&resource)) || !resource || FAILED(resource->GetSharedHandle(&slot.sharedHandle)) || !slot.sharedHandle)
                {
                    ReleaseSharedResources();
                    g_nativeStage.store(static_cast<int>(NativeStage::ErrorSharedTexture), std::memory_order_release);
                    return false;
                }
            }
        }
        g_sharedWidth = static_cast<int>(sourceDesc.Width);
        g_sharedHeight = static_cast<int>(sourceDesc.Height);
        g_sharedFormat = sourceDesc.Format;
        return true;
    }

    bool EnsureFrameResources(ID3D11Texture2D* source)
    {
        if (!source)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorBadBackbuffer), std::memory_order_release);
            return false;
        }
        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(&sourceDevice);
        if (!sourceDevice)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorNoDevice), std::memory_order_release);
            return false;
        }

        if (!g_device || g_device != sourceDevice.Get())
        {
            ReleaseFrameResourcesUnlocked();
            g_interpolateCs.Reset();
            g_context.Reset();
            g_device = sourceDevice.Get();
            g_device->GetImmediateContext(&g_context);
            g_d3d11Ready.store(g_context != nullptr, std::memory_order_release);
        }
        if (!g_device || !g_context)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorNoDevice), std::memory_order_release);
            return false;
        }

        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (!desc.Width || !desc.Height || desc.SampleDesc.Count != 1)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorBadBackbuffer), std::memory_order_release);
            return false;
        }
        if (g_previousFrame && g_currentFrame && g_generatedFrame &&
            g_frameWidth == static_cast<int>(desc.Width) && g_frameHeight == static_cast<int>(desc.Height) && g_frameFormat == desc.Format)
            return EnsureSharedResources(desc);

        ReleaseFrameResourcesUnlocked();

        D3D11_TEXTURE2D_DESC history = desc;
        history.MipLevels = 1;
        history.ArraySize = 1;
        history.Usage = D3D11_USAGE_DEFAULT;
        history.CPUAccessFlags = 0;
        history.MiscFlags = 0;
        history.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&history, nullptr, &g_previousFrame)) ||
            FAILED(g_device->CreateTexture2D(&history, nullptr, &g_currentFrame)))
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorHistoryTexture), std::memory_order_release);
            return false;
        }

        D3D11_TEXTURE2D_DESC generated = history;
        generated.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&generated, nullptr, &g_generatedFrame)))
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorOutputTexture), std::memory_order_release);
            return false;
        }

        UINT support = 0;
        const bool queryOk = SUCCEEDED(g_device->CheckFormatSupport(desc.Format, &support));
        const bool canSrv = queryOk && (support & D3D11_FORMAT_SUPPORT_SHADER_SAMPLE) != 0;
        const bool canUav = queryOk && (support & D3D11_FORMAT_SUPPORT_TYPED_UNORDERED_ACCESS_VIEW) != 0;
        g_computeReady = false;
        if (canSrv && canUav &&
            SUCCEEDED(g_device->CreateShaderResourceView(g_previousFrame.Get(), nullptr, &g_previousSrv)) &&
            SUCCEEDED(g_device->CreateShaderResourceView(g_currentFrame.Get(), nullptr, &g_currentSrv)) &&
            SUCCEEDED(g_device->CreateUnorderedAccessView(g_generatedFrame.Get(), nullptr, &g_generatedUav)) && EnsureComputeShader())
            g_computeReady = true;

        g_flowReady = g_computeReady && g_flowBackend.Initialize(g_device, static_cast<int>(desc.Width), static_cast<int>(desc.Height));
        g_gpuBudget.Initialize(g_device);
        g_frameWidth = static_cast<int>(desc.Width);
        g_frameHeight = static_cast<int>(desc.Height);
        g_frameFormat = desc.Format;
        return EnsureSharedResources(desc);
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
                ComPtr<ID3D11Device> captured;
                if (SUCCEEDED(chain->GetDevice(__uuidof(ID3D11Device), reinterpret_cast<void**>(captured.GetAddressOf()))) && captured)
                {
                    g_device = captured.Get();
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
        {
            std::lock_guard<std::mutex> lock(g_resourceMutex);
            ReleaseFrameResourcesUnlocked();
            g_interpolateCs.Reset();
            g_context.Reset();
            g_d3d11Ready.store(false, std::memory_order_release);
            g_device = nullptr;
            break;
        }
        default:
            break;
        }
    }

    RimFGFlow::MotionInput BuildMotionInput(const FrameMetadata& previous, const FrameMetadata& current)
    {
        RimFGFlow::MotionInput result{};
        result.width = g_frameWidth;
        result.height = g_frameHeight;
        result.zoomScale = 1.0f;
        if (current.orthographicSize <= 0.001f || previous.orthographicSize <= 0.001f || g_frameHeight <= 0)
            return result;
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

    bool UploadMotionConstants(const RimFGFlow::MotionInput& motion, bool useFlow, float backwardFraction)
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
        c.predictionFraction = std::max(0.0f, std::min(1.0f, backwardFraction));
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_context->Map(g_motionConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return false;
        std::memcpy(mapped.pData, &c, sizeof(c));
        g_context->Unmap(g_motionConstants.Get(), 0);
        return true;
    }

    void CopyProtectedRects(ID3D11Texture2D* from, ID3D11Texture2D* to, const MetadataSlot& slot)
    {
        if (!g_context || !from || !to) return;
        for (int i = 0; i < slot.hudCount; ++i)
        {
            const HudRect& r = slot.hud[static_cast<std::size_t>(i)];
            const LONG left = std::max<LONG>(0, static_cast<LONG>(std::floor(r.x)));
            const LONG top = std::max<LONG>(0, static_cast<LONG>(std::floor(r.y)));
            const LONG right = std::min<LONG>(g_frameWidth, static_cast<LONG>(std::ceil(r.x + r.width)));
            const LONG bottom = std::min<LONG>(g_frameHeight, static_cast<LONG>(std::ceil(r.y + r.height)));
            if (right <= left || bottom <= top) continue;
            D3D11_BOX box{static_cast<UINT>(left), static_cast<UINT>(top), 0,
                static_cast<UINT>(right), static_cast<UINT>(bottom), 1};
            g_context->CopySubresourceRegion(to, 0, static_cast<UINT>(left), static_cast<UINT>(top), 0, from, 0, &box);
        }
    }

    bool DispatchInterpolation(const RimFGFlow::MotionInput& motion, bool useFlow, float backwardFraction)
    {
        if (!g_computeReady || !g_context || !g_currentSrv || !g_generatedUav || !g_interpolateCs || !g_motionConstants)
            return false;
        if (!UploadMotionConstants(motion, useFlow, backwardFraction)) return false;

        ID3D11ShaderResourceView* srvs[3] = {g_previousSrv.Get(), g_currentSrv.Get(), useFlow ? g_flowBackend.FlowSrv() : nullptr};
        ID3D11UnorderedAccessView* uavs[1] = {g_generatedUav.Get()};
        ID3D11Buffer* cbs[1] = {g_motionConstants.Get()};
        g_context->CSSetShader(g_interpolateCs.Get(), nullptr, 0);
        g_context->CSSetShaderResources(0, 3, srvs);
        g_context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, cbs);
        g_context->Dispatch(static_cast<UINT>((g_frameWidth + 7) / 8), static_cast<UINT>((g_frameHeight + 7) / 8), 1);
        ID3D11ShaderResourceView* nullSrvs[3] = {nullptr, nullptr, nullptr};
        ID3D11UnorderedAccessView* nullUavs[1] = {nullptr};
        ID3D11Buffer* nullCbs[1] = {nullptr};
        g_context->CSSetShaderResources(0, 3, nullSrvs);
        g_context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, nullCbs);
        g_context->CSSetShader(nullptr, nullptr, 0);
        return true;
    }

    bool AcquireBatchSet(int setIndex, int frameCount)
    {
        int acquired = 0;
        for (; acquired < frameCount; ++acquired)
        {
            SharedTextureSlot& slot = g_sharedSets[setIndex][acquired];
            if (!slot.keyedMutex || slot.keyedMutex->AcquireSync(0, 0) != S_OK)
                break;
        }
        if (acquired == frameCount) return true;
        for (int i = 0; i < acquired; ++i)
            g_sharedSets[setIndex][i].keyedMutex->ReleaseSync(0);
        return false;
    }

    void ReleaseBatchToPresenter(int setIndex, int frameCount)
    {
        for (int i = 0; i < frameCount; ++i)
            g_sharedSets[setIndex][i].keyedMutex->ReleaseSync(1);
    }

    int ComputeBatchFrameCount(double realInterval)
    {
        const int target = std::max(1, RimFGPresent::GetTargetOutputFps());
        const int monitor = std::max(24, RimFGPresent::MonitorRefreshHz());
        const int effectiveTarget = std::min(target, monitor);
        const int count = static_cast<int>(std::lround(std::max(0.001, realInterval) * static_cast<double>(effectiveTarget)));
        return std::max(1, std::min(kMaxBatchFrames, count));
    }

    bool PublishRealOnlyBatch(const MetadataSlot& slot, HWND sourceWindow)
    {
        const int setIndex = g_nextSharedSet++ % kSharedBatchSetCount;
        if (!AcquireBatchSet(setIndex, 1)) return false;
        g_context->CopyResource(g_sharedSets[setIndex][0].texture.Get(), g_currentFrame.Get());
        g_context->Flush();
        ReleaseBatchToPresenter(setIndex, 1);

        RimFGPresent::SharedFrameBatch batch{};
        batch.batchId = ++g_nextBatchId;
        batch.frameIndex = slot.frame.frameIndex;
        batch.width = g_frameWidth;
        batch.height = g_frameHeight;
        batch.format = g_frameFormat;
        batch.sourceWindow = sourceWindow;
        batch.frameCount = 1;
        batch.realFrameIndex = 0;
        batch.handles[0] = g_sharedSets[setIndex][0].sharedHandle;
        RimFGPresent::PublishSharedFrameBatch(batch);
        return true;
    }

    bool BuildAndPublishInterpolationBatch(const MetadataSlot& slot, HWND sourceWindow, double realInterval)
    {
        const int frameCount = ComputeBatchFrameCount(realInterval);
        const int setIndex = g_nextSharedSet++ % kSharedBatchSetCount;
        if (!AcquireBatchSet(setIndex, frameCount)) return false;

        RimFGFlow::QualityTier tier = g_gpuBudget.Tier();
        if (tier == RimFGFlow::QualityTier::Bypass) tier = RimFGFlow::QualityTier::CameraZoomOnly;
        const RimFGFlow::MotionInput motion = BuildMotionInput(g_previousMetadata, slot.frame);
        bool useFlow = false;

        g_gpuBudget.Begin(g_context.Get());
        if (tier == RimFGFlow::QualityTier::ResidualFlow && g_computeReady && g_flowReady && g_previousSrv && g_currentSrv)
            useFlow = g_flowBackend.Dispatch(g_context.Get(), g_previousSrv.Get(), g_currentSrv.Get(), motion);

        for (int i = 0; i < frameCount; ++i)
        {
            const float t = static_cast<float>(i + 1) / static_cast<float>(frameCount);
            SharedTextureSlot& shared = g_sharedSets[setIndex][i];
            if (i == frameCount - 1)
            {
                // Last slot is the exact real Current frame. This establishes a
                // one-real-frame delayed timeline with no future extrapolation.
                g_context->CopyResource(shared.texture.Get(), g_currentFrame.Get());
                continue;
            }

            const float backward = 1.0f - t;
            if (DispatchInterpolation(motion, useFlow, backward))
            {
                // Text/UI is never warped. Use nearest-real UI so names do not smear.
                ID3D11Texture2D* uiSource = t < 0.5f ? g_previousFrame.Get() : g_currentFrame.Get();
                CopyProtectedRects(uiSource, g_generatedFrame.Get(), slot);
                g_context->CopyResource(shared.texture.Get(), g_generatedFrame.Get());
            }
            else
            {
                g_context->CopyResource(shared.texture.Get(), g_currentFrame.Get());
                g_nativeStage.store(static_cast<int>(NativeStage::DuplicateFallback), std::memory_order_release);
            }
        }
        g_gpuBudget.End(g_context.Get());
        g_context->Flush();
        ReleaseBatchToPresenter(setIndex, frameCount);

        RimFGPresent::SharedFrameBatch batch{};
        batch.batchId = ++g_nextBatchId;
        batch.frameIndex = slot.frame.frameIndex;
        batch.width = g_frameWidth;
        batch.height = g_frameHeight;
        batch.format = g_frameFormat;
        batch.sourceWindow = sourceWindow;
        batch.frameCount = frameCount;
        batch.realFrameIndex = frameCount - 1;
        for (int i = 0; i < frameCount; ++i)
            batch.handles[i] = g_sharedSets[setIndex][i].sharedHandle;
        RimFGPresent::PublishSharedFrameBatch(batch);

        g_nativeStage.store(static_cast<int>(NativeStage::Generated), std::memory_order_release);
        g_hasGeneratedFrame.store(frameCount > 1, std::memory_order_release);
        return true;
    }

    bool CaptureFromBackbuffer(ID3D11Texture2D* source, HWND sourceWindow)
    {
        if (!g_enabled.load(std::memory_order_relaxed) || !source || !sourceWindow) return false;
        std::unique_lock<std::mutex> lock(g_resourceMutex, std::try_to_lock);
        if (!lock.owns_lock()) return false;

        const MetadataSlot slot = ReadLatestSlot();
        g_nativeStage.store(static_cast<int>(NativeStage::BackbufferSeen), std::memory_order_release);
        if (slot.frame.abiVersion != kAbiVersion || slot.frame.frameIndex == 0 || !EnsureFrameResources(source)) return false;
        if (!g_context || !g_previousFrame || !g_currentFrame || !g_generatedFrame) return false;

        const Clock::time_point now = Clock::now();
        double realInterval = 1.0 / std::max(1.0, RimFGPresent::EstimatedBaseFps());
        if (g_lastCaptureAt.time_since_epoch().count() != 0)
        {
            const double measured = std::chrono::duration<double>(now - g_lastCaptureAt).count();
            if (measured >= 0.001 && measured <= 0.5) realInterval = measured;
        }
        g_lastCaptureAt = now;

        g_context->CopyResource(g_currentFrame.Get(), source);
        if (!g_haveHistory || !g_havePreviousMetadata)
        {
            g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
            g_previousMetadata = slot.frame;
            g_haveHistory = true;
            g_havePreviousMetadata = true;
            const bool published = PublishRealOnlyBatch(slot, sourceWindow);
            g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);
            return published;
        }

        g_gpuBudget.Poll(g_context.Get());
        const bool published = BuildAndPublishInterpolationBatch(slot, sourceWindow, realInterval);

        // Only after the whole delayed batch is generated may Current become Previous.
        g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
        g_previousMetadata = slot.frame;
        return published;
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId) { (void)eventId; }
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
    RimFGPresent::SetBackbufferGenerationCallback(&CaptureFromBackbuffer);
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics) g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_enabled.store(false, std::memory_order_release);
    RimFGPresent::SetBackbufferGenerationCallback(nullptr);
    RimFGPresent::Shutdown();
    {
        std::lock_guard<std::mutex> lock(g_resourceMutex);
        ReleaseFrameResourcesUnlocked();
        g_interpolateCs.Reset();
        g_context.Reset();
        g_d3d11Ready.store(false, std::memory_order_release);
        g_device = nullptr;
    }
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API RimFG_GetRenderEventFunc()
{
    RimFGPresent::SetBackbufferGenerationCallback(&CaptureFromBackbuffer);
    return OnRenderEvent;
}

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetEnabled(int enabled)
{
    const bool on = enabled != 0;
    g_enabled.store(on, std::memory_order_release);
    if (on)
    {
        RimFGPresent::SetBackbufferGenerationCallback(&CaptureFromBackbuffer);
    }
    else
    {
        RimFGPresent::ClearGeneratedFrameSource();
        std::lock_guard<std::mutex> lock(g_resourceMutex);
        g_hasGeneratedFrame.store(false, std::memory_order_release);
    }
}

extern "C" UNITY_INTERFACE_EXPORT int RimFG_IsD3D11Ready()
{
    if (!g_d3d11Ready.load(std::memory_order_acquire)) RefreshD3D11Device();
    return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0;
}

extern "C" UNITY_INTERFACE_EXPORT int RimFG_HasGeneratedFrame() { return g_hasGeneratedFrame.load(std::memory_order_acquire) ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetNativeStage() { return g_nativeStage.load(std::memory_order_acquire); }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetGpuQualityTier() { return static_cast<int>(g_gpuBudget.Tier()); }
extern "C" UNITY_INTERFACE_EXPORT double RimFG_GetGpuFrameGenerationMs() { return g_gpuBudget.EmaMilliseconds(); }

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SubmitFrameState(const FrameMetadata* metadata, const HudRect* rects, int count)
{
    if (!metadata || metadata->abiVersion != kAbiVersion) return;
    const std::uint32_t next = g_writeSequence.load(std::memory_order_relaxed) + 1u;
    MetadataSlot& slot = g_slots[next % kMetadataSlotCount];
    slot.frame = *metadata;
    const int clamped = std::max(0, std::min(count, kMaxHudRects));
    slot.hudCount = clamped;
    if (rects && clamped > 0)
        std::memcpy(slot.hud.data(), rects, sizeof(HudRect) * static_cast<std::size_t>(clamped));
    g_writeSequence.store(next, std::memory_order_release);
}

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetSceneTexture(void*, int, int) {}
