using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT;

internal static class PlayerHumanResidualAttribution093T14
{
	private struct RecentEntry
	{
		internal readonly long Frame;

		internal readonly int GameTick;

		internal readonly int ThingId;

		internal readonly string Job;

		internal readonly long PawnUs;

		internal readonly long OriginalUs;

		internal readonly long JobUs;

		internal readonly long PatherUs;

		internal readonly long ResidualUs;

		internal readonly long TrackedUs;

		internal readonly long UntrackedUs;

		internal readonly long PostfixUs;

		internal readonly string StageText;

		internal RecentEntry(long frame, int gameTick, int thingId, string job, long pawnUs, long originalUs, long jobUs, long patherUs, long residualUs, long trackedUs, long untrackedUs, long postfixUs, long[] stages)
		{
			Frame = frame;
			GameTick = gameTick;
			ThingId = thingId;
			Job = job;
			PawnUs = pawnUs;
			OriginalUs = originalUs;
			JobUs = jobUs;
			PatherUs = patherUs;
			ResidualUs = residualUs;
			TrackedUs = trackedUs;
			UntrackedUs = untrackedUs;
			PostfixUs = postfixUs;
			StringBuilder stringBuilder = new StringBuilder(256);
			if (stages != null)
			{
				for (int i = 0; i < 8; i++)
				{
					if (stages[i] > 0)
					{
						if (stringBuilder.Length != 0)
						{
							stringBuilder.Append('/');
						}
						stringBuilder.Append(StageNames[i]).Append(':').Append(((double)stages[i] / 1000.0).ToString("F2"));
					}
				}
			}
			StageText = ((stringBuilder.Length == 0) ? "none" : stringBuilder.ToString());
		}
	}

	internal const int StageCount = 8;

	private const int RecentCapacity = 24;

	private const int SampleMask = 255;

	private static readonly string[] StageNames = new string[8] { "BaseComps", "Health", "NeedsMind", "Stance", "GearInventory", "AbilityGene", "Social", "OtherTracker" };

	private static readonly long[] StageCalls = new long[8];

	private static readonly long[] StageTotalUs = new long[8];

	private static readonly long[] StageMaxUs = new long[8];

	private static readonly long[] StageOver1 = new long[8];

	private static readonly long[] StageOver5 = new long[8];

	private static readonly int[] ProbeSites = new int[8];

	private static readonly string[] ProbeMethods = new string[8];

	private static readonly RecentEntry[] Recent = new RecentEntry[24];

	[ThreadStatic]
	internal static bool Active;

	[ThreadStatic]
	private static Pawn currentPawn;

	[ThreadStatic]
	private static long pawnStartTicks;

	[ThreadStatic]
	private static long currentOriginalUs;

	[ThreadStatic]
	private static long currentJobUs;

	[ThreadStatic]
	private static long currentPatherUs;

	[ThreadStatic]
	private static long[] stageStartTicks;

	[ThreadStatic]
	private static long[] currentStageUs;

	private static long sampledPawns;

	private static long totalPawnUs;

	private static long totalOriginalUs;

	private static long totalJobUs;

	private static long totalPatherUs;

	private static long totalOriginalResidualUs;

	private static long totalTrackedResidualUs;

	private static long totalUntrackedOriginalResidualUs;

	private static long totalPostfixBeforeT2Us;

	private static long maxOriginalResidualUs;

	private static long maxUntrackedUs;

	private static long maxPostfixUs;

	private static long residualOver1;

	private static long residualOver5;

	private static long residualOver10;

	private static long residualOver20;

	private static long postfixOver1;

	private static long postfixOver5;

	private static long missingOriginalEnd;

	private static long residualClamp;

	private static long untrackedClamp;

	private static long postfixClamp;

	private static int recentPos;

	private static int recentCount;

	private static int retSites;

	private static int skippedEhSites;

	private static bool transpilerSuppressed;

	private static string transpilerReason = string.Empty;

	internal static void BeginPawn(Pawn pawn, bool deepActive, long t2StartTicks)
	{
		ClearCurrent();
		if (!deepActive || t2StartTicks == 0L || !RimMTThreadGuard.IsMainThread || pawn == null)
		{
			return;
		}
		int num = 0;
		try
		{
			if (Find.TickManager != null)
			{
				num = Find.TickManager.TicksGame;
			}
		}
		catch
		{
			return;
		}
		if ((num & 0xFF) != 0)
		{
			return;
		}
		try
		{
			if (((Thing)pawn).def == null || ((Thing)pawn).def.race == null || !((Thing)pawn).def.race.Humanlike || ((Thing)pawn).Faction == null || !((Thing)pawn).Faction.IsPlayer)
			{
				return;
			}
		}
		catch
		{
			return;
		}
		EnsureThreadArrays();
		Array.Clear(stageStartTicks, 0, stageStartTicks.Length);
		Array.Clear(currentStageUs, 0, currentStageUs.Length);
		currentPawn = pawn;
		pawnStartTicks = t2StartTicks;
		currentOriginalUs = 0L;
		currentJobUs = 0L;
		currentPatherUs = 0L;
		Active = true;
	}

