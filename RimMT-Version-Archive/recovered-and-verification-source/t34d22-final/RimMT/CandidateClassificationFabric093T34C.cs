using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class CandidateClassificationFabric093T34C
{
	internal sealed class ClassificationPlan
	{
		internal readonly KernelKind Kernel;

		internal readonly byte[] RejectReasons;

		internal readonly byte[] ShadowReasons;

		internal int ConsumptionNoted;

		internal int Count => RejectReasons.Length;

		internal ClassificationPlan(KernelKind kernel, byte[] rejects, byte[] shadows)
		{
			Kernel = kernel;
			RejectReasons = rejects;
			ShadowReasons = shadows;
		}
	}

	internal enum KernelKind : byte
	{
		None,
		Corpse,
		Pawn,
		Fire,
		HoldingTarget,
		Refuel
	}

	private sealed class KernelCounters
	{
		internal long Observed;

		internal long Resolved;

		internal long AuthorityBypass;

		internal long PlanHits;

		internal long PlanMisses;

		internal long ConsumedQueries;

		internal long CandidatesRejected;

		internal long ParityChecks;

		internal long ParityFailures;

		internal long Quarantined;

		internal long ShadowAuthorityUnsafe;
	}

	private enum ScannerResolveFailure : byte
	{
		None,
		NoValidator,
		NoTarget,
		NoAccessorPath,
		NullPathValue,
		Exception
	}

	private sealed class ScannerMissEvidence
	{
		internal readonly Type TargetType;

		internal readonly MethodInfo Method;

		internal readonly ScannerResolveFailure Failure;

		internal long Count;

		internal ScannerMissEvidence(Type targetType, MethodInfo method, ScannerResolveFailure failure)
		{
			TargetType = targetType;
			Method = method;
			Failure = failure;
			Count = 1L;
		}
	}

	private sealed class AuthorityEvidence
	{
		internal readonly Type ScannerType;

		internal readonly MethodInfo TargetMethod;

		internal readonly string Owner;

		internal readonly MethodInfo PatchMethod;

		internal readonly string Category;

		internal readonly int Priority;

		internal AuthorityEvidence(Type scannerType, MethodInfo targetMethod, string owner, MethodInfo patchMethod, string category, int priority)
		{
			ScannerType = scannerType;
			TargetMethod = targetMethod;
			Owner = owner;
			PatchMethod = patchMethod;
			Category = category;
			Priority = priority;
		}
	}

	private struct CandidateFact
	{
		internal readonly int Flags;

		internal CandidateFact(int flags)
		{
			Flags = flags;
		}
	}

	private sealed class ClassificationSlot
	{
		internal ClassificationPlan Plan;

		internal int Scheduled;

		internal int Ready;

		internal int Quarantined;

		internal int FirstRootX;

		internal int FirstRootZ;
	}

	private sealed class SnapshotPlans
	{
		internal readonly ConcurrentDictionary<long, ClassificationSlot> Slots = new ConcurrentDictionary<long, ClassificationSlot>();
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

		internal static ScannerAccessor Build(Type root)
		{
			List<FieldInfo> list = new List<FieldInfo>();
			HashSet<Type> visited = new HashSet<Type>();
			if (!Find(root, 0, list, visited))
			{
				return null;
			}
			return new ScannerAccessor(list.ToArray());
		}

		private static bool Find(Type type, int depth, List<FieldInfo> path, HashSet<Type> visited)
		{
			if (type == null || depth > 3 || visited.Contains(type))
			{
				return false;
			}
			visited.Add(type);
			FieldInfo[] fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
			for (int i = 0; i < fields.Length; i++)
			{
				if (typeof(WorkGiver_Scanner).IsAssignableFrom(fields[i].FieldType))
				{
					path.Add(fields[i]);
					return true;
				}
			}
			for (int j = 0; j < fields.Length; j++)
			{
				Type fieldType = fields[j].FieldType;
				if (!fieldType.IsPrimitive && !fieldType.IsEnum && !(fieldType == typeof(string)) && !fieldType.IsPointer)
				{
					path.Add(fields[j]);
					if (Find(fieldType, depth + 1, path, visited))
					{
						return true;
					}
					path.RemoveAt(path.Count - 1);
				}
			}
			return false;
		}
	}

	internal const string FeatureId = "parallel.candidateClassification";

	private const string MainHarmonyOwner = "allen.rimmt";

	private const string DiagnosticsHarmonyOwner = "allen.rimmt.diagnostics";

	private const int MaxTrackedUnresolvedTypes = 64;

	private const int MaxReportedUnresolvedTypes = 12;

	private const int MaxAuthorityEvidence = 16;

	private const int MaxScannerMissEvidence = 16;

	private const byte RejectWrongType = 1;

	private const byte RejectMissingFixedComp = 2;

	private const byte ShadowStackFull = 1;

	private const byte ShadowPawnUnavailable = 2;

	private const byte ShadowBuildingUnpowered = 4;

	private const byte ShadowRefuelable = 8;

	private const byte ShadowRefuelFull = 16;

	private const byte ShadowRefuelAutoNow = 32;

	private const int FactPawn = 1;

	private const int FactCorpse = 2;

	private const int FactFire = 4;

	private const int FactBuilding = 8;

	private const int FactHoldingTargetComp = 16;

	private const int FactStackFull = 32;

	private const int FactPawnUnavailable = 64;

	private const int FactBuildingUnpowered = 128;

	private const int FactRefuelable = 256;

	private const int FactRefuelFull = 512;

	private const int FactRefuelAutoNow = 1024;

	private static readonly ConditionalWeakTable<PersistentMapSearchFabric.SourceSnapshot, SnapshotPlans> Plans = new ConditionalWeakTable<PersistentMapSearchFabric.SourceSnapshot, SnapshotPlans>();

	private static readonly Dictionary<Type, ScannerAccessor> ScannerAccessors = new Dictionary<Type, ScannerAccessor>();

	private static readonly Dictionary<Type, bool> AuthorityCache = new Dictionary<Type, bool>();

	private static readonly Dictionary<Type, long> UnresolvedTypes = new Dictionary<Type, long>();

	private static readonly Dictionary<Type, long> AuthorityBypassTypes = new Dictionary<Type, long>();

	private static readonly List<AuthorityEvidence> AuthorityEvidenceRows = new List<AuthorityEvidence>();

	private static readonly List<ScannerMissEvidence> ScannerMissEvidenceRows = new List<ScannerMissEvidence>();

	private static readonly KernelCounters[] KernelYield = new KernelCounters[6]
	{
		null,
		new KernelCounters(),
		new KernelCounters(),
		new KernelCounters(),
		new KernelCounters(),
		new KernelCounters()
	};

	private static long observed;

	private static long kernelResolved;

	private static long kernelUnresolved;

	private static long authorityBypass;

	private static long authorityCompatiblePatches;

	private static long authorityForeignPatches;

	private static long planHits;

	private static long planMisses;

	private static long crossRootPlanHits;

	private static long plansScheduled;

	private static long schedulerRejected;

	private static long plansBuilt;

	private static long workerFailures;

	private static long factsCaptured;

	private static long authoritativeClassified;

	private static long shadowStack;

	private static long shadowPawn;

	private static long shadowBuilding;

	private static long consumedQueries;

	private static long candidatesRejected;

	private static long liveParityChecks;

	private static long liveParityFailures;

	private static long quarantined;

	private static long sourceIndexInvalid;

	private static long sourceIndexDuplicate;

	private static long scannerResolveMisses;

	private static long unresolvedTypeDropped;

	private static long authorityTypeDropped;

	private static long authorityEvidenceDropped;

	private static long scannerMissEvidenceDropped;

	private static long refuelFacts;

	private static long refuelFullFacts;

	private static long refuelAutoNowFacts;

	private static long refuelValidatorObserved;

	private static long refuelValidatorRejected;

	private static long refuelFullValidatorObserved;

	private static long refuelFullValidatorAccepted;

	private static long refuelAutoValidatorObserved;

	private static long refuelAutoValidatorAccepted;

	private static long plansFirstConsumed;

	internal static bool TryGetOrSchedule(PersistentMapSearchFabric.SourceSnapshot snapshot, PersistentMapSearchFabric.DistancePlanEntry[] entries, Predicate<Thing> validator, int rootX, int rootZ, out ClassificationPlan plan)
	{
		//IL_0028: Unknown result type (might be due to invalid IL or missing references)
		//IL_002e: Invalid comparison between Unknown and I4
		plan = null;
		Interlocked.Increment(ref observed);
		if (!FeatureGate.IsEnabled("parallel.candidateClassification") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || snapshot == null || entries == null || entries.Length == 0)
		{
			return false;
		}
		if (!TryResolveKernel(validator, out var kernel))
		{
			Interlocked.Increment(ref kernelUnresolved);
			return false;
		}
		Interlocked.Increment(ref kernelResolved);
		Interlocked.Increment(ref Counters(kernel).Resolved);
		SnapshotPlans value = Plans.GetValue(snapshot, (PersistentMapSearchFabric.SourceSnapshot ignored) => new SnapshotPlans());
		long key = MakeKey(kernel);
		ClassificationSlot slot = value.Slots.GetOrAdd(key, (long _003Cp0_003E) => new ClassificationSlot());
		if (Volatile.Read(ref slot.Quarantined) != 0)
		{
			return false;
		}
		if (Volatile.Read(ref slot.Ready) != 0)
		{
			ClassificationPlan plan2 = slot.Plan;
			if (plan2 != null && plan2.Count == snapshot.Count && plan2.Kernel == kernel)
			{
				plan = plan2;
				Interlocked.Increment(ref planHits);
				Interlocked.Increment(ref Counters(kernel).PlanHits);
				if (slot.FirstRootX != rootX || slot.FirstRootZ != rootZ)
				{
					Interlocked.Increment(ref crossRootPlanHits);
				}
				return true;
			}
		}
		Interlocked.Increment(ref planMisses);
		Interlocked.Increment(ref Counters(kernel).PlanMisses);
		if (Interlocked.CompareExchange(ref slot.Scheduled, 1, 0) != 0)
		{
			return false;
		}
		CandidateFact[] facts;
		try
		{
			facts = CaptureFacts(entries, snapshot.Count, kernel);
		}
		catch
		{
			Volatile.Write(ref slot.Scheduled, 0);
			return false;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler != null && scheduler.TryEnqueue("parallel.candidateClassification", JobPriority.High, delegate
		{
			BuildPlan(slot, kernel, facts);
		}))
		{
			slot.FirstRootX = rootX;
			slot.FirstRootZ = rootZ;
			Interlocked.Increment(ref plansScheduled);
		}
		else
		{
			Volatile.Write(ref slot.Scheduled, 0);
			Interlocked.Increment(ref schedulerRejected);
		}
		return false;
	}

	internal static bool ValidateReject(ClassificationPlan plan, int index, Thing thing)
	{
		if (plan == null || index < 0 || index >= plan.Count || thing == null)
		{
			return false;
		}
		byte b = plan.RejectReasons[index];
		if (b == 0)
		{
			return false;
		}
		Interlocked.Increment(ref liveParityChecks);
		Interlocked.Increment(ref Counters(plan.Kernel).ParityChecks);
		if (!LiveReject(plan.Kernel, b, thing))
		{
			Interlocked.Increment(ref liveParityFailures);
			Interlocked.Increment(ref Counters(plan.Kernel).ParityFailures);
			return false;
		}
		return true;
	}

	internal static bool TryGetRejectReason(ClassificationPlan plan, int sourceIndex, out byte reason)
	{
		reason = 0;
		if (plan == null || sourceIndex < 0 || sourceIndex >= plan.Count)
		{
			Interlocked.Increment(ref sourceIndexInvalid);
			return false;
		}
		reason = plan.RejectReasons[sourceIndex];
		return true;
	}

	internal static void Quarantine(PersistentMapSearchFabric.SourceSnapshot snapshot, ClassificationPlan plan)
	{
		if (snapshot != null && plan != null && Plans.TryGetValue(snapshot, out var value) && value != null && value.Slots.TryGetValue(MakeKey(plan.Kernel), out var value2) && value2 != null)
		{
			Volatile.Write(ref value2.Quarantined, 1);
			Interlocked.Increment(ref quarantined);
			Interlocked.Increment(ref Counters(plan.Kernel).Quarantined);
		}
	}

	internal static void NoteConsumed(ClassificationPlan plan, int rejected, int within)
	{
		if (plan != null)
		{
			if (Interlocked.CompareExchange(ref plan.ConsumptionNoted, 1, 0) == 0)
			{
				Interlocked.Increment(ref plansFirstConsumed);
			}
			Interlocked.Increment(ref consumedQueries);
			Interlocked.Increment(ref Counters(plan.Kernel).ConsumedQueries);
			if (rejected > 0)
			{
				Interlocked.Add(ref candidatesRejected, rejected);
				Interlocked.Add(ref Counters(plan.Kernel).CandidatesRejected, rejected);
			}
		}
	}

	internal static void NoteValidatorResult(ClassificationPlan plan, int sourceIndex, bool accepted)
	{
		if (plan == null || plan.Kernel != KernelKind.Refuel || sourceIndex < 0 || sourceIndex >= plan.Count)
		{
			return;
		}
		byte num = plan.ShadowReasons[sourceIndex];
		Interlocked.Increment(ref refuelValidatorObserved);
		if (!accepted)
		{
			Interlocked.Increment(ref refuelValidatorRejected);
		}
		if ((num & 0x10) != 0)
		{
			Interlocked.Increment(ref refuelFullValidatorObserved);
			if (accepted)
			{
				Interlocked.Increment(ref refuelFullValidatorAccepted);
			}
		}
		if ((num & 0x20) != 0)
		{
			Interlocked.Increment(ref refuelAutoValidatorObserved);
			if (accepted)
			{
				Interlocked.Increment(ref refuelAutoValidatorAccepted);
			}
		}
	}

	private static CandidateFact[] CaptureFacts(PersistentMapSearchFabric.DistancePlanEntry[] entries, int sourceCount, KernelKind kernel)
	{
		if (sourceCount <= 0 || entries.Length != sourceCount)
		{
			Interlocked.Increment(ref sourceIndexInvalid);
			throw new InvalidOperationException("T34-C.2 source count mismatch");
		}
		CandidateFact[] array = new CandidateFact[sourceCount];
		bool[] array2 = new bool[sourceCount];
		for (int i = 0; i < entries.Length; i++)
		{
			int sourceIndex = entries[i].SourceIndex;
			if (sourceIndex < 0 || sourceIndex >= sourceCount)
			{
				Interlocked.Increment(ref sourceIndexInvalid);
				throw new InvalidOperationException("T34-C.2 source index outside snapshot");
			}
			if (array2[sourceIndex])
			{
				Interlocked.Increment(ref sourceIndexDuplicate);
				throw new InvalidOperationException("T34-C.2 duplicate source index");
			}
			array2[sourceIndex] = true;
			Thing thing = entries[i].Thing;
			int num = 0;
			if (thing is Pawn)
			{
				num |= 1;
			}
			if (thing is Corpse)
			{
				num |= 2;
			}
			if (thing is Fire)
			{
				num |= 4;
			}
			if (thing is Building)
			{
				num |= 8;
			}
			if (thing != null)
			{
				if (ThingCompUtility.TryGetComp<CompHoldingPlatformTarget>(thing) != null)
				{
					num |= 0x10;
				}
				if (thing.def != null && thing.def.stackLimit > 1 && thing.stackCount >= thing.def.stackLimit)
				{
					num |= 0x20;
				}
				Pawn val = (Pawn)(object)((thing is Pawn) ? thing : null);
				if (val != null && (val.Dead || val.Downed || !RestUtility.Awake(val)))
				{
					num |= 0x40;
				}
				CompPowerTrader val2 = ThingCompUtility.TryGetComp<CompPowerTrader>(thing);
				if (thing is Building && val2 != null && !val2.PowerOn)
				{
					num |= 0x80;
				}
				if (kernel == KernelKind.Refuel)
				{
					CompRefuelable val3 = ThingCompUtility.TryGetComp<CompRefuelable>(thing);
					if (val3 != null)
					{
						num |= 0x100;
						if (val3.IsFull)
						{
							num |= 0x200;
						}
						if (val3.ShouldAutoRefuelNow)
						{
							num |= 0x400;
						}
					}
				}
			}
			array[sourceIndex] = new CandidateFact(num);
		}
		Interlocked.Add(ref factsCaptured, array.Length);
		return array;
	}

	private static void BuildPlan(ClassificationSlot slot, KernelKind kernel, CandidateFact[] facts)
	{
		try
		{
			byte[] array = new byte[facts.Length];
			byte[] array2 = new byte[facts.Length];
			long num = 0L;
			long num2 = 0L;
			long num3 = 0L;
			long num4 = 0L;
			long num5 = 0L;
			long num6 = 0L;
			long num7 = 0L;
			for (int i = 0; i < facts.Length; i++)
			{
				int flags = facts[i].Flags;
				if ((array[i] = EvaluateAuthoritative(kernel, flags)) != 0)
				{
					num++;
				}
				byte b = 0;
				if ((flags & 0x20) != 0)
				{
					b |= 1;
					num2++;
				}
				if ((flags & 0x40) != 0)
				{
					b |= 2;
					num3++;
				}
				if ((flags & 0x80) != 0)
				{
					b |= 4;
					num4++;
				}
				if ((flags & 0x100) != 0)
				{
					b |= 8;
					num5++;
				}
				if ((flags & 0x200) != 0)
				{
					b |= 0x10;
					num6++;
				}
				if ((flags & 0x400) != 0)
				{
					b |= 0x20;
					num7++;
				}
				array2[i] = b;
			}
			slot.Plan = new ClassificationPlan(kernel, array, array2);
			Interlocked.Add(ref authoritativeClassified, num);
			Interlocked.Add(ref shadowStack, num2);
			Interlocked.Add(ref shadowPawn, num3);
			Interlocked.Add(ref shadowBuilding, num4);
			Interlocked.Add(ref refuelFacts, num5);
			Interlocked.Add(ref refuelFullFacts, num6);
			Interlocked.Add(ref refuelAutoNowFacts, num7);
			Interlocked.Increment(ref plansBuilt);
			Volatile.Write(ref slot.Ready, 1);
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref workerFailures);
			CircuitBreaker.RecordFailure("parallel.candidateClassification", exception);
		}
	}

	private static byte EvaluateAuthoritative(KernelKind kernel, int flags)
	{
		switch (kernel)
		{
		case KernelKind.Corpse:
			return ((flags & 2) == 0) ? ((byte)1) : ((byte)0);
		case KernelKind.Pawn:
			return ((flags & 1) == 0) ? ((byte)1) : ((byte)0);
		case KernelKind.Fire:
			return ((flags & 4) == 0) ? ((byte)1) : ((byte)0);
		case KernelKind.HoldingTarget:
			if ((flags & 0x10) != 0)
			{
				return 0;
			}
			return 2;
		default:
			return 0;
		}
	}

	private static bool LiveReject(KernelKind kernel, byte reason, Thing thing)
	{
		if ((reason & 1) != 0)
		{
			return kernel switch
			{
				KernelKind.Corpse => !(thing is Corpse), 
				KernelKind.Pawn => !(thing is Pawn), 
				KernelKind.Fire => !(thing is Fire), 
				_ => false, 
			};
		}
		if ((reason & 2) != 0 && kernel == KernelKind.HoldingTarget)
		{
			return ThingCompUtility.TryGetComp<CompHoldingPlatformTarget>(thing) == null;
		}
		return false;
	}

	private static bool TryResolveKernel(Predicate<Thing> validator, out KernelKind kernel)
	{
		kernel = KernelKind.None;
		ScannerResolveFailure failure;
		WorkGiver_Scanner val = ResolveScanner(validator, out failure);
		if (val == null)
		{
			Interlocked.Increment(ref scannerResolveMisses);
			RecordScannerMiss(validator, failure);
			return false;
		}
		Type type = ((object)val).GetType();
		if (type == typeof(WorkGiver_HaulCorpses))
		{
			kernel = KernelKind.Corpse;
		}
		else if (type == typeof(WorkGiver_TakeEntityToHoldingPlatform))
		{
			kernel = KernelKind.HoldingTarget;
		}
		else if (type == typeof(Workgiver_AdministerHemogen) || type == typeof(WorkGiver_VisitSickPawn))
		{
			kernel = KernelKind.Pawn;
		}
		else if (type.FullName == "RimWorld.WorkGiver_FightFires")
		{
			kernel = KernelKind.Fire;
		}
		else if (type == typeof(WorkGiver_Refuel) || type == typeof(WorkGiver_Refuel_Turret))
		{
			kernel = KernelKind.Refuel;
		}
		if (kernel == KernelKind.None)
		{
			RecordBoundedType(UnresolvedTypes, type, 64, ref unresolvedTypeDropped);
			return false;
		}
		Interlocked.Increment(ref Counters(kernel).Observed);
		bool flag = AuthoritySafe(type);
		if (!flag && kernel != KernelKind.Refuel)
		{
			Interlocked.Increment(ref authorityBypass);
			Interlocked.Increment(ref Counters(kernel).AuthorityBypass);
			RecordBoundedType(AuthorityBypassTypes, type, 16, ref authorityTypeDropped);
			return false;
		}
		if (!flag)
		{
			Interlocked.Increment(ref Counters(kernel).ShadowAuthorityUnsafe);
		}
		return true;
	}

	private static WorkGiver_Scanner ResolveScanner(Predicate<Thing> validator, out ScannerResolveFailure failure)
	{
		failure = ScannerResolveFailure.None;
		if (validator == null)
		{
			failure = ScannerResolveFailure.NoValidator;
			return null;
		}
		if (validator.Target == null)
		{
			failure = ScannerResolveFailure.NoTarget;
			return null;
		}
		try
		{
			Type type = validator.Target.GetType();
			if (!ScannerAccessors.TryGetValue(type, out var value))
			{
				value = ScannerAccessor.Build(type);
				ScannerAccessors[type] = value;
			}
			if (value == null)
			{
				failure = ScannerResolveFailure.NoAccessorPath;
				return null;
			}
			WorkGiver_Scanner obj = value.Read(validator.Target);
			if (obj == null)
			{
				failure = ScannerResolveFailure.NullPathValue;
			}
			return obj;
		}
		catch
		{
			failure = ScannerResolveFailure.Exception;
			return null;
		}
	}

	private static void RecordScannerMiss(Predicate<Thing> validator, ScannerResolveFailure failure)
	{
		Type type = ((validator == null || validator.Target == null) ? null : validator.Target.GetType());
		MethodInfo methodInfo = validator?.Method;
		for (int i = 0; i < ScannerMissEvidenceRows.Count; i++)
		{
			ScannerMissEvidence scannerMissEvidence = ScannerMissEvidenceRows[i];
			if (scannerMissEvidence.TargetType == type && scannerMissEvidence.Method == methodInfo && scannerMissEvidence.Failure == failure)
			{
				scannerMissEvidence.Count++;
				return;
			}
		}
		if (ScannerMissEvidenceRows.Count < 16)
		{
			ScannerMissEvidenceRows.Add(new ScannerMissEvidence(type, methodInfo, failure));
		}
		else
		{
			Interlocked.Increment(ref scannerMissEvidenceDropped);
		}
	}

	private static bool AuthoritySafe(Type scannerType)
	{
		if (AuthorityCache.TryGetValue(scannerType, out var value))
		{
			return value;
		}
		value = true;
		try
		{
			Type[] types = new Type[3]
			{
				typeof(Pawn),
				typeof(Thing),
				typeof(bool)
			};
			string[] array = new string[2] { "HasJobOnThing", "JobOnThing" };
			for (int i = 0; i < array.Length; i++)
			{
				Type type = scannerType;
				while (type != null && typeof(WorkGiver).IsAssignableFrom(type))
				{
					MethodInfo method = type.GetMethod(array[i], BindingFlags.DeclaredOnly | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, types, null);
					if (method != null)
					{
						Patches patchInfo = Harmony.GetPatchInfo((MethodBase)method);
						if (patchInfo != null && (0u | (HasIncompatiblePatch(scannerType, method, patchInfo.Prefixes, allowDiagnosticsMeasurement: true, "prefix") ? 1u : 0u) | (HasIncompatiblePatch(scannerType, method, patchInfo.Postfixes, allowDiagnosticsMeasurement: true, "postfix") ? 1u : 0u) | (HasIncompatiblePatch(scannerType, method, patchInfo.Transpilers, allowDiagnosticsMeasurement: false, "transpiler") ? 1u : 0u) | (HasIncompatiblePatch(scannerType, method, patchInfo.Finalizers, allowDiagnosticsMeasurement: false, "finalizer") ? 1u : 0u)) != 0)
						{
							value = false;
						}
					}
					type = type.BaseType;
				}
			}
		}
		catch
		{
			value = false;
		}
		AuthorityCache[scannerType] = value;
		return value;
	}

	private static bool HasIncompatiblePatch(Type scannerType, MethodInfo targetMethod, IEnumerable<Patch> patches, bool allowDiagnosticsMeasurement, string category)
	{
		if (patches == null)
		{
			return false;
		}
		bool result = false;
		foreach (Patch patch in patches)
		{
			if (patch == null)
			{
				continue;
			}
			if (string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				Interlocked.Increment(ref authorityCompatiblePatches);
				continue;
			}
			if (allowDiagnosticsMeasurement && string.Equals(patch.owner, "allen.rimmt.diagnostics", StringComparison.Ordinal) && IsDiagnosticsMeasurementPatch(patch.PatchMethod))
			{
				Interlocked.Increment(ref authorityCompatiblePatches);
				continue;
			}
			Interlocked.Increment(ref authorityForeignPatches);
			result = true;
			if (AuthorityEvidenceRows.Count < 16)
			{
				AuthorityEvidenceRows.Add(new AuthorityEvidence(scannerType, targetMethod, patch.owner, patch.PatchMethod, category, patch.priority));
			}
			else
			{
				Interlocked.Increment(ref authorityEvidenceDropped);
			}
		}
		return result;
	}

	private static bool IsDiagnosticsMeasurementPatch(MethodInfo method)
	{
		Type type = ((method == null) ? null : method.DeclaringType);
		string text = ((type == null) ? null : type.Namespace);
		if (!string.IsNullOrEmpty(text))
		{
			if (!string.Equals(text, "RimMT.Diagnostics", StringComparison.Ordinal))
			{
				return text.StartsWith("RimMT.Diagnostics.", StringComparison.Ordinal);
			}
			return true;
		}
		return false;
	}

	private static long MakeKey(KernelKind kernel)
	{
		return (int)kernel;
	}

	private static KernelCounters Counters(KernelKind kernel)
	{
		if ((int)kernel <= 0 || (int)kernel >= KernelYield.Length)
		{
			return KernelYield[1];
		}
		return KernelYield[(uint)kernel];
	}

	private static void RecordBoundedType(Dictionary<Type, long> rows, Type type, int capacity, ref long dropped)
	{
		if (!(type == null))
		{
			if (rows.TryGetValue(type, out var value))
			{
				rows[type] = value + 1;
			}
			else if (rows.Count < capacity)
			{
				rows.Add(type, 1L);
			}
			else
			{
				Interlocked.Increment(ref dropped);
			}
		}
	}

	private static string KernelYieldSummary()
	{
		StringBuilder stringBuilder = new StringBuilder();
		KernelKind[] array = new KernelKind[5]
		{
			KernelKind.Corpse,
			KernelKind.Pawn,
			KernelKind.Fire,
			KernelKind.HoldingTarget,
			KernelKind.Refuel
		};
		for (int i = 0; i < array.Length; i++)
		{
			if (i != 0)
			{
				stringBuilder.Append(';');
			}
			KernelCounters kernelCounters = Counters(array[i]);
			stringBuilder.Append(array[i]).Append('=').Append(Interlocked.Read(ref kernelCounters.Observed))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.Resolved))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.AuthorityBypass))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.PlanHits))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.PlanMisses))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.ConsumedQueries))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.CandidatesRejected))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.ParityChecks))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.ParityFailures))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.Quarantined))
				.Append('/')
				.Append(Interlocked.Read(ref kernelCounters.ShadowAuthorityUnsafe));
		}
		return stringBuilder.ToString();
	}

	private static string TopTypeSummary(Dictionary<Type, long> rows, int limit)
	{
		List<KeyValuePair<Type, long>> list = new List<KeyValuePair<Type, long>>(rows);
		list.Sort(delegate(KeyValuePair<Type, long> left, KeyValuePair<Type, long> right)
		{
			int num3 = right.Value.CompareTo(left.Value);
			return (num3 != 0) ? num3 : string.Compare(left.Key.FullName, right.Key.FullName, StringComparison.Ordinal);
		});
		StringBuilder stringBuilder = new StringBuilder();
		int num = Math.Min(limit, list.Count);
		for (int num2 = 0; num2 < num; num2++)
		{
			if (num2 != 0)
			{
				stringBuilder.Append(';');
			}
			stringBuilder.Append(list[num2].Key.FullName).Append('=').Append(list[num2].Value);
		}
		if (stringBuilder.Length != 0)
		{
			return stringBuilder.ToString();
		}
		return "none";
	}

	private static string AuthorityEvidenceSummary()
	{
		StringBuilder stringBuilder = new StringBuilder();
		for (int i = 0; i < AuthorityEvidenceRows.Count; i++)
		{
			AuthorityEvidence authorityEvidence = AuthorityEvidenceRows[i];
			if (i != 0)
			{
				stringBuilder.Append(';');
			}
			stringBuilder.Append((authorityEvidence.ScannerType == null) ? "?" : authorityEvidence.ScannerType.FullName).Append('.').Append((authorityEvidence.TargetMethod == null) ? "?" : authorityEvidence.TargetMethod.Name)
				.Append(':')
				.Append(authorityEvidence.Category)
				.Append("[owner=")
				.Append(authorityEvidence.Owner ?? "?")
				.Append(",patch=")
				.Append((authorityEvidence.PatchMethod == null || authorityEvidence.PatchMethod.DeclaringType == null) ? "?" : authorityEvidence.PatchMethod.DeclaringType.FullName)
				.Append('.')
				.Append((authorityEvidence.PatchMethod == null) ? "?" : authorityEvidence.PatchMethod.Name)
				.Append(",priority=")
				.Append(authorityEvidence.Priority)
				.Append(']');
		}
		if (stringBuilder.Length != 0)
		{
			return stringBuilder.ToString();
		}
		return "none";
	}

	private static string ScannerMissEvidenceSummary()
	{
		List<ScannerMissEvidence> list = new List<ScannerMissEvidence>(ScannerMissEvidenceRows);
		list.Sort((ScannerMissEvidence left, ScannerMissEvidence right) => right.Count.CompareTo(left.Count));
		StringBuilder stringBuilder = new StringBuilder();
		for (int num = 0; num < list.Count; num++)
		{
			ScannerMissEvidence scannerMissEvidence = list[num];
			if (num != 0)
			{
				stringBuilder.Append(';');
			}
			stringBuilder.Append((scannerMissEvidence.TargetType == null) ? "static-or-null" : scannerMissEvidence.TargetType.FullName).Append('.').Append((scannerMissEvidence.Method == null) ? "?" : scannerMissEvidence.Method.Name)
				.Append('[')
				.Append(scannerMissEvidence.Failure)
				.Append("]=")
				.Append(scannerMissEvidence.Count);
		}
		if (stringBuilder.Length != 0)
		{
			return stringBuilder.ToString();
		}
		return "none";
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref plansBuilt);
		long num2 = Interlocked.Read(ref plansFirstConsumed);
		return "T34-C.4 Refuel Eligibility Shadow Census: feature=" + FeatureGate.IsEnabled("parallel.candidateClassification") + ", observed=" + Interlocked.Read(ref observed) + ", kernels[resolved/unresolved/authorityBypass]=" + Interlocked.Read(ref kernelResolved) + "/" + Interlocked.Read(ref kernelUnresolved) + "/" + Interlocked.Read(ref authorityBypass) + ", authorityPatches[compatible/foreign]=" + Interlocked.Read(ref authorityCompatiblePatches) + "/" + Interlocked.Read(ref authorityForeignPatches) + ", sourcePlans[hit/miss/crossRoot/scheduled/rejected/built/fail]=" + Interlocked.Read(ref planHits) + "/" + Interlocked.Read(ref planMisses) + "/" + Interlocked.Read(ref crossRootPlanHits) + "/" + Interlocked.Read(ref plansScheduled) + "/" + Interlocked.Read(ref schedulerRejected) + "/" + Interlocked.Read(ref plansBuilt) + "/" + Interlocked.Read(ref workerFailures) + ", entryIndex[invalid/duplicate]=" + Interlocked.Read(ref sourceIndexInvalid) + "/" + Interlocked.Read(ref sourceIndexDuplicate) + ", facts=" + Interlocked.Read(ref factsCaptured) + ", classified[authoritative/shadowStack/shadowPawn/shadowBuilding]=" + Interlocked.Read(ref authoritativeClassified) + "/" + Interlocked.Read(ref shadowStack) + "/" + Interlocked.Read(ref shadowPawn) + "/" + Interlocked.Read(ref shadowBuilding) + ", consumed[queries/rejected]=" + Interlocked.Read(ref consumedQueries) + "/" + Interlocked.Read(ref candidatesRejected) + ", parity[checks/fail/quarantine]=" + Interlocked.Read(ref liveParityChecks) + "/" + Interlocked.Read(ref liveParityFailures) + "/" + Interlocked.Read(ref quarantined) + ", planUse[firstConsumed/built/unconsumedUpper]=" + num2 + "/" + num + "/" + Math.Max(0L, num - num2) + ", kernelYield[observed/resolved/authorityBypass/hit/miss/consumed/rejected/parityChecks/parityFail/quarantine/shadowAuthorityUnsafe]=" + KernelYieldSummary() + ", unresolved[scannerMiss/dropped/top]=" + Interlocked.Read(ref scannerResolveMisses) + "/" + Interlocked.Read(ref unresolvedTypeDropped) + "/" + TopTypeSummary(UnresolvedTypes, 12) + ", authorityBypassTypes[dropped/top]=" + Interlocked.Read(ref authorityTypeDropped) + "/" + TopTypeSummary(AuthorityBypassTypes, 16) + ", authorityEvidence[dropped/rows]=" + Interlocked.Read(ref authorityEvidenceDropped) + "/" + AuthorityEvidenceSummary() + ", scannerMissEvidence[dropped/rows]=" + Interlocked.Read(ref scannerMissEvidenceDropped) + "/" + ScannerMissEvidenceSummary() + ", refuelShadow[facts/full/autoNow/validatorObserved/validatorRejected/fullObserved/fullAccepted/autoObserved/autoAccepted]=" + Interlocked.Read(ref refuelFacts) + "/" + Interlocked.Read(ref refuelFullFacts) + "/" + Interlocked.Read(ref refuelAutoNowFacts) + "/" + Interlocked.Read(ref refuelValidatorObserved) + "/" + Interlocked.Read(ref refuelValidatorRejected) + "/" + Interlocked.Read(ref refuelFullValidatorObserved) + "/" + Interlocked.Read(ref refuelFullValidatorAccepted) + "/" + Interlocked.Read(ref refuelAutoValidatorObserved) + "/" + Interlocked.Read(ref refuelAutoValidatorAccepted) + ". Refuel/Refuel_Turret facts are measurement-only and never reject candidates; Root-independent source-index plans; no-wait; worker input is primitive-only; mutable stack/pawn/building facts are shadow-only; live validator and Reachability remain authoritative.";
	}
}
