using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics;

internal static class DiagnosticsV03
{
	private sealed class DetermineContext
	{
		internal long Started;

		internal Pawn Pawn;

		internal bool CaptureDetails;

		internal Dictionary<string, MethodAccum> Methods;
	}

	private struct MethodAccum
	{
		internal long Calls;

		internal long TotalUs;

		internal long MaxUs;
	}

	private struct SlowDetermineRecord
	{
		internal readonly int Tick;

		internal readonly string Pawn;

		internal readonly long TotalUs;

		internal readonly string ResultJob;

		internal readonly string Source;

		internal readonly string TopMethods;

		internal SlowDetermineRecord(int tick, string pawn, long totalUs, string resultJob, string source, string topMethods)
		{
			Tick = tick;
			Pawn = pawn;
			TotalUs = totalUs;
			ResultJob = resultJob;
			Source = source;
			TopMethods = topMethods;
		}
	}

	private const long SlowDetermineUs = 20000L;

	private const int RecentCapacity = 32;

	private const int MaxMethodsPerDetermine = 256;

	private const int TopMethodsPerBurst = 16;

	private const int PostSlowDetailPackages = 24;

	private static readonly FieldInfo JobTrackerPawnField = AccessTools.Field(typeof(Pawn_JobTracker), "pawn");

	private static readonly SlowDetermineRecord[] Recent = new SlowDetermineRecord[32];

	private static readonly List<SlowDetermineRecord> Worst = new List<SlowDetermineRecord>(16);

	[ThreadStatic]
	private static DetermineContext current;

	[ThreadStatic]
	private static int detailPackagesRemaining;

	private static int recentPos;

	private static int recentCount;

	private static long determines;

	private static long slowDetermines;

	private static long detailedDetermines;

	private static long slowDetailed;

	private static long slowUndetailed;

	private static long workGiverCallsInsideDetermine;

	private static long workGiverUsInsideDetermine;

	private static long workGiverPatched;

	private static long patchFailures;

	private static long contextReentry;

	private static long failures;

	internal static bool InDetermine => current != null;

