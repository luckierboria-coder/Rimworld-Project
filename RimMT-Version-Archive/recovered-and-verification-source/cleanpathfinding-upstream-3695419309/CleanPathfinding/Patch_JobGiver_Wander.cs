using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace CleanPathfinding;

[HarmonyPatch(typeof(JobGiver_Wander), "TryGiveJob")]
internal static class Patch_JobGiver_Wander
{
	private static bool Prepare()
	{
		if (!Mod_CleanPathfinding.patchLedger.ContainsKey("Patch_JobGiver_Wander"))
		{
			Mod_CleanPathfinding.patchLedger.Add("Patch_JobGiver_Wander", ModSettings_CleanPathfinding.wanderTuning);
		}
		return ModSettings_CleanPathfinding.wanderTuning;
	}

	public static void Postfix(Job __result)
	{
		if (((Def)__result?.def).shortHash == ((Def)JobDefOf.Wait_Wander).shortHash)
		{
			__result.expiryInterval += ModSettings_CleanPathfinding.wanderDelay;
		}
	}
}
