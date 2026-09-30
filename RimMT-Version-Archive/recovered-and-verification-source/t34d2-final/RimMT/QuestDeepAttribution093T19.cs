using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using RimWorld.QuestGen;
using Verse;

namespace RimMT;

internal static class QuestDeepAttribution093T19
{
	public sealed class ChooserState
	{
		internal long Started;

		internal ChoiceContext Previous;

		internal ChoiceContext Context;

		internal bool Completed;
	}

	private sealed class MethodBaseHolder
	{
		internal readonly MethodBase Chooser;

		internal readonly MethodBase CanRun;

		internal MethodBaseHolder(MethodBase chooser, MethodBase canRun)
		{
			Chooser = chooser;
			CanRun = canRun;
		}
	}

	public sealed class ChoiceContext
	{
		internal long CanRunTicks;

		internal int CanRunCalls;

		internal readonly List<SlowQuestCall> SlowCalls = new List<SlowQuestCall>();

		internal void AddSlow(SlowQuestCall call)
		{
			SlowCalls.Add(call);
		}
	}

	private sealed class QuestStats
	{
		internal readonly string DefName;

		internal readonly string ModName;

		internal readonly string PackageId;

		internal long Calls;

		internal long TotalTicks;

		internal long MaxTicks;

		internal long TrueCount;

		internal long FalseCount;

		internal long Exceptions;

		internal QuestStats(string defName, string modName, string packageId)
		{
			DefName = defName;
			ModName = modName;
			PackageId = packageId;
		}
	}

	public struct SlowQuestCall
	{
		internal readonly string DefName;

		internal readonly string ModName;

		internal readonly string PackageId;

		internal readonly long ElapsedTicks;

		internal readonly bool Result;

		internal readonly bool Exception;

		internal SlowQuestCall(string defName, string modName, string packageId, long elapsedTicks, bool result, bool exception)
		{
			DefName = defName;
			ModName = modName;
			PackageId = packageId;
			ElapsedTicks = elapsedTicks;
			Result = result;
			Exception = exception;
		}
	}

	private sealed class Catastrophe
	{
		internal long Tick;

		internal long TotalTicks;

		internal long CanRunTicks;

		internal int CanRunCalls;

		internal bool Exception;

		internal List<SlowQuestCall> SlowCalls;
	}

	private const double SlowThresholdMs = 5.0;

	private const double CatastrophicThresholdMs = 100.0;

	private const int MaxRecentCatastrophes = 12;

	private const int MaxSlowCallsPerCatastrophe = 16;

	private const int MaxSummaryQuests = 24;

	[ThreadStatic]
	private static ChoiceContext currentChoice;

	private static bool installed;

	private static MethodBaseHolder targets;

	private static long chooserCalls;

	private static long chooserTicks;

	private static long chooserMaxTicks;

	private static long chooserCatastrophic;

	private static long canRunCalls;

	private static long canRunTicks;

	private static long canRunSlow5;

	private static long canRunSlow20;

	private static long canRunSlow100;

	private static long failures;

	private static readonly Dictionary<string, QuestStats> stats = new Dictionary<string, QuestStats>(StringComparer.Ordinal);

	private static readonly List<Catastrophe> recentCatastrophes = new List<Catastrophe>();

