using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class ReachProfileSafety0418
{
	private static long positiveProfileResultsForcedToVanilla;

	private static long immediateTruePreserved;

	private static long immediateProbeFailures;

	private static long patchFailures;

	internal static void Apply(Harmony harmony)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0052: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(AggressiveReachabilityProfiles), "Prefix", (Type[])null, (Type[])null);
			if (methodBase == null)
			{
				Interlocked.Increment(ref patchFailures);
				Log.Warning("[RimMT] V0.4.18 reach-profile positive-authority guard unavailable: AggressiveReachabilityProfiles.Prefix not found.");
				return;
			}
			HarmonyMethod val = new HarmonyMethod(typeof(ReachProfileSafety0418), "PrefixPostfix", (Type[])null);
			val.priority = 0;
			harmony.Patch(methodBase, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] V0.4.18 reach-profile safety guard active: profile Unreachable may remain authoritative; non-immediate profile Reachable is forced through live Vanilla CanReach confirmation.");
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref patchFailures);
			Log.Warning("[RimMT] V0.4.18 reach-profile positive-authority guard patch failed. Existing V0.4.16 parity fuse remains active. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PrefixPostfix(IntVec3 __0, LocalTargetInfo __1, PathEndMode __2, TraverseParms __3, Map __4, ref bool __6, ref bool __result)
	{
		//IL_000d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0021: Unknown result type (might be due to invalid IL or missing references)
		//IL_0022: Unknown result type (might be due to invalid IL or missing references)
		//IL_0025: Unknown result type (might be due to invalid IL or missing references)
		if (__result || !__6)
		{
			return;
		}
		try
		{
			Pawn pawn = __3.pawn;
			if (__4 != null && !__4.Disposed && ReachabilityImmediate.CanReachImmediate(__0, __1, __4, __2, pawn))
			{
				Interlocked.Increment(ref immediateTruePreserved);
				return;
			}
		}
		catch
		{
			Interlocked.Increment(ref immediateProbeFailures);
		}
		__result = true;
		Interlocked.Increment(ref positiveProfileResultsForcedToVanilla);
	}

	internal static string Summary()
	{
		return "Reach-profile V0.4.18 positive guard: forcedVanillaPositive=" + Interlocked.Read(ref positiveProfileResultsForcedToVanilla) + ", immediateTruePreserved=" + Interlocked.Read(ref immediateTruePreserved) + ", immediateProbeFailures=" + Interlocked.Read(ref immediateProbeFailures) + ", patchFailures=" + Interlocked.Read(ref patchFailures) + ". Policy: profile false may short-circuit; profile true requires live Vanilla confirmation.";
	}
}
