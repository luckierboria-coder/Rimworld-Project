using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;

namespace CleanPathfinding;

public static class DoorPathingUtility
{
	public enum DoorType
	{
		Normal = 1,
		Side,
		Emergency,
		Exclusive
	}

	public static Dictionary<int, MapComponent_DoorPathing> compCache = new Dictionary<int, MapComponent_DoorPathing>();

	public static bool usingDoorsExpanded;

	public static IEnumerable<Gizmo> GetGizmos(IEnumerable<Gizmo> values, Building thing)
	{
		foreach (Gizmo value in values)
		{
			yield return value;
		}
		if ((int)((BuildableDef)((Thing)thing).def).passability != 2 && Find.Selector.NumSelected == 1 && compCache.TryGetValue(((Thing)thing).Map.uniqueID, out var doorPathingComp))
		{
			if (!doorPathingComp.doorRegistry.TryGetValue(((Thing)thing).thingIDNumber, out var doorType))
			{
				doorPathingComp.doorRegistry.Add(((Thing)thing).thingIDNumber, DoorType.Normal);
				doorPathingComp.doorCostGrid[((Thing)thing).Map.cellIndices.CellToIndex(((Thing)thing).Position)] = GetDoorCost(DoorType.Normal, thing);
				doorType = DoorType.Normal;
			}
			yield return (Gizmo)new Command_Action
			{
				icon = (Texture)(object)ResourceBank.iconPriority,
				defaultDesc = TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Icon.DoorType.Desc")),
				defaultLabel = TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.Icon." + doorType)),
				action = delegate
				{
					doorPathingComp.SwitchDoorType(thing, doorType);
				}
			};
		}
	}

	public static int GetDoorCost(DoorType doorType, Building door)
	{
		return doorType switch
		{
			DoorType.Normal => 0, 
			DoorType.Side => ModSettings_CleanPathfinding.doorPathingSide, 
			DoorType.Exclusive => -1, 
			_ => ModSettings_CleanPathfinding.doorPathingEmergency, 
		};
	}

	public static void UpdateAllDoorsOnAllMaps()
	{
		//IL_0006: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Expected O, but got Unknown
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Invalid comparison between Unknown and I4
		Dialog_MessageBox val = new Dialog_MessageBox(Translator.Translate("CleanPathfinding.ReloadRequired"), (string)null, (Action)null, (string)null, (Action)null, TaggedString.op_Implicit(Translator.Translate("CleanPathfinding.ReloadHeader")), true, (Action)null, (Action)null, (WindowLayer)1);
		if ((int)Current.ProgramState != 2)
		{
			return;
		}
		foreach (Map map in Find.Maps)
		{
			if (compCache.TryGetValue(map.uniqueID, out var value))
			{
				value.RecalculateAllDoors();
			}
			else if (!Find.WindowStack.IsOpen((Window)(object)val))
			{
				Find.WindowStack.Add((Window)(object)val);
			}
		}
	}

	public static void DrawDoorField(Thing thing)
	{
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0073: Unknown result type (might be due to invalid IL or missing references)
		//IL_0077: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_0093: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		Map map = thing.Map;
		if (map != null && ((!usingDoorsExpanded && thing is Building_Door) || usingDoorsExpanded) && compCache.TryGetValue(map.uniqueID, out var value) && value.doorRegistry.TryGetValue(thing.thingIDNumber, out var value2))
		{
			Color val = (Color)(value2 switch
			{
				DoorType.Side => ResourceBank.yellow, 
				DoorType.Emergency => ResourceBank.red, 
				DoorType.Exclusive => ResourceBank.blue, 
				_ => ResourceBank.white, 
			});
			GenDraw.DrawFieldEdges(new List<IntVec3>((IEnumerable<IntVec3>)(object)GenAdj.OccupiedRect(thing)), val, (float?)null);
		}
	}
}
