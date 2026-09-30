using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class TailAttributionPatches093T1
{
	private static int patched;

	private static int missing;

	private static string missingNames = string.Empty;

	internal static void Apply(Harmony harmony)
	{
		if (harmony != null)
		{
			PatchAny(harmony, "TickListPostfix", "Verse.TickList:Tick");
			PatchAny(harmony, "MapPrePostfix", "Verse.Map:MapPreTick");
			PatchAny(harmony, "WorldPostfix", "Verse.World:WorldTick");
			PatchAny(harmony, "StoryWatcherPostfix", "RimWorld.StoryWatcher:StoryWatcherTick");
			PatchAny(harmony, "GameEndPostfix", "RimWorld.GameEnder:GameEndTick");
			PatchAny(harmony, "StorytellerPostfix", "RimWorld.Storyteller:StorytellerTick");
			PatchAny(harmony, "TalesPostfix", "RimWorld.TaleManager:TaleManagerTick");
			PatchAny(harmony, "WorldPostTickPostfix", "Verse.World:WorldPostTick");
			PatchAny(harmony, "MapPostPostfix", "Verse.Map:MapPostTick");
			PatchAny(harmony, "HistoryPostfix", "RimWorld.History:HistoryTick", "Verse.History:HistoryTick");
			PatchAny(harmony, "GameComponentsPostfix", "Verse.GameComponentUtility:GameComponentTick");
			PatchAny(harmony, "AutosaverPostfix", "RimWorld.Autosaver:AutosaverTick", "Verse.Autosaver:AutosaverTick");
			PatchAny(harmony, "ScenarioPostfix", "RimWorld.Scenario:TickScenario", "Verse.Scenario:TickScenario");
			PatchAny(harmony, "DateNotifierPostfix", "RimWorld.DateNotifier:DateNotifierTick", "Verse.DateNotifier:DateNotifierTick");
			PatchAny(harmony, "LettersPostfix", "RimWorld.LetterStack:LetterStackTick", "Verse.LetterStack:LetterStackTick");
			PatchAny(harmony, "FilthPostfix", "RimWorld.FilthMonitor:FilthMonitorTick", "Verse.FilthMonitor:FilthMonitorTick");
			Log.Message("[RimMT] T1 Tail Attribution installed: patched=" + patched + ", missing=" + missing + ((missing == 0) ? "." : (", missingTargets=" + missingNames + ".")) + " Measurement-only top-level timing; optimizer behavior unchanged.");
		}
	}

	private static void PatchAny(Harmony harmony, string postfixName, params string[] specs)
	{
		//IL_00a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ba: Expected O, but got Unknown
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cb: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Expected O, but got Unknown
		MethodBase methodBase = null;
		string text = null;
		for (int i = 0; i < specs.Length; i++)
		{
			try
			{
				methodBase = AccessTools.Method(specs[i], (Type[])null, (Type[])null);
			}
			catch
			{
				methodBase = null;
			}
			if (methodBase != null)
			{
				text = specs[i];
				break;
			}
		}
		if (methodBase == null)
		{
			missing++;
			if (missingNames.Length < 512)
			{
				if (missingNames.Length != 0)
				{
					missingNames += ",";
				}
				missingNames += ((specs.Length == 0) ? "<empty>" : specs[0]);
			}
			return;
		}
		try
		{
			HarmonyMethod val = new HarmonyMethod(typeof(TailAttributionPatches093T1), "PhasePrefix", (Type[])null)
			{
				priority = 800
			};
			HarmonyMethod val2 = new HarmonyMethod(typeof(TailAttributionPatches093T1), postfixName, (Type[])null)
			{
				priority = 0
			};
			harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
			patched++;
		}
		catch (Exception ex)
		{
			missing++;
			Log.Warning("[RimMT] T1 attribution target failed closed: " + text + " -> " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PhasePrefix(ref long __state)
	{
		__state = TailAttribution093T1.BeginPhase();
	}

	public static void TickListPostfix(long __state)
	{
		TailAttribution093T1.EndTickList(__state);
	}

	public static void MapPrePostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPreTick);
	}

	public static void WorldPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldTick);
	}

	public static void StoryWatcherPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.StoryWatcher);
	}

	public static void GameEndPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameEnd);
	}

	public static void StorytellerPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Storyteller);
	}

	public static void TalesPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Tales);
	}

	public static void WorldPostTickPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldPostTick);
	}

	public static void MapPostPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPostTick);
	}

	public static void HistoryPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.History);
	}

	public static void GameComponentsPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameComponents);
	}

	public static void AutosaverPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Autosaver);
	}

	public static void ScenarioPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Scenario);
	}

	public static void DateNotifierPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.DateNotifier);
	}

	public static void LettersPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Letters);
	}

	public static void FilthPostfix(long __state)
	{
		TailAttribution093T1.EndPhase(__state, TailPhase093T1.Filth);
	}
}
