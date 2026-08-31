#include "OpticalFlowBackend.h"

#include <algorithm>
#include <cstring>
#include <d3dcompiler.h>

using Microsoft::WRL::ComPtr;

namespace RimFGFlow
{
    namespace
    {
#pragma pack(push, 4)
        struct FlowConstants
        {
            float imageShiftX;
            float imageShiftY;
            float zoomScale;
            float pad0;
            int width;
            int height;
            int flowWidth;
            int flowHeight;
        };
#pragma pack(pop)

        static_assert(sizeof(FlowConstants) == 32, "Flow constant buffer size must be 16-byte aligned");

        constexpr const char* kFlowShader = R"HLSL(
Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame  : register(t1);
RWTexture2D<float2> ResidualFlow : register(u0);

cbuffer FlowConstants : register(b0)
{
    float2 GlobalShiftPixels;
    float ZoomScale;
    float _Pad0;
    int2 FrameSize;
    int2 FlowSize;
};

float Luma(float3 c)
{
    return dot(c, float3(0.299, 0.587, 0.114));
}

int2 ClampFrame(int2 p)
{
    return clamp(p, int2(0, 0), FrameSize - int2(1, 1));
}

float PatchError(int2 currentCenter, float2 previousCenter)
{
    float e = 0.0;
    [unroll]
    for (int y = -1; y <= 1; ++y)
    {
        [unroll]
        for (int x = -1; x <= 1; ++x)
        {
            int2 c = ClampFrame(currentCenter + int2(x, y));
            int2 p = ClampFrame(int2(round(previousCenter)) + int2(x, y));
            float a = Luma(CurrentFrame.Load(int3(c, 0)).rgb);
            float b = Luma(PreviousFrame.Load(int3(p, 0)).rgb);
            e += abs(a - b);
        }
    }
    return e;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)FlowSize.x || tid.y >= (uint)FlowSize.y)
        return;

    // Half-resolution flow grid. Each cell represents a 2x2 full-resolution region.
    int2 currentCenter = min(int2(tid.xy) * 2 + int2(1, 1), FrameSize - int2(1, 1));
    float2 center = (float2(FrameSize) - 1.0) * 0.5;

    // Remove deterministic orthographic camera translation/zoom first. The search
    // therefore only needs to find local residual motion (pawns, projectiles, motes).
    float2 currentFromCenter = float2(currentCenter) - center;
    float safeZoom = max(ZoomScale, 0.001);
    float2 predictedPrevious = center + currentFromCenter / safeZoom - GlobalShiftPixels;

    float bestError = 1e20;
    int2 best = int2(0, 0);

    // Small search radius keeps this predictable and GPU-friendly.
    [loop]
    for (int dy = -3; dy <= 3; ++dy)
    {
        [loop]
        for (int dx = -3; dx <= 3; ++dx)
        {
            float e = PatchError(currentCenter, predictedPrevious + float2(dx, dy));
            if (e < bestError)
            {
                bestError = e;
                best = int2(dx, dy);
            }
        }
    }

