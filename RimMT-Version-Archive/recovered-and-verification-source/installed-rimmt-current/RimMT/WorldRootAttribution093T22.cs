using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;

namespace RimMT;

internal static class WorldRootAttribution093T22
{
	internal sealed class WorldTickContext
	{
		internal long StartWarnings;

		internal long StartPawnGen;

		internal long StartWtlGlobal;

		internal long StartWtlNarrow;

		internal string TopComponent;

		internal double TopComponentMs;
	}

	internal sealed class ComponentStat
	{
		internal readonly string Name;

		internal long Calls;

		internal long TotalTicks;

		internal long MaxTicks;

		internal ComponentStat(string name)
		{
			Name = name;
		}
	}

	internal sealed class WarningStat
	{
		internal readonly string Text;

		internal long Calls;

		internal long TotalTicks;

		internal long MaxTicks;

		internal WarningStat(string text)
		{
			Text = text;
		}
	}

	internal struct WarningCallState
	{
		internal long Start;

		internal string Text;
	}

	private static readonly double TickToMs = 1000.0 / (double)Stopwatch.Frequency;

	private const double CatastropheMs = 50.0;

	private const int RecentCapacity = 12;

	private const int WarningKeyCapacity = 32;

	private static readonly object sync = new object();

	private static readonly Dictionary<string, ComponentStat> componentStats = new Dictionary<string, ComponentStat>();

	private static readonly Dictionary<string, WarningStat> warningStats = new Dictionary<string, WarningStat>();

	private static readonly Queue<string> recentWorldTails = new Queue<string>();

	private static bool installed;

	private static int installFailures;

	private static int componentMethodsPatched;

	private static int pawnGeneratorMethodsPatched;

	private static long worldTickCalls;

	private static long worldTickTotalTicks;

	private static long worldTickMaxTicks;

	private static long worldComponentUtilityCalls;

	private static long worldComponentUtilityTotalTicks;

	private static long worldComponentUtilityMaxTicks;

	private static long pawnGeneratorCalls;

	private static long pawnGeneratorTotalTicks;

	private static long pawnGeneratorMaxTicks;

	private static long warningCalls;

	private static long warningTotalTicks;

	private static long warningMaxTicks;

	private static int warningCurrentTick = int.MinValue;

	private static int warningCurrentTickCount;

	private static int warningMaxPerTick;

	private static int warningMaxTick = -1;

	private static long traitDbAdds;

	private static long traitDbRemoves;

	private static long traitDbSetIndicesCalls;

	private static long traitDbSetIndicesTicks;

	private static bool wtlDetected;

	private static bool wtlEnsurePatched;

	private static bool wtlNarrowAuthoritySafe;

	private static bool wtlGlobalInitPatched;

	private static int wtlForeignPatches;

	private static MethodInfo wtlTraitInitialize;

	private static MethodInfo wtlTraitApplyOverrides;

	private static FieldInfo wtlTraitLevels;

	private static long wtlEnsureCalls;

	private static long wtlMismatchEntries;

	private static long wtlNarrowRebuilds;

	private static long wtlNarrowFailures;

	private static long wtlPostconditionFailures;

	private static long wtlGlobalInitCalls;

	private static long wtlGlobalInitTicks;

	private static long wtlGlobalInitMaxTicks;

	private static int wtlLastDefsCount;

	private static int wtlLastLevelsCount;

	[ThreadStatic]
	private static WorldTickContext currentWorldTick;

	[ThreadStatic]
	private static int pawnGenerationDepth;

