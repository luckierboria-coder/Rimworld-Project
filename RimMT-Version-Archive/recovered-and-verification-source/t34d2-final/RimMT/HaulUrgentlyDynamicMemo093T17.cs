using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class HaulUrgentlyDynamicMemo093T17
{
	private sealed class PackageMemo
	{
		internal readonly Type SourceType;

		internal readonly List<Thing> Items = new List<Thing>();

		internal bool Building;

		internal bool Complete;

		internal int ReuseSerial;

		internal PackageMemo(Type sourceType)
		{
			SourceType = sourceType;
		}
	}

	private sealed class RecordingEnumerable : IEnumerable<Thing>, IEnumerable
	{
		private readonly IEnumerable<Thing> source;

		private readonly PackageMemo memo;

		internal RecordingEnumerable(IEnumerable<Thing> source, PackageMemo memo)
		{
			this.source = source;
			this.memo = memo;
		}

		public IEnumerator<Thing> GetEnumerator()
		{
			if (memo.Complete)
			{
				wrapperReplayHits++;
				return memo.Items.GetEnumerator();
			}
			if (memo.Building)
			{
				reentrantBypass++;
				return source.GetEnumerator();
			}
			return Record().GetEnumerator();
		}

		IEnumerator IEnumerable.GetEnumerator()
		{
			return GetEnumerator();
		}

		private IEnumerable<Thing> Record()
		{
			memo.Building = true;
			memo.Complete = false;
			memo.Items.Clear();
			bool completed = false;
			try
			{
				foreach (Thing item in source)
				{
					memo.Items.Add(item);
					yield return item;
				}
				completed = true;
			}
			finally
			{
				RecordingEnumerable recordingEnumerable = this;
				recordingEnumerable.memo.Building = false;
				if (completed)
				{
					recordingEnumerable.memo.Complete = true;
					completedBuilds++;
					recordedItems += recordingEnumerable.memo.Items.Count;
					if (recordingEnumerable.memo.Items.Count > maxRecordedItems)
					{
						maxRecordedItems = recordingEnumerable.memo.Items.Count;
					}
				}
				else
				{
					recordingEnumerable.memo.Complete = false;
					recordingEnumerable.memo.Items.Clear();
					incompleteBuilds++;
				}
			}
		}
	}

	internal const string FeatureId = "ai.haulUrgentlyDynamicMemo";

	private const int ShadowSampleMask = 63;

	private const string TargetDeclaringType = "AllowTool.WorkGiver_HaulUrgently";

	private const string TargetIteratorMarker = "PotentialWorkThingsGlobal";

	[ThreadStatic]
	private static long currentScopeStart;

	[ThreadStatic]
	private static PackageMemo currentMemo;

	private static readonly Dictionary<Type, bool> AuthorityCache = new Dictionary<Type, bool>();

	private static volatile bool installed;

	private static volatile bool enabled = true;

	private static volatile bool runtimeQuarantined;

	private static int patchedMethods;

	private static long observed;

	private static long targetDetected;

	private static long authorityBypass;

	private static long wrappedFirstPass;

	private static long completedBuilds;

	private static long incompleteBuilds;

	private static long cacheHits;

	private static long wrapperReplayHits;

	private static long shadowSamples;

	private static long shadowMatches;

	private static long shadowMismatches;

	private static long shadowItems;

	private static long recordedItems;

	private static int maxRecordedItems;

	private static long reentrantBypass;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_003b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0053: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			int num = 0;
			foreach (MethodInfo methodInfo in methods)
			{
				if (IsSupportedOverload(methodInfo))
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(HaulUrgentlyDynamicMemo093T17), "Prefix", (Type[])null)
					{
						priority = 1000
					}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
					num++;
				}
			}
			patchedMethods = num;
			installed = num > 0;
			if (installed)
			{
				Log.Message("[RimMT] T17 HaulUrgently package-local dynamic-source memo active on " + num + " ClosestThingReachable overload(s); Vanilla search/validator/reachability remain authoritative.");
			}
		}
		catch (Exception ex)
		{
			installed = false;
			failures++;
			Log.Warning("[RimMT] T17 HaulUrgently dynamic-source memo install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void SetEnabled(bool value)
	{
		enabled = value;
	}

	private static bool IsSupportedOverload(MethodInfo method)
	{
		if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable")
		{
			return false;
		}
		ParameterInfo[] parameters = method.GetParameters();
		if (parameters.Length >= 8)
		{
			return typeof(IEnumerable<Thing>).IsAssignableFrom(parameters[7].ParameterType);
		}
		return false;
	}

	public static void Prefix(ref IEnumerable<Thing> __7)
	{
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Invalid comparison between Unknown and I4
		observed++;
		if (!enabled || runtimeQuarantined || __7 == null || !JobGiverGlobalNearest04181.InJobGiverScope || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return;
		}
		Type type = __7.GetType();
		if (!IsTargetSource(type))
		{
			return;
		}
		targetDetected++;
		if (!IsAuthoritySafe(type))
		{
			authorityBypass++;
			return;
		}
		long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
		if (currentScopeStartTicks <= 0)
		{
			return;
		}
		if (currentScopeStartTicks != currentScopeStart)
		{
			currentScopeStart = currentScopeStartTicks;
			currentMemo = null;
		}
		PackageMemo packageMemo = currentMemo;
		if (packageMemo == null || packageMemo.SourceType != type)
		{
			packageMemo = (currentMemo = new PackageMemo(type));
		}
		if (packageMemo.Complete)
		{
			packageMemo.ReuseSerial++;
			if ((packageMemo.ReuseSerial & 0x3F) == 0)
			{
				List<Thing> list = MaterializeFresh(__7);
				shadowSamples++;
				shadowItems += list.Count;
				if (!SameReferenceOrder(packageMemo.Items, list))
				{
					shadowMismatches++;
					runtimeQuarantined = true;
					__7 = list;
				}
				else
				{
					shadowMatches++;
					__7 = list;
				}
			}
			else
			{
				cacheHits++;
				__7 = packageMemo.Items;
			}
		}
		else if (packageMemo.Building)
		{
			reentrantBypass++;
		}
		else
		{
			wrappedFirstPass++;
			__7 = new RecordingEnumerable(__7, packageMemo);
		}
	}

	private static bool IsTargetSource(Type sourceType)
	{
		if (sourceType == null)
		{
			return false;
		}
		string text = sourceType.FullName ?? sourceType.Name;
		if (text.StartsWith("AllowTool.WorkGiver_HaulUrgently+", StringComparison.Ordinal))
		{
			return text.IndexOf("PotentialWorkThingsGlobal", StringComparison.Ordinal) >= 0;
		}
		return false;
	}

	private static bool IsAuthoritySafe(Type iteratorType)
	{
		if (AuthorityCache.TryGetValue(iteratorType, out var value))
		{
			return value;
		}
		bool flag = false;
		try
		{
			Type declaringType = iteratorType.DeclaringType;
			if (declaringType == null || !string.Equals(declaringType.FullName, "AllowTool.WorkGiver_HaulUrgently", StringComparison.Ordinal))
			{
				AuthorityCache[iteratorType] = false;
				return false;
			}
			MethodInfo methodInfo = null;
			MethodInfo[] methods = declaringType.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			foreach (MethodInfo methodInfo2 in methods)
			{
				if (!(methodInfo2.Name != "PotentialWorkThingsGlobal") && typeof(IEnumerable<Thing>).IsAssignableFrom(methodInfo2.ReturnType))
				{
					methodInfo = methodInfo2;
					break;
				}
			}
			if (methodInfo == null)
			{
				AuthorityCache[iteratorType] = false;
				return false;
			}
			MethodInfo method = iteratorType.GetMethod("MoveNext", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			flag = !HasAnyHarmonyPatch(methodInfo) && (method == null || !HasAnyHarmonyPatch(method));
		}
		catch
		{
			flag = false;
		}
		AuthorityCache[iteratorType] = flag;
		return flag;
	}

	private static bool HasAnyHarmonyPatch(MethodBase method)
	{
		if (method == null)
		{
			return false;
		}
		Patches patchInfo = Harmony.GetPatchInfo(method);
		if (patchInfo != null)
		{
			if (patchInfo.Prefixes.Count == 0 && patchInfo.Postfixes.Count == 0 && patchInfo.Transpilers.Count == 0)
			{
				return patchInfo.Finalizers.Count != 0;
			}
			return true;
		}
		return false;
	}

	private static List<Thing> MaterializeFresh(IEnumerable<Thing> source)
	{
		List<Thing> list = new List<Thing>();
		try
		{
			foreach (Thing item in source)
			{
				list.Add(item);
			}
			return list;
		}
		catch
		{
			failures++;
			runtimeQuarantined = true;
			throw;
		}
	}

	private static bool SameReferenceOrder(List<Thing> a, List<Thing> b)
	{
		if (a == null || b == null || a.Count != b.Count)
		{
			return false;
		}
		for (int i = 0; i < a.Count; i++)
		{
			if (a[i] != b[i])
			{
				return false;
			}
		}
		return true;
	}

	internal static string Summary()
	{
		return "T17 HaulUrgently dynamic-source memo: installed=" + installed + ", patchedMethods=" + patchedMethods + ", enabled=" + enabled + ", runtimeQuarantined=" + runtimeQuarantined + ", observed=" + observed + ", targetDetected=" + targetDetected + ", authorityBypass=" + authorityBypass + ", wrappedFirstPass=" + wrappedFirstPass + ", completedBuilds=" + completedBuilds + ", incompleteBuilds=" + incompleteBuilds + ", cacheHits=" + cacheHits + ", wrapperReplayHits=" + wrapperReplayHits + ", shadowSamples/matches/mismatches=" + shadowSamples + "/" + shadowMatches + "/" + shadowMismatches + ", shadowItems=" + shadowItems + ", recordedItems=" + recordedItems + ", maxRecordedItems=" + maxRecordedItems + ", reentrantBypass=" + reentrantBypass + ", failures=" + failures + ". Cache lifetime=one synchronous JobGiver_Work package; first pass preserves source order; sampled parity compares exact Thing references/order; final Vanilla validator/Reachability/JobOnThing remain authoritative.";
	}
}
