using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class WorkGiverParallelSafety093T27_2
{
	internal enum SafetyTier
	{
		MainThreadOnly,
		SnapshotComputeCandidate,
		DirectIlCleanCandidate
	}

	[Flags]
	private enum RiskFlags
	{
		None = 0,
		UnityApi = 1,
		ReservationApi = 2,
		ReachabilityApi = 4,
		RandApi = 8,
		JobFailReasonApi = 0x10,
		JobCreationApi = 0x20,
		MutableWorldApi = 0x40,
		GlobalSideEffect = 0x80,
		IlScanIncomplete = 0x100
	}

	private sealed class AuditRecord
	{
		internal readonly Type Type;

		internal readonly SafetyTier Tier;

		internal readonly RiskFlags Risks;

		internal readonly int RelevantMethods;

		internal readonly int ForeignPatches;

		internal readonly bool ModType;

		internal readonly bool Scanner;

		internal AuditRecord(Type type, SafetyTier tier, RiskFlags risks, int relevantMethods, int foreignPatches, bool modType, bool scanner)
		{
			Type = type;
			Tier = tier;
			Risks = risks;
			RelevantMethods = relevantMethods;
			ForeignPatches = foreignPatches;
			ModType = modType;
			Scanner = scanner;
		}
	}

	internal const string FeatureId = "parallel.workSafety";

	private static readonly object Sync;

	private static readonly Dictionary<Type, AuditRecord> Records;

	private static readonly List<string> CleanExamples;

	private static readonly List<string> SnapshotExamples;

	private static readonly OpCode[] OneByteOpCodes;

	private static readonly OpCode[] TwoByteOpCodes;

	private static volatile bool initialized;

	private static long defsSeen;

	private static long uniqueTypes;

	private static long scannerTypes;

	private static long modTypes;

	private static long foreignPatchedTypes;

	private static long snapshotCandidates;

	private static long directIlCleanCandidates;

	private static long directIlScanFailures;

	private static long fullParallelAdmitted;

	private static long fullParallelDenied;

	private static long riskUnity;

	private static long riskReservation;

	private static long riskReachability;

	private static long riskRand;

	private static long riskJobFailReason;

	private static long riskJobCreation;

	private static long riskMutableWorld;

	private static long riskGlobalSideEffect;

	private const bool FoundationReservationPromise = false;

	private const bool FoundationReachabilityIsolation = false;

	private const bool FoundationJobToilPools = false;

	private const bool FoundationThreadLocalRand = false;

	private const bool FoundationThreadLocalJobFailReason = false;

	private const bool FoundationModCallbackContainment = false;

	static WorkGiverParallelSafety093T27_2()
	{
		Sync = new object();
		Records = new Dictionary<Type, AuditRecord>();
		CleanExamples = new List<string>();
		SnapshotExamples = new List<string>();
		OneByteOpCodes = new OpCode[256];
		TwoByteOpCodes = new OpCode[256];
		BuildOpcodeTables();
	}

	internal static void Initialize()
	{
		if (initialized)
		{
			return;
		}
		lock (Sync)
		{
			if (initialized)
			{
				return;
			}
			try
			{
				List<WorkGiverDef> allDefsListForReading = DefDatabase<WorkGiverDef>.AllDefsListForReading;
				if (allDefsListForReading != null)
				{
					for (int i = 0; i < allDefsListForReading.Count; i++)
					{
						WorkGiverDef val = allDefsListForReading[i];
						if (val != null && !(val.giverClass == null))
						{
							Interlocked.Increment(ref defsSeen);
							if (!Records.ContainsKey(val.giverClass))
							{
								AuditRecord value = AuditType(val.giverClass);
								Records.Add(val.giverClass, value);
								Interlocked.Increment(ref uniqueTypes);
							}
						}
					}
				}
				initialized = true;
				Log.Message("[RimMT] T27.2 WorkGiver parallel safety registry initialized. Audit only; FullParallel admission hard-OFF.");
			}
			catch (Exception ex)
			{
				initialized = false;
				FeatureGate.Suppress("parallel.workSafety", "T27.2 safety registry init failure: " + ex.GetType().Name);
				Log.Warning("[RimMT] T27.2 WorkGiver safety registry failed closed: " + ex.GetType().Name + ": " + ex.Message);
			}
		}
	}

	internal static bool CanAdmitFullParallel(Type giverType, out string reason)
	{
		Interlocked.Increment(ref fullParallelDenied);
		reason = "FullParallel hard-OFF: reservation/reachability/job-pool/Rand/JobFailReason/mod-callback foundations are incomplete.";
		return false;
	}

	internal static SafetyTier GetTier(Type giverType)
	{
		if (giverType == null)
		{
			return SafetyTier.MainThreadOnly;
		}
		lock (Sync)
		{
			if (Records.TryGetValue(giverType, out var value) && value != null)
			{
				return value.Tier;
			}
		}
		return SafetyTier.MainThreadOnly;
	}

	private static AuditRecord AuditType(Type type)
	{
		bool flag = typeof(WorkGiver_Scanner).IsAssignableFrom(type);
		bool flag2 = type.Assembly != typeof(WorkGiver).Assembly;
		if (flag)
		{
			Interlocked.Increment(ref scannerTypes);
		}
		if (flag2)
		{
			Interlocked.Increment(ref modTypes);
		}
		RiskFlags risks = RiskFlags.None;
		int num = 0;
		int num2 = 0;
		HashSet<MethodBase> hashSet = new HashSet<MethodBase>();
		string[] names = new string[16]
		{
			"NonScanJob", "ShouldSkip", "MissingRequiredCapacity", "PotentialWorkThingsGlobal", "PotentialWorkCellsGlobal", "HasJobOnThing", "HasJobOnCell", "JobOnThing", "JobOnCell", "GetPriority",
			"MaxPathDanger", "get_Prioritized", "get_AllowUnreachable", "get_PotentialWorkThingRequest", "get_PathEndMode", "get_MaxRegionsToScanBeforeGlobalSearch"
		};
		MethodInfo[] array;
		try
		{
			array = type.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
		}
		catch
		{
			array = new MethodInfo[0];
		}
		foreach (MethodInfo methodInfo in array)
		{
			if (!(methodInfo == null) && NameIn(methodInfo.Name, names) && hashSet.Add(methodInfo))
			{
				num++;
				if (HasForeignHarmonyPatch(methodInfo))
				{
					num2++;
				}
				try
				{
					ScanDirectDependencies(methodInfo, ref risks);
				}
				catch
				{
					risks |= RiskFlags.IlScanIncomplete;
					Interlocked.Increment(ref directIlScanFailures);
				}
			}
		}
		if (num2 > 0)
		{
			Interlocked.Increment(ref foreignPatchedTypes);
		}
		AccumulateRiskCounters(risks);
		SafetyTier tier;
		if (!flag || flag2 || num2 > 0)
		{
			tier = SafetyTier.MainThreadOnly;
		}
		else
		{
			tier = SafetyTier.SnapshotComputeCandidate;
			Interlocked.Increment(ref snapshotCandidates);
			AddExample(SnapshotExamples, type.FullName);
			RiskFlags riskFlags = RiskFlags.UnityApi | RiskFlags.ReservationApi | RiskFlags.ReachabilityApi | RiskFlags.RandApi | RiskFlags.JobFailReasonApi | RiskFlags.JobCreationApi | RiskFlags.MutableWorldApi | RiskFlags.GlobalSideEffect | RiskFlags.IlScanIncomplete;
			if ((risks & riskFlags) == 0)
			{
				tier = SafetyTier.DirectIlCleanCandidate;
				Interlocked.Increment(ref directIlCleanCandidates);
				AddExample(CleanExamples, type.FullName);
			}
		}
		return new AuditRecord(type, tier, risks, num, num2, flag2, flag);
	}

	private static void ScanDirectDependencies(MethodInfo method, ref RiskFlags risks)
	{
		MethodBody methodBody = method.GetMethodBody();
		if (methodBody == null)
		{
			return;
		}
		byte[] iLAsByteArray = methodBody.GetILAsByteArray();
		if (iLAsByteArray == null || iLAsByteArray.Length == 0)
		{
			return;
		}
		int num;
		for (int i = 0; i < iLAsByteArray.Length; i += num)
		{
			byte b = iLAsByteArray[i++];
			OpCode opCode;
			if (b == 254)
			{
				if (i >= iLAsByteArray.Length)
				{
					break;
				}
				opCode = TwoByteOpCodes[iLAsByteArray[i++]];
			}
			else
			{
				opCode = OneByteOpCodes[b];
			}
			int startIndex = i;
			num = OperandSize(opCode.OperandType, iLAsByteArray, i);
			if (num < 0 || i + num > iLAsByteArray.Length)
			{
				risks |= RiskFlags.IlScanIncomplete;
				break;
			}
			if ((opCode.OperandType != OperandType.InlineMethod && opCode.OperandType != OperandType.InlineField && opCode.OperandType != OperandType.InlineTok && opCode.OperandType != OperandType.InlineType) || num < 4)
			{
				continue;
			}
			int metadataToken = BitConverter.ToInt32(iLAsByteArray, startIndex);
			try
			{
				Type[] genericTypeArguments = ((method.DeclaringType != null && method.DeclaringType.IsGenericType) ? method.DeclaringType.GetGenericArguments() : Type.EmptyTypes);
				Type[] genericMethodArguments = (method.IsGenericMethod ? method.GetGenericArguments() : Type.EmptyTypes);
				MemberInfo memberInfo = method.Module.ResolveMember(metadataToken, genericTypeArguments, genericMethodArguments);
				if (memberInfo != null)
				{
					ClassifyMember(memberInfo, ref risks);
				}
			}
			catch
			{
				risks |= RiskFlags.IlScanIncomplete;
			}
		}
	}

	private static void ClassifyMember(MemberInfo member, ref RiskFlags risks)
	{
		Type type = member as Type;
		if (type == null)
		{
			type = member.DeclaringType;
		}
		if (type == null)
		{
			return;
		}
		string obj = type.Namespace ?? string.Empty;
		string text = type.Name ?? string.Empty;
		string text2 = type.FullName ?? text;
		if (obj.StartsWith("UnityEngine", StringComparison.Ordinal))
		{
			risks |= RiskFlags.UnityApi;
		}
		if (text == "ReservationManager" || text2.IndexOf("ReservationManager", StringComparison.Ordinal) >= 0)
		{
			risks |= RiskFlags.ReservationApi;
		}
		switch (text)
		{
		default:
			if (text2.IndexOf("Reachability", StringComparison.Ordinal) < 0)
			{
				break;
			}
			goto case "Reachability";
		case "Reachability":
		case "RegionTraverser":
		case "Region":
			risks |= RiskFlags.ReachabilityApi;
			break;
		}
		if (text == "Rand" || text2 == "Verse.Rand")
		{
			risks |= RiskFlags.RandApi;
		}
		if (text == "JobFailReason" || text2.IndexOf("JobFailReason", StringComparison.Ordinal) >= 0)
		{
			risks |= RiskFlags.JobFailReasonApi;
		}
		if (text == "JobMaker" || text == "Job" || text.StartsWith("Toil", StringComparison.Ordinal) || text2.IndexOf("SimplePool", StringComparison.Ordinal) >= 0)
		{
			risks |= RiskFlags.JobCreationApi;
		}
		switch (text)
		{
		case "Pawn":
		case "Thing":
		case "Map":
		case "ListerThings":
		case "ThingGrid":
		case "Pawn_JobTracker":
		case "Pawn_PathFollower":
		case "DesignationManager":
		case "ZoneManager":
		case "ThingOwner":
			risks |= RiskFlags.MutableWorldApi;
			break;
		}
		switch (text)
		{
		case "Messages":
		case "Log":
		case "SoundStarter":
		case "MoteMaker":
			risks |= RiskFlags.GlobalSideEffect;
			break;
		}
	}

	private static bool HasForeignHarmonyPatch(MethodBase method)
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

	private static bool NameIn(string name, string[] names)
	{
		for (int i = 0; i < names.Length; i++)
		{
			if (string.Equals(name, names[i], StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	private static void AccumulateRiskCounters(RiskFlags risks)
	{
		if ((risks & RiskFlags.UnityApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskUnity);
		}
		if ((risks & RiskFlags.ReservationApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskReservation);
		}
		if ((risks & RiskFlags.ReachabilityApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskReachability);
		}
		if ((risks & RiskFlags.RandApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskRand);
		}
		if ((risks & RiskFlags.JobFailReasonApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskJobFailReason);
		}
		if ((risks & RiskFlags.JobCreationApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskJobCreation);
		}
		if ((risks & RiskFlags.MutableWorldApi) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskMutableWorld);
		}
		if ((risks & RiskFlags.GlobalSideEffect) != RiskFlags.None)
		{
			Interlocked.Increment(ref riskGlobalSideEffect);
		}
	}

	private static void AddExample(List<string> list, string value)
	{
		if (!string.IsNullOrEmpty(value) && list.Count < 8)
		{
			list.Add(value);
		}
	}

	private static void BuildOpcodeTables()
	{
		FieldInfo[] fields = typeof(OpCodes).GetFields(BindingFlags.Static | BindingFlags.Public);
		for (int i = 0; i < fields.Length; i++)
		{
			if (fields[i].GetValue(null) is OpCode opCode)
			{
				ushort num = (ushort)opCode.Value;
				if (num < 256)
				{
					OneByteOpCodes[num] = opCode;
				}
				else if ((num & 0xFF00) == 65024)
				{
					TwoByteOpCodes[num & 0xFF] = opCode;
				}
			}
		}
	}

	private static int OperandSize(OperandType type, byte[] il, int p)
	{
		switch (type)
		{
		case OperandType.InlineNone:
			return 0;
		case OperandType.ShortInlineBrTarget:
		case OperandType.ShortInlineI:
		case OperandType.ShortInlineVar:
			return 1;
		case OperandType.InlineVar:
			return 2;
		case OperandType.InlineBrTarget:
		case OperandType.InlineField:
		case OperandType.InlineI:
		case OperandType.InlineMethod:
		case OperandType.InlineSig:
		case OperandType.InlineString:
		case OperandType.InlineTok:
		case OperandType.InlineType:
		case OperandType.ShortInlineR:
			return 4;
		case OperandType.InlineI8:
		case OperandType.InlineR:
			return 8;
		case OperandType.InlineSwitch:
		{
			if (p + 4 > il.Length)
			{
				return -1;
			}
			int num = BitConverter.ToInt32(il, p);
			if (num < 0 || num > 100000)
			{
				return -1;
			}
			return 4 + num * 4;
		}
		default:
			return -1;
		}
	}

	internal static string Summary()
	{
		string text;
		string text2;
		lock (Sync)
		{
			text = ((SnapshotExamples.Count == 0) ? "<none>" : string.Join(";", SnapshotExamples.ToArray()));
			text2 = ((CleanExamples.Count == 0) ? "<none>" : string.Join(";", CleanExamples.ToArray()));
		}
		return "T27.2 WorkGiver parallel safety registry: initialized=" + initialized + ", defs/types/scanners=" + Interlocked.Read(ref defsSeen) + "/" + Interlocked.Read(ref uniqueTypes) + "/" + Interlocked.Read(ref scannerTypes) + ", modTypes=" + Interlocked.Read(ref modTypes) + ", foreignPatchedTypes=" + Interlocked.Read(ref foreignPatchedTypes) + ", preliminary[snapshot/directILClean/fullAdmitted]=" + Interlocked.Read(ref snapshotCandidates) + "/" + Interlocked.Read(ref directIlCleanCandidates) + "/" + Interlocked.Read(ref fullParallelAdmitted) + ", fullDenied=" + Interlocked.Read(ref fullParallelDenied) + ", risks[unity/reservation/reach/rand/jobFail/jobCreate/mutableWorld/sideEffect]=" + Interlocked.Read(ref riskUnity) + "/" + Interlocked.Read(ref riskReservation) + "/" + Interlocked.Read(ref riskReachability) + "/" + Interlocked.Read(ref riskRand) + "/" + Interlocked.Read(ref riskJobFailReason) + "/" + Interlocked.Read(ref riskJobCreation) + "/" + Interlocked.Read(ref riskMutableWorld) + "/" + Interlocked.Read(ref riskGlobalSideEffect) + ", ilScanFailures=" + Interlocked.Read(ref directIlScanFailures) + ". FullParallel=HARD_OFF; direct IL clean is candidate evidence only, never an admission decision. SnapshotExamples=" + text + "; DirectILCleanExamples=" + text2;
	}

	internal static string ApiMatrixSummary()
	{
		return "T27.2 thread-safety API matrix: PrimitiveSnapshot=WORKER_SAFE; PersistentFabricSnapshot=WORKER_SAFE; DefRead=READ_ONLY_CANDIDATE; Pawn/Thing/MapMutable=MAIN_THREAD; ReservationManager=MAIN_THREAD; LiveReachability=MAIN_THREAD; UnityEngine=MAIN_THREAD; Job/ToilPool=THREAD_LOCAL_REQUIRED(" + OnOff(value: false) + "); Rand=THREAD_LOCAL_REQUIRED(" + OnOff(value: false) + "); JobFailReason=THREAD_LOCAL_REQUIRED(" + OnOff(value: false) + "); ReservationPromise=DEFERRED_COMMIT_REQUIRED(" + OnOff(value: false) + "); ReachabilityIsolation=SNAPSHOT_REQUIRED(" + OnOff(value: false) + "); ModCallbackContainment=REQUIRED(" + OnOff(value: false) + ").";
	}

	private static string OnOff(bool value)
	{
		if (!value)
		{
			return "OFF";
		}
		return "READY";
	}
}