	internal static void Apply(Harmony harmony)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0060: Unknown result type (might be due to invalid IL or missing references)
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0082: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Expected O, but got Unknown
		//IL_008e: Expected O, but got Unknown
		//IL_008e: Expected O, but got Unknown
		//IL_00e1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Expected O, but got Unknown
		//IL_0102: Expected O, but got Unknown
		if (installed)
		{
			return;
		}
		installed = true;
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(World), "WorldTick", (Type[])null, (Type[])null);
			if (methodBase != null)
			{
				harmony.Patch(methodBase, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldTickPrefix", (Type[])null)
				{
					priority = 800
				}, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldTickPostfix", (Type[])null)
				{
					priority = 0
				}, (HarmonyMethod)null, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldTickFinalizer", (Type[])null)
				{
					priority = 0
				});
			}
			else
			{
				installFailures++;
			}
			MethodBase methodBase2 = AccessTools.Method(typeof(WorldComponentUtility), "WorldComponentTick", new Type[1] { typeof(World) }, (Type[])null);
			if (methodBase2 != null)
			{
				harmony.Patch(methodBase2, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldComponentUtilityPrefix", (Type[])null), new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldComponentUtilityPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
			}
			else
			{
				installFailures++;
			}
			PatchWorldComponentOverrides(harmony);
			PatchPawnGenerator(harmony);
			PatchWarningAggregator(harmony);
			PatchWorldTechLevel(harmony);
			Log.Message("[RimMT] T22 world-root attribution active. Direct WorldTick/world-component timing installed; WorldTechLevel TraitDef narrow-rebuild guard detected=" + wtlDetected + ", authoritySafe=" + wtlNarrowAuthoritySafe + ".");
		}
		catch (Exception ex)
		{
			installFailures++;
			Log.Warning("[RimMT] T22 world-root attribution failed partially and will fail open: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void PatchWorldComponentOverrides(Harmony harmony)
	{
		//IL_00d2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e7: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Expected O, but got Unknown
		//IL_00f3: Expected O, but got Unknown
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
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
				if (type == null || type.IsAbstract || !typeof(WorldComponent).IsAssignableFrom(type))
				{
					continue;
				}
				MethodInfo method;
				try
				{
					method = type.GetMethod("WorldComponentTick", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
				}
				catch
				{
					continue;
				}
				if (!(method == null) && !(method.DeclaringType == typeof(WorldComponent)) && hashSet.Add(method))
				{
					try
					{
						harmony.Patch((MethodBase)method, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldComponentPrefix", (Type[])null), new HarmonyMethod(typeof(WorldRootAttribution093T22), "WorldComponentPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
						componentMethodsPatched++;
					}
					catch
					{
						installFailures++;
					}
				}
			}
		}
	}

	private static void PatchPawnGenerator(Harmony harmony)
	{
		//IL_0059: Unknown result type (might be due to invalid IL or missing references)
		//IL_006e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Expected O, but got Unknown
		//IL_008e: Expected O, but got Unknown
		//IL_008e: Expected O, but got Unknown
		MethodInfo[] methods = typeof(PawnGenerator).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (MethodInfo methodInfo in methods)
		{
			if (!(methodInfo.Name != "GeneratePawn") && !(methodInfo.ReturnType != typeof(Pawn)))
			{
				try
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(WorldRootAttribution093T22), "PawnGeneratePrefix", (Type[])null), new HarmonyMethod(typeof(WorldRootAttribution093T22), "PawnGeneratePostfix", (Type[])null), (HarmonyMethod)null, new HarmonyMethod(typeof(WorldRootAttribution093T22), "PawnGenerateFinalizer", (Type[])null));
					pawnGeneratorMethodsPatched++;
				}
				catch
				{
					installFailures++;
				}
			}
		}
	}

	private static void PatchWarningAggregator(Harmony harmony)
	{
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_0071: Unknown result type (might be due to invalid IL or missing references)
		//IL_0076: Unknown result type (might be due to invalid IL or missing references)
		//IL_0084: Expected O, but got Unknown
		//IL_0084: Expected O, but got Unknown
		MethodBase methodBase = AccessTools.Method(typeof(Log), "Warning", new Type[1] { typeof(string) }, (Type[])null);
		if (methodBase == null)
		{
			installFailures++;
			return;
		}
		harmony.Patch(methodBase, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WarningPrefix", (Type[])null)
		{
			priority = 800
		}, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WarningPostfix", (Type[])null)
		{
			priority = 0
		}, (HarmonyMethod)null, (HarmonyMethod)null);
	}

	private static void PatchTraitDefDatabaseSignals(Harmony harmony)
	{
		Type typeFromHandle = typeof(DefDatabase<TraitDef>);
		TryPatchTraitSignal(harmony, typeFromHandle, "SetIndices", Type.EmptyTypes, "TraitSetIndicesPrefix", "TraitSetIndicesPostfix");
		TryPatchTraitSignal(harmony, typeFromHandle, "Add", new Type[1] { typeof(TraitDef) }, "TraitAddPrefix", null);
		TryPatchTraitSignal(harmony, typeFromHandle, "Remove", new Type[1] { typeof(TraitDef) }, "TraitRemovePrefix", null);
	}

	private static void TryPatchTraitSignal(Harmony harmony, Type db, string name, Type[] args, string prefixName, string postfixName)
	{
		try
		{
			MethodBase method = AccessTools.Method(db, name, args, (Type[])null);
			PatchOptionalNoArgOrAny(harmony, method, prefixName, postfixName);
		}
		catch
		{
			installFailures++;
		}
	}

	private static void PatchOptionalNoArgOrAny(Harmony harmony, MethodBase method, string prefixName, string postfixName)
	{
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		if (method == null)
		{
			return;
		}
		try
		{
			HarmonyMethod val = ((prefixName == null) ? ((HarmonyMethod)null) : new HarmonyMethod(typeof(WorldRootAttribution093T22), prefixName, (Type[])null));
			HarmonyMethod val2 = ((postfixName == null) ? ((HarmonyMethod)null) : new HarmonyMethod(typeof(WorldRootAttribution093T22), postfixName, (Type[])null));
			harmony.Patch(method, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
		}
		catch
		{
			installFailures++;
		}
	}

	private static void PatchWorldTechLevel(Harmony harmony)
	{
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_007b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0087: Expected O, but got Unknown
		//IL_0087: Expected O, but got Unknown
		try
		{
			Type type = AccessTools.TypeByName("WorldTechLevel.DefTechLevels");
			if (!(type == null))
			{
				wtlDetected = true;
				wtlNarrowAuthoritySafe = false;
				wtlEnsurePatched = false;
				wtlTraitInitialize = null;
				wtlTraitApplyOverrides = null;
				wtlTraitLevels = null;
				MethodInfo methodInfo = AccessTools.Method(type, "Initialize", (Type[])null, (Type[])null);
				if (methodInfo != null)
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(WorldRootAttribution093T22), "WtlGlobalInitPrefix", (Type[])null), new HarmonyMethod(typeof(WorldRootAttribution093T22), "WtlGlobalInitPostfix", (Type[])null), (HarmonyMethod)null, (HarmonyMethod)null);
					wtlGlobalInitPatched = true;
				}
			}
		}
		catch
		{
			wtlNarrowAuthoritySafe = false;
			installFailures++;
		}
	}

	private static bool IsAuthoritySafeForNarrowWtl(MethodBase ensure)
	{
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(ensure);
			if (patchInfo == null)
			{
				return true;
			}
			int num = 0;
			foreach (Patch prefix in patchInfo.Prefixes)
			{
				if (prefix != null && prefix.owner != "allen.rimmt")
				{
					num++;
				}
			}
			foreach (Patch postfix in patchInfo.Postfixes)
			{
				if (postfix != null && postfix.owner != "allen.rimmt")
				{
					num++;
				}
			}
			foreach (Patch transpiler in patchInfo.Transpilers)
			{
				if (transpiler != null && transpiler.owner != "allen.rimmt")
				{
					num++;
				}
			}
			foreach (Patch finalizer in patchInfo.Finalizers)
			{
				if (finalizer != null && finalizer.owner != "allen.rimmt")
				{
					num++;
				}
			}
			wtlForeignPatches = num;
			return num == 0;
		}
		catch
		{
			return false;
		}
	}

	public static void WorldTickPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
		currentWorldTick = new WorldTickContext
		{
			StartWarnings = Interlocked.Read(ref warningCalls),
			StartPawnGen = Interlocked.Read(ref pawnGeneratorCalls),
			StartWtlGlobal = Interlocked.Read(ref wtlGlobalInitCalls),
			StartWtlNarrow = Interlocked.Read(ref wtlNarrowRebuilds)
		};
	}

	public static void WorldTickPostfix(long __state)
	{
		FinishWorldTick(__state, null);
	}

	public static Exception WorldTickFinalizer(Exception __exception, long __state)
	{
		if (__exception != null)
		{
			FinishWorldTick(__state, __exception);
		}
		return __exception;
	}

	private static void FinishWorldTick(long start, Exception exception)
	{
		if (start == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - start;
		Interlocked.Increment(ref worldTickCalls);
		Interlocked.Add(ref worldTickTotalTicks, num);
		Max(ref worldTickMaxTicks, num);
		WorldTickContext worldTickContext = currentWorldTick;
		currentWorldTick = null;
		if (worldTickContext == null)
		{
			return;
		}
		double num2 = (double)num * TickToMs;
		if (num2 < 50.0 && exception == null)
		{
			return;
		}
		long num3 = Interlocked.Read(ref warningCalls) - worldTickContext.StartWarnings;
		long num4 = Interlocked.Read(ref pawnGeneratorCalls) - worldTickContext.StartPawnGen;
		long num5 = Interlocked.Read(ref wtlGlobalInitCalls) - worldTickContext.StartWtlGlobal;
		long num6 = Interlocked.Read(ref wtlNarrowRebuilds) - worldTickContext.StartWtlNarrow;
		int num7 = SafeTick();
		string item = "tick=" + num7 + ", worldMs=" + num2.ToString("F2") + ", topComponent=" + (worldTickContext.TopComponent ?? "none") + ":" + worldTickContext.TopComponentMs.ToString("F2") + "ms, warnings=" + num3 + ", pawnGen=" + num4 + ", wtlGlobalInit=" + num5 + ", wtlNarrow=" + num6 + ((exception == null) ? "" : (", exception=" + exception.GetType().Name));
		lock (sync)
		{
			while (recentWorldTails.Count >= 12)
			{
				recentWorldTails.Dequeue();
			}
			recentWorldTails.Enqueue(item);
		}
	}

	public static void WorldComponentUtilityPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void WorldComponentUtilityPostfix(long __state)
	{
		if (__state != 0L)
		{
			long value = Stopwatch.GetTimestamp() - __state;
			Interlocked.Increment(ref worldComponentUtilityCalls);
			Interlocked.Add(ref worldComponentUtilityTotalTicks, value);
			Max(ref worldComponentUtilityMaxTicks, value);
		}
	}

	public static void WorldComponentPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void WorldComponentPostfix(MethodBase __originalMethod, long __state)
	{
		if (__state == 0L || __originalMethod == null)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - __state;
		string text = ((__originalMethod.DeclaringType == null) ? "<unknown>" : __originalMethod.DeclaringType.FullName);
		lock (sync)
		{
			if (!componentStats.TryGetValue(text, out var value))
			{
				value = new ComponentStat(text);
				componentStats.Add(text, value);
			}
			value.Calls++;
			value.TotalTicks += num;
			if (num > value.MaxTicks)
			{
				value.MaxTicks = num;
			}
		}
		WorldTickContext worldTickContext = currentWorldTick;
		if (worldTickContext != null)
		{
			double num2 = (double)num * TickToMs;
			if (num2 > worldTickContext.TopComponentMs)
			{
				worldTickContext.TopComponentMs = num2;
				worldTickContext.TopComponent = text;
			}
		}
	}

	public static void PawnGeneratePrefix(ref long __state)
	{
		pawnGenerationDepth++;
		__state = ((pawnGenerationDepth == 1) ? Stopwatch.GetTimestamp() : 0);
	}

	public static void PawnGeneratePostfix(long __state)
	{
		FinishPawnGenerate(__state);
	}

	public static Exception PawnGenerateFinalizer(Exception __exception, long __state)
	{
		if (__exception != null)
		{
			FinishPawnGenerate(__state);
		}
		return __exception;
	}

	private static void FinishPawnGenerate(long start)
	{
		try
		{
			if (start != 0L)
			{
				long value = Stopwatch.GetTimestamp() - start;
				Interlocked.Increment(ref pawnGeneratorCalls);
				Interlocked.Add(ref pawnGeneratorTotalTicks, value);
				Max(ref pawnGeneratorMaxTicks, value);
			}
		}
		finally
		{
			if (pawnGenerationDepth > 0)
			{
				pawnGenerationDepth--;
			}
		}
	}

	public static void WarningPrefix(string text, ref WarningCallState __state)
	{
		__state = new WarningCallState
		{
			Start = Stopwatch.GetTimestamp(),
			Text = text
		};
	}

	public static void WarningPostfix(WarningCallState __state)
	{
		if (__state.Start == 0L)
		{
			return;
		}
		long num = Stopwatch.GetTimestamp() - __state.Start;
		Interlocked.Increment(ref warningCalls);
		Interlocked.Add(ref warningTotalTicks, num);
		Max(ref warningMaxTicks, num);
		int num2 = SafeTick();
		lock (sync)
		{
			if (num2 != warningCurrentTick)
			{
				if (warningCurrentTickCount > warningMaxPerTick)
				{
					warningMaxPerTick = warningCurrentTickCount;
					warningMaxTick = warningCurrentTick;
				}
				warningCurrentTick = num2;
				warningCurrentTickCount = 0;
			}
			warningCurrentTickCount++;
			string text = NormalizeWarning(__state.Text);
			if (warningStats.TryGetValue(text, out var value))
			{
				value.Calls++;
				value.TotalTicks += num;
				if (num > value.MaxTicks)
				{
					value.MaxTicks = num;
				}
			}
			else if (warningStats.Count < 32)
			{
				value = new WarningStat(text)
				{
					Calls = 1L,
					TotalTicks = num,
					MaxTicks = num
				};
				warningStats.Add(text, value);
			}
		}
	}

	public static void TraitSetIndicesPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void TraitSetIndicesPostfix(long __state)
	{
		Interlocked.Increment(ref traitDbSetIndicesCalls);
		if (__state != 0L)
		{
			Interlocked.Add(ref traitDbSetIndicesTicks, Stopwatch.GetTimestamp() - __state);
		}
	}

	public static void TraitAddPrefix()
	{
		Interlocked.Increment(ref traitDbAdds);
	}

	public static void TraitRemovePrefix()
	{
		Interlocked.Increment(ref traitDbRemoves);
	}

	public static bool WtlTraitEnsurePrefix()
	{
		Interlocked.Increment(ref wtlEnsureCalls);
		if (!wtlNarrowAuthoritySafe || wtlTraitLevels == null || wtlTraitInitialize == null || wtlTraitApplyOverrides == null)
		{
			return true;
		}
		try
		{
			int count = DefDatabase<TraitDef>.AllDefsListForReading.Count;
			int num = ((wtlTraitLevels.GetValue(null) is Array array) ? array.Length : 0);
			wtlLastDefsCount = count;
			wtlLastLevelsCount = num;
			if (num <= 0 || count == num)
			{
				return true;
			}
			Interlocked.Increment(ref wtlMismatchEntries);
			if (wtlTraitInitialize.GetParameters().Length == 0)
			{
				wtlTraitInitialize.Invoke(null, null);
			}
			else
			{
				wtlTraitInitialize.Invoke(null, new object[1]);
			}
			wtlTraitApplyOverrides.Invoke(null, null);
			if ((wtlLastLevelsCount = ((wtlTraitLevels.GetValue(null) is Array array2) ? array2.Length : 0)) != count)
			{
				Interlocked.Increment(ref wtlPostconditionFailures);
				return true;
			}
			Interlocked.Increment(ref wtlNarrowRebuilds);
			return false;
		}
		catch
		{
			Interlocked.Increment(ref wtlNarrowFailures);
			return true;
		}
	}

	public static void WtlGlobalInitPrefix(ref long __state)
	{
		__state = Stopwatch.GetTimestamp();
	}

	public static void WtlGlobalInitPostfix(long __state)
	{
		if (__state != 0L)
		{
			long value = Stopwatch.GetTimestamp() - __state;
			Interlocked.Increment(ref wtlGlobalInitCalls);
			Interlocked.Add(ref wtlGlobalInitTicks, value);
			Max(ref wtlGlobalInitMaxTicks, value);
		}
	}

	internal static string Summary()
	{
		List<ComponentStat> list;
		List<WarningStat> list2;
		string[] array;
		lock (sync)
		{
			if (warningCurrentTickCount > warningMaxPerTick)
			{
				warningMaxPerTick = warningCurrentTickCount;
				warningMaxTick = warningCurrentTick;
			}
			list = new List<ComponentStat>(componentStats.Values);
			list2 = new List<WarningStat>(warningStats.Values);
			array = recentWorldTails.ToArray();
		}
		list.Sort((ComponentStat a, ComponentStat b) => b.MaxTicks.CompareTo(a.MaxTicks));
		list2.Sort((WarningStat a, WarningStat b) => b.Calls.CompareTo(a.Calls));
		StringBuilder stringBuilder = new StringBuilder(4096);
		stringBuilder.Append("T22 world-root attribution: installed=").Append(installed).Append(", installFailures=")
			.Append(installFailures)
			.Append(", componentMethods=")
			.Append(componentMethodsPatched)
			.Append(", pawnGeneratorMethods=")
			.Append(pawnGeneratorMethodsPatched)
			.Append(", WorldTick[calls=")
			.Append(Interlocked.Read(ref worldTickCalls))
			.Append(",totalMs=")
			.Append(((double)Interlocked.Read(ref worldTickTotalTicks) * TickToMs).ToString("F2"))
			.Append(",maxMs=")
			.Append(((double)Interlocked.Read(ref worldTickMaxTicks) * TickToMs).ToString("F2"))
			.Append(']')
			.Append(", WorldComponentUtility[calls=")
			.Append(Interlocked.Read(ref worldComponentUtilityCalls))
			.Append(",totalMs=")
			.Append(((double)Interlocked.Read(ref worldComponentUtilityTotalTicks) * TickToMs).ToString("F2"))
			.Append(",maxMs=")
			.Append(((double)Interlocked.Read(ref worldComponentUtilityMaxTicks) * TickToMs).ToString("F2"))
			.Append(']')
			.Append(", PawnGenerator[calls=")
			.Append(Interlocked.Read(ref pawnGeneratorCalls))
			.Append(",totalMs=")
			.Append(((double)Interlocked.Read(ref pawnGeneratorTotalTicks) * TickToMs).ToString("F2"))
			.Append(",maxMs=")
			.Append(((double)Interlocked.Read(ref pawnGeneratorMaxTicks) * TickToMs).ToString("F2"))
			.Append(']')
			.Append(", warnings[calls=")
			.Append(Interlocked.Read(ref warningCalls))
			.Append(",totalMs=")
			.Append(((double)Interlocked.Read(ref warningTotalTicks) * TickToMs).ToString("F2"))
			.Append(",maxMs=")
			.Append(((double)Interlocked.Read(ref warningMaxTicks) * TickToMs).ToString("F2"))
			.Append(",maxPerTick=")
			.Append(warningMaxPerTick)
			.Append("@tick=")
			.Append(warningMaxTick)
			.Append(']')
			.Append(", TraitDefDB[add=")
			.Append(Interlocked.Read(ref traitDbAdds))
			.Append(",remove=")
			.Append(Interlocked.Read(ref traitDbRemoves))
			.Append(",setIndices=")
			.Append(Interlocked.Read(ref traitDbSetIndicesCalls))
			.Append(",setIndicesMs=")
			.Append(((double)Interlocked.Read(ref traitDbSetIndicesTicks) * TickToMs).ToString("F2"))
			.Append(']')
			.AppendLine();
		stringBuilder.Append("T22 WorldTechLevel bridge: detected=").Append(wtlDetected).Append(", ensurePatched=")
			.Append(wtlEnsurePatched)
			.Append(", narrowAuthoritySafe=")
			.Append(wtlNarrowAuthoritySafe)
			.Append(", foreignPatches=")
			.Append(wtlForeignPatches)
			.Append(", globalInitPatched=")
			.Append(wtlGlobalInitPatched)
			.Append(", ensureCalls=")
			.Append(Interlocked.Read(ref wtlEnsureCalls))
			.Append(", mismatchEntries=")
			.Append(Interlocked.Read(ref wtlMismatchEntries))
			.Append(", narrowRebuilds=")
			.Append(Interlocked.Read(ref wtlNarrowRebuilds))
			.Append(", narrowFailures=")
			.Append(Interlocked.Read(ref wtlNarrowFailures))
			.Append(", postconditionFailures=")
			.Append(Interlocked.Read(ref wtlPostconditionFailures))
			.Append(", globalInitCalls=")
			.Append(Interlocked.Read(ref wtlGlobalInitCalls))
			.Append(", globalInitTotalMs=")
			.Append(((double)Interlocked.Read(ref wtlGlobalInitTicks) * TickToMs).ToString("F2"))
			.Append(", globalInitMaxMs=")
			.Append(((double)Interlocked.Read(ref wtlGlobalInitMaxTicks) * TickToMs).ToString("F2"))
			.Append(", lastDefs/levels=")
			.Append(wtlLastDefsCount)
			.Append('/')
			.Append(wtlLastLevelsCount)
			.AppendLine(". Narrow rebuild touches only TechLevelDatabase<TraitDef>.Initialize + ApplyOverrides; any failure falls back to WorldTechLevel original.");
		int num = Math.Min(8, list.Count);
		for (int num2 = 0; num2 < num; num2++)
		{
			ComponentStat componentStat = list[num2];
			stringBuilder.Append("  WorldComp#").Append(num2 + 1).Append(' ')
				.Append(componentStat.Name)
				.Append(": calls=")
				.Append(componentStat.Calls)
				.Append(", totalMs=")
				.Append(((double)componentStat.TotalTicks * TickToMs).ToString("F2"))
				.Append(", maxMs=")
				.Append(((double)componentStat.MaxTicks * TickToMs).ToString("F2"))
				.AppendLine();
		}
		int num3 = Math.Min(6, list2.Count);
		for (int num4 = 0; num4 < num3; num4++)
		{
			WarningStat warningStat = list2[num4];
			stringBuilder.Append("  Warning#").Append(num4 + 1).Append(": calls=")
				.Append(warningStat.Calls)
				.Append(", totalMs=")
				.Append(((double)warningStat.TotalTicks * TickToMs).ToString("F2"))
				.Append(", maxMs=")
				.Append(((double)warningStat.MaxTicks * TickToMs).ToString("F2"))
				.Append(", text=")
				.Append(warningStat.Text)
				.AppendLine();
		}
		for (int num5 = 0; num5 < array.Length; num5++)
		{
			stringBuilder.Append("  WORLDTAIL#").Append(num5 + 1).Append(": ")
				.AppendLine(array[num5]);
		}
		return stringBuilder.ToString().TrimEnd();
	}

	private static string NormalizeWarning(string text)
	{
		if (string.IsNullOrEmpty(text))
		{
			return "<empty>";
		}
		string text2 = text.Replace('\r', ' ').Replace('\n', ' ');
		if (text2.Length > 180)
		{
			return text2.Substring(0, 180);
		}
		return text2;
	}

	private static int SafeTick()
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

	private static void Max(ref long target, long value)
	{
		long num;
		do
		{
			num = Interlocked.Read(ref target);
		}
		while (value > num && Interlocked.CompareExchange(ref target, value, num) != num);
	}
}
