using Verse;

namespace RimMT
{
    public sealed class RimMTSettings : ModSettings
    {
        public bool TextCache = true;
        public bool OverlayCache = true;
        public int OverlayRefreshFrames = 30;
        public bool ReachNoCache = false;
        public int ReachNoCacheTtl = 20;
        public bool AdaptiveBurst = true;
        public bool HotPathDiagnostics = true;
        public bool PathSnapshotWorker = true;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref TextCache, "textCache", true);
            Scribe_Values.Look(ref OverlayCache, "overlayCache", true);
            Scribe_Values.Look(ref OverlayRefreshFrames, "overlayRefreshFrames", 30);
            Scribe_Values.Look(ref ReachNoCache, "reachNoCache", false);
            Scribe_Values.Look(ref ReachNoCacheTtl, "reachNoCacheTtl", 20);
            Scribe_Values.Look(ref AdaptiveBurst, "adaptiveBurst", true);
            Scribe_Values.Look(ref HotPathDiagnostics, "hotPathDiagnostics", true);
            Scribe_Values.Look(ref PathSnapshotWorker, "pathSnapshotWorker", true);
            OverlayRefreshFrames = Clamp(OverlayRefreshFrames, 5, 120);
            ReachNoCacheTtl = Clamp(ReachNoCacheTtl, 5, 60);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}
