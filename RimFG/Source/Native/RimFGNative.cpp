#include <atomic>
#include <array>
#include <cstdint>
#include <cstring>
#include <cmath>

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

    struct MotionConstants
    {
        float imageShiftX;
        float imageShiftY;
        std::int32_t width;
        std::int32_t height;
    };
#pragma pack(pop)

    static_assert(sizeof(FrameMetadata) == 48, "Managed/native ABI mismatch for FrameMetadata");
    static_assert(sizeof(HudRect) == 16, "Managed/native ABI mismatch for HudRect");
    static_assert(sizeof(MotionConstants) == 16, "D3D11 constant buffer alignment mismatch");
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
    ComPtr<ID3D11Buffer> g_motionConstants;
    bool g_haveHistory = false;
    bool g_havePreviousMetadata = false;
    FrameMetadata g_previousMetadata{};

    constexpr const char* kInterpolateShader = R"HLSL(
Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame  : register(t1);
RWTexture2D<float4> OutputFrame : register(u0);

cbuffer Motion : register(b0)
{
    float2 ImageShiftPixels;
    int2 FrameSize;
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

    // ImageShiftPixels is where static world content moved from Previous -> Current.
    // Sample each real frame half-way toward the temporal midpoint.
    float2 p = float2(id.xy);
    int2 prevCoord = ClampCoord(p - ImageShiftPixels * 0.5);
    int2 currCoord = ClampCoord(p + ImageShiftPixels * 0.5);

    float4 a = PreviousFrame.Load(int3(prevCoord, 0));
    float4 b = CurrentFrame.Load(int3(currCoord, 0));
    OutputFrame[id.xy] = lerp(a, b, 0.5);
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
        g_motionConstants.Reset();
        g_haveHistory = false;
        g_havePreviousMetadata = false;
        g_hasGeneratedFrame.store(false, std::memory_order_release);
    }

    bool EnsureComputeShader()
    {
        if (g_interpolateCs && g_motionConstants) return true;
        if (!g_device) return false;

        ComPtr<ID3DBlob> bytecode;
        ComPtr<ID3DBlob> errors;
        const HRESULT compileHr = D3DCompile(kInterpolateShader, std::strlen(kInterpolateShader), "RimFG.CameraAwareInterpolateCS", nullptr, nullptr, "CSMain", "cs_5_0", D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, &bytecode, &errors);
        if (FAILED(compileHr) || !bytecode) return false;
        if (FAILED(g_device->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &g_interpolateCs))) return false;

        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(MotionConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        return SUCCEEDED(g_device->CreateBuffer(&cb, nullptr, &g_motionConstants));
    }

    bool CreateFrameResources(ID3D11Texture2D* source)
    {
        if (!source || !g_device) return false;
        D3D11_TEXTURE2D_DESC desc{};
        source->GetDesc(&desc);
        if (!desc.Width || !desc.Height || desc.SampleDesc.Count != 1) return false;

        ReleaseFrameResources();
        desc.MipLevels = 1; desc.ArraySize = 1; desc.Usage = D3D11_USAGE_DEFAULT; desc.CPUAccessFlags = 0; desc.MiscFlags = 0; desc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_previousFrame))) return false;
        if (FAILED(g_device->CreateTexture2D(&desc, nullptr, &g_currentFrame))) return false;
        D3D11_TEXTURE2D_DESC generated = desc;
        generated.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_device->CreateTexture2D(&generated, nullptr, &g_generatedFrame))) return false;
        if (FAILED(g_device->CreateShaderResourceView(g_previousFrame.Get(), nullptr, &g_previousSrv))) return false;
        if (FAILED(g_device->CreateShaderResourceView(g_currentFrame.Get(), nullptr, &g_currentSrv))) return false;
        if (FAILED(g_device->CreateUnorderedAccessView(g_generatedFrame.Get(), nullptr, &g_generatedUav))) return false;
        return EnsureComputeShader();
    }

    void AdoptPendingSceneTexture()
    {
        if (!g_sceneTextureUpdatePending.exchange(false, std::memory_order_acq_rel)) return;
        ID3D11Texture2D* pending = g_pendingSceneTexture.exchange(nullptr, std::memory_order_acq_rel);
        g_sceneTexture.Reset();
        if (pending) g_sceneTexture.Attach(pending);
        g_sceneWidth = g_pendingWidth.load(std::memory_order_acquire);
        g_sceneHeight = g_pendingHeight.load(std::memory_order_acquire);
        ReleaseFrameResources();
        if (g_sceneTexture) CreateFrameResources(g_sceneTexture.Get());
    }

    void RefreshD3D11Device()
    {
        g_context.Reset(); g_device = nullptr; g_d3d11Ready.store(false, std::memory_order_release);
        if (!g_unityInterfaces) return;
        auto* d3d11 = g_unityInterfaces->Get<IUnityGraphicsD3D11>();
        if (!d3d11) return;
        g_device = d3d11->GetDevice();
        if (g_device) g_device->GetImmediateContext(&g_context);
        g_d3d11Ready.store(g_device && g_context, std::memory_order_release);
    }

    void UNITY_INTERFACE_API OnGraphicsDeviceEvent(UnityGfxDeviceEventType eventType)
    {
        switch (eventType)
        {
        case kUnityGfxDeviceEventInitialize:
        case kUnityGfxDeviceEventAfterReset: RefreshD3D11Device(); break;
        case kUnityGfxDeviceEventBeforeReset:
        case kUnityGfxDeviceEventShutdown:
            ReleaseFrameResources(); g_sceneTexture.Reset(); g_interpolateCs.Reset(); g_context.Reset(); g_d3d11Ready.store(false, std::memory_order_release); g_device = nullptr; break;
        default: break;
        }
    }

    MetadataSlot ReadLatestSlot()
    {
        const std::uint32_t seq = g_writeSequence.load(std::memory_order_acquire);
        return g_slots[seq & 1u];
    }

    MotionConstants BuildMotionConstants(const FrameMetadata& previous, const FrameMetadata& current)
    {
        MotionConstants result{};
        result.width = g_sceneWidth;
        result.height = g_sceneHeight;

        if (current.orthographicSize <= 0.001f || previous.orthographicSize <= 0.001f || g_sceneHeight <= 0)
            return result;

        const float ortho = (current.orthographicSize + previous.orthographicSize) * 0.5f;
        const float pixelsPerWorldUnit = static_cast<float>(g_sceneHeight) / (2.0f * ortho);
        const float cameraDx = current.cameraX - previous.cameraX;
        const float cameraDz = current.cameraZ - previous.cameraZ;

        // Moving the camera right makes static world pixels move left. RimWorld's
        // map plane uses X/Z, so Z drives screen vertical displacement.
        result.imageShiftX = -cameraDx * pixelsPerWorldUnit;
        result.imageShiftY = cameraDz * pixelsPerWorldUnit;

        // Teleports/map changes should not smear an entire frame. Large movement is
        // treated as a cut and falls back to an unwarped blend for this midpoint.
        const float maxShift = static_cast<float>(std::max(g_sceneWidth, g_sceneHeight)) * 0.25f;
        if (std::fabs(result.imageShiftX) > maxShift || std::fabs(result.imageShiftY) > maxShift)
        {
            result.imageShiftX = 0.0f;
            result.imageShiftY = 0.0f;
        }
        return result;
    }

    bool UploadMotionConstants(const MotionConstants& constants)
    {
        if (!g_context || !g_motionConstants) return false;
        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(g_context->Map(g_motionConstants.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped))) return false;
        std::memcpy(mapped.pData, &constants, sizeof(constants));
        g_context->Unmap(g_motionConstants.Get(), 0);
        return true;
    }

    bool GenerateMidpointFrame(const FrameMetadata& metadata)
    {
        if (!g_sceneTexture || !g_context || !g_previousFrame || !g_currentFrame || !g_generatedFrame || !g_previousSrv || !g_currentSrv || !g_generatedUav || !g_interpolateCs || !g_motionConstants)
            return false;

        g_context->CopyResource(g_currentFrame.Get(), g_sceneTexture.Get());
        if (!g_haveHistory || !g_havePreviousMetadata)
        {
            g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
            g_haveHistory = true;
            g_previousMetadata = metadata;
            g_havePreviousMetadata = true;
            return false;
        }

        const MotionConstants motion = BuildMotionConstants(g_previousMetadata, metadata);
        if (!UploadMotionConstants(motion)) return false;

        ID3D11ShaderResourceView* srvs[2] = { g_previousSrv.Get(), g_currentSrv.Get() };
        ID3D11UnorderedAccessView* uavs[1] = { g_generatedUav.Get() };
        ID3D11Buffer* cb[1] = { g_motionConstants.Get() };
        g_context->CSSetShader(g_interpolateCs.Get(), nullptr, 0);
        g_context->CSSetShaderResources(0, 2, srvs);
        g_context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, cb);
        g_context->Dispatch(static_cast<UINT>((g_sceneWidth + 7) / 8), static_cast<UINT>((g_sceneHeight + 7) / 8), 1);

        ID3D11ShaderResourceView* nullSrvs[2] = { nullptr, nullptr };
        ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
        ID3D11Buffer* nullCb[1] = { nullptr };
        g_context->CSSetShaderResources(0, 2, nullSrvs);
        g_context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        g_context->CSSetConstantBuffers(0, 1, nullCb);
        g_context->CSSetShader(nullptr, nullptr, 0);

        g_context->CopyResource(g_previousFrame.Get(), g_currentFrame.Get());
        g_previousMetadata = metadata;
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
        RimFGPresent::SetGeneratedFrameSource(g_generatedFrame.Get(), g_sceneWidth, g_sceneHeight, rects.data(), count, slot.frame.frameIndex);
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        if (eventId != 1 || !g_enabled.load(std::memory_order_relaxed)) return;
        if (!g_d3d11Ready.load(std::memory_order_acquire) || !g_device || !g_context) return;
        AdoptPendingSceneTexture();
        const MetadataSlot slot = ReadLatestSlot();
        if (slot.frame.abiVersion != kAbiVersion) return;
        if (GenerateMidpointFrame(slot.frame)) PublishGeneratedFrame(slot);
    }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* unityInterfaces)
{
    g_unityInterfaces = unityInterfaces;
    g_unityGraphics = unityInterfaces ? unityInterfaces->Get<IUnityGraphics>() : nullptr;
    if (g_unityGraphics) { g_unityGraphics->RegisterDeviceEventCallback(OnGraphicsDeviceEvent); OnGraphicsDeviceEvent(kUnityGfxDeviceEventInitialize); }
}

