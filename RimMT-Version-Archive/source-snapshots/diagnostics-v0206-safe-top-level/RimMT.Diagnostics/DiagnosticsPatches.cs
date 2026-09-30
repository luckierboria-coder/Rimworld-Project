using Verse;
using Verse.AI;

namespace RimMT.Diagnostics;

internal static class DiagnosticsPatches
{
	public static void TickPrefix()
	{
		DiagnosticsHub.BeginTick();
	}

	public static void TickPostfix()
	{
		DiagnosticsHub.EndTick();
	}

	public static void PawnPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void PawnPostfix(Pawn __instance, long __state)
	{
		DiagnosticsHub.EndPawn(__state, __instance);
	}

	public static void JobTrackerPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void JobTrackerPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.JobTracker);
	}

	public static void DeterminePrefix(Pawn_JobTracker __instance, ref long __state)
	{
		DiagnosticsV03.BeginDetermine(__instance);
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void DeterminePostfix(Pawn_JobTracker __instance, ThinkResult __result, long __state)
	{
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		DiagnosticsHub.EndPhase(__state, DiagPhase.DetermineNextJob);
		DiagnosticsHub.ObserveDetermine(__instance, __result);
		DiagnosticsV03.EndDetermine(__instance, __result);
	}

	public static void PatherPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void PatherPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.Pather);
	}

	public static void GenClosestPrefix(ref long __state)
	{
		__state = (RimMTDiagnosticsSettings.EnableSearchTiming ? DiagnosticsHub.BeginPhase() : 0);
	}

	public static void GenClosestPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.GenClosest);
	}

	public static void ReachPrefix(ref long __state)
	{
		__state = (RimMTDiagnosticsSettings.EnableSearchTiming ? DiagnosticsHub.BeginPhase() : 0);
	}

	public static void ReachPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.Reachability);
	}

	public static void MapPostPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void MapPostPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.MapPostTick);
	}

	public static void WorldPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void WorldPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.WorldTick);
	}

	public static void StorytellerPrefix(ref long __state)
	{
		__state = DiagnosticsHub.BeginPhase();
	}

	public static void StorytellerPostfix(long __state)
	{
		DiagnosticsHub.EndPhase(__state, DiagPhase.Storyteller);
	}
}
