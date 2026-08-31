# RimFG

RimFG is an experimental RimWorld-aware frame-generation project for RimWorld 1.5 on Windows/D3D11.

## Design goal

Use spare GPU capacity to improve presentation smoothness without materially increasing RimWorld main-thread or simulation cost.

Hard constraints:

- No per-frame GPU -> CPU framebuffer readback.
- No image processing in C#/Harmony.
- No per-frame `Texture.GetNativeTexturePtr()` calls.
- Frame generation work stays in native D3D11/GPU code.
- Managed code only sends tiny metadata packets (camera state, screen size, pause/speed, HUD rectangles).
- Target CPU overhead: <1% and no measurable TPS regression.

## Current V0.1 pipeline

Implemented on branch `rimfg-v0.1`:

- RimWorld 1.5 managed bridge.
- Persistent HUD-less scene capture through a Camera command buffer.
- Scene capture remains GPU-resident; `GetNativeTexturePtr()` is used only on allocation/resize.
- Lock-free managed/native metadata handoff.
- D3D11 previous/current/generated frame textures.
- Runtime-compiled D3D11 compute shader producing a real midpoint GPU frame.
- DXGI `IDXGISwapChain::Present` interception using MinHook.
- Swapchain filtering to the current RimWorld process/window.
- GPU-only real-frame scratch/composite surfaces.
- Conservative HUD regions copied from the real backbuffer onto generated frames.
- Generated midpoint Present inserted immediately before the original Unity Present.
- Flip-model-safe backbuffer reacquisition between generated and real Presents.
- Fail-safe bypass on TEST presents, exclusive fullscreen, format/size/MSAA mismatch, or missing GPU resources.
- Native telemetry counters for generated/skipped Presents.
- Windows x64 native CI build.

The midpoint algorithm is currently a simple GPU blend. It exists to validate the complete zero-readback history/composite/Present path before replacing the interpolation kernel with camera-aware warping and optical flow.

## Pipeline

```text
RimWorld Camera
   |
   | GPU-only command-buffer blit
   v
HUD-less RenderTexture
   |
   v
RimFG.Native / D3D11
   |-- previous frame
   |-- current frame
   |-- generated midpoint frame
   |-- camera/HUD metadata
   v
DXGI Present hook
   |
   | preserve full real backbuffer -> GPU scratch
   | generated scene -> composite
   | real HUD rectangles -> composite
   |
   +--> Present generated midpoint
   |
   | reacquire active backbuffer
   | restore preserved real frame
   v
Original Unity Present
```

## Current limitation: frame pacing

The V0.1 double-Present path is now wired, but the inserted generated Present currently uses immediate presentation before Unity's original Present. This validates correctness and resource lifetime without intentionally blocking RimWorld's simulation/main thread.

The next pacing milestone is to distribute generated and real Presents across display intervals without adding meaningful CPU work. Candidate modes are:

1. conservative VSync 2x pacing for high-refresh displays;
2. DXGI/frame-latency-assisted pacing where supported;
3. adaptive bypass when base FPS or GPU headroom is insufficient.

RimFG must not gain smoothness by simply sleeping or performing image work on the RimWorld main thread.

## Next milestone

1. Validate the native double-Present build in CI and in RimWorld 1.5.
2. Add an explicit safe enable/disable setting for generated Present injection.
3. Add proper 2x display pacing and resize/device-reset recovery telemetry.
4. Replace simple midpoint blend with camera-aware warp.
5. Add vendor-neutral optical flow, then optional NVIDIA Optical Flow D3D11 backend.
6. Replace coarse HUD bands with dynamic RimWorld UI/window masking.

## Optical-flow backends

Planned backend order:

1. Vendor-neutral compute-shader fallback.
2. NVIDIA Optical Flow API on supported GPUs (D3D11).
3. Optional vendor-specific backends later.

NVIDIA Optical Flow is attractive because the D3D11 API can use dedicated optical-flow hardware instead of consuming RimWorld CPU time. RimFG must still retain a vendor-neutral fallback and must not require CUDA or DX12.

## Build

Managed build expects `RIMWORLD_DIR` to point to the RimWorld install directory.

Native build:

```powershell
cmake -S RimFG/Source/Native -B build/rimfg-native -A x64
cmake --build build/rimfg-native --config Release --parallel
```

You may pass `UNITY_PLUGIN_API_DIR` to headers matching the exact Unity build. If omitted, CMake fetches Unity's official NativeRenderingPlugin headers for development/CI.

## Status

Development prototype. The GPU-only midpoint generation, HUD composite, DXGI hook, and Generated Present -> Real Present chain are now implemented. The remaining V0.1 blockers are build/runtime validation, safe user controls, and proper display pacing.
