using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class JobSearchTransaction093T20
{
	internal sealed class ReachChainAudit
	{
		internal int ForeignPrefixes;

		internal int ForeignPostfixes;

		internal int ResultMutatingPostfixes;

		internal int RunOriginalPostfixes;

		internal int ForeignTranspilers;

		internal int ForeignFinalizers;

		internal int MinAnyPrefixPriority = int.MaxValue;

		internal int MaxAnyPostfixPriority = int.MinValue;

		internal string[] ForeignPrefixOwners = new string[0];

		internal string[] ForeignPostfixOwners = new string[0];

		internal bool PriorityRoom = true;

		internal bool Unknown;
	}

	internal struct PackageState
	{
		internal bool Entered;

		internal bool Outermost;

		internal TransactionContext Context;
	}

	internal struct ValidatorCallState
	{
		internal TransactionContext Context;

		internal ValidatorKey Key;

		internal WorkGiver_Scanner Scanner;

		internal Thing Thing;

		internal ValidatorTrustState Trust;

		internal bool Store;

		internal bool Verify;

		internal bool Prime;

		internal bool PrimeForbiddenBefore;

		internal ThingFingerprint PrimeFingerprint;

		internal bool AuthoritativeHit;

		internal static ValidatorCallState ForStore(TransactionContext context, ValidatorKey key, WorkGiver_Scanner scanner, Thing thing)
		{
			return new ValidatorCallState
			{
				Context = context,
				Key = key,
				Scanner = scanner,
				Thing = thing,
				Store = true
			};
		}

		internal static ValidatorCallState ForVerify(TransactionContext context, ValidatorKey key, ValidatorTrustState trust)
		{
			return new ValidatorCallState
			{
				Context = context,
				Key = key,
				Trust = trust,
				Verify = true
			};
		}
	}

	internal struct ReachCallState
	{
		internal TransactionContext Context;

		internal ReachKey Key;

		internal LocalTargetInfo Dest;

		internal bool Cached;

		internal bool Store;

		internal bool Verify;

		internal bool AuthoritativeHit;

		internal static ReachCallState ForStore(TransactionContext context, ReachKey key, LocalTargetInfo dest)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new ReachCallState
			{
				Context = context,
				Key = key,
				Dest = dest,
				Store = true
			};
		}

		internal static ReachCallState VerifyOnly(TransactionContext context, ReachKey key, bool cached, LocalTargetInfo dest)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new ReachCallState
			{
				Context = context,
				Key = key,
				Dest = dest,
				Cached = cached,
				Verify = true
			};
		}

		internal static ReachCallState Authoritative(TransactionContext context, ReachKey key, bool cached, LocalTargetInfo dest)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new ReachCallState
			{
				Context = context,
				Key = key,
				Dest = dest,
				Cached = cached,
				AuthoritativeHit = true
			};
		}
	}

	internal sealed class TransactionContext
	{
		internal readonly Pawn Pawn;

		internal readonly Map Map;

		internal readonly IntVec3 StartPosition;

		internal readonly Dictionary<ValidatorKey, ValidatorNegativeEntry> ValidatorNegatives = new Dictionary<ValidatorKey, ValidatorNegativeEntry>();

		internal readonly Dictionary<ReachKey, ReachEntry> ReachMemo = new Dictionary<ReachKey, ReachEntry>();

		internal readonly HashSet<ReachKey> ReachQuarantined = new HashSet<ReachKey>();

		internal long ReachHitSerial;

		internal int ReachValidatedMatches;

		internal TransactionContext(Pawn pawn)
		{
			//IL_004c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0045: Unknown result type (might be due to invalid IL or missing references)
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			Pawn = pawn;
			Map = ((pawn == null) ? null : ((Thing)pawn).Map);
			StartPosition = ((pawn == null) ? IntVec3.Invalid : ((Thing)pawn).Position);
		}
	}

	internal sealed class ValidatorTrustState
	{
		internal long HitSerial;

		internal int ValidatedMatches;

		internal bool Quarantined;

		internal int AdaptiveStores;

		internal int AdaptiveFirstRepeats;

		internal bool PreferEager;

		internal void ObserveStore()
		{
			if (AdaptiveStores < int.MaxValue)
			{
				AdaptiveStores++;
			}
			EvaluateAdaptiveMode();
		}

		internal void ObserveFirstRepeat()
		{
			if (AdaptiveFirstRepeats < int.MaxValue)
			{
				AdaptiveFirstRepeats++;
			}
			EvaluateAdaptiveMode();
		}

		private void EvaluateAdaptiveMode()
		{
			if (AdaptiveStores >= 4096)
			{
				AdaptiveStores = AdaptiveStores + 1 >> 1;
				AdaptiveFirstRepeats = AdaptiveFirstRepeats + 1 >> 1;
			}
			if (Quarantined)
			{
				return;
			}
			if (!PreferEager)
			{
				if (ValidatedMatches >= 12 && AdaptiveStores >= 128 && (long)AdaptiveFirstRepeats * 8L >= AdaptiveStores)
				{
					PreferEager = true;
					AdaptiveStores = 0;
					AdaptiveFirstRepeats = 0;
					Interlocked.Increment(ref validatorAdaptiveSwitchToEager);
				}
			}
			else if (AdaptiveStores >= 256 && (long)AdaptiveFirstRepeats * 16L < AdaptiveStores)
			{
				PreferEager = false;
				AdaptiveStores = 0;
				AdaptiveFirstRepeats = 0;
				Interlocked.Increment(ref validatorAdaptiveSwitchToLazy);
			}
		}
	}

	internal struct ValidatorNegativeEntry
	{
		internal ThingFingerprint Fingerprint;

		internal bool Primed;

		internal bool RepeatObserved;

		internal ValidatorNegativeEntry(ThingFingerprint fingerprint, bool primed)
		{
			Fingerprint = fingerprint;
			Primed = primed;
			RepeatObserved = false;
		}
	}

	internal struct ReachEntry
	{
		internal readonly bool Result;

		internal readonly TargetFingerprint Fingerprint;

		internal ReachEntry(bool result, TargetFingerprint fingerprint)
		{
			Result = result;
			Fingerprint = fingerprint;
		}
	}

	internal struct ValidatorKey : IEquatable<ValidatorKey>
	{
		internal readonly MethodBase Method;

		internal readonly WorkGiver_Scanner Scanner;

		internal readonly Thing Thing;

		internal ValidatorKey(MethodBase method, WorkGiver_Scanner scanner, Thing thing)
		{
			Method = method;
			Scanner = scanner;
			Thing = thing;
		}

		public bool Equals(ValidatorKey other)
		{
			if ((object)Method == other.Method && Scanner == other.Scanner)
			{
				return Thing == other.Thing;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ValidatorKey)
			{
				return Equals((ValidatorKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((RuntimeHelpers.GetHashCode(Method) * 397) ^ RuntimeHelpers.GetHashCode(Scanner)) * 397) ^ RuntimeHelpers.GetHashCode(Thing);
		}
	}

	internal struct ValidatorTrustKey : IEquatable<ValidatorTrustKey>
	{
		internal readonly MethodBase Method;

		internal readonly WorkGiver_Scanner Scanner;

		internal ValidatorTrustKey(MethodBase method, WorkGiver_Scanner scanner)
		{
			Method = method;
			Scanner = scanner;
		}

		public bool Equals(ValidatorTrustKey other)
		{
			if ((object)Method == other.Method)
			{
				return Scanner == other.Scanner;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ValidatorTrustKey)
			{
				return Equals((ValidatorTrustKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (RuntimeHelpers.GetHashCode(Method) * 397) ^ RuntimeHelpers.GetHashCode(Scanner);
		}
	}

	internal struct ReachKey : IEquatable<ReachKey>
	{
		internal readonly Reachability Reachability;

		internal readonly IntVec3 Start;

		internal readonly bool HasThing;

		internal readonly Thing Thing;

		internal readonly IntVec3 Cell;

		internal readonly PathEndMode EndMode;

		internal readonly TraverseParms Traverse;

		internal ReachKey(Reachability reachability, IntVec3 start, LocalTargetInfo dest, PathEndMode endMode, TraverseParms traverse)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
			//IL_003c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0042: Unknown result type (might be due to invalid IL or missing references)
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_004a: Unknown result type (might be due to invalid IL or missing references)
			//IL_004c: Unknown result type (might be due to invalid IL or missing references)
			Reachability = reachability;
			Start = start;
			HasThing = ((LocalTargetInfo)(ref dest)).HasThing;
			Thing = (((LocalTargetInfo)(ref dest)).HasThing ? ((LocalTargetInfo)(ref dest)).Thing : null);
			Cell = ((LocalTargetInfo)(ref dest)).Cell;
			EndMode = endMode;
			Traverse = traverse;
		}

		public bool Equals(ReachKey other)
		{
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_0051: Unknown result type (might be due to invalid IL or missing references)
			//IL_0057: Unknown result type (might be due to invalid IL or missing references)
			//IL_005f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0064: Unknown result type (might be due to invalid IL or missing references)
			//IL_0068: Unknown result type (might be due to invalid IL or missing references)
			if (Reachability == other.Reachability && Start == other.Start && HasThing == other.HasThing && Thing == other.Thing && Cell == other.Cell && EndMode == other.EndMode)
			{
				TraverseParms traverse = Traverse;
				return ((TraverseParms)(ref traverse)).Equals(other.Traverse);
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ReachKey)
			{
				return Equals((ReachKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_0035: Unknown result type (might be due to invalid IL or missing references)
			//IL_003a: Unknown result type (might be due to invalid IL or missing references)
			//IL_005d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_0068: Unknown result type (might be due to invalid IL or missing references)
			//IL_006a: Unknown result type (might be due to invalid IL or missing references)
			//IL_006f: Unknown result type (might be due to invalid IL or missing references)
			//IL_007d: Unknown result type (might be due to invalid IL or missing references)
			//IL_007f: Expected I4, but got Unknown
			return (((((((RuntimeHelpers.GetHashCode(Reachability) * 397) ^ ((object)Start/*cast due to .constrained prefix*/).GetHashCode()) * 397) ^ (HasThing ? RuntimeHelpers.GetHashCode(Thing) : ((object)Cell/*cast due to .constrained prefix*/).GetHashCode())) * 397) ^ EndMode) * 397) ^ ((object)Traverse/*cast due to .constrained prefix*/).GetHashCode();
		}
	}

	internal struct ThingFingerprint
	{
		internal readonly Map MapHeld;

		internal readonly IntVec3 PositionHeld;

		internal readonly bool Spawned;

		internal readonly int StackCount;

		internal readonly int HitPoints;

		internal readonly bool HasForbidden;

		internal readonly bool Forbidden;

		internal ThingFingerprint(Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount, int hitPoints, bool hasForbidden, bool forbidden)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			MapHeld = mapHeld;
			PositionHeld = positionHeld;
			Spawned = spawned;
			StackCount = stackCount;
			HitPoints = hitPoints;
			HasForbidden = hasForbidden;
			Forbidden = forbidden;
		}

		internal static ThingFingerprint CaptureCheap(Thing thing)
		{
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0004: Unknown result type (might be due to invalid IL or missing references)
			if (thing == null)
			{
				return new ThingFingerprint(null, IntVec3.Invalid, spawned: false, 0, 0, hasForbidden: false, forbidden: false);
			}
			return new ThingFingerprint(thing.MapHeld, thing.PositionHeld, thing.Spawned, thing.stackCount, thing.HitPoints, hasForbidden: false, forbidden: false);
		}

		internal ThingFingerprint WithForbidden(bool forbidden)
		{
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			return new ThingFingerprint(MapHeld, PositionHeld, Spawned, StackCount, HitPoints, hasForbidden: true, forbidden);
		}

		internal bool MatchesCheap(Thing thing)
		{
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			if (thing != null && MapHeld == thing.MapHeld && PositionHeld == thing.PositionHeld && Spawned == thing.Spawned && StackCount == thing.stackCount)
			{
				return HitPoints == thing.HitPoints;
			}
			return false;
		}

		internal bool MatchesForbidden(Pawn pawn, Thing thing)
		{
			if (!HasForbidden || !MatchesCheap(thing))
			{
				return false;
			}
			if (TryReadForbidden(pawn, thing, out var forbidden))
			{
				return Forbidden == forbidden;
			}
			return false;
		}
	}

	internal struct TargetFingerprint
	{
		internal readonly bool HasThing;

		internal readonly Thing Thing;

		internal readonly Map MapHeld;

		internal readonly IntVec3 PositionHeld;

		internal readonly bool Spawned;

		internal readonly IntVec3 Cell;

		internal TargetFingerprint(bool hasThing, Thing thing, Map mapHeld, IntVec3 positionHeld, bool spawned, IntVec3 cell)
		{
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_0026: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			HasThing = hasThing;
			Thing = thing;
			MapHeld = mapHeld;
			PositionHeld = positionHeld;
			Spawned = spawned;
			Cell = cell;
		}

		internal static TargetFingerprint Capture(LocalTargetInfo target)
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0014: Unknown result type (might be due to invalid IL or missing references)
			//IL_0040: Unknown result type (might be due to invalid IL or missing references)
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_0053: Unknown result type (might be due to invalid IL or missing references)
			if (!((LocalTargetInfo)(ref target)).HasThing)
			{
				return new TargetFingerprint(hasThing: false, null, null, IntVec3.Invalid, spawned: false, ((LocalTargetInfo)(ref target)).Cell);
			}
			Thing thing = ((LocalTargetInfo)(ref target)).Thing;
			return new TargetFingerprint(hasThing: true, thing, (thing == null) ? null : thing.MapHeld, (thing == null) ? IntVec3.Invalid : thing.PositionHeld, thing != null && thing.Spawned, ((LocalTargetInfo)(ref target)).Cell);
		}

		internal bool Matches(LocalTargetInfo target)
		{
			//IL_0010: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_0052: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Unknown result type (might be due to invalid IL or missing references)
			if (HasThing != ((LocalTargetInfo)(ref target)).HasThing || Cell != ((LocalTargetInfo)(ref target)).Cell)
			{
				return false;
			}
			if (!HasThing)
			{
				return true;
			}
			Thing thing = ((LocalTargetInfo)(ref target)).Thing;
			if (Thing == thing && thing != null && MapHeld == thing.MapHeld && PositionHeld == thing.PositionHeld)
			{
				return Spawned == thing.Spawned;
			}
			return false;
		}
	}

	private sealed class ScannerAccessor
	{
		private readonly FieldInfo[] path;

		private ScannerAccessor(FieldInfo[] path)
		{
			this.path = path;
		}

		internal WorkGiver_Scanner Read(object root)
		{
			object obj = root;
			try
			{
				for (int i = 0; i < path.Length; i++)
				{
					if (obj == null)
					{
						return null;
					}
					obj = path[i].GetValue(obj);
				}
				return (WorkGiver_Scanner)((obj is WorkGiver_Scanner) ? obj : null);
			}
			catch
			{
				return null;
			}
		}

		internal static ScannerAccessor Build(Type root)
		{
			List<FieldInfo> list = new List<FieldInfo>();
			HashSet<Type> visited = new HashSet<Type>();
			if (Find(root, 0, list, visited))
			{
				return new ScannerAccessor(list.ToArray());
			}
			return null;
		}

		private static bool Find(Type type, int depth, List<FieldInfo> path, HashSet<Type> visited)
		{
			if (type == null || depth > 3 || visited.Contains(type))
			{
				return false;
			}
			visited.Add(type);
			FieldInfo[] fields;
			try
			{
				fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			}
			catch
			{
				return false;
			}
			foreach (FieldInfo fieldInfo in fields)
			{
				if (!(fieldInfo == null) && typeof(WorkGiver_Scanner).IsAssignableFrom(fieldInfo.FieldType))
				{
					path.Add(fieldInfo);
					return true;
				}
			}
			foreach (FieldInfo fieldInfo2 in fields)
			{
				if (!(fieldInfo2 == null) && !fieldInfo2.FieldType.IsPrimitive && !(fieldInfo2.FieldType == typeof(string)) && !fieldInfo2.FieldType.IsEnum && !fieldInfo2.FieldType.IsPointer)
				{
					path.Add(fieldInfo2);
					if (Find(fieldInfo2.FieldType, depth + 1, path, visited))
					{
						return true;
					}
					path.RemoveAt(path.Count - 1);
				}
			}
			return false;
		}
	}

	internal const string FeatureId = "ai.jobSearchTransaction";

	private const int ValidatorCapacity = 8192;

	private const int ReachCapacity = 8192;

	private const int ValidatorWarmupMatches = 12;

	private const int ValidatorVerifyMask = 63;

	private const int AdaptiveEagerEnterMinStores = 128;

	private const int AdaptiveLazyReturnMinStores = 256;

	private const int AdaptiveEvidenceMaxStores = 4096;

	private const int ReachWarmupMatches = 8;

	private const int ReachVerifyMask = 63;

	private const int ReachReplayPrefixFallbackPriority = -1000000;

	private const int ReachBasePostfixFallbackPriority = 1000000;

	[ThreadStatic]
	private static int depth;

	[ThreadStatic]
	private static TransactionContext current;

	private static readonly object TrustLock = new object();

	private static readonly Dictionary<ValidatorTrustKey, ValidatorTrustState> ValidatorTrust = new Dictionary<ValidatorTrustKey, ValidatorTrustState>();

	private static bool installed;

	private static bool packagePatched;

	private static bool reachPatched;

	private static bool reachChainAuthoritativeSafe;

	private static int reachReplayPrefixPriority = -1000000;

	private static int reachBasePostfixPriority = 1000000;

	private static int reachForeignPrefixes;

	private static int reachForeignPostfixes;

	private static int reachForeignResultPostfixes;

	private static int reachRunOriginalPostfixes;

	private static int reachForeignTranspilers;

	private static int reachForeignFinalizers;

	private static bool reachChainAuditUnknown;

	private static int validatorMethodsPatched;

	private static int validatorMethodsSkippedForeign;

	private static int installFailures;

	private static long packages;

	private static long nestedPackages;

	private static long validatorObserved;

	private static long validatorNegativeStores;

	private static long validatorMemoCandidates;

	private static long validatorAuthoritativeHits;

	private static long validatorVerifyRuns;

	private static long validatorVerifyMatches;

	private static long validatorMismatches;

	private static long validatorQuarantines;

	private static long validatorFingerprintBypass;

	private static long validatorCapacityBypass;

	private static long validatorScannerResolveBypass;

	private static long validatorPositiveLive;

	private static long validatorLazyStores;

	private static long validatorLazyRepeatProbes;

	private static long validatorLazyPrimeSuccess;

	private static long validatorLazyPrimePositive;

	private static long validatorLazyPrimeUnstable;

	private static long validatorForbiddenReads;

	private static long validatorStoreForbiddenReadsAvoided;

	private static long validatorAdaptiveFirstRepeats;

	private static long validatorAdaptiveEagerStores;

	private static long validatorAdaptiveEagerCaptures;

	private static long validatorAdaptiveEagerFallbackLazy;

	private static long validatorAdaptiveSwitchToEager;

	private static long validatorAdaptiveSwitchToLazy;

	private static long reachObserved;

	private static long reachStores;

	private static long reachMemoCandidates;

	private static long reachAuthoritativeHits;

	private static long reachVerifyRuns;

	private static long reachVerifyMatches;

	private static long reachMismatches;

	private static long reachLocalQuarantines;

	private static long reachLocalQuarantineBypass;

	private static long reachMutationBypass;

	private static long reachCapacityBypass;

	private static long reachForeignResultBypass;

	private static long reachInvalidations;

	private static long reachExceptions;

	private static volatile bool reachRuntimeQuarantined;

	private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors = new Dictionary<Type, ScannerAccessor>();

	private static readonly object ScannerAccessorLock = new object();

	internal static void Apply(Harmony harmony)
	{
		if (harmony == null)
		{
			return;
		}
		try
		{
			packagePatched = JobSearchPackageContext093T28.Installed;
			PatchJobGiverValidators(harmony);
			PatchReachability(harmony);
			installed = packagePatched && validatorMethodsPatched > 0;
			Log.Message("[RimMT] T21 Foundation II transaction core installed=" + installed + ", package=" + packagePatched + ", validatorMethods=" + validatorMethodsPatched + ", reach=" + reachPatched + ", reachChainAuthoritativeSafe=" + reachChainAuthoritativeSafe + ". Lifetime=one synchronous JobGiver_Work package; negative-validator only; Vanilla live parity/quarantine remains authority.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			Log.Warning("[RimMT] T21 Foundation II transaction install failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	private static void PatchJobGiverValidators(Harmony harmony)
	{
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0114: Unknown result type (might be due to invalid IL or missing references)
		//IL_0119: Unknown result type (might be due to invalid IL or missing references)
		//IL_012b: Expected O, but got Unknown
		//IL_012b: Expected O, but got Unknown
		List<Type> list = new List<Type>();
		CollectNestedTypes(typeof(JobGiver_Work), list);
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		for (int i = 0; i < list.Count; i++)
		{
			Type type = list[i];
			MethodInfo[] methods;
			try
			{
				methods = type.GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
			}
			catch
			{
				continue;
			}
			foreach (MethodInfo methodInfo in methods)
			{
				if (methodInfo == null || methodInfo.ReturnType != typeof(bool) || methodInfo.Name.IndexOf("Validator", StringComparison.OrdinalIgnoreCase) < 0)
				{
					continue;
				}
				ParameterInfo[] parameters = methodInfo.GetParameters();
				if (parameters.Length != 1 || !typeof(Thing).IsAssignableFrom(parameters[0].ParameterType) || !hashSet.Add(methodInfo))
				{
					continue;
				}
				if (HasForeignPatches(methodInfo))
				{
					validatorMethodsSkippedForeign++;
					continue;
				}
				try
				{
					harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(JobSearchTransaction093T20), "ValidatorPrefix", (Type[])null)
					{
						priority = 1050
					}, new HarmonyMethod(typeof(JobSearchTransaction093T20), "ValidatorPostfix", (Type[])null)
					{
						priority = -250
					}, (HarmonyMethod)null, (HarmonyMethod)null);
					validatorMethodsPatched++;
				}
				catch
				{
					installFailures++;
				}
			}
		}
	}

	private static void CollectNestedTypes(Type parent, List<Type> output)
	{
		Type[] nestedTypes;
		try
		{
			nestedTypes = parent.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic);
		}
		catch
		{
			return;
		}
		foreach (Type type in nestedTypes)
		{
			if (!(type == null))
			{
				output.Add(type);
				CollectNestedTypes(type, output);
			}
		}
	}

	private static void PatchReachability(Harmony harmony)
	{
		//IL_014f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0154: Unknown result type (might be due to invalid IL or missing references)
		//IL_015f: Unknown result type (might be due to invalid IL or missing references)
		//IL_016c: Expected O, but got Unknown
		//IL_017c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0181: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0199: Expected O, but got Unknown
		//IL_01a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bb: Expected O, but got Unknown
		//IL_024a: Unknown result type (might be due to invalid IL or missing references)
		//IL_024f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0258: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(Reachability), "CanReach", new Type[4]
			{
				typeof(IntVec3),
				typeof(LocalTargetInfo),
				typeof(PathEndMode),
				typeof(TraverseParms)
			}, (Type[])null);
			if (!(methodBase == null))
			{
				ReachChainAudit reachChainAudit = InspectReachChain(methodBase);
				reachForeignPrefixes = reachChainAudit.ForeignPrefixes;
				reachForeignPostfixes = reachChainAudit.ForeignPostfixes;
				reachForeignResultPostfixes = reachChainAudit.ResultMutatingPostfixes;
				reachRunOriginalPostfixes = reachChainAudit.RunOriginalPostfixes;
				reachForeignTranspilers = reachChainAudit.ForeignTranspilers;
				reachForeignFinalizers = reachChainAudit.ForeignFinalizers;
				reachChainAuditUnknown = reachChainAudit.Unknown;
				if (!reachChainAudit.Unknown && reachChainAudit.MinAnyPrefixPriority > int.MinValue)
				{
					reachReplayPrefixPriority = reachChainAudit.MinAnyPrefixPriority - 1;
				}
				else
				{
					reachReplayPrefixPriority = -1000000;
				}
				if (!reachChainAudit.Unknown && reachChainAudit.MaxAnyPostfixPriority < int.MaxValue)
				{
					reachBasePostfixPriority = reachChainAudit.MaxAnyPostfixPriority + 1;
				}
				else
				{
					reachBasePostfixPriority = 1000000;
				}
				reachChainAuthoritativeSafe = !reachChainAudit.Unknown && reachChainAudit.PriorityRoom && reachChainAudit.ForeignTranspilers == 0 && reachChainAudit.ForeignFinalizers == 0 && reachChainAudit.RunOriginalPostfixes == 0;
				HarmonyMethod val = new HarmonyMethod(typeof(JobSearchTransaction093T20), "ReachPrefix", (Type[])null)
				{
					priority = reachReplayPrefixPriority,
					after = reachChainAudit.ForeignPrefixOwners
				};
				HarmonyMethod val2 = new HarmonyMethod(typeof(JobSearchTransaction093T20), "ReachBasePostfix", (Type[])null)
				{
					priority = reachBasePostfixPriority,
					before = reachChainAudit.ForeignPostfixOwners
				};
				HarmonyMethod val3 = new HarmonyMethod(typeof(JobSearchTransaction093T20), "ReachFinalizer", (Type[])null)
				{
					priority = -300
				};
				harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, val3);
				reachPatched = true;
				MethodBase methodBase2 = AccessTools.Method(typeof(Reachability), "ClearCache", (Type[])null, (Type[])null);
				MethodBase methodBase3 = AccessTools.Method(typeof(Reachability), "ClearCacheFor", new Type[1] { typeof(Pawn) }, (Type[])null);
				MethodBase methodBase4 = AccessTools.Method(typeof(Reachability), "ClearCacheForHostile", new Type[1] { typeof(Thing) }, (Type[])null);
				HarmonyMethod val4 = new HarmonyMethod(typeof(JobSearchTransaction093T20), "ReachCacheInvalidated", (Type[])null)
				{
					priority = 0
				};
				if (methodBase2 != null)
				{
					harmony.Patch(methodBase2, (HarmonyMethod)null, val4, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				if (methodBase3 != null)
				{
					harmony.Patch(methodBase3, (HarmonyMethod)null, val4, (HarmonyMethod)null, (HarmonyMethod)null);
				}
				if (methodBase4 != null)
				{
					harmony.Patch(methodBase4, (HarmonyMethod)null, val4, (HarmonyMethod)null, (HarmonyMethod)null);
				}
			}
		}
		catch
		{
			reachPatched = false;
			installFailures++;
		}
	}

	public static void PackagePrefix(Pawn __0, ref PackageState __state)
	{
		//IL_000e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0014: Invalid comparison between Unknown and I4
		__state = default(PackageState);
		if (RimMTThreadGuard.IsMainThread && (int)Current.ProgramState == 2)
		{
			__state.Entered = true;
			__state.Outermost = depth == 0;
			depth++;
			if (__state.Outermost)
			{
				__state.Context = (current = new TransactionContext(__0));
				Interlocked.Increment(ref packages);
			}
			else
			{
				__state.Context = current;
				Interlocked.Increment(ref nestedPackages);
			}
		}
	}

	public static Exception PackageFinalizer(Exception __exception, PackageState __state)
	{
		if (!__state.Entered)
		{
			return __exception;
		}
		if (depth > 0)
		{
			depth--;
		}
		if (__state.Outermost && current == __state.Context)
		{
			current = null;
		}
		return __exception;
	}

	public static bool ValidatorPrefix(object __instance, MethodBase __originalMethod, Thing __0, ref bool __result, ref ValidatorCallState __state)
	{
		__state = default(ValidatorCallState);
		if (!RimMTThreadGuard.IsMainThread)
		{
			return true;
		}
		Interlocked.Increment(ref validatorObserved);
		TransactionContext transactionContext = current;
		if (transactionContext == null || depth <= 0 || __originalMethod == null)
		{
			return true;
		}
		if (__0 == null || transactionContext.Pawn == null)
		{
			return true;
		}
		WorkGiver_Scanner val = ResolveScanner(__instance);
		if (val == null)
		{
			Interlocked.Increment(ref validatorScannerResolveBypass);
			return true;
		}
		ValidatorKey key = new ValidatorKey(__originalMethod, val, __0);
		if (!transactionContext.ValidatorNegatives.TryGetValue(key, out var value))
		{
			__state = ValidatorCallState.ForStore(transactionContext, key, val, __0);
			return true;
		}
		if (!value.Fingerprint.MatchesCheap(__0))
		{
			transactionContext.ValidatorNegatives.Remove(key);
			Interlocked.Increment(ref validatorFingerprintBypass);
			__state = ValidatorCallState.ForStore(transactionContext, key, val, __0);
			return true;
		}
		ValidatorTrustState trust = GetTrust(__originalMethod, val);
		if (!value.RepeatObserved)
		{
			value.RepeatObserved = true;
			transactionContext.ValidatorNegatives[key] = value;
			trust.ObserveFirstRepeat();
			Interlocked.Increment(ref validatorAdaptiveFirstRepeats);
		}
		if (!value.Primed)
		{
			if (!TryReadForbidden(transactionContext.Pawn, __0, out var forbidden))
			{
				transactionContext.ValidatorNegatives.Remove(key);
				Interlocked.Increment(ref validatorFingerprintBypass);
				__state = ValidatorCallState.ForStore(transactionContext, key, val, __0);
				return true;
			}
			Interlocked.Increment(ref validatorLazyRepeatProbes);
			__state = new ValidatorCallState
			{
				Context = transactionContext,
				Key = key,
				Scanner = val,
				Thing = __0,
				Prime = true,
				PrimeFingerprint = value.Fingerprint,
				PrimeForbiddenBefore = forbidden
			};
			return true;
		}
		if (!value.Fingerprint.MatchesForbidden(transactionContext.Pawn, __0))
		{
			transactionContext.ValidatorNegatives.Remove(key);
			Interlocked.Increment(ref validatorFingerprintBypass);
			__state = ValidatorCallState.ForStore(transactionContext, key, val, __0);
			return true;
		}
		Interlocked.Increment(ref validatorMemoCandidates);
		if (trust.Quarantined)
		{
			return true;
		}
		long num = ++trust.HitSerial;
		if (trust.ValidatedMatches < 12 || (num & 0x3F) == 0)
		{
			Interlocked.Increment(ref validatorVerifyRuns);
			__state = ValidatorCallState.ForVerify(transactionContext, key, trust);
			return true;
		}
		__result = false;
		Interlocked.Increment(ref validatorAuthoritativeHits);
		__state.AuthoritativeHit = true;
		return false;
	}

	public static void ValidatorPostfix(bool __result, ValidatorCallState __state)
	{
		if (__state.AuthoritativeHit)
		{
			return;
		}
		TransactionContext context = __state.Context;
		if (context == null || current != context)
		{
			return;
		}
		if (__state.Prime)
		{
			if (__result)
			{
				context.ValidatorNegatives.Remove(__state.Key);
				Interlocked.Increment(ref validatorLazyPrimePositive);
				return;
			}
			Thing thing = __state.Thing;
			bool forbidden;
			ValidatorNegativeEntry value;
			if (thing == null || !__state.PrimeFingerprint.MatchesCheap(thing))
			{
				context.ValidatorNegatives.Remove(__state.Key);
				Interlocked.Increment(ref validatorLazyPrimeUnstable);
			}
			else if (!TryReadForbidden(context.Pawn, thing, out forbidden) || forbidden != __state.PrimeForbiddenBefore)
			{
				context.ValidatorNegatives.Remove(__state.Key);
				Interlocked.Increment(ref validatorLazyPrimeUnstable);
			}
			else if (context.ValidatorNegatives.TryGetValue(__state.Key, out value) && value.Fingerprint.MatchesCheap(thing))
			{
				value.Primed = true;
				value.Fingerprint = value.Fingerprint.WithForbidden(forbidden);
				context.ValidatorNegatives[__state.Key] = value;
				Interlocked.Increment(ref validatorLazyPrimeSuccess);
			}
		}
		else if (__state.Verify && __state.Trust != null)
		{
			if (!__result)
			{
				__state.Trust.ValidatedMatches++;
				Interlocked.Increment(ref validatorVerifyMatches);
				return;
			}
			__state.Trust.Quarantined = true;
			__state.Trust.ValidatedMatches = 0;
			context.ValidatorNegatives.Clear();
			Interlocked.Increment(ref validatorMismatches);
			Interlocked.Increment(ref validatorQuarantines);
		}
		else if (__result)
		{
			Interlocked.Increment(ref validatorPositiveLive);
		}
		else
		{
			if (!__state.Store || __state.Thing == null)
			{
				return;
			}
			if (context.ValidatorNegatives.Count >= 8192)
			{
				Interlocked.Increment(ref validatorCapacityBypass);
			}
			else
			{
				if (context.ValidatorNegatives.ContainsKey(__state.Key))
				{
					return;
				}
				ValidatorTrustState trust = GetTrust(__state.Key.Method, __state.Scanner);
				trust.ObserveStore();
				ThingFingerprint fingerprint = ThingFingerprint.CaptureCheap(__state.Thing);
				bool primed = false;
				if (trust.PreferEager)
				{
					Interlocked.Increment(ref validatorAdaptiveEagerStores);
					if (TryReadForbidden(context.Pawn, __state.Thing, out var forbidden2))
					{
						fingerprint = fingerprint.WithForbidden(forbidden2);
						primed = true;
						Interlocked.Increment(ref validatorAdaptiveEagerCaptures);
					}
					else
					{
						Interlocked.Increment(ref validatorAdaptiveEagerFallbackLazy);
						Interlocked.Increment(ref validatorLazyStores);
					}
				}
				else
				{
					Interlocked.Increment(ref validatorLazyStores);
					Interlocked.Increment(ref validatorStoreForbiddenReadsAvoided);
				}
				context.ValidatorNegatives.Add(__state.Key, new ValidatorNegativeEntry(fingerprint, primed));
				Interlocked.Increment(ref validatorNegativeStores);
			}
		}
	}

	public static bool ReachPrefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, bool __runOriginal, ref bool __result, ref ReachCallState __state)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_0075: Unknown result type (might be due to invalid IL or missing references)
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_008d: Unknown result type (might be due to invalid IL or missing references)
		//IL_008e: Unknown result type (might be due to invalid IL or missing references)
		//IL_008f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0090: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0134: Unknown result type (might be due to invalid IL or missing references)
		//IL_01ac: Unknown result type (might be due to invalid IL or missing references)
		//IL_018c: Unknown result type (might be due to invalid IL or missing references)
		__state = default(ReachCallState);
		if (!RimMTThreadGuard.IsMainThread)
		{
			return true;
		}
		Interlocked.Increment(ref reachObserved);
		TransactionContext transactionContext = current;
		if (!__runOriginal || transactionContext == null || depth <= 0 || __instance == null || transactionContext.Pawn == null || traverseParams.pawn == null || traverseParams.pawn != transactionContext.Pawn)
		{
			return true;
		}
		if (!((IntVec3)(ref start)).IsValid || !((LocalTargetInfo)(ref dest)).IsValid || ((Thing)transactionContext.Pawn).Map == null || start != ((Thing)transactionContext.Pawn).Position)
		{
			return true;
		}
		ReachKey reachKey = new ReachKey(__instance, start, dest, peMode, traverseParams);
		if (transactionContext.ReachQuarantined.Contains(reachKey))
		{
			Interlocked.Increment(ref reachLocalQuarantineBypass);
			return true;
		}
		if (!transactionContext.ReachMemo.TryGetValue(reachKey, out var value))
		{
			__state = ReachCallState.ForStore(transactionContext, reachKey, dest);
			return true;
		}
		if (!value.Fingerprint.Matches(dest))
		{
			transactionContext.ReachMemo.Remove(reachKey);
			Interlocked.Increment(ref reachMutationBypass);
			__state = ReachCallState.ForStore(transactionContext, reachKey, dest);
			return true;
		}
		Interlocked.Increment(ref reachMemoCandidates);
		if (!reachChainAuthoritativeSafe)
		{
			Interlocked.Increment(ref reachForeignResultBypass);
			__state = ReachCallState.VerifyOnly(transactionContext, reachKey, value.Result, dest);
			Interlocked.Increment(ref reachVerifyRuns);
			return true;
		}
		transactionContext.ReachHitSerial++;
		if (transactionContext.ReachValidatedMatches < 8 || (transactionContext.ReachHitSerial & 0x3F) == 0)
		{
			Interlocked.Increment(ref reachVerifyRuns);
			__state = ReachCallState.VerifyOnly(transactionContext, reachKey, value.Result, dest);
			return true;
		}
		__result = value.Result;
		__state = ReachCallState.Authoritative(transactionContext, reachKey, value.Result, dest);
		Interlocked.Increment(ref reachAuthoritativeHits);
		return false;
	}

	public static void ReachBasePostfix(bool __result, ref ReachCallState __state)
	{
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		if (__state.AuthoritativeHit)
		{
			return;
		}
		TransactionContext context = __state.Context;
		if (context == null || current != context)
		{
			return;
		}
		if (__state.Verify)
		{
			if (__result == __state.Cached)
			{
				context.ReachValidatedMatches++;
				Interlocked.Increment(ref reachVerifyMatches);
				return;
			}
			context.ReachMemo.Remove(__state.Key);
			context.ReachQuarantined.Add(__state.Key);
			context.ReachValidatedMatches = 0;
			Interlocked.Increment(ref reachMismatches);
			Interlocked.Increment(ref reachLocalQuarantines);
		}
		else if (__state.Store)
		{
			if (context.ReachMemo.Count >= 8192)
			{
				Interlocked.Increment(ref reachCapacityBypass);
			}
			else if (!context.ReachMemo.ContainsKey(__state.Key))
			{
				context.ReachMemo.Add(__state.Key, new ReachEntry(__result, TargetFingerprint.Capture(__state.Dest)));
				Interlocked.Increment(ref reachStores);
			}
		}
	}

	public static Exception ReachFinalizer(Exception __exception, ReachCallState __state)
	{
		if (__exception != null)
		{
			Interlocked.Increment(ref reachExceptions);
			TransactionContext context = __state.Context;
			if (context != null && current == context)
			{
				context.ReachMemo.Remove(__state.Key);
			}
		}
		return __exception;
	}

	public static void ReachCacheInvalidated()
	{
		TransactionContext transactionContext = current;
		if (transactionContext != null && depth > 0)
		{
			transactionContext.ReachMemo.Clear();
			transactionContext.ReachQuarantined.Clear();
			transactionContext.ReachValidatedMatches = 0;
			Interlocked.Increment(ref reachInvalidations);
		}
	}

	private static WorkGiver_Scanner ResolveScanner(object closure)
	{
		if (closure == null)
		{
			return null;
		}
		Type type = closure.GetType();
		ScannerAccessor value;
		lock (ScannerAccessorLock)
		{
			if (!ScannerAccessors.TryGetValue(type, out value))
			{
				value = ScannerAccessor.Build(type);
				ScannerAccessors[type] = value;
			}
		}
		return value?.Read(closure);
	}

	private static ValidatorTrustState GetTrust(MethodBase method, WorkGiver_Scanner scanner)
	{
		ValidatorTrustKey key = new ValidatorTrustKey(method, scanner);
		lock (TrustLock)
		{
			if (!ValidatorTrust.TryGetValue(key, out var value))
			{
				value = new ValidatorTrustState();
				ValidatorTrust.Add(key, value);
			}
			return value;
		}
	}

	private static bool HasForeignPatches(MethodBase method)
	{
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo == null)
			{
				return false;
			}
			return HasForeign(patchInfo.Prefixes) || HasForeign(patchInfo.Postfixes) || HasForeign(patchInfo.Transpilers) || HasForeign(patchInfo.Finalizers);
		}
		catch
		{
			return true;
		}
	}

	private static bool HasForeign(IEnumerable<Patch> patches)
	{
		if (patches == null)
		{
			return false;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static ReachChainAudit InspectReachChain(MethodBase method)
	{
		ReachChainAudit reachChainAudit = new ReachChainAudit();
		try
		{
			Patches patchInfo = Harmony.GetPatchInfo(method);
			if (patchInfo == null)
			{
				return reachChainAudit;
			}
			HashSet<string> hashSet = new HashSet<string>();
			HashSet<string> hashSet2 = new HashSet<string>();
			foreach (Patch prefix in patchInfo.Prefixes)
			{
				if (prefix == null)
				{
					continue;
				}
				if (prefix.priority < reachChainAudit.MinAnyPrefixPriority)
				{
					reachChainAudit.MinAnyPrefixPriority = prefix.priority;
				}
				if (!string.Equals(prefix.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					reachChainAudit.ForeignPrefixes++;
					if (!string.IsNullOrEmpty(prefix.owner))
					{
						hashSet.Add(prefix.owner);
					}
				}
			}
			foreach (Patch postfix in patchInfo.Postfixes)
			{
				if (postfix == null)
				{
					continue;
				}
				if (postfix.priority > reachChainAudit.MaxAnyPostfixPriority)
				{
					reachChainAudit.MaxAnyPostfixPriority = postfix.priority;
				}
				if (!string.Equals(postfix.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					reachChainAudit.ForeignPostfixes++;
					if (!string.IsNullOrEmpty(postfix.owner))
					{
						hashSet2.Add(postfix.owner);
					}
					if (PostfixMutatesResult(postfix))
					{
						reachChainAudit.ResultMutatingPostfixes++;
					}
					if (PatchReadsRunOriginal(postfix))
					{
						reachChainAudit.RunOriginalPostfixes++;
					}
				}
			}
			foreach (Patch transpiler in patchInfo.Transpilers)
			{
				if (transpiler != null && !string.Equals(transpiler.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					reachChainAudit.ForeignTranspilers++;
				}
			}
			foreach (Patch finalizer in patchInfo.Finalizers)
			{
				if (finalizer != null && !string.Equals(finalizer.owner, "allen.rimmt", StringComparison.Ordinal))
				{
					reachChainAudit.ForeignFinalizers++;
				}
			}
			reachChainAudit.ForeignPrefixOwners = new string[hashSet.Count];
			hashSet.CopyTo(reachChainAudit.ForeignPrefixOwners);
			reachChainAudit.ForeignPostfixOwners = new string[hashSet2.Count];
			hashSet2.CopyTo(reachChainAudit.ForeignPostfixOwners);
			reachChainAudit.PriorityRoom = reachChainAudit.MinAnyPrefixPriority > int.MinValue && reachChainAudit.MaxAnyPostfixPriority < int.MaxValue;
			return reachChainAudit;
		}
		catch
		{
			reachChainAudit.Unknown = true;
			reachChainAudit.PriorityRoom = false;
			return reachChainAudit;
		}
	}

	private static bool PostfixMutatesResult(Patch patch)
	{
		MethodInfo methodInfo = ((patch == null) ? null : patch.PatchMethod);
		if (methodInfo == null)
		{
			return true;
		}
		if (methodInfo.ReturnType == typeof(bool))
		{
			return true;
		}
		ParameterInfo[] parameters = methodInfo.GetParameters();
		foreach (ParameterInfo parameterInfo in parameters)
		{
			if (parameterInfo.Name == "__result" && parameterInfo.ParameterType.IsByRef && parameterInfo.ParameterType.GetElementType() == typeof(bool))
			{
				return true;
			}
		}
		return false;
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

	private static void CountAdaptiveModes(out int lazy, out int eager)
	{
		lazy = 0;
		eager = 0;
		lock (TrustLock)
		{
			foreach (ValidatorTrustState value in ValidatorTrust.Values)
			{
				if (value != null && value.PreferEager)
				{
					eager++;
				}
				else
				{
					lazy++;
				}
			}
		}
	}

	internal static string Summary()
	{
		CountAdaptiveModes(out var lazy, out var eager);
		return "T21 foundation transaction: installed=" + installed + ", packagePatched=" + packagePatched + ", validatorMethods=" + validatorMethodsPatched + ", validatorForeignSkipped=" + validatorMethodsSkippedForeign + ", reachPatched=" + reachPatched + ", reachChainAuthoritativeSafe=" + reachChainAuthoritativeSafe + ", reachChain[foreignPrefixes=" + reachForeignPrefixes + ", foreignPostfixes=" + reachForeignPostfixes + ", resultMutators=" + reachForeignResultPostfixes + ", runOriginalReaders=" + reachRunOriginalPostfixes + ", transpilers=" + reachForeignTranspilers + ", finalizers=" + reachForeignFinalizers + ", auditUnknown=" + reachChainAuditUnknown + ", replayPrefixPriority=" + reachReplayPrefixPriority + ", basePostfixPriority=" + reachBasePostfixPriority + "], packages=" + Interlocked.Read(ref packages) + ", nested=" + Interlocked.Read(ref nestedPackages) + ", validator[observed=" + Interlocked.Read(ref validatorObserved) + ", stores=" + Interlocked.Read(ref validatorNegativeStores) + ", memoCandidates=" + Interlocked.Read(ref validatorMemoCandidates) + ", authoritativeHits=" + Interlocked.Read(ref validatorAuthoritativeHits) + ", verify=" + Interlocked.Read(ref validatorVerifyRuns) + ", matches=" + Interlocked.Read(ref validatorVerifyMatches) + ", mismatches=" + Interlocked.Read(ref validatorMismatches) + ", quarantines=" + Interlocked.Read(ref validatorQuarantines) + ", fingerprintBypass=" + Interlocked.Read(ref validatorFingerprintBypass) + ", capBypass=" + Interlocked.Read(ref validatorCapacityBypass) + ", scannerResolveBypass=" + Interlocked.Read(ref validatorScannerResolveBypass) + ", positiveLive=" + Interlocked.Read(ref validatorPositiveLive) + ", lazy[stores=" + Interlocked.Read(ref validatorLazyStores) + ", repeatProbes=" + Interlocked.Read(ref validatorLazyRepeatProbes) + ", primeSuccess=" + Interlocked.Read(ref validatorLazyPrimeSuccess) + ", primePositive=" + Interlocked.Read(ref validatorLazyPrimePositive) + ", primeUnstable=" + Interlocked.Read(ref validatorLazyPrimeUnstable) + ", forbiddenReads=" + Interlocked.Read(ref validatorForbiddenReads) + ", storeForbiddenReadsAvoided=" + Interlocked.Read(ref validatorStoreForbiddenReadsAvoided) + ", adaptive[firstRepeats=" + Interlocked.Read(ref validatorAdaptiveFirstRepeats) + ", eagerStores=" + Interlocked.Read(ref validatorAdaptiveEagerStores) + ", eagerCaptures=" + Interlocked.Read(ref validatorAdaptiveEagerCaptures) + ", eagerFallbackLazy=" + Interlocked.Read(ref validatorAdaptiveEagerFallbackLazy) + ", switchToEager=" + Interlocked.Read(ref validatorAdaptiveSwitchToEager) + ", switchToLazy=" + Interlocked.Read(ref validatorAdaptiveSwitchToLazy) + ", modesLazy/Eager=" + lazy + "/" + eager + ", enter>=1/8@128+trusted, return<1/16@256]]], reach[observed=" + Interlocked.Read(ref reachObserved) + ", stores=" + Interlocked.Read(ref reachStores) + ", memoCandidates=" + Interlocked.Read(ref reachMemoCandidates) + ", authoritativeHits=" + Interlocked.Read(ref reachAuthoritativeHits) + ", verify=" + Interlocked.Read(ref reachVerifyRuns) + ", matches=" + Interlocked.Read(ref reachVerifyMatches) + ", mismatches=" + Interlocked.Read(ref reachMismatches) + ", localQuarantines=" + Interlocked.Read(ref reachLocalQuarantines) + ", localQuarantineBypass=" + Interlocked.Read(ref reachLocalQuarantineBypass) + ", runtimeQuarantined=" + reachRuntimeQuarantined + ", foreignResultBypass=" + Interlocked.Read(ref reachForeignResultBypass) + ", mutationBypass=" + Interlocked.Read(ref reachMutationBypass) + ", invalidations=" + Interlocked.Read(ref reachInvalidations) + ", capBypass=" + Interlocked.Read(ref reachCapacityBypass) + ", exceptions=" + Interlocked.Read(ref reachExceptions) + "], installFailures=" + installFailures + ". One synchronous package only; validator caches false only; T32-B.1 adaptive fingerprint mode is keyed only by validator method+scanner trust state; no Job/JobOnThing/reservation/priority/cross-package result is cached.";
	}

	private static bool TryReadForbidden(Pawn pawn, Thing thing, out bool forbidden)
	{
		forbidden = false;
		if (pawn == null || thing == null)
		{
			return false;
		}
		try
		{
			forbidden = ForbidUtility.IsForbidden(thing, pawn);
			Interlocked.Increment(ref validatorForbiddenReads);
			return true;
		}
		catch
		{
			return false;
		}
	}
}
