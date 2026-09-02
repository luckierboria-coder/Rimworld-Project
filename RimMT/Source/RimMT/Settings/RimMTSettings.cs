using Verse;

namespace RimMT
{
    public sealed class RimMTSettings : ModSettings
    {
        // Production controls exposed by V0.9.2 Unified Lean.
        public bool TextCache = true;
        public bool AdaptiveBurst = true;
        public bool WorkScanAcceleration = true;

        // Hidden legacy compatibility fields. Retired production modules still compile in the
        // source tree, but Unified Lean never installs/enables their Harmony hooks. Keeping these
        // fields avoids source-level breakage and lets old settings XML load harmlessly.
        public bool OverlayCache = false;
        public int OverlayRefreshFrames = 30;
        public bool ReachNoCache = false;
        public int ReachNoCacheTtl = 20;
        public bool HotPathDiagnostics = false;
        public bool PathSnapshotWorker = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref TextCache, "textCache", true);
            Scribe_Values.Look(ref AdaptiveBurst, "adaptiveBurst", true);
            Scribe_Values.Look(ref WorkScanAcceleration, "workScanAcceleration", true);

            // Read/write legacy keys only for migration compatibility; runtime hard-forces all
            // corresponding retired features OFF and the UI does not expose them.
            Scribe_Values.Look(ref OverlayCache, "overlayCache", false);
            Scribe_Values.Look(ref OverlayRefreshFrames, "overlayRefreshFrames", 30);
            Scribe_Values.Look(ref ReachNoCache, "reachNoCache", false);
            Scribe_Values.Look(ref ReachNoCacheTtl, "reachNoCacheTtl", 20);
            Scribe_Values.Look(ref HotPathDiagnostics, "hotPathDiagnostics", false);
            Scribe_Values.Look(ref PathSnapshotWorker, "pathSnapshotWorker", false);

            OverlayCache = false;
            ReachNoCache = false;
            HotPathDiagnostics = false;
            PathSnapshotWorker = false;
        }
    }
}
