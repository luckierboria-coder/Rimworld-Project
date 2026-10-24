// RimFG vendor-neutral residual optical flow prototype.
// Goal: estimate object/local motion AFTER camera reprojection so the search
// radius can remain small and GPU cost predictable.
//
// Pass 1 runs at half resolution. Each output texel represents a 2x2 source
// region and stores motion in full-resolution pixels (Previous -> Current).

Texture2D<float4> PreviousFrame : register(t0);
Texture2D<float4> CurrentFrame  : register(t1);
RWTexture2D<float2> MotionField : register(u0);

cbuffer OpticalFlowConstants : register(b0)
{
    int2 FullFrameSize;
    int SearchRadius;      // V0 target: 3..6 half-res texels.
    int PatchRadius;       // V0 target: 1..2 half-res texels.
    float2 CameraShiftPx;  // Previous -> Current static-world motion.
    float LumaThreshold;
    float Padding0;
};

float Luma(float3 rgb)
{
    return dot(rgb, float3(0.299, 0.587, 0.114));
}

int2 ClampFull(int2 p)
{
    return clamp(p, int2(0, 0), FullFrameSize - int2(1, 1));
}

float SampleLuma(Texture2D<float4> tex, int2 halfCoord)
{
    // Half-res grid uses the center of each 2x2 full-res block.
    int2 full = ClampFull(halfCoord * 2 + int2(1, 1));
    return Luma(tex.Load(int3(full, 0)).rgb);
}

float PatchError(int2 prevHalf, int2 currHalf)
{
    float error = 0.0;
    int samples = 0;

    [loop]
    for (int y = -PatchRadius; y <= PatchRadius; ++y)
    {
        [loop]
        for (int x = -PatchRadius; x <= PatchRadius; ++x)
        {
            float a = SampleLuma(PreviousFrame, prevHalf + int2(x, y));
            float b = SampleLuma(CurrentFrame, currHalf + int2(x, y));
            error += abs(a - b);
            samples++;
        }
    }

    return samples > 0 ? error / samples : 1.0;
}

[numthreads(8, 8, 1)]
void CSMain(uint3 id : SV_DispatchThreadID)
{
    uint motionWidth, motionHeight;
    MotionField.GetDimensions(motionWidth, motionHeight);
    if (id.x >= motionWidth || id.y >= motionHeight)
        return;

    int2 currHalf = int2(id.xy);

    // Camera shift is already known from RimWorld metadata. Convert it to the
    // half-res grid and search only for residual/object motion around it.
    float2 cameraHalf = CameraShiftPx * 0.5;
    int2 predictedPrev = currHalf - int2(round(cameraHalf));

    float centerLuma = SampleLuma(CurrentFrame, currHalf);
    float bestError = 1e9;
    int2 bestOffset = int2(0, 0);

    [loop]
    for (int oy = -SearchRadius; oy <= SearchRadius; ++oy)
    {
        [loop]
        for (int ox = -SearchRadius; ox <= SearchRadius; ++ox)
        {
            int2 candidatePrev = predictedPrev - int2(ox, oy);
            float e = PatchError(candidatePrev, currHalf);
            if (e < bestError)
            {
                bestError = e;
                bestOffset = int2(ox, oy);
            }
        }
    }

    // Flat/low-information areas should trust deterministic camera motion rather
    // than invent noisy residual vectors.
    float neighborLuma = SampleLuma(CurrentFrame, currHalf + int2(1, 0));
    bool lowInformation = abs(centerLuma - neighborLuma) < LumaThreshold;
    if (lowInformation)
        bestOffset = int2(0, 0);

    float2 residualFull = float2(bestOffset) * 2.0;
    MotionField[id.xy] = CameraShiftPx + residualFull;
}
