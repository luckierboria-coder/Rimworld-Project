using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DubsBadHygiene;

internal class ClosestSanitation
{
	public static bool Occupied(Building_AssignableFixture fixture, Pawn checker)
	{
		if (!fixture.OccupancyCheck)
		{
			return false;
		}
		Room room = RegionAndRoomQuery.GetRoom((Thing)(object)fixture, (RegionType)15);
		if (room == null)
		{
			return false;
		}
		Map map = ((Thing)fixture).Map;
		return GenCollection.Any<Thing>(room.ContainedAndAdjacentThings, (Predicate<Thing>)delegate(Thing x)
		{
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			Pawn val = (Pawn)(object)((x is Pawn) ? x : null);
			return (val != null && val != checker) || map.reservationManager.IsReservedByAnyoneOf(LocalTargetInfo.op_Implicit(x), ((Thing)checker).Faction);
		});
	}

	public static bool UsableNow(Thing x, Pawn pawn, bool Urgent, float range)
	{
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		if (FireUtility.IsBurning(x))
		{
			return false;
		}
		if (!ReservationUtility.CanReserve(pawn, LocalTargetInfo.op_Implicit(x), 2, -1, (ReservationLayerDef)null, false))
		{
			return false;
		}
		if (!Urgent && IntVec3Utility.DistanceTo(x.Position, ((Thing)pawn).Position) > range)
		{
			return false;
		}
		if (!Urgent && x is Building_AssignableFixture fixture && Occupied(fixture, pawn))
		{
			return false;
		}
		return true;
	}

	public static bool IsEverUsable(Thing x, Pawn fetcher, Pawn user, bool Urgent, FixtureType[] Fixtures, bool CaresAboutContamination = true)
	{
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_0047: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		if (!Urgent && !user.IsPrisonerOfColony && !SocialProperness.IsSociallyProper(x, user, false, false))
		{
			return false;
		}
		if (x is Building_AssignableFixture building_AssignableFixture)
		{
			if (!FixtureIsOfTypes((Thing)(object)building_AssignableFixture, Fixtures))
			{
				return false;
			}
			AcceptanceReport val = building_AssignableFixture.Working();
			if (!((AcceptanceReport)(ref val)).Accepted)
			{
				return false;
			}
			val = building_AssignableFixture.PawnAllowed(fetcher);
			if (!((AcceptanceReport)(ref val)).Accepted)
			{
				return false;
			}
			if ((!Urgent & CaresAboutContamination) && building_AssignableFixture.pipe != null && building_AssignableFixture.pipe.pipeNet.IsNetContaminated())
			{
				return false;
			}
		}
		if (ForbidUtility.IsForbidden(x, fetcher))
		{
			return false;
		}
		if (!ReachabilityUtility.CanReach(fetcher, LocalTargetInfo.op_Implicit(x), (PathEndMode)2, (Danger)3, false, false, (TraverseMode)0))
		{
			return false;
		}
		return true;
	}

