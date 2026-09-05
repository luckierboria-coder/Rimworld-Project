using RimWorld;
using UnityEngine;
using Verse;

namespace RimFG
{
    internal sealed class RimFGSettings : ModSettings
    {
        public PresentMode presentMode = PresentMode.ImmediateValidation;
        public float targetOutputFps = 60f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref presentMode, "presentMode", PresentMode.ImmediateValidation);
            Scribe_Values.Look(ref targetOutputFps, "targetOutputFps", 60f);
            base.ExposeData();
        }
    }

    internal sealed class RimFGMod : Mod
    {
        internal static RimFGSettings Settings;

        public RimFGMod(ModContentPack content) : base(content)
        {
            NativeInterop.ConfigureModRoot(content.RootDir);
            if (NativeInterop.EnsureNativeLoaded(out string loadError))
                Log.Message("[RimFG] Explicit native load succeeded: " + NativeInterop.LoadedNativePath);
            else
                Log.Warning("[RimFG] Explicit native load failed: " + loadError);

            Settings = GetSettings<RimFGSettings>();
            if (Settings.presentMode == PresentMode.VSync2x)
            {
                Settings.presentMode = PresentMode.ImmediateValidation;
                Settings.Write();
                Log.Message("[RimFG] Retired legacy VSync2x setting migrated to the non-blocking target presenter.");
            }
        }

        public override string SettingsCategory() => "RimFG";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("Present mode");

            if (listing.RadioButton("Disabled — native game Present only", Settings.presentMode == PresentMode.Disabled))
                SetMode(PresentMode.Disabled);
            if (listing.RadioButton("Target pacing — DirectComposition presenter", Settings.presentMode == PresentMode.ImmediateValidation))
                SetMode(PresentMode.ImmediateValidation);

            listing.GapLine();
            listing.Label("Target output FPS: " + Settings.targetOutputFps.ToString("F0"));

            float oldTarget = Settings.targetOutputFps;
            float safeTarget = Mathf.Clamp(Settings.targetOutputFps, 1f, 1000000f);
            float logValue = Mathf.Log10(safeTarget);
            float newLogValue = listing.Slider(logValue, 0f, 6f);
            float newTarget = Mathf.Pow(10f, newLogValue);

            float[] common = { 30f, 45f, 60f, 75f, 90f, 120f, 144f, 165f, 240f, 360f, 480f, 1000f };
            float best = newTarget;
            float bestRelative = 1f;
            for (int i = 0; i < common.Length; ++i)
            {
                float relative = Mathf.Abs(newTarget - common[i]) / common[i];
                if (relative < bestRelative)
                {
                    bestRelative = relative;
                    best = common[i];
                }
            }
            if (bestRelative < 0.025f)
                newTarget = best;

            Settings.targetOutputFps = Mathf.Clamp(Mathf.Round(newTarget), 1f, 1000000f);
            if (Mathf.Abs(Settings.targetOutputFps - oldTarget) >= 0.5f)
            {
                Settings.Write();
                try
                {
                    if (NativeInterop.EnsureNativeLoaded(out _))
                        NativeInterop.RimFG_SetTargetOutputFps(Mathf.RoundToInt(Settings.targetOutputFps));
                }
                catch { }
            }

            listing.Label("RimFG has no fixed 2×/3× prediction multiplier cap. Visible output is physically capped by the active monitor refresh rate; requesting more never makes the RimWorld thread wait.");

            listing.GapLine();
            listing.Label("Live presenter / GPU telemetry");
            try
            {
                if (!NativeInterop.EnsureNativeLoaded(out string loadError))
                {
                    listing.Label("Native backend load failed:");
                    listing.Label(loadError ?? "unknown error");
                }
                else
                {
                    double gpuMs = NativeInterop.RimFG_GetGpuFrameGenerationMs();
                    GpuQualityTier tier = (GpuQualityTier)NativeInterop.RimFG_GetGpuQualityTier();
                    NativeStage stage = (NativeStage)NativeInterop.RimFG_GetNativeStage();
                    ulong real = NativeInterop.RimFG_GetRealPresentCount();
                    ulong generated = NativeInterop.RimFG_GetGeneratedPresentCount();
                    ulong skipped = NativeInterop.RimFG_GetSkippedPresentCount();
                    ulong waitTo = NativeInterop.RimFG_GetFrameLatencyTimeoutCount();
                    ulong presentFail = NativeInterop.RimFG_GetPresentFailureCount();
                    ulong stale = NativeInterop.RimFG_GetStalePredictionCount();
                    ulong ringBusy = NativeInterop.RimFG_GetRingBusyDropCount();
                    ulong compFail = NativeInterop.RimFG_GetCompositionFailureCount();
                    int swapchain = NativeInterop.RimFG_HasUnitySwapChain();
                    int presenter = NativeInterop.RimFG_IsPresenterReady();
                    int monitorHz = NativeInterop.RimFG_GetMonitorRefreshHz();
                    double baseFps = NativeInterop.RimFG_GetEstimatedBaseFps();
                    double outputFps = NativeInterop.RimFG_GetEstimatedOutputFps();
                    int targetFps = NativeInterop.RimFG_GetTargetOutputFps();
                    double ratio = baseFps > 0.0 ? targetFps / baseFps : 0.0;

                    listing.Label("Native: loaded");
                    listing.Label("Generation stage: " + DescribeStage(stage));
                    listing.Label("Base FPS: " + (baseFps > 0.0 ? baseFps.ToString("F1") : "warming up") +
                        "   Actual output: " + (outputFps > 0.0 ? outputFps.ToString("F1") : "warming up"));
                    listing.Label("Target: " + targetFps + "   Monitor: " + monitorHz + " Hz   Requested ratio: " +
                        (ratio > 0.0 ? ratio.ToString("F2") + "×" : "warming up"));
                    listing.Label("DirectComposition presenter: " + (presenter != 0 ? "ready" : "not ready") +
                        "   Unity swapchain: " + (swapchain != 0 ? "captured" : "not captured"));
                    listing.Label("FG GPU EMA: " + (gpuMs > 0.0 ? gpuMs.ToString("F2") + " ms / generated frame" : "warming up") +
                        "   Quality: " + tier);
                    listing.Label("Shown — Real: " + real + "   FG: " + generated + "   Skipped: " + skipped);
                    listing.Label("Failures — Wait: " + waitTo + "   Present: " + presentFail + "   Stale: " + stale +
                        "   Ring: " + ringBusy + "   DComp: " + compFail);
                }
            }
            catch (System.Exception ex)
            {
                listing.Label("Native backend call failed: " + ex.GetType().Name + " — " + ex.Message);
            }

            listing.GapLine();
            listing.Label("Pawn name labels and major UI regions are copied from the matching real frame instead of being warped. Player.log also receives a per-cause PresenterDiag summary every five seconds.");
            listing.Label("RimFG never waits for VBlank or the presenter mutex on the RimWorld Present path. If the presenter is busy, the FG opportunity is dropped instead of stalling TPS.");
            listing.End();
        }

        private static string DescribeStage(NativeStage stage)
        {
            switch (stage)
            {
                case NativeStage.Idle: return "idle — waiting for RimWorld backbuffer";
                case NativeStage.BackbufferSeen: return "backbuffer captured — preparing temporal history";
                case NativeStage.HistoryPrimed: return "history primed — waiting for second real frame";
                case NativeStage.Generated: return "variable-fraction prediction active";
                case NativeStage.DuplicateFallback: return "compute unavailable — GPU duplicate fallback";
                case NativeStage.ErrorNoDevice: return "ERROR: D3D11 device/context unavailable";
                case NativeStage.ErrorBadBackbuffer: return "ERROR: unsupported backbuffer size/MSAA state";
                case NativeStage.ErrorHistoryTexture: return "ERROR: history texture creation failed";
                case NativeStage.ErrorShader: return "ERROR: prediction shader creation failed";
                case NativeStage.ErrorOutputTexture: return "ERROR: generated output texture creation failed";
                case NativeStage.ErrorMotionConstants: return "ERROR: motion constant buffer upload failed";
                default: return stage.ToString();
            }
        }

        private static void SetMode(PresentMode mode)
        {
            if (mode == PresentMode.VSync2x) mode = PresentMode.ImmediateValidation;
            Settings.presentMode = mode;
            Settings.Write();
            try
            {
                if (NativeInterop.EnsureNativeLoaded(out _))
                    NativeInterop.RimFG_SetPresentMode((int)mode);
            }
            catch { }
        }
    }
}
