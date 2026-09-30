using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class ReservationTransaction093T32A
{
	internal struct CallState
	{
		internal PackageContext Context;

		internal ReserveKey Key;

		internal LocalTargetInfo Target;

		internal bool Store;

		internal bool Verify;

		internal bool PositiveShadow;

		internal bool PositiveTrustSample;

		internal bool PositiveAuthoritativeHit;

		internal bool AuthoritativeHit;

		internal static CallState ForStore(PackageContext context, ReserveKey key, LocalTargetInfo target)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new CallState
			{
				Context = context,
				Key = key,
				Target = target,
				Store = true
			};
		}

		internal static CallState ForVerify(PackageContext context, ReserveKey key, LocalTargetInfo target)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new CallState
			{
				Context = context,
				Key = key,
				Target = target,
				Verify = true
			};
		}

		internal static CallState ForPositiveShadow(PackageContext context, ReserveKey key, LocalTargetInfo target, bool trustSample)
		{
			//IL_001a: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			return new CallState
			{
				Context = context,
				Key = key,
				Target = target,
				Store = true,
				PositiveShadow = true,
				PositiveTrustSample = trustSample
			};
		}

		internal static CallState ForPositiveAuthoritative(PackageContext context)
		{
			return new CallState
			{
				Context = context,
				PositiveAuthoritativeHit = true
			};
		}

		internal static CallState ForAuthoritative(PackageContext context)
		{
			return new CallState
			{
				Context = context,
				AuthoritativeHit = true
			};
		}
	}

	internal sealed class PackageContext
	{
		internal readonly Pawn Pawn;

		internal readonly long Generation;

		internal readonly Dictionary<ReserveKey, NegativeEntry> Negatives = new Dictionary<ReserveKey, NegativeEntry>();

		internal readonly Dictionary<ReserveKey, PositiveEntry> PositiveShadows = new Dictionary<ReserveKey, PositiveEntry>();

		internal long MutationEpoch;

		internal long HitSerial;

		internal int ValidatedMatches;

		internal long PositiveHitSerial;

		internal int PositiveValidatedMatches;

		internal PackageContext(Pawn pawn, long generation)
		{
			Pawn = pawn;
			Generation = generation;
		}
	}

	internal struct NegativeEntry
	{
		internal readonly long MutationEpoch;

		internal readonly TargetFingerprint Fingerprint;

		internal NegativeEntry(long mutationEpoch, TargetFingerprint fingerprint)
		{
			MutationEpoch = mutationEpoch;
			Fingerprint = fingerprint;
		}
	}

	internal struct PositiveEntry
	{
		internal readonly long MutationEpoch;

		internal readonly TargetFingerprint Fingerprint;

		internal PositiveEntry(long mutationEpoch, TargetFingerprint fingerprint)
		{
			MutationEpoch = mutationEpoch;
			Fingerprint = fingerprint;
		}
	}

	internal struct ReserveKey : IEquatable<ReserveKey>
	{
		internal readonly ReservationManager Manager;

		internal readonly Pawn Pawn;

		internal readonly bool HasThing;

		internal readonly Thing Thing;

		internal readonly IntVec3 Cell;

		internal readonly int MaxPawns;

		internal readonly int StackCount;

		internal readonly ReservationLayerDef Layer;

		internal readonly bool IgnoreOtherReservations;

		internal ReserveKey(ReservationManager manager, Pawn pawn, LocalTargetInfo target, int maxPawns, int stackCount, ReservationLayerDef layer, bool ignoreOtherReservations)
		{
			//IL_0037: Unknown result type (might be due to invalid IL or missing references)
			//IL_003c: Unknown result type (might be due to invalid IL or missing references)
			Manager = manager;
			Pawn = pawn;
			HasThing = ((LocalTargetInfo)(ref target)).HasThing;
			Thing = (((LocalTargetInfo)(ref target)).HasThing ? ((LocalTargetInfo)(ref target)).Thing : null);
			Cell = ((LocalTargetInfo)(ref target)).Cell;
			MaxPawns = maxPawns;
			StackCount = stackCount;
			Layer = layer;
			IgnoreOtherReservations = ignoreOtherReservations;
		}

		public bool Equals(ReserveKey other)
		{
			//IL_0039: Unknown result type (might be due to invalid IL or missing references)
			//IL_003f: Unknown result type (might be due to invalid IL or missing references)
			if (Manager == other.Manager && Pawn == other.Pawn && HasThing == other.HasThing && Thing == other.Thing && Cell == other.Cell && MaxPawns == other.MaxPawns && StackCount == other.StackCount && Layer == other.Layer)
			{
				return IgnoreOtherReservations == other.IgnoreOtherReservations;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ReserveKey)
			{
				return Equals((ReserveKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0067: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			return (int)(((((((((((((uint)(((((Manager != null) ? RuntimeHelpers.GetHashCode(Manager) : 0) * 397) ^ ((Pawn != null) ? RuntimeHelpers.GetHashCode(Pawn) : 0)) * 397) ^ (HasThing ? 1u : 0u)) * 397) ^ (uint)((Thing != null) ? RuntimeHelpers.GetHashCode(Thing) : 0)) * 397) ^ (uint)((object)Cell/*cast due to .constrained prefix*/).GetHashCode()) * 397) ^ (uint)MaxPawns) * 397) ^ (uint)StackCount) * 397) ^ (uint)((Layer != null) ? RuntimeHelpers.GetHashCode(Layer) : 0)) * 397) ^ (IgnoreOtherReservations ? 1 : 0);
		}
	}

	internal struct TargetFingerprint
	{
		internal readonly bool HasThing;

		internal readonly Thing Thing;

		internal readonly Map MapHeld;

		internal readonly IntVec3 PositionHeld;

		internal readonly bool Spawned;

		internal readonly int StackCount;

		internal TargetFingerprint(bool hasThing, Thing thing, Map mapHeld, IntVec3 positionHeld, bool spawned, int stackCount)
		{
			//IL_0016: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			HasThing = hasThing;
			Thing = thing;
			MapHeld = mapHeld;
			PositionHeld = positionHeld;
			Spawned = spawned;
			StackCount = stackCount;
		}

		internal static TargetFingerprint Capture(LocalTargetInfo target)
		{
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			if (!((LocalTargetInfo)(ref target)).HasThing)
			{
				return new TargetFingerprint(hasThing: false, null, null, IntVec3.Invalid, spawned: false, 0);
			}
			Thing thing = ((LocalTargetInfo)(ref target)).Thing;
			if (thing == null)
			{
				return new TargetFingerprint(hasThing: true, null, null, IntVec3.Invalid, spawned: false, 0);
			}
			return new TargetFingerprint(hasThing: true, thing, thing.MapHeld, thing.PositionHeld, thing.Spawned, thing.stackCount);
		}

		internal bool Matches(LocalTargetInfo target)
		{
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			if (HasThing != ((LocalTargetInfo)(ref target)).HasThing)
			{
				return false;
			}
			if (!HasThing)
			{
				return true;
			}
			Thing thing = ((LocalTargetInfo)(ref target)).Thing;
			if (Thing == thing && thing != null && MapHeld == thing.MapHeld && PositionHeld == thing.PositionHeld && Spawned == thing.Spawned)
			{
				return StackCount == thing.stackCount;
			}
			return false;
		}
	}

	internal const string FeatureId = "ai.reservationTransaction";

	private const int Capacity = 8192;

	private const int PositiveShadowCapacity = 8192;

	private const int WarmupMatches = 16;

	private const int VerifyMask = 63;

	private const int PositiveWarmupMatches = 32;

	private const int PositiveVerifyMask = 63;

	[ThreadStatic]
	private static PackageContext current;

	private static bool installed;

	private static bool canReservePatched;

	private static bool chainAuthoritativeSafe;

	private static bool runtimeQuarantined;

	private static bool positiveRuntimeQuarantined;

	private static int installFailures;

	private static MethodBase canReserveTarget;

	private static long chainAudits;

	private static long lateUnsafeTransitions;

	private static int foreignPrefixes;

	private static int foreignPostfixes;

	private static int foreignTranspilers;

	private static int foreignFinalizers;

	private static bool chainAuditUnknown;

	private static int mutationMethodsPatched;

	private static int mutationMethodsMissing;

	private static long packages;

	private static long observed;

	private static long inScope;

	private static long stores;

	private static long memoCandidates;

	private static long authoritativeHits;

	private static long verifyRuns;

	private static long verifyMatches;

	private static long mismatches;

	private static long quarantines;

	private static long positiveLive;

	private static long negativeLive;

	private static long positiveShadowStores;

	private static long positiveShadowCandidates;

	private static long positiveShadowMatches;

	private static long positiveShadowMismatches;

	private static long positiveShadowAuthorityEligible;

	private static long positiveShadowAuthorityUnsafe;

	private static long positiveShadowFingerprintBypass;

	private static long positiveShadowCapacityBypass;

	private static long positiveShadowMutationClears;

	private static long positiveShadowEntriesCleared;

	private static long positiveVerifyRuns;

	private static long positiveVerifyMatches;

	private static long positiveVerifyMismatches;

	private static long positiveAuthoritativeHits;

	private static long positiveQuarantines;

	private static long positiveAuthorityBypass;

	private static long fingerprintBypass;

	private static long mutationInvalidations;

	private static long capacityBypass;

	private static long runOriginalBypass;

	private static long pawnBypass;

	private static long invalidTargetBypass;

	private static long authorityBypass;

	private static long exceptions;

	internal static bool Installed => installed;

	internal static void Apply(Harmony harmony)
	{
		//IL_00cf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_0106: Expected O, but got Unknown
		//IL_0106: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(ReservationManager), "CanReserve", new Type[6]
			{
				typeof(Pawn),
				typeof(LocalTargetInfo),
				typeof(int),
				typeof(int),
				typeof(ReservationLayerDef),
				typeof(bool)
			}, (Type[])null);
			if (methodBase == null)
			{
				installFailures++;
				Log.Warning("[RimMT] T32-A Reservation transaction unavailable: exact ReservationManager.CanReserve signature not found.");
				return;
			}
			canReserveTarget = methodBase;
			AuditChain(methodBase);
			chainAuthoritativeSafe = !chainAuditUnknown && foreignTranspilers == 0 && foreignFinalizers == 0;
			harmony.Patch(methodBase, new HarmonyMethod(typeof(ReservationTransaction093T32A), "CanReservePrefix", (Type[])null)
			{
				priority = -400
			}, (HarmonyMethod)null, (HarmonyMethod)null, new HarmonyMethod(typeof(ReservationTransaction093T32A), "CanReserveFinalizer", (Type[])null)
			{
				priority = -400
			});
			canReservePatched = true;
			PatchMutationMethods(harmony);
			installed = canReservePatched && mutationMethodsPatched > 0 && mutationMethodsMissing == 0;
			Log.Message("[RimMT] T32-A Reservation transaction installed=" + installed + ", canReserve=" + canReservePatched + ", authoritySafe=" + chainAuthoritativeSafe + ", chain[audits=" + Interlocked.Read(ref chainAudits) + ", lateUnsafeTransitions=" + Interlocked.Read(ref lateUnsafeTransitions) + ", foreignPrefixes=" + foreignPrefixes + ", foreignPostfixes=" + foreignPostfixes + ", foreignTranspilers=" + foreignTranspilers + ", foreignFinalizers=" + foreignFinalizers + ", auditUnknown=" + chainAuditUnknown + "], mutationMethods=" + mutationMethodsPatched + ", mutationMissing=" + mutationMethodsMissing + ". False-only; one synchronous T28 package; mutation invalidation; Vanilla/live parity remains authority.");
		}
		catch (Exception ex)
		{
			installFailures++;
			installed = false;
			Log.Warning("[RimMT] T32-A Reservation transaction failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void BeginPackage(Pawn pawn, long generation)
	{
		current = null;
		if (installed && pawn != null)
		{
			long num = Interlocked.Increment(ref packages);
			if (num == 1 || (num & 0x3FF) == 0L)
			{
				ReauditChain();
			}
			current = new PackageContext(pawn, generation);
		}
	}

	internal static void EndPackage()
	{
		current = null;
	}

	public static bool CanReservePrefix(ReservationManager __instance, Pawn __0, LocalTargetInfo __1, int __2, int __3, ReservationLayerDef __4, bool __5, bool __runOriginal, ref bool __result, ref CallState __state)
	{
		//IL_0043: Unknown result type (might be due to invalid IL or missing references)
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Unknown result type (might be due to invalid IL or missing references)
		//IL_0228: Unknown result type (might be due to invalid IL or missing references)
		//IL_0202: Unknown result type (might be due to invalid IL or missing references)
		//IL_01db: Unknown result type (might be due to invalid IL or missing references)
		//IL_00fe: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d8: Unknown result type (might be due to invalid IL or missing references)
		//IL_025e: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_0160: Unknown result type (might be due to invalid IL or missing references)
		//IL_019f: Unknown result type (might be due to invalid IL or missing references)
		__state = default(CallState);
		Interlocked.Increment(ref observed);
		PackageContext packageContext = current;
		if (packageContext == null || !JobSearchPackageContext093T28.InScope)
		{
			return true;
		}
		Interlocked.Increment(ref inScope);
		if (!__runOriginal)
		{
			Interlocked.Increment(ref runOriginalBypass);
			return true;
		}
		LocalTargetInfo target = __1;
		bool ignoreOtherReservations = __5;
		if (__instance == null || __0 == null || __0 != packageContext.Pawn)
		{
			Interlocked.Increment(ref pawnBypass);
			return true;
		}
		if (!((LocalTargetInfo)(ref target)).IsValid)
		{
			Interlocked.Increment(ref invalidTargetBypass);
			return true;
		}
		ReserveKey key = new ReserveKey(__instance, __0, target, __2, __3, __4, ignoreOtherReservations);
		if (!packageContext.Negatives.TryGetValue(key, out var value))
		{
			if (packageContext.PositiveShadows.TryGetValue(key, out var value2))
			{
				if (value2.MutationEpoch != packageContext.MutationEpoch || !value2.Fingerprint.Matches(target))
				{
					packageContext.PositiveShadows.Remove(key);
					Interlocked.Increment(ref positiveShadowFingerprintBypass);
					__state = CallState.ForStore(packageContext, key, target);
					return true;
				}
				Interlocked.Increment(ref positiveShadowCandidates);
				bool num = chainAuthoritativeSafe && !runtimeQuarantined && !positiveRuntimeQuarantined;
				if (chainAuthoritativeSafe)
				{
					Interlocked.Increment(ref positiveShadowAuthorityEligible);
				}
				else
				{
					Interlocked.Increment(ref positiveShadowAuthorityUnsafe);
				}
				if (!num)
				{
					Interlocked.Increment(ref positiveAuthorityBypass);
					__state = CallState.ForPositiveShadow(packageContext, key, target, trustSample: false);
					return true;
				}
				packageContext.PositiveHitSerial++;
				if (packageContext.PositiveValidatedMatches < 32 || (packageContext.PositiveHitSerial & 0x3F) == 0)
				{
					__state = CallState.ForPositiveShadow(packageContext, key, target, trustSample: true);
					Interlocked.Increment(ref positiveVerifyRuns);
					return true;
				}
				__result = true;
				__state = CallState.ForPositiveAuthoritative(packageContext);
				Interlocked.Increment(ref positiveAuthoritativeHits);
				return false;
			}
			__state = CallState.ForStore(packageContext, key, target);
			return true;
		}
		if (value.MutationEpoch != packageContext.MutationEpoch || !value.Fingerprint.Matches(target))
		{
			packageContext.Negatives.Remove(key);
			Interlocked.Increment(ref fingerprintBypass);
			__state = CallState.ForStore(packageContext, key, target);
			return true;
		}
		Interlocked.Increment(ref memoCandidates);
		if (!chainAuthoritativeSafe || runtimeQuarantined)
		{
			Interlocked.Increment(ref authorityBypass);
			__state = CallState.ForVerify(packageContext, key, target);
			Interlocked.Increment(ref verifyRuns);
			return true;
		}
		packageContext.HitSerial++;
		if (packageContext.ValidatedMatches < 16 || (packageContext.HitSerial & 0x3F) == 0)
		{
			__state = CallState.ForVerify(packageContext, key, target);
			Interlocked.Increment(ref verifyRuns);
			return true;
		}
		__result = false;
		__state = CallState.ForAuthoritative(packageContext);
		Interlocked.Increment(ref authoritativeHits);
		return false;
	}

	public static Exception CanReserveFinalizer(Exception __exception, bool __result, CallState __state)
	{
		//IL_020e: Unknown result type (might be due to invalid IL or missing references)
		//IL_019d: Unknown result type (might be due to invalid IL or missing references)
		if (__exception != null)
		{
			Interlocked.Increment(ref exceptions);
			return __exception;
		}
		PackageContext context = __state.Context;
		if (context == null || current != context)
		{
			return __exception;
		}
		if (__state.AuthoritativeHit || __state.PositiveAuthoritativeHit)
		{
			return __exception;
		}
		if (__state.PositiveShadow)
		{
			if (__result)
			{
				Interlocked.Increment(ref positiveShadowMatches);
				if (__state.PositiveTrustSample)
				{
					context.PositiveValidatedMatches++;
					Interlocked.Increment(ref positiveVerifyMatches);
				}
				return __exception;
			}
			context.PositiveShadows.Remove(__state.Key);
			Interlocked.Increment(ref positiveShadowMismatches);
			if (__state.PositiveTrustSample)
			{
				int count = context.PositiveShadows.Count;
				context.PositiveShadows.Clear();
				context.PositiveValidatedMatches = 0;
				context.PositiveHitSerial = 0L;
				positiveRuntimeQuarantined = true;
				Interlocked.Increment(ref positiveVerifyMismatches);
				Interlocked.Increment(ref positiveQuarantines);
				if (count > 0)
				{
					Interlocked.Add(ref positiveShadowEntriesCleared, count);
				}
			}
		}
		if (__state.Verify)
		{
			if (!__result)
			{
				context.ValidatedMatches++;
				Interlocked.Increment(ref verifyMatches);
			}
			else
			{
				context.Negatives.Clear();
				context.ValidatedMatches = 0;
				runtimeQuarantined = true;
				Interlocked.Increment(ref mismatches);
				Interlocked.Increment(ref quarantines);
			}
			return __exception;
		}
		if (!__state.Store)
		{
			return __exception;
		}
		if (__result)
		{
			Interlocked.Increment(ref positiveLive);
			if (context.PositiveShadows.Count >= 8192)
			{
				Interlocked.Increment(ref positiveShadowCapacityBypass);
				return __exception;
			}
			if (!context.PositiveShadows.ContainsKey(__state.Key))
			{
				context.PositiveShadows.Add(__state.Key, new PositiveEntry(context.MutationEpoch, TargetFingerprint.Capture(__state.Target)));
				Interlocked.Increment(ref positiveShadowStores);
			}
			return __exception;
		}
		Interlocked.Increment(ref negativeLive);
		if (context.Negatives.Count >= 8192)
		{
			Interlocked.Increment(ref capacityBypass);
			return __exception;
		}
		if (!context.Negatives.ContainsKey(__state.Key))
		{
			context.Negatives.Add(__state.Key, new NegativeEntry(context.MutationEpoch, TargetFingerprint.Capture(__state.Target)));
			Interlocked.Increment(ref stores);
		}
		return __exception;
	}

	public static void ReservationMutationPrefix()
	{
		PackageContext packageContext = current;
		if (packageContext != null)
		{
			packageContext.MutationEpoch++;
			packageContext.Negatives.Clear();
			int count = packageContext.PositiveShadows.Count;
			if (count > 0)
			{
				packageContext.PositiveShadows.Clear();
				Interlocked.Increment(ref positiveShadowMutationClears);
				Interlocked.Add(ref positiveShadowEntriesCleared, count);
			}
			packageContext.PositiveValidatedMatches = 0;
			packageContext.PositiveHitSerial = 0L;
			packageContext.ValidatedMatches = 0;
			Interlocked.Increment(ref mutationInvalidations);
		}
	}

	private static void PatchMutationMethods(Harmony harmony)
	{
		//IL_00b1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Expected O, but got Unknown
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		string[] array = new string[5] { "Reserve", "Release", "ReleaseClaimedBy", "ReleaseAllClaimedBy", "ReleaseAllForTarget" };
		MethodInfo[] methods;
		try
		{
			methods = typeof(ReservationManager).GetMethods(BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		catch
		{
			installFailures++;
			return;
		}
		for (int i = 0; i < array.Length; i++)
		{
			int num = 0;
			foreach (MethodInfo methodInfo in methods)
			{
				if (!(methodInfo == null) && !(methodInfo.Name != array[i]) && hashSet.Add(methodInfo))
				{
					num++;
					try
					{
						harmony.Patch((MethodBase)methodInfo, new HarmonyMethod(typeof(ReservationTransaction093T32A), "ReservationMutationPrefix", (Type[])null)
						{
							priority = 1100
						}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
						mutationMethodsPatched++;
					}
					catch
					{
						installFailures++;
					}
				}
			}
			if (num == 0)
			{
				mutationMethodsMissing++;
			}
		}
	}

	private static void ReauditChain()
	{
		MethodBase methodBase = canReserveTarget;
		if (methodBase == null)
		{
			chainAuditUnknown = true;
			chainAuthoritativeSafe = false;
			return;
		}
		bool num = chainAuthoritativeSafe;
		AuditChain(methodBase);
		Interlocked.Increment(ref chainAudits);
		bool flag = !chainAuditUnknown && foreignTranspilers == 0 && foreignFinalizers == 0;
		if (num && !flag)
		{
			chainAuthoritativeSafe = false;
			PackageContext packageContext = current;
			if (packageContext != null)
			{
				packageContext.Negatives.Clear();
				packageContext.PositiveShadows.Clear();
				packageContext.ValidatedMatches = 0;
				packageContext.PositiveValidatedMatches = 0;
				packageContext.PositiveHitSerial = 0L;
			}
			Interlocked.Increment(ref lateUnsafeTransitions);
		}
		else if (!runtimeQuarantined)
		{
			chainAuthoritativeSafe = flag;
		}
	}

	private static void AuditChain(MethodBase target)
	{
		try
		{
			chainAuditUnknown = false;
			Patches patchInfo = Harmony.GetPatchInfo(target);
			if (patchInfo == null)
			{
				foreignPrefixes = (foreignPostfixes = (foreignTranspilers = (foreignFinalizers = 0)));
				return;
			}
			foreignPrefixes = CountForeign(patchInfo.Prefixes);
			foreignPostfixes = CountForeign(patchInfo.Postfixes);
			foreignTranspilers = CountForeign(patchInfo.Transpilers);
			foreignFinalizers = CountForeign(patchInfo.Finalizers);
		}
		catch
		{
			chainAuditUnknown = true;
		}
	}

	private static int CountForeign(IEnumerable<Patch> patches)
	{
		int num = 0;
		if (patches == null)
		{
			return num;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				num++;
			}
		}
		return num;
	}

	internal static string Summary()
	{
		return "T32-A Reservation transaction: installed=" + installed + ", canReservePatched=" + canReservePatched + ", chainAuthoritativeSafe=" + chainAuthoritativeSafe + ", runtimeQuarantined=" + runtimeQuarantined + ", chain[foreignPrefixes=" + foreignPrefixes + ", foreignPostfixes=" + foreignPostfixes + ", foreignTranspilers=" + foreignTranspilers + ", foreignFinalizers=" + foreignFinalizers + ", auditUnknown=" + chainAuditUnknown + "], mutationMethods[patched/missing]=" + mutationMethodsPatched + "/" + mutationMethodsMissing + ", packages=" + Interlocked.Read(ref packages) + ", calls[observed/inScope]=" + Interlocked.Read(ref observed) + "/" + Interlocked.Read(ref inScope) + ", negative[stores/memoCandidates/authoritativeHits]=" + Interlocked.Read(ref stores) + "/" + Interlocked.Read(ref memoCandidates) + "/" + Interlocked.Read(ref authoritativeHits) + ", verify[runs/matches/mismatches/quarantines]=" + Interlocked.Read(ref verifyRuns) + "/" + Interlocked.Read(ref verifyMatches) + "/" + Interlocked.Read(ref mismatches) + "/" + Interlocked.Read(ref quarantines) + ", live[positive/negative]=" + Interlocked.Read(ref positiveLive) + "/" + Interlocked.Read(ref negativeLive) + ", positiveShadow[stores/candidates/liveMatches/liveMismatches]=" + Interlocked.Read(ref positiveShadowStores) + "/" + Interlocked.Read(ref positiveShadowCandidates) + "/" + Interlocked.Read(ref positiveShadowMatches) + "/" + Interlocked.Read(ref positiveShadowMismatches) + ", authorityEligible/unsafe=" + Interlocked.Read(ref positiveShadowAuthorityEligible) + "/" + Interlocked.Read(ref positiveShadowAuthorityUnsafe) + ", fingerprintBypass/capacityBypass=" + Interlocked.Read(ref positiveShadowFingerprintBypass) + "/" + Interlocked.Read(ref positiveShadowCapacityBypass) + ", mutationClears/entriesCleared=" + Interlocked.Read(ref positiveShadowMutationClears) + "/" + Interlocked.Read(ref positiveShadowEntriesCleared) + "], positiveReplay[runtimeQuarantined=" + positiveRuntimeQuarantined + ", warmup=" + 32 + ", verifyEvery=" + 64 + ", verify[runs/matches/mismatches/quarantines]=" + Interlocked.Read(ref positiveVerifyRuns) + "/" + Interlocked.Read(ref positiveVerifyMatches) + "/" + Interlocked.Read(ref positiveVerifyMismatches) + "/" + Interlocked.Read(ref positiveQuarantines) + ", authoritativeHits=" + Interlocked.Read(ref positiveAuthoritativeHits) + ", authorityBypass=" + Interlocked.Read(ref positiveAuthorityBypass) + "], invalidation[reservationMutation/fingerprint]=" + Interlocked.Read(ref mutationInvalidations) + "/" + Interlocked.Read(ref fingerprintBypass) + ", bypass[authority/runOriginal/pawn/invalidTarget/capacity]=" + Interlocked.Read(ref authorityBypass) + "/" + Interlocked.Read(ref runOriginalBypass) + "/" + Interlocked.Read(ref pawnBypass) + "/" + Interlocked.Read(ref invalidTargetBypass) + "/" + Interlocked.Read(ref capacityBypass) + ", exceptions=" + Interlocked.Read(ref exceptions) + ", installFailures=" + installFailures + ". T32-A false replay and T32-C.1 positive replay use independent trust; positive authority requires prior exact live=true observation + stable fingerprint/epoch + 32 live matches, then keeps 1/64 live parity; any positive parity mismatch quarantines positive replay only. Reservation mutations clear both result classes and package trust; foreign transpiler/finalizer or authority loss => live fallback; no reservation/Job/priority/reachability/cross-package result is created or cached.";
	}
}
