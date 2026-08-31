using System;
using UnityEngine;
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
        private uint frameIndex;

        // Fixed storage: no per-frame allocation. V0.1 uses conservative coarse HUD bands.
        private readonly HudRect[] hudRects = new HudRect[8];
        private int hudRectCount;

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
                NativeInterop.RimFG_SubmitFrameMetadata(ref metadata);
                NativeInterop.RimFG_SubmitHudRects(hudRects, hudRectCount);

                // Queues GPU/native work onto Unity's render thread. No framebuffer readback occurs here.
                GL.IssuePluginEvent(renderEventFunc, 1);

                if (!nativeReadyLogged && NativeInterop.RimFG_IsD3D11Ready() != 0)
                {
                    nativeReadyLogged = true;
                    Log.Message("[RimFG] D3D11 native backend is ready.");
                }
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] Native bridge disabled after runtime error: " + ex.Message);
            }
        }

        private void BuildHudRects(int width, int height)
        {
            // V0.1 coarse mask. Dynamic window/gizmo rectangles are a later managed patch layer.
            // Coordinates use Unity screen space; native shader converts as needed.
            hudRectCount = 0;

            if (width <= 0 || height <= 0)
                return;

            // Top colonist/resource/alerts region.
            hudRects[hudRectCount++] = new HudRect(0f, 0f, width, Mathf.Min(150f, height * 0.14f));

            // Bottom tabs/gizmos/inspect controls region.
            float bottomHeight = Mathf.Min(260f, height * 0.25f);
            hudRects[hudRectCount++] = new HudRect(0f, height - bottomHeight, width, bottomHeight);

            // Left-side inspection pane reserve. Conservative only while a map is active.
            if (Find.CurrentMap != null)
            {
                float paneWidth = Mathf.Min(520f, width * 0.24f);
                float paneHeight = Mathf.Min(620f, height * 0.54f);
                hudRects[hudRectCount++] = new HudRect(0f, height - bottomHeight - paneHeight, paneWidth, paneHeight);
            }
        }
    }
}
