using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class StorytellerCatastrophic093T15
{
	private struct Entry
	{
		internal readonly long Frame;

		internal readonly int Tick;

		internal readonly long Us;

		internal Entry(long frame, int tick, long us)
		{
			Frame = frame;
			Tick = tick;
			Us = us;
		}
	}

	private const long ThresholdUs = 100000L;

	private const int RecentCapacity = 12;

	private static readonly Entry[] Recent = new Entry[12];

	private static long events;

	private static long maxUs;

	private static int recentPos;

	private static int recentCount;

	internal static void Observe(long us)
	{
		if (us < 100000 || !RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		events++;
		if (us > maxUs)
		{
			maxUs = us;
		}
		StorytellerDeepAttribution093T18.ObserveCatastrophic(us);
		int tick = -1;
		try
		{
			if (Find.TickManager != null)
			{
				tick = Find.TickManager.TicksGame;
			}
		}
		catch
		{
		}
		Recent[recentPos] = new Entry(RimMTRuntime.MainThreadFrames, tick, us);
		recentPos = (recentPos + 1) % 12;
		if (recentCount < 12)
		{
			recentCount++;
		}
	}

	internal static string Summary()
	{
		StringBuilder stringBuilder = new StringBuilder(2048);
		stringBuilder.Append("T15 Storyteller catastrophic tails >=100ms: events=").Append(events).Append(", maxMs=")
			.Append(((double)maxUs / 1000.0).ToString("F2"));
		if (recentCount == 0)
		{
			return stringBuilder.Append(", recent=none. T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.").ToString();
		}
		stringBuilder.Append(", recent(oldest->newest)=");
		int num = ((recentCount == 12) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append(';');
			}
			Entry entry = Recent[(num + i) % 12];
			stringBuilder.Append("frame=").Append(entry.Frame).Append(",tick=")
				.Append(entry.Tick)
				.Append(",ms=")
				.Append(((double)entry.Us / 1000.0).ToString("F2"));
		}
		stringBuilder.Append(". T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.");
		return stringBuilder.ToString();
	}

	internal static string HarmonyCensus()
	{
		MethodBase methodBase = AccessTools.Method(typeof(Storyteller), "StorytellerTick", (Type[])null, (Type[])null);
		if (methodBase == null)
		{
			return "T15 Storyteller Harmony census: target missing.";
		}
		Patches patchInfo = Harmony.GetPatchInfo(methodBase);
		if (patchInfo == null)
		{
			return "T15 Storyteller Harmony census: no patches.";
		}
		StringBuilder stringBuilder = new StringBuilder(3072);
		int foreign = 0;
		Append(stringBuilder, "Prefix", patchInfo.Prefixes, ref foreign);
		Append(stringBuilder, "Postfix", patchInfo.Postfixes, ref foreign);
		Append(stringBuilder, "Transpiler", patchInfo.Transpilers, ref foreign);
		Append(stringBuilder, "Finalizer", patchInfo.Finalizers, ref foreign);
		stringBuilder.Insert(0, "T15 Storyteller Harmony census: foreignPatches=" + foreign + ". ");
		stringBuilder.Append(" Census is on-demand only; T15 does not wrap foreign Storyteller patches.");
		return stringBuilder.ToString();
	}

	private static void Append(StringBuilder sb, string kind, IEnumerable<Patch> patches, ref int foreign)
	{
		if (patches == null)
		{
			return;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null)
			{
				bool flag = patch.owner != null && patch.owner.StartsWith("allen.rimmt", StringComparison.Ordinal);
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
}
