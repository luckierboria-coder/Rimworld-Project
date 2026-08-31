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
            Settings.targetOutputFps = Mathf.Round(listing.Slider(Settings.targetOutputFps, 30f, 165f));
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

            listing.Label("RimFG now aims at this display/output rate instead of disabling frame generation when base FPS is low. V0.1 can generate at most one extra frame per real frame, so practical output is capped near 2× the current base FPS.");

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
                    double maxUseful = baseFps > 0.0 ? baseFps * 2.0 : 0.0;

                    listing.Label("Native: loaded");
                    listing.Label("Generation stage: " + DescribeStage(stage));
                    listing.Label("Estimated real/base FPS: " + (baseFps > 0.0 ? baseFps.ToString("F1") : "warming up"));
                    listing.Label("Target output FPS: " + targetFps + (maxUseful > 0.0 && targetFps > maxUseful + 1.0 ? "   (currently 2×-limited to ~" + maxUseful.ToString("F0") + ")" : ""));
                    listing.Label("FG GPU EMA: " + (gpuMs > 0.0 ? gpuMs.ToString("F2") + " ms" : "warming up"));
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
            listing.Label("Target pacing never waits for VBlank on the RimWorld thread. If a generated Present cannot be submitted without blocking, RimFG drops that generated frame instead of stalling TPS.");
            listing.End();
        }

        private static string DescribeStage(NativeStage stage)
        {
            switch (stage)
            {
                case NativeStage.Idle: return "idle — waiting for RimWorld backbuffer";
                case NativeStage.BackbufferSeen: return "backbuffer captured — creating GPU resources";
                case NativeStage.HistoryPrimed: return "history primed — next real frame can generate";
                case NativeStage.Generated: return "prediction/interpolation active";
                case NativeStage.DuplicateFallback: return "Present path active — compute unavailable, GPU duplicate fallback";
                case NativeStage.ErrorNoDevice: return "ERROR: D3D11 device/context unavailable";
                case NativeStage.ErrorBadBackbuffer: return "ERROR: unsupported backbuffer size/MSAA state";
                case NativeStage.ErrorHistoryTexture: return "ERROR: history texture creation failed";
                case NativeStage.ErrorShader: return "ERROR: interpolation shader creation failed";
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
