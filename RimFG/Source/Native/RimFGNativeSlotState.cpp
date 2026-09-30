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

using Microsoft::WRL::ComPtr;

namespace
{
    constexpr std::uint32_t kAbiVersion = 1;
    constexpr int kMaxHudRects = RimFGPresent::MaxHudRects;
    constexpr int kCaptureSlots = 4;
    constexpr int kOutputSets = 2;
    constexpr int kMaxFramesPerBatch = 8;

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
        float backwardFraction;
        std::int32_t width;
        std::int32_t height;
        float pad0;
        float pad1;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch");
    static_assert(sizeof(HudRect) == 16, "Managed/native HUD ABI mismatch");
    static_assert(sizeof(MotionConstants) == 32, "Constant buffer alignment mismatch");

    struct MetadataSlot
    {
        FrameMetadata frame{};
        std::array<HudRect, kMaxHudRects> hud{};
        int hudCount = 0;
    };

    // state: 0=FREE, 1=CAPTURING, 2=READY_FOR_WORKER
    struct CaptureSlot
    {
        std::atomic<int> state{0};
        ComPtr<ID3D11Texture2D> texture;
        ComPtr<IDXGIKeyedMutex> keyed;
        HANDLE handle = nullptr;
        std::uint64_t sequence = 0;
        std::uint64_t generation = 0;
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

    std::array<CaptureSlot, kCaptureSlots> g_captureSlots{};
    int g_captureWidth = 0;
    int g_captureHeight = 0;
    DXGI_FORMAT g_captureFormat = DXGI_FORMAT_UNKNOWN;
    std::uint64_t g_captureGeneration = 1;
    std::uint64_t g_nextSequence = 0;
    int g_nextCaptureIndex = 0;

    std::thread g_workerThread;
    std::mutex g_workerCvMutex;
    std::condition_variable g_workerCv;
    std::atomic<bool> g_workerStop{false};

    ComPtr<ID3D11Device> g_workerDevice;
    ComPtr<ID3D11DeviceContext> g_workerContext;
    std::array<OpenedCapture, kCaptureSlots> g_openedCaptures{};

    ComPtr<ID3D11Texture2D> g_previous;
    ComPtr<ID3D11Texture2D> g_current;
    ComPtr<ID3D11Texture2D> g_generated;
    ComPtr<ID3D11ShaderResourceView> g_currentSrv;
    ComPtr<ID3D11UnorderedAccessView> g_generatedUav;
    ComPtr<ID3D11ComputeShader> g_interpolateCs;
    ComPtr<ID3D11Buffer> g_motionConstants;

    std::array<std::array<OutputTexture, kMaxFramesPerBatch>, kOutputSets> g_outputSets{};
    int g_nextOutputSet = 0;
    std::uint64_t g_nextBatchId = 0;

    int g_workerWidth = 0;
    int g_workerHeight = 0;
    DXGI_FORMAT g_workerFormat = DXGI_FORMAT_UNKNOWN;
    bool g_haveCurrent = false;
    bool g_havePrevious = false;
    MetadataSlot g_previousMetadata{};
    MetadataSlot g_currentMetadata{};
    std::int64_t g_previousQpc = 0;
    std::int64_t g_currentQpc = 0;
    std::uint64_t g_workerGeneration = 0;
    std::atomic<double> g_workerMs{0.0};

    LARGE_INTEGER g_qpcFrequency{};

    constexpr const char* kInterpolationShader = R"HLSL(
Texture2D<float4> CurrentFrame : register(t0);
RWTexture2D<float4> OutputFrame : register(u0);
cbuffer Motion : register(b0)
{
    float2 ImageShiftPixels;
    float ZoomScale;
    float BackwardFraction;
    int2 FrameSize;
    float2 Padding;
};
int2 ClampCoord(float2 p)
{
    return clamp(int2(round(p)), int2(0,0), FrameSize - int2(1,1));
}
[numthreads(8,8,1)]
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
        return g_metadataSlots[g_metadataSequence.load(std::memory_order_acquire) & 3u];
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
        const bool ready = g_unityContext && g_adapter;
        g_d3d11Ready.store(ready, std::memory_order_release);
        return ready;
    }

    void ClearCaptureResources()
    {
        ++g_captureGeneration;
        for (auto& slot : g_captureSlots)
        {
            slot.state.store(0, std::memory_order_release);
            slot.keyed.Reset();
            slot.texture.Reset();
            slot.handle = nullptr;
            slot.sequence = 0;
        }
        g_captureWidth = g_captureHeight = 0;
        g_captureFormat = DXGI_FORMAT_UNKNOWN;
        g_nextCaptureIndex = 0;
    }

