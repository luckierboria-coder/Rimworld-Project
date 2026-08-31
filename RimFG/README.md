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
- Windows x64 native CI build.

The midpoint algorithm is currently a simple GPU blend. It exists to validate the complete zero-readback frame-history path before replacing the interpolation kernel with camera-aware warping and optical flow.

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
   +-- current milestone: identify RimWorld swapchain
   +-- next milestone: composite HUD + schedule generated/real presents
```

## Next milestone

1. Build a present-sized generated composite texture.
2. Preserve real HUD/UI from the current backbuffer while replacing only the scene area with the generated frame.
3. Insert one generated Present between consecutive real Presents.
4. Add frame pacing and fail-safe bypass for resize/device reset/low base FPS.
5. Replace simple midpoint blend with camera-aware warp and then optical flow.

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

Development prototype. It now creates GPU-resident intermediate frames and captures RimWorld's DXGI Present path, but does not yet display generated frames to the monitor. The independent double-Present/composite stage is the remaining V0.1 blocker.
