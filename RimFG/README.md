# RimFG

RimFG is an experimental RimWorld-aware frame-generation project for RimWorld 1.5 on Windows/D3D11.

## Design goal

Use spare GPU capacity to improve presentation smoothness without materially increasing RimWorld main-thread or simulation cost.

Hard constraints:

- No per-frame GPU -> CPU framebuffer readback.
- No image processing in C#/Harmony.
- No per-frame `Texture.GetNativeTexturePtr()` calls.
- Frame-generation work stays in native D3D11/GPU code.
- Managed code sends only tiny camera/game/UI metadata.
- Target CPU overhead: <1% and no measurable TPS regression.

## Current V0.1 pipeline

Implemented on branch `rimfg-v0.1`:

- RimWorld 1.5 managed bridge.
- Persistent HUD-less scene capture through a Camera command buffer.
- GPU-resident capture; `GetNativeTexturePtr()` only on allocation/resize.
- Lock-free managed/native metadata handoff.
- D3D11 previous/current/generated frame textures.
- Camera-aware midpoint reprojection using RimWorld's orthographic camera X/Z motion.
- Large camera cuts automatically fall back to an unwarped midpoint for that frame.
- DXGI `IDXGISwapChain::Present` interception using MinHook.
- Swapchain filtering to the current RimWorld process/window.
- GPU-only real-frame scratch/composite surfaces.
- Conservative real HUD regions copied back onto generated frames.
- Generated Present -> restored real frame -> original Unity Present.
- Flip-model-safe backbuffer reacquisition.
- Fail-safe bypass on TEST presents, exclusive fullscreen, size/format/MSAA mismatch, or missing GPU resources.
- Selectable Present modes: Disabled, Immediate Validation, VSync 2x.
- In-game RimFG Mod Settings for Present mode selection.
- Native generated/skipped Present telemetry.
- Windows x64 native CI builds passing.

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
   |-- Previous frame
   |-- Current frame
   |-- Camera motion metadata
   |-- Camera-aware midpoint compute pass
   v
Generated scene
   |
   | real HUD rectangles copied on GPU
   v
Generated composite
   |
   +--> Present generated frame
   |
   | restore preserved real backbuffer
   v
Original Unity Present
```

## Present modes

- `Disabled`: native Unity/RimWorld presentation only.
- `Immediate Validation`: inject the generated frame immediately before the real frame. Safest mode for validating compatibility.
- `VSync 2x`: generated and real frames each consume a VBlank. Intended for high-refresh displays such as 120/144/165 Hz.

VSync 2x is not recommended on 60 Hz displays because it can reduce the real-frame rate. Adaptive refresh/base-FPS gating is planned before release.

## Next milestone

1. Zoom-aware camera reprojection.
2. Automatic high-refresh/base-FPS/GPU-headroom gating for VSync 2x.
3. Vendor-neutral object-motion optical flow for Pawns/projectiles/motes after camera motion is removed.
4. Optional NVIDIA Optical Flow D3D11 backend.
5. Dynamic RimWorld UI/window masking instead of coarse HUD bands.
6. Package a first installable V0.1 test build after managed/native runtime validation.

## Optical-flow strategy

Camera motion is handled deterministically first, so optical flow does not waste GPU work rediscovering whole-map movement. Optical-flow backends are planned in this order:

1. Vendor-neutral compute-shader fallback.
2. NVIDIA Optical Flow API on supported GPUs (D3D11).
3. Optional vendor-specific backends later.

RimFG must always retain a vendor-neutral fallback and must not require CUDA or DX12.

## Build

Managed build expects `RIMWORLD_DIR` to point to the RimWorld install directory.

Native build:

```powershell
cmake -S RimFG/Source/Native -B build/rimfg-native -A x64
cmake --build build/rimfg-native --config Release --parallel
```

You may pass `UNITY_PLUGIN_API_DIR` to headers matching the exact Unity build. If omitted, CMake fetches Unity's official NativeRenderingPlugin headers for development/CI.

## Status

Development prototype. GPU-only frame history, camera-aware midpoint generation, HUD composite, double-Present output, selectable pacing, and fail-safe bypass are implemented. The next focus is adaptive pacing, zoom handling, optical flow, and first in-game test packaging.