    bool EnsureCaptureResources(ID3D11Texture2D* source)
    {
        if (!source || !RefreshUnityDevice()) return false;
        D3D11_TEXTURE2D_DESC src{};
        source->GetDesc(&src);
        if (!src.Width || !src.Height || src.SampleDesc.Count != 1) return false;
        if (g_captureSlots[0].texture && g_captureWidth == static_cast<int>(src.Width) &&
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

        for (auto& slot : g_captureSlots)
        {
            if (FAILED(g_unityDevice->CreateTexture2D(&td, nullptr, &slot.texture)) || !slot.texture ||
                FAILED(slot.texture.As(&slot.keyed)) || !slot.keyed)
            {
                ClearCaptureResources();
                g_nativeStage.store(static_cast<int>(NativeStage::ErrorSharedTexture), std::memory_order_release);
                return false;
            }
            ComPtr<IDXGIResource> resource;
            if (FAILED(slot.texture.As(&resource)) || !resource || FAILED(resource->GetSharedHandle(&slot.handle)) || !slot.handle)
            {
                ClearCaptureResources();
                g_nativeStage.store(static_cast<int>(NativeStage::ErrorSharedTexture), std::memory_order_release);
                return false;
            }
            slot.state.store(0, std::memory_order_release);
        }
        g_captureWidth = static_cast<int>(src.Width);
        g_captureHeight = static_cast<int>(src.Height);
        g_captureFormat = src.Format;
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
            int expected = 0;
            if (!g_captureSlots[index].state.compare_exchange_strong(expected, 1, std::memory_order_acq_rel)) continue;
            if (g_captureSlots[index].keyed->AcquireSync(0, 0) == S_OK)
            {
                selected = index;
                g_nextCaptureIndex = (index + 1) % kCaptureSlots;
                break;
            }
            g_captureSlots[index].state.store(0, std::memory_order_release);
        }
        if (selected < 0) return false;

        CaptureSlot& slot = g_captureSlots[selected];
        g_unityContext->CopyResource(slot.texture.Get(), source);
        LARGE_INTEGER qpc{};
        QueryPerformanceCounter(&qpc);
        slot.sequence = ++g_nextSequence;
        slot.generation = g_captureGeneration;
        slot.sourceWindow = sourceWindow;
        slot.width = g_captureWidth;
        slot.height = g_captureHeight;
        slot.format = g_captureFormat;
        slot.qpc = qpc.QuadPart;
        slot.metadata = ReadLatestMetadata();
        slot.keyed->ReleaseSync(1);
        slot.state.store(2, std::memory_order_release);
        g_workerCv.notify_one();
        return true;
    }