	internal static void Apply(Harmony harmony)
	{
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_0148: Expected O, but got Unknown
		//IL_0148: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
			for (int i = 0; i < assemblies.Length; i++)
			{
				Type[] types;
				try
				{
					types = assemblies[i].GetTypes();
				}
				catch (ReflectionTypeLoadException ex)
				{
					types = ex.Types;
				}
				catch
				{
					continue;
				}
				if (types == null)
				{
					continue;
				}
				foreach (Type type in types)
				{
					if (type == null || type.IsAbstract || !typeof(WorkGiver_Scanner).IsAssignableFrom(type))
					{
						continue;
					}
					MethodInfo[] methods;
					try
					{
						methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
					}
					catch
					{
						continue;
					}
					foreach (MethodInfo methodInfo in methods)
					{
						if (!(methodInfo == null) && (!(methodInfo.Name != "HasJobOnThing") || !(methodInfo.Name != "JobOnThing") || !(methodInfo.Name != "NonScanJob") || !(methodInfo.Name != "ShouldSkip")) && hashSet.Add(methodInfo))
						{
							try
							{
								harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(DiagnosticsV03), "WorkGiverPrefix", (Type[])null)
								{
									priority = 820
								}, new HarmonyMethod(typeof(DiagnosticsV03), "WorkGiverPostfix", (Type[])null)
								{
									priority = -20
								}, (HarmonyMethod)null, (HarmonyMethod)null);
								workGiverPatched++;
							}
							catch
							{
								patchFailures++;
							}
						}
					}
				}
			}
		}
		catch
		{
			patchFailures++;
		}
	}

	public static void WorkGiverPrefix(ref long __state)
	{
		DetermineContext determineContext = current;
		__state = ((determineContext == null || !determineContext.CaptureDetails) ? 0 : Stopwatch.GetTimestamp());
	}

	public static void WorkGiverPostfix(object __instance, MethodBase __originalMethod, long __state)
	{
		RecordWorkGiver(__instance, __originalMethod, __state);
	}

	internal static void BeginDetermine(Pawn_JobTracker tracker)
	{
		if (current != null)
		{
			contextReentry++;
			current = null;
		}
		Pawn pawn = null;
		try
		{
			if (tracker != null && JobTrackerPawnField != null)
			{
				object value = JobTrackerPawnField.GetValue(tracker);
				pawn = (Pawn)((value is Pawn) ? value : null);
			}
		}
		catch
		{
			failures++;
		}
		bool flag = detailPackagesRemaining > 0;
		if (flag)
		{
			detailPackagesRemaining--;
		}
		bool flag2 = DiagnosticsHub.DeepActive || flag;
		DetermineContext determineContext = new DetermineContext();
		determineContext.Started = Stopwatch.GetTimestamp();
		determineContext.Pawn = pawn;
		determineContext.CaptureDetails = flag2;
		if (flag2)
		{
			determineContext.Methods = new Dictionary<string, MethodAccum>(StringComparer.Ordinal);
			detailedDetermines++;
		}
		current = determineContext;
		determines++;
	}

	internal static void RecordWorkGiver(object instance, MethodBase method, long started)
	{
		DetermineContext determineContext = current;
		if (determineContext == null || !determineContext.CaptureDetails || determineContext.Methods == null || started == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - started;
		if (num <= 0)
		{
			return;
		}
		long num2 = num * 1000000 / Stopwatch.Frequency;
		string text = ((instance != null) ? instance.GetType().FullName : ((method == null || method.DeclaringType == null) ? "<unknown>" : method.DeclaringType.FullName));
		if (string.IsNullOrEmpty(text))
		{
			text = "<unknown>";
		}
		string key = text + "." + ((method == null) ? "<method>" : method.Name);
		if (!determineContext.Methods.TryGetValue(key, out var value))
		{
			if (determineContext.Methods.Count >= 256)
			{
				key = "<other>";
			}
			if (!determineContext.Methods.TryGetValue(key, out value))
			{
				value = default(MethodAccum);
			}
		}
		value.Calls++;
		value.TotalUs += num2;
		if (num2 > value.MaxUs)
		{
			value.MaxUs = num2;
		}
		determineContext.Methods[key] = value;
		workGiverCallsInsideDetermine++;
		workGiverUsInsideDetermine += num2;
	}

	internal static void EndDetermine(Pawn_JobTracker tracker, ThinkResult result)
	{
		DetermineContext determineContext = current;
		current = null;
		if (determineContext == null || determineContext.Started == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - determineContext.Started;
		if (num <= 0)
		{
			return;
		}
		long num2 = num * 1000000 / Stopwatch.Frequency;
		if (num2 < 20000)
		{
			return;
		}
		slowDetermines++;
		if (determineContext.CaptureDetails)
		{
			slowDetailed++;
		}
		else
		{
			slowUndetailed++;
			if (detailPackagesRemaining < 24)
			{
				detailPackagesRemaining = 24;
			}
		}
		int tick = -1;
		try
		{
			tick = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
		}
		string pawn = PawnText(determineContext.Pawn);
		string resultJob = "<none>";
		string source = "<none>";
		try
		{
			if (((ThinkResult)(ref result)).Job != null && ((ThinkResult)(ref result)).Job.def != null)
			{
				resultJob = ((Def)((ThinkResult)(ref result)).Job.def).defName;
			}
			if (((ThinkResult)(ref result)).SourceNode != null)
			{
				source = ((object)((ThinkResult)(ref result)).SourceNode).GetType().FullName;
			}
		}
		catch
		{
			failures++;
		}
		string topMethods = "detail-not-armed";
		if (determineContext.Methods != null && determineContext.Methods.Count != 0)
		{
			topMethods = string.Join(" | ", (from kv in determineContext.Methods.OrderByDescending((KeyValuePair<string, MethodAccum> kv) => kv.Value.TotalUs).Take(16)
				select kv.Key + "[calls=" + kv.Value.Calls + ",totalMs=" + ((double)kv.Value.TotalUs / 1000.0).ToString("F2") + ",maxMs=" + ((double)kv.Value.MaxUs / 1000.0).ToString("F2") + "]").ToArray());
		}
		SlowDetermineRecord slowDetermineRecord = new SlowDetermineRecord(tick, pawn, num2, resultJob, source, topMethods);
		Recent[recentPos] = slowDetermineRecord;
		recentPos = (recentPos + 1) % 32;
		if (recentCount < 32)
		{
			recentCount++;
		}
		AddWorst(slowDetermineRecord);
	}

	internal static string BuildSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(16384);
		stringBuilder.Append("SlowDNJCorrelation: determines=").Append(determines).Append(", slow>=20ms=")
			.Append(slowDetermines)
			.Append(", detailedDetermines=")
			.Append(detailedDetermines)
			.Append(", slowDetailed/undetailed=")
			.Append(slowDetailed)
			.Append('/')
			.Append(slowUndetailed)
			.Append(", detailBurstRemaining=")
			.Append(detailPackagesRemaining)
			.Append(", workGiverPatched=")
			.Append(workGiverPatched)
			.Append(", patchFailures=")
			.Append(patchFailures)
			.Append(", workGiverCalls=")
			.Append(workGiverCallsInsideDetermine)
			.Append(", workGiverTimeMs=")
			.Append(((double)workGiverUsInsideDetermine / 1000.0).ToString("F1"))
			.Append(", contextReentry=")
			.Append(contextReentry)
			.Append(", failures=")
			.Append(failures)
			.AppendLine();
		stringBuilder.Append("RecentSlowDNJ=");
		if (recentCount == 0)
		{
			stringBuilder.AppendLine("none");
			return stringBuilder.ToString();
		}
		stringBuilder.AppendLine();
		int num = ((recentCount == 32) ? recentPos : 0);
		for (int i = 0; i < recentCount; i++)
		{
			SlowDetermineRecord slowDetermineRecord = Recent[(num + i) % 32];
			stringBuilder.Append(" - tick=").Append(slowDetermineRecord.Tick).Append(", pawn=")
				.Append(slowDetermineRecord.Pawn)
				.Append(", totalMs=")
				.Append(((double)slowDetermineRecord.TotalUs / 1000.0).ToString("F2"))
				.Append(", result=")
				.Append(slowDetermineRecord.ResultJob)
				.Append(", source=")
				.Append(slowDetermineRecord.Source)
				.Append(", top=")
				.Append(string.IsNullOrEmpty(slowDetermineRecord.TopMethods) ? "none" : slowDetermineRecord.TopMethods)
				.AppendLine();
		}
		stringBuilder.AppendLine("WorstSlowDNJ=");
		if (Worst.Count == 0)
		{
			stringBuilder.AppendLine("none");
		}
		else
		{
			for (int j = 0; j < Worst.Count; j++)
			{
				AppendRecord(stringBuilder, Worst[j]);
			}
		}
		return stringBuilder.ToString();
	}

	private static void AddWorst(SlowDetermineRecord record)
	{
		if (Worst.Count < 16)
		{
			Worst.Add(record);
			Worst.Sort((SlowDetermineRecord a, SlowDetermineRecord b) => b.TotalUs.CompareTo(a.TotalUs));
		}
		else if (record.TotalUs > Worst[Worst.Count - 1].TotalUs)
		{
			Worst[Worst.Count - 1] = record;
			Worst.Sort((SlowDetermineRecord a, SlowDetermineRecord b) => b.TotalUs.CompareTo(a.TotalUs));
		}
	}

	private static void AppendRecord(StringBuilder sb, SlowDetermineRecord e)
	{
		sb.Append(" - tick=").Append(e.Tick).Append(", pawn=")
			.Append(e.Pawn)
			.Append(", totalMs=")
			.Append(((double)e.TotalUs / 1000.0).ToString("F2"))
			.Append(", result=")
			.Append(e.ResultJob)
			.Append(", source=")
			.Append(e.Source)
			.Append(", top=")
			.Append(string.IsNullOrEmpty(e.TopMethods) ? "none" : e.TopMethods)
			.AppendLine();
	}

	internal static void Reset()
	{
		Array.Clear(Recent, 0, Recent.Length);
		Worst.Clear();
		recentPos = (recentCount = 0);
		determines = (slowDetermines = (detailedDetermines = (slowDetailed = (slowUndetailed = 0L))));
		workGiverCallsInsideDetermine = (workGiverUsInsideDetermine = (contextReentry = (failures = 0L)));
		detailPackagesRemaining = 0;
		current = null;
	}

	private static string PawnText(Pawn pawn)
	{
		if (pawn == null)
		{
			return "<null>";
		}
		string text = null;
		try
		{
			text = ((Entity)pawn).LabelShortCap;
		}
		catch
		{
		}
		if (string.IsNullOrEmpty(text))
		{
			text = ((((Thing)pawn).def == null) ? "Pawn" : ((Def)((Thing)pawn).def).defName);
		}
		return text + "#" + ((Thing)pawn).thingIDNumber;
	}
}
