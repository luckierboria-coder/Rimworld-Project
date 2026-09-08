// Builds an R8-style logical HUD mask entirely on GPU.
// 0 = frame-generation allowed, 1 = preserve real-frame UI.

struct HudRect
{
    float x;
    float y;
    float width;
    float height;
};

cbuffer HudMaskConstants : register(b0)
{
    uint2 OutputSize;
    uint HudRectCount;
    uint _Padding0;
};

StructuredBuffer<HudRect> HudRects : register(t0);
RWTexture2D<float> HudMask : register(u0);

[numthreads(8, 8, 1)]
void CSMain(uint3 dispatchThreadId : SV_DispatchThreadID)
{
    uint2 p = dispatchThreadId.xy;
    if (p.x >= OutputSize.x || p.y >= OutputSize.y)
        return;

    float2 pixel = float2(p) + 0.5;
    float masked = 0.0;

    [loop]
    for (uint i = 0; i < HudRectCount; ++i)
    {
        HudRect r = HudRects[i];
        float2 rMin = float2(r.x, r.y);
        float2 rMax = rMin + float2(r.width, r.height);

        if (all(pixel >= rMin) && all(pixel < rMax))
        {
            masked = 1.0;
            break;
        }
    }

    HudMask[p] = masked;
}
