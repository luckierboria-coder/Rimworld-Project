using UnityEngine;
using Verse;

namespace RimMT
{
    public sealed class RimMTMod : Mod
    {
        public static RimMTSettings Settings;
        private static Vector2 settingsScrollPosition;

        public RimMTMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<RimMTSettings>();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override string SettingsCategory() { return "RimMT"; }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            const float contentHeight = 760f;
            Rect topRect = new Rect(inRect.x, inRect.y, inRect.width, 92f);
            Listing_Standard top = new Listing_Standard();
            top.Begin(topRect);
            top.Label("RimMT_SettingsIntro".Translate());
            if (top.ButtonText("RimMT_LogReport".Translate())) RimMTDiagnostics.LogRuntimeReport();
            if (top.ButtonText("RimMT_RunSelfTest".Translate())) RimMTDiagnostics.RunWorkerSelfTest();
            top.End();

            Rect scrollOut = new Rect(inRect.x, inRect.y + 96f, inRect.width, Mathf.Max(80f, inRect.height - 96f));
            Rect viewRect = new Rect(0f, 0f, scrollOut.width - 18f, contentHeight);
            Widgets.BeginScrollView(scrollOut, ref settingsScrollPosition, viewRect);

            Listing_Standard listing = new Listing_Standard();
            listing.Begin(viewRect);
            listing.CheckboxLabeled("RimMT_AdaptiveBurst".Translate(), ref Settings.AdaptiveBurst, "RimMT_AdaptiveBurstDesc".Translate());
            listing.CheckboxLabeled("RimMT_HotPathDiagnostics".Translate(), ref Settings.HotPathDiagnostics, "RimMT_HotPathDiagnosticsDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("RimMT_TextCache".Translate(), ref Settings.TextCache, "RimMT_TextCacheDesc".Translate());
            listing.CheckboxLabeled("RimMT_OverlayCache".Translate(), ref Settings.OverlayCache, "RimMT_OverlayCacheDesc".Translate());
            listing.Label("RimMT_OverlayRefresh".Translate(Settings.OverlayRefreshFrames));
            Settings.OverlayRefreshFrames = (int)listing.Slider(Settings.OverlayRefreshFrames, 5f, 120f);
            listing.GapLine();

            listing.Label("RimMT_Production".Translate());
            listing.CheckboxLabeled("RimMT_WorkScanAcceleration".Translate(), ref Settings.WorkScanAcceleration, "RimMT_WorkScanAccelerationDesc".Translate());
            listing.Label("RimMT_JobPartitionWorkerThreshold".Translate(Settings.JobPartitionWorkerThreshold));
            int workerThreshold = Mathf.RoundToInt(listing.Slider(Settings.JobPartitionWorkerThreshold, 96f, 2048f) / 32f) * 32;
            Settings.JobPartitionWorkerThreshold = Mathf.Clamp(workerThreshold, 96, 2048);
            listing.Label("RimMT_JobPartitionWorkerThresholdDesc".Translate());
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

            listing.End();
            Widgets.EndScrollView();
            RimMTRuntime.ApplySettings(Settings);
        }

        public override void WriteSettings()
        {
            base.WriteSettings();
            RimMTRuntime.ApplySettings(Settings);
        }
    }
}
