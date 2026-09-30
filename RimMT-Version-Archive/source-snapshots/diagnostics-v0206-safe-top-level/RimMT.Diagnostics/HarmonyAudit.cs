using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics;

internal static class HarmonyAudit
{
	internal static string BuildSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(16384);
		AuditOne(stringBuilder, AccessTools.Method(typeof(Root_Play), "Update", (Type[])null, (Type[])null), "Root_Play.Update");
		AuditOne(stringBuilder, AccessTools.Method(typeof(TickManager), "DoSingleTick", (Type[])null, (Type[])null), "TickManager.DoSingleTick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Pawn), "Tick", (Type[])null, (Type[])null), "Pawn.Tick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick", (Type[])null, (Type[])null), "Pawn_JobTracker.JobTrackerTick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob", (Type[])null, (Type[])null), "Pawn_JobTracker.DetermineNextJob");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick", (Type[])null, (Type[])null), "Pawn_PathFollower.PatherTick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell", (Type[])null, (Type[])null), "Pawn_PathFollower.TryEnterNextPathCell");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Map), "MapPostTick", (Type[])null, (Type[])null), "Map.MapPostTick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(World), "WorldTick", (Type[])null, (Type[])null), "World.WorldTick");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Storyteller), "StorytellerTick", (Type[])null, (Type[])null), "Storyteller.StorytellerTick");
		AuditNamed(stringBuilder, typeof(Reachability), "CanReach", "Reachability.CanReach");
		AuditNamed(stringBuilder, typeof(GenClosest), "ClosestThingReachable", "GenClosest.ClosestThingReachable");
		AuditNamed(stringBuilder, typeof(GenClosest), "ClosestThing_Global", "GenClosest.ClosestThing_Global");
		Type type = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
		if (type != null)
		{
			AuditOne(stringBuilder, AccessTools.Method(type, "PotentialWorkThingsGlobal", (Type[])null, (Type[])null), "PickUpAndHaul.WorkGiver_HaulToInventory.PotentialWorkThingsGlobal");
			AuditOne(stringBuilder, AccessTools.Method(type, "HasJobOnThing", (Type[])null, (Type[])null), "PickUpAndHaul.WorkGiver_HaulToInventory.HasJobOnThing");
		}
		stringBuilder.AppendLine("-- HaulMerge authority chain --");
		AuditOne(stringBuilder, AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing", new Type[3]
		{
			typeof(Pawn),
			typeof(Thing),
			typeof(bool)
		}, (Type[])null), "WorkGiver_Merge.JobOnThing");
		AuditOne(stringBuilder, AccessTools.Method(typeof(Thing), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null), "Thing.CanStackWith");
		AuditOne(stringBuilder, AccessTools.Method(typeof(ThingWithComps), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null), "ThingWithComps.CanStackWith");
		AuditOne(stringBuilder, AccessTools.Method(typeof(MinifiedThing), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null), "MinifiedThing.CanStackWith");
		return stringBuilder.ToString();
	}

	private static void AuditNamed(StringBuilder sb, Type type, string name, string label)
	{
		List<MethodInfo> declaredMethods;
		try
		{
			declaredMethods = AccessTools.GetDeclaredMethods(type);
		}
		catch
		{
			return;
		}
		int num = 0;
		for (int i = 0; i < declaredMethods.Count; i++)
		{
			MethodInfo methodInfo = declaredMethods[i];
			if (!(methodInfo == null) && !(methodInfo.Name != name))
			{
				AuditOne(sb, methodInfo, label + "#" + num);
				num++;
			}
		}
	}

	private static void AuditOne(StringBuilder sb, MethodBase method, string label)
	{
		if (method == null)
		{
			sb.AppendLine(label + ": missing");
			return;
		}
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo == null)
			{
				sb.AppendLine(label + ": no patches");
				return;
			}
			List<string> list = new List<string>();
			Add(list, "P", patchInfo.Prefixes);
			Add(list, "Q", patchInfo.Postfixes);
			Add(list, "T", patchInfo.Transpilers);
			Add(list, "F", patchInfo.Finalizers);
			sb.AppendLine(label + ": " + ((list.Count == 0) ? "no patches" : string.Join(" | ", list.ToArray())));
		}
		catch (Exception ex)
		{
			sb.AppendLine(label + ": audit failed " + ex.GetType().Name);
		}
	}

	private static void Add(List<string> rows, string kind, IEnumerable<Patch> patches)
	{
		if (patches == null)
		{
			return;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null)
			{
				string text = ((patch.PatchMethod == null) ? "<null>" : (patch.PatchMethod.DeclaringType.FullName + "." + patch.PatchMethod.Name));
				string[] obj = new string[8] { kind, "[", patch.owner, ",pri=", null, null, null, null };
				int priority = patch.priority;
				obj[4] = priority.ToString();
				obj[5] = ",";
				obj[6] = text;
				obj[7] = "]";
				rows.Add(string.Concat(obj));
			}
		}
	}
}