    bool CreateWorkerDevice()
    {
        if (g_workerDevice && g_workerContext) return true;
        if (!g_adapter) return false;
        D3D_FEATURE_LEVEL created = D3D_FEATURE_LEVEL_11_0;
        const D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        HRESULT hr = D3D11CreateDevice(g_adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
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

    void ClearWorkerResources()
    {
        RimFGPresent::ClearGeneratedFrameSource();
        for (auto& o : g_openedCaptures) { o.keyed.Reset(); o.texture.Reset(); o.handle = nullptr; }
        for (auto& set : g_outputSets)
            for (auto& o : set) { o.keyed.Reset(); o.texture.Reset(); o.handle = nullptr; }
        g_currentSrv.Reset();
        g_generatedUav.Reset();
        g_previous.Reset();
        g_current.Reset();
        g_generated.Reset();
        g_motionConstants.Reset();
        g_interpolateCs.Reset();
        g_workerContext.Reset();
        g_workerDevice.Reset();
        g_haveCurrent = g_havePrevious = false;
        g_workerWidth = g_workerHeight = 0;
        g_workerFormat = DXGI_FORMAT_UNKNOWN;
        g_workerGeneration = 0;
        g_workerMs.store(0.0, std::memory_order_release);
    }

    bool EnsureWorkerResources(int width, int height, DXGI_FORMAT format)
    {
        if (!CreateWorkerDevice()) return false;
        if (g_current && g_workerWidth == width && g_workerHeight == height && g_workerFormat == format) return true;

        RimFGPresent::ClearGeneratedFrameSource();
        for (auto& set : g_outputSets)
            for (auto& o : set) { o.keyed.Reset(); o.texture.Reset(); o.handle = nullptr; }
        g_currentSrv.Reset(); g_generatedUav.Reset(); g_previous.Reset(); g_current.Reset(); g_generated.Reset();
        g_motionConstants.Reset(); g_interpolateCs.Reset();
        g_haveCurrent = g_havePrevious = false;

        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(width);
        td.Height = static_cast<UINT>(height);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = format;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_previous)) ||
            FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_current))) return false;
        td.BindFlags = D3D11_BIND_UNORDERED_ACCESS;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &g_generated))) return false;
        if (FAILED(g_workerDevice->CreateShaderResourceView(g_current.Get(), nullptr, &g_currentSrv)) ||
            FAILED(g_workerDevice->CreateUnorderedAccessView(g_generated.Get(), nullptr, &g_generatedUav))) return false;

        ComPtr<ID3DBlob> code, errors;
        if (FAILED(D3DCompile(kInterpolationShader, std::strlen(kInterpolationShader), "RimFG.SlotState", nullptr, nullptr,
            "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &code, &errors)) || !code ||
            FAILED(g_workerDevice->CreateComputeShader(code->GetBufferPointer(), code->GetBufferSize(), nullptr, &g_interpolateCs))) return false;
        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(MotionConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(g_workerDevice->CreateBuffer(&cb, nullptr, &g_motionConstants))) return false;

        g_workerWidth = width;
        g_workerHeight = height;
        g_workerFormat = format;
        return true;
    }

    OpenedCapture* OpenSlot(int index, HANDLE handle)
    {
        if (index < 0 || index >= kCaptureSlots || !handle || !g_workerDevice) return nullptr;
        OpenedCapture& opened = g_openedCaptures[index];
        if (opened.handle == handle && opened.texture && opened.keyed) return &opened;
        opened.keyed.Reset(); opened.texture.Reset(); opened.handle = nullptr;
        if (FAILED(g_workerDevice->OpenSharedResource(handle, __uuidof(ID3D11Texture2D), reinterpret_cast<void**>(opened.texture.GetAddressOf()))) || !opened.texture) return nullptr;
        if (FAILED(opened.texture.As(&opened.keyed)) || !opened.keyed) { opened.texture.Reset(); return nullptr; }
        opened.handle = handle;
        return &opened;
    }

    int FindOldestReadySlot()
    {
        std::uint64_t bestSeq = UINT64_MAX;
        int best = -1;
        for (int i = 0; i < kCaptureSlots; ++i)
        {
            if (g_captureSlots[i].state.load(std::memory_order_acquire) != 2) continue;
            const std::uint64_t seq = g_captureSlots[i].sequence;
            if (seq && seq < bestSeq) { bestSeq = seq; best = i; }
        }
        return best;
    }

    bool ConsumeCaptureSlot(int index)
    {
        if (index < 0 || index >= kCaptureSlots) return false;
        CaptureSlot& slot = g_captureSlots[index];
        if (slot.state.load(std::memory_order_acquire) != 2) return false;
        if (!EnsureWorkerResources(slot.width, slot.height, slot.format)) return false;
        OpenedCapture* opened = OpenSlot(index, slot.handle);
        if (!opened || !opened->keyed) return false;

        const HRESULT acq = opened->keyed->AcquireSync(1, 20);
        if (acq != S_OK) return false; // Leave READY; retry later. Never lose ownership.

        if (g_haveCurrent)
        {
            g_workerContext->CopyResource(g_previous.Get(), g_current.Get());
            g_previousMetadata = g_currentMetadata;
            g_previousQpc = g_currentQpc;
            g_havePrevious = true;
        }
        g_workerContext->CopyResource(g_current.Get(), opened->texture.Get());
        g_currentMetadata = slot.metadata;
        g_currentQpc = slot.qpc;
        g_workerGeneration = slot.generation;
        g_haveCurrent = true;

        opened->keyed->ReleaseSync(0);
        slot.state.store(0, std::memory_order_release);
        return true;
    }

    bool EnsureOutputTexture(OutputTexture& o)
    {
        if (o.texture && o.keyed && o.handle) return true;
        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(g_workerWidth);
        td.Height = static_cast<UINT>(g_workerHeight);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = g_workerFormat;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.MiscFlags = D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        if (FAILED(g_workerDevice->CreateTexture2D(&td, nullptr, &o.texture)) || !o.texture ||
            FAILED(o.texture.As(&o.keyed)) || !o.keyed) return false;
        ComPtr<IDXGIResource> r;
        if (FAILED(o.texture.As(&r)) || !r || FAILED(r->GetSharedHandle(&o.handle)) || !o.handle) return false;
        return true;
    }

    bool AcquireOutputSet(int setIndex, int count)
    {
        int acquired = 0;
        for (; acquired < count; ++acquired)
        {
            auto& o = g_outputSets[setIndex][acquired];
            if (!EnsureOutputTexture(o) || o.keyed->AcquireSync(0, 0) != S_OK) break;
        }
        if (acquired == count) return true;
        for (int i = 0; i < acquired; ++i) g_outputSets[setIndex][i].keyed->ReleaseSync(0);
        return false;
    }

    void CopyProtectedRects(ID3D11Texture2D* from, ID3D11Texture2D* to, const MetadataSlot& metadata)
    {
        const int count = std::max(0, std::min(metadata.hudCount, kMaxHudRects));
        for (int i = 0; i < count; ++i)
        {
            const auto& r = metadata.hud[static_cast<std::size_t>(i)];
            const LONG l = std::max<LONG>(0, static_cast<LONG>(std::floor(r.x)));
            const LONG t = std::max<LONG>(0, static_cast<LONG>(std::floor(r.y)));
            const LONG rr = std::min<LONG>(g_workerWidth, static_cast<LONG>(std::ceil(r.x + r.width)));
            const LONG b = std::min<LONG>(g_workerHeight, static_cast<LONG>(std::ceil(r.y + r.height)));
            if (rr <= l || b <= t) continue;
            D3D11_BOX box{static_cast<UINT>(l), static_cast<UINT>(t), 0, static_cast<UINT>(rr), static_cast<UINT>(b), 1};
            g_workerContext->CopySubresourceRegion(to, 0, static_cast<UINT>(l), static_cast<UINT>(t), 0, from, 0, &box);
        }
    }

    bool DispatchInterpolation(float backward)
    {
        MotionConstants c{};
        c.width = g_workerWidth;
        c.height = g_workerHeight;
        c.zoomScale = 1.0f;
        c.backwardFraction = std::max(0.0f, std::min(1.0f, backward));
        if (g_previousMetadata.frame.orthographicSize > 0.001f && g_currentMetadata.frame.orthographicSize > 0.001f)
        {
            const float ortho = (g_previousMetadata.frame.orthographicSize + g_currentMetadata.frame.orthographicSize) * 0.5f;
            const float ppw = static_cast<float>(g_workerHeight) / (2.0f * ortho);
            c.imageShiftX = (g_currentMetadata.frame.cameraX - g_previousMetadata.frame.cameraX) * ppw;
            c.imageShiftY = -(g_currentMetadata.frame.cameraZ - g_previousMetadata.frame.cameraZ) * ppw;
            c.zoomScale = g_currentMetadata.frame.orthographicSize / g_previousMetadata.frame.orthographicSize;
        }
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_workerContext->Map(g_motionConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return false;
        std::memcpy(mapped.pData, &c, sizeof(c));
        g_workerContext->Unmap(g_motionConstants.Get(), 0);
        ID3D11ShaderResourceView* srv = g_currentSrv.Get();
        ID3D11UnorderedAccessView* uav = g_generatedUav.Get();
        ID3D11Buffer* cb = g_motionConstants.Get();
        g_workerContext->CSSetShader(g_interpolateCs.Get(), nullptr, 0);
        g_workerContext->CSSetShaderResources(0, 1, &srv);
        g_workerContext->CSSetUnorderedAccessViews(0, 1, &uav, nullptr);
        g_workerContext->CSSetConstantBuffers(0, 1, &cb);
        g_workerContext->Dispatch(static_cast<UINT>((g_workerWidth + 7) / 8), static_cast<UINT>((g_workerHeight + 7) / 8), 1);
        ID3D11ShaderResourceView* ns = nullptr; ID3D11UnorderedAccessView* nu = nullptr; ID3D11Buffer* nb = nullptr;
        g_workerContext->CSSetShaderResources(0, 1, &ns);
        g_workerContext->CSSetUnorderedAccessViews(0, 1, &nu, nullptr);
        g_workerContext->CSSetConstantBuffers(0, 1, &nb);
        g_workerContext->CSSetShader(nullptr, nullptr, 0);
        return true;
    }

    bool BuildAndPublishBatch(HWND sourceWindow)
    {
        if (!g_havePrevious || !g_haveCurrent || !sourceWindow) return false;
        double interval = 1.0 / std::max(1.0, RimFGPresent::EstimatedBaseFps());
        if (g_qpcFrequency.QuadPart > 0 && g_currentQpc > g_previousQpc)
        {
            const double measured = static_cast<double>(g_currentQpc - g_previousQpc) / static_cast<double>(g_qpcFrequency.QuadPart);
            if (measured >= 0.001 && measured <= 0.5) interval = measured;
        }
        const int effective = std::min(std::max(1, RimFGPresent::GetTargetOutputFps()), std::max(24, RimFGPresent::MonitorRefreshHz()));
        const int frameCount = std::max(1, std::min(kMaxFramesPerBatch, static_cast<int>(std::lround(interval * effective))));
        const int setIndex = g_nextOutputSet++ % kOutputSets;
        if (!AcquireOutputSet(setIndex, frameCount)) return false;

        const auto begin = std::chrono::steady_clock::now();
        for (int i = 0; i < frameCount; ++i)
        {
            auto& out = g_outputSets[setIndex][i];
            const float t = static_cast<float>(i + 1) / static_cast<float>(frameCount);
            if (i == frameCount - 1)
                g_workerContext->CopyResource(out.texture.Get(), g_current.Get());
            else if (DispatchInterpolation(1.0f - t))
            {
                CopyProtectedRects(t < 0.5f ? g_previous.Get() : g_current.Get(), g_generated.Get(),
                    t < 0.5f ? g_previousMetadata : g_currentMetadata);
                g_workerContext->CopyResource(out.texture.Get(), g_generated.Get());
            }
            else
                g_workerContext->CopyResource(out.texture.Get(), g_current.Get());
        }
        g_workerContext->Flush();
        for (int i = 0; i < frameCount; ++i) g_outputSets[setIndex][i].keyed->ReleaseSync(1);

        RimFGPresent::SharedFrameBatch batch{};
        batch.batchId = ++g_nextBatchId;
        batch.frameIndex = g_currentMetadata.frame.frameIndex;
        batch.width = g_workerWidth;
        batch.height = g_workerHeight;
        batch.format = g_workerFormat;
        batch.sourceWindow = sourceWindow;
        batch.frameCount = frameCount;
        batch.realFrameIndex = frameCount - 1;
        for (int i = 0; i < frameCount; ++i) batch.handles[i] = g_outputSets[setIndex][i].handle;
        RimFGPresent::PublishSharedFrameBatch(batch);

        const double ms = std::chrono::duration<double, std::milli>(std::chrono::steady_clock::now() - begin).count();
        const double old = g_workerMs.load(std::memory_order_relaxed);
        g_workerMs.store(old <= 0.0 ? ms : old + (ms - old) * 0.15, std::memory_order_relaxed);
        g_hasGeneratedFrame.store(frameCount > 1, std::memory_order_release);
        g_nativeStage.store(static_cast<int>(NativeStage::Generated), std::memory_order_release);
        return true;
    }

    void WorkerMain()
    {
        while (!g_workerStop.load(std::memory_order_acquire))
        {
            int index = FindOldestReadySlot();
            if (index < 0)
            {
                std::unique_lock<std::mutex> lock(g_workerCvMutex);
                g_workerCv.wait_for(lock, std::chrono::milliseconds(4));
                continue;
            }

            CaptureSlot& slot = g_captureSlots[index];
            const HWND window = slot.sourceWindow;
            const std::uint64_t generation = slot.generation;
            if (generation != g_workerGeneration)
            {
                g_haveCurrent = g_havePrevious = false;
                g_workerGeneration = generation;
            }

            if (!ConsumeCaptureSlot(index))
            {
                std::this_thread::sleep_for(std::chrono::milliseconds(1));
                continue;
            }

            if (g_havePrevious && g_haveCurrent)
                BuildAndPublishBatch(window);
            else
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
        if (type == kUnityGfxDeviceEventInitialize || type == kUnityGfxDeviceEventAfterReset) RefreshUnityDevice();
        else if (type == kUnityGfxDeviceEventBeforeReset || type == kUnityGfxDeviceEventShutdown)
        {
            ClearCaptureResources();
            g_unityContext.Reset(); g_adapter.Reset(); g_unityDevice = nullptr;
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
    g_unityContext.Reset(); g_adapter.Reset(); g_unityDevice = nullptr;
    g_unityGraphics = nullptr; g_unityInterfaces = nullptr;
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
extern "C" UNITY_INTERFACE_EXPORT int RimFG_GetGpuQualityTier() { return 1; }
extern "C" UNITY_INTERFACE_EXPORT double RimFG_GetGpuFrameGenerationMs() { return g_workerMs.load(std::memory_order_acquire); }

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
