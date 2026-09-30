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

internal static class DiagnosticsV02
{
	private sealed class LiveWaitState
	{
		internal int PawnId;

		internal string PawnLabel;

		internal string LastJob;

		internal int IdleSince;

		internal int LastSeen;
	}

	private struct MethodStat
	{
		internal long Calls;

		internal long TotalUs;

		internal long MaxUs;

		internal long Over5;

		internal long Over20;

		internal long Over50;
	}

	private const int WaitCensusEveryTicks = 30;

	private const int MaxWaitStates = 512;

	private const int MaxRows = 48;

	private const long GapThresholdMs = 1000L;

	private static readonly Dictionary<int, LiveWaitState> LiveWait = new Dictionary<int, LiveWaitState>();

	private static readonly Dictionary<string, MethodStat> PatherChildren = new Dictionary<string, MethodStat>(StringComparer.Ordinal);

	private static readonly Dictionary<string, MethodStat> WorkGivers = new Dictionary<string, MethodStat>(StringComparer.Ordinal);

	private static readonly Dictionary<string, MethodStat> WorldCatastrophic = new Dictionary<string, MethodStat>(StringComparer.Ordinal);

	private static readonly Dictionary<string, MethodStat> ReachCaptureMethods = new Dictionary<string, MethodStat>(StringComparer.Ordinal);

	[ThreadStatic]
	private static int reachCaptureDepth;

	private static long lastTickWall;

	private static long pauseGapCount;

	private static long maxPauseGapMs;

	private static int lastGapGameTick = -1;

	private static long waitCensuses;

	private static long waitPawnSamples;

	private static long workGiverPatched;

	private static long patherChildPatched;

	private static long reachCapturePatched;

	private static long worldCatPatched;

	private static long installFailures;

	internal static void Apply(Harmony harmony)
	{
		if (harmony != null)
		{
			PatchPatherChildren(harmony);
			PatchReachProfileCapture(harmony);
			PatchKnownWorldCatastrophic(harmony);
		}
	}

	internal static void OnTickBegin()
	{
		long timestamp = Stopwatch.GetTimestamp();
		long num = lastTickWall;
		lastTickWall = timestamp;
		if (num == 0L)
		{
			return;
		}
		long num2 = (timestamp - num) * 1000 / Stopwatch.Frequency;
		if (num2 < 1000)
		{
			return;
		}
		pauseGapCount++;
		if (num2 > maxPauseGapMs)
		{
			maxPauseGapMs = num2;
		}
		try
		{
			lastGapGameTick = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
			lastGapGameTick = -1;
		}
	}

	internal static void OnTickEnd()
	{
		if (RimMTDiagnosticsSettings.EnableWaitTrace)
		{
			int num;
			try
			{
				num = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
			}
			catch
			{
				return;
			}
			if (num >= 0 && num % 30 == 0)
			{
				RunWaitCensus(num);
			}
		}
	}

	private static void RunWaitCensus(int tick)
	{
		waitCensuses++;
		HashSet<int> hashSet = new HashSet<int>();
		try
		{
			List<Map> maps = Find.Maps;
			for (int i = 0; i < maps.Count; i++)
			{
				Map val = maps[i];
				if (val == null || val.mapPawns == null)
				{
					continue;
				}
				List<Pawn> freeColonistsSpawned = val.mapPawns.FreeColonistsSpawned;
				for (int j = 0; j < freeColonistsSpawned.Count; j++)
				{
					Pawn val2 = freeColonistsSpawned[j];
					if (val2 == null || ((Thing)val2).Destroyed)
					{
						continue;
					}
					int thingIDNumber = ((Thing)val2).thingIDNumber;
					hashSet.Add(thingIDNumber);
					waitPawnSamples++;
					Job val3 = null;
					try
					{
						val3 = val2.CurJob;
					}
					catch
					{
					}
					bool num = IsIdle(val3);
					if (!LiveWait.TryGetValue(thingIDNumber, out var value))
					{
						if (LiveWait.Count >= 512)
						{
							int key = LiveWait.Keys.FirstOrDefault();
							LiveWait.Remove(key);
						}
						value = new LiveWaitState
						{
							PawnId = thingIDNumber,
							PawnLabel = PawnLabel(val2),
							IdleSince = -1
						};
					}
					if (num)
					{
						if (value.IdleSince < 0)
						{
							value.IdleSince = tick;
						}
						value.LastJob = ((val3 == null || val3.def == null) ? "<null>" : ((Def)val3.def).defName);
						value.LastSeen = tick;
					}
					else
					{
						value.IdleSince = -1;
						value.LastJob = ((val3 == null || val3.def == null) ? "<null>" : ((Def)val3.def).defName);
						value.LastSeen = tick;
					}
					LiveWait[thingIDNumber] = value;
				}
			}
			if (LiveWait.Count <= 0)
			{
				return;
			}
			List<int> list = null;
			foreach (KeyValuePair<int, LiveWaitState> item in LiveWait)
			{
				if (!hashSet.Contains(item.Key))
				{
					if (list == null)
					{
						list = new List<int>();
					}
					list.Add(item.Key);
				}
			}
			if (list != null)
			{
				for (int k = 0; k < list.Count; k++)
				{
					LiveWait.Remove(list[k]);
				}
			}
		}
		catch
		{
		}
	}

