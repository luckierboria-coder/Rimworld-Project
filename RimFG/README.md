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

## V0.1 scope

- RimWorld 1.5 managed bridge.
- Native Unity rendering plug-in skeleton for D3D11.
- Render-thread callback through `GL.IssuePluginEvent`.
- Camera metadata channel.
- HUD rectangle metadata channel.
- GPU compute shader prototypes for HUD masking and camera-aware midpoint interpolation.
- Native telemetry counters.
- Present/frame-pacing layer is intentionally isolated as the next milestone; Unity plug-in callbacks alone cannot create independent display presents between game presents.

## Planned pipeline

```text
RimWorld / Unity
   | tiny metadata only
   v
RimFG.Managed
   |
   v
RimFG.Native (D3D11)
   |-- current/previous GPU textures
   |-- HUD mask compute pass
   |-- camera-aware warp
   |-- optical flow backend
   |-- interpolation/composite
   v
Present layer -> generated frame -> real frame
```

## Optical-flow backends

Planned backend order:

1. Vendor-neutral compute-shader fallback.
2. NVIDIA Optical Flow API on supported GPUs (D3D11).
3. Optional vendor-specific backends later.

The project must always keep a fallback path and must not require CUDA or DX12.

## Build layout

- `Source/Managed` - RimWorld/Unity C# bridge.
- `Source/Native` - Windows x64 D3D11 native plug-in.
- `Source/Native/Shaders` - HLSL compute passes.

Managed build expects `RIMWORLD_DIR` to point to the RimWorld install directory.

Native build expects Visual Studio 2022 and a Unity native plug-in header checkout supplied via `UNITY_PLUGIN_API_DIR`.

## Status

V0.1 bootstrap branch. Not yet a user-installable frame generator. The current milestone establishes the low-overhead GPU-native architecture before implementing the independent Present scheduler/hook.
