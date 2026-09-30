using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using UnityEngine;
using Verse;

namespace CleanPathfinding;

public static class CleanPathfindingUtility
{
	public static Dictionary<ushort, int> terrainCache = new Dictionary<ushort, int>();

	public static Dictionary<ushort, int> terrainCacheOriginalValues = new Dictionary<ushort, int>();

	public static SimpleCurve Custom_DistanceCurve;

	public static MapComponent_DoorPathing cachedComp;

	private static bool lastFactionHostileCache;

	private static bool lastPawnReversionCache;

	public static int cachedMapID = -1;

	public static int lastFactionID = -1;

	private static int loggedOnTick;

	private static int calls;

	private static int lastTerrainCacheCost;

	private static int lastPawnID;

	private static ushort lastTerrainDefID;

	public static void UpdatePathCosts()
	{
		//IL_027d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0282: Unknown result type (might be due to invalid IL or missing references)
		//IL_0294: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a0: Unknown result type (might be due to invalid IL or missing references)
		//IL_02b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c5: Expected O, but got Unknown
		//IL_02c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02cb: Invalid comparison between Unknown and I4
		try
		{
			List<string> list = new List<string>();
			foreach (ushort item in terrainCache.Keys.ToList())
			{
				if (terrainCacheOriginalValues.TryGetValue(item, out var value))
				{
					terrainCache[item] = value;
				}
				else
				{
					terrainCache[item] = 0;
				}
			}
			List<TerrainDef> allDefsListForReading = DefDatabase<TerrainDef>.AllDefsListForReading;
			int count = allDefsListForReading.Count;
			for (int i = 0; i < count; i++)
			{
				TerrainDef val = allDefsListForReading[i];
				ushort shortHash = ((Def)val).shortHash;
				if (!terrainCache.ContainsKey(shortHash))
				{
					continue;
				}
				val.extraNonDraftedPerceivedPathCost = terrainCacheOriginalValues[shortHash];
				if (!Setup.safetyNeeded && ModSettings_CleanPathfinding.roadBias > 0)
				{
					List<string> tags = val.tags;
					if (tags != null && tags.Contains("CleanPath"))
					{
						val.extraNonDraftedPerceivedPathCost -= ModSettings_CleanPathfinding.roadBias;
						terrainCache[shortHash] += ModSettings_CleanPathfinding.roadBias;
						goto IL_01f4;
					}
				}
				if (ModSettings_CleanPathfinding.bias != 0 && val.generatedFilth != null)
				{
					val.extraNonDraftedPerceivedPathCost += ModSettings_CleanPathfinding.bias;
					terrainCache[shortHash] -= ModSettings_CleanPathfinding.bias;
				}
				if (ModSettings_CleanPathfinding.naturalBias > 0 && val.generatedFilth == null && ((Def)val).defName.Contains("_Rough"))
				{
					val.extraNonDraftedPerceivedPathCost += ModSettings_CleanPathfinding.naturalBias;
					terrainCache[shortHash] -= ModSettings_CleanPathfinding.naturalBias;
				}
				goto IL_01f4;
				IL_01f4:
				if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
				{
					list.Add(((Def)val).defName + ": " + val.extraNonDraftedPerceivedPathCost);
				}
			}
			if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
			{
				list.Sort();
				Log.Message("[Clean Pathfinding] Terrain report:\n" + string.Join("\n - ", list));
			}
			SimpleCurve val2 = new SimpleCurve();
			val2.Add(new CurvePoint(40f + (float)ModSettings_CleanPathfinding.heuristicAdjuster, 1f), true);
			val2.Add(new CurvePoint(120f + (float)(ModSettings_CleanPathfinding.heuristicAdjuster * 3), 3f), true);
			Custom_DistanceCurve = val2;
			if ((int)Current.ProgramState != 2)
			{
				return;
			}
			foreach (Map map in Find.Maps)
			{
				map.pathing.RecalculateAllPerceivedPathCosts();
			}
		}
		catch (Exception ex)
		{
			Log.Error("[Clean Pathfinding] Error processing settings, skipping...\n" + ex);
		}
	}

