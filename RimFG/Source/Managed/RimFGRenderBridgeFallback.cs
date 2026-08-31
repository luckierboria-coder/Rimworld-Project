using System;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimFG
{
    // Unity 2019.4 can load a native DLL through Mono/LoadLibrary without ever
    // invoking UnityPluginLoad. GL.IssuePluginEvent normally still works, but on
    // some RimWorld render paths it never reaches the native callback. This
    // fail-closed bridge schedules the same callback from a camera CommandBuffer,
    // after a GPU-only copy of CameraTarget, so the event is guaranteed to execute
    // on Unity's render thread in the correct order.
    [StaticConstructorOnStartup]
    internal static class RimFGRenderBridgeFallbackBootstrap
    {
        static RimFGRenderBridgeFallbackBootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(() =>
            {
                try
                {
                    var go = new GameObject("RimFG.RenderBridgeFallback");
                    UnityEngine.Object.DontDestroyOnLoad(go);
                    go.hideFlags = HideFlags.HideAndDontSave;
                    go.AddComponent<RimFGRenderBridgeFallback>();
                }
                catch (Exception ex)
                {
                    Log.Warning("[RimFG] Failed to create render-event fallback bridge: " + ex.Message);
                }
            });
        }
    }

    internal sealed class RimFGRenderBridgeFallback : MonoBehaviour
    {
        private const int ReadyProbeFrames = 30;
        private const int GeneratedProbeFrames = 240;

        private IntPtr renderEventFunc;
        private Camera captureCamera;
        private CommandBuffer commands;
        private RenderTexture sceneCapture;
        private int captureWidth;
        private int captureHeight;
        private int frames;
        private bool active;
        private bool logged;

        private void Awake()
        {
            try
            {
                if (!NativeInterop.EnsureNativeLoaded(out _))
                    return;
                renderEventFunc = NativeInterop.RimFG_GetRenderEventFunc();
            }
            catch
            {
                renderEventFunc = IntPtr.Zero;
            }
        }

        private void LateUpdate()
        {
            if (renderEventFunc == IntPtr.Zero)
                return;

            ++frames;
            if (!active)
            {
                bool d3dReady = false;
                bool generated = false;
                try
                {
                    d3dReady = NativeInterop.RimFG_IsD3D11Ready() != 0;
                    generated = NativeInterop.RimFG_HasGeneratedFrame() != 0;
                }
                catch
                {
                    return;
                }

                // Do not add a second capture path when the normal bridge is healthy.
                // If D3D11 never becomes ready, or we are on a map for several seconds
                // with no generated frame, arm the render-thread fallback.
                if (frames < ReadyProbeFrames)
                    return;
                if (d3dReady && (generated || Find.CurrentMap == null || frames < GeneratedProbeFrames))
                    return;

                active = true;
            }

            Camera cam = ResolveCamera();
            if (cam == null)
                return;

            EnsureCapture(cam, Screen.width, Screen.height);
        }

        private Camera ResolveCamera()
        {
            if (captureCamera != null && captureCamera.isActiveAndEnabled)
                return captureCamera;

            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

            // Camera.main depends on the MainCamera tag. RimWorld/modded render stacks
            // do not always preserve that tag, so resolve once from active cameras.
            Camera[] cameras = Camera.allCameras;
            Camera best = null;
            float bestArea = 0f;
            for (int i = 0; i < cameras.Length; ++i)
            {
                Camera c = cameras[i];
                if (c == null || !c.isActiveAndEnabled)
                    continue;

                Rect p = c.pixelRect;
                float area = p.width * p.height;
                if (c.orthographic)
                    area *= 2f;
                if (area > bestArea)
                {
                    bestArea = area;
                    best = c;
                }
            }
            return best;
        }

        private void EnsureCapture(Camera cam, int width, int height)
        {
            if (width <= 0 || height <= 0)
                return;

            if (sceneCapture != null && captureCamera == cam && captureWidth == width && captureHeight == height)
                return;

            ReleaseCapture();

            captureCamera = cam;
            captureWidth = width;
            captureHeight = height;
            sceneCapture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "RimFG.RenderBridgeFallback.SceneCapture",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            sceneCapture.Create();

            commands = new CommandBuffer { name = "RimFG GPU capture + native render event fallback" };
            commands.Blit(BuiltinRenderTextureType.CameraTarget, sceneCapture);
            commands.IssuePluginEvent(renderEventFunc, 1);
            captureCamera.AddCommandBuffer(CameraEvent.AfterEverything, commands);

            // GetNativeTexturePtr is intentionally called only on create/recreate.
            NativeInterop.RimFG_SetSceneTexture(sceneCapture.GetNativeTexturePtr(), width, height);

            if (!logged)
            {
                logged = true;
                Log.Message("[RimFG] Render-thread CommandBuffer fallback armed on camera '" + captureCamera.name + "'.");
            }
        }

        private void ReleaseCapture()
        {
            if (captureCamera != null && commands != null)
            {
                try { captureCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, commands); }
                catch { }
            }

            if (commands != null)
            {
                commands.Release();
                commands = null;
            }

            if (sceneCapture != null)
            {
                sceneCapture.Release();
                UnityEngine.Object.Destroy(sceneCapture);
                sceneCapture = null;
            }

            captureCamera = null;
            captureWidth = 0;
            captureHeight = 0;
        }

        private void OnDestroy()
        {
            ReleaseCapture();
        }
    }
}
