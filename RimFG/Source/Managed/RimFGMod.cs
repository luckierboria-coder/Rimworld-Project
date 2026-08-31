using RimWorld;
using UnityEngine;
using Verse;

namespace RimFG
{
    internal sealed class RimFGSettings : ModSettings
    {
        public PresentMode presentMode = PresentMode.ImmediateValidation;
        public bool adaptiveBypass = true;
        public float minimumBaseFps = 30f;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref presentMode, "presentMode", PresentMode.ImmediateValidation);
            Scribe_Values.Look(ref adaptiveBypass, "adaptiveBypass", true);
            Scribe_Values.Look(ref minimumBaseFps, "minimumBaseFps", 30f);
            base.ExposeData();
        }
    }

    internal sealed class RimFGMod : Mod
    {
        internal static RimFGSettings Settings;

        public RimFGMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimFGSettings>();
        }

        public override string SettingsCategory()
        {
            return "RimFG";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("Present mode");

            if (listing.RadioButton("Disabled — native game Present only", Settings.presentMode == PresentMode.Disabled))
                SetMode(PresentMode.Disabled);
            if (listing.RadioButton("Immediate Validation — generated frame is injected immediately", Settings.presentMode == PresentMode.ImmediateValidation))
                SetMode(PresentMode.ImmediateValidation);
            if (listing.RadioButton("VSync 2× — generated and real frames each consume one VBlank (high-refresh displays)", Settings.presentMode == PresentMode.VSync2x))
                SetMode(PresentMode.VSync2x);

            listing.GapLine();
            listing.CheckboxLabeled("Adaptive bypass", ref Settings.adaptiveBypass,
                "Temporarily bypass generated Presents when the real/base frame rate becomes too low. GPU history remains active, so recovery is immediate.");

            listing.Label("Minimum base FPS: " + Settings.minimumBaseFps.ToString("F0"));
            Settings.minimumBaseFps = listing.Slider(Settings.minimumBaseFps, 20f, 60f);
            listing.Label("The adaptive check uses a tiny exponential moving average of Unity's existing frame time. It does not sample the framebuffer or run image analysis on CPU.");

            listing.GapLine();
            listing.Label("Live GPU telemetry");
            try
            {
                double gpuMs = NativeInterop.RimFG_GetGpuFrameGenerationMs();
                GpuQualityTier tier = (GpuQualityTier)NativeInterop.RimFG_GetGpuQualityTier();
                ulong generated = NativeInterop.RimFG_GetGeneratedPresentCount();
                ulong skipped = NativeInterop.RimFG_GetSkippedPresentCount();
                int swapchain = NativeInterop.RimFG_HasUnitySwapChain();

                listing.Label("FG GPU EMA: " + (gpuMs > 0.0 ? gpuMs.ToString("F2") + " ms" : "warming up"));
                listing.Label("Quality tier: " + tier);
                listing.Label("Generated Presents: " + generated + "   Skipped: " + skipped);
                listing.Label("DXGI swapchain: " + (swapchain != 0 ? "captured" : "not captured yet"));
            }
            catch
            {
                listing.Label("Telemetry unavailable — native backend not loaded.");
            }

            listing.GapLine();
            listing.Label("VSync 2× is intended for 120/144/165 Hz class displays. On a 60 Hz display it can reduce the real-frame rate, so use Immediate Validation until display pacing is verified.");
            listing.End();
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
        }

        private static void SetMode(PresentMode mode)
        {
            Settings.presentMode = mode;
            Settings.Write();
            try { NativeInterop.RimFG_SetPresentMode((int)mode); }
            catch { }
        }
    }
}
