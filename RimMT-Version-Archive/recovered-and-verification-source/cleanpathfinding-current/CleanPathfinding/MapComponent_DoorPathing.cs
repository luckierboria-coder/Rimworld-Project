using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.Sound;

namespace CleanPathfinding;

public class MapComponent_DoorPathing : MapComponent
{
	public Dictionary<int, DoorPathingUtility.DoorType> doorRegistry = new Dictionary<int, DoorPathingUtility.DoorType>();

	public int[] doorCostGrid;

	public bool usingAvoidArea = false;

	public Area avoidArea;

	public MapComponent_DoorPathing(Map map)
		: base(map)
	{
		if (!ModSettings_CleanPathfinding.doorPathing)
		{
			map.components.Remove((MapComponent)(object)this);
		}
	}

	public override void ExposeData()
	{
		if (ModSettings_CleanPathfinding.doorPathing)
		{
			Scribe_Collections.Look<int, DoorPathingUtility.DoorType>(ref doorRegistry, "doorRegistry", (LookMode)0, (LookMode)0);
		}
		if (doorRegistry == null)
		{
			doorRegistry = new Dictionary<int, DoorPathingUtility.DoorType>();
		}
	}

	public override void FinalizeInit()
	{
		if (!DoorPathingUtility.compCache.ContainsKey(base.map.uniqueID))
		{
			GenCollection.AddDistinct<int, MapComponent_DoorPathing>(DoorPathingUtility.compCache, base.map.uniqueID, this);
		}
		else
		{
			Log.Warning("[Clean Pathfinding] Tried to register a doorpathing component to a map that already has one. Did the cache not flush?");
		}
		if (doorRegistry == null)
		{
			doorRegistry = new Dictionary<int, DoorPathingUtility.DoorType>();
		}
		doorCostGrid = new int[base.map.info.NumCells];
		RecalculateAllDoors();
		CheckForAvoidArea();
	}

	public void CheckForAvoidArea()
	{
		foreach (Area allArea in base.map.areaManager.AllAreas)
		{
			if (allArea.Label == "Avoid")
			{
				if (ModSettings_CleanPathfinding.logging)
				{
					Log.Message("[Clean Pathfinding] Registering avoid zone.");
				}
				RegisterAvoidArea(allArea, quiet: true);
				break;
			}
		}
	}

	public void RegisterAvoidArea(Area area, bool quiet = false)
	{
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		usingAvoidArea = true;
		avoidArea = area;
		if (!quiet)
		{
			Messages.Message(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.NewAvoidArea")), MessageTypeDefOf.PositiveEvent, false);
		}
		for (int i = 0; i < avoidArea.innerGrid.arr.Length; i++)
		{
			if (avoidArea.innerGrid.arr[i] && doorCostGrid[i] == 0)
			{
				doorCostGrid[i] = 45;
			}
		}
	}

	public void DeregisterAvoidArea(Area area)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		usingAvoidArea = false;
		avoidArea = null;
		Messages.Message(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.DeletedAvoidArea")), MessageTypeDefOf.PositiveEvent, false);
		Array.Clear(doorCostGrid, 0, base.map.info.NumCells);
		RecalculateAllDoors();
	}

	public void UpdateAvoidArea(IntVec3 c, bool val)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		int num = base.map.cellIndices.CellToIndex(c);
		if (val)
		{
			if (doorCostGrid[num] == 0)
			{
				doorCostGrid[num] = 45;
			}
			return;
		}
		Building edifice = GridsUtility.GetEdifice(c, base.map);
		if (edifice != null && doorRegistry.TryGetValue(((Thing)edifice).thingIDNumber, out var value))
		{
			doorCostGrid[num] = DoorPathingUtility.GetDoorCost(value, edifice);
		}
		else
		{
			doorCostGrid[num] = 0;
		}
	}

	public void RecalculateAllDoors()
	{
		try
		{
			List<Building> allBuildingsColonist = base.map.listerBuildings.allBuildingsColonist;
			int count = allBuildingsColonist.Count;
			for (int i = 0; i < count; i++)
			{
				Building val = allBuildingsColonist[i];
				if (doorRegistry.ContainsKey(((Thing)val).thingIDNumber))
				{
					WriteToDoorGrid(val, doorRegistry[((Thing)val).thingIDNumber]);
				}
			}
		}
		catch (Exception ex)
		{
			Log.Error("[Clean Pathfinding] Error processing door recalculation, skipping...\n" + ex);
		}
	}