	internal static void RecordPhase(PawnTailPhase093T2 phase, long us)
	{
		if (Active && currentPawn != null && us > 0)
		{
			switch (phase)
			{
			case PawnTailPhase093T2.JobTrackerTick:
				currentJobUs += us;
				break;
			case PawnTailPhase093T2.PatherTick:
				currentPatherUs += us;
				break;
			}
		}
	}

	internal static void StartStage(int stage)
	{
		if (Active && stage >= 0 && stage < 8)
		{
			EnsureThreadArrays();
			stageStartTicks[stage] = Stopwatch.GetTimestamp();
		}
	}

	internal static void EndStage(int stage)
	{
		if (!Active || stage < 0 || stage >= 8 || stageStartTicks == null)
		{
			return;
		}
		long num = stageStartTicks[stage];
		stageStartTicks[stage] = 0L;
		if (num == 0L)
		{
			return;
		}
		long num2 = Stopwatch.GetTimestamp() - num;
		if (num2 > 0)
		{
			long num3 = TicksToUs(num2);
			currentStageUs[stage] += num3;
			StageCalls[stage]++;
			StageTotalUs[stage] += num3;
			if (num3 > StageMaxUs[stage])
			{
				StageMaxUs[stage] = num3;
			}
			if (num3 >= 1000)
			{
				StageOver1[stage]++;
			}
			if (num3 >= 5000)
			{
				StageOver5[stage]++;
			}
		}
	}

	internal static void OriginalEnd()
	{
		if (Active && pawnStartTicks != 0L && currentOriginalUs == 0L)
		{
			long num = Stopwatch.GetTimestamp() - pawnStartTicks;
			if (num > 0)
			{
				currentOriginalUs = TicksToUs(num);
			}
		}
	}

