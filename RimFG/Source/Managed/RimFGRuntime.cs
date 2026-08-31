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
        private bool swapChainLogged;
        private bool injectedPresentLogged;
        private bool renderBridgeLogged;
        private string lastRenderBridgeError;
        private int retryCaptureAfterFrame;
        private uint frameIndex;

        private readonly HudRect[] hudRects = new HudRect[8];
        private int hudRectCount;

        private Camera captureCamera;
        private CommandBuffer captureCommands;
        private RenderTexture sceneCapture;
        private int captureWidth;
        private int captureHeight;

        private float emaFrameSeconds = 1f / 60f;
        private bool adaptiveBypassed;
        private PresentMode appliedPresentMode = (PresentMode)(-1);

        private void Awake()
        {
            try
            {
                renderEventFunc = NativeInterop.RimFG_GetRenderEventFunc();
                nativeAvailable = renderEventFunc != IntPtr.Zero;
                NativeInterop.RimFG_SetEnabled(nativeAvailable ? 1 : 0);

                if (nativeAvailable && NativeInterop.RimFG_StartPresentHook() == 0)
                    Log.Warning("[RimFG] DXGI Present hook did not initialize; interpolation can run but generated frames cannot be displayed.");

                ApplyConfiguredPresentMode(force: true);
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

            try
            {
                UpdateAdaptivePresentState();

                Camera cam = ResolveCaptureCamera();
                if (Time.frameCount >= retryCaptureAfterFrame)
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

                // Metadata is tiny CPU-side state. The actual capture and native callback
                // are both queued in one Camera CommandBuffer and execute on Unity's
                // render thread in strict GPU order: CameraTarget -> sceneCapture -> event.
                NativeInterop.RimFG_SubmitFrameState(ref metadata, hudRects, hudRectCount);

                if (!nativeReadyLogged && NativeInterop.RimFG_IsD3D11Ready() != 0)
                {
                    nativeReadyLogged = true;
                    Log.Message("[RimFG] D3D11 native backend is ready.");
                }

                if (!generatedLogged && NativeInterop.RimFG_HasGeneratedFrame() != 0)
                {
                    generatedLogged = true;
                    Log.Message("[RimFG] Camera/zoom/residual-flow GPU interpolation produced its first generated frame.");
                }

                if (!swapChainLogged && NativeInterop.RimFG_HasUnitySwapChain() != 0)
                {
                    swapChainLogged = true;
                    Log.Message("[RimFG] RimWorld DXGI swapchain captured.");
                }

                if (!injectedPresentLogged && NativeInterop.RimFG_GetGeneratedPresentCount() > 0UL)
                {
                    injectedPresentLogged = true;
                    Log.Message("[RimFG] First generated frame reached the Present path.");
                }
            }
            catch (Exception ex)
            {
                // Never let a presentation experiment poison RimWorld's update loop.
                // Tear down only the camera-side bridge and retry later; the real game
                // Present and simulation remain authoritative and unblocked.
                HandleRenderBridgeFailure(ex);
            }
        }

        private Camera ResolveCaptureCamera()
        {
            if (captureCamera != null && captureCamera.isActiveAndEnabled)
                return captureCamera;

            Camera main = Camera.main;
            if (main != null && main.isActiveAndEnabled)
                return main;

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

        private void HandleRenderBridgeFailure(Exception ex)
        {
            string message = ex.GetType().Name + ": " + ex.Message;
            ReleaseSceneCapture();
            retryCaptureAfterFrame = Time.frameCount + 120;

            if (lastRenderBridgeError != message)
            {
                lastRenderBridgeError = message;
                Log.Warning("[RimFG] Render bridge failed closed; native Present remains pass-through. Retrying in 120 frames. " + message);
            }
        }

        private void UpdateAdaptivePresentState()
        {
            float dt = Time.unscaledDeltaTime;
            if (dt > 0f && dt < 0.5f)
                emaFrameSeconds += (dt - emaFrameSeconds) * 0.05f;

            RimFGSettings settings = RimFGMod.Settings;
            if (settings == null || !settings.adaptiveBypass || settings.presentMode == PresentMode.Disabled)
            {
                if (adaptiveBypassed)
                {
                    adaptiveBypassed = false;
                    ApplyConfiguredPresentMode(force: true);
                }
                else
                {
                    ApplyConfiguredPresentMode(force: false);
                }
                return;
            }

            float fps = emaFrameSeconds > 0.0001f ? 1f / emaFrameSeconds : 999f;
            float lowThreshold = Mathf.Max(10f, settings.minimumBaseFps);
            float recoverThreshold = lowThreshold + 5f;

            if (!adaptiveBypassed && fps < lowThreshold)
            {
                adaptiveBypassed = true;
                ApplyPresentMode(PresentMode.Disabled);
                Log.Message("[RimFG] Adaptive bypass: base FPS fell below " + lowThreshold.ToString("F0") + ". Generated Present paused; GPU history remains active.");
            }
            else if (adaptiveBypassed && fps >= recoverThreshold)
            {
                adaptiveBypassed = false;
                ApplyConfiguredPresentMode(force: true);
                Log.Message("[RimFG] Adaptive bypass cleared: base FPS recovered to " + fps.ToString("F1") + ".");
            }
            else if (!adaptiveBypassed)
            {
                ApplyConfiguredPresentMode(force: false);
            }
        }

        private void ApplyConfiguredPresentMode(bool force)
        {
            PresentMode mode = RimFGMod.Settings != null ? RimFGMod.Settings.presentMode : PresentMode.ImmediateValidation;
            if (adaptiveBypassed)
                mode = PresentMode.Disabled;

            if (force || mode != appliedPresentMode)
                ApplyPresentMode(mode);
        }

        private void ApplyPresentMode(PresentMode mode)
        {
            if (mode == appliedPresentMode)
                return;

            NativeInterop.RimFG_SetPresentMode((int)mode);
            appliedPresentMode = mode;
            Log.Message("[RimFG] Effective Present mode: " + mode + ".");
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
            if (!sceneCapture.IsCreated())
                throw new InvalidOperationException("RenderTexture.Create returned no GPU resource.");

            captureCommands = new CommandBuffer { name = "RimFG GPU capture + native render event" };
            captureCommands.Blit(BuiltinRenderTextureType.CameraTarget, sceneCapture);
            captureCommands.IssuePluginEvent(renderEventFunc, 1);
            captureCamera.AddCommandBuffer(CameraEvent.AfterEverything, captureCommands);

            // Called only on create/recreate. Native takes its own COM reference before
            // the pointer crosses to the render thread; no framebuffer readback occurs.
            IntPtr nativeTexture = sceneCapture.GetNativeTexturePtr();
            if (nativeTexture == IntPtr.Zero)
                throw new InvalidOperationException("RenderTexture returned a null D3D11 texture pointer.");
            NativeInterop.RimFG_SetSceneTexture(nativeTexture, width, height);

            retryCaptureAfterFrame = 0;
            lastRenderBridgeError = null;
            if (!renderBridgeLogged)
            {
                renderBridgeLogged = true;
                Log.Message("[RimFG] Unified render-thread bridge armed on camera '" + captureCamera.name + "'.");
            }
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
                try { NativeInterop.RimFG_SetPresentMode((int)PresentMode.Disabled); } catch { }
                try { NativeInterop.RimFG_SetEnabled(0); } catch { }
                try { NativeInterop.RimFG_StopPresentHook(); } catch { }
            }
        }

        private void AddHudRect(float x, float y, float width, float height, int screenWidth, int screenHeight)
        {
            if (hudRectCount >= hudRects.Length || width <= 0f || height <= 0f)
                return;

            float left = Mathf.Clamp(x, 0f, screenWidth);
            float top = Mathf.Clamp(y, 0f, screenHeight);
            float right = Mathf.Clamp(x + width, 0f, screenWidth);
            float bottom = Mathf.Clamp(y + height, 0f, screenHeight);
            if (right <= left || bottom <= top)
                return;

            hudRects[hudRectCount++] = new HudRect(left, top, right - left, bottom - top);
        }

        private void BuildHudRects(int width, int height)
        {
            hudRectCount = 0;
            if (width <= 0 || height <= 0)
                return;

            AddHudRect(0f, 0f, width, Mathf.Min(150f, height * 0.14f), width, height);

            float bottomHeight = Mathf.Min(260f, height * 0.25f);
            AddHudRect(0f, height - bottomHeight, width, bottomHeight, width, height);

            if (Find.CurrentMap != null)
            {
                float paneWidth = Mathf.Min(520f, width * 0.24f);
                float paneHeight = Mathf.Min(620f, height * 0.54f);
                AddHudRect(0f, height - bottomHeight - paneHeight, paneWidth, paneHeight, width, height);
            }

            WindowStack stack = Find.WindowStack;
            if (stack == null)
                return;

            var windows = stack.Windows;
            if (windows == null)
                return;

            for (int i = 0; i < windows.Count && hudRectCount < hudRects.Length; ++i)
            {
                Window window = windows[i];
                if (window == null)
                    continue;

                Rect rect = window.windowRect;
                AddHudRect(rect.xMin - 4f, rect.yMin - 4f, rect.width + 8f, rect.height + 8f, width, height);
            }
        }
    }
}
