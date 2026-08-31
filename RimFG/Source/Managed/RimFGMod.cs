using RimWorld;
using UnityEngine;
using Verse;

namespace RimFG
{
    internal sealed class RimFGSettings : ModSettings
    {
        public PresentMode presentMode = PresentMode.ImmediateValidation;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref presentMode, "presentMode", PresentMode.ImmediateValidation);
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
            listing.Label("VSync 2× is intended for 120/144/165 Hz class displays. On a 60 Hz display it can reduce the real-frame rate, so use Immediate Validation until display pacing is verified.");
            listing.End();
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
