// Camera-aware midpoint interpolation prototype.
// This is deliberately simple: RimWorld's orthographic camera means a large
// portion of screen motion can be represented by a global 2D translation.
// Optical flow can later refine local motion for pawns/projectiles/motes.

cbuffer InterpolateConstants : register(b0)
{
    uint2 OutputSize;
    float2 CameraDeltaPixels;
    float BlendT;
    float _Padding0;
    float2 _Padding1;
};

Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame : register(t1);
Texture2D<float> HudMask : register(t2);
SamplerState LinearClamp : register(s0);
RWTexture2D<float4> GeneratedFrame : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 p = dispatchThreadId.xy;
    if (p.x >= OutputSize.x || p.y >= OutputSize.y)
        return;

    float2 invSize = 1.0 / float2(OutputSize);
    float2 uv = (float2(p) + 0.5) * invSize;

    // Warp each real frame toward the temporal midpoint using the known
    // orthographic camera translation. Local object motion is not handled yet.
    float2 halfDeltaUv = 0.5 * CameraDeltaPixels * invSize;
    float4 a = PreviousFrame.SampleLevel(LinearClamp, uv + halfDeltaUv, 0.0);
    float4 b = CurrentFrame.SampleLevel(LinearClamp, uv - halfDeltaUv, 0.0);
    float4 interpolated = lerp(a, b, saturate(BlendT));

    // HUD is never interpolated. The latest real UI is copied verbatim.
    float mask = HudMask.Load(int3(p, 0));
    float4 realUi = CurrentFrame.Load(int3(p, 0));
    GeneratedFrame[p] = lerp(interpolated, realUi, saturate(mask));
}
