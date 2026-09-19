using System;
using System.IO;
using System.Threading;
using RimWorld;
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
        private static readonly object TelemetryFileLock = new object();

        private bool nativeAvailable;
        private bool nativeReadyLogged;
        private bool generatedLogged;
        private bool swapChainLogged;
        private bool firstGeneratedPresentLogged;
        private uint frameIndex;
        private int appliedTargetFps = -1;
        private PresentMode appliedPresentMode = (PresentMode)(-1);

        private float nextTelemetryAt;
        private float previousTelemetryAt;
        private int consecutiveNoOutputWindows;
        private ulong previousRealPresents;
        private ulong previousGeneratedPresents;
        private ulong previousSkippedPresents;
        private ulong previousLatencyTimeouts;
        private ulong previousPresentFailures;
        private ulong previousStalePredictions;
        private ulong previousRingBusyDrops;
        private ulong previousCompositionFailures;
        private string telemetryPath;

        private readonly HudRect[] hudRects = new HudRect[NativeInterop.MaxHudRects];
        private int hudRectCount;

        private void Awake()
        {
            try
            {
                telemetryPath = Path.Combine(Application.persistentDataPath, "RimFG.log");
                QueueTelemetry("=== RimFG session " + DateTime.UtcNow.ToString("O") + " ===");

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
                ResetTelemetryBaselines();
                Log.Message("[RimFG] Buffered DirectComposition presenter armed. Presenter uses an independent D3D11 device/context.");
                Log.Message("[RimFG] Diagnostics are written to RimFG.log under Unity persistentDataPath; periodic telemetry no longer goes through Verse.Log.");
            }
            catch (Exception ex)
            {
                nativeAvailable = false;
                Log.Warning("[RimFG] Native bridge unavailable: " + ex);
            }
        }

        private void QueueTelemetry(string line)
        {
            string path = telemetryPath;
            if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(line))
                return;
            string payload = DateTime.UtcNow.ToString("O") + " " + line + Environment.NewLine;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    lock (TelemetryFileLock)
                        File.AppendAllText(path, payload);
                }
                catch
                {
                    // Diagnostics must never affect gameplay or disable FG.
                }
            });
        }

        private void ResetTelemetryBaselines()
        {
            float now = Time.realtimeSinceStartup;
            nextTelemetryAt = now + TelemetryIntervalSeconds;
            previousTelemetryAt = now;
            previousRealPresents = NativeInterop.RimFG_GetRealPresentCount();
            previousGeneratedPresents = NativeInterop.RimFG_GetGeneratedPresentCount();
            previousSkippedPresents = NativeInterop.RimFG_GetSkippedPresentCount();
            previousLatencyTimeouts = NativeInterop.RimFG_GetFrameLatencyTimeoutCount();
            previousPresentFailures = NativeInterop.RimFG_GetPresentFailureCount();
            previousStalePredictions = NativeInterop.RimFG_GetStalePredictionCount();
            previousRingBusyDrops = NativeInterop.RimFG_GetRingBusyDropCount();
            previousCompositionFailures = NativeInterop.RimFG_GetCompositionFailureCount();
        }

        private void LateUpdate()
        {
            if (!nativeAvailable)
                return;

            ApplyConfiguredState(force: false);

            Camera cam = Camera.main;
            Vector3 cameraPos = cam != null ? cam.transform.position : Vector3.zero;
            BuildProtectedRects(Screen.width, Screen.height);

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
                    Log.Message("[RimFG] Buffered interpolation history is ready.");
                }
                if (!swapChainLogged && NativeInterop.RimFG_HasUnitySwapChain() != 0)
                {
                    swapChainLogged = true;
                    Log.Message("[RimFG] RimWorld DXGI swapchain captured.");
                }
                if (!firstGeneratedPresentLogged && NativeInterop.RimFG_GetGeneratedPresentCount() > 0UL)
                {
                    firstGeneratedPresentLogged = true;
                    Log.Message("[RimFG] First generated frame reached the independent DirectComposition presenter.");
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

        private static ulong Delta(ulong current, ref ulong previous)
        {
            ulong result = current >= previous ? current - previous : 0UL;
            previous = current;
            return result;
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
            int monitorHz = NativeInterop.RimFG_GetMonitorRefreshHz();
            bool presenterReady = NativeInterop.RimFG_IsPresenterReady() != 0;
            bool unitySwapchain = NativeInterop.RimFG_HasUnitySwapChain() != 0;
            bool predictionReady = NativeInterop.RimFG_HasGeneratedFrame() != 0;

            double realPerSecond = Delta(NativeInterop.RimFG_GetRealPresentCount(), ref previousRealPresents) / (double)elapsed;
            double generatedPerSecond = Delta(NativeInterop.RimFG_GetGeneratedPresentCount(), ref previousGeneratedPresents) / (double)elapsed;
            double skippedPerSecond = Delta(NativeInterop.RimFG_GetSkippedPresentCount(), ref previousSkippedPresents) / (double)elapsed;
            double waitTimeoutPerSecond = Delta(NativeInterop.RimFG_GetFrameLatencyTimeoutCount(), ref previousLatencyTimeouts) / (double)elapsed;
            double presentFailPerSecond = Delta(NativeInterop.RimFG_GetPresentFailureCount(), ref previousPresentFailures) / (double)elapsed;
            double stalePerSecond = Delta(NativeInterop.RimFG_GetStalePredictionCount(), ref previousStalePredictions) / (double)elapsed;
            double ringBusyPerSecond = Delta(NativeInterop.RimFG_GetRingBusyDropCount(), ref previousRingBusyDrops) / (double)elapsed;
            double compositionFailPerSecond = Delta(NativeInterop.RimFG_GetCompositionFailureCount(), ref previousCompositionFailures) / (double)elapsed;

            double gpuMs = NativeInterop.RimFG_GetGpuFrameGenerationMs();
            int stage = NativeInterop.RimFG_GetNativeStage();
            int quality = NativeInterop.RimFG_GetGpuQualityTier();

            string verdict = ClassifyPresenter(
                baseFps, outputFps, targetFps, monitorHz, realPerSecond, generatedPerSecond,
                skippedPerSecond, waitTimeoutPerSecond, presentFailPerSecond, stalePerSecond,
                ringBusyPerSecond, compositionFailPerSecond, unitySwapchain, predictionReady, presenterReady);

            QueueTelemetry(
                "[RimFG][PresenterDiag] " +
                "base=" + baseFps.ToString("F1") + "fps, " +
                "target=" + targetFps + "fps, monitor=" + monitorHz + "Hz, " +
                "actualOutput=" + outputFps.ToString("F1") + "fps, " +
                "realShown=" + realPerSecond.ToString("F1") + "/s, " +
                "fgShown=" + generatedPerSecond.ToString("F1") + "/s, " +
                "skipped=" + skippedPerSecond.ToString("F1") + "/s, " +
                "waitTO=" + waitTimeoutPerSecond.ToString("F1") + "/s, " +
                "presentFail=" + presentFailPerSecond.ToString("F1") + "/s, " +
                "stale=" + stalePerSecond.ToString("F1") + "/s, " +
                "ringBusy=" + ringBusyPerSecond.ToString("F1") + "/s, " +
                "compFail=" + compositionFailPerSecond.ToString("F1") + "/s, " +
                "gpuFG=" + gpuMs.ToString("F2") + "ms, " +
                "stage=" + stage + ", quality=" + quality + ", " +
                "presenter=" + (presenterReady ? "ready" : "not-ready") + ", " +
                "protectedRects=" + hudRectCount + ", " +
                "verdict=" + verdict + ".");

            if (targetFps > baseFps + 1.0 && outputFps < 1.0)
            {
                consecutiveNoOutputWindows++;
                if (consecutiveNoOutputWindows == 2)
                    QueueTelemetry("[RimFG][PresenterDiag] No measurable presenter output for 10 seconds while FG is requested.");
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
            int monitorHz,
            double realPerSecond,
            double generatedPerSecond,
            double skippedPerSecond,
            double waitTimeoutPerSecond,
            double presentFailPerSecond,
            double stalePerSecond,
            double ringBusyPerSecond,
            double compositionFailPerSecond,
            bool unitySwapchain,
            bool predictionReady,
            bool presenterReady)
        {
            if (!unitySwapchain) return "NO_UNITY_SWAPCHAIN";
            if (!predictionReady) return "PREDICTION_NOT_READY";
            if (targetFps <= baseFps + 0.5) return "FG_NOT_NEEDED_AT_CURRENT_BASE_FPS";
            if (compositionFailPerSecond > 0.1) return "DIRECTCOMPOSITION_FAILURE";
            if (!presenterReady) return "COMPOSITION_PRESENTER_NOT_READY";
            if (presentFailPerSecond > 0.1) return "DXGI_PRESENT_FAILURE";
            if (ringBusyPerSecond > 1.0) return "SHARED_BATCH_PRODUCER_PRESSURE";
            if (stalePerSecond > Math.Max(2.0, generatedPerSecond * 0.25)) return "STALE_BATCH_PRESSURE";
            if (waitTimeoutPerSecond > Math.Max(2.0, generatedPerSecond * 0.25)) return "SHARED_TEXTURE_CONTENTION";
            if (outputFps < 1.0) return "PRESENTER_NOT_OUTPUTTING";
            if (generatedPerSecond < 0.1 && targetFps > baseFps + 1.0) return "OUTPUT_ACTIVE_BUT_NO_GENERATED_FRAMES";

            double desired = Math.Max(1.0, Math.Min(targetFps, Math.Max(24, monitorHz)));
            if (outputFps >= desired * 0.90)
            {
                if (skippedPerSecond > Math.Max(2.0, generatedPerSecond * 0.25))
                    return "OUTPUT_NEAR_TARGET_WITH_HIGH_DROP_RATE";
                return "OUTPUT_NEAR_TARGET";
            }
            if (realPerSecond + generatedPerSecond < desired * 0.70) return "OUTPUT_SLOT_UNDERRUN";
            return "OUTPUT_BELOW_TARGET";
        }

        private void ApplyConfiguredState(bool force)
        {
            RimFGSettings settings = RimFGMod.Settings;
            PresentMode mode = settings != null ? settings.presentMode : PresentMode.ImmediateValidation;
            if (mode == PresentMode.VSync2x)
                mode = PresentMode.ImmediateValidation;

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

        private void AddPhysicalRect(float x, float y, float width, float height, int physicalWidth, int physicalHeight)
        {
            if (hudRectCount >= hudRects.Length || width <= 0f || height <= 0f)
                return;

            float left = Mathf.Clamp(x, 0f, physicalWidth);
            float top = Mathf.Clamp(y, 0f, physicalHeight);
            float right = Mathf.Clamp(x + width, 0f, physicalWidth);
            float bottom = Mathf.Clamp(y + height, 0f, physicalHeight);
            if (right <= left || bottom <= top)
                return;

            hudRects[hudRectCount++] = new HudRect(left, top, right - left, bottom - top);
        }

        private void AddUiRect(float x, float y, float width, float height, int physicalWidth, int physicalHeight)
        {
            float scale = Mathf.Max(0.01f, Prefs.UIScale);
            AddPhysicalRect(x * scale, y * scale, width * scale, height * scale, physicalWidth, physicalHeight);
        }

        private static bool PawnNameIsDrawn(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Map == null || pawn.Map.fogGrid.IsFogged(pawn.Position))
                return false;
            if (pawn.RaceProps.Humanlike)
                return true;

            AnimalNameDisplayMode mode = Prefs.AnimalNameMode;
            if (mode == AnimalNameDisplayMode.None)
                return false;
            if (mode == AnimalNameDisplayMode.TameAll)
                return pawn.Name != null;
            if (mode == AnimalNameDisplayMode.TameNamed)
                return pawn.Name != null && !pawn.Name.Numerical;
            return false;
        }

        private void AddPawnLabelRects(int physicalWidth, int physicalHeight)
        {
            Map map = Find.CurrentMap;
            if (map == null || map.mapPawns == null)
                return;

            var pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count && hudRectCount < hudRects.Length; ++i)
            {
                Pawn pawn = pawns[i];
                if (!PawnNameIsDrawn(pawn))
                    continue;

                Vector2 pos = GenMapUI.LabelDrawPosFor(pawn, -0.6f);
                if (!pawn.RaceProps.Humanlike)
                    pos.y -= 4f;

                int labelChars = pawn.LabelShort != null ? pawn.LabelShort.Length : 0;
                float labelWidth = Mathf.Clamp(28f + labelChars * 8f, 44f, 320f);
                const float horizontalPad = 10f;
                const float topPad = 6f;
                const float protectedHeight = 28f;

                float left = pos.x - labelWidth * 0.5f - horizontalPad;
                float top = pos.y - topPad;

                if (left > UI.screenWidth + 32f || left + labelWidth + horizontalPad * 2f < -32f ||
                    top > UI.screenHeight + 32f || top + protectedHeight < -32f)
                    continue;

                AddUiRect(left, top, labelWidth + horizontalPad * 2f, protectedHeight, physicalWidth, physicalHeight);
            }
        }

        private void BuildProtectedRects(int physicalWidth, int physicalHeight)
        {
            hudRectCount = 0;
            if (physicalWidth <= 0 || physicalHeight <= 0)
                return;

            int uiWidth = Math.Max(1, UI.screenWidth);
            int uiHeight = Math.Max(1, UI.screenHeight);

            float topHeight = Mathf.Min(150f, uiHeight * 0.14f);
            AddUiRect(0f, 0f, uiWidth, topHeight, physicalWidth, physicalHeight);

            float bottomHeight = Mathf.Min(260f, uiHeight * 0.25f);
            AddUiRect(0f, uiHeight - bottomHeight, uiWidth, bottomHeight, physicalWidth, physicalHeight);

            if (Find.CurrentMap != null)
            {
                float paneWidth = Mathf.Min(520f, uiWidth * 0.24f);
                float paneHeight = Mathf.Min(620f, uiHeight * 0.54f);
                AddUiRect(0f, uiHeight - bottomHeight - paneHeight, paneWidth, paneHeight, physicalWidth, physicalHeight);
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
                    AddUiRect(rect.xMin - 6f, rect.yMin - 6f, rect.width + 12f, rect.height + 12f, physicalWidth, physicalHeight);
                }
            }

            AddPawnLabelRects(physicalWidth, physicalHeight);
        }

        private void OnDestroy()
        {
            if (nativeAvailable)
            {
                try { NativeInterop.RimFG_SetPresentMode((int)PresentMode.Disabled); } catch { }
                try { NativeInterop.RimFG_SetEnabled(0); } catch { }
                try { NativeInterop.RimFG_StopPresentHook(); } catch { }
            }
            QueueTelemetry("=== RimFG session end ===");
        }
    }
}