extern "C" void UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API UnityPluginUnload()
{
    if (g_unityGraphics) g_unityGraphics->UnregisterDeviceEventCallback(OnGraphicsDeviceEvent);
    g_enabled.store(false, std::memory_order_release);
    RimFGPresent::Shutdown();
    ReleaseFrameResources(); g_sceneTexture.Reset(); g_interpolateCs.Reset(); g_context.Reset();
    ID3D11Texture2D* pending = g_pendingSceneTexture.exchange(nullptr, std::memory_order_acq_rel);
    if (pending) pending->Release();
    g_d3d11Ready.store(false, std::memory_order_release); g_device = nullptr; g_unityGraphics = nullptr; g_unityInterfaces = nullptr;
}

extern "C" UnityRenderingEvent UNITY_INTERFACE_EXPORT UNITY_INTERFACE_API RimFG_GetRenderEventFunc() { return OnRenderEvent; }
extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetEnabled(int enabled)
{
    g_enabled.store(enabled != 0, std::memory_order_release);
    if (!enabled) RimFGPresent::ClearGeneratedFrameSource();
}
extern "C" UNITY_INTERFACE_EXPORT int RimFG_IsD3D11Ready() { return g_d3d11Ready.load(std::memory_order_acquire) ? 1 : 0; }
extern "C" UNITY_INTERFACE_EXPORT int RimFG_HasGeneratedFrame() { return g_hasGeneratedFrame.load(std::memory_order_acquire) ? 1 : 0; }

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

extern "C" UNITY_INTERFACE_EXPORT void RimFG_SetSceneTexture(void* nativeTexture, int width, int height)
{
    ID3D11Texture2D* next = static_cast<ID3D11Texture2D*>(nativeTexture);
    if (next) next->AddRef();
    ID3D11Texture2D* oldPending = g_pendingSceneTexture.exchange(next, std::memory_order_acq_rel);
    if (oldPending) oldPending->Release();
    g_pendingWidth.store(width, std::memory_order_release);
    g_pendingHeight.store(height, std::memory_order_release);
    g_sceneTextureUpdatePending.store(true, std::memory_order_release);
}