	internal static void Apply(Harmony harmony)
	{
		//IL_00a3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c8: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e5: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Expected O, but got Unknown
		//IL_00f1: Expected O, but got Unknown
		//IL_00f1: Expected O, but got Unknown
		//IL_0104: Unknown result type (might be due to invalid IL or missing references)
		//IL_0109: Unknown result type (might be due to invalid IL or missing references)
		//IL_0124: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Unknown result type (might be due to invalid IL or missing references)
		//IL_0141: Unknown result type (might be due to invalid IL or missing references)
		//IL_0146: Unknown result type (might be due to invalid IL or missing references)
		//IL_0152: Expected O, but got Unknown
		//IL_0152: Expected O, but got Unknown
		//IL_0152: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodInfo methodInfo = AccessTools.Method(typeof(NaturalRandomQuestChooser), "ChooseNaturalRandomQuest", new Type[2]
			{
				typeof(float),
				typeof(IIncidentTarget)
			}, (Type[])null);
			MethodInfo methodInfo2 = AccessTools.Method(typeof(QuestScriptDef), "CanRun", new Type[1] { typeof(Slate) }, (Type[])null);
			if (methodInfo == null || methodInfo2 == null)
			{
				Log.Warning("[RimMT] T19 quest deep attribution unavailable: chooser or QuestScriptDef.CanRun(Slate) not found.");
				return;
			}
			targets = new MethodBaseHolder(methodInfo, methodInfo2);
			harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "ChooserPrefix", (Type[])null)
			{
				priority = 800
			}, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "ChooserPostfix", (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "ChooserFinalizer", (Type[])null)
			{
				priority = 0
			});
			harmony.Patch((MethodBase)methodInfo2, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "CanRunPrefix", (Type[])null)
			{
				priority = 800
			}, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "CanRunPostfix", (Type[])null)
			{
				priority = 0
			}, (HarmonyMethod)null, new HarmonyMethod(typeof(QuestDeepAttribution093T19), "CanRunFinalizer", (Type[])null)
			{
				priority = 0
			});
			installed = true;
			Log.Message("[RimMT] T19 quest deep attribution installed. Measurement-only; timers are active only inside NaturalRandomQuestChooser.ChooseNaturalRandomQuest().");
		}
		catch (Exception ex)
		{
			installed = false;
			failures++;
			Log.Warning("[RimMT] T19 quest deep attribution install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void ChooserPrefix(ref ChooserState __state)
	{
		//IL_0011: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Invalid comparison between Unknown and I4
		__state = null;
		if (installed && RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			ChoiceContext context = new ChoiceContext();
			__state = new ChooserState
			{
				Started = Stopwatch.GetTimestamp(),
				Previous = currentChoice,
				Context = context,
				Completed = false
			};
			currentChoice = context;
		}
	}

	public static void ChooserPostfix(ChooserState __state)
	{
		CompleteChooser(__state, exception: false);
	}

	public static Exception ChooserFinalizer(Exception __exception, ChooserState __state)
	{
		if (__exception != null)
		{
			CompleteChooser(__state, exception: true);
		}
		return __exception;
	}

	public static void CanRunPrefix(ref long __state)
	{
		__state = ((currentChoice == null) ? 0 : Stopwatch.GetTimestamp());
	}

	public static void CanRunPostfix(QuestScriptDef __instance, bool __result, long __state)
	{
		if (__state != 0L && currentChoice != null && __instance != null)
		{
			RecordCanRun(__instance, __result, exception: false, Stopwatch.GetTimestamp() - __state);
		}
	}

	public static Exception CanRunFinalizer(QuestScriptDef __instance, Exception __exception, long __state)
	{
		if (__exception != null && __state != 0L && currentChoice != null && __instance != null)
		{
			RecordCanRun(__instance, result: false, exception: true, Stopwatch.GetTimestamp() - __state);
		}
		return __exception;
	}

	private static void RecordCanRun(QuestScriptDef def, bool result, bool exception, long elapsedTicks)
	{
		canRunCalls++;
		canRunTicks += elapsedTicks;
		ChoiceContext choiceContext = currentChoice;
		choiceContext.CanRunCalls++;
		choiceContext.CanRunTicks += elapsedTicks;
		double num = ToMs(elapsedTicks);
		if (num >= 5.0)
		{
			canRunSlow5++;
		}
		if (num >= 20.0)
		{
			canRunSlow20++;
		}
		if (num >= 100.0)
		{
			canRunSlow100++;
		}
		string text = ((Def)def).defName ?? "<unnamed>";
		string modName = ((((Def)def).modContentPack != null) ? (((Def)def).modContentPack.Name ?? "<unnamed-mod>") : "<no-mod>");
		string text2 = ((((Def)def).modContentPack != null) ? (((Def)def).modContentPack.PackageId ?? "<no-package>") : "<no-package>");
		string key = text2 + "|" + text;
		if (!stats.TryGetValue(key, out var value))
		{
			value = new QuestStats(text, modName, text2);
			stats.Add(key, value);
		}
		value.Calls++;
		value.TotalTicks += elapsedTicks;
		if (elapsedTicks > value.MaxTicks)
		{
			value.MaxTicks = elapsedTicks;
		}
		if (exception)
		{
			value.Exceptions++;
		}
		else if (result)
		{
			value.TrueCount++;
		}
		else
		{
			value.FalseCount++;
		}
		if (num >= 5.0)
		{
			choiceContext.AddSlow(new SlowQuestCall(text, modName, text2, elapsedTicks, result, exception));
		}
	}

	private static void CompleteChooser(ChooserState state, bool exception)
	{
		if (state == null || state.Completed)
		{
			return;
		}
		state.Completed = true;
		long num = Stopwatch.GetTimestamp() - state.Started;
		chooserCalls++;
		chooserTicks += num;
		if (num > chooserMaxTicks)
		{
			chooserMaxTicks = num;
		}
		ChoiceContext context = state.Context;
		currentChoice = state.Previous;
		if (context != null && !(ToMs(num) < 100.0))
		{
			chooserCatastrophic++;
			long tick = -1L;
			try
			{
				tick = ((Find.TickManager != null) ? Find.TickManager.TicksGame : (-1));
			}
			catch
			{
			}
			context.SlowCalls.Sort((SlowQuestCall a, SlowQuestCall b) => b.ElapsedTicks.CompareTo(a.ElapsedTicks));
			if (context.SlowCalls.Count > 16)
			{
				context.SlowCalls.RemoveRange(16, context.SlowCalls.Count - 16);
			}
			recentCatastrophes.Add(new Catastrophe
			{
				Tick = tick,
				TotalTicks = num,
				CanRunTicks = context.CanRunTicks,
				CanRunCalls = context.CanRunCalls,
				Exception = exception,
				SlowCalls = new List<SlowQuestCall>(context.SlowCalls)
			});
			if (recentCatastrophes.Count > 12)
			{
				recentCatastrophes.RemoveAt(0);
			}
		}
	}

	internal static string Summary()
	{
		StringBuilder stringBuilder = new StringBuilder();
		double num = ToMs(chooserTicks);
		double num2 = ToMs(chooserMaxTicks);
		double num3 = ToMs(canRunTicks);
		stringBuilder.Append("T19 quest deep attribution: installed=").Append(installed).Append(", chooserCalls=")
			.Append(chooserCalls)
			.Append(", chooserTotalMs=")
			.Append(num.ToString("F2"))
			.Append(", chooserMaxMs=")
			.Append(num2.ToString("F2"))
			.Append(", chooser>=100ms=")
			.Append(chooserCatastrophic)
			.Append(", canRunCalls=")
			.Append(canRunCalls)
			.Append(", canRunTotalMs=")
			.Append(num3.ToString("F2"))
			.Append(", canRun>=5/20/100ms=")
			.Append(canRunSlow5)
			.Append('/')
			.Append(canRunSlow20)
			.Append('/')
			.Append(canRunSlow100)
			.Append(", failures=")
			.Append(failures)
			.Append(". Measurement-only; active only inside NaturalRandomQuestChooser; results are never altered.");
		List<QuestStats> list = new List<QuestStats>(stats.Values);
		list.Sort(delegate(QuestStats a, QuestStats b)
		{
			int num14 = b.MaxTicks.CompareTo(a.MaxTicks);
			return (num14 == 0) ? b.TotalTicks.CompareTo(a.TotalTicks) : num14;
		});
		int num4 = Math.Min(24, list.Count);
		for (int num5 = 0; num5 < num4; num5++)
		{
			QuestStats questStats = list[num5];
			double num6 = ToMs(questStats.TotalTicks);
			double num7 = ToMs(questStats.MaxTicks);
			double num8 = ((questStats.Calls == 0L) ? 0.0 : (num6 / (double)questStats.Calls));
			stringBuilder.AppendLine();
			stringBuilder.Append("  #").Append(num5 + 1).Append(" QuestCanRun:")
				.Append(questStats.DefName)
				.Append(" | mod=")
				.Append(questStats.ModName)
				.Append(" | package=")
				.Append(questStats.PackageId)
				.Append(" | calls=")
				.Append(questStats.Calls)
				.Append(" | totalMs=")
				.Append(num6.ToString("F2"))
				.Append(" | avgMs=")
				.Append(num8.ToString("F3"))
				.Append(" | maxMs=")
				.Append(num7.ToString("F3"))
				.Append(" | true/false/ex=")
				.Append(questStats.TrueCount)
				.Append('/')
				.Append(questStats.FalseCount)
				.Append('/')
				.Append(questStats.Exceptions);
		}
		for (int num9 = 0; num9 < recentCatastrophes.Count; num9++)
		{
			Catastrophe catastrophe = recentCatastrophes[num9];
			double num10 = ToMs(catastrophe.TotalTicks);
			double num11 = ToMs(catastrophe.CanRunTicks);
			double num12 = Math.Max(0.0, num10 - num11);
			stringBuilder.AppendLine();
			stringBuilder.Append("  CAT#").Append(num9 + 1).Append(": tick=")
				.Append(catastrophe.Tick)
				.Append(", totalMs=")
				.Append(num10.ToString("F2"))
				.Append(", canRunMs=")
				.Append(num11.ToString("F2"))
				.Append(", residualMs=")
				.Append(num12.ToString("F2"))
				.Append(", canRunCalls=")
				.Append(catastrophe.CanRunCalls)
				.Append(", exception=")
				.Append(catastrophe.Exception)
				.Append(", slow=");
			if (catastrophe.SlowCalls == null || catastrophe.SlowCalls.Count == 0)
			{
				stringBuilder.Append("<none>=5ms");
				continue;
			}
			for (int num13 = 0; num13 < catastrophe.SlowCalls.Count; num13++)
			{
				if (num13 != 0)
				{
					stringBuilder.Append(" ; ");
				}
				SlowQuestCall slowQuestCall = catastrophe.SlowCalls[num13];
				stringBuilder.Append(slowQuestCall.DefName).Append('@').Append(slowQuestCall.PackageId)
					.Append(':')
					.Append(ToMs(slowQuestCall.ElapsedTicks).ToString("F2"))
					.Append("ms")
					.Append(slowQuestCall.Exception ? "[EX]" : (slowQuestCall.Result ? "[T]" : "[F]"));
			}
		}
		return stringBuilder.ToString();
	}

	private static double ToMs(long ticks)
	{
		return (double)ticks * 1000.0 / (double)Stopwatch.Frequency;
	}
}
