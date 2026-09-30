using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT;

internal static class GenClosestTransactionIndex093T22
{
	internal struct PackageState
	{
		internal bool Entered;

		internal bool Outermost;

		internal PackageContext Context;
	}

	internal sealed class PackageContext
	{
		internal readonly Dictionary<IndexKey, int> Seen = new Dictionary<IndexKey, int>();

		internal readonly Dictionary<IndexKey, CandidateIndex> Indexes = new Dictionary<IndexKey, CandidateIndex>();
	}

	internal struct IndexKey : IEquatable<IndexKey>
	{
		private readonly object source;

		private readonly IntVec3 center;

		internal IndexKey(object source, IntVec3 center)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			this.source = source;
			this.center = center;
		}

		public bool Equals(IndexKey other)
		{
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			if (source == other.source)
			{
				return center == other.center;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is IndexKey)
			{
				return Equals((IndexKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			return (RuntimeHelpers.GetHashCode(source) * 397) ^ ((object)center/*cast due to .constrained prefix*/).GetHashCode();
		}
	}

	internal sealed class CandidateIndex
	{
		internal readonly Candidate[] Candidates;

		private readonly Sample[] samples;

		private readonly int count;

		private CandidateIndex(Candidate[] candidates, Sample[] samples, int count)
		{
			Candidates = candidates;
			this.samples = samples;
			this.count = count;
		}

		internal static CandidateIndex Build(IList list, IntVec3 center)
		{
			//IL_0036: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0043: Unknown result type (might be due to invalid IL or missing references)
			//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
			if (list == null)
			{
				return null;
			}
			int num = list.Count;
			Candidate[] array = new Candidate[num];
			for (int i = 0; i < num; i++)
			{
				object obj = list[i];
				Thing val = (Thing)((obj is Thing) ? obj : null);
				if (val == null || !val.Spawned)
				{
					return null;
				}
				IntVec3 val2 = center - val.PositionHeld;
				int lengthHorizontalSquared = ((IntVec3)(ref val2)).LengthHorizontalSquared;
				array[i] = new Candidate(val, lengthHorizontalSquared, i);
			}
			Array.Sort(array, delegate(Candidate a, Candidate b)
			{
				int num4 = a.DistanceSquared.CompareTo(b.DistanceSquared);
				return (num4 != 0) ? num4 : a.SourceIndex.CompareTo(b.SourceIndex);
			});
			int num2 = Math.Min(8, num);
			Sample[] array2 = new Sample[num2];
			if (num2 > 0)
			{
				for (int num3 = 0; num3 < num2; num3++)
				{
					int index = (int)((num2 != 1) ? ((long)num3 * (long)(num - 1) / (num2 - 1)) : 0);
					object obj2 = list[index];
					Thing val3 = (Thing)((obj2 is Thing) ? obj2 : null);
					array2[num3] = new Sample(index, val3, val3.PositionHeld);
				}
			}
			return new CandidateIndex(array, array2, num);
		}

		internal bool FingerprintMatches(IList list)
		{
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			if (list == null || list.Count != count)
			{
				return false;
			}
			for (int i = 0; i < samples.Length; i++)
			{
				Sample sample = samples[i];
				object obj = list[sample.Index];
				Thing val = (Thing)((obj is Thing) ? obj : null);
				if (val != sample.Thing || val == null || !val.Spawned || val.PositionHeld != sample.Position)
				{
					return false;
				}
			}
			return true;
		}
	}

	internal struct Candidate
	{
		internal readonly Thing Thing;

		internal readonly int DistanceSquared;

		internal readonly int SourceIndex;

		internal Candidate(Thing thing, int distanceSquared, int sourceIndex)
		{
			Thing = thing;
			DistanceSquared = distanceSquared;
			SourceIndex = sourceIndex;
		}
	}

	internal struct Sample
	{
		internal readonly int Index;

		internal readonly Thing Thing;

		internal readonly IntVec3 Position;

		internal Sample(int index, Thing thing, IntVec3 position)
		{
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			Index = index;
			Thing = thing;
			Position = position;
		}
	}

	internal sealed class ChainAudit
	{
		internal int ForeignPrefixes;

		internal int ForeignPostfixes;

		internal int ForeignTranspilers;

		internal int ForeignFinalizers;

		internal int RunOriginalPostfixes;

		internal string[] ForeignPrefixOwners = new string[0];

		internal bool Unknown;
	}

	private const int MinSourceCount = 32;

	private const int MaxSourceCount = 4096;

	private const int ReplayPrefixPriority = -500;

	[ThreadStatic]
	private static int packageDepth;

	[ThreadStatic]
	private static PackageContext current;

	private static bool installed;

	private static bool packagePatched;

	private static bool globalPatched;

	private static bool chainAuthoritativeSafe;

	private static int foreignPrefixes;

	private static int foreignPostfixes;

	private static int foreignTranspilers;

	private static int foreignFinalizers;

	private static int runOriginalReaders;

	private static int installFailures;

	private static long packages;

	private static long observed;

	private static long eligible;

	private static long firstObservationBypass;

	private static long builds;

	private static long reuses;

	private static long authoritative;

	private static long authoritativeNull;

	private static long candidatesVisited;

	private static long validatorCalls;

	private static long sourceShapeBypass;

	private static long sizeBypass;

	private static long priorityBypass;

	private static long haulSourceBypass;

	private static long foreignChainBypass;

	private static long runOriginalBypass;

	private static long mutationRebuilds;

	private static long buildTicks;

	private static long maxBuildTicks;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_00fc: Unknown result type (might be due to invalid IL or missing references)
		//IL_0101: Unknown result type (might be due to invalid IL or missing references)
		//IL_010c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			packagePatched = JobSearchPackageContext093T28.Installed;
			MethodBase methodBase = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global_NewTemp", new Type[6]
			{
				typeof(IntVec3),
				typeof(IEnumerable),
				typeof(float),
				typeof(Predicate<Thing>),
				typeof(Func<Thing, float>),
				typeof(bool)
			}, (Type[])null);
			if (methodBase != null)
			{
				ChainAudit chainAudit = InspectChain(methodBase);
				foreignPrefixes = chainAudit.ForeignPrefixes;
				foreignPostfixes = chainAudit.ForeignPostfixes;
				foreignTranspilers = chainAudit.ForeignTranspilers;
				foreignFinalizers = chainAudit.ForeignFinalizers;
				runOriginalReaders = chainAudit.RunOriginalPostfixes;
				chainAuthoritativeSafe = !chainAudit.Unknown && chainAudit.ForeignTranspilers == 0 && chainAudit.ForeignFinalizers == 0 && chainAudit.RunOriginalPostfixes == 0;
				HarmonyMethod val = new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), "GlobalPrefix", (Type[])null)
				{
					priority = -500,
					after = chainAudit.ForeignPrefixOwners
				};
				harmony.Patch(methodBase, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
				globalPatched = true;
			}
			installed = packagePatched && globalPatched;
			Log.Message("[RimMT] T22 generic GenClosest index coordinated by T28; installed=" + installed + ", chainSafe=" + chainAuthoritativeSafe + ", foreignPrefix/postfix/transpiler/finalizer=" + foreignPrefixes + "/" + foreignPostfixes + "/" + foreignTranspilers + "/" + foreignFinalizers + ". Index lifetime=one synchronous JobGiver_Work package; live validator remains authority.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			Log.Warning("[RimMT] T22 GenClosest transaction index failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void PackagePrefix(ref PackageState __state)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Invalid comparison between Unknown and I4
		__state = default(PackageState);
		if (RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			__state.Entered = true;
			__state.Outermost = packageDepth == 0;
			packageDepth++;
			if (__state.Outermost)
			{
				current = new PackageContext();
				__state.Context = current;
				Interlocked.Increment(ref packages);
			}
			else
			{
				__state.Context = current;
			}
		}
	}

