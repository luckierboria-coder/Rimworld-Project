using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class AggressiveReachabilityProfilesV17
{
	internal struct ReachSampleState
	{
		internal readonly bool Active;

		internal readonly bool Predicted;

		internal readonly ProfileSlot Slot;

		internal readonly long RegionGeneration;

		internal readonly bool LeaseProbe;

		internal ReachSampleState(bool active, bool predicted, ProfileSlot slot, long generation, bool leaseProbe = false)
		{
			Active = active;
			Predicted = predicted;
			Slot = slot;
			RegionGeneration = generation;
			LeaseProbe = leaseProbe;
		}
	}

	private enum ReachFuseMode
	{
		Normal,
		Cooldown,
		Probation,
		HardFused
	}

	internal enum Prediction
	{
		Unknown,
		Reachable,
		Unreachable
	}

	private enum TopologyAdvanceResult
	{
		Pending,
		Complete,
		Stale,
		Failed
	}

	private enum TopologyBuildPhase
	{
		Cells,
		NormalizeCells,
		Districts,
		Adjacency,
		EdgeOffsets,
		FlattenEdges,
		Complete
	}

	private sealed class MapState
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly ConditionalWeakTable<Pawn, PawnState> Pawns = new ConditionalWeakTable<Pawn, PawnState>();

		internal long RegionGeneration = 1L;

		internal TopologySnapshot Topology;

		internal TopologyBuildState TopologyBuild;

		internal long CaptureDisabledUntilFrame;

		internal MapState(int mapId, int width, int height)
		{
			MapId = mapId;
			Width = width;
			Height = height;
		}
	}

	private sealed class PawnState
	{
		private readonly Dictionary<TraverseKey, ProfileSlot> slots = new Dictionary<TraverseKey, ProfileSlot>();

		internal ProfileSlot GetOrCreate(TraverseKey key)
		{
			if (!slots.TryGetValue(key, out var value))
			{
				value = new ProfileSlot();
				slots.Add(key, value);
			}
			return value;
		}
	}

	internal sealed class ProfileSlot
	{
		internal int BuildScheduled;

		internal long LastScheduleFrame;

		internal long DisabledUntilFrame;

		internal int ValidatedMatches;

		internal int PredictionSerial;

		internal int LeaseProbeMatches;

		internal long LeaseUntilFrame;

		internal ProfileSnapshot Published;
	}

	internal struct TraverseKey : IEquatable<TraverseKey>
	{
		internal readonly TraverseMode Mode;

		internal readonly Danger MaxDanger;

		internal readonly bool CanBashDoors;

		internal readonly bool CanBashFences;

		internal readonly bool AlwaysUseAvoidGrid;

		internal readonly bool FenceBlocked;

		internal TraverseKey(TraverseParms parms)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0002: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000d: Unknown result type (might be due to invalid IL or missing references)
			//IL_000e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0013: Unknown result type (might be due to invalid IL or missing references)
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_0031: Unknown result type (might be due to invalid IL or missing references)
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			Mode = parms.mode;
			MaxDanger = parms.maxDanger;
			CanBashDoors = parms.canBashDoors;
			CanBashFences = parms.canBashFences;
			AlwaysUseAvoidGrid = parms.alwaysUseAvoidGrid;
			FenceBlocked = parms.fenceBlocked;
		}

		public bool Equals(TraverseKey other)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			if (Mode == other.Mode && MaxDanger == other.MaxDanger && CanBashDoors == other.CanBashDoors && CanBashFences == other.CanBashFences && AlwaysUseAvoidGrid == other.AlwaysUseAvoidGrid)
			{
				return FenceBlocked == other.FenceBlocked;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is TraverseKey)
			{
				return Equals((TraverseKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_000b: Unknown result type (might be due to invalid IL or missing references)
			//IL_000d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_0022: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0038: Unknown result type (might be due to invalid IL or missing references)
			//IL_0042: Unknown result type (might be due to invalid IL or missing references)
			//IL_0048: Unknown result type (might be due to invalid IL or missing references)
			//IL_0052: Unknown result type (might be due to invalid IL or missing references)
			//IL_0054: Expected I4, but got Unknown
			return (((((((((Mode * 397) ^ MaxDanger) * 397) ^ CanBashDoors) * 397) ^ CanBashFences) * 397) ^ AlwaysUseAvoidGrid) * 397) ^ FenceBlocked;
		}
	}

	private sealed class TopologyBuildState
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly long RegionGeneration;

		internal readonly Region[] Direct;

		internal readonly Dictionary<Region, int> RegionIndex = new Dictionary<Region, int>(ReferenceEqualityComparer<Region>.Instance);

		internal readonly List<Region> Regions = new List<Region>();

		internal readonly int[] CellRegion;

		internal readonly Dictionary<District, int> DistrictIndex = new Dictionary<District, int>(ReferenceEqualityComparer<District>.Instance);

		internal int[] DistrictByRegion;

		internal List<int>[] Adjacency;

		internal int[] EdgeOffsets;

		internal int[] Edges;

		internal TopologyBuildPhase Phase;

		internal int CellCursor;

		internal int RegionCursor;

		internal int NextDistrict = 1;

		internal int EdgeCount;

		internal int FlattenRegion;

		internal int FlattenLocal;

		internal int EdgeWrite;

		internal long LastSliceFrame = -1L;

		internal TopologyBuildState(int mapId, int width, int height, long generation, Region[] direct, int cells)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			RegionGeneration = generation;
			Direct = direct;
			CellRegion = new int[cells];
			Phase = TopologyBuildPhase.Cells;
		}
	}

	private sealed class TopologySnapshot
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly long RegionGeneration;

		internal readonly Region[] RegionRefs;

		internal readonly int[] CellRegion;

		internal readonly int[] DistrictByRegion;

		internal readonly int[] EdgeOffsets;

		internal readonly int[] Edges;

		internal TopologySnapshot(int mapId, int width, int height, long generation, Region[] regions, int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			RegionGeneration = generation;
			RegionRefs = regions;
			CellRegion = cellRegion;
			DistrictByRegion = districtByRegion;
			EdgeOffsets = edgeOffsets;
			Edges = edges;
		}
	}

	private sealed class ProfileCaptureState
	{
		internal readonly Map Map;

		internal readonly MapState MapState;

		internal readonly Pawn Pawn;

		internal readonly TraverseParms TraverseParams;

		internal readonly TraverseKey Key;

		internal readonly ProfileSlot Slot;

		internal readonly TopologySnapshot Topology;

		internal readonly long RegionGeneration;

		internal readonly long QueuedFrame;

		internal readonly bool[] TraverseAllowed;

		internal readonly bool[] DestinationAllowed;

		internal int Cursor;

		internal ProfileCaptureState(Map map, MapState mapState, Pawn pawn, TraverseParms traverseParams, TraverseKey key, ProfileSlot slot, TopologySnapshot topology, long generation, long queuedFrame)
		{
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001e: Unknown result type (might be due to invalid IL or missing references)
			Map = map;
			MapState = mapState;
			Pawn = pawn;
			TraverseParams = traverseParams;
			Key = key;
			Slot = slot;
			Topology = topology;
			RegionGeneration = generation;
			QueuedFrame = queuedFrame;
			TraverseAllowed = new bool[topology.RegionRefs.Length];
			DestinationAllowed = new bool[topology.RegionRefs.Length];
			Cursor = 0;
		}
	}

	private sealed class ProfileBuildContext
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly long RegionGeneration;

		internal readonly long CaptureFrame;

		internal readonly TraverseKey Key;

		internal readonly int[] CellRegion;

		internal readonly int[] DistrictByRegion;

		internal readonly int[] EdgeOffsets;

		internal readonly int[] Edges;

		internal readonly bool[] TraverseAllowed;

		internal readonly bool[] DestinationAllowed;

		internal ProfileBuildContext(int mapId, int width, int height, long generation, long captureFrame, TraverseKey key, int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges, bool[] traverseAllowed, bool[] destinationAllowed)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			RegionGeneration = generation;
			CaptureFrame = captureFrame;
			Key = key;
			CellRegion = cellRegion;
			DistrictByRegion = districtByRegion;
			EdgeOffsets = edgeOffsets;
			Edges = edges;
			TraverseAllowed = traverseAllowed;
			DestinationAllowed = destinationAllowed;
		}
	}

	internal sealed class ProfileSnapshot
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly long RegionGeneration;

		internal readonly long CaptureFrame;

		internal readonly TraverseKey Key;

		private readonly int[] cellRegion;

		private readonly int[] districtByRegion;

		private readonly int[] edgeOffsets;

		private readonly int[] edges;

		private readonly int[] components;

		private readonly bool[] destinationAllowed;

		internal ProfileSnapshot(int mapId, int width, int height, long generation, long captureFrame, TraverseKey key, int[] cellRegion, int[] districtByRegion, int[] edgeOffsets, int[] edges, int[] components, bool[] destinationAllowed)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			RegionGeneration = generation;
			CaptureFrame = captureFrame;
			Key = key;
			this.cellRegion = cellRegion;
			this.districtByRegion = districtByRegion;
			this.edgeOffsets = edgeOffsets;
			this.edges = edges;
			this.components = components;
			this.destinationAllowed = destinationAllowed;
		}

		internal Prediction Classify(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, Map map, TraverseParms traverseParams)
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_000c: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_0086: Unknown result type (might be due to invalid IL or missing references)
			//IL_0089: Unknown result type (might be due to invalid IL or missing references)
			//IL_003a: Unknown result type (might be due to invalid IL or missing references)
			//IL_003c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0042: Invalid comparison between Unknown and I4
			//IL_0044: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_004c: Invalid comparison between Unknown and I4
			//IL_012e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0130: Invalid comparison between Unknown and I4
			//IL_0170: Unknown result type (might be due to invalid IL or missing references)
			//IL_017e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0191: Unknown result type (might be due to invalid IL or missing references)
			//IL_0159: Unknown result type (might be due to invalid IL or missing references)
			//IL_015e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0163: Unknown result type (might be due to invalid IL or missing references)
			//IL_0168: Unknown result type (might be due to invalid IL or missing references)
			//IL_0225: Unknown result type (might be due to invalid IL or missing references)
			//IL_019f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0214: Unknown result type (might be due to invalid IL or missing references)
			if (!GenGrid.InBounds(start, map) || !GenGrid.InBounds(((LocalTargetInfo)(ref dest)).Cell, map))
			{
				return Prediction.Unknown;
			}
			int num = RegionAt(start);
			int num2 = RegionAt(((LocalTargetInfo)(ref dest)).Cell);
			if (num >= 0 && num2 >= 0 && (int)traverseParams.mode != 5 && (int)traverseParams.mode != 6)
			{
				int num3 = districtByRegion[num];
				int num4 = districtByRegion[num2];
				if (num3 != 0 && num3 == num4)
				{
					return Prediction.Reachable;
				}
			}
			int[] array = rootRegionScratch ?? (rootRegionScratch = new int[16]);
			int num5 = GatherStartRegions(start, map, traverseParams, array);
			if (num5 == 0)
			{
				return Prediction.Unreachable;
			}
			int[] array2 = rootComponentScratch ?? (rootComponentScratch = new int[32]);
			int count = 0;
			for (int i = 0; i < num5; i++)
			{
				int num6 = array[i];
				AddUniqueComponent(components[num6], array2, ref count);
				int num7 = edgeOffsets[num6];
				int num8 = edgeOffsets[num6 + 1];
				for (int j = num7; j < num8; j++)
				{
					int num9 = edges[j];
					if (num9 >= 0 && num9 < components.Length)
					{
						AddUniqueComponent(components[num9], array2, ref count);
					}
				}
			}
			if ((int)peMode == 1)
			{
				return RegionReachability(num2, array, num5, array2, count);
			}
			CellRect val2 = default(CellRect);
			if (((LocalTargetInfo)(ref dest)).HasThing && ((LocalTargetInfo)(ref dest)).Thing != null)
			{
				CellRect val = GenAdj.OccupiedRect(((LocalTargetInfo)(ref dest)).Thing);
				val2 = ((CellRect)(ref val)).ExpandedBy(1);
			}
			else
			{
				((CellRect)(ref val2))._002Ector(((LocalTargetInfo)(ref dest)).Cell.x - 1, ((LocalTargetInfo)(ref dest)).Cell.z - 1, 3, 3);
			}
			for (int k = val2.minZ; k <= val2.maxZ; k++)
			{
				for (int l = val2.minX; l <= val2.maxX; l++)
				{
					if (l >= 0 && k >= 0 && l < Width && k < Height)
					{
						int num10 = cellRegion[l + k * Width];
						if (num10 >= 0 && num10 < destinationAllowed.Length && destinationAllowed[num10] && RegionReachability(num10, array, num5, array2, count) == Prediction.Reachable)
						{
							return Prediction.Reachable;
						}
					}
				}
			}
			return Prediction.Unreachable;
		}

		private int GatherStartRegions(IntVec3 start, Map map, TraverseParms traverseParams, int[] output)
		{
			//IL_0008: Unknown result type (might be due to invalid IL or missing references)
			//IL_001c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0025: Unknown result type (might be due to invalid IL or missing references)
			//IL_003a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_004b: Unknown result type (might be due to invalid IL or missing references)
			//IL_004d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0058: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			int count = 0;
			PathGrid pathGrid;
			try
			{
				pathGrid = map.pathing.For(traverseParams).pathGrid;
			}
			catch
			{
				return 0;
			}
			if (pathGrid.WalkableFast(start))
			{
				AddUniqueRegion(RegionAt(start), output, ref count);
				return count;
			}
			for (int i = 0; i < 8; i++)
			{
				IntVec3 val = start + GenAdj.AdjacentCells[i];
				if (GenGrid.InBounds(val, map) && pathGrid.WalkableFast(val))
				{
					AddUniqueRegion(RegionAt(val), output, ref count);
				}
			}
			return count;
		}

		private Prediction RegionReachability(int targetRegion, int[] seedRegions, int seedCount, int[] rootComponents, int rootComponentCount)
		{
			if (targetRegion < 0 || targetRegion >= destinationAllowed.Length || !destinationAllowed[targetRegion])
			{
				return Prediction.Unreachable;
			}
			for (int i = 0; i < seedCount; i++)
			{
				if (seedRegions[i] == targetRegion)
				{
					return Prediction.Reachable;
				}
			}
			int num = components[targetRegion];
			if (num == 0)
			{
				return Prediction.Unreachable;
			}
			for (int j = 0; j < rootComponentCount; j++)
			{
				if (rootComponents[j] == num)
				{
					return Prediction.Reachable;
				}
			}
			return Prediction.Unreachable;
		}

		private int RegionAt(IntVec3 c)
		{
			//IL_0000: Unknown result type (might be due to invalid IL or missing references)
			//IL_0009: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0020: Unknown result type (might be due to invalid IL or missing references)
			//IL_0036: Unknown result type (might be due to invalid IL or missing references)
			//IL_003c: Unknown result type (might be due to invalid IL or missing references)
			if (c.x < 0 || c.z < 0 || c.x >= Width || c.z >= Height)
			{
				return -1;
			}
			return cellRegion[c.x + c.z * Width];
		}

		private static void AddUniqueRegion(int region, int[] output, ref int count)
		{
			if (region < 0)
			{
				return;
			}
			for (int i = 0; i < count; i++)
			{
				if (output[i] == region)
				{
					return;
				}
			}
			if (count < output.Length)
			{
				output[count++] = region;
			}
		}

		private static void AddUniqueComponent(int component, int[] output, ref int count)
		{
			if (component <= 0)
			{
				return;
			}
			for (int i = 0; i < count; i++)
			{
				if (output[i] == component)
				{
					return;
				}
			}
			if (count < output.Length)
			{
				output[count++] = component;
			}
		}
	}

	private sealed class ReferenceEqualityComparer<T> : IEqualityComparer<T> where T : class
	{
		internal static readonly ReferenceEqualityComparer<T> Instance = new ReferenceEqualityComparer<T>();

		public bool Equals(T x, T y)
		{
			return x == y;
		}

		public int GetHashCode(T obj)
		{
			return RuntimeHelpers.GetHashCode(obj);
		}
	}

	internal const string FeatureId = "parallel.reachProfile";

	private const int MaxSnapshotCells = 160000;

	private const long MaxProfileAgeFrames = 180L;

	private const long LeaseExtensionFrames = 180L;

	private const long HardMaxProfileAgeFrames = 720L;

	private const int LeaseProbeSamplesRequired = 4;

	private const long BuildCooldownFrames = 12L;

	private const long MismatchCooldownFrames = 600L;

	private const int WarmupSamples = 8;

	private const int SampleMask = 127;

	private const int GlobalWindowSamples = 8192;

	private const int GlobalMismatchLimit = 8;

	private const int GlobalDistinctSlotLimit = 3;

	private const long GlobalCooldownFrames = 3600L;

	private const int ProbationSamples = 256;

	private const int EmergencyWindowSamples = 256;

	private const int EmergencyMismatchLimit = 16;

	private const int SliceCheckMask = 15;

	private const int CaptureCheckMask = 3;

	private const long MaxCaptureQueueAgeFrames = 8L;

	private const int CaptureWatchdogMicroseconds = 5000;

	private const long CaptureMapQuarantineFrames = 3600L;

	private static readonly ConditionalWeakTable<Map, MapState> MapStates = new ConditionalWeakTable<Map, MapState>();

	private static readonly Queue<ProfileCaptureState> PendingProfileCaptures = new Queue<ProfileCaptureState>();

	private static readonly bool[] GlobalMismatchWindow = new bool[8192];

	private static readonly ProfileSlot[] GlobalMismatchSlotWindow = new ProfileSlot[8192];

	private static readonly Dictionary<ProfileSlot, int> GlobalMismatchSlotCounts = new Dictionary<ProfileSlot, int>();

	private static readonly bool[] EmergencyMismatchWindow = new bool[256];

	private static volatile bool compatibilityReady;

	[ThreadStatic]
	private static int[] rootRegionScratch;

	[ThreadStatic]
	private static int[] rootComponentScratch;

	private static long observed;

	private static long eligible;

	private static long priorPrefixOwned;

	private static long immediateHits;

	private static long unsupported;

	private static long profileHits;

	private static long profileMisses;

	private static long profileExpired;

	private static long profileCooldownBypass;

	private static long topologyBuildStarts;

	private static long topologyBuilds;

	private static long topologyBuildDiscarded;

	private static long topologyStale;

	private static long topologyFailures;

	private static long topologySlices;

	private static long topologySliceTicks;

	private static long topologySliceTicksMax;

	private static long profileCaptures;

	private static long profileCaptureTicks;

	private static long profileCaptureTicksMax;

	private static long profileCaptureQueued;

	private static long profileCaptureSlices;

	private static long profileCaptureAborted;

	private static long profileCaptureWatchdogTrips;

	private static long profileCapturePressureBypass;

	private static long profileCaptureMapQuarantines;

	private static long profileCapturePairs;

	private static long profileCapturePairTicksMax;

	private static long admissionCompatibilityBypass;

	private static long admissionFeatureGateBypass;

	private static long admissionThreadBypass;

	private static long admissionProgramStateBypass;

	private static long buildsScheduled;

	private static long buildsPublished;

	private static long buildsRejected;

	private static long buildsStale;

	private static long workerFailures;

	private static long workerBuildTicks;

	private static long workerBuildTicksMax;

	private static long predictedReachable;

	private static long predictedUnreachable;

	private static long authoritativeTrue;

	private static long authoritativeFalse;

	private static long shadowSamples;

	private static long shadowMatches;

	private static long parityMismatches;

	private static long mismatchReachableToFalse;

	private static long mismatchUnreachableToTrue;

	private static long regionDirtyEvents;

	private static long queriesUnknown;

	private static long queryTicks;

	private static long queryTicksMax;

	private static long localSlotQuarantines;

	private static long localOnlyFuseDeferrals;

	private static long leaseProbeCalls;

	private static long leaseProbeMatches;

	private static long leaseRenewals;

	private static long leaseFailures;

	private static long forcedRefreshes;

	private static ReachFuseMode reachFuseMode;

	private static int globalWindowPos;

	private static int globalWindowCount;

	private static int globalWindowMismatches;

	private static int emergencyWindowPos;

	private static int emergencyWindowCount;

	private static int emergencyWindowMismatches;

	private static long cooldownUntilFrame;

	private static int probationRemaining;

	private static int probationMatches;

	private static long rollingSamples;

	private static long rollingMismatches;

	private static long softFuses;

	private static long cooldownLiveBypass;

	private static long probationForcedShadow;

	private static long probationPasses;

	private static long probationFailures;

	private static long hardFuses;

	internal static void Apply(Harmony harmony)
	{
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Expected O, but got Unknown
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c9: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		PatchRegionDirtySignals(harmony);
		PatchProfileCaptureDrain(harmony);
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(Reachability), "CanReach", new Type[4]
			{
				typeof(IntVec3),
				typeof(LocalTargetInfo),
				typeof(PathEndMode),
				typeof(TraverseParms)
			}, (Type[])null);
			if (methodBase == null)
			{
				FeatureGate.Suppress("parallel.reachProfile", "Reachability.CanReach target not found");
				Log.Warning("[RimMT] parallel.reachProfile V0.4.17 unavailable: Reachability.CanReach target not found.");
				return;
			}
			CompatibilityGuard.RegisterTarget("parallel.reachProfile", methodBase);
			HarmonyMethod val = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), "Prefix", (Type[])null);
			val.priority = 200;
			HarmonyMethod val2 = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), "Postfix", (Type[])null);
			val2.priority = 800;
			harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] parallel.reachProfile V0.4.18-T1A installed: topology capture remains frame-sliced; expired profiles still require four clean forced-live lease probes; global soft/probation/hard fuse is disabled; mismatch handling is local-slot quarantine only.");
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("parallel.reachProfile", "reachability profile V0.4.17 patch failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] parallel.reachProfile V0.4.17 patch failed; Vanilla Reachability remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void MarkCompatibilityReady()
	{
		compatibilityReady = true;
	}

	public static bool Prefix(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, Map ___map, bool __runOriginal, ref bool __result, out ReachSampleState __state)
	{
		//IL_0067: Unknown result type (might be due to invalid IL or missing references)
		//IL_006d: Invalid comparison between Unknown and I4
		//IL_007c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ae: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ff: Unknown result type (might be due to invalid IL or missing references)
		//IL_0100: Unknown result type (might be due to invalid IL or missing references)
		//IL_0102: Unknown result type (might be due to invalid IL or missing references)
		//IL_016a: Unknown result type (might be due to invalid IL or missing references)
		//IL_026f: Unknown result type (might be due to invalid IL or missing references)
		//IL_01d9: Unknown result type (might be due to invalid IL or missing references)
		//IL_022c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0240: Unknown result type (might be due to invalid IL or missing references)
		//IL_02db: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b4: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b5: Unknown result type (might be due to invalid IL or missing references)
		//IL_03b7: Unknown result type (might be due to invalid IL or missing references)
		//IL_030f: Unknown result type (might be due to invalid IL or missing references)
		//IL_0310: Unknown result type (might be due to invalid IL or missing references)
		//IL_0311: Unknown result type (might be due to invalid IL or missing references)
		//IL_0313: Unknown result type (might be due to invalid IL or missing references)
		//IL_033c: Unknown result type (might be due to invalid IL or missing references)
		__state = default(ReachSampleState);
		Interlocked.Increment(ref observed);
		if (!__runOriginal)
		{
			Interlocked.Increment(ref priorPrefixOwned);
			return true;
		}
		if (!compatibilityReady)
		{
			Interlocked.Increment(ref admissionCompatibilityBypass);
			return true;
		}
		if (!FeatureGate.IsEnabled("parallel.reachProfile"))
		{
			Interlocked.Increment(ref admissionFeatureGateBypass);
			return true;
		}
		if (!RimMTThreadGuard.IsMainThread)
		{
			Interlocked.Increment(ref admissionThreadBypass);
			return true;
		}
		if ((int)Current.ProgramState != 2)
		{
			Interlocked.Increment(ref admissionProgramStateBypass);
			return true;
		}
		Pawn pawn = traverseParams.pawn;
		if (___map == null || ___map.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != ___map || !((IntVec3)(ref start)).IsValid || !GenGrid.InBounds(start, ___map) || !((LocalTargetInfo)(ref dest)).IsValid || !GenGrid.InBounds(((LocalTargetInfo)(ref dest)).Cell, ___map))
		{
			Interlocked.Increment(ref unsupported);
			return true;
		}
		if (!SupportedTraverseMode(traverseParams.mode) || !SupportedPathEndMode(peMode))
		{
			Interlocked.Increment(ref unsupported);
			return true;
		}
		try
		{
			if (ReachabilityImmediate.CanReachImmediate(start, dest, ___map, peMode, pawn))
			{
				__result = true;
				Interlocked.Increment(ref immediateHits);
				return false;
			}
		}
		catch
		{
			return true;
		}
		Interlocked.Increment(ref eligible);
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			MapState value = MapStates.GetValue(___map, (Map m) => new MapState(m.uniqueID, m.Size.x, m.Size.z));
			TraverseKey traverseKey = new TraverseKey(traverseParams);
			ProfileSlot orCreate = value.Pawns.GetValue(pawn, (Pawn p) => new PawnState()).GetOrCreate(traverseKey);
			long mainThreadFrames = RimMTRuntime.MainThreadFrames;
			long num = Interlocked.Read(ref orCreate.DisabledUntilFrame);
			if (num > mainThreadFrames)
			{
				Interlocked.Increment(ref profileCooldownBypass);
				if (num - mainThreadFrames <= 12)
				{
					EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				}
				return true;
			}
			ProfileSnapshot profileSnapshot = Volatile.Read(ref orCreate.Published);
			long num2 = Interlocked.Read(ref value.RegionGeneration);
			if (profileSnapshot == null || profileSnapshot.RegionGeneration != num2 || profileSnapshot.MapId != ___map.uniqueID || profileSnapshot.Width != ___map.Size.x || profileSnapshot.Height != ___map.Size.z || !profileSnapshot.Key.Equals(traverseKey))
			{
				Interlocked.Increment(ref profileMisses);
				EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				return true;
			}
			long num3 = mainThreadFrames - profileSnapshot.CaptureFrame;
			long num4 = Interlocked.Read(ref orCreate.LeaseUntilFrame);
			if (num3 > 720)
			{
				Interlocked.Increment(ref profileExpired);
				Interlocked.Increment(ref forcedRefreshes);
				Interlocked.Exchange(ref orCreate.LeaseProbeMatches, 0);
				Interlocked.Exchange(ref orCreate.LeaseUntilFrame, 0L);
				EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				return true;
			}
			if (num3 > 180 && num4 <= mainThreadFrames)
			{
				Interlocked.Increment(ref profileExpired);
				Prediction prediction = profileSnapshot.Classify(start, dest, peMode, ___map, traverseParams);
				if (prediction == Prediction.Unknown)
				{
					Interlocked.Increment(ref queriesUnknown);
					Interlocked.Exchange(ref orCreate.LeaseProbeMatches, 0);
					EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
					return true;
				}
				bool flag = prediction == Prediction.Reachable;
				if (flag)
				{
					Interlocked.Increment(ref predictedReachable);
				}
				else
				{
					Interlocked.Increment(ref predictedUnreachable);
				}
				__state = new ReachSampleState(active: true, flag, orCreate, profileSnapshot.RegionGeneration, leaseProbe: true);
				Interlocked.Increment(ref shadowSamples);
				Interlocked.Increment(ref leaseProbeCalls);
				return true;
			}
			Interlocked.Increment(ref profileHits);
			Prediction prediction2 = profileSnapshot.Classify(start, dest, peMode, ___map, traverseParams);
			if (prediction2 == Prediction.Unknown)
			{
				Interlocked.Increment(ref queriesUnknown);
				return true;
			}
			bool flag2 = prediction2 == Prediction.Reachable;
			if (flag2)
			{
				Interlocked.Increment(ref predictedReachable);
			}
			else
			{
				Interlocked.Increment(ref predictedUnreachable);
			}
			int num5 = Volatile.Read(ref orCreate.ValidatedMatches);
			int num6 = Interlocked.Increment(ref orCreate.PredictionSerial);
			if (num5 < 8 || (num6 & 0x7F) == 0)
			{
				__state = new ReachSampleState(active: true, flag2, orCreate, profileSnapshot.RegionGeneration);
				Interlocked.Increment(ref shadowSamples);
				return true;
			}
			__result = flag2;
			if (flag2)
			{
				Interlocked.Increment(ref authoritativeTrue);
			}
			else
			{
				Interlocked.Increment(ref authoritativeFalse);
			}
			return false;
		}
		catch (Exception ex)
		{
			CircuitBreaker.RecordFailure("parallel.reachProfile", ex);
			Log.Warning("[RimMT] parallel.reachProfile V0.4.17 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
			return true;
		}
		finally
		{
			RecordElapsed(ref queryTicks, ref queryTicksMax, timestamp);
		}
	}

	public static void Postfix(bool __result, ReachSampleState __state)
	{
		if (!__state.Active || __state.Slot == null)
		{
			return;
		}
		bool flag = __result != __state.Predicted;
		if (__state.LeaseProbe && !flag)
		{
			Interlocked.Increment(ref shadowMatches);
			Interlocked.Increment(ref leaseProbeMatches);
			if (Interlocked.Increment(ref __state.Slot.LeaseProbeMatches) >= 4)
			{
				Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
				Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, RimMTRuntime.MainThreadFrames + 180);
				Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 4);
				Interlocked.Increment(ref leaseRenewals);
			}
			return;
		}
		if (__state.LeaseProbe)
		{
			Interlocked.Increment(ref leaseFailures);
			Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
			Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
		}
		if (!flag)
		{
			Interlocked.Increment(ref shadowMatches);
			Interlocked.Increment(ref __state.Slot.ValidatedMatches);
			return;
		}
		Interlocked.Increment(ref parityMismatches);
		if (__state.Predicted)
		{
			Interlocked.Increment(ref mismatchReachableToFalse);
		}
		else
		{
			Interlocked.Increment(ref mismatchUnreachableToTrue);
		}
		Interlocked.Exchange(ref __state.Slot.ValidatedMatches, 0);
		Interlocked.Exchange(ref __state.Slot.LeaseProbeMatches, 0);
		Interlocked.Exchange(ref __state.Slot.LeaseUntilFrame, 0L);
		Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + 600);
		Volatile.Write(ref __state.Slot.Published, null);
		Interlocked.Increment(ref localSlotQuarantines);
	}

	private static void UpdateRollingFuseMode()
	{
		if (reachFuseMode == ReachFuseMode.Cooldown && RimMTRuntime.MainThreadFrames >= cooldownUntilFrame)
		{
			reachFuseMode = ReachFuseMode.Probation;
			probationRemaining = 256;
			probationMatches = 0;
			ClearGlobalWindow();
			ClearEmergencyWindow();
			Log.Message("[RimMT] ReachProfile V0.4.17 global cooldown ended; entering 256-sample forced-live probation.");
		}
	}

	private static void ObserveRollingSample(bool mismatch, ProfileSlot slot)
	{
		rollingSamples++;
		if (mismatch)
		{
			rollingMismatches++;
		}
		if (reachFuseMode == ReachFuseMode.HardFused || reachFuseMode == ReachFuseMode.Cooldown)
		{
			return;
		}
		PushGlobalWindow(mismatch, slot);
		PushBoolWindow(EmergencyMismatchWindow, ref emergencyWindowPos, ref emergencyWindowCount, ref emergencyWindowMismatches, mismatch);
		if (emergencyWindowMismatches >= 16)
		{
			reachFuseMode = ReachFuseMode.HardFused;
			hardFuses++;
			FeatureGate.Suppress("parallel.reachProfile", "V0.4.17 emergency ReachProfile hard fuse: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " sampled mismatches");
			Log.Warning("[RimMT] ReachProfile HARD FUSE V0.4.17: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " mismatches in the emergency sample window. Vanilla Reachability is authoritative for the rest of this run.");
		}
		else if (reachFuseMode == ReachFuseMode.Probation)
		{
			if (mismatch)
			{
				probationFailures++;
				EnterGlobalCooldown("probation mismatch");
				return;
			}
			probationMatches++;
			if (probationRemaining > 0)
			{
				probationRemaining--;
			}
			if (probationRemaining <= 0)
			{
				reachFuseMode = ReachFuseMode.Normal;
				probationPasses++;
				ClearGlobalWindow();
				ClearEmergencyWindow();
				Log.Message("[RimMT] ReachProfile V0.4.17 probation passed 256 clean live-shadow samples; profile authority restored.");
			}
		}
		else if (reachFuseMode == ReachFuseMode.Normal && globalWindowMismatches >= 8)
		{
			int count = GlobalMismatchSlotCounts.Count;
			if (count >= 3)
			{
				EnterGlobalCooldown("rolling mismatch density " + globalWindowMismatches + "/" + globalWindowCount + " across " + count + " slots");
			}
			else if (mismatch)
			{
				localOnlyFuseDeferrals++;
			}
		}
	}

	private static void EnterGlobalCooldown(string reason)
	{
		reachFuseMode = ReachFuseMode.Cooldown;
		cooldownUntilFrame = RimMTRuntime.MainThreadFrames + 3600;
		probationRemaining = 0;
		probationMatches = 0;
		softFuses++;
		ClearGlobalWindow();
		ClearEmergencyWindow();
		Log.Warning("[RimMT] ReachProfile SOFT FUSE V0.4.17: " + reason + ". Global profile authority is bypassed for 3600 main-thread frames; affected slots are already quarantined independently.");
	}

	private static void PushGlobalWindow(bool mismatch, ProfileSlot slot)
	{
		if (globalWindowCount >= GlobalMismatchWindow.Length)
		{
			if (GlobalMismatchWindow[globalWindowPos])
			{
				globalWindowMismatches--;
				ProfileSlot profileSlot = GlobalMismatchSlotWindow[globalWindowPos];
				if (profileSlot != null && GlobalMismatchSlotCounts.TryGetValue(profileSlot, out var value))
				{
					if (value <= 1)
					{
						GlobalMismatchSlotCounts.Remove(profileSlot);
					}
					else
					{
						GlobalMismatchSlotCounts[profileSlot] = value - 1;
					}
				}
			}
		}
		else
		{
			globalWindowCount++;
		}
		GlobalMismatchWindow[globalWindowPos] = mismatch;
		GlobalMismatchSlotWindow[globalWindowPos] = (mismatch ? slot : null);
		if (mismatch)
		{
			globalWindowMismatches++;
			if (slot != null)
			{
				GlobalMismatchSlotCounts.TryGetValue(slot, out var value2);
				GlobalMismatchSlotCounts[slot] = value2 + 1;
			}
		}
		globalWindowPos = (globalWindowPos + 1) % GlobalMismatchWindow.Length;
	}

	private static void PushBoolWindow(bool[] window, ref int pos, ref int count, ref int mismatchCount, bool mismatch)
	{
		if (count < window.Length)
		{
			window[pos] = mismatch;
			if (mismatch)
			{
				mismatchCount++;
			}
			count++;
			pos = (pos + 1) % window.Length;
			return;
		}
		if (window[pos])
		{
			mismatchCount--;
		}
		window[pos] = mismatch;
		if (mismatch)
		{
			mismatchCount++;
		}
		pos = (pos + 1) % window.Length;
	}

	private static void ClearGlobalWindow()
	{
		Array.Clear(GlobalMismatchWindow, 0, GlobalMismatchWindow.Length);
		Array.Clear(GlobalMismatchSlotWindow, 0, GlobalMismatchSlotWindow.Length);
		GlobalMismatchSlotCounts.Clear();
		globalWindowPos = 0;
		globalWindowCount = 0;
		globalWindowMismatches = 0;
	}

	private static void ClearEmergencyWindow()
	{
		Array.Clear(EmergencyMismatchWindow, 0, EmergencyMismatchWindow.Length);
		emergencyWindowPos = 0;
		emergencyWindowCount = 0;
		emergencyWindowMismatches = 0;
	}

	private static bool SupportedTraverseMode(TraverseMode mode)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0003: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Invalid comparison between Unknown and I4
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0009: Invalid comparison between Unknown and I4
		if ((int)mode != 0 && (int)mode != 1)
		{
			return (int)mode == 2;
		}
		return true;
	}

	private static bool SupportedPathEndMode(PathEndMode mode)
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0002: Invalid comparison between Unknown and I4
		//IL_0004: Unknown result type (might be due to invalid IL or missing references)
		//IL_0006: Invalid comparison between Unknown and I4
		//IL_0008: Unknown result type (might be due to invalid IL or missing references)
		//IL_000a: Invalid comparison between Unknown and I4
		if ((int)mode != 1 && (int)mode != 2)
		{
			return (int)mode == 3;
		}
		return true;
	}

	private static void EnsureProfileScheduled(Map map, MapState mapState, Pawn pawn, TraverseParms traverseParams, TraverseKey key, ProfileSlot slot)
	{
		//IL_00dc: Unknown result type (might be due to invalid IL or missing references)
		if (map == null || map.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != map || !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled("parallel.reachProfile"))
		{
			return;
		}
		long mainThreadFrames = RimMTRuntime.MainThreadFrames;
		if (AdaptiveLoadBalancer.Pressure == LoadPressure.Critical)
		{
			Interlocked.Increment(ref profileCapturePressureBypass);
			return;
		}
		if (Interlocked.Read(ref mapState.CaptureDisabledUntilFrame) > mainThreadFrames)
		{
			Interlocked.Increment(ref profileCapturePressureBypass);
			return;
		}
		long num = Interlocked.Read(ref slot.LastScheduleFrame);
		if ((num != 0L && mainThreadFrames - num < 12) || Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0)
		{
			return;
		}
		TopologySnapshot topologySnapshot = EnsureTopology(map, mapState);
		if (topologySnapshot == null)
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			return;
		}
		long num2 = Interlocked.Read(ref mapState.RegionGeneration);
		if (topologySnapshot.RegionGeneration != num2)
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsStale);
			return;
		}
		try
		{
			ProfileCaptureState item = new ProfileCaptureState(map, mapState, pawn, traverseParams, key, slot, topologySnapshot, num2, mainThreadFrames);
			PendingProfileCaptures.Enqueue(item);
			Interlocked.Increment(ref profileCaptureQueued);
		}
		catch
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsRejected);
		}
	}

	private static void PatchProfileCaptureDrain(Harmony harmony)
	{
		//IL_0032: Unknown result type (might be due to invalid IL or missing references)
		//IL_0038: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(Root_Play), "Update", (Type[])null, (Type[])null);
			if (!(methodBase == null))
			{
				HarmonyMethod val = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), "ProfileCaptureDrainPostfix", (Type[])null);
				val.priority = -100;
				harmony.Patch(methodBase, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null);
			}
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] T23 ReachProfile capture drain unavailable; profile misses remain Vanilla-authoritative. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void ProfileCaptureDrainPostfix()
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_000d: Invalid comparison between Unknown and I4
		if (!RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || PendingProfileCaptures.Count == 0)
		{
			return;
		}
		if (!FeatureGate.IsEnabled("parallel.reachProfile"))
		{
			while (PendingProfileCaptures.Count != 0)
			{
				DropCurrentCapture(stale: false, watchdog: false);
			}
		}
		else
		{
			DrainProfileCaptureBudget();
		}
	}

	private static void DrainProfileCaptureBudget()
	{
		//IL_0185: Unknown result type (might be due to invalid IL or missing references)
		//IL_01a3: Unknown result type (might be due to invalid IL or missing references)
		long mainThreadFrames = RimMTRuntime.MainThreadFrames;
		if (AdaptiveLoadBalancer.Pressure == LoadPressure.Critical)
		{
			while (PendingProfileCaptures.Count != 0)
			{
				Interlocked.Increment(ref profileCapturePressureBypass);
				DropCurrentCapture(stale: false, watchdog: false);
			}
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		long budgetTicks = ProfileCaptureSliceBudgetTicks();
		long num = Math.Max(1L, Stopwatch.Frequency * 5000 / 1000000);
		while (PendingProfileCaptures.Count != 0)
		{
			ProfileCaptureState profileCaptureState = PendingProfileCaptures.Peek();
			if (profileCaptureState == null || profileCaptureState.Slot == null || profileCaptureState.MapState == null || profileCaptureState.Topology == null)
			{
				DropCurrentCapture(stale: false, watchdog: false);
				continue;
			}
			if (mainThreadFrames - profileCaptureState.QueuedFrame > 8 || profileCaptureState.Map == null || profileCaptureState.Map.Disposed || profileCaptureState.Pawn == null || !((Thing)profileCaptureState.Pawn).Spawned || ((Thing)profileCaptureState.Pawn).Map != profileCaptureState.Map || Interlocked.Read(ref profileCaptureState.MapState.RegionGeneration) != profileCaptureState.RegionGeneration || profileCaptureState.Topology.RegionGeneration != profileCaptureState.RegionGeneration)
			{
				DropCurrentCapture(stale: true, watchdog: false);
				continue;
			}
			long timestamp2 = Stopwatch.GetTimestamp();
			bool flag = false;
			while (profileCaptureState.Cursor < profileCaptureState.Topology.RegionRefs.Length)
			{
				Region val = profileCaptureState.Topology.RegionRefs[profileCaptureState.Cursor];
				if (val == null || !val.valid)
				{
					RecordProfileCaptureSlice(timestamp2);
					DropCurrentCapture(stale: true, watchdog: false);
					flag = true;
					break;
				}
				long timestamp3 = Stopwatch.GetTimestamp();
				try
				{
					profileCaptureState.TraverseAllowed[profileCaptureState.Cursor] = val.Allows(profileCaptureState.TraverseParams, false);
					profileCaptureState.DestinationAllowed[profileCaptureState.Cursor] = val.Allows(profileCaptureState.TraverseParams, true);
				}
				catch
				{
					RecordProfileCaptureSlice(timestamp2);
					DropCurrentCapture(stale: false, watchdog: false);
					flag = true;
					break;
				}
				long num2 = Stopwatch.GetTimestamp() - timestamp3;
				profileCaptureState.Cursor++;
				Interlocked.Increment(ref profileCapturePairs);
				UpdateMax(ref profileCapturePairTicksMax, num2);
				if (num2 >= num)
				{
					Interlocked.Exchange(ref profileCaptureState.Slot.DisabledUntilFrame, mainThreadFrames + 3600);
					Interlocked.Exchange(ref profileCaptureState.MapState.CaptureDisabledUntilFrame, mainThreadFrames + 3600);
					Interlocked.Increment(ref profileCaptureMapQuarantines);
					RecordProfileCaptureSlice(timestamp2);
					DropCurrentCapture(stale: false, watchdog: true);
					flag = true;
					break;
				}
				if ((profileCaptureState.Cursor & 3) == 0 && BudgetSpent(timestamp, budgetTicks))
				{
					RecordProfileCaptureSlice(timestamp2);
					return;
				}
			}
			if (flag)
			{
				continue;
			}
			RecordProfileCaptureSlice(timestamp2);
			if (profileCaptureState.Cursor < profileCaptureState.Topology.RegionRefs.Length)
			{
				break;
			}
			if (Interlocked.Read(ref profileCaptureState.MapState.RegionGeneration) != profileCaptureState.RegionGeneration)
			{
				DropCurrentCapture(stale: true, watchdog: false);
				continue;
			}
			JobScheduler scheduler = RimMTRuntime.Scheduler;
			if (scheduler == null)
			{
				DropCurrentCapture(stale: false, watchdog: false);
				continue;
			}
			ProfileBuildContext context = new ProfileBuildContext(profileCaptureState.Topology.MapId, profileCaptureState.Topology.Width, profileCaptureState.Topology.Height, profileCaptureState.RegionGeneration, mainThreadFrames, profileCaptureState.Key, profileCaptureState.Topology.CellRegion, profileCaptureState.Topology.DistrictByRegion, profileCaptureState.Topology.EdgeOffsets, profileCaptureState.Topology.Edges, profileCaptureState.TraverseAllowed, profileCaptureState.DestinationAllowed);
			bool flag2;
			try
			{
				MapState mapState = profileCaptureState.MapState;
				ProfileSlot slot = profileCaptureState.Slot;
				flag2 = scheduler.TryEnqueue("parallel.reachProfile", AdaptiveLoadBalancer.RecommendedOffloadPriority, delegate
				{
					BuildAndPublishProfile(mapState, slot, context);
				});
			}
			catch
			{
				flag2 = false;
			}
			PendingProfileCaptures.Dequeue();
			if (!flag2)
			{
				Volatile.Write(ref profileCaptureState.Slot.BuildScheduled, 0);
				Interlocked.Increment(ref buildsRejected);
				Interlocked.Increment(ref profileCaptureAborted);
			}
			else
			{
				Interlocked.Exchange(ref profileCaptureState.Slot.LastScheduleFrame, mainThreadFrames);
				Interlocked.Increment(ref profileCaptures);
				Interlocked.Increment(ref buildsScheduled);
			}
			if (!BudgetSpent(timestamp, budgetTicks))
			{
				continue;
			}
			break;
		}
	}

	private static void DropCurrentCapture(bool stale, bool watchdog)
	{
		if (PendingProfileCaptures.Count != 0)
		{
			ProfileCaptureState profileCaptureState = PendingProfileCaptures.Dequeue();
			if (profileCaptureState != null && profileCaptureState.Slot != null)
			{
				Volatile.Write(ref profileCaptureState.Slot.BuildScheduled, 0);
			}
			Interlocked.Increment(ref profileCaptureAborted);
			if (stale)
			{
				Interlocked.Increment(ref buildsStale);
			}
			else
			{
				Interlocked.Increment(ref buildsRejected);
			}
			if (watchdog)
			{
				Interlocked.Increment(ref profileCaptureWatchdogTrips);
			}
		}
	}

	private static void RecordProfileCaptureSlice(long started)
	{
		long num = Stopwatch.GetTimestamp() - started;
		if (num >= 0)
		{
			Interlocked.Increment(ref profileCaptureSlices);
			Interlocked.Add(ref profileCaptureTicks, num);
			UpdateMax(ref profileCaptureTicksMax, num);
		}
	}

	private static long ProfileCaptureSliceBudgetTicks()
	{
		int num = ProfileCaptureSliceBudgetMicroseconds();
		return Math.Max(1L, Stopwatch.Frequency * num / 1000000);
	}

	private static int ProfileCaptureSliceBudgetMicroseconds()
	{
		return AdaptiveLoadBalancer.Pressure switch
		{
			LoadPressure.Low => 2000, 
			LoadPressure.Normal => 1500, 
			LoadPressure.High => 1000, 
			_ => 500, 
		};
	}

	private static TopologySnapshot EnsureTopology(Map map, MapState state)
	{
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c7: Unknown result type (might be due to invalid IL or missing references)
		long num = Interlocked.Read(ref state.RegionGeneration);
		TopologySnapshot topologySnapshot = Volatile.Read(ref state.Topology);
		if (topologySnapshot != null && topologySnapshot.RegionGeneration == num && topologySnapshot.MapId == map.uniqueID && topologySnapshot.Width == map.Size.x && topologySnapshot.Height == map.Size.z)
		{
			return topologySnapshot;
		}
		int num2 = map.Size.x * map.Size.z;
		if (num2 <= 0 || num2 > 160000)
		{
			return null;
		}
		long mainThreadFrames = RimMTRuntime.MainThreadFrames;
		TopologyBuildState topologyBuildState = state.TopologyBuild;
		if (topologyBuildState == null || topologyBuildState.RegionGeneration != num || topologyBuildState.MapId != map.uniqueID || topologyBuildState.Width != map.Size.x || topologyBuildState.Height != map.Size.z)
		{
			if (topologyBuildState != null)
			{
				Interlocked.Increment(ref topologyBuildDiscarded);
			}
			topologyBuildState = StartTopologyBuild(map, num, num2);
			if (topologyBuildState == null)
			{
				Interlocked.Increment(ref topologyFailures);
				return null;
			}
			state.TopologyBuild = topologyBuildState;
			Interlocked.Increment(ref topologyBuildStarts);
		}
		if (topologyBuildState.LastSliceFrame == mainThreadFrames)
		{
			return null;
		}
		topologyBuildState.LastSliceFrame = mainThreadFrames;
		long timestamp = Stopwatch.GetTimestamp();
		TopologyAdvanceResult topologyAdvanceResult;
		try
		{
			topologyAdvanceResult = AdvanceTopologyBuild(map, state, topologyBuildState, timestamp, TopologySliceBudgetTicks());
		}
		catch (Exception exception)
		{
			state.TopologyBuild = null;
			Interlocked.Increment(ref topologyFailures);
			CircuitBreaker.RecordFailure("parallel.reachProfile", exception);
			topologyAdvanceResult = TopologyAdvanceResult.Failed;
		}
		finally
		{
			long value = Stopwatch.GetTimestamp() - timestamp;
			Interlocked.Increment(ref topologySlices);
			Interlocked.Add(ref topologySliceTicks, value);
			UpdateMax(ref topologySliceTicksMax, value);
		}
		switch (topologyAdvanceResult)
		{
		case TopologyAdvanceResult.Pending:
			return null;
		case TopologyAdvanceResult.Stale:
			state.TopologyBuild = null;
			Interlocked.Increment(ref topologyStale);
			Interlocked.Increment(ref topologyBuildDiscarded);
			return null;
		case TopologyAdvanceResult.Failed:
			state.TopologyBuild = null;
			return null;
		default:
		{
			long num3 = Interlocked.Read(ref state.RegionGeneration);
			if (num3 != topologyBuildState.RegionGeneration)
			{
				state.TopologyBuild = null;
				Interlocked.Increment(ref topologyStale);
				Interlocked.Increment(ref topologyBuildDiscarded);
				return null;
			}
			TopologySnapshot topologySnapshot2 = new TopologySnapshot(topologyBuildState.MapId, topologyBuildState.Width, topologyBuildState.Height, num3, topologyBuildState.Regions.ToArray(), topologyBuildState.CellRegion, topologyBuildState.DistrictByRegion, topologyBuildState.EdgeOffsets, topologyBuildState.Edges);
			Volatile.Write(ref state.Topology, topologySnapshot2);
			state.TopologyBuild = null;
			Interlocked.Increment(ref topologyBuilds);
			return topologySnapshot2;
		}
		}
	}

	private static TopologyBuildState StartTopologyBuild(Map map, long generation, int cells)
	{
		//IL_001e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0029: Unknown result type (might be due to invalid IL or missing references)
		Region[] directGrid = map.regionGrid.DirectGrid;
		if (directGrid == null || directGrid.Length != cells)
		{
			return null;
		}
		return new TopologyBuildState(map.uniqueID, map.Size.x, map.Size.z, generation, directGrid, cells);
	}

	private static TopologyAdvanceResult AdvanceTopologyBuild(Map map, MapState state, TopologyBuildState build, long sliceStart, long budgetTicks)
	{
		if (Interlocked.Read(ref state.RegionGeneration) != build.RegionGeneration)
		{
			return TopologyAdvanceResult.Stale;
		}
		do
		{
			switch (build.Phase)
			{
			case TopologyBuildPhase.Cells:
				while (build.CellCursor < build.Direct.Length)
				{
					int num5 = build.CellCursor++;
					Region val5 = build.Direct[num5];
					if (val5 != null && val5.valid)
					{
						if (!build.RegionIndex.TryGetValue(val5, out var value3))
						{
							value3 = build.Regions.Count;
							build.RegionIndex.Add(val5, value3);
							build.Regions.Add(val5);
						}
						build.CellRegion[num5] = value3 + 1;
					}
					if ((build.CellCursor & 0xF) == 0 && BudgetSpent(sliceStart, budgetTicks))
					{
						return TopologyAdvanceResult.Pending;
					}
				}
				build.Phase = TopologyBuildPhase.NormalizeCells;
				build.CellCursor = 0;
				break;
			case TopologyBuildPhase.NormalizeCells:
				while (build.CellCursor < build.CellRegion.Length)
				{
					build.CellRegion[build.CellCursor] = build.CellRegion[build.CellCursor] - 1;
					build.CellCursor++;
					if ((build.CellCursor & 0xF) == 0 && BudgetSpent(sliceStart, budgetTicks))
					{
						return TopologyAdvanceResult.Pending;
					}
				}
				build.DistrictByRegion = new int[build.Regions.Count];
				build.Adjacency = new List<int>[build.Regions.Count];
				build.Phase = TopologyBuildPhase.Districts;
				build.RegionCursor = 0;
				break;
			case TopologyBuildPhase.Districts:
				while (build.RegionCursor < build.Regions.Count)
				{
					int num3 = build.RegionCursor++;
					Region val4 = build.Regions[num3];
					if (val4 == null || !val4.valid)
					{
						return TopologyAdvanceResult.Stale;
					}
					District district = val4.District;
					if (district != null)
					{
						if (!build.DistrictIndex.TryGetValue(district, out var value2))
						{
							value2 = build.NextDistrict++;
							build.DistrictIndex.Add(district, value2);
						}
						build.DistrictByRegion[num3] = value2;
					}
					if ((build.RegionCursor & 0xF) == 0 && BudgetSpent(sliceStart, budgetTicks))
					{
						return TopologyAdvanceResult.Pending;
					}
				}
				build.Phase = TopologyBuildPhase.Adjacency;
				build.RegionCursor = 0;
				break;
			case TopologyBuildPhase.Adjacency:
				while (build.RegionCursor < build.Regions.Count)
				{
					int num = build.RegionCursor++;
					Region val = build.Regions[num];
					if (val == null || !val.valid)
					{
						return TopologyAdvanceResult.Stale;
					}
					List<int> list = new List<int>(4);
					build.Adjacency[num] = list;
					List<RegionLink> links = val.links;
					if (links != null)
					{
						for (int i = 0; i < links.Count; i++)
						{
							RegionLink val2 = links[i];
							if (val2 == null)
							{
								continue;
							}
							for (int j = 0; j < 2; j++)
							{
								Region val3 = val2.regions[j];
								if (val3 != null && val3 != val && val3.valid && build.RegionIndex.TryGetValue(val3, out var value) && !list.Contains(value))
								{
									list.Add(value);
								}
							}
						}
					}
					if ((build.RegionCursor & 3) == 0 && BudgetSpent(sliceStart, budgetTicks))
					{
						return TopologyAdvanceResult.Pending;
					}
				}
				build.EdgeOffsets = new int[build.Regions.Count + 1];
				build.Phase = TopologyBuildPhase.EdgeOffsets;
				build.RegionCursor = 0;
				build.EdgeCount = 0;
				break;
			case TopologyBuildPhase.EdgeOffsets:
				while (build.RegionCursor < build.Regions.Count)
				{
					int num4 = build.RegionCursor++;
					build.EdgeOffsets[num4] = build.EdgeCount;
					List<int> list3 = build.Adjacency[num4];
					if (list3 != null)
					{
						build.EdgeCount += list3.Count;
					}
					if ((build.RegionCursor & 0xF) == 0 && BudgetSpent(sliceStart, budgetTicks))
					{
						return TopologyAdvanceResult.Pending;
					}
				}
				build.EdgeOffsets[build.Regions.Count] = build.EdgeCount;
				build.Edges = new int[build.EdgeCount];
				build.Phase = TopologyBuildPhase.FlattenEdges;
				build.FlattenRegion = 0;
				build.FlattenLocal = 0;
				build.EdgeWrite = 0;
				break;
			case TopologyBuildPhase.FlattenEdges:
				while (build.FlattenRegion < build.Regions.Count)
				{
					List<int> list2 = build.Adjacency[build.FlattenRegion];
					int num2 = list2?.Count ?? 0;
					while (build.FlattenLocal < num2)
					{
						build.Edges[build.EdgeWrite++] = list2[build.FlattenLocal++];
						if ((build.EdgeWrite & 0xF) == 0 && BudgetSpent(sliceStart, budgetTicks))
						{
							return TopologyAdvanceResult.Pending;
						}
					}
					build.FlattenRegion++;
					build.FlattenLocal = 0;
				}
				build.Phase = TopologyBuildPhase.Complete;
				break;
			default:
				if (Interlocked.Read(ref state.RegionGeneration) != build.RegionGeneration)
				{
					return TopologyAdvanceResult.Stale;
				}
				return TopologyAdvanceResult.Complete;
			}
			if (Interlocked.Read(ref state.RegionGeneration) != build.RegionGeneration)
			{
				return TopologyAdvanceResult.Stale;
			}
		}
		while (!BudgetSpent(sliceStart, budgetTicks));
		if (build.Phase != TopologyBuildPhase.Complete)
		{
			return TopologyAdvanceResult.Pending;
		}
		return TopologyAdvanceResult.Complete;
	}

	private static bool BudgetSpent(long sliceStart, long budgetTicks)
	{
		return Stopwatch.GetTimestamp() - sliceStart >= budgetTicks;
	}

	private static long TopologySliceBudgetTicks()
	{
		int num = AdaptiveLoadBalancer.Pressure switch
		{
			LoadPressure.Low => 2000, 
			LoadPressure.Normal => 1500, 
			LoadPressure.High => 1000, 
			_ => 500, 
		};
		return Math.Max(1L, Stopwatch.Frequency * num / 1000000);
	}

	private static int TopologySliceBudgetMicroseconds()
	{
		return AdaptiveLoadBalancer.Pressure switch
		{
			LoadPressure.Low => 2000, 
			LoadPressure.Normal => 1500, 
			LoadPressure.High => 1000, 
			_ => 500, 
		};
	}

	private static void BuildAndPublishProfile(MapState mapState, ProfileSlot slot, ProfileBuildContext context)
	{
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			if (Interlocked.Read(ref mapState.RegionGeneration) != context.RegionGeneration)
			{
				Interlocked.Increment(ref buildsStale);
				return;
			}
			int num = context.TraverseAllowed.Length;
			int[] array = new int[num];
			int[] array2 = new int[Math.Max(1, num)];
			int num2 = 0;
			for (int i = 0; i < num; i++)
			{
				if (!context.TraverseAllowed[i] || array[i] != 0)
				{
					continue;
				}
				num2++;
				int num3 = 0;
				int num4 = 0;
				array2[num4++] = i;
				array[i] = num2;
				while (num3 < num4)
				{
					int num5 = array2[num3++];
					int num6 = context.EdgeOffsets[num5];
					int num7 = context.EdgeOffsets[num5 + 1];
					for (int j = num6; j < num7; j++)
					{
						int num8 = context.Edges[j];
						if (num8 >= 0 && num8 < num && array[num8] == 0 && context.TraverseAllowed[num8])
						{
							array[num8] = num2;
							array2[num4++] = num8;
						}
					}
				}
			}
			if (Interlocked.Read(ref mapState.RegionGeneration) != context.RegionGeneration)
			{
				Interlocked.Increment(ref buildsStale);
				return;
			}
			ProfileSnapshot value = new ProfileSnapshot(context.MapId, context.Width, context.Height, context.RegionGeneration, context.CaptureFrame, context.Key, context.CellRegion, context.DistrictByRegion, context.EdgeOffsets, context.Edges, array, context.DestinationAllowed);
			Interlocked.Exchange(ref slot.ValidatedMatches, 0);
			Interlocked.Exchange(ref slot.PredictionSerial, 0);
			Interlocked.Exchange(ref slot.LeaseProbeMatches, 0);
			Interlocked.Exchange(ref slot.LeaseUntilFrame, 0L);
			Volatile.Write(ref slot.Published, value);
			Interlocked.Increment(ref buildsPublished);
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref workerFailures);
			CircuitBreaker.RecordFailure("parallel.reachProfile", exception);
		}
		finally
		{
			RecordElapsed(ref workerBuildTicks, ref workerBuildTicksMax, timestamp);
			Volatile.Write(ref slot.BuildScheduled, 0);
		}
	}

	private static void PatchRegionDirtySignals(Harmony harmony)
	{
		TryPatchDirtySignal(harmony, "Notify_WalkabilityChanged", new Type[2]
		{
			typeof(IntVec3),
			typeof(bool)
		});
		TryPatchDirtySignal(harmony, "Notify_ThingAffectingRegionsSpawned", new Type[1] { typeof(Thing) });
		TryPatchDirtySignal(harmony, "Notify_ThingAffectingRegionsDespawned", new Type[1] { typeof(Thing) });
		TryPatchDirtySignal(harmony, "SetAllDirty", Type.EmptyTypes);
	}

	private static void TryPatchDirtySignal(Harmony harmony, string name, Type[] args)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0034: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(RegionDirtyer), name, args, (Type[])null);
			if (!(methodBase == null))
			{
				HarmonyMethod val = new HarmonyMethod(typeof(AggressiveReachabilityProfilesV17), "RegionDirtyPostfix", (Type[])null);
				val.priority = 0;
				harmony.Patch(methodBase, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null);
			}
		}
		catch
		{
		}
	}

	public static void RegionDirtyPostfix(Map ___map)
	{
		if (___map != null)
		{
			MapState value = MapStates.GetValue(___map, (Map m) => new MapState(m.uniqueID, m.Size.x, m.Size.z));
			Interlocked.Increment(ref value.RegionGeneration);
			Volatile.Write(ref value.Topology, null);
			if (value.TopologyBuild != null)
			{
				value.TopologyBuild = null;
				Interlocked.Increment(ref topologyBuildDiscarded);
			}
			Interlocked.Increment(ref regionDirtyEvents);
		}
	}

	private static long RecordElapsed(ref long total, ref long max, long started)
	{
		long num = Stopwatch.GetTimestamp() - started;
		Interlocked.Add(ref total, num);
		UpdateMax(ref max, num);
		return num;
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref topologyBuilds);
		long num2 = Interlocked.Read(ref topologySlices);
		long num3 = Interlocked.Read(ref profileCaptures);
		long num4 = Interlocked.Read(ref buildsPublished);
		long num5 = Interlocked.Read(ref profileHits);
		double num6 = (double)Interlocked.Read(ref topologySliceTicks) * 1000.0 / (double)Stopwatch.Frequency;
		double num7 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref topologySliceTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		double num8 = (double)Interlocked.Read(ref topologySliceTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num9 = ((num3 == 0L) ? 0.0 : ((double)Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num3));
		double num10 = (double)Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num11 = ((num4 == 0L) ? 0.0 : ((double)Interlocked.Read(ref workerBuildTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num4));
		double num12 = (double)Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num13 = ((num5 == 0L) ? 0.0 : ((double)Interlocked.Read(ref queryTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num5));
		double num14 = (double)Interlocked.Read(ref queryTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		return "Aggressive reachability profile V0.4.18-T1A sliced/local-first/lease/no-global-fuse: compatibilityReady=" + compatibilityReady + ", observed=" + Interlocked.Read(ref observed) + ", admissionBypass[compat/gate/thread/state]=" + Interlocked.Read(ref admissionCompatibilityBypass) + "/" + Interlocked.Read(ref admissionFeatureGateBypass) + "/" + Interlocked.Read(ref admissionThreadBypass) + "/" + Interlocked.Read(ref admissionProgramStateBypass) + ", eligible=" + Interlocked.Read(ref eligible) + ", priorPrefixOwned=" + Interlocked.Read(ref priorPrefixOwned) + ", immediateHits=" + Interlocked.Read(ref immediateHits) + ", unsupported=" + Interlocked.Read(ref unsupported) + ", profileHits=" + num5 + ", profileMisses=" + Interlocked.Read(ref profileMisses) + ", profileExpired=" + Interlocked.Read(ref profileExpired) + ", cooldownBypass=" + Interlocked.Read(ref profileCooldownBypass) + ", topologyBuildStarts=" + Interlocked.Read(ref topologyBuildStarts) + ", topologyBuilds=" + num + ", topologyDiscarded=" + Interlocked.Read(ref topologyBuildDiscarded) + ", topologyStale=" + Interlocked.Read(ref topologyStale) + ", topologyFailures=" + Interlocked.Read(ref topologyFailures) + ", topologySlices=" + num2 + ", topologyBudgetUs=" + TopologySliceBudgetMicroseconds() + ", topologyWorkMs=" + num6.ToString("F2") + ", avgTopologySliceUs=" + num7.ToString("F2") + ", maxTopologySliceUs=" + num8.ToString("F2") + ", regionDirtyEvents=" + Interlocked.Read(ref regionDirtyEvents) + ", profileCaptures=" + num3 + ", captureQueued=" + Interlocked.Read(ref profileCaptureQueued) + ", captureSlices=" + Interlocked.Read(ref profileCaptureSlices) + ", captureAborted=" + Interlocked.Read(ref profileCaptureAborted) + ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) + ", capturePressureBypass=" + Interlocked.Read(ref profileCapturePressureBypass) + ", captureMapQuarantines=" + Interlocked.Read(ref profileCaptureMapQuarantines) + ", capturePairs=" + Interlocked.Read(ref profileCapturePairs) + ", maxCapturePairUs=" + ((double)Interlocked.Read(ref profileCapturePairTicksMax) * 1000000.0 / (double)Stopwatch.Frequency).ToString("F2") + ", capturePending=" + PendingProfileCaptures.Count + ", captureBudgetUs=" + ProfileCaptureSliceBudgetMicroseconds() + ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) + ", buildsPublished=" + num4 + ", buildsRejected=" + Interlocked.Read(ref buildsRejected) + ", buildsStale=" + Interlocked.Read(ref buildsStale) + ", workerFailures=" + Interlocked.Read(ref workerFailures) + ", predictedReachable=" + Interlocked.Read(ref predictedReachable) + ", predictedUnreachable=" + Interlocked.Read(ref predictedUnreachable) + ", authoritativeTrue=" + Interlocked.Read(ref authoritativeTrue) + ", authoritativeFalse=" + Interlocked.Read(ref authoritativeFalse) + ", shadowSamples=" + Interlocked.Read(ref shadowSamples) + ", shadowMatches=" + Interlocked.Read(ref shadowMatches) + ", parityMismatches=" + Interlocked.Read(ref parityMismatches) + " (predTrue/liveFalse=" + Interlocked.Read(ref mismatchReachableToFalse) + ", predFalse/liveTrue=" + Interlocked.Read(ref mismatchUnreachableToTrue) + "), localSlotQuarantines=" + Interlocked.Read(ref localSlotQuarantines) + ", leaseProbeCalls=" + Interlocked.Read(ref leaseProbeCalls) + ", leaseProbeMatches=" + Interlocked.Read(ref leaseProbeMatches) + ", leaseRenewals=" + Interlocked.Read(ref leaseRenewals) + ", leaseFailures=" + Interlocked.Read(ref leaseFailures) + ", forcedRefreshes=" + Interlocked.Read(ref forcedRefreshes) + ", globalFuse=OFF, localSlotPolicy=quarantine-only, unknown=" + Interlocked.Read(ref queriesUnknown) + ", warmupSamples=" + 8 + ", sampleEvery=" + 128 + ", maxProfileAgeFrames=" + 180L + ", leaseExtensionFrames=" + 180L + ", hardMaxProfileAgeFrames=" + 720L + ", leaseProbeRequired=" + 4 + ", avgProfileCaptureWorkUs=" + num9.ToString("F2") + ", maxProfileCaptureSliceUs=" + num10.ToString("F2") + ", avgWorkerBuildUs=" + num11.ToString("F2") + ", maxWorkerBuildUs=" + num12.ToString("F2") + ", avgQueryUs=" + num13.ToString("F2") + ", maxQueryUs=" + num14.ToString("F2") + ". Topology and Region.Allows profile capture are frame-budgeted on the main thread; workers consume primitive immutable arrays only. Vanilla remains authoritative during incomplete slices, local quarantine, global cooldown, misses and shadow validation.";
	}
}
