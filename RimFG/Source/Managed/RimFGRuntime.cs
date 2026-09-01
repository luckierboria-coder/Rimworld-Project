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
        private const float TelemetryIntervalSeconds = 5f;

        private bool nativeAvailable;
        private bool nativeReadyLogged;
        private bool generatedLogged;
        private bool swapChainLogged;
        private bool injectedPresentLogged;
        private uint frameIndex;
        private int appliedTargetFps = -1;
        private PresentMode appliedPresentMode = (PresentMode)(-1);

        private float nextTelemetryAt;
        private ulong previousGeneratedPresents;
        private ulong previousSkippedPresents;
        private float previousTelemetryAt;
        private int consecutiveNoOutputWindows;

        private readonly HudRect[] hudRects = new HudRect[8];
        private int hudRectCount;

        private void Awake()
        {
            try
            {
                if (!NativeInterop.EnsureNativeLoaded(out string loadError))
                {
                    nativeAvailable = false;
                    Log.Warning("[RimFG] Native bridge unavailable: " + loadError);
                    return;
                }

                nativeAvailable = true;
                NativeInterop.RimFG_GetRenderEventFunc();
                NativeInterop.RimFG_SetEnabled(1);

                if (NativeInterop.RimFG_StartPresentHook() == 0)
                    Log.Warning("[RimFG] DXGI Present hook did not initialize; frame generation is disabled.");

                ApplyConfiguredState(force: true);
                nextTelemetryAt = Time.realtimeSinceStartup + TelemetryIntervalSeconds;
                previousTelemetryAt = Time.realtimeSinceStartup;
                previousGeneratedPresents = NativeInterop.RimFG_GetGeneratedPresentCount();
                previousSkippedPresents = NativeInterop.RimFG_GetSkippedPresentCount();
                Log.Message("[RimFG] Dedicated presenter armed. Unity swapchain is capture-only; generated frames are presented through RimFG's independent output swapchain.");
                Log.Message("[RimFG] Diagnostics enabled: presenter effectiveness summary will be written to Player.log every 5 seconds while RimFG is active.");
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] Native bridge unavailable: " + ex);
            }
        }

        private void LateUpdate()
        {
            if (!nativeAvailable)
                return;

            ApplyConfiguredState(force: false);

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
                NativeInterop.RimFG_SubmitFrameState(ref metadata, hudRects, hudRectCount);

                if (!nativeReadyLogged && NativeInterop.RimFG_IsD3D11Ready() != 0)
                {
                    nativeReadyLogged = true;
                    Log.Message("[RimFG] D3D11 native backend is ready.");
                }
                if (!generatedLogged && NativeInterop.RimFG_HasGeneratedFrame() != 0)
                {
                    generatedLogged = true;
                    Log.Message("[RimFG] DXGI backbuffer prediction history is ready.");
                }
                if (!swapChainLogged && NativeInterop.RimFG_HasUnitySwapChain() != 0)
                {
                    swapChainLogged = true;
                    Log.Message("[RimFG] RimWorld DXGI swapchain captured.");
                }
                if (!injectedPresentLogged && NativeInterop.RimFG_GetGeneratedPresentCount() > 0UL)
                {
                    injectedPresentLogged = true;
                    Log.Message("[RimFG] First generated frame reached the dedicated presenter swapchain.");
                }

                EmitTelemetryIfDue();
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                try { NativeInterop.RimFG_SetPresentMode((int)PresentMode.Disabled); } catch { }
                Log.Warning("[RimFG] Native bridge disabled after runtime error: " + ex);
            }
        }

        private void EmitTelemetryIfDue()
        {
            float now = Time.realtimeSinceStartup;
            if (now < nextTelemetryAt)
                return;

            float elapsed = Mathf.Max(0.1f, now - previousTelemetryAt);
            nextTelemetryAt = now + TelemetryIntervalSeconds;
            previousTelemetryAt = now;

            double baseFps = NativeInterop.RimFG_GetEstimatedBaseFps();
            double outputFps = NativeInterop.RimFG_GetEstimatedOutputFps();
            int targetFps = NativeInterop.RimFG_GetTargetOutputFps();
            ulong generated = NativeInterop.RimFG_GetGeneratedPresentCount();
            ulong skipped = NativeInterop.RimFG_GetSkippedPresentCount();
            ulong generatedDelta = generated >= previousGeneratedPresents ? generated - previousGeneratedPresents : 0UL;
            ulong skippedDelta = skipped >= previousSkippedPresents ? skipped - previousSkippedPresents : 0UL;
            previousGeneratedPresents = generated;
            previousSkippedPresents = skipped;

            double generatedPerSecond = generatedDelta / (double)elapsed;
            double skippedPerSecond = skippedDelta / (double)elapsed;
            double gpuMs = NativeInterop.RimFG_GetGpuFrameGenerationMs();
            int stage = NativeInterop.RimFG_GetNativeStage();
            int quality = NativeInterop.RimFG_GetGpuQualityTier();
            bool unitySwapchain = NativeInterop.RimFG_HasUnitySwapChain() != 0;
            bool hasGeneratedFrame = NativeInterop.RimFG_HasGeneratedFrame() != 0;

            string verdict = ClassifyPresenter(baseFps, outputFps, targetFps, generatedPerSecond, skippedPerSecond, unitySwapchain, hasGeneratedFrame);

            Log.Message(
                "[RimFG][PresenterDiag] " +
                "base=" + baseFps.ToString("F1") + "fps, " +
                "target=" + targetFps + "fps, " +
                "actualOutput=" + outputFps.ToString("F1") + "fps, " +
                "generated=" + generatedPerSecond.ToString("F1") + "/s, " +
                "skipped=" + skippedPerSecond.ToString("F1") + "/s, " +
                "gpuFG=" + gpuMs.ToString("F2") + "ms, " +
                "stage=" + stage + ", quality=" + quality + ", " +
                "unitySwapchain=" + (unitySwapchain ? "yes" : "no") + ", " +
                "prediction=" + (hasGeneratedFrame ? "ready" : "not-ready") + ", " +
                "verdict=" + verdict + ".");

            if (targetFps > baseFps + 1.0 && outputFps < 1.0)
            {
                consecutiveNoOutputWindows++;
                if (consecutiveNoOutputWindows == 2)
                    Log.Warning("[RimFG][PresenterDiag] Dedicated presenter has produced no measurable output for 10 seconds even though FG is requested. This points to the output swapchain/window/present path rather than the prediction generator.");
            }
            else
            {
                consecutiveNoOutputWindows = 0;
            }
        }

        private static string ClassifyPresenter(
            double baseFps,
            double outputFps,
            int targetFps,
            double generatedPerSecond,
            double skippedPerSecond,
            bool unitySwapchain,
            bool hasGeneratedFrame)
        {
            if (!unitySwapchain)
                return "NO_UNITY_SWAPCHAIN";
            if (!hasGeneratedFrame)
                return "PREDICTION_NOT_READY";
            if (targetFps <= baseFps + 0.5)
                return "FG_NOT_NEEDED_AT_CURRENT_BASE_FPS";
            if (outputFps < 1.0)
                return generatedPerSecond > 0.1 ? "GENERATED_BUT_OUTPUT_CLOCK_NOT_MEASURABLE" : "DEDICATED_PRESENTER_NOT_OUTPUTTING";
            if (generatedPerSecond < 0.1)
                return "OUTPUT_ACTIVE_BUT_NO_GENERATED_FRAMES";

            double desired = Math.Min(targetFps, 1000);
            if (outputFps >= desired * 0.90)
                return skippedPerSecond > generatedPerSecond * 0.25 ? "OUTPUT_NEAR_TARGET_WITH_HIGH_DROP_RATE" : "OUTPUT_NEAR_TARGET";
            if (skippedPerSecond > generatedPerSecond)
                return "PRESENTER_DROP_BOUND";
            return "OUTPUT_BELOW_TARGET";
        }

        private void ApplyConfiguredState(bool force)
        {
            RimFGSettings settings = RimFGMod.Settings;
            PresentMode mode = settings != null ? settings.presentMode : PresentMode.ImmediateValidation;
            int targetFps = settings != null ? Mathf.RoundToInt(settings.targetOutputFps) : 60;
            targetFps = Mathf.Clamp(targetFps, 1, 1000000000);

            if (force || mode != appliedPresentMode)
            {
                NativeInterop.RimFG_SetPresentMode((int)mode);
                appliedPresentMode = mode;
                Log.Message("[RimFG] Effective Present mode: " + mode + ".");
            }

            if (force || targetFps != appliedTargetFps)
            {
                NativeInterop.RimFG_SetTargetOutputFps(targetFps);
                appliedTargetFps = targetFps;
                Log.Message("[RimFG] Target output FPS: " + targetFps + ".");
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
            if (stack != null)
            {
                var windows = stack.Windows;
                for (int i = 0; i < windows.Count && hudRectCount < hudRects.Length; ++i)
                {
                    Window window = windows[i];
                    if (window == null) continue;
                    Rect rect = window.windowRect;
                    AddHudRect(rect.xMin - 4f, rect.yMin - 4f, rect.width + 8f, rect.height + 8f, width, height);
                }
            }
        }

        private void OnDestroy()
        {
            if (nativeAvailable)
            {
                try { NativeInterop.RimFG_SetPresentMode((int)PresentMode.Disabled); } catch { }
                try { NativeInterop.RimFG_SetEnabled(0); } catch { }
                try { NativeInterop.RimFG_StopPresentHook(); } catch { }
            }
        }
    }
}
