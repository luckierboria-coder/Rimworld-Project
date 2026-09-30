using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class WorkGiverDetailPatches
{
	public struct InfrastructureStateT16
	{
		public long LegacyStarted;

		public GenClosestDeepAttribution093T16.Scope Deep;
	}

	private const int CaptureJobPackages = 64;

	private static readonly HashSet<string> TargetNames = new HashSet<string>(StringComparer.Ordinal) { "ShouldSkip", "NonScanJob", "HasJobOnThing", "HasJobOnCell", "JobOnThing", "JobOnCell", "GetPriority" };

	private static readonly List<MethodBase> CandidateMethods = new List<MethodBase>();

	private static readonly List<MethodBase> InfrastructureMethods = new List<MethodBase>();

	private static readonly object Sync = new object();

	private static Harmony harmony;

	private static MethodBase jobPackageTarget;

	private static bool candidatesDiscovered;

	private static int active;

	private static int stopRequested;

	private static int patchFailures;

	internal static bool CaptureActive => Volatile.Read(ref active) != 0;

	internal static int PackagesRemaining => WorkGiverProfiler.PackagesRemaining;

	internal static void Initialize(Harmony owner)
	{
		harmony = owner;
		FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: false);
		Log.Message("[RimMT] diagnostics.jobGiverDetail V0.4.8 is on-demand. Slow JobPackage traces now include bounded GenClosest, Reachability, RegionTraverser and scanner-enumeration timings; all temporary detours are removed after capture.");
	}

	internal static bool StartCapture()
	{
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f0: Expected O, but got Unknown
		//IL_00f2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Expected O, but got Unknown
		if (!RimMTThreadGuard.IsMainThread || harmony == null || CaptureActive)
		{
			return false;
		}
		lock (Sync)
		{
			if (CaptureActive)
			{
				return false;
			}
			if (!EnsureCandidates())
			{
				return false;
			}
			int num = 0;
			patchFailures = 0;
			MethodInfo prefixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), "Prefix", (Type[])null, (Type[])null);
			MethodInfo postfixMethod = AccessTools.Method(typeof(WorkGiverDetailPatches), "Postfix", (Type[])null, (Type[])null);
			MethodInfo prefixMethod2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "InfrastructurePrefix", (Type[])null, (Type[])null);
			MethodInfo postfixMethod2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "InfrastructurePostfix", (Type[])null, (Type[])null);
			MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiverDetailPatches), "JobPackagePrefix", (Type[])null, (Type[])null);
			MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "JobPackageFinalizer", (Type[])null, (Type[])null);
			try
			{
				HarmonyMethod val = new HarmonyMethod(methodInfo)
				{
					priority = 800
				};
				HarmonyMethod val2 = new HarmonyMethod(methodInfo2)
				{
					priority = 0
				};
				harmony.Patch(jobPackageTarget, val, (HarmonyMethod)null, (HarmonyMethod)null, val2);
			}
			catch (Exception ex)
			{
				FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: false);
				Log.Warning("[RimMT] JobGiver detail capture could not patch the package scope. Capture was not started. " + ex.GetType().Name + ": " + ex.Message);
				return false;
			}
			num += PatchMethods(CandidateMethods, prefixMethod, postfixMethod, "WorkGiver phase");
			num += PatchMethods(InfrastructureMethods, prefixMethod2, postfixMethod2, "infrastructure");
			Interlocked.Exchange(ref stopRequested, 0);
			Interlocked.Exchange(ref active, 1);
			FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: true);
			WorkGiverProfiler.StartSession(64, num, patchFailures);
			Log.Message("[RimMT] T15 JobGiver detail capture started for up to " + 64 + " outer TryIssueJobPackage calls; temporarily patched " + CandidateMethods.Count + " WorkGiver phase candidates and " + InfrastructureMethods.Count + " infrastructure candidates. It will auto-unpatch when complete.");
			return true;
		}
	}

	internal static void RequestStopCapture()
	{
		if (CaptureActive)
		{
			Interlocked.Exchange(ref active, 0);
			FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: false);
			Interlocked.Exchange(ref stopRequested, 1);
		}
	}

	internal static void OnMainThreadFrame()
	{
		if (RimMTThreadGuard.IsMainThread && Interlocked.Exchange(ref stopRequested, 0) != 0)
		{
			StopCaptureNow();
		}
	}

	private static int PatchMethods(List<MethodBase> methods, MethodInfo prefixMethod, MethodInfo postfixMethod, string kind)
	{
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		//IL_0017: Unknown result type (might be due to invalid IL or missing references)
		//IL_0023: Expected O, but got Unknown
		//IL_0024: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		//IL_0032: Expected O, but got Unknown
		int num = 0;
		for (int i = 0; i < methods.Count; i++)
		{
			MethodBase methodBase = methods[i];
			try
			{
				HarmonyMethod val = new HarmonyMethod(prefixMethod)
				{
					priority = 800
				};
				HarmonyMethod val2 = new HarmonyMethod(postfixMethod)
				{
					priority = 0
				};
				harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
				num++;
			}
			catch (Exception ex)
			{
				patchFailures++;
				if (patchFailures <= 8)
				{
					Log.Warning("[RimMT] JobGiver detail capture skipped " + kind + " method " + methodBase?.ToString() + ": " + ex.GetType().Name + ": " + ex.Message);
				}
			}
		}
		return num;
	}

	private static void StopCaptureNow()
	{
		lock (Sync)
		{
			if (harmony == null)
			{
				return;
			}
			MethodInfo prefix = AccessTools.Method(typeof(WorkGiverDetailPatches), "Prefix", (Type[])null, (Type[])null);
			MethodInfo postfix = AccessTools.Method(typeof(WorkGiverDetailPatches), "Postfix", (Type[])null, (Type[])null);
			MethodInfo prefix2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "InfrastructurePrefix", (Type[])null, (Type[])null);
			MethodInfo postfix2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "InfrastructurePostfix", (Type[])null, (Type[])null);
			MethodInfo methodInfo = AccessTools.Method(typeof(WorkGiverDetailPatches), "JobPackagePrefix", (Type[])null, (Type[])null);
			MethodInfo methodInfo2 = AccessTools.Method(typeof(WorkGiverDetailPatches), "JobPackageFinalizer", (Type[])null, (Type[])null);
			try
			{
				if (jobPackageTarget != null)
				{
					harmony.Unpatch(jobPackageTarget, methodInfo);
					harmony.Unpatch(jobPackageTarget, methodInfo2);
				}
				UnpatchMethods(CandidateMethods, prefix, postfix);
				UnpatchMethods(InfrastructureMethods, prefix2, postfix2);
			}
			catch (Exception ex)
			{
				Log.Warning("[RimMT] JobGiver detail capture unpatch encountered " + ex.GetType().Name + ": " + ex.Message + ". Gameplay remains vanilla-authoritative.");
			}
			WorkGiverProfiler.StopSession();
			FeatureGate.SetEnabled("diagnostics.jobGiverDetail", enabled: false);
			Log.Message("[RimMT] T15 JobGiver detail capture stopped and temporary detours were removed. " + WorkGiverProfiler.Summary(12) + "\n" + JobGiverInfrastructureProfiler.Summary(12));
		}
	}

	private static void UnpatchMethods(List<MethodBase> methods, MethodInfo prefix, MethodInfo postfix)
	{
		for (int i = 0; i < methods.Count; i++)
		{
			harmony.Unpatch(methods[i], prefix);
			harmony.Unpatch(methods[i], postfix);
		}
	}

	private static bool EnsureCandidates()
	{
		if (candidatesDiscovered)
		{
			if (jobPackageTarget != null)
			{
				return CandidateMethods.Count > 0;
			}
			return false;
		}
		candidatesDiscovered = true;
		try
		{
			jobPackageTarget = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage", new Type[2]
			{
				typeof(Pawn),
				typeof(JobIssueParams)
			}, (Type[])null);
			if (jobPackageTarget == null)
			{
				Log.Warning("[RimMT] JobGiver detail capture unavailable: JobGiver_Work.TryIssueJobPackage was not found.");
				return false;
			}
			DiscoverWorkGiverMethods();
			DiscoverInfrastructureMethods();
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] JobGiver detail candidate discovery failed: " + ex.GetType().Name + ": " + ex.Message);
			return false;
		}
		return CandidateMethods.Count > 0;
	}

	private static void DiscoverWorkGiverMethods()
	{
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		List<Type> allTypes = GenTypes.AllTypes;
		for (int i = 0; i < allTypes.Count; i++)
		{
			Type type = allTypes[i];
			if (type == null || !typeof(WorkGiver).IsAssignableFrom(type))
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
				if (!(methodInfo == null) && !methodInfo.IsAbstract && !methodInfo.ContainsGenericParameters && TargetNames.Contains(methodInfo.Name) && IsUsefulSignature(methodInfo) && hashSet.Add(methodInfo))
				{
					CandidateMethods.Add(methodInfo);
				}
			}
		}
	}

	private static void DiscoverInfrastructureMethods()
	{
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		AddNamedMethods(typeof(GenClosest), hashSet, (MethodInfo m) => m.Name.StartsWith("ClosestThing", StringComparison.Ordinal));
		AddNamedMethods(typeof(Reachability), hashSet, (MethodInfo m) => m.Name == "CanReach");
		Type type = AccessTools.TypeByName("Verse.RegionTraverser");
		if (type != null)
		{
			AddNamedMethods(type, hashSet, (MethodInfo m) => m.Name == "BreadthFirstTraverse");
		}
		List<Type> allTypes = GenTypes.AllTypes;
		for (int num = 0; num < allTypes.Count; num++)
		{
			Type type2 = allTypes[num];
			if (type2 == null || !typeof(WorkGiver_Scanner).IsAssignableFrom(type2))
			{
				continue;
			}
			MethodInfo[] methods;
			try
			{
				methods = type2.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			catch
			{
				continue;
			}
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo == null) && !methodInfo.IsAbstract && !methodInfo.ContainsGenericParameters && (!(methodInfo.Name != "get_PotentialWorkThingsGlobal") || !(methodInfo.Name != "get_PotentialWorkCellsGlobal")) && hashSet.Add(methodInfo))
				{
					InfrastructureMethods.Add(methodInfo);
				}
			}
		}
	}

	private static void AddNamedMethods(Type type, HashSet<MethodBase> unique, Predicate<MethodInfo> predicate)
	{
		MethodInfo[] methods = type.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
		foreach (MethodInfo methodInfo in methods)
		{
			if (!(methodInfo == null) && !methodInfo.IsAbstract && !methodInfo.ContainsGenericParameters && predicate(methodInfo) && unique.Add(methodInfo))
			{
				InfrastructureMethods.Add(methodInfo);
			}
		}
	}

	public static void JobPackagePrefix(Pawn __0, ref WorkGiverProfiler.JobPackageScope __state)
	{
		__state = WorkGiverProfiler.BeginJobPackage(__0);
	}

	public static Exception JobPackageFinalizer(Exception __exception, WorkGiverProfiler.JobPackageScope __state)
	{
		WorkGiverProfiler.EndJobPackage(__state);
		return __exception;
	}

	public static void Prefix(WorkGiver __instance, MethodBase __originalMethod, ref long __state)
	{
		WorkGiverProfiler.EnterCaller(__instance, __originalMethod);
		__state = WorkGiverProfiler.Begin();
	}

	public static void Postfix(WorkGiver __instance, MethodBase __originalMethod, long __state)
	{
		try
		{
			WorkGiverProfiler.Record(__instance, __originalMethod, __state);
		}
		finally
		{
			WorkGiverProfiler.ExitCaller();
		}
	}

	public static void InfrastructurePrefix(MethodBase __originalMethod, object[] __args, ref InfrastructureStateT16 __state)
	{
		__state.LegacyStarted = JobGiverInfrastructureProfiler.Begin();
		__state.Deep = GenClosestDeepAttribution093T16.Begin(__originalMethod, __args);
	}

	public static void InfrastructurePostfix(MethodBase __originalMethod, InfrastructureStateT16 __state)
	{
		JobGiverInfrastructureProfiler.Record(__originalMethod, __state.LegacyStarted);
		GenClosestDeepAttribution093T16.End(__state.Deep);
	}

	private static bool IsUsefulSignature(MethodInfo method)
	{
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Pawn))
		{
			return false;
		}
		if (method.Name == "GetPriority")
		{
			if (parameters.Length == 2)
			{
				return parameters[1].ParameterType == typeof(TargetInfo);
			}
			return false;
		}
		return true;
	}
}
