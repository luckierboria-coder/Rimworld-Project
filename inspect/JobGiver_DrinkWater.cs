using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace DubsBadHygiene;

public class JobGiver_DrinkWater : ThinkNode_JobGiver
{
	public virtual float MinLevel => 0.3f;

	public override float GetPriority(Pawn pawn)
	{
		Pawn_NeedsTracker needs = pawn.needs;
		Need_Thirst need_Thirst = ((needs != null) ? needs.TryGetNeed<Need_Thirst>() : null);
		if (need_Thirst == null)
		{
			return 0f;
		}
		if (FoodUtility.ShouldBeFedBySomeone(pawn))
		{
			return 0f;
		}
		if (((Need)need_Thirst).CurLevel > MinLevel)
		{
			return 0f;
		}
		return 9.6f;
	}

	public override Job TryGiveJob(Pawn pawn)
	{
		//IL_0057: Unknown result type (might be due to invalid IL or missing references)
		//IL_005c: Unknown result type (might be due to invalid IL or missing references)
		//IL_005d: Unknown result type (might be due to invalid IL or missing references)
		//IL_005f: Unknown result type (might be due to invalid IL or missing references)
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_00aa: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0172: Unknown result type (might be due to invalid IL or missing references)
		//IL_0177: Unknown result type (might be due to invalid IL or missing references)
		//IL_0182: Unknown result type (might be due to invalid IL or missing references)
		//IL_0183: Unknown result type (might be due to invalid IL or missing references)
		//IL_0159: Unknown result type (might be due to invalid IL or missing references)
		//IL_0164: Unknown result type (might be due to invalid IL or missing references)
		//IL_0165: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		Pawn_NeedsTracker needs = pawn.needs;
		Need_Thirst need_Thirst = ((needs != null) ? needs.TryGetNeed<Need_Thirst>() : null);
		if (need_Thirst == null)
		{
			return null;
		}
		if (((ThinkNode)this).GetPriority(pawn) <= 0f)
		{
			return null;
		}
		LocalTargetInfo val2 = LocalTargetInfo.op_Implicit(((IEnumerable<Thing>)pawn.inventory.innerContainer).FirstOrDefault((Thing x) => val(x.def)));
		if (val2 != LocalTargetInfo.op_Implicit((Thing)null))
		{
			Job obj = JobMaker.MakeJob(JobDefOf.Ingest, val2);
			obj.count = 1;
			return obj;
		}
		LocomotionUrgency locomotionUrgency = (LocomotionUrgency)3;
		bool urgent = false;
		if (((Need)need_Thirst).CurLevel <= 0f || pawn.health.hediffSet.HasHediff(DubDef.DBHDehydration, true))
		{
			urgent = true;
			locomotionUrgency = (LocomotionUrgency)4;
		}
		float range = 20f;
		if (((Need)need_Thirst).CurLevel <= 0.3f)
		{
			range = 30f;
		}
		if (((Need)need_Thirst).CurLevel <= 0.2f)
		{
			range = 40f;
		}
		if (((Need)need_Thirst).CurLevel <= 0.1f)
		{
			range = 9999f;
		}
		val2 = ClosestSanitation.FindBestDrink(pawn, pawn, urgent, range, 300);
		if (val2 != LocalTargetInfo.op_Implicit((Thing)null))
		{
			if (((LocalTargetInfo)(ref val2)).HasThing)
			{
				if (((Def)((LocalTargetInfo)(ref val2)).Thing.def).HasModExtension<WaterExt>())
				{
					Job obj2 = JobMaker.MakeJob(JobDefOf.Ingest, LocalTargetInfo.op_Implicit(((LocalTargetInfo)(ref val2)).Thing));
					obj2.count = 1;
					obj2.locomotionUrgency = locomotionUrgency;
					return obj2;
				}
				Job obj3 = JobMaker.MakeJob(DubDef.DBHDrinkFromBasin, LocalTargetInfo.op_Implicit(((LocalTargetInfo)(ref val2)).Thing));
				obj3.locomotionUrgency = locomotionUrgency;
				return obj3;
			}
			Job obj4 = JobMaker.MakeJob(DubDef.DBHDrinkFromGround, LocalTargetInfo.op_Implicit(((LocalTargetInfo)(ref val2)).Cell));
			obj4.locomotionUrgency = locomotionUrgency;
			return obj4;
		}
		return null;
		static bool val(ThingDef x)
		{
			return ((Def)x).GetModExtension<WaterExt>()?.SeekForThirst ?? false;
		}
	}
}