	public static Exception PackageFinalizer(Exception __exception, PackageState __state)
	{
		if (!__state.Entered)
		{
			return __exception;
		}
		if (packageDepth > 0)
		{
			packageDepth--;
		}
		if (__state.Outermost && current == __state.Context)
		{
			current = null;
		}
		return __exception;
	}

	public static bool GlobalPrefix(IntVec3 center, IEnumerable searchSet, float maxDistance, Predicate<Thing> validator, Func<Thing, float> priorityGetter, bool lookInHaulSources, bool __runOriginal, ref Thing __result)
	{
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_0193: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fd: Unknown result type (might be due to invalid IL or missing references)
		Interlocked.Increment(ref observed);
		PackageContext packageContext = current;
		if (packageContext == null || packageDepth <= 0 || !RimMTThreadGuard.IsMainThread)
		{
			return true;
		}
		if (!__runOriginal)
		{
			Interlocked.Increment(ref runOriginalBypass);
			return true;
		}
		if (!chainAuthoritativeSafe)
		{
			Interlocked.Increment(ref foreignChainBypass);
			return true;
		}
		if (priorityGetter != null)
		{
			Interlocked.Increment(ref priorityBypass);
			return true;
		}
		if (lookInHaulSources)
		{
			Interlocked.Increment(ref haulSourceBypass);
			return true;
		}
		if (!(searchSet is IList { Count: var count } list))
		{
			Interlocked.Increment(ref sourceShapeBypass);
			return true;
		}
		if (count < 32 || count > 4096)
		{
			Interlocked.Increment(ref sizeBypass);
			return true;
		}
		Interlocked.Increment(ref eligible);
		IndexKey key = new IndexKey(searchSet, center);
		if (!packageContext.Indexes.TryGetValue(key, out var value))
		{
			if (!packageContext.Seen.TryGetValue(key, out var _))
			{
				packageContext.Seen.Add(key, 1);
				Interlocked.Increment(ref firstObservationBypass);
				return true;
			}
			long timestamp = Stopwatch.GetTimestamp();
			try
			{
				value = CandidateIndex.Build(list, center);
			}
			catch
			{
				Interlocked.Increment(ref failures);
				return true;
			}
			if (value == null)
			{
				Interlocked.Increment(ref sourceShapeBypass);
				return true;
			}
			long num = Stopwatch.GetTimestamp() - timestamp;
			if (num > 0)
			{
				Interlocked.Add(ref buildTicks, num);
				UpdateMax(ref maxBuildTicks, num);
			}
			packageContext.Indexes[key] = value;
			Interlocked.Increment(ref builds);
		}
		else if (!value.FingerprintMatches(list))
		{
			Interlocked.Increment(ref mutationRebuilds);
			long timestamp2 = Stopwatch.GetTimestamp();
			CandidateIndex candidateIndex;
			try
			{
				candidateIndex = CandidateIndex.Build(list, center);
			}
			catch
			{
				Interlocked.Increment(ref failures);
				return true;
			}
			if (candidateIndex == null)
			{
				packageContext.Indexes.Remove(key);
				return true;
			}
			long num2 = Stopwatch.GetTimestamp() - timestamp2;
			if (num2 > 0)
			{
				Interlocked.Add(ref buildTicks, num2);
				UpdateMax(ref maxBuildTicks, num2);
			}
			packageContext.Indexes[key] = candidateIndex;
			value = candidateIndex;
			Interlocked.Increment(ref builds);
		}
		else
		{
			Interlocked.Increment(ref reuses);
		}
		float num3 = maxDistance * maxDistance;
		Candidate[] candidates = value.Candidates;
		int num4 = 0;
		while (num4 < candidates.Length)
		{
			Candidate candidate = candidates[num4];
			if ((float)candidate.DistanceSquared > num3)
			{
				break;
			}
			Interlocked.Increment(ref candidatesVisited);
			Thing thing = candidate.Thing;
			if (thing == null || !thing.Spawned)
			{
				packageContext.Indexes.Remove(key);
				Interlocked.Increment(ref mutationRebuilds);
				return true;
			}
			if (validator != null)
			{
				Interlocked.Increment(ref validatorCalls);
				if (!validator(thing))
				{
					num4++;
					continue;
				}
			}
			__result = thing;
			Interlocked.Increment(ref authoritative);
			return false;
		}
		__result = null;
		Interlocked.Increment(ref authoritative);
		Interlocked.Increment(ref authoritativeNull);
		return false;
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref builds);
		double num2 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref buildTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num3 = (double)Interlocked.Read(ref maxBuildTicks) * 1000000.0 / (double)Stopwatch.Frequency;
		return "T22 generic GenClosest transaction index: installed=" + installed + ", packagePatched=" + packagePatched + ", globalNewTempPatched=" + globalPatched + ", chainAuthoritativeSafe=" + chainAuthoritativeSafe + ", chain[foreignPrefixes=" + foreignPrefixes + ", foreignPostfixes=" + foreignPostfixes + ", transpilers=" + foreignTranspilers + ", finalizers=" + foreignFinalizers + ", runOriginalReaders=" + runOriginalReaders + "], packages=" + Interlocked.Read(ref packages) + ", observed=" + Interlocked.Read(ref observed) + ", eligible=" + Interlocked.Read(ref eligible) + ", firstObservationBypass=" + Interlocked.Read(ref firstObservationBypass) + ", builds=" + num + ", reuses=" + Interlocked.Read(ref reuses) + ", authoritative=" + Interlocked.Read(ref authoritative) + ", authoritativeNull=" + Interlocked.Read(ref authoritativeNull) + ", candidatesVisited=" + Interlocked.Read(ref candidatesVisited) + ", validatorCalls=" + Interlocked.Read(ref validatorCalls) + ", bypass[shape/size/priority/haul/foreign/runOriginal]=" + Interlocked.Read(ref sourceShapeBypass) + "/" + Interlocked.Read(ref sizeBypass) + "/" + Interlocked.Read(ref priorityBypass) + "/" + Interlocked.Read(ref haulSourceBypass) + "/" + Interlocked.Read(ref foreignChainBypass) + "/" + Interlocked.Read(ref runOriginalBypass) + ", mutationRebuilds=" + Interlocked.Read(ref mutationRebuilds) + ", avgBuildUs=" + num2.ToString("F2") + ", maxBuildUs=" + num3.ToString("F2") + ", failures=" + Interlocked.Read(ref failures) + ", installFailures=" + installFailures + ". Scope=one synchronous JobGiver_Work package, repeated IList source+center only, priorityGetter=null, lookInHaulSources=false, all members spawned; sorted by distance then source order; original live validator is called exactly once per visited candidate.";
	}

	private static ChainAudit InspectChain(MethodBase method)
	{
		ChainAudit chainAudit = new ChainAudit();
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo == null)
			{
				return chainAudit;
			}
			HashSet<string> hashSet = new HashSet<string>();
			foreach (Patch prefix in patchInfo.Prefixes)
			{
				if (prefix != null && !string.Equals(prefix.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					chainAudit.ForeignPrefixes++;
					if (!string.IsNullOrEmpty(prefix.owner))
					{
						hashSet.Add(prefix.owner);
					}
				}
			}
			foreach (Patch postfix in patchInfo.Postfixes)
			{
				if (postfix != null && !string.Equals(postfix.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					chainAudit.ForeignPostfixes++;
					if (PatchReadsRunOriginal(postfix))
					{
						chainAudit.RunOriginalPostfixes++;
					}
				}
			}
			foreach (Patch transpiler in patchInfo.Transpilers)
			{
				if (transpiler != null && !string.Equals(transpiler.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					chainAudit.ForeignTranspilers++;
				}
			}
			foreach (Patch finalizer in patchInfo.Finalizers)
			{
				if (finalizer != null && !string.Equals(finalizer.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					chainAudit.ForeignFinalizers++;
				}
			}
			chainAudit.ForeignPrefixOwners = new string[hashSet.Count];
			hashSet.CopyTo(chainAudit.ForeignPrefixOwners);
			return chainAudit;
		}
		catch
		{
			chainAudit.Unknown = true;
			return chainAudit;
		}
	}

	private static bool PatchReadsRunOriginal(Patch patch)
	{
		MethodInfo methodInfo = ((patch == null) ? null : patch.PatchMethod);
		if (methodInfo == null)
		{
			return true;
		}
		ParameterInfo[] parameters = methodInfo.GetParameters();
		for (int i = 0; i < parameters.Length; i++)
		{
			if (parameters[i].Name == "__runOriginal")
			{
				return true;
			}
		}
		return false;
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
