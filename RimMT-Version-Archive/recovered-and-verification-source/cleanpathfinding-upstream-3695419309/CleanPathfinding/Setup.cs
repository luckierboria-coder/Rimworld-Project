using System.Collections.Generic;
using Verse;

namespace CleanPathfinding;

[StaticConstructorOnStartup]
public static class Setup
{
	public static bool safetyNeeded;

	static Setup()
	{
		safetyNeeded = true;
		List<string> list = new List<string>();
		List<TerrainDef> allDefsListForReading = DefDatabase<TerrainDef>.AllDefsListForReading;
		int count = allDefsListForReading.Count;
		while (count-- > 0)
		{
			TerrainDef val = allDefsListForReading[count];
			if (val.destroyEffectWater != null)
			{
				if (val.tags == null)
				{
					val.tags = new List<string>();
				}
				val.tags.Add("CleanPath");
			}
			bool flag = val.tags?.Contains("CleanPath") ?? false;
			if (val.generatedFilth != null || flag || (val.generatedFilth == null && ((Def)val).defName.Contains("_Rough")))
			{
				CleanPathfindingUtility.terrainCacheOriginalValues.Add(((Def)val).shortHash, val.extraNonDraftedPerceivedPathCost);
				CleanPathfindingUtility.terrainCache.Add(((Def)val).shortHash, val.extraNonDraftedPerceivedPathCost);
				if (flag)
				{
					list.Add(((Def)val).label);
				}
			}
		}
		SafetyCheck();
		CleanPathfindingUtility.UpdatePathCosts();
	}

	private static void SafetyCheck()
	{
		List<ModContentPack> runningModsListForReading = LoadedModManager.RunningModsListForReading;
		int count = runningModsListForReading.Count;
		while (count-- > 0)
		{
			string packageIdPlayerFacingInt = runningModsListForReading[count].packageIdPlayerFacingInt;
			switch (packageIdPlayerFacingInt)
			{
			case "Haplo.Miscellaneous.Robots":
				Print(packageIdPlayerFacingInt);
				return;
			case "BiomesTeam.BiomesIslands":
				Print(packageIdPlayerFacingInt);
				return;
			case "RH2.Faction.VOID":
				Print(packageIdPlayerFacingInt);
				return;
			}
		}
		safetyNeeded = false;
		static void Print(string mod)
		{
			Log.Warning("[Clean Pathfinding] the mod " + mod + " is partially incompatible. The 'road attraction' calculations will be skipped.");
		}
	}
}
