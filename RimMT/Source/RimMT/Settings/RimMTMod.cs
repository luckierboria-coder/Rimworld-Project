using UnityEngine;
using Verse;

namespace RimMT
{
    public sealed class RimMTMod : Mod
    {
        public static RimMTSettings Settings;

        public RimMTMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimMTSettings>();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override string SettingsCategory() { return "RimMT"; }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("RimMT V0.9.3 Consolidated Stable — single DLL production build");
            listing.Label("Production counters are lightweight aggregates. The realtime monitor is optional, closed by default, and refreshes its text every 30 rendered frames.");
            listing.GapLine();

            listing.CheckboxLabeled("RimMT_AdaptiveBurst".Translate(), ref Settings.AdaptiveBurst, "RimMT_AdaptiveBurstDesc".Translate());
            listing.CheckboxLabeled("RimMT_TextCache".Translate(), ref Settings.TextCache, "RimMT_TextCacheDesc".Translate());
            listing.CheckboxLabeled("RimMT_WorkScanAcceleration".Translate(), ref Settings.WorkScanAcceleration, "RimMT_WorkScanAccelerationDesc".Translate());

            listing.GapLine();
            if (listing.ButtonText("RimMT_OpenMonitor".Translate()))
                Find.WindowStack.Add(new RimMTMonitorWindow());
            if (listing.ButtonText("RimMT_LogReport".Translate()))
                RimMTDiagnostics.LogRuntimeReport();
            if (listing.ButtonText("RimMT_RunSelfTest".Translate()))
                RimMTDiagnostics.RunWorkerSelfTest();

            listing.End();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            RimMTRuntime.ApplySettings(Settings);
        }
    }
}