	private static void PatchPatherChildren(Harmony harmony)
	{
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Expected O, but got Unknown
		//IL_00b0: Expected O, but got Unknown
		string[] array = new string[6] { "TryEnterNextPathCell", "SetupMoveIntoNextCell", "CostToMoveIntoCell", "StartPath", "StopDead", "PatherFailed" };
		try
		{
			List<MethodInfo> declaredMethods = AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower));
			for (int i = 0; i < declaredMethods.Count; i++)
			{
				MethodInfo methodInfo = declaredMethods[i];
				if (!(methodInfo == null) && Array.IndexOf(array, methodInfo.Name) >= 0)
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(DiagnosticsV02), "PatherChildPrefix", (Type[])null)
					{
						priority = 800
					}, new HarmonyMethod(typeof(DiagnosticsV02), "PatherChildPostfix", (Type[])null)
					{
						priority = 0
					}, (HarmonyMethod)null, (HarmonyMethod)null);
					patherChildPatched++;
				}
			}
		}
		catch
		{
			installFailures++;
		}
	}

	public static void PatherChildPrefix(ref long __state)
	{
		__state = (DiagnosticsHub.DeepActive ? Stopwatch.GetTimestamp() : 0);
	}

	public static void PatherChildPostfix(MethodBase __originalMethod, long __state)
	{
		RecordMethod(PatherChildren, __originalMethod, __state);
	}

	private static void PatchWorkGiverMethods(Harmony harmony)
	{
		//IL_010f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_012f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_0142: Expected O, but got Unknown
		//IL_0142: Expected O, but got Unknown
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
								harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(DiagnosticsV02), "WorkGiverPrefix", (Type[])null)
								{
									priority = 800
								}, new HarmonyMethod(typeof(DiagnosticsV02), "WorkGiverPostfix", (Type[])null)
								{
									priority = 0
								}, (HarmonyMethod)null, (HarmonyMethod)null);
								workGiverPatched++;
							}
							catch
							{
								installFailures++;
							}
						}
					}
				}
			}
		}
		catch
		{
			installFailures++;
		}
	}

	public static void WorkGiverPrefix(ref long __state)
	{
		__state = (DiagnosticsHub.DeepActive ? Stopwatch.GetTimestamp() : 0);
	}

	public static void WorkGiverPostfix(object __instance, MethodBase __originalMethod, long __state)
	{
		if (__state != 0L)
		{
			string name = ((__instance != null) ? __instance.GetType().FullName : ((__originalMethod == null || __originalMethod.DeclaringType == null) ? "<unknown>" : __originalMethod.DeclaringType.FullName)) + "." + ((__originalMethod == null) ? "<method>" : __originalMethod.Name);
			RecordMethod(WorkGivers, name, __state);
		}
	}

	private static void PatchReachProfileCapture(Harmony harmony)
	{
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_009d: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b0: Expected O, but got Unknown
		//IL_00b0: Expected O, but got Unknown
		//IL_0110: Unknown result type (might be due to invalid IL or missing references)
		//IL_0115: Unknown result type (might be due to invalid IL or missing references)
		//IL_0130: Unknown result type (might be due to invalid IL or missing references)
		//IL_0135: Unknown result type (might be due to invalid IL or missing references)
		//IL_0143: Expected O, but got Unknown
		//IL_0143: Expected O, but got Unknown
		try
		{
			Assembly assembly = AppDomain.CurrentDomain.GetAssemblies().FirstOrDefault((Assembly a) => a.GetName().Name == "RimMT");
			Type type = ((assembly == null) ? null : assembly.GetType("RimMT.AggressiveReachabilityProfilesV17", throwOnError: false));
			MethodInfo methodInfo = ((type == null) ? null : type.GetMethod("ProfileCaptureDrainPostfix", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic));
			if (methodInfo != null)
			{
				harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(DiagnosticsV02), "ReachCaptureDrainPrefix", (Type[])null)
				{
					priority = 800
				}, new HarmonyMethod(typeof(DiagnosticsV02), "ReachCaptureDrainPostfix", (Type[])null)
				{
					priority = 0
				}, (HarmonyMethod)null, (HarmonyMethod)null);
				reachCapturePatched++;
			}
			List<MethodInfo> declaredMethods = AccessTools.GetDeclaredMethods(typeof(Region));
			for (int num = 0; num < declaredMethods.Count; num++)
			{
				MethodInfo methodInfo2 = declaredMethods[num];
				if (!(methodInfo2 == null) && !(methodInfo2.Name != "Allows"))
				{
					harmony.Patch((MethodBase)methodInfo2, new HarmonyMethod(typeof(DiagnosticsV02), "RegionAllowsPrefix", (Type[])null)
					{
						priority = 800
					}, new HarmonyMethod(typeof(DiagnosticsV02), "RegionAllowsPostfix", (Type[])null)
					{
						priority = 0
					}, (HarmonyMethod)null, (HarmonyMethod)null);
					reachCapturePatched++;
				}
			}
		}
		catch
		{
			installFailures++;
		}
	}

	public static void ReachCaptureDrainPrefix()
	{
		reachCaptureDepth++;
	}

	public static void ReachCaptureDrainPostfix()
	{
		if (reachCaptureDepth > 0)
		{
			reachCaptureDepth--;
		}
	}

	public static void RegionAllowsPrefix(ref long __state)
	{
		__state = ((reachCaptureDepth > 0) ? Stopwatch.GetTimestamp() : 0);
	}

	public static void RegionAllowsPostfix(MethodBase __originalMethod, long __state)
	{
		RecordMethod(ReachCaptureMethods, __originalMethod, __state);
	}

	private static void PatchKnownWorldCatastrophic(Harmony harmony)
	{
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_00af: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Expected O, but got Unknown
		//IL_00c2: Expected O, but got Unknown
		string[] array = new string[2] { "VFEEmpire.WorldComponent_Hierarchy", "Vehicles.WorldVehiclePathGrid" };
		try
		{
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			for (int i = 0; i < array.Length; i++)
			{
				Type type = null;
				for (int j = 0; j < assemblies.Length; j++)
				{
					if (!(type == null))
					{
						break;
					}
					try
					{
						type = assemblies[j].GetType(array[i], throwOnError: false);
					}
					catch
					{
					}
				}
				if (!(type == null))
				{
					MethodInfo methodInfo = AccessTools.Method(type, "WorldComponentTick", (Type[])null, (Type[])null);
					if (!(methodInfo == null))
					{
						harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(DiagnosticsV02), "WorldCatPrefix", (Type[])null)
						{
							priority = 800
						}, new HarmonyMethod(typeof(DiagnosticsV02), "WorldCatPostfix", (Type[])null)
						{
							priority = 0
						}, (HarmonyMethod)null, (HarmonyMethod)null);
						worldCatPatched++;
					}
				}
			}
		}
		catch
		{
			installFailures++;
		}
	}

	public static void WorldCatPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void WorldCatPostfix(MethodBase __originalMethod, long __state)
	{
		RecordMethod(WorldCatastrophic, __originalMethod, __state);
	}

	private static void RecordMethod(Dictionary<string, MethodStat> dict, MethodBase method, long started)
	{
		if (started != 0L)
		{
			string name = ((method == null) ? "<unknown>" : (((method.DeclaringType == null) ? "<type>" : method.DeclaringType.FullName) + "." + method.Name));
			RecordMethod(dict, name, started);
		}
	}

	private static void RecordMethod(Dictionary<string, MethodStat> dict, string name, long started)
	{
		if (started == 0L)
		{
			return;
		}
		long num = (Stopwatch.GetTimestamp() - started) * 1000000 / Stopwatch.Frequency;
		if (num < 0)
		{
			return;
		}
		if (!dict.TryGetValue(name, out var value))
		{
			if (dict.Count >= 48)
			{
				name = "<other>";
			}
			if (!dict.TryGetValue(name, out value))
			{
				value = default(MethodStat);
			}
		}
		value.Calls++;
		value.TotalUs += num;
		if (num > value.MaxUs)
		{
			value.MaxUs = num;
		}
		if (num >= 5000)
		{
			value.Over5++;
		}
		if (num >= 20000)
		{
			value.Over20++;
		}
		if (num >= 50000)
		{
			value.Over50++;
		}
		dict[name] = value;
	}

	internal static string BuildSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(12288);
		stringBuilder.AppendLine("[Diagnostics v0.2 targeted probes]");
		stringBuilder.Append("PauseResume: gaps>=1s=").Append(pauseGapCount).Append(", maxGapMs=")
			.Append(maxPauseGapMs)
			.Append(", lastGapGameTick=")
			.Append(lastGapGameTick)
			.AppendLine();
		stringBuilder.Append("LiveWaitCensus: censuses=").Append(waitCensuses).Append(", pawnSamples=")
			.Append(waitPawnSamples)
			.Append(", activeIdle=")
			.Append(LiveWait.Values.Count((LiveWaitState s) => s.IdleSince >= 0))
			.AppendLine();
		stringBuilder.AppendLine("LongestCurrentWait=" + LongestCurrentWait());
		AppendTop(stringBuilder, "PatherChildren", PatherChildren);
		stringBuilder.AppendLine("WorkGiverSampled=RETIRED in Diagnostics v0.4; SlowDNJCorrelation is the sole WorkGiver timing path.");
		AppendTop(stringBuilder, "ReachCaptureRegionAllows", ReachCaptureMethods);
		AppendTop(stringBuilder, "WorldCatastrophic", WorldCatastrophic);
		stringBuilder.Append("Install: workGiverPatched=").Append(workGiverPatched).Append(", patherChildPatched=")
			.Append(patherChildPatched)
			.Append(", reachCapturePatched=")
			.Append(reachCapturePatched)
			.Append(", worldCatPatched=")
			.Append(worldCatPatched)
			.Append(", failures=")
			.Append(installFailures)
			.AppendLine();
		return stringBuilder.ToString();
	}

	internal static void Reset()
	{
		LiveWait.Clear();
		PatherChildren.Clear();
		WorkGivers.Clear();
		WorldCatastrophic.Clear();
		ReachCaptureMethods.Clear();
		pauseGapCount = (maxPauseGapMs = (waitCensuses = (waitPawnSamples = 0L)));
		lastGapGameTick = -1;
	}

	private static string LongestCurrentWait()
	{
		int tick;
		try
		{
			tick = ((Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame);
		}
		catch
		{
			tick = -1;
		}
		return string.Join("; ", (from s in (from s in LiveWait.Values
				where s.IdleSince >= 0
				orderby (tick >= 0) ? (tick - s.IdleSince) : 0 descending
				select s).Take(16)
			select s.PawnLabel + "#" + s.PawnId + " job=" + s.LastJob + " durTicks=" + ((tick >= 0) ? Math.Max(0, tick - s.IdleSince) : 0)).ToArray());
	}

	private static void AppendTop(StringBuilder sb, string label, Dictionary<string, MethodStat> dict)
	{
		if (dict.Count == 0)
		{
			sb.AppendLine(label + "=none");
			return;
		}
		string text = string.Join("; ", (from kv in dict.OrderByDescending((KeyValuePair<string, MethodStat> kv) => kv.Value.TotalUs).Take(16)
			select kv.Key + "[calls=" + kv.Value.Calls + ",avgUs=" + ((kv.Value.Calls == 0L) ? 0.0 : ((double)kv.Value.TotalUs / (double)kv.Value.Calls)).ToString("F1") + ",>5/20/50=" + kv.Value.Over5 + "/" + kv.Value.Over20 + "/" + kv.Value.Over50 + ",maxMs=" + ((double)kv.Value.MaxUs / 1000.0).ToString("F2") + "]").ToArray());
		sb.AppendLine(label + "=" + text);
	}

	private static bool IsIdle(Job job)
	{
		if (job == null || job.def == null || string.IsNullOrEmpty(((Def)job.def).defName))
		{
			return false;
		}
		string defName = ((Def)job.def).defName;
		switch (defName)
		{
		default:
			if (!defName.StartsWith("Wait_", StringComparison.Ordinal))
			{
				return defName.EndsWith("IdleWait", StringComparison.Ordinal);
			}
			break;
		case "Wait":
		case "Wait_MaintainPosture":
		case "IPPO_IdleWait":
			break;
		}
		return true;
	}

	private static string PawnLabel(Pawn pawn)
	{
		try
		{
			if (pawn.Name != null)
			{
				return pawn.Name.ToStringShort;
			}
		}
		catch
		{
		}
		if (((Thing)pawn).def != null)
		{
			return ((Def)((Thing)pawn).def).defName;
		}
		return "Pawn";
	}
}
