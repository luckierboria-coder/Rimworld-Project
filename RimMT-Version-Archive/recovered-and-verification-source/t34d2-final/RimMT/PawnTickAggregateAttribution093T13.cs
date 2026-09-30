using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class PawnTickAggregateAttribution093T13
{
	private struct SlowPawn
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly int ThingId;

		internal readonly string PawnDef;

		internal readonly string JobDef;

		internal readonly int Category;

		internal readonly long TotalUs;

		internal readonly long JobUs;

		internal readonly long DetermineUs;

		internal readonly long OverrideUs;

		internal readonly long PatherUs;

		internal readonly long ResidualUs;

		internal SlowPawn(long frame, int gameTick, int thingId, string pawnDef, string jobDef, int category, long totalUs, long jobUs, long determineUs, long overrideUs, long patherUs, long residualUs)
		{
			Frame = frame;
			GameTick = gameTick;
			ThingId = thingId;
			PawnDef = pawnDef;
			JobDef = jobDef;
			Category = category;
			TotalUs = totalUs;
			JobUs = jobUs;
			DetermineUs = determineUs;
			OverrideUs = overrideUs;
			PatherUs = patherUs;
			ResidualUs = residualUs;
		}
	}

	private const int CategoryCount = 7;

	private const int RecentCapacity = 24;

	private static readonly string[] CategoryNames = new string[7] { "PlayerHumanlike", "OtherHumanlike", "PlayerAnimal", "OtherAnimal", "PlayerMech", "OtherMech", "Other" };

	private static readonly long[] CategoryCalls = new long[7];

	private static readonly long[] CategoryTotalUs = new long[7];

	private static readonly long[] CategoryJobUs = new long[7];

	private static readonly long[] CategoryPatherUs = new long[7];

	private static readonly long[] CategoryResidualUs = new long[7];

	private static readonly long[] CategoryMaxUs = new long[7];

	private static readonly SlowPawn[] Recent = new SlowPawn[24];

	[ThreadStatic]
	private static Pawn currentPawn;

	[ThreadStatic]
	private static long currentJobTrackerUs;

	[ThreadStatic]
	private static long currentDetermineUs;

	[ThreadStatic]
	private static long currentOverrideUs;

	[ThreadStatic]
	private static long currentPatherUs;

	private static long sampledPawns;

	private static long totalPawnUs;

	private static long totalJobTrackerUs;

	private static long totalDetermineUs;

	private static long totalOverrideUs;

	private static long totalPatherUs;

	private static long totalResidualUs;

	private static long maxPawnUs;

	private static long maxResidualUs;

	private static long over1;

	private static long over5;

	private static long over10;

	private static long over20;

	private static long residualOver1;

	private static long residualOver5;

	private static long residualOver10;

	private static long residualOver20;

	private static long negativeResidualClamp;

	private static int recentPos;

	private static int recentCount;

	internal static void BeginPawn(Pawn pawn, bool deepActive)
	{
		if (!deepActive || !RimMTThreadGuard.IsMainThread)
		{
			currentPawn = null;
			currentJobTrackerUs = (currentDetermineUs = (currentOverrideUs = (currentPatherUs = 0L)));
			return;
		}
		currentPawn = pawn;
		currentJobTrackerUs = 0L;
		currentDetermineUs = 0L;
		currentOverrideUs = 0L;
		currentPatherUs = 0L;
	}

	internal static void RecordPhase(PawnTailPhase093T2 phase, long us)
	{
		if (currentPawn != null && us > 0)
		{
			switch (phase)
			{
			case PawnTailPhase093T2.JobTrackerTick:
				currentJobTrackerUs += us;
				break;
			case PawnTailPhase093T2.DetermineNextJob:
				currentDetermineUs += us;
				break;
			case PawnTailPhase093T2.CheckForJobOverride:
				currentOverrideUs += us;
				break;
			case PawnTailPhase093T2.PatherTick:
				currentPatherUs += us;
				break;
			}
		}
	}

	internal static void RecordPawn(Pawn pawn, long pawnUs)
	{
		Pawn val = currentPawn ?? pawn;
		if (val == null || pawnUs <= 0)
		{
			ClearCurrent();
			return;
		}
		long num = currentJobTrackerUs;
		long num2 = currentPatherUs;
		long num3 = pawnUs - num - num2;
		if (num3 < 0)
		{
			negativeResidualClamp++;
			num3 = 0L;
		}
		sampledPawns++;
		totalPawnUs += pawnUs;
		totalJobTrackerUs += num;
		totalDetermineUs += currentDetermineUs;
		totalOverrideUs += currentOverrideUs;
		totalPatherUs += num2;
		totalResidualUs += num3;
		if (pawnUs > maxPawnUs)
		{
			maxPawnUs = pawnUs;
		}
		if (num3 > maxResidualUs)
		{
			maxResidualUs = num3;
		}
		if (pawnUs >= 1000)
		{
			over1++;
		}
		if (pawnUs >= 5000)
		{
			over5++;
		}
		if (pawnUs >= 10000)
		{
			over10++;
		}
		if (pawnUs >= 20000)
		{
			over20++;
		}
		if (num3 >= 1000)
		{
			residualOver1++;
		}
		if (num3 >= 5000)
		{
			residualOver5++;
		}
		if (num3 >= 10000)
		{
			residualOver10++;
		}
		if (num3 >= 20000)
		{
			residualOver20++;
		}
		int num4 = CategoryOf(val);
		CategoryCalls[num4]++;
		CategoryTotalUs[num4] += pawnUs;
		CategoryJobUs[num4] += num;
		CategoryPatherUs[num4] += num2;
		CategoryResidualUs[num4] += num3;
		if (pawnUs > CategoryMaxUs[num4])
		{
			CategoryMaxUs[num4] = pawnUs;
		}
		if (pawnUs >= 5000)
		{
			Recent[recentPos] = MakeSlowPawn(val, pawnUs, num, currentDetermineUs, currentOverrideUs, num2, num3, num4);
			recentPos = (recentPos + 1) % 24;
			if (recentCount < 24)
			{
				recentCount++;
			}
		}
		ClearCurrent();
	}

	private static void ClearCurrent()
	{
		currentPawn = null;
		currentJobTrackerUs = (currentDetermineUs = (currentOverrideUs = (currentPatherUs = 0L)));
	}

	private static int CategoryOf(Pawn pawn)
	{
		try
		{
			if (pawn == null || ((Thing)pawn).def == null || ((Thing)pawn).def.race == null)
			{
				return 6;
			}
			bool flag = ((Thing)pawn).Faction != null && ((Thing)pawn).Faction.IsPlayer;
			if (((Thing)pawn).def.race.IsMechanoid)
			{
				return flag ? 4 : 5;
			}
			if (((Thing)pawn).def.race.Humanlike)
			{
				return (!flag) ? 1 : 0;
			}
			return flag ? 2 : 3;
		}
		catch
		{
			return 6;
		}
	}

	internal static string Summary()
	{
		double num = ((sampledPawns == 0L) ? 0.0 : ((double)totalPawnUs / (double)sampledPawns));
		double num2 = ((sampledPawns == 0L) ? 0.0 : ((double)totalJobTrackerUs / (double)sampledPawns));
		double num3 = ((sampledPawns == 0L) ? 0.0 : ((double)totalDetermineUs / (double)sampledPawns));
		double num4 = ((sampledPawns == 0L) ? 0.0 : ((double)totalOverrideUs / (double)sampledPawns));
		double num5 = ((sampledPawns == 0L) ? 0.0 : ((double)totalPatherUs / (double)sampledPawns));
		double num6 = ((sampledPawns == 0L) ? 0.0 : ((double)totalResidualUs / (double)sampledPawns));
		double num7 = ((totalPawnUs == 0L) ? 0.0 : ((double)totalJobTrackerUs * 100.0 / (double)totalPawnUs));
		double num8 = ((totalPawnUs == 0L) ? 0.0 : ((double)totalPatherUs * 100.0 / (double)totalPawnUs));
		double num9 = ((totalPawnUs == 0L) ? 0.0 : ((double)totalResidualUs * 100.0 / (double)totalPawnUs));
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T13 PawnTick aggregate attribution: sampledPawns=").Append(sampledPawns).Append(", avgPawnUs=")
			.Append(num.ToString("F1"))
			.Append(", avgJobTrackerUs=")
			.Append(num2.ToString("F1"))
			.Append(", avgDetermineUs[nested]=")
			.Append(num3.ToString("F1"))
			.Append(", avgOverrideUs[nested]=")
			.Append(num4.ToString("F1"))
			.Append(", avgPatherUs=")
			.Append(num5.ToString("F1"))
			.Append(", avgResidualUs=")
			.Append(num6.ToString("F1"))
			.Append(", shares[job/pather/residual]=")
			.Append(num7.ToString("F1"))
			.Append('%')
			.Append('/')
			.Append(num8.ToString("F1"))
			.Append('%')
			.Append('/')
			.Append(num9.ToString("F1"))
			.Append('%')
			.Append(", pawn>1/5/10/20ms=")
			.Append(over1)
			.Append('/')
			.Append(over5)
			.Append('/')
			.Append(over10)
			.Append('/')
			.Append(over20)
			.Append(", residual>1/5/10/20ms=")
			.Append(residualOver1)
			.Append('/')
			.Append(residualOver5)
			.Append('/')
			.Append(residualOver10)
			.Append('/')
			.Append(residualOver20)
			.Append(", maxPawnMs=")
			.Append(((double)maxPawnUs / 1000.0).ToString("F2"))
			.Append(", maxResidualMs=")
			.Append(((double)maxResidualUs / 1000.0).ToString("F2"))
			.Append(", negativeResidualClamp=")
			.Append(negativeResidualClamp)
			.Append(". JobTracker and Pather are direct-child inclusive totals; Determine/Override are nested evidence and are not subtracted again.");
		return stringBuilder.ToString();
	}

	internal static string CategorySummary()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T13 PawnTick categories: ");
		for (int i = 0; i < 7; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			long num = CategoryCalls[i];
			double num2 = ((num == 0L) ? 0.0 : ((double)CategoryTotalUs[i] / (double)num));
			double num3 = ((num == 0L) ? 0.0 : ((double)CategoryJobUs[i] / (double)num));
			double num4 = ((num == 0L) ? 0.0 : ((double)CategoryPatherUs[i] / (double)num));
			double num5 = ((num == 0L) ? 0.0 : ((double)CategoryResidualUs[i] / (double)num));
			double num6 = ((totalPawnUs == 0L) ? 0.0 : ((double)CategoryTotalUs[i] * 100.0 / (double)totalPawnUs));
			stringBuilder.Append(CategoryNames[i]).Append("[calls=").Append(num)
				.Append(",avgUs=")
				.Append(num2.ToString("F1"))
				.Append(",job=")
				.Append(num3.ToString("F1"))
				.Append(",pather=")
				.Append(num4.ToString("F1"))
				.Append(",residual=")
				.Append(num5.ToString("F1"))
				.Append(",cpuShare=")
				.Append(num6.ToString("F1"))
				.Append('%')
				.Append(",maxMs=")
				.Append(((double)CategoryMaxUs[i] / 1000.0).ToString("F2"))
				.Append(']');
		}
		return stringBuilder.ToString();
	}

	internal static string RecentSummary()
	{
		if (recentCount <= 0)
		{
			return "T13 recent sampled Pawn.Tick >=5ms: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(8192);
		stringBuilder.Append("T13 recent sampled Pawn.Tick >=5ms (oldest->newest): ");
		int num = ((recentCount == 24) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			SlowPawn slowPawn = Recent[(num + i) % 24];
			stringBuilder.Append("frame=").Append(slowPawn.Frame).Append(",tick=")
				.Append(slowPawn.GameTick)
				.Append(",pawn=")
				.Append(slowPawn.PawnDef)
				.Append('#')
				.Append(slowPawn.ThingId)
				.Append('[')
				.Append(slowPawn.JobDef)
				.Append(']')
				.Append(",cat=")
				.Append(CategoryNames[slowPawn.Category])
				.Append(",total=")
				.Append(((double)slowPawn.TotalUs / 1000.0).ToString("F2"))
				.Append(",job=")
				.Append(((double)slowPawn.JobUs / 1000.0).ToString("F2"))
				.Append(",det=")
				.Append(((double)slowPawn.DetermineUs / 1000.0).ToString("F2"))
				.Append(",ovr=")
				.Append(((double)slowPawn.OverrideUs / 1000.0).ToString("F2"))
				.Append(",path=")
				.Append(((double)slowPawn.PatherUs / 1000.0).ToString("F2"))
				.Append(",residual=")
				.Append(((double)slowPawn.ResidualUs / 1000.0).ToString("F2"));
		}
		return stringBuilder.ToString();
	}

	internal static string PawnTickHarmonyCensus()
	{
		MethodBase methodBase = AccessTools.Method(typeof(Pawn), "Tick", (Type[])null, (Type[])null);
		if (methodBase == null)
		{
			return "T13 Pawn.Tick Harmony census: target missing.";
		}
		Patches patchInfo = Harmony.GetPatchInfo(methodBase);
		if (patchInfo == null)
		{
			return "T13 Pawn.Tick Harmony census: no patches.";
		}
		StringBuilder stringBuilder = new StringBuilder(4096);
		int foreign = 0;
		AppendPatchList(stringBuilder, "Prefix", patchInfo.Prefixes, ref foreign);
		AppendPatchList(stringBuilder, "Postfix", patchInfo.Postfixes, ref foreign);
		AppendPatchList(stringBuilder, "Transpiler", patchInfo.Transpilers, ref foreign);
		AppendPatchList(stringBuilder, "Finalizer", patchInfo.Finalizers, ref foreign);
		stringBuilder.Insert(0, "T13 Pawn.Tick Harmony census: foreignPatches=" + foreign + ". ");
		stringBuilder.Append(" Census is on-demand only; T13 does not wrap foreign patch methods with timers.");
		return stringBuilder.ToString();
	}

	private static void AppendPatchList(StringBuilder sb, string kind, IEnumerable<Patch> patches, ref int foreign)
	{
		if (patches == null)
		{
			return;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null)
			{
				bool flag = string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal);
				if (!flag)
				{
					foreign++;
				}
				MethodInfo patchMethod = patch.PatchMethod;
				sb.Append(kind).Append("[owner=").Append(patch.owner ?? "<null>")
					.Append(",priority=")
					.Append(patch.priority)
					.Append(",method=")
					.Append((patchMethod == null) ? "<null>" : (patchMethod.DeclaringType.FullName + "." + patchMethod.Name))
					.Append(flag ? ",RimMT] " : ",FOREIGN] ");
			}
		}
	}

	private static SlowPawn MakeSlowPawn(Pawn pawn, long totalUs, long jobUs, long determineUs, long overrideUs, long patherUs, long residualUs, int category)
	{
		int gameTick = -1;
		try
		{
			if (Find.TickManager != null)
			{
				gameTick = Find.TickManager.TicksGame;
			}
		}
		catch
		{
		}
		string pawnDef = ((((Thing)pawn).def == null) ? "Pawn" : ((Def)((Thing)pawn).def).defName);
		string jobDef = "none";
		try
		{
			if (pawn.CurJobDef != null)
			{
				jobDef = ((Def)pawn.CurJobDef).defName;
			}
		}
		catch
		{
		}
		return new SlowPawn(RimMTRuntime.MainThreadFrames, gameTick, ((Thing)pawn).thingIDNumber, pawnDef, jobDef, category, totalUs, jobUs, determineUs, overrideUs, patherUs, residualUs);
	}
}
