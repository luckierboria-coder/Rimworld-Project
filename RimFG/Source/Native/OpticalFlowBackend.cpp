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
    // Five samples are enough for RimWorld's high-contrast 2D sprites and are much
    // cheaper than the old 3x3 patch. False vectors are filtered by confidence below.
    int2 pc = ClampFrame(int2(round(previousCenter)));
    float e = 0.0;
    float c0 = Luma(CurrentFrame.Load(int3(ClampFrame(currentCenter), 0)).rgb);
    float p0 = Luma(PreviousFrame.Load(int3(pc, 0)).rgb);
    e += abs(c0 - p0) * 2.0;
    const int2 d0 = int2(2, 0);
    const int2 d1 = int2(0, 2);
    e += abs(Luma(CurrentFrame.Load(int3(ClampFrame(currentCenter + d0), 0)).rgb) - Luma(PreviousFrame.Load(int3(ClampFrame(pc + d0), 0)).rgb));
    e += abs(Luma(CurrentFrame.Load(int3(ClampFrame(currentCenter - d0), 0)).rgb) - Luma(PreviousFrame.Load(int3(ClampFrame(pc - d0), 0)).rgb));
    e += abs(Luma(CurrentFrame.Load(int3(ClampFrame(currentCenter + d1), 0)).rgb) - Luma(PreviousFrame.Load(int3(ClampFrame(pc + d1), 0)).rgb));
    e += abs(Luma(CurrentFrame.Load(int3(ClampFrame(currentCenter - d1), 0)).rgb) - Luma(PreviousFrame.Load(int3(ClampFrame(pc - d1), 0)).rgb));
    return e;
}

float LocalContrast(int2 center)
{
    float c = Luma(CurrentFrame.Load(int3(ClampFrame(center), 0)).rgb);
    float l = Luma(CurrentFrame.Load(int3(ClampFrame(center + int2(-2, 0)), 0)).rgb);
    float r = Luma(CurrentFrame.Load(int3(ClampFrame(center + int2(2, 0)), 0)).rgb);
    float u = Luma(CurrentFrame.Load(int3(ClampFrame(center + int2(0, -2)), 0)).rgb);
    float d = Luma(CurrentFrame.Load(int3(ClampFrame(center + int2(0, 2)), 0)).rgb);
    return abs(c - l) + abs(c - r) + abs(c - u) + abs(c - d);
}

void ConsiderCandidate(int2 currentCenter, float2 predictedPrevious, float2 offset,
    inout float bestError, inout float secondError, inout float2 bestOffset)
{
    float e = PatchError(currentCenter, predictedPrevious + offset);
    e += dot(offset, offset) * 0.00045;
    if (e < bestError)
    {
        secondError = bestError;
        bestError = e;
        bestOffset = offset;
    }
    else if (e < secondError)
    {
        secondError = e;
    }
}

[numthreads(8, 8, 1)]
void CSMain(uint3 tid : SV_DispatchThreadID)
{
    if (tid.x >= (uint)FlowSize.x || tid.y >= (uint)FlowSize.y)
        return;

    // Quarter-resolution vector field: one vector per 4x4 full-resolution pixels.
    // This cuts flow workload to one quarter of the previous half-resolution field.
    int2 currentCenter = min(int2(tid.xy) * 4 + int2(2, 2), FrameSize - int2(1, 1));
    float2 center = (float2(FrameSize) - 1.0) * 0.5;
    float2 currentFromCenter = float2(currentCenter) - center;
    float safeZoom = max(ZoomScale, 0.001);
    float2 predictedPrevious = center + currentFromCenter / safeZoom - GlobalShiftPixels;

    float bestError = 1e20;
    float secondError = 1e20;
    float2 bestOffset = float2(0.0, 0.0);

    // 9x9 coarse search at 4px spacing covers +/-16px local displacement.
    [loop]
    for (int cy = -4; cy <= 4; ++cy)
    {
        [loop]
        for (int cx = -4; cx <= 4; ++cx)
        {
            ConsiderCandidate(currentCenter, predictedPrevious, float2(cx * 4, cy * 4),
                bestError, secondError, bestOffset);
        }
    }

    // Small fine refinement around the coarse winner.
    float2 coarseBest = bestOffset;
    [unroll]
    for (int fy = -1; fy <= 1; ++fy)
    {
        [unroll]
        for (int fx = -1; fx <= 1; ++fx)
        {
            ConsiderCandidate(currentCenter, predictedPrevious, coarseBest + float2(fx * 2, fy * 2),
                bestError, secondError, bestOffset);
        }
    }

    float uniqueness = saturate((secondError - bestError) / max(secondError, 0.0001));
    float contrast = saturate(LocalContrast(currentCenter) * 4.0);
    float errorGate = 1.0 - smoothstep(0.18, 0.38, bestError);
    float confidence = uniqueness * contrast * errorGate;
    float weight = smoothstep(0.12, 0.46, confidence);
    float2 residual = bestOffset * weight;

    if (dot(residual, residual) < 0.50)
        residual = float2(0.0, 0.0);
    float mag = length(residual);
    if (mag > 16.0)
        residual *= 16.0 / mag;

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

        flowWidth_ = std::max(1, (width_ + 3) / 4);
        flowHeight_ = std::max(1, (height_ + 3) / 4);

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

        ID3D11ShaderResourceView* srvs[2] = {previous, current};
        ID3D11UnorderedAccessView* uavs[1] = {flowUav_.Get()};
        ID3D11Buffer* cbs[1] = {constants_.Get()};

        context->CSSetShader(shader_.Get(), nullptr, 0);
        context->CSSetShaderResources(0, 2, srvs);
        context->CSSetUnorderedAccessViews(0, 1, uavs, nullptr);
        context->CSSetConstantBuffers(0, 1, cbs);
        context->Dispatch(static_cast<UINT>((flowWidth_ + 7) / 8), static_cast<UINT>((flowHeight_ + 7) / 8), 1);

        ID3D11ShaderResourceView* nullSrvs[2] = {nullptr, nullptr};
        ID3D11UnorderedAccessView* nullUavs[1] = {nullptr};
        ID3D11Buffer* nullCbs[1] = {nullptr};
        context->CSSetShaderResources(0, 2, nullSrvs);
        context->CSSetUnorderedAccessViews(0, 1, nullUavs, nullptr);
        context->CSSetConstantBuffers(0, 1, nullCbs);
        context->CSSetShader(nullptr, nullptr, 0);
        return true;
    }
}