	internal static void RecordPawn(Pawn pawn, long pawnUs)
	{
		if (!Active)
		{
			ClearCurrent();
			return;
		}
		Pawn val = currentPawn;
		if (val == null || pawn == null || val != pawn || pawnUs <= 0)
		{
			ClearCurrent();
			return;
		}
		long num = currentOriginalUs;
		if (num <= 0)
		{
			missingOriginalEnd++;
			ClearCurrent();
			return;
		}
		long num2 = num - currentJobUs - currentPatherUs;
		if (num2 < 0)
		{
			residualClamp++;
			num2 = 0L;
		}
		long num3 = 0L;
		for (int i = 0; i < 8; i++)
		{
			num3 += currentStageUs[i];
		}
		long num4 = num2 - num3;
		if (num4 < 0)
		{
			untrackedClamp++;
			num4 = 0L;
		}
		long num5 = pawnUs - num;
		if (num5 < 0)
		{
			postfixClamp++;
			num5 = 0L;
		}
		sampledPawns++;
		totalPawnUs += pawnUs;
		totalOriginalUs += num;
		totalJobUs += currentJobUs;
		totalPatherUs += currentPatherUs;
		totalOriginalResidualUs += num2;
		totalTrackedResidualUs += num3;
		totalUntrackedOriginalResidualUs += num4;
		totalPostfixBeforeT2Us += num5;
		if (num2 > maxOriginalResidualUs)
		{
			maxOriginalResidualUs = num2;
		}
		if (num4 > maxUntrackedUs)
		{
			maxUntrackedUs = num4;
		}
		if (num5 > maxPostfixUs)
		{
			maxPostfixUs = num5;
		}
		if (num2 >= 1000)
		{
			residualOver1++;
		}
		if (num2 >= 5000)
		{
			residualOver5++;
		}
		if (num2 >= 10000)
		{
			residualOver10++;
		}
		if (num2 >= 20000)
		{
			residualOver20++;
		}
		if (num5 >= 1000)
		{
			postfixOver1++;
		}
		if (num5 >= 5000)
		{
			postfixOver5++;
		}
		if (num2 >= 2000 || num5 >= 1000)
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
			string job = "none";
			try
			{
				if (val.CurJobDef != null)
				{
					job = ((Def)val.CurJobDef).defName;
				}
			}
			catch
			{
			}
			Recent[recentPos] = new RecentEntry(RimMTRuntime.MainThreadFrames, gameTick, ((Thing)val).thingIDNumber, job, pawnUs, num, currentJobUs, currentPatherUs, num2, num3, num4, num5, currentStageUs);
			recentPos = (recentPos + 1) % 24;
			if (recentCount < 24)
			{
				recentCount++;
			}
		}
		ClearCurrent();
	}

	internal static void ResetProbeRegistry()
	{
		Array.Clear(ProbeSites, 0, ProbeSites.Length);
		Array.Clear(ProbeMethods, 0, ProbeMethods.Length);
		retSites = 0;
		skippedEhSites = 0;
		transpilerSuppressed = false;
		transpilerReason = string.Empty;
	}

	internal static void RegisterProbe(int stage, string methodName)
	{
		if (stage < 0 || stage >= 8)
		{
			return;
		}
		ProbeSites[stage]++;
		if (!string.IsNullOrEmpty(methodName))
		{
			string text = ProbeMethods[stage];
			if (string.IsNullOrEmpty(text) || text.IndexOf(methodName, StringComparison.Ordinal) < 0)
			{
				ProbeMethods[stage] = (string.IsNullOrEmpty(text) ? methodName : (text + "," + methodName));
			}
		}
	}

	internal static void SetTranspilerShape(int returns, int skippedEh)
	{
		retSites = returns;
		skippedEhSites = skippedEh;
	}

	internal static void SuppressTranspiler(string reason)
	{
		transpilerSuppressed = true;
		transpilerReason = reason ?? "unspecified";
	}

	internal static string Summary()
	{
		double num = ((sampledPawns == 0L) ? 1.0 : ((double)sampledPawns));
		double num2 = ((totalOriginalResidualUs == 0L) ? 0.0 : ((double)totalTrackedResidualUs * 100.0 / (double)totalOriginalResidualUs));
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T14 PlayerHuman residual attribution: sampledPawns=").Append(sampledPawns).Append(", samplePolicy=T2-periodic/4 (gameTick%256==0), avgPawnUs=")
			.Append(((double)totalPawnUs / num).ToString("F1"))
			.Append(", avgOriginalUs=")
			.Append(((double)totalOriginalUs / num).ToString("F1"))
			.Append(", avgJobUs=")
			.Append(((double)totalJobUs / num).ToString("F1"))
			.Append(", avgPatherUs=")
			.Append(((double)totalPatherUs / num).ToString("F1"))
			.Append(", avgOriginalResidualUs=")
			.Append(((double)totalOriginalResidualUs / num).ToString("F1"))
			.Append(", avgTrackedResidualUs=")
			.Append(((double)totalTrackedResidualUs / num).ToString("F1"))
			.Append(", trackedCoverage=")
			.Append(num2.ToString("F1"))
			.Append('%')
			.Append(", avgUntrackedOriginalResidualUs=")
			.Append(((double)totalUntrackedOriginalResidualUs / num).ToString("F1"))
			.Append(", avgPostfixBeforeT2Us=")
			.Append(((double)totalPostfixBeforeT2Us / num).ToString("F1"))
			.Append(", residual>1/5/10/20ms=")
			.Append(residualOver1)
			.Append('/')
			.Append(residualOver5)
			.Append('/')
			.Append(residualOver10)
			.Append('/')
			.Append(residualOver20)
			.Append(", postfix>1/5ms=")
			.Append(postfixOver1)
			.Append('/')
			.Append(postfixOver5)
			.Append(", maxResidualMs=")
			.Append(((double)maxOriginalResidualUs / 1000.0).ToString("F2"))
			.Append(", maxUntrackedMs=")
			.Append(((double)maxUntrackedUs / 1000.0).ToString("F2"))
			.Append(", maxPostfixMs=")
			.Append(((double)maxPostfixUs / 1000.0).ToString("F2"))
			.Append(", missingOriginalEnd=")
			.Append(missingOriginalEnd)
			.Append(", clamps[residual/untracked/postfix]=")
			.Append(residualClamp)
			.Append('/')
			.Append(untrackedClamp)
			.Append('/')
			.Append(postfixClamp)
			.Append(". originalResidual=Pawn.Tick original body - JobTracker - Pather; postfixBeforeT2 is aggregate Harmony/wrapper time before the existing T2 last-priority postfix, not per-owner attribution.");
		return stringBuilder.ToString();
	}

	internal static string StageSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T14 PlayerHuman residual stages: ");
		double num = ((sampledPawns == 0L) ? 1.0 : ((double)sampledPawns));
		for (int i = 0; i < 8; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			long num2 = StageCalls[i];
			stringBuilder.Append(StageNames[i]).Append("[sites=").Append(ProbeSites[i])
				.Append(",calls=")
				.Append(num2)
				.Append(",avgPerPawnUs=")
				.Append(((double)StageTotalUs[i] / num).ToString("F1"))
				.Append(",avgCallUs=")
				.Append((num2 == 0L) ? "0.0" : ((double)StageTotalUs[i] / (double)num2).ToString("F1"))
				.Append(",>1/5ms=")
				.Append(StageOver1[i])
				.Append('/')
				.Append(StageOver5[i])
				.Append(",maxMs=")
				.Append(((double)StageMaxUs[i] / 1000.0).ToString("F2"))
				.Append(']');
		}
		return stringBuilder.ToString();
	}

	internal static string ProbeSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T14 Pawn.Tick transpiler probes: suppressed=").Append(transpilerSuppressed).Append(", retSites=")
			.Append(retSites)
			.Append(", skippedEhSites=")
			.Append(skippedEhSites);
		if (transpilerSuppressed)
		{
			stringBuilder.Append(", reason=").Append(transpilerReason);
		}
		stringBuilder.Append(". ");
		for (int i = 0; i < 8; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			stringBuilder.Append(StageNames[i]).Append('=').Append(ProbeSites[i])
				.Append('{')
				.Append(ProbeMethods[i] ?? string.Empty)
				.Append('}');
		}
		return stringBuilder.ToString();
	}

	internal static string RecentSummary()
	{
		if (recentCount <= 0)
		{
			return "T14 recent PlayerHuman residual/postfix tails: none.";
		}
		StringBuilder stringBuilder = new StringBuilder(8192);
		stringBuilder.Append("T14 recent PlayerHuman residual>=2ms or postfix>=1ms (oldest->newest): ");
		int num = ((recentCount == 24) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append("; ");
			}
			RecentEntry recentEntry = Recent[(num + i) % 24];
			stringBuilder.Append("frame=").Append(recentEntry.Frame).Append(",tick=")
				.Append(recentEntry.GameTick)
				.Append(",Human#")
				.Append(recentEntry.ThingId)
				.Append('[')
				.Append(recentEntry.Job)
				.Append(']')
				.Append(",pawn=")
				.Append(((double)recentEntry.PawnUs / 1000.0).ToString("F2"))
				.Append(",orig=")
				.Append(((double)recentEntry.OriginalUs / 1000.0).ToString("F2"))
				.Append(",job=")
				.Append(((double)recentEntry.JobUs / 1000.0).ToString("F2"))
				.Append(",path=")
				.Append(((double)recentEntry.PatherUs / 1000.0).ToString("F2"))
				.Append(",resid=")
				.Append(((double)recentEntry.ResidualUs / 1000.0).ToString("F2"))
				.Append(",tracked=")
				.Append(((double)recentEntry.TrackedUs / 1000.0).ToString("F2"))
				.Append(",untracked=")
				.Append(((double)recentEntry.UntrackedUs / 1000.0).ToString("F2"))
				.Append(",postfix=")
				.Append(((double)recentEntry.PostfixUs / 1000.0).ToString("F2"))
				.Append(",stages=")
				.Append(recentEntry.StageText);
		}
		return stringBuilder.ToString();
	}

	private static void EnsureThreadArrays()
	{
		if (stageStartTicks == null || stageStartTicks.Length != 8)
		{
			stageStartTicks = new long[8];
		}
		if (currentStageUs == null || currentStageUs.Length != 8)
		{
			currentStageUs = new long[8];
		}
	}

	private static void ClearCurrent()
	{
		Active = false;
		currentPawn = null;
		pawnStartTicks = 0L;
		currentOriginalUs = 0L;
		currentJobUs = 0L;
		currentPatherUs = 0L;
		if (stageStartTicks != null)
		{
			Array.Clear(stageStartTicks, 0, stageStartTicks.Length);
		}
		if (currentStageUs != null)
		{
			Array.Clear(currentStageUs, 0, currentStageUs.Length);
		}
	}

	private static long TicksToUs(long ticks)
	{
		if (ticks > 0)
		{
			return (long)((double)ticks * (1000000.0 / (double)Stopwatch.Frequency));
		}
		return 0L;
	}
}
