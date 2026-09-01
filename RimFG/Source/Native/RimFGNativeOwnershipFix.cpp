#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <condition_variable>
#include <cstdint>
#include <cstring>
#include <mutex>
#include <thread>

#include <windows.h>
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
    constexpr std::uint32_t kAbiVersion = 1;
    constexpr int kMaxHudRects = RimFGPresent::MaxHudRects;
    constexpr int kMaxBatchFrames = RimFGPresent::MaxBatchFrames;
    constexpr int kCaptureSlots = 4;
    constexpr int kPacketSlots = 16;
    constexpr int kOutputSets = 2;
    constexpr int kOperationalMaxBatchFrames = 8;

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
        ErrorSharedTexture = -7,
        ErrorWorkerDevice = -8
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
        float backwardFraction;
        float pad0;
        float pad1;
        float pad2;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch");
    static_assert(sizeof(HudRect) == 16, "Managed/native HUD ABI mismatch");
    static_assert(sizeof(MotionConstants) == 48, "Constant buffer alignment mismatch");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        int hudCount = 0;
    };

    struct CaptureTexture
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> keyed;
        HANDLE handle = nullptr;
    };

    struct CapturePacket
    {
        std::atomic<std::uint64_t> published{0};
        std::uint64_t sequence = 0;
        std::uint64_t resourceGeneration = 0;
        int captureIndex = -1;
        HANDLE handle = nullptr;
        HWND sourceWindow = nullptr;
        int width = 0;
        int height = 0;
        DXGI_FORMAT format = DXGI_FORMAT_UNKNOWN;
        std::int64_t qpc = 0;
        MetadataSlot metadata{};
    };

    struct OpenedCapture
    {
        HANDLE handle = nullptr;
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> keyed;
    };

    struct OutputTexture
    {
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> keyed;
        HANDLE handle = nullptr;
    };

    IUnityInterfaces* g_unityInterfaces = nullptr;
    IUnityGraphics* g_unityGraphics = nullptr;
    ID3D11Device* g_unityDevice = nullptr;
    ComPtr<ID3D11DeviceContext> g_unityContext;
    ComPtr<IDXGIAdapter> g_adapter;

    std::atomic<bool> g_enabled{false};
    std::atomic<bool> g_d3d11Ready{false};
    std::atomic<bool> g_hasGeneratedFrame{false};
    std::atomic<int> g_nativeStage{static_cast<int>(NativeStage::Idle)};

    std::array<MetadataSlot, 4> g_metadataSlots{};
    std::atomic<std::uint32_t> g_metadataSequence{0};

    std::array<CaptureTexture, kCaptureSlots> g_captureTextures{};
    int g_captureWidth = 0;
    int g_captureHeight = 0;
    DXGI_FORMAT g_captureFormat = DXGI_FORMAT_UNKNOWN;
    std::uint64_t g_captureResourceGeneration = 0;
    int g_nextCaptureIndex = 0;

    std::array<CapturePacket, kPacketSlots> g_packets{};
    std::atomic<std::uint64_t> g_latestPacketSequence{0};
    std::uint64_t g_nextPacketSequence = 0;

    std::thread g_workerThread;
    std::mutex g_workerCvMutex;
    std::condition_variable g_workerCv;
    std::atomic<bool> g_workerStop{false};

    ComPtr<ID3D11Device> g_workerDevice;
    ComPtr<ID3D11DeviceContext> g_workerContext;
    std::array<OpenedCapture, kCaptureSlots> g_openedCaptures{};
    std::array<std::array<OutputTexture, kOperationalMaxBatchFrames>, kOutputSets> g_outputSets{};
    int g_outputWidth = 0;
    int g_outputHeight = 0;
    DXGI_FORMAT g_outputFormat = DXGI_FORMAT_UNKNOWN;
    int g_nextOutputSet = 0;
    std::uint64_t g_nextBatchId = 0;

    ComPtr<ID3D11Texture2D> g_workerIncoming;
    ComPtr<ID3D11Texture2D> g_workerPrevious;
    ComPtr<ID3D11Texture2D> g_workerCurrent;
    ComPtr<ID3D11Texture2D> g_workerGenerated;
    ComPtr<ID3D11ShaderResourceView> g_workerPreviousSrv;
    ComPtr<ID3D11ShaderResourceView> g_workerCurrentSrv;
    ComPtr<ID3D11UnorderedAccessView> g_workerGeneratedUav;
    ComPtr<ID3D11ComputeShader> g_workerInterpolateCs;
    ComPtr<ID3D11Buffer> g_workerMotionConstants;
    RimFGFlow::Backend g_workerFlow;
    RimFGFlow::GpuBudget g_workerBudget;
    bool g_workerFlowReady = false;
    bool g_workerComputeReady = false;
    bool g_workerHaveCurrent = false;
    bool g_workerHavePrevious = false;
    MetadataSlot g_workerPreviousMetadata{};
    MetadataSlot g_workerCurrentMetadata{};
    std::int64_t g_workerPreviousQpc = 0;
    std::int64_t g_workerCurrentQpc = 0;
    std::uint64_t g_workerResourceGeneration = 0;
    std::uint64_t g_lastGeneratedSequence = 0;
    std::atomic<double> g_workerGpuMs{0.0};

    LARGE_INTEGER g_qpcFrequency{};

    constexpr const char* kInterpolationShader = R"HLSL(
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
    float BackwardFraction;
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
    float f = saturate(BackwardFraction);
    float safeZoom = max(ZoomScale, 0.001);
    float z = pow(safeZoom, f);
    float2 src = center + (p - center) / z;
    src += ImageShiftPixels * f;
    if (UseResidualFlow > 0.5 && FlowSize.x > 0 && FlowSize.y > 0)
    {
        float2 uv = p / max(float2(FrameSize), 1.0);
        int2 fp = clamp(int2(uv * float2(FlowSize)), int2(0, 0), FlowSize - int2(1, 1));
        src += ResidualFlow.Load(int3(fp, 0)) * f;
    }
    OutputFrame[id.xy] = CurrentFrame.Load(int3(ClampCoord(src), 0));
}
)HLSL";

    MetadataSlot ReadLatestMetadata()
    {
        MetadataSlot result{};
        for (int attempt = 0; attempt < 3; ++attempt)
        {
            const std::uint32_t before = g_metadataSequence.load(std::memory_order_acquire);
            result = g_metadataSlots[before & 3u];
            const std::uint32_t after = g_metadataSequence.load(std::memory_order_acquire);
            if (before == after) return result;
        }
        const std::uint32_t seq = g_metadataSequence.load(std::memory_order_acquire);
        return g_metadataSlots[seq & 3u];
    }

    void ClearCaptureResources()
    {
        for (CaptureTexture& slot : g_captureTextures)
        {
            slot.keyed.Reset();
            slot.texture.Reset();
            slot.handle = nullptr;
        }
        g_captureWidth = g_captureHeight = 0;
        g_captureFormat = DXGI_FORMAT_UNKNOWN;
        g_nextCaptureIndex = 0;
        ++g_captureResourceGeneration;
    }

    bool RefreshUnityDevice()
    {
        if (g_unityDevice && g_unityContext && g_adapter)
        {
            g_d3d11Ready.store(true, std::memory_order_release);
            return true;
        }
        if (!g_unityInterfaces) return false;
        auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (!d3d11) return false;
        g_unityDevice = d3d11->GetDevice();
        if (!g_unityDevice) return false;
        g_unityDevice->GetImmediateContext(&g_unityContext);
        ComPtr<IDXGIDevice> dxgi;
        if (SUCCEEDED(g_unityDevice->QueryInterface(__uuidof(IDXGIDevice), reinterpret_cast<void**>(dxgi.GetAddressOf()))) && dxgi)
            dxgi->GetAdapter(&g_adapter);
        const bool ready = g_unityContext != nullptr && g_adapter != nullptr;
        g_d3d11Ready.store(ready, std::memory_order_release);
        return ready;
    }

    bool EnsureCaptureResources(ID3D11Texture2D* source)
    {
        if (!source || !RefreshUnityDevice()) return false;
        ComPtr<ID3D11Device> sourceDevice;
        source->GetDevice(&sourceDevice);
        if (!sourceDevice || sourceDevice.Get() != g_unityDevice) return false;
        D3D11_TEXTURE2D_DESC src{};
        source->GetDesc(&src);
        if (!src.Width || !src.Height || src.SampleDesc.Count != 1) return false;
        if (g_captureTextures[0].texture && g_captureWidth == static_cast<int>(src.Width) &&
            g_captureHeight == static_cast<int>(src.Height) && g_captureFormat == src.Format)
            return true;

        ClearCaptureResources();
        D3D11_TEXTURE2D_DESC td{};
        td.Width = src.Width;
        td.Height = src.Height;
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = src.Format;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        for (CaptureTexture& slot : g_captureTextures)
        {
            if (FAILED(g_unityDevice->CreateTexture2D(&td, nullptr, &slot.texture)) || !slot.texture ||
                FAILED(slot.texture.As(&slot.keyed)) || !slot.keyed)
                return false;
            ComPtr<IDXGIResource> resource;
            if (FAILED(slot.texture.As(&resource)) || !resource || FAILED(resource->GetSharedHandle(&slot.handle)) || !slot.handle)
                return false;
        }
        g_captureWidth = static_cast<int>(src.Width);
        g_captureHeight = static_cast<int>(src.Height);
        g_captureFormat = src.Format;
        ++g_captureResourceGeneration;
        return true;
    }

    bool TryCaptureRealFrame(ID3D11Texture2D* source, HWND sourceWindow)
    {
        if (!g_enabled.load(std::memory_order_relaxed) || !source || !sourceWindow) return false;
        g_nativeStage.store(static_cast<int>(NativeStage::BackbufferSeen), std::memory_order_release);
        if (!EnsureCaptureResources(source) || !g_unityContext) return false;

        int selected = -1;
        for (int probe = 0; probe < kCaptureSlots; ++probe)
        {
            const int index = (g_nextCaptureIndex + probe) % kCaptureSlots;
            CaptureTexture& slot = g_captureTextures[index];
            if (slot.keyed && slot.keyed->AcquireSync(0, 0) == S_OK)
            {
                selected = index;
                g_nextCaptureIndex = (index + 1) % kCaptureSlots;
                break;
            }
        }
        if (selected < 0) return false;

        CaptureTexture& capture = g_captureTextures[selected];
        g_unityContext->CopyResource(capture.texture.Get(), source);

        LARGE_INTEGER qpc{};
        QueryPerformanceCounter(&qpc);
        const std::uint64_t seq = ++g_nextPacketSequence;
        CapturePacket& packet = g_packets[seq % kPacketSlots];
        packet.sequence = seq;
        packet.resourceGeneration = g_captureResourceGeneration;
        packet.captureIndex = selected;
        packet.handle = capture.handle;
        packet.sourceWindow = sourceWindow;
        packet.width = g_captureWidth;
        packet.height = g_captureHeight;
        packet.format = g_captureFormat;
        packet.qpc = qpc.QuadPart;
        packet.metadata = ReadLatestMetadata();
        packet.published.store(seq, std::memory_order_release);

        capture.keyed->ReleaseSync(1);
        g_latestPacketSequence.store(seq, std::memory_order_release);
        g_workerCv.notify_one();
        return true;
    }

    bool CreateWorkerDevice()
    {
        if (g_workerDevice && g_workerContext) return true;
        if (!g_adapter) return false;
        D3D_FEATURE_LEVEL created = D3D_FEATURE_LEVEL_11_0;
        const D3D_FEATURE_LEVEL requested[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        HRESULT hr = D3D11CreateDevice(g_adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, requested, 2, D3D11_SDK_VERSION,
            &g_workerDevice, &created, &g_workerContext);
        if (hr == E_INVALIDARG)
        {
            const D3D_FEATURE_LEVEL fallback[] = {D3D_FEATURE_LEVEL_11_0};
            hr = D3D11CreateDevice(g_adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                D3D11_CREATE_DEVICE_BGRA_SUPPORT, fallback, 1, D3D11_SDK_VERSION,
                &g_workerDevice, &created, &g_workerContext);
        }
        if (FAILED(hr) || !g_workerDevice || !g_workerContext)
        {
            g_nativeStage.store(static_cast<int>(NativeStage::ErrorWorkerDevice), std::memory_order_release);
            return false;
        }
        return true;
    }

    bool EnsureWorkerShader()
    {
        if (g_workerInterpolateCs && g_workerMotionConstants) return true;
        ComPtr<ID3DBlob> code, errors;
        const HRESULT hr = D3DCompile(kInterpolationShader, std::strlen(kInterpolationShader),
            "RimFG.OwnershipFixInterpolation", nullptr, nullptr, "CSMain", "cs_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors);
        if (FAILED(hr) || !code) return false;
        if (FAILED(g_workerDevice->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_workerInterpolateCs))) return false;
        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(MotionConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        return SUCCEEDED(g_workerDevice->CreateBuffer(&cb, nullptr, &g_workerMotionConstants));
    }

    void ClearOutputResources()
    {
        for (auto& set : g_outputSets)
            for (auto& slot : set)
            {
                slot.keyed.Reset();
                slot.texture.Reset();
                slot.handle = nullptr;
            }
        g_nextOutputSet = 0;
    }

    void ClearWorkerResources()
    {
        RimFGPresent::ClearGeneratedFrameSource();
        ClearOutputResources();
        for (auto& opened : g_openedCaptures)
        {
            opened.keyed.Reset();
            opened.texture.Reset();
            opened.handle = nullptr;
        }
        g_workerFlow.Shutdown();
        g_workerBudget.Shutdown();
        g_workerIncoming.Reset();
        g_workerPreviousSrv.Reset();
        g_workerCurrentSrv.Reset();
        g_workerGeneratedUav.Reset();
        g_workerPrevious.Reset();
        g_workerCurrent.Reset();
        g_workerGenerated.Reset();
        g_workerMotionConstants.Reset();
        g_workerInterpolateCs.Reset();
        g_workerContext.Reset();
        g_workerDevice.Reset();
        g_workerFlowReady = false;
        g_workerComputeReady = false;
        g_workerHaveCurrent = false;
        g_workerHavePrevious = false;
        g_outputWidth = g_outputHeight = 0;
        g_outputFormat = DXGI_FORMAT_UNKNOWN;
        g_workerResourceGeneration = 0;
        g_lastGeneratedSequence = 0;
        g_workerGpuMs.store(0.0, std::memory_order_release);
    }

    bool EnsureWorkerResources(const CapturePacket& packet)
    {
        if (!CreateWorkerDevice() || packet.width <= 0 || packet.height <= 0) return false;
        if (g_workerCurrent && g_outputWidth == packet.width && g_outputHeight == packet.height && g_outputFormat == packet.format)
            return true;

        ClearOutputResources();
        g_workerFlow.Shutdown();
        g_workerPreviousSrv.Reset();
        g_workerCurrentSrv.Reset();
        g_workerGeneratedUav.Reset();
        g_workerIncoming.Reset();
        g_workerPrevious.Reset();
        g_workerCurrent.Reset();
        g_workerGenerated.Reset();
        g_workerHaveCurrent = false;
        g_workerHavePrevious = false;

        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(packet.width);
        td.Height = static_cast<UINT>(packet.height);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = packet.format;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_workerIncoming)) ||
            FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_workerPrevious)) ||
            FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_workerCurrent))) return false;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_workerGenerated))) return false;
        if (FAILED(g_workerDevice->CreateShaderResourceView(g_workerPrevious.Get(), nullptr, &g_workerPreviousSrv)) ||
            FAILED(g_workerDevice->CreateShaderResourceView(g_workerCurrent.Get(), nullptr, &g_workerCurrentSrv)) ||
            FAILED(g_workerDevice->CreateUnorderedAccessView(g_workerGenerated.Get(), nullptr, &g_workerGeneratedUav)) || !EnsureWorkerShader()) return false;

        g_workerFlowReady = g_workerFlow.Initialize(g_workerDevice.Get(), packet.width, packet.height);
        g_workerBudget.Initialize(g_workerDevice.Get());
        g_workerComputeReady = true;
        g_outputWidth = packet.width;
        g_outputHeight = packet.height;
        g_outputFormat = packet.format;
        return true;
    }

    bool ReadPacket(std::uint64_t sequence, CapturePacket& out)
    {
        if (!sequence) return false;
        CapturePacket& packet = g_packets[sequence % kPacketSlots];
        if (packet.published.load(std::memory_order_acquire) != sequence) return false;
        out.sequence = packet.sequence;
        out.resourceGeneration = packet.resourceGeneration;
        out.captureIndex = packet.captureIndex;
        out.handle = packet.handle;
        out.sourceWindow = packet.sourceWindow;
        out.width = packet.width;
        out.height = packet.height;
        out.format = packet.format;
        out.qpc = packet.qpc;
        out.metadata = packet.metadata;
        out.published.store(sequence, std::memory_order_relaxed);
        return packet.published.load(std::memory_order_acquire) == sequence;
    }

    OpenedCapture* OpenCapture(const CapturePacket& packet)
    {
        if (packet.captureIndex < 0 || packet.captureIndex >= kCaptureSlots || !packet.handle) return nullptr;
        OpenedCapture& opened = g_openedCaptures[packet.captureIndex];
        if (opened.handle == packet.handle && opened.texture && opened.keyed) return &opened;
        opened.keyed.Reset();
        opened.texture.Reset();
        opened.handle = nullptr;
        if (FAILED(g_workerDevice->OpenSharedResource(packet.handle, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(opened.texture.GetAddressOf()))) || !opened.texture) return nullptr;
        if (FAILED(opened.texture.As(&opened.keyed)) || !opened.keyed)
        {
            opened.texture.Reset();
            return nullptr;
        }
        opened.handle = packet.handle;
        return &opened;
    }

    bool PullAndReturnCapture(const CapturePacket& packet)
    {
        if (!EnsureWorkerResources(packet)) return false;
        OpenedCapture* opened = OpenCapture(packet);
        if (!opened || !opened->keyed) return false;
        const HRESULT acquire = opened->keyed->AcquireSync(1, 50);
        if (acquire != S_OK) return false;
        g_workerContext->CopyResource(g_workerIncoming.Get(), opened->texture.Get());
        opened->keyed->ReleaseSync(0);

        if (g_workerHaveCurrent)
        {
            g_workerContext->CopyResource(g_workerPrevious.Get(), g_workerCurrent.Get());
            g_workerPreviousMetadata = g_workerCurrentMetadata;
            g_workerPreviousQpc = g_workerCurrentQpc;
            g_workerHavePrevious = true;
        }
        g_workerContext->CopyResource(g_workerCurrent.Get(), g_workerIncoming.Get());
        g_workerCurrentMetadata = packet.metadata;
        g_workerCurrentQpc = packet.qpc;
        g_workerHaveCurrent = true;
        return true;
    }

    bool EnsureOutputTexture(OutputTexture& slot)
    {
        if (slot.texture && slot.keyed && slot.handle) return true;
        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(g_outputWidth);
        td.Height = static_cast<UINT>(g_outputHeight);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = g_outputFormat;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &slot.texture)) || !slot.texture ||
            FAILED(slot.texture.As(&slot.keyed)) || !slot.keyed) return false;
        ComPtr<IDXGIResource> resource;
        if (FAILED(slot.texture.As(&resource)) || !resource || FAILED(resource->GetSharedHandle(&slot.handle)) || !slot.handle) return false;
        return true;
    }

    bool AcquireOutputSet(int setIndex, int count)
    {
        int acquired = 0;
        for (; acquired < count; ++acquired)
        {
            OutputTexture& slot = g_outputSets[setIndex][acquired];
            if (!EnsureOutputTexture(slot) || slot.keyed->AcquireSync(0, 0) != S_OK) break;
        }
        if (acquired == count) return true;
        for (int i = 0; i < acquired; ++i) g_outputSets[setIndex][i].keyed->ReleaseSync(0);
        return false;
    }

    RimFGFlow::MotionInput BuildMotionInput()
    {
        RimFGFlow::MotionInput result{};
        result.width = g_outputWidth;
        result.height = g_outputHeight;
        result.zoomScale = 1.0f;
        if (!g_workerHavePrevious || !g_workerHaveCurrent || g_workerPreviousMetadata.frame.orthographicSize <= 0.001f ||
            g_workerCurrentMetadata.frame.orthographicSize <= 0.001f || g_outputHeight <= 0) return result;
        const float ortho = (g_workerPreviousMetadata.frame.orthographicSize + g_workerCurrentMetadata.frame.orthographicSize) * 0.5f;
        const float pixelsPerWorld = static_cast<float>(g_outputHeight) / (2.0f * ortho);
        result.imageShiftX = (g_workerCurrentMetadata.frame.cameraX - g_workerPreviousMetadata.frame.cameraX) * pixelsPerWorld;
        result.imageShiftY = -(g_workerCurrentMetadata.frame.cameraZ - g_workerPreviousMetadata.frame.cameraZ) * pixelsPerWorld;
        result.zoomScale = g_workerCurrentMetadata.frame.orthographicSize / g_workerPreviousMetadata.frame.orthographicSize;
        return result;
    }

    bool UploadMotion(const RimFGFlow::MotionInput& motion, bool useFlow, float backward)
    {
        MotionConstants c{};
        c.imageShiftX = motion.imageShiftX;
        c.imageShiftY = motion.imageShiftY;
        c.zoomScale = motion.zoomScale;
        c.useResidualFlow = useFlow ? 1.0f : 0.0f;
        c.width = g_outputWidth;
        c.height = g_outputHeight;
        c.flowWidth = useFlow ? g_workerFlow.FlowWidth() : 0;
        c.flowHeight = useFlow ? g_workerFlow.FlowHeight() : 0;
        c.backwardFraction = std::max(0.0f, std::min(1.0f, backward));
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_workerContext->Map(g_workerMotionConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return false;
        std::memcpy(mapped.pData, &c, sizeof(c));
        g_workerContext->Unmap(g_workerMotionConstants.Get(), 0);
        return true;
    }

    bool DispatchInterpolation(const RimFGFlow::MotionInput& motion, bool useFlow, float backward)
    {
        if (!g_workerComputeReady || !UploadMotion(motion, useFlow, backward)) return false;
        ID3D11ShaderResourceView* srvs[3] = {g_workerPreviousSrv.Get(), g_workerCurrentSrv.Get(), useFlow ? g_workerFlow.FlowSrv() : nullptr};
        ID3D11UnorderedAccessView* uavs[1] = {g_workerGeneratedUav.Get()};
        ID3D11Buffer* cbs[1] = {g_workerMotionConstants.Get()};
        g_workerContext->CSSetShader(g_workerInterpolateCs.Get(), nullptr, 0);
        g_workerContext->CSSetShaderResources(0, 3, srvs);
        g_workerContext->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        g_workerContext->CSSetConstantBuffers(0, 1, cbs);
        g_workerContext->Dispatch(static_cast<UINT>((g_outputWidth + 7) / 8), static_cast<UINT>((g_outputHeight + 7) / 8), 1);
        ID3D11ShaderResourceView* nullSrvs[3] = {nullptr, nullptr, nullptr};
        ID3D11UnorderedAccessView* nullUavs[1] = {nullptr};
        ID3D11Buffer* nullCbs[1] = {nullptr};
        g_workerContext->CSSetShaderResources(0, 3, nullSrvs);
        g_workerContext->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        g_workerContext->CSSetConstantBuffers(0, 1, nullCbs);
        g_workerContext->CSSetShader(nullptr, nullptr, 0);
        return true;
    }

    void CopyProtectedRects(ID3D11Texture2D* from, ID3D11Texture2D* to, const MetadataSlot& metadata)
    {
        const int count = std::max(0, std::min(metadata.hudCount, kMaxHudRects));
        for (int i = 0; i < count; ++i)
        {
            const HudRect& r = metadata.hud[static_cast<std::size_t>(i)];
            const LONG left = std::max<LONG>(0, static_cast<LONG>(std::floor(r.x)));
            const LONG top = std::max<LONG>(0, static_cast<LONG>(std::floor(r.y)));
            const LONG right = std::min<LONG>(g_outputWidth, static_cast<LONG>(std::ceil(r.x + r.width)));
            const LONG bottom = std::min<LONG>(g_outputHeight, static_cast<LONG>(std::ceil(r.y + r.height)));
            if (right <= left || bottom <= top) continue;
            D3D11_BOX box{static_cast<UINT>(left), static_cast<UINT>(top), 0, static_cast<UINT>(right), static_cast<UINT>(bottom), 1};
            g_workerContext->CopySubresourceRegion(to, 0, static_cast<UINT>(left), static_cast<UINT>(top), 0, from, 0, &box);
        }
    }

    int ComputeBatchFrameCount(double realInterval)
    {
        const int target = std::max(1, RimFGPresent::GetTargetOutputFps());
        const int monitor = std::max(24, RimFGPresent::MonitorRefreshHz());
        const int effective = std::min(target, monitor);
        const int desired = static_cast<int>(std::lround(std::max(0.001, realInterval) * static_cast<double>(effective)));
        return std::max(1, std::min(kOperationalMaxBatchFrames, desired));
    }

    bool BuildAndPublishBatch(std::uint64_t sequence, HWND sourceWindow)
    {
        if (!g_workerHavePrevious || !g_workerHaveCurrent || sequence == g_lastGeneratedSequence) return false;
        double realInterval = 1.0 / std::max(1.0, RimFGPresent::EstimatedBaseFps());
        if (g_qpcFrequency.QuadPart > 0 && g_workerCurrentQpc > g_workerPreviousQpc)
        {
            const double measured = static_cast<double>(g_workerCurrentQpc - g_workerPreviousQpc) / static_cast<double>(g_qpcFrequency.QuadPart);
            if (measured >= 0.001 && measured <= 0.5) realInterval = measured;
        }
        const int frameCount = ComputeBatchFrameCount(realInterval);
        const int setIndex = g_nextOutputSet++ % kOutputSets;
        if (!AcquireOutputSet(setIndex, frameCount)) return false;

        const auto start = std::chrono::steady_clock::now();
        g_workerBudget.Poll(g_workerContext.Get());
        const RimFGFlow::MotionInput motion = BuildMotionInput();
        const bool wantFlow = g_workerBudget.Tier() == RimFGFlow::QualityTier::ResidualFlow && g_workerFlowReady;
        g_workerBudget.Begin(g_workerContext.Get());
        const bool useFlow = wantFlow && g_workerFlow.Dispatch(g_workerContext.Get(), g_workerPreviousSrv.Get(), g_workerCurrentSrv.Get(), motion);

        for (int i = 0; i < frameCount; ++i)
        {
            OutputTexture& out = g_outputSets[setIndex][i];
            const float t = static_cast<float>(i + 1) / static_cast<float>(frameCount);
            if (i == frameCount - 1)
            {
                g_workerContext->CopyResource(out.texture.Get(), g_workerCurrent.Get());
            }
            else if (DispatchInterpolation(motion, useFlow, 1.0f - t))
            {
                if (t < 0.5f) CopyProtectedRects(g_workerPrevious.Get(), g_workerGenerated.Get(), g_workerPreviousMetadata);
                else CopyProtectedRects(g_workerCurrent.Get(), g_workerGenerated.Get(), g_workerCurrentMetadata);
                g_workerContext->CopyResource(out.texture.Get(), g_workerGenerated.Get());
            }
            else
            {
                g_workerContext->CopyResource(out.texture.Get(), g_workerCurrent.Get());
                g_nativeStage.store(static_cast<int>(NativeStage::DuplicateFallback), std::memory_order_release);
            }
        }
        g_workerBudget.End(g_workerContext.Get());
        g_workerContext->Flush();
        for (int i = 0; i < frameCount; ++i) g_outputSets[setIndex][i].keyed->ReleaseSync(1);

        RimFGPresent::SharedFrameBatch batch{};
        batch.batchId = ++g_nextBatchId;
        batch.frameIndex = g_workerCurrentMetadata.frame.frameIndex;
        batch.width = g_outputWidth;
        batch.height = g_outputHeight;
        batch.format = g_outputFormat;
        batch.sourceWindow = sourceWindow;
        batch.frameCount = frameCount;
        batch.realFrameIndex = frameCount - 1;
        for (int i = 0; i < frameCount; ++i) batch.handles[i] = g_outputSets[setIndex][i].handle;
        RimFGPresent::PublishSharedFrameBatch(batch);

        const double elapsedMs = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - start).count();
        const double old = g_workerGpuMs.load(std::memory_order_relaxed);
        g_workerGpuMs.store(old <= 0.0 ? elapsedMs : old + (elapsedMs - old) * 0.15, std::memory_order_relaxed);
        g_lastGeneratedSequence = sequence;
        g_hasGeneratedFrame.store(frameCount > 1, std::memory_order_release);
        g_nativeStage.store(static_cast<int>(NativeStage::Generated), std::memory_order_release);
        return true;
    }

    void ResetOpenedCapturesForGeneration(std::uint64_t generation)
    {
        for (auto& opened : g_openedCaptures)
        {
            opened.keyed.Reset();
            opened.texture.Reset();
            opened.handle = nullptr;
        }
        g_workerHaveCurrent = false;
        g_workerHavePrevious = false;
        g_workerResourceGeneration = generation;
    }

    void WorkerMain()
    {
        std::uint64_t consumed = 0;
        while (!g_workerStop.load(std::memory_order_acquire))
        {
            const std::uint64_t latest = g_latestPacketSequence.load(std::memory_order_acquire);
            if (latest <= consumed)
            {
                std::unique_lock<std::mutex> lock(g_workerCvMutex);
                g_workerCv.wait_for(lock, std::chrono::milliseconds(8));
                continue;
            }

            HWND newestWindow = nullptr;
            std::uint64_t newestSequence = 0;
            while (consumed < latest)
            {
                const std::uint64_t next = consumed + 1;
                CapturePacket packet{};
                if (!ReadPacket(next, packet))
                {
                    consumed = next;
                    continue;
                }
                if (packet.resourceGeneration != g_workerResourceGeneration)
                    ResetOpenedCapturesForGeneration(packet.resourceGeneration);

                // Critical ownership invariant: every published capture packet is
                // acquired and returned in sequence. We may skip generating from an
                // intermediate frame, but we never skip its keyed-mutex handback.
                if (PullAndReturnCapture(packet))
                {
                    newestWindow = packet.sourceWindow;
                    newestSequence = packet.sequence;
                }
                consumed = next;
            }

            if (newestSequence != 0 && g_workerHavePrevious && g_workerHaveCurrent)
                BuildAndPublishBatch(newestSequence, newestWindow);
            else if (g_workerHaveCurrent)
                g_nativeStage.store(static_cast<int>(NativeStage::HistoryPrimed), std::memory_order_release);
        }
        ClearWorkerResources();
    }

    void StartWorker()
    {
        if (g_workerThread.joinable()) return;
        g_workerStop.store(false, std::memory_order_release);
        g_workerThread = std::thread(WorkerMain);
    }

    void StopWorker()
    {
        g_workerStop.store(true, std::memory_order_release);
        g_workerCv.notify_all();
        if (g_workerThread.joinable()) g_workerThread.join();
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType type)
    {
        if (type == kUnityGfxDeviceEventInitialize || type == kUnityGfxDeviceEventAfterReset)
            RefreshUnityDevice();
        else if (type == kUnityGfxDeviceEventBeforeReset || type == kUnityGfxDeviceEventShutdown)
        {
            ClearCaptureResources();
            g_unityContext.Reset();
            g_adapter.Reset();
            g_unityDevice = nullptr;
            g_d3d11Ready.store(false, std::memory_order_release);
        }
    }

    void UNITY_INTERFACE_API OnRenderEvent(int) {}
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    QueryPerformanceFrequency(&g_qpcFrequency);
    g_unityInterfaces = interfaces;
    g_unityGraphics = interfaces ? interfaces->Get<IUnityGraphics>() : nullptr;
    if (g_unityGraphics)
    {
        g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent);
        OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize);
    }
    RimFGPresent::SetBackbufferGenerationCallback(&TryCaptureRealFrame);
    StartWorker();
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    g_enabled.store(false, std::memory_order_release);
    RimFGPresent::SetBackbufferGenerationCallback(nullptr);
    StopWorker();
    RimFGPresent::Shutdown();
    ClearCaptureResources();
    if (g_unityGraphics) g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_unityContext.Reset();
    g_adapter.Reset();
    g_unityDevice = nullptr;
    g_unityGraphics = nullptr;
    g_unityInterfaces = nullptr;
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API RimFG_GetRenderEventFunc()
{
    RimFGPresent::SetBackbufferGenerationCallback(&TryCaptureRealFrame);
    StartWorker();
    return OnRenderEvent;
}

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetEnabled(int enabled)
{
    const bool on = enabled != 0;
    g_enabled.store(on, std::memory_order_release);
    if (on)
    {
        RimFGPresent::SetBackbufferGenerationCallback(&TryCaptureRealFrame);
        StartWorker();
    }
    else
    {
        RimFGPresent::ClearGeneratedFrameSource();
        g_hasGeneratedFrame.store(false, std::memory_order_release);
    }
}