	public void SwitchDoorType(Building thing, DoorPathingUtility.DoorType doorType)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_001b: Unknown result type (might be due to invalid IL or missing references)
		if (ValidateRoomDoors(((Thing)thing).Position) == 1)
		{
			Messages.Message(TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.InvalidDoor")), MessageTypeDefOf.RejectInput, false);
			return;
		}
		SoundStarter.PlayOneShotOnCamera(SoundDefOf.Click, (Map)null);
		doorType = ((doorType == DoorPathingUtility.DoorType.Exclusive) ? DoorPathingUtility.DoorType.Normal : (++doorType));
		doorRegistry[((Thing)thing).thingIDNumber] = doorType;
		WriteToDoorGrid(thing, doorType);
	}

	private void WriteToDoorGrid(Building thing, DoorPathingUtility.DoorType doorType)
	{
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0041: Unknown result type (might be due to invalid IL or missing references)
		CellRect val = GenAdj.OccupiedRect((Thing)(object)thing);
		foreach (IntVec3 cell in ((CellRect)(ref val)).Cells)
		{
			if (GenGrid.InBounds(cell, base.map))
			{
				doorCostGrid[base.map.cellIndices.CellToIndex(cell)] = DoorPathingUtility.GetDoorCost(doorType, thing);
			}
		}
	}

	public unsafe int ValidateRoomDoors(IntVec3 c, bool roomUpdate = false)
	{
		//IL_0356: Unknown result type (might be due to invalid IL or missing references)
		//IL_0357: Unknown result type (might be due to invalid IL or missing references)
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_0113: Unknown result type (might be due to invalid IL or missing references)
		//IL_0173: Unknown result type (might be due to invalid IL or missing references)
		//IL_0179: Invalid comparison between Unknown and I4
		//IL_0190: Unknown result type (might be due to invalid IL or missing references)
		//IL_0196: Invalid comparison between Unknown and I4
		//IL_01cc: Unknown result type (might be due to invalid IL or missing references)
		try
		{
			if (roomUpdate)
			{
				Region region = GridsUtility.GetRegion(c, base.map, (RegionType)14);
				if (region?.door == null)
				{
					return 0;
				}
				c = ((Thing)region.door).positionInt;
			}
			if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
			{
				base.map.debugDrawer.FlashCell(c, 0f, "REF", 50);
			}
			Region region2 = GridsUtility.GetRegion(c, base.map, (RegionType)14);
			if (region2 == null)
			{
				return 0;
			}
			List<Building> list = new List<Building>();
			foreach (RegionLink link in region2.links)
			{
				Region otherRegion = link.GetOtherRegion(region2);
				Room val = ((otherRegion != null) ? otherRegion.Room : null);
				if (val == null || val.TouchesMapEdge)
				{
					continue;
				}
				if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
				{
					base.map.debugDrawer.FlashCell(val.FirstRegion.AnyCell, 0f, "\nROOM", 50);
				}
				foreach (Region region3 in val.Regions)
				{
					foreach (RegionLink link2 in region3.links)
					{
						Region otherRegion2 = link2.GetOtherRegion(region3);
						if ((int)otherRegion2.type == 4 && otherRegion2.IsDoorway && (int)((BuildableDef)((Thing)otherRegion2.door).def).passability != 2)
						{
							if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
							{
								base.map.debugDrawer.FlashCell(((Thing)otherRegion2.door).positionInt, 0f, "\n\nDOOR", 50);
							}
							if (!list.Contains((Building)(object)otherRegion2.door))
							{
								list.Add((Building)(object)otherRegion2.door);
							}
						}
					}
				}
			}
			if (ModSettings_CleanPathfinding.logging && Prefs.DevMode)
			{
				Log.Message("[Clean Pathfinding] Doors in new room layout: " + list.Count);
			}
			if (list.Count > 0)
			{
				foreach (Building item in list)
				{
					if (!doorRegistry.ContainsKey(((Thing)item).thingIDNumber))
					{
						doorRegistry.Add(((Thing)item).thingIDNumber, DoorPathingUtility.DoorType.Normal);
					}
					if (list.Count == 1)
					{
						doorRegistry[((Thing)item).thingIDNumber] = DoorPathingUtility.DoorType.Exclusive;
						WriteToDoorGrid(item, DoorPathingUtility.DoorType.Exclusive);
					}
				}
			}
			return list.Count;
		}
		catch (Exception ex)
		{
			string[] obj = new string[6] { "[Clean Pathfinding] Could not validate doors at ", null, null, null, null, null };
			IntVec3 val2 = c;
			obj[1] = ((object)(*(IntVec3*)(&val2))/*cast due to .constrained prefix*/).ToString();
			obj[2] = " (update: ";
			obj[3] = roomUpdate.ToString();
			obj[4] = ") for some-odd reason: ";
			obj[5] = ex?.ToString();
			Log.Warning(string.Concat(obj));
			return 0;
		}
	}
}