	public static float AdjustCosts(Pawn pawn, TerrainDef def, float cost, Map map, int index)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_009a: Invalid comparison between Unknown and I4
		//IL_02bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_02c4: Unknown result type (might be due to invalid IL or missing references)
		//IL_02f7: Unknown result type (might be due to invalid IL or missing references)
		if (pawn != null)
		{
			bool flag = lastPawnReversionCache;
			if (((Thing)pawn).thingIDNumber != lastPawnID)
			{
				lastPawnID = ((Thing)pawn).thingIDNumber;
				Faction faction = ((Thing)pawn).Faction;
				flag = (lastPawnReversionCache = faction == null || (int)((Thing)pawn).def.race.intelligence == 0 || (!faction.def.isPlayer && IsHostileFast(faction)) || (ModSettings_CleanPathfinding.factorCarryingPawn && pawn.carryTracker != null && pawn.carryTracker.CarriedThing != null && (int)pawn.carryTracker.CarriedThing.def.category == 1) || (ModSettings_CleanPathfinding.factorBleeding && pawn.health.hediffSet.cachedBleedRate > 0.1f));
			}
			if (!flag)
			{
				if (ModSettings_CleanPathfinding.doorPathing)
				{
					int num = 0;
					if (cachedMapID == map.uniqueID)
					{
						num = cachedComp.doorCostGrid[index];
					}
					else if (DoorPathingUtility.compCache.TryGetValue(map.uniqueID, out cachedComp))
					{
						cachedMapID = map.uniqueID;
						num = cachedComp.doorCostGrid[index];
					}
					if (num < 0)
					{
						goto IL_030d;
					}
					cost += (float)num;
				}
				if (ModSettings_CleanPathfinding.factorLight && GameGlowAtFast(map, index) < 0.3f)
				{
					cost += (float)ModSettings_CleanPathfinding.darknessPenalty;
				}
			}
			else if (((Def)def).shortHash == lastTerrainDefID)
			{
				cost += (float)lastTerrainCacheCost;
			}
			else
			{
				lastTerrainDefID = ((Def)def).shortHash;
				if (terrainCache.TryGetValue(((Def)def).shortHash, out lastTerrainCacheCost))
				{
					if (lastTerrainCacheCost > 0 && ((Thing)pawn).factionInt != null && ((Thing)pawn).factionInt.def.isPlayer)
					{
						lastTerrainCacheCost = 0;
					}
					cost += (float)lastTerrainCacheCost;
				}
				else
				{
					lastTerrainCacheCost = 0;
				}
			}
			if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
			{
				calls++;
				if (Current.gameInt.tickManager.ticksGameInt != loggedOnTick)
				{
					loggedOnTick = Current.gameInt.tickManager.ticksGameInt;
					if (calls != 0)
					{
						Log.Message("[Clean Pathfinding] Calls last pathfinding: " + calls);
					}
					calls = 0;
				}
				if (cost < 0f)
				{
					cost = 0f;
				}
				IntVec3 cell = map.cellIndices.IndexToCell(index);
				if (!GenCollection.Any<DebugCell>(map.debugDrawer.debugCells, (Predicate<DebugCell>)((DebugCell x) => x.c == cell)))
				{
					map.debugDrawer.FlashCell(cell, cost, cost.ToString(), 50);
				}
			}
		}
		goto IL_030d;
		IL_030d:
		if (cost < 0f)
		{
			return 0f;
		}
		return cost;
		static float GameGlowAtFast(Map val, int num3)
		{
			//IL_0045: Unknown result type (might be due to invalid IL or missing references)
			//IL_004a: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0063: Unknown result type (might be due to invalid IL or missing references)
			//IL_0069: Unknown result type (might be due to invalid IL or missing references)
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			float num2 = 0f;
			if (val.roofGrid.roofGrid[num3] == null)
			{
				num2 = val.skyManager.curSkyGlowInt;
				if (num2 == 1f)
				{
					return 1f;
				}
			}
			Color32 val2 = val.glowGrid.VisualGlowAt(num3);
			if (val2.a == 1)
			{
				return 1f;
			}
			return (float)(val2.r + val2.g + val2.b) * 0.004705882f;
		}
		static bool IsHostileFast(Faction val)
		{
			//IL_0089: Unknown result type (might be due to invalid IL or missing references)
			//IL_008f: Invalid comparison between Unknown and I4
			if (Current.gameInt.tickManager.ticksGameInt % 600 == 0)
			{
				lastFactionID = -1;
			}
			if (val.loadID == lastFactionID)
			{
				return lastFactionHostileCache;
			}
			lastFactionID = val.loadID;
			List<FactionRelation> relations = val.relations;
			int count = relations.Count;
			while (count-- > 0)
			{
				FactionRelation val2 = relations[count];
				if (val2.other == Current.gameInt.worldInt.factionManager.ofPlayer)
				{
					lastFactionHostileCache = (int)val2.kind == 0;
					break;
				}
			}
			return lastFactionHostileCache;
		}
	}
}
