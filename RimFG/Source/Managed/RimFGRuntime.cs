using System;
using UnityEngine;
using UnityEngine.Rendering;
using Verse;

namespace RimFG
{
    [StaticConstructorOnStartup]
    internal static class RimFGBootstrap
    {
        static RimFGBootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(CreateRuntime);
        }

        private static void CreateRuntime()
        {
            try
            {
                var go = new GameObject("RimFG.Runtime");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<RimFGRuntime>();
                Log.Message("[RimFG] Managed bridge initialized.");
            }
            catch (Exception ex)
            {
                Log.Error("[RimFG] Failed to initialize managed bridge: " + ex);
            }
        }
    }

    internal sealed class RimFGRuntime : MonoBehaviour
    {
        private IntPtr renderEventFunc;
        private bool nativeAvailable;
        private bool nativeReadyLogged;
        private bool generatedLogged;
        private uint frameIndex;

        private readonly HudRect[] hudRects = new HudRect[8];
        private int hudRectCount;

        private Camera captureCamera;
        private CommandBuffer captureCommands;
        private RenderTexture sceneCapture;
        private int captureWidth;
        private int captureHeight;

        private void Awake()
        {
            try
            {
                renderEventFunc = NativeInterop.RimFG_GetRenderEventFunc();
                nativeAvailable = renderEventFunc != IntPtr.Zero;
                NativeInterop.RimFG_SetEnabled(nativeAvailable ? 1 : 0);
            }
            catch (DllNotFoundException)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] RimFG.Native.dll not found. GPU frame-generation bridge is disabled.");
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] Native bridge unavailable: " + ex.Message);
            }
        }

        private void LateUpdate()
        {
            if (!nativeAvailable)
                return;

            Camera cam = Camera.main;
            EnsureSceneCapture(cam, Screen.width, Screen.height);

            Vector3 cameraPos = cam != null ? cam.transform.position : Vector3.zero;
            BuildHudRects(Screen.width, Screen.height);

            var metadata = new FrameMetadata
            {
                abiVersion = NativeInterop.AbiVersion,
                frameIndex = ++frameIndex,
                screenWidth = Screen.width,
                screenHeight = Screen.height,
                cameraX = cameraPos.x,
                cameraY = cameraPos.y,
                cameraZ = cameraPos.z,
                orthographicSize = cam != null && cam.orthographic ? cam.orthographicSize : 0f,
                unscaledDeltaTime = Time.unscaledDeltaTime,
                paused = Find.TickManager != null && Find.TickManager.Paused ? 1 : 0,
                gameSpeed = Find.TickManager != null ? (int)Find.TickManager.CurTimeSpeed : 0,
                hudRectCount = hudRectCount
            };

            try
            {
                NativeInterop.RimFG_SubmitFrameState(ref metadata, hudRects, hudRectCount);

                // Native render callback copies the already GPU-resident HUD-less scene texture
                // and dispatches interpolation. No framebuffer readback or image work occurs in C#.
                GL.IssuePluginEvent(renderEventFunc, 1);

                if (!nativeReadyLogged && NativeInterop.RimFG_IsD3D11Ready() != 0)
                {
                    nativeReadyLogged = true;
                    Log.Message("[RimFG] D3D11 native backend is ready.");
                }

                if (!generatedLogged && NativeInterop.RimFG_HasGeneratedFrame() != 0)
                {
                    generatedLogged = true;
                    Log.Message("[RimFG] GPU interpolation pipeline produced its first generated frame.");
                }
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] Native bridge disabled after runtime error: " + ex.Message);
            }
        }

        private void EnsureSceneCapture(Camera cam, int width, int height)
        {
            if (cam == null || width <= 0 || height <= 0)
            {
                ReleaseSceneCapture();
                return;
            }

            if (sceneCapture != null && captureCamera == cam && captureWidth == width && captureHeight == height)
                return;

            ReleaseSceneCapture();

            captureCamera = cam;
            captureWidth = width;
            captureHeight = height;

            sceneCapture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Default)
            {
                name = "RimFG.SceneCapture",
                useMipMap = false,
                autoGenerateMips = false,
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp
            };
            sceneCapture.Create();

            captureCommands = new CommandBuffer { name = "RimFG HUD-less GPU scene capture" };
            captureCommands.Blit(BuiltinRenderTextureType.CameraTarget, sceneCapture);
            captureCamera.AddCommandBuffer(CameraEvent.AfterEverything, captureCommands);

            // Deliberately one-time per allocation/resize. GetNativeTexturePtr may synchronize
            // Unity's render thread, therefore it is forbidden in the per-frame path.
            IntPtr nativeTexture = sceneCapture.GetNativeTexturePtr();
            NativeInterop.RimFG_SetSceneTexture(nativeTexture, width, height);
        }

        private void ReleaseSceneCapture()
        {
            if (captureCamera != null && captureCommands != null)
            {
                try { captureCamera.RemoveCommandBuffer(CameraEvent.AfterEverything, captureCommands); }
                catch { }
            }

            if (captureCommands != null)
            {
                captureCommands.Release();
                captureCommands = null;
            }

            if (sceneCapture != null)
            {
                try { NativeInterop.RimFG_SetSceneTexture(IntPtr.Zero, 0, 0); }
                catch { }
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
            ReleaseSceneCapture();
            if (nativeAvailable)
            {
                try { NativeInterop.RimFG_SetEnabled(0); }
                catch { }
            }
        }

        private void BuildHudRects(int width, int height)
        {
            hudRectCount = 0;
            if (width <= 0 || height <= 0)
                return;

            hudRects[hudRectCount++] = new HudRect(0f, 0f, width, Mathf.Min(150f, height * 0.14f));

            float bottomHeight = Mathf.Min(260f, height * 0.25f);
            hudRects[hudRectCount++] = new HudRect(0f, height - bottomHeight, width, bottomHeight);

            if (Find.CurrentMap != null)
            {
                float paneWidth = Mathf.Min(520f, width * 0.24f);
                float paneHeight = Mathf.Min(620f, height * 0.54f);
                hudRects[hudRectCount++] = new HudRect(0f, height - bottomHeight - paneHeight, paneWidth, paneHeight);
            }
        }
    }
}
