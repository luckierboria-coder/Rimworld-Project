using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class TailPawnPatches093T2
{
	private static int patched;

	private static int missing;

	private static string missingNames = string.Empty;

	internal static void Apply(Harmony harmony)
	{
		if (harmony != null)
		{
			PatchOne(harmony, AccessTools.Method(typeof(Pawn), "Tick", (Type[])null, (Type[])null), "PawnPrefix", "PawnPostfix", "Pawn.Tick");
			PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick", (Type[])null, (Type[])null), "PhasePrefix", "JobTrackerPostfix", "Pawn_JobTracker.JobTrackerTick");
			PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob", (Type[])null, (Type[])null), "DeterminePrefix", "DeterminePostfix", "Pawn_JobTracker.DetermineNextJob");
			PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "CheckForJobOverride_NewTemp", (Type[])null, (Type[])null), "PhasePrefix", "OverridePostfix", "Pawn_JobTracker.CheckForJobOverride_NewTemp");
			PatchOne(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick", (Type[])null, (Type[])null), "PhasePrefix", "PatherPostfix", "Pawn_PathFollower.PatherTick");
			Log.Message("[RimMT] T2 Pawn Tail Attribution installed: patched=" + patched + ", missing=" + missing + ((missing == 0) ? "." : (", missingTargets=" + missingNames + ".")) + " Stopwatch timing only on bounded deep-sample ticks; optimizer behavior unchanged.");
		}
	}

	private static void PatchOne(Harmony harmony, MethodBase target, string prefixName, string postfixName, string label)
	{
		//IL_0054: Unknown result type (might be due to invalid IL or missing references)
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Expected O, but got Unknown
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007e: Expected O, but got Unknown
		if (target == null)
		{
			missing++;
			if (missingNames.Length != 0)
			{
				missingNames += ",";
			}
			missingNames += label;
			return;
		}
		try
		{
			HarmonyMethod val = new HarmonyMethod(typeof(TailPawnPatches093T2), prefixName, (Type[])null)
			{
				priority = 800
			};
			HarmonyMethod val2 = new HarmonyMethod(typeof(TailPawnPatches093T2), postfixName, (Type[])null)
			{
				priority = 0
			};
			harmony.Patch(target, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
			patched++;
		}
		catch (Exception ex)
		{
			missing++;
			Log.Warning("[RimMT] T2 pawn attribution target failed closed: " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PhasePrefix(ref long __state)
	{
		__state = (TailPawnAttribution093T2.DeepActive ? TailPawnAttribution093T2.BeginPhase() : 0);
	}

	public static void DeterminePrefix(ref long __state)
	{
		if (!TailPawnAttribution093T2.DeepActive)
		{
			__state = 0L;
			return;
		}
		JobGiverSlowSearch0419S.T8BeginDetermineAttribution();
		__state = TailPawnAttribution093T2.BeginPhase();
	}

	public static void PawnPrefix(Pawn __instance, ref long __state)
	{
		bool deepActive = TailPawnAttribution093T2.DeepActive;
		PawnTickAggregateAttribution093T13.BeginPawn(__instance, deepActive);
		__state = (deepActive ? TailPawnAttribution093T2.BeginPhase() : 0);
		PlayerHumanResidualAttribution093T14.BeginPawn(__instance, deepActive, __state);
	}

	public static void PawnPostfix(Pawn __instance, long __state)
	{
		if (__state != 0L)
		{
			TailPawnAttribution093T2.EndPawn(__state, __instance);
		}
	}

	public static void JobTrackerPostfix(long __state)
	{
		if (__state != 0L)
		{
			TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.JobTrackerTick);
		}
	}

	public static void DeterminePostfix(long __state)
	{
		if (__state != 0L)
		{
			JobGiverSlowSearch0419S.T8EndDetermineAttribution(__state);
			TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
		}
	}

	public static void OverridePostfix(long __state)
	{
		if (__state != 0L)
		{
			TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.CheckForJobOverride);
		}
	}

	public static void PatherPostfix(long __state)
	{
		if (__state != 0L)
		{
			TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.PatherTick);
		}
	}
}
