using System;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT;

[StaticConstructorOnStartup]
internal static class RimMTBootstrap
{
	internal const string HarmonyId = "allen.rimmt";

	internal const string Version = "0.9.3-t34d2-dobill-readiness-workers";

	internal static bool DispatcherBridgePatched { get; private set; }

	static RimMTBootstrap()
	{
		//IL_000f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		//IL_001a: Expected O, but got Unknown
		//IL_001a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0020: Expected O, but got Unknown
		//IL_0020: Unknown result type (might be due to invalid IL or missing references)
		//IL_0026: Expected O, but got Unknown
		//IL_002b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Expected O, but got Unknown
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Expected O, but got Unknown
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		//IL_003d: Expected O, but got Unknown
		//IL_003d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0043: Expected O, but got Unknown
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Expected O, but got Unknown
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Expected O, but got Unknown
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Expected O, but got Unknown
		//IL_0055: Unknown result type (might be due to invalid IL or missing references)
		//IL_005b: Expected O, but got Unknown
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0061: Expected O, but got Unknown
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_006c: Expected O, but got Unknown
		//IL_006c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Expected O, but got Unknown
		//IL_0072: Unknown result type (might be due to invalid IL or missing references)
		//IL_0078: Expected O, but got Unknown
		//IL_007d: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Expected O, but got Unknown
		//IL_0088: Expected O, but got Unknown
		try
		{
			RimMTThreadGuard.InitializeMainThread();
			RimMTRuntime.Initialize();
			Harmony val = new Harmony("allen.rimmt");
			TryPatchDispatcher(val);
			RimMTPatches.Apply(val);
			WorkGiverMergePartnerIndex093T4.Apply(val);
			HaulMergePatchCensus093T5.Apply();
			PathGridInvalidation.ApplyBulkGuard(val);
			JobSearchPackageContext093T28.Apply(val);
			CandidateFabric093T34A.Apply(val);
			ScannerParallelFabric093T34B.Apply(val);
			AggressiveParallelScanner093T34D.Apply(val);
			ReservationTransaction093T32A.Apply(val);
			BroadGenClosestOrder0418.Apply(val);
			JobGiverGlobalNearest04181.Apply(val);
			JobGiverSlowSearch0419S.Apply(val);
			StorytellerDeepAttribution093T18.Initialize();
			JobSearchTransaction093T20.Apply(val);
			GenClosestTransactionIndex093T22.Apply(val);
			SimulationEpochCoordinator093T26.Apply(val);
			WorkGiverParallelSafety093T27_2.Initialize();
			HaulWorkAccelerator.Apply(val);
			GlobalHaulAccelerator.Apply(val);
			Log.Message("[RimMT] V0.9.3-T34D.2 DoBill Readiness Workers initialized. Live unknown BillStack.AnyShouldDoNow checks from large DoBill sources execute across the worker pool; the main thread commits ordered results and package-local false memo state after all workers finish. Any worker exception falls back the whole call to the existing serial path. T34-D.1.3 Reachability and MapPawns guards, worker validator fast path and scanner quarantine remain active. T20/T21 main-thread transaction core retained; repeated package-local GenClosest_Global_NewTemp IList sources use a distance/source-order index; dispatcher drain moved from TickManagerUpdate to Root_Play.Update for SimplyMoreFPS coexistence; direct WorldTick boundary timing retained; WorldRoot attribution restored; ReachProfile Region.Allows capture moved out of CanReach into a bounded Root_Play frame queue. Single-DLL production mode: diagnostic hot-path probes, PathSnapshot shadow validation, SafePath telemetry and WorkPrefilter are not installed. T34-C.4 production paths are retained as the baseline; T34-D.2 adds measured live DoBill readiness work to the persistent worker pool.");
		}
		catch (Exception ex)
		{
			Log.Error("[RimMT] Consolidated Stable core initialization failed. RimMT will remain inert. " + ex);
		}
	}

	private static void TryPatchDispatcher(Harmony harmony)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_0055: Expected O, but got Unknown
		//IL_0065: Unknown result type (might be due to invalid IL or missing references)
		//IL_006a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0072: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(Root_Play), "Update", (Type[])null, (Type[])null);
			if (methodBase == null)
			{
				FeatureGate.Suppress("runtime.dispatcher", "Root_Play.Update was not found");
				return;
			}
			HarmonyMethod val = new HarmonyMethod(typeof(RimMTBootstrap), "RootPlayUpdatePrefix", (Type[])null)
			{
				priority = 800
			};
			HarmonyMethod val2 = new HarmonyMethod(typeof(RimMTBootstrap), "RootPlayUpdatePostfix", (Type[])null)
			{
				priority = 0
			};
			harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
			DispatcherBridgePatched = true;
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("runtime.dispatcher", "Root_Play dispatcher bridge failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] Root_Play dispatcher bridge failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void RootPlayUpdatePrefix(ref long __state)
	{
		__state = 0L;
		if (RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
		{
			__state = Stopwatch.GetTimestamp();
		}
	}

	public static void RootPlayUpdatePostfix(long __state)
	{
		if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
		{
			AdaptiveLoadBalancer.RecordButterFrameSlice(__state);
		}
		RimMTRuntime.OnMainThreadFrame();
	}
}