    // Low-texture/ambiguous regions are safer with no residual motion.
    float2 residual = bestError < 0.32 ? float2(best) : float2(0.0, 0.0);
    ResidualFlow[tid.xy] = residual;
}
)HLSL";
    }

    bool Backend::Initialize(ID3D11Device* device, int width, int height)
    {
        device_ = device;
        width_ = width;
        height_ = height;
        return EnsureShader() && CreateResources();
    }

    void Backend::Shutdown()
    {
        flowTexture_.Reset();
        flowSrv_.Reset();
        flowUav_.Reset();
        shader_.Reset();
        constants_.Reset();
        device_ = nullptr;
        width_ = height_ = flowWidth_ = flowHeight_ = 0;
    }

    bool Backend::Resize(int width, int height)
    {
        if (width == width_ && height == height_ && flowTexture_)
            return true;
        width_ = width;
        height_ = height;
        flowTexture_.Reset();
        flowSrv_.Reset();
        flowUav_.Reset();
        return CreateResources();
    }

    bool Backend::EnsureShader()
    {
        if (shader_ && constants_)
            return true;
        if (!device_)
            return false;

        ComPtr<ID3DBlob> bytecode;
        ComPtr<ID3DBlob> errors;
        HRESULT hr = D3DCompile(
            kFlowShader,
            std::strlen(kFlowShader),
            "RimFG.ResidualFlowCS",
            nullptr,
            nullptr,
            "CSMain",
            "cs_5_0",
            D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            &bytecode,
            &errors);
        if (FAILED(hr) || !bytecode)
            return false;

        if (FAILED(device_->CreateComputeShader(bytecode->GetBufferPointer(), bytecode->GetBufferSize(), nullptr, &shader_)))
            return false;

        D3D11_BUFFER_DESC cb{};
        cb.ByteWidth = sizeof(FlowConstants);
        cb.Usage = D3D11_USAGE_DYNAMIC;
        cb.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        cb.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        return SUCCEEDED(device_->CreateBuffer(&cb, nullptr, &constants_));
    }

    bool Backend::CreateResources()
    {
        if (!device_ || width_ <= 0 || height_ <= 0)
            return false;

        flowWidth_ = std::max(1, (width_ + 1) / 2);
        flowHeight_ = std::max(1, (height_ + 1) / 2);

        D3D11_TEXTURE2D_DESC td{};
        td.Width = static_cast<UINT>(flowWidth_);
        td.Height = static_cast<UINT>(flowHeight_);
        td.MipLevels = 1;
        td.ArraySize = 1;
        td.Format = DXGI_FORMAT_R16G16_FLOAT;
        td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT;
        td.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;

        if (FAILED(device_->CreateTexture2D(&td, nullptr, &flowTexture_)))
            return false;
        if (FAILED(device_->CreateShaderResourceView(flowTexture_.Get(), nullptr, &flowSrv_)))
            return false;
        if (FAILED(device_->CreateUnorderedAccessView(flowTexture_.Get(), nullptr, &flowUav_)))
            return false;
        return true;
    }

    bool Backend::UploadConstants(ID3D11DeviceContext* context, const MotionInput& motion)
    {
        if (!context || !constants_)
            return false;

        FlowConstants c{};
        c.imageShiftX = motion.imageShiftX;
        c.imageShiftY = motion.imageShiftY;
        c.zoomScale = motion.zoomScale;
        c.width = width_;
        c.height = height_;
        c.flowWidth = flowWidth_;
        c.flowHeight = flowHeight_;

        D3D11_MAPPED_SUBRESOURCE mapped{};
        if (FAILED(context->Map(constants_.Get(), 0, D3D11_MAP_WRITE_DISCARD, 0, &mapped)))
            return false;
        std::memcpy(mapped.pData, &c, sizeof(c));
        context->Unmap(constants_.Get(), 0);
        return true;
    }

    bool Backend::Dispatch(
        ID3D11DeviceContext* context,
        ID3D11ShaderResourceView* previous,
        ID3D11ShaderResourceView* current,
        const MotionInput& motion)
    {
        if (!context || !previous || !current || !shader_ || !flowUav_ || !UploadConstants(context, motion))
            return false;

        ID3D11ShaderResourceView* srvs[2] = { previous, current };
        ID3D11UnorderedAccessView* uavs[1] = { flowUav_.Get() };
        ID3D11Buffer* cbs[1] = { constants_.Get() };

        context->CSSetShader(shader_.Get(), nullptr, 0);
        context->CSSetShaderResources(0, 2, srvs);
        context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        context->CSSetConstantBuffers(0, 1, cbs);
        context->Dispatch(static_cast<UINT>((flowWidth_ + 7) / 8), static_cast<UINT>((flowHeight_ + 7) / 8), 1);

        ID3D11ShaderResourceView* nullSrvs[2] = { nullptr, nullptr };
        ID3D11UnorderedAccessView* nullUavs[1] = { nullptr };
        ID3D11Buffer* nullCbs[1] = { nullptr };
        context->CSSetShaderResources(0, 2, nullSrvs);
        context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        context->CSSetConstantBuffers(0, 1, nullCbs);
        context->CSSetShader(nullptr, nullptr, 0);
        return true;
    }
}
