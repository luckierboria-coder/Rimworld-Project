// RimFG camera/zoom-aware midpoint interpolation.
// Global camera translation + orthographic zoom are deterministic from RimWorld
// metadata. Optional residual flow handles local object motion after that global
// transform is removed.

cbuffer InterpolateConstants : register(b0)
{
    uint2 OutputSize;
    float2 CameraDeltaPixels;
    float PreviousOrthoSize;
    float CurrentOrthoSize;
    float BlendT;
    float ResidualFlowEnabled;
};

Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame : register(t1);
Texture2D<float> HudMask : register(t2);
Texture2D<float2> ResidualMotion : register(t3); // full-res pixel motion, prev -> curr
SamplerState LinearClamp : register(s0);
RWTexture2D<float4> GeneratedFrame : register(u0);

float2 ZoomWarp(float2 uv, float fromOrtho, float toOrtho)
{
    if (fromOrtho <= 0.0001 || toOrtho <= 0.0001)
        return uv;

    // Orthographic size grows when zooming out. Map about screen center.
    float scale = fromOrtho / toOrtho;
    return (uv - 0.5) * scale + 0.5;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 p = dispatchThreadId.xy;
    if (p.x >= OutputSize.x || p.y >= OutputSize.y)
        return;

    float2 invSize = 1.0 / float2(OutputSize);
    float2 uv = (float2(p) + 0.5) * invSize;
    float t = saturate(BlendT);

    // Temporal midpoint orthographic size. This lets zoom animation use a known
    // deterministic transform instead of asking optical flow to guess scale.
    float midpointOrtho = lerp(PreviousOrthoSize, CurrentOrthoSize, t);

    float2 prevUv = ZoomWarp(uv, midpointOrtho, PreviousOrthoSize);
    float2 currUv = ZoomWarp(uv, midpointOrtho, CurrentOrthoSize);

    // CameraDeltaPixels is Previous -> Current static-world image motion.
    float2 halfDeltaUv = CameraDeltaPixels * invSize * 0.5;
    prevUv -= halfDeltaUv;
    currUv += halfDeltaUv;

    if (ResidualFlowEnabled > 0.5)
    {
        // Residual flow is estimated AFTER camera compensation, so it should be
        // small. The half-res OF texture is sampled linearly and stores full-res px.
        float2 residualPx = ResidualMotion.SampleLevel(LinearClamp, uv, 0.0);
        float2 residualUv = residualPx * invSize * 0.5;
        prevUv -= residualUv;
        currUv += residualUv;
    }

    float4 a = PreviousFrame.SampleLevel(LinearClamp, prevUv, 0.0);
    float4 b = CurrentFrame.SampleLevel(LinearClamp, currUv, 0.0);
    float4 interpolated = lerp(a, b, t);

    // HUD/UI stays real. PresentHook currently performs the conservative real-HUD
    // composite; this path remains useful once pixel/dynamic masks replace bands.
    float mask = HudMask.Load(int3(p, 0));
    float4 realUi = CurrentFrame.Load(int3(p, 0));
    GeneratedFrame[p] = lerp(interpolated, realUi, saturate(mask));
}