extern "C" UNITY_INTERFACE_EXPORT int RimFG_IsD3D11Ready()
{
    if (!g_d3d11Ready.load(std::memory_order_acquire)) RefreshUnityDevice();
    return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0;
}
extern "C" UNITY_INTERFACE_EXPORT int RimFG_HasGeneratedFrame() { return g_hasGeneratedFrame.load(std::memory_order_acquire) ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetNativeStage() { return g_nativeStage.load(std::memory_order_acquire); }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetGpuQualityTier() { return g_workerFlowReady ? 2 : 1; }
extern "C" UNITY_INTERFACE_EXPORT double RimFG_GetGpuFrameGenerationMs() { return g_workerGpuMs.load(std::memory_order_acquire); }

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SubmitFrameState(const FrameMetadata* metadata, const HudRect* rects, int count)
{
    if (!metadata || metadata->abiVersion != kAbiVersion) return;
    const std::uint32_t next = g_metadataSequence.load(std::memory_order_relaxed) + 1u;
    MetadataSlot& slot = g_metadataSlots[next & 3u];
    slot.frame = *metadata;
    slot.hudCount = std::max(0, std::min(count, kMaxHudRects));
    if (rects && slot.hudCount > 0)
        std::memcpy(slot.hud.data(), rects, sizeof(HudRect) * static_cast<std::size_t>(slot.hudCount));
    g_metadataSequence.store(next, std::memory_order_release);
}

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetSceneTexture(void*, int, int) {}
