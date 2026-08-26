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
            listing.Label("RimMT_SettingsIntro".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimMT_AdaptiveBurst".Translate(), ref Settings.AdaptiveBurst, "RimMT_AdaptiveBurstDesc".Translate());
            listing.CheckboxLabeled("RimMT_HotPathDiagnostics".Translate(), ref Settings.HotPathDiagnostics, "RimMT_HotPathDiagnosticsDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimMT_TextCache".Translate(), ref Settings.TextCache, "RimMT_TextCacheDesc".Translate());
            listing.CheckboxLabeled("RimMT_OverlayCache".Translate(), ref Settings.OverlayCache, "RimMT_OverlayCacheDesc".Translate());
            listing.Label("RimMT_OverlayRefresh".Translate(Settings.OverlayRefreshFrames));
            Settings.OverlayRefreshFrames = (int)listing.Slider(Settings.OverlayRefreshFrames, 5f, 120f);
            listing.GapLine();
            listing.Label("RimMT_Experimental".Translate());
            listing.CheckboxLabeled("RimMT_PathSnapshotWorker".Translate(), ref Settings.PathSnapshotWorker, "RimMT_PathSnapshotWorkerDesc".Translate());
            listing.CheckboxLabeled("RimMT_ReachNoCache".Translate(), ref Settings.ReachNoCache, "RimMT_ReachNoCacheDesc".Translate());
            listing.Label("RimMT_ReachTtl".Translate(Settings.ReachNoCacheTtl));
            Settings.ReachNoCacheTtl = (int)listing.Slider(Settings.ReachNoCacheTtl, 5f, 60f);
            listing.GapLine();

            if (WorkGiverDetailPatches.CaptureActive)
            {
                listing.Label("RimMT_JobGiverCaptureActive".Translate(WorkGiverDetailPatches.PackagesRemaining));
                if (listing.ButtonText("RimMT_StopJobGiverCapture".Translate()))
                    WorkGiverDetailPatches.RequestStopCapture();
            }
            else
            {
                listing.Label("RimMT_JobGiverCaptureDesc".Translate());
                if (listing.ButtonText("RimMT_StartJobGiverCapture".Translate()))
                    WorkGiverDetailPatches.StartCapture();
            }

            listing.GapLine();
            if (listing.ButtonText("RimMT_LogReport".Translate())) RimMTDiagnostics.LogRuntimeReport();
            if (listing.ButtonText("RimMT_RunSelfTest".Translate())) RimMTDiagnostics.RunWorkerSelfTest();
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
