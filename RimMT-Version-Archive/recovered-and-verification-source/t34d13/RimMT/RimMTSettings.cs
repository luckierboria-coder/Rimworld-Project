using Verse;

namespace RimMT;

public sealed class RimMTSettings : ModSettings
{
	public bool TextCache = true;

	public bool AdaptiveBurst = true;

	public bool WorkScanAcceleration = true;

	public bool OverlayCache;

	public int OverlayRefreshFrames = 30;

	public bool ReachNoCache;

	public int ReachNoCacheTtl = 20;

	public bool HotPathDiagnostics;

	public bool PathSnapshotWorker;

	public override void ExposeData()
	{
		Scribe_Values.Look<bool>(ref TextCache, "textCache", true, false);
		Scribe_Values.Look<bool>(ref AdaptiveBurst, "adaptiveBurst", true, false);
		Scribe_Values.Look<bool>(ref WorkScanAcceleration, "workScanAcceleration", true, false);
		Scribe_Values.Look<bool>(ref OverlayCache, "overlayCache", false, false);
		Scribe_Values.Look<int>(ref OverlayRefreshFrames, "overlayRefreshFrames", 30, false);
		Scribe_Values.Look<bool>(ref ReachNoCache, "reachNoCache", false, false);
		Scribe_Values.Look<int>(ref ReachNoCacheTtl, "reachNoCacheTtl", 20, false);
		Scribe_Values.Look<bool>(ref HotPathDiagnostics, "hotPathDiagnostics", false, false);
		Scribe_Values.Look<bool>(ref PathSnapshotWorker, "pathSnapshotWorker", false, false);
		OverlayCache = false;
		ReachNoCache = false;
		HotPathDiagnostics = false;
		PathSnapshotWorker = false;
	}
}