	public static LocalTargetInfo FindBestToilet(Pawn pawn, bool Urgent, float shortRange = 30f)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_0069: Unknown result type (might be due to invalid IL or missing references)
		//IL_007a: Unknown result type (might be due to invalid IL or missing references)
		//IL_009e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004b: Unknown result type (might be due to invalid IL or missing references)
		List<Thing> list = (from x in SanitationUtil.AllFixtures(((Thing)pawn).Map)
			where x is Building_BaseToilet && IsEverUsable(x, pawn, pawn, Urgent, new FixtureType[1] { FixtureType.Toilet }, CaresAboutContamination: false)
			select x).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)list))
		{
			return LocalTargetInfo.op_Implicit((Thing)null);
		}
		return LocalTargetInfo.op_Implicit(GenClosest.ClosestThingReachable(((Thing)pawn).Position, ((Thing)pawn).Map, ThingRequest.ForGroup((ThingRequestGroup)10), (PathEndMode)1, TraverseParms.For(pawn, (Danger)3, (TraverseMode)0, false, false, false), shortRange, (Predicate<Thing>)Validator, (IEnumerable<Thing>)list, 0, 10, true, (RegionType)14, false));
		bool Validator(Thing x)
		{
			if (x is Building_BaseToilet && IsEverUsable(x, pawn, pawn, Urgent, null, CaresAboutContamination: false))
			{
				return UsableNow(x, pawn, Urgent, shortRange);
			}
			return false;
		}
	}

	public static bool FixtureIsOfTypes(Thing x, FixtureType[] Fixtures)
	{
		if (GenList.NullOrEmpty<FixtureType>((IList<FixtureType>)Fixtures))
		{
			return true;
		}
		if (x is Building_AssignableFixture building_AssignableFixture && Fixtures.Contains(building_AssignableFixture.fixture))
		{
			return true;
		}
		return false;
	}

	public static LocalTargetInfo FindBestHygieneSource(Pawn pawn, bool Urgent, float MaxRange = 100f)
	{
		//IL_0078: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0158: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00cc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0131: Unknown result type (might be due to invalid IL or missing references)
		//IL_017d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0184: Unknown result type (might be due to invalid IL or missing references)
		//IL_0189: Unknown result type (might be due to invalid IL or missing references)
		//IL_018e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01e9: Unknown result type (might be due to invalid IL or missing references)
		Map map = ((Thing)pawn).Map;
		FixtureType[] types = new FixtureType[2]
		{
			FixtureType.Basin,
			FixtureType.Bath
		};
		List<Thing> list = (from x in SanitationUtil.AllFixtures(map)
			where IsEverUsable(x, pawn, pawn, Urgent, types, Urgent)
			select x).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)list))
		{
			return LocalTargetInfo.op_Implicit(SanitationUtil.TryFindWaterCell(pawn, 9999, MaxRange, Urgent));
		}
		List<Thing> list2 = list.Where(delegate(Thing x)
		{
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			//IL_001f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0024: Unknown result type (might be due to invalid IL or missing references)
			//IL_0029: Unknown result type (might be due to invalid IL or missing references)
			if (x is Building_AssignableFixture { fixture: FixtureType.Bath })
			{
				IntVec3 val6 = ((Thing)pawn).Position - x.Position;
				return (float)((IntVec3)(ref val6)).LengthManhattan < MaxRange;
			}
			return false;
		}).ToList();
		IntVec3 val3;
		if (GenCollection.Any<Thing>(list2))
		{
			Thing val = null;
			float num = float.MinValue;
			for (int num2 = 0; num2 < list2.Count; num2++)
			{
				Thing val2 = list2[num2];
				val3 = ((Thing)pawn).Position - val2.Position;
				float num3 = ((IntVec3)(ref val3)).LengthManhattan;
				if (num3 <= MaxRange)
				{
					float num4 = prio(val2, num3);
					if (num4 >= num && UsableNow(val2, pawn, Urgent, 9999f))
					{
						val = val2;
						num = num4;
					}
				}
			}
			return LocalTargetInfo.op_Implicit(val);
		}
		list = list.Where((Thing x) => UsableNow(x, pawn, Urgent, MaxRange)).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)list))
		{
			return LocalTargetInfo.op_Implicit((Thing)null);
		}
		Thing val4 = null;
		float num5 = float.MinValue;
		for (int num6 = 0; num6 < list.Count; num6++)
		{
			Thing val5 = list[num6];
			val3 = ((Thing)pawn).Position - val5.Position;
			float num7 = ((IntVec3)(ref val3)).LengthManhattan;
			if (num7 <= MaxRange)
			{
				float num8 = prio(val5, num7);
				if (num8 >= num5 && UsableNow(val5, pawn, Urgent, 9999f))
				{
					val4 = val5;
					num5 = num8;
				}
			}
		}
		return LocalTargetInfo.op_Implicit(val4);
		static float prio(Thing x, float distance)
		{
			float num9 = 300f;
			num9 -= distance;
			if (x is Building_AssignableFixture building_AssignableFixture)
			{
				if (building_AssignableFixture.fixture == FixtureType.Bath)
				{
					if (building_AssignableFixture.Quality == FixtureQuality.luxury)
					{
						num9 += 90f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.fine)
					{
						num9 += 80f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.normal)
					{
						num9 += 70f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.shabby)
					{
						num9 += 60f;
					}
				}
				else if (building_AssignableFixture.fixture == FixtureType.Basin)
				{
					if (building_AssignableFixture.Quality == FixtureQuality.luxury)
					{
						num9 += 70f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.fine)
					{
						num9 += 60f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.normal)
					{
						num9 += 50f;
					}
					else if (building_AssignableFixture.Quality == FixtureQuality.shabby)
					{
						num9 += 40f;
					}
				}
			}
			else if (((Def)x.def).HasModExtension<WaterExt>())
			{
				num9 += 30f;
			}
			return num9;
		}
	}

	public static LocalTargetInfo FindBestDrinkAnimal(Pawn pawn, bool Urgent, float range = 30f, int regions = 30)
	{
		//IL_006f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0080: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		Map map = ((Thing)pawn).Map;
		FixtureType[] types = new FixtureType[2]
		{
			FixtureType.Basin,
			FixtureType.Toilet
		};
		List<Thing> list = (from x in SanitationUtil.AllFixtures(map)
			where (x is Building_washbucket || x is Building_BaseToilet) && IsEverUsable(x, pawn, pawn, Urgent, types)
			select x).ToList();
		Thing val = null;
		float num = float.MinValue;
		for (int num2 = 0; num2 < list.Count; num2++)
		{
			Thing val2 = list[num2];
			IntVec3 val3 = ((Thing)pawn).Position - val2.Position;
			if ((float)((IntVec3)(ref val3)).LengthManhattan <= range)
			{
				float num3 = prio(val2);
				if (num3 >= num && UsableNow(val2, pawn, Urgent, 9999f))
				{
					val = val2;
					num = num3;
				}
			}
		}
		if (val != null)
		{
			return LocalTargetInfo.op_Implicit(val);
		}
		if (Urgent)
		{
			range = 9999f;
			regions = 9999;
		}
		return LocalTargetInfo.op_Implicit(SanitationUtil.TryFindWaterCell(pawn, regions, range, allowOcean: false, Urgent));
		static float prio(Thing x)
		{
			if (x is Building_AssignableFixture building_AssignableFixture)
			{
				if (building_AssignableFixture.fixture == FixtureType.Toilet)
				{
					return 5f;
				}
				if (building_AssignableFixture.fixture == FixtureType.Basin)
				{
					return 9f;
				}
			}
			return 1f;
		}
	}

	public static LocalTargetInfo FindBestDrink(Pawn pawn, Pawn user, bool Urgent, float range = 30f, int regions = 30)
	{
		//IL_0048: Unknown result type (might be due to invalid IL or missing references)
		//IL_01b8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d5: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_0180: Unknown result type (might be due to invalid IL or missing references)
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		if (pawn.RaceProps.Animal)
		{
			return FindBestDrinkAnimal(pawn, Urgent, range, regions);
		}
		Map map = ((Thing)pawn).Map;
		FixtureType[] types = new FixtureType[2]
		{
			FixtureType.Basin,
			FixtureType.Bath
		};
		List<Thing> first;
		if (DubsBadHygieneMod.Settings.AllowModdedDrinks)
		{
			first = (from x in SanitationUtil.AllFixtures(((Thing)pawn).Map)
				where IsEverUsable(x, pawn, user, Urgent, types)
				select x).ToList();
			IEnumerable<Thing> second = from x in WaterExt.Drinks.Where(val).SelectMany((ThingDef v) => map.listerThings.ThingsOfDef(v))
				where IsEverUsable(x, pawn, user, Urgent, types)
				select x;
			first = first.Concat(second).ToList();
		}
		else
		{
			first = (from x in SanitationUtil.AllFixtures(((Thing)pawn).Map)
				where IsEverUsable(x, pawn, user, Urgent, types)
				select x).ToList();
			IEnumerable<Thing> second2 = from x in map.listerThings.ThingsOfDef(DubDef.DBH_WaterBottle)
				where IsEverUsable(x, pawn, user, Urgent, types)
				select x;
			first = first.Concat(second2).ToList();
		}
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)first))
		{
			if (Urgent)
			{
				range = 9999f;
				regions = 9999;
			}
			return LocalTargetInfo.op_Implicit(SanitationUtil.TryFindWaterCell(pawn, regions, range, allowOcean: false, Urgent));
		}
		first = first.Where((Thing x) => UsableNow(x, pawn, Urgent, range)).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)first))
		{
			return LocalTargetInfo.op_Implicit((Thing)null);
		}
		return LocalTargetInfo.op_Implicit(GenClosest.ClosestThing_Global_Reachable(((Thing)pawn).Position, ((Thing)pawn).Map, (IEnumerable<Thing>)first, (PathEndMode)2, TraverseParms.For(pawn, (Danger)3, (TraverseMode)0, false, false, false), range, (Predicate<Thing>)Validator, (Func<Thing, float>)prio));
		bool Validator(Thing x)
		{
			return UsableNow(x, pawn, Urgent, range);
		}
		static float prio(Thing x)
		{
			if (((Def)x.def).HasModExtension<WaterExt>())
			{
				return 9f;
			}
			if (x is Building_AssignableFixture building_AssignableFixture)
			{
				if (building_AssignableFixture.fixture == FixtureType.Bath)
				{
					return 5f;
				}
				if (building_AssignableFixture.fixture == FixtureType.Basin)
				{
					return building_AssignableFixture.Quality switch
					{
						FixtureQuality.luxury => 9f, 
						FixtureQuality.fine => 9f, 
						FixtureQuality.normal => 9f, 
						FixtureQuality.shabby => 6f, 
						_ => 9f, 
					};
				}
			}
			return 1f;
		}
		static bool val(ThingDef x)
		{
			return ((Def)x).GetModExtension<WaterExt>()?.SeekForThirst ?? false;
		}
	}

	public static LocalTargetInfo FindBestCleanWaterSource(Pawn pawn, Pawn user, bool Urgent, float range = 30f, ThingDef exempt = null, Pawn targetPawn = null)
	{
		//IL_0085: Unknown result type (might be due to invalid IL or missing references)
		//IL_008a: Unknown result type (might be due to invalid IL or missing references)
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00da: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		FixtureType[] types = new FixtureType[2]
		{
			FixtureType.Basin,
			FixtureType.Bath
		};
		List<Thing> list = (from x in SanitationUtil.AllFixtures(((Thing)pawn).Map)
			where x.def != exempt && IsEverUsable(x, pawn, user, Urgent, types)
			select x).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)list))
		{
			return LocalTargetInfo.op_Implicit(SanitationUtil.TryFindWaterCell(pawn, 9999, range, allowOcean: false, Urgent));
		}
		list = list.Where((Thing x) => UsableNow(x, pawn, Urgent, range)).ToList();
		if (GenList.NullOrEmpty<Thing>((IList<Thing>)list))
		{
			return LocalTargetInfo.op_Implicit((Thing)null);
		}
		return LocalTargetInfo.op_Implicit(GenClosest.ClosestThing_Global_Reachable(((Thing)pawn).Position, ((Thing)pawn).Map, (IEnumerable<Thing>)list, (PathEndMode)2, TraverseParms.For(pawn, (Danger)3, (TraverseMode)0, false, false, false), range, (Predicate<Thing>)Validator, (Func<Thing, float>)prio));
		bool Validator(Thing x)
		{
			return UsableNow(x, pawn, Urgent, range);
		}
		static float prio(Thing x)
		{
			if (x is Building_AssignableFixture building_AssignableFixture)
			{
				if (building_AssignableFixture.fixture == FixtureType.Bath)
				{
					return 5f;
				}
				if (building_AssignableFixture.fixture == FixtureType.Basin)
				{
					return building_AssignableFixture.Quality switch
					{
						FixtureQuality.luxury => 9f, 
						FixtureQuality.fine => 9f, 
						FixtureQuality.normal => 9f, 
						FixtureQuality.shabby => 6f, 
						_ => 6f, 
					};
				}
			}
			return 1f;
		}
	}

	public unsafe static Thing ClosestThingReachablePrioritized(IntVec3 root, Map map, ThingRequest thingReq, PathEndMode peMode, TraverseParms traverseParams, float maxDistance = 9999f, Predicate<Thing> validator = null, Func<Thing, float> priorityGetter = null, IEnumerable<Thing> customGlobalSearchSet = null, int searchRegionsMin = 0, int searchRegionsMax = -1, bool forceAllowGlobalSearch = false, RegionType traversableRegionTypes = (RegionType)14, bool ignoreEntirelyForbiddenRegions = false)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		//IL_0016: Unknown result type (might be due to invalid IL or missing references)
		//IL_001c: Unknown result type (might be due to invalid IL or missing references)
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0098: Unknown result type (might be due to invalid IL or missing references)
		//IL_0117: Unknown result type (might be due to invalid IL or missing references)
		//IL_011b: Invalid comparison between Unknown and I4
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f5: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_013d: Unknown result type (might be due to invalid IL or missing references)
		bool flag = (searchRegionsMax < 0) | forceAllowGlobalSearch;
		if (!flag && customGlobalSearchSet != null)
		{
			Log.ErrorOnce("searchRegionsMax >= 0 && customGlobalSearchSet != null && !forceAllowGlobalSearch. customGlobalSearchSet will never be used.", 634984);
		}
		if (!flag && !((ThingRequest)(ref thingReq)).IsUndefined && !((ThingRequest)(ref thingReq)).CanBeFoundInRegion)
		{
			Log.ErrorOnce("ClosestThingReachable with thing request group " + ((object)(*(ThingRequestGroup*)(&thingReq.group))/*cast due to constrained. prefix*/).ToString() + " and global search not allowed. This will never find anything because this group is never stored in regions. Either allow global search or don't call this method at all.", 518498981);
			return null;
		}
		if (GenClosest.EarlyOutSearch(root, map, thingReq, customGlobalSearchSet, validator))
		{
			return null;
		}
		Thing val = null;
		bool flag2 = false;
		if (!((ThingRequest)(ref thingReq)).IsUndefined && ((ThingRequest)(ref thingReq)).CanBeFoundInRegion)
		{
			int num = ((searchRegionsMax > 0) ? searchRegionsMax : 30);
			int num2 = default(int);
			val = GenClosest.RegionwiseBFSWorker(root, map, thingReq, peMode, traverseParams, validator, (Func<Thing, float>)null, searchRegionsMin, num, maxDistance, ref num2, traversableRegionTypes, ignoreEntirelyForbiddenRegions);
			flag2 = val == null && num2 < num;
		}
		if (((val == null) & flag) && !flag2)
		{
			if ((int)traversableRegionTypes != 14)
			{
				Log.ErrorOnce("ClosestThingReachable had to do a global search, but traversableRegionTypes is not set to passable only. It's not supported, because Reachability is based on passable regions only.", 14384767);
			}
			IEnumerable<Thing> enumerable = customGlobalSearchSet ?? map.listerThings.ThingsMatching(thingReq);
			val = GenClosest.ClosestThing_Global(root, (IEnumerable)enumerable, maxDistance, (Predicate<Thing>)Validator, priorityGetter);
		}
		return val;
		bool Validator(Thing t)
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			if (map.reachability.CanReach(root, LocalTargetInfo.op_Implicit(t), peMode, traverseParams))
			{
				if (validator != null)
				{
					return validator(t);
				}
				return true;
			}
			return false;
		}
	}
}
