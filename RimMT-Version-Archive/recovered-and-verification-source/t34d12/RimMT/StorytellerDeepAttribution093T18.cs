using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class StorytellerDeepAttribution093T18
{
	internal struct TimedState
	{
		internal long Started;

		internal string Label;
	}

	private sealed class Stat
	{
		internal string Label;

		internal long Calls;

		internal long TotalTicks;

		internal long MaxTicks;

		internal long Over5;

		internal long Over20;

		internal long Over100;
	}

	private const string DeepHarmonyId = "allen.rimmt.storyteller-t18";

	private const int CaptureIntervals = 64;

	private const int MaxStats = 32;

	private static readonly long Threshold5 = Math.Max(1L, Stopwatch.Frequency * 5 / 1000);

	private static readonly long Threshold20 = Math.Max(1L, Stopwatch.Frequency * 20 / 1000);

	private static readonly long Threshold100 = Math.Max(1L, Stopwatch.Frequency * 100 / 1000);

	private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>(StringComparer.Ordinal);

	private static readonly List<MethodBase> Patched = new List<MethodBase>();

	private static readonly Dictionary<MethodBase, bool> TopIterator = new Dictionary<MethodBase, bool>();

	private static readonly Dictionary<Type, FieldInfo> CompFieldCache = new Dictionary<Type, FieldInfo>();

	private static Harmony deepHarmony;

	private static int initialized;

	private static int requested;

	private static int started;

	private static int active;

	private static int completed;

	private static int stopRequested;

	private static int startFailures;

	private static int patchedMethods;

	private static int iteratorMethods;

	private static int intervalsCompleted;

	private static long queueCalls;

	private static long queueTicks;

	private static long queueMaxTicks;

	private static long queue5;

	private static long queue20;

	private static long queue100;

	private static long deepCatastrophicEvents;

	private static long deepCatastrophicMaxUs;

	private static long startFrame = -1L;

	private static long completionFrame = -1L;

	private static int startTick = -1;

	private static int completionTick = -1;

	internal static void Initialize()
	{
		Interlocked.Exchange(ref initialized, 1);
	}

	internal static void ObserveCatastrophic(long us)
	{
		if (us >= 100000 && Volatile.Read(ref active) != 0 && RimMTThreadGuard.IsMainThread)
		{
			deepCatastrophicEvents++;
			if (us > deepCatastrophicMaxUs)
			{
				deepCatastrophicMaxUs = us;
			}
		}
	}

	internal static void OnMainThreadFrame()
	{
		//IL_003f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Invalid comparison between Unknown and I4
		if (!RimMTThreadGuard.IsMainThread)
		{
			return;
		}
		if (Volatile.Read(ref stopRequested) != 0 && Volatile.Read(ref active) != 0)
		{
			StopCapture();
		}
		else
		{
			if (Volatile.Read(ref started) != 0 || Volatile.Read(ref requested) == 0 || (int)Current.ProgramState != 2 || RimMTRuntime.MainThreadFrames <= 1)
			{
				return;
			}
			Interlocked.Exchange(ref requested, 0);
			try
			{
				if (StartCapture())
				{
					Interlocked.Exchange(ref started, 1);
					Interlocked.Exchange(ref active, 1);
					startFrame = RimMTRuntime.MainThreadFrames;
					startTick = CurrentTick();
					Log.Message("[RimMT] T18 Storyteller deep burst started for the next " + 64 + " completed storyteller intervals; temporary detours auto-remove afterward.");
				}
				else
				{
					startFailures++;
				}
			}
			catch (Exception ex)
			{
				startFailures++;
				FailClosed(ex);
			}
		}
	}

	private static bool StartCapture()
	{
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000f: Expected O, but got Unknown
		//IL_007f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0094: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a0: Expected O, but got Unknown
		//IL_00a0: Expected O, but got Unknown
		//IL_010d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0122: Unknown result type (might be due to invalid IL or missing references)
		//IL_012e: Expected O, but got Unknown
		//IL_012e: Expected O, but got Unknown
		//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
		//IL_01fd: Unknown result type (might be due to invalid IL or missing references)
		//IL_0209: Expected O, but got Unknown
		//IL_0209: Expected O, but got Unknown
		deepHarmony = new Harmony("allen.rimmt.storyteller-t18");
		Patched.Clear();
		TopIterator.Clear();
		CompFieldCache.Clear();
		Stats.Clear();
		intervalsCompleted = 0;
		patchedMethods = 0;
		iteratorMethods = 0;
		MethodBase methodBase = AccessTools.Method(typeof(IncidentQueue), "IncidentQueueTick", (Type[])null, (Type[])null);
		if (methodBase != null)
		{
			deepHarmony.Patch(methodBase, new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "QueuePrefix", (Type[])null), new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "QueuePostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
			Patched.Add(methodBase);
			patchedMethods++;
		}
		MethodBase methodBase2 = AccessTools.Method(typeof(Storyteller), "TryFire", new Type[2]
		{
			typeof(FiringIncident),
			typeof(bool)
		}, (Type[])null);
		if (methodBase2 != null)
		{
			deepHarmony.Patch(methodBase2, new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "TryFirePrefix", (Type[])null), new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "TryFirePostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
			Patched.Add(methodBase2);
			patchedMethods++;
		}
		Type[] nestedTypes = typeof(Storyteller).GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
		foreach (Type type in nestedTypes)
		{
			if (((type == null) ? string.Empty : type.Name).IndexOf("MakeIncidentsForInterval", StringComparison.Ordinal) >= 0)
			{
				MethodInfo method = type.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
				if (!(method == null) && !(method.ReturnType != typeof(bool)))
				{
					bool value = FindCompField(type) == null;
					deepHarmony.Patch((MethodBase)method, new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "IteratorPrefix", (Type[])null), new HarmonyMethod(typeof(StorytellerDeepAttribution093T18), "IteratorPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
					Patched.Add(method);
					TopIterator[method] = value;
					patchedMethods++;
					iteratorMethods++;
				}
			}
		}
		if (iteratorMethods == 0)
		{
			StopAllPatches();
			return false;
		}
		return true;
	}

	private static void StopCapture()
	{
		StopAllPatches();
		Interlocked.Exchange(ref active, 0);
		Interlocked.Exchange(ref completed, 1);
		Interlocked.Exchange(ref stopRequested, 0);
		completionFrame = RimMTRuntime.MainThreadFrames;
		completionTick = CurrentTick();
		Log.Message("[RimMT] T18 Storyteller deep burst completed and temporary detours were removed. intervals=" + intervalsCompleted + ".");
	}

	private static void StopAllPatches()
	{
		Harmony val = deepHarmony;
		if (val != null)
		{
			for (int i = 0; i < Patched.Count; i++)
			{
				try
				{
					val.Unpatch(Patched[i], (HarmonyPatchType)0, "allen.rimmt.storyteller-t18");
				}
				catch
				{
				}
			}
		}
		Patched.Clear();
	}

	private static void FailClosed(Exception ex)
	{
		try
		{
			StopAllPatches();
		}
		catch
		{
		}
		Interlocked.Exchange(ref active, 0);
		Interlocked.Exchange(ref stopRequested, 0);
		Log.Warning("[RimMT] T18 Storyteller deep burst failed closed: " + ex.GetType().Name + ": " + ex.Message);
	}

	public static void QueuePrefix(ref long __state)
	{
		__state = ((Volatile.Read(ref active) != 0) ? Stopwatch.GetTimestamp() : 0);
	}

	public static void QueuePostfix(long __state)
	{
		if (__state != 0L && Volatile.Read(ref active) != 0)
		{
			long num = Stopwatch.GetTimestamp() - __state;
			queueCalls++;
			queueTicks += num;
			if (num > queueMaxTicks)
			{
				queueMaxTicks = num;
			}
			if (num >= Threshold5)
			{
				queue5++;
			}
			if (num >= Threshold20)
			{
				queue20++;
			}
			if (num >= Threshold100)
			{
				queue100++;
			}
		}
	}

	public static void TryFirePrefix(object[] __args, ref TimedState __state)
	{
		if (Volatile.Read(ref active) == 0)
		{
			return;
		}
		__state.Started = Stopwatch.GetTimestamp();
		__state.Label = "TryFire:<unknown>";
		try
		{
			FiringIncident val = (FiringIncident)((__args != null && __args.Length != 0) ? /*isinst with value type is only supported in some contexts*/: null);
			if (val != null && val.def != null)
			{
				string text = ((val.def.Worker == null) ? "<no-worker>" : ((object)val.def.Worker).GetType().FullName);
				__state.Label = "TryFire:" + ((Def)val.def).defName + "|" + text;
			}
		}
		catch
		{
		}
	}

	public static void TryFirePostfix(TimedState __state)
	{
		Record(__state);
	}

	public static void IteratorPrefix(MethodBase __originalMethod, object __instance, ref TimedState __state)
	{
		if (Volatile.Read(ref active) == 0)
		{
			return;
		}
		__state.Started = Stopwatch.GetTimestamp();
		bool value = false;
		TopIterator.TryGetValue(__originalMethod, out value);
		if (value)
		{
			__state.Label = "Iterator:Storyteller.MakeIncidentsForInterval(top)";
			return;
		}
		string text = "<unknown-comp>";
		try
		{
			FieldInfo fieldInfo = FindCompField(__instance?.GetType());
			StorytellerComp val = (StorytellerComp)((fieldInfo == null || __instance == null) ? null : /*isinst with value type is only supported in some contexts*/);
			if (val != null)
			{
				text = ((object)val).GetType().FullName;
			}
		}
		catch
		{
		}
		__state.Label = "Iterator:StorytellerComp:" + text;
	}

	public static void IteratorPostfix(MethodBase __originalMethod, bool __result, TimedState __state)
	{
		Record(__state);
		bool value = false;
		if (TopIterator.TryGetValue(__originalMethod, out value) && value && !__result && Interlocked.Increment(ref intervalsCompleted) >= 64)
		{
			Interlocked.Exchange(ref stopRequested, 1);
		}
	}

	private static void Record(TimedState state)
	{
		if (state.Started == 0L || Volatile.Read(ref active) == 0)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - state.Started;
		string text = (string.IsNullOrEmpty(state.Label) ? "<unknown>" : state.Label);
		if (!Stats.TryGetValue(text, out var value))
		{
			if (Stats.Count >= 32)
			{
				text = "<other>";
			}
			if (!Stats.TryGetValue(text, out value))
			{
				value = new Stat
				{
					Label = text
				};
				Stats[text] = value;
			}
		}
		value.Calls++;
		value.TotalTicks += num;
		if (num > value.MaxTicks)
		{
			value.MaxTicks = num;
		}
		if (num >= Threshold5)
		{
			value.Over5++;
		}
		if (num >= Threshold20)
		{
			value.Over20++;
		}
		if (num >= Threshold100)
		{
			value.Over100++;
		}
	}

	private static FieldInfo FindCompField(Type type)
	{
		if (type == null)
		{
			return null;
		}
		if (CompFieldCache.TryGetValue(type, out var value))
		{
			return value;
		}
		FieldInfo fieldInfo = null;
		try
		{
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			for (int i = 0; i < fields.Length; i++)
			{
				if (typeof(StorytellerComp).IsAssignableFrom(fields[i].FieldType))
				{
					fieldInfo = fields[i];
					break;
				}
			}
		}
		catch
		{
		}
		CompFieldCache[type] = fieldInfo;
		return fieldInfo;
	}

	internal static string Summary()
	{
		List<Stat> list = new List<Stat>(Stats.Values);
		list.Sort((Stat a, Stat b) => b.TotalTicks.CompareTo(a.TotalTicks));
		StringBuilder stringBuilder = new StringBuilder(8192);
		double num = (double)queueTicks * 1000.0 / (double)Stopwatch.Frequency;
		double num2 = ((queueCalls == 0L) ? 0.0 : ((double)queueTicks * 1000000.0 / (double)Stopwatch.Frequency / (double)queueCalls));
		double num3 = (double)queueMaxTicks * 1000.0 / (double)Stopwatch.Frequency;
		stringBuilder.Append("T18 Storyteller deep burst: requested=").Append(Volatile.Read(ref requested) != 0).Append(", started=")
			.Append(Volatile.Read(ref started) != 0)
			.Append(", active=")
			.Append(Volatile.Read(ref active) != 0)
			.Append(", completed=")
			.Append(Volatile.Read(ref completed) != 0)
			.Append(", intervals=")
			.Append(intervalsCompleted)
			.Append('/')
			.Append(64)
			.Append(", patchedMethods=")
			.Append(patchedMethods)
			.Append(", iteratorMethods=")
			.Append(iteratorMethods)
			.Append(", startFailures=")
			.Append(startFailures)
			.Append(", deepCatastrophicEvents=")
			.Append(deepCatastrophicEvents)
			.Append(", deepCatastrophicMaxMs=")
			.Append(((double)deepCatastrophicMaxUs / 1000.0).ToString("F2"))
			.Append(", startFrame/tick=")
			.Append(startFrame)
			.Append('/')
			.Append(startTick)
			.Append(", completionFrame/tick=")
			.Append(completionFrame)
			.Append('/')
			.Append(completionTick)
			.Append("\n  IncidentQueueTick: calls=")
			.Append(queueCalls)
			.Append(", totalMs=")
			.Append(num.ToString("F2"))
			.Append(", avgUs=")
			.Append(num2.ToString("F2"))
			.Append(", maxMs=")
			.Append(num3.ToString("F3"))
			.Append(", >=5/20/100ms=")
			.Append(queue5)
			.Append('/')
			.Append(queue20)
			.Append('/')
			.Append(queue100);
		int num4 = Math.Min(20, list.Count);
		for (int num5 = 0; num5 < num4; num5++)
		{
			Stat stat = list[num5];
			double num6 = (double)stat.TotalTicks * 1000.0 / (double)Stopwatch.Frequency;
			double num7 = ((stat.Calls == 0L) ? 0.0 : (num6 / (double)stat.Calls));
			double num8 = (double)stat.MaxTicks * 1000.0 / (double)Stopwatch.Frequency;
			stringBuilder.Append("\n  #").Append(num5 + 1).Append(' ')
				.Append(stat.Label)
				.Append(": calls=")
				.Append(stat.Calls)
				.Append(", totalMs=")
				.Append(num6.ToString("F2"))
				.Append(", avgMs=")
				.Append(num7.ToString("F3"))
				.Append(", maxMs=")
				.Append(num8.ToString("F3"))
				.Append(", >=5/20/100ms=")
				.Append(stat.Over5)
				.Append('/')
				.Append(stat.Over20)
				.Append('/')
				.Append(stat.Over100);
		}
		stringBuilder.Append(". Temporary Harmony detours only; measurement-only; auto-unpatch after bounded interval window.");
		return stringBuilder.ToString();
	}

	private static int CurrentTick()
	{
		try
		{
			return (Find.TickManager == null) ? (-1) : Find.TickManager.TicksGame;
		}
		catch
		{
			return -1;
		}
	}
}
