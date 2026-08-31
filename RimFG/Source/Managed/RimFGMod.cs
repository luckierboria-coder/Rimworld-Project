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
        }

        public override string SettingsCategory() => "RimFG";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("Present mode");

            if (listing.RadioButton("Disabled — native game Present only", Settings.presentMode == PresentMode.Disabled))
                SetMode(PresentMode.Disabled);
            if (listing.RadioButton("Target pacing — generate frames toward the selected output FPS", Settings.presentMode == PresentMode.ImmediateValidation))
                SetMode(PresentMode.ImmediateValidation);
            if (listing.RadioButton("VSync 2× — legacy validation mode", Settings.presentMode == PresentMode.VSync2x))
                SetMode(PresentMode.VSync2x);

            listing.GapLine();
            listing.Label("Target output FPS: " + Settings.targetOutputFps.ToString("F0"));

            float oldTarget = Settings.targetOutputFps;
            float safeTarget = Mathf.Clamp(Settings.targetOutputFps, 1f, 1000000f);
            float logValue = Mathf.Log10(safeTarget);
            float newLogValue = listing.Slider(logValue, 0f, 6f);
            float newTarget = Mathf.Pow(10f, newLogValue);

            // Snap near common display rates while still allowing the slider to span
            // all the way from 1 FPS to 1,000,000 FPS on a useful logarithmic scale.
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

            listing.Label("Variable-ratio FG has no fixed 2× or 3× multiplier cap. RimFG divides each real-frame interval into as many prediction points as the selected target requires. GPU/DXGI/display throughput still determines how many generated frames can actually reach the screen.");

            listing.GapLine();
            listing.Label("Live GPU telemetry");
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
                    ulong generated = NativeInterop.RimFG_GetGeneratedPresentCount();
                    ulong skipped = NativeInterop.RimFG_GetSkippedPresentCount();
                    int swapchain = NativeInterop.RimFG_HasUnitySwapChain();
                    double baseFps = NativeInterop.RimFG_GetEstimatedBaseFps();
                    int targetFps = NativeInterop.RimFG_GetTargetOutputFps();
                    double ratio = baseFps > 0.0 ? targetFps / baseFps : 0.0;

                    listing.Label("Native: loaded");
                    listing.Label("Generation stage: " + DescribeStage(stage));
                    listing.Label("Estimated real/base FPS: " + (baseFps > 0.0 ? baseFps.ToString("F1") : "warming up"));
                    listing.Label("Target output FPS: " + targetFps);
                    listing.Label("Requested FG ratio: " + (ratio > 0.0 ? ratio.ToString("F2") + "×" : "warming up"));
                    listing.Label("FG GPU EMA: " + (gpuMs > 0.0 ? gpuMs.ToString("F2") + " ms / generated frame" : "warming up"));
                    listing.Label("Quality tier: " + tier);
                    listing.Label("Generated Presents: " + generated + "   Skipped: " + skipped);
                    listing.Label("DXGI swapchain: " + (swapchain != 0 ? "captured" : "not captured yet"));
                }
            }
            catch (System.Exception ex)
            {
                listing.Label("Native backend call failed: " + ex.GetType().Name + " — " + ex.Message);
            }

            listing.GapLine();
            listing.Label("RimFG never waits for VBlank on the RimWorld thread. If prediction or DXGI submission cannot keep up with the requested target, generated frames are dropped instead of stalling TPS.");
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
                case NativeStage.DuplicateFallback: return "Present path active — compute unavailable, GPU duplicate fallback";
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
