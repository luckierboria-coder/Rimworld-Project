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

internal static class AggressiveReachabilityProfiles
{
	internal struct ReachSampleState
	{
		internal readonly bool Active;

		internal readonly bool Predicted;

		internal readonly ProfileSlot Slot;

		internal readonly long RegionGeneration;

		internal ReachSampleState(bool active, bool predicted, ProfileSlot slot, long generation)
		{
			Active = active;
			Predicted = predicted;
			Slot = slot;
			RegionGeneration = generation;
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

	private sealed class MapState
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly ConditionalWeakTable<Pawn, PawnState> Pawns = new ConditionalWeakTable<Pawn, PawnState>();

		internal long RegionGeneration = 1L;

		internal TopologySnapshot Topology;

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
			//IL_0194: Unknown result type (might be due to invalid IL or missing references)
			//IL_0159: Unknown result type (might be due to invalid IL or missing references)
			//IL_015e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0163: Unknown result type (might be due to invalid IL or missing references)
			//IL_0168: Unknown result type (might be due to invalid IL or missing references)
			//IL_022b: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a2: Unknown result type (might be due to invalid IL or missing references)
			//IL_021a: Unknown result type (might be due to invalid IL or missing references)
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
			bool flag = false;
			for (int k = val2.minZ; k <= val2.maxZ; k++)
			{
				for (int l = val2.minX; l <= val2.maxX; l++)
				{
					if (l < 0 || k < 0 || l >= Width || k >= Height)
					{
						continue;
					}
					int num10 = cellRegion[l + k * Width];
					if (num10 >= 0 && num10 < destinationAllowed.Length && destinationAllowed[num10])
					{
						flag = true;
						if (RegionReachability(num10, array, num5, array2, count) == Prediction.Reachable)
						{
							return Prediction.Reachable;
						}
					}
				}
			}
			if (!flag)
			{
				return Prediction.Unreachable;
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

	private const long BuildCooldownFrames = 12L;

	private const long MismatchCooldownFrames = 600L;

	private const int WarmupSamples = 8;

	private const int SampleMask = 127;

	private const int GlobalWindowSamples = 8192;

	private const int GlobalMismatchLimit = 8;

	private const long GlobalCooldownFrames = 3600L;

	private const int ProbationSamples = 256;

	private const int EmergencyWindowSamples = 256;

	private const int EmergencyMismatchLimit = 16;

	private static readonly ConditionalWeakTable<Map, MapState> MapStates = new ConditionalWeakTable<Map, MapState>();

	private static readonly bool[] GlobalMismatchWindow = new bool[8192];

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

	private static long topologyBuilds;

	private static long topologyStale;

	private static long topologyFailures;

	private static long topologyCaptureTicks;

	private static long topologyCaptureTicksMax;

	private static long profileCaptures;

	private static long profileCaptureTicks;

	private static long profileCaptureTicksMax;

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
		//IL_009c: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Expected O, but got Unknown
		//IL_00bd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		PatchRegionDirtySignals(harmony);
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
				Log.Warning("[RimMT] parallel.reachProfile V0.4.16 unavailable: Reachability.CanReach target not found.");
				return;
			}
			CompatibilityGuard.RegisterTarget("parallel.reachProfile", methodBase);
			HarmonyMethod val = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), "Prefix", (Type[])null);
			val.priority = 200;
			HarmonyMethod val2 = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), "Postfix", (Type[])null);
			val2.priority = 800;
			harmony.Patch(methodBase, val, val2, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] parallel.reachProfile V0.4.16 + JS1.1R rolling fuse installed. Profile prediction/build semantics are unchanged; the old lifetime-16 fuse is replaced in-place by rolling 8192/8 soft fuse, 3600-frame live cooldown, 256-clean-shadow probation, and 16/256 emergency hard fuse. No additional Reachability Harmony wrapper is installed.");
		}
		catch (Exception ex)
		{
			FeatureGate.Suppress("parallel.reachProfile", "reachability profile patch failed: " + ex.GetType().Name);
			Log.Warning("[RimMT] parallel.reachProfile V0.4.16 patch failed; Vanilla Reachability remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static void MarkCompatibilityReady()
	{
		compatibilityReady = true;
	}

	public static bool Prefix(IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, Map ___map, bool __runOriginal, ref bool __result, out ReachSampleState __state)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0046: Invalid comparison between Unknown and I4
		//IL_0070: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b6: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d0: Unknown result type (might be due to invalid IL or missing references)
		//IL_00d1: Unknown result type (might be due to invalid IL or missing references)
		//IL_00dd: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f4: Unknown result type (might be due to invalid IL or missing references)
		//IL_00f6: Unknown result type (might be due to invalid IL or missing references)
		//IL_015e: Unknown result type (might be due to invalid IL or missing references)
		//IL_01bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_0255: Unknown result type (might be due to invalid IL or missing references)
		//IL_0212: Unknown result type (might be due to invalid IL or missing references)
		//IL_0226: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a5: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a6: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a7: Unknown result type (might be due to invalid IL or missing references)
		//IL_02a9: Unknown result type (might be due to invalid IL or missing references)
		//IL_0287: Unknown result type (might be due to invalid IL or missing references)
		__state = default(ReachSampleState);
		Interlocked.Increment(ref observed);
		if (!__runOriginal)
		{
			Interlocked.Increment(ref priorPrefixOwned);
			return true;
		}
		if (!compatibilityReady || !FeatureGate.IsEnabled("parallel.reachProfile") || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return true;
		}
		UpdateRollingFuseMode();
		if (reachFuseMode == ReachFuseMode.Cooldown)
		{
			cooldownLiveBypass++;
			return true;
		}
		if (reachFuseMode == ReachFuseMode.HardFused)
		{
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
			if (Interlocked.Read(ref orCreate.DisabledUntilFrame) > mainThreadFrames)
			{
				Interlocked.Increment(ref profileCooldownBypass);
				EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				return true;
			}
			ProfileSnapshot profileSnapshot = Volatile.Read(ref orCreate.Published);
			long num = Interlocked.Read(ref value.RegionGeneration);
			if (profileSnapshot == null || profileSnapshot.RegionGeneration != num || profileSnapshot.MapId != ___map.uniqueID || profileSnapshot.Width != ___map.Size.x || profileSnapshot.Height != ___map.Size.z || !profileSnapshot.Key.Equals(traverseKey))
			{
				Interlocked.Increment(ref profileMisses);
				EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				return true;
			}
			if (mainThreadFrames - profileSnapshot.CaptureFrame > 180)
			{
				Interlocked.Increment(ref profileExpired);
				EnsureProfileScheduled(___map, value, pawn, traverseParams, traverseKey, orCreate);
				return true;
			}
			Interlocked.Increment(ref profileHits);
			Prediction prediction = profileSnapshot.Classify(start, dest, peMode, ___map, traverseParams);
			if (prediction == Prediction.Unknown)
			{
				Interlocked.Increment(ref queriesUnknown);
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
			int num2 = Volatile.Read(ref orCreate.ValidatedMatches);
			int num3 = Interlocked.Increment(ref orCreate.PredictionSerial);
			bool flag2 = reachFuseMode == ReachFuseMode.Probation;
			if (flag2 || num2 < 8 || (num3 & 0x7F) == 0)
			{
				__state = new ReachSampleState(active: true, flag, orCreate, profileSnapshot.RegionGeneration);
				Interlocked.Increment(ref shadowSamples);
				if (flag2)
				{
					probationForcedShadow++;
				}
				return true;
			}
			__result = flag;
			if (flag)
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
			Log.Warning("[RimMT] parallel.reachProfile V0.4.16 query failure; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
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
		if (__result == __state.Predicted)
		{
			Interlocked.Increment(ref shadowMatches);
			Interlocked.Increment(ref __state.Slot.ValidatedMatches);
			ObserveRollingSample(mismatch: false);
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
		Interlocked.Exchange(ref __state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + 600);
		Volatile.Write(ref __state.Slot.Published, null);
		ObserveRollingSample(mismatch: true);
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
			Log.Message("[RimMT] ReachProfile JS1.1R soft cooldown ended; entering probation. All profile predictions are forced through live Vanilla until 256 clean shadow samples complete.");
		}
	}

	private static void ObserveRollingSample(bool mismatch)
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
		PushWindow(GlobalMismatchWindow, ref globalWindowPos, ref globalWindowCount, ref globalWindowMismatches, mismatch);
		PushWindow(EmergencyMismatchWindow, ref emergencyWindowPos, ref emergencyWindowCount, ref emergencyWindowMismatches, mismatch);
		if (emergencyWindowMismatches >= 16)
		{
			reachFuseMode = ReachFuseMode.HardFused;
			hardFuses++;
			FeatureGate.Suppress("parallel.reachProfile", "JS1.1R emergency ReachProfile hard fuse: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " sampled mismatches");
			Log.Warning("[RimMT] ReachProfile HARD FUSE JS1.1R: " + emergencyWindowMismatches + "/" + emergencyWindowCount + " mismatches in the recent emergency sample window. Vanilla Reachability is authoritative for the rest of this run.");
		}
		else if (reachFuseMode == ReachFuseMode.Probation)
		{
			if (mismatch)
			{
				probationFailures++;
				EnterCooldown("probation mismatch");
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
				Log.Message("[RimMT] ReachProfile JS1.1R probation passed 256 clean live-shadow samples; profile authority restored.");
			}
		}
		else if (reachFuseMode == ReachFuseMode.Normal && globalWindowMismatches >= 8)
		{
			EnterCooldown("rolling mismatch density " + globalWindowMismatches + "/" + globalWindowCount);
		}
	}

	private static void EnterCooldown(string reason)
	{
		reachFuseMode = ReachFuseMode.Cooldown;
		cooldownUntilFrame = RimMTRuntime.MainThreadFrames + 3600;
		probationRemaining = 0;
		probationMatches = 0;
		softFuses++;
		ClearGlobalWindow();
		ClearEmergencyWindow();
		Log.Warning("[RimMT] ReachProfile SOFT FUSE JS1.1R: " + reason + ". Profile authority is bypassed for 3600 main-thread frames; then 256 clean forced-shadow samples are required before authority returns.");
	}

	private static void PushWindow(bool[] window, ref int pos, ref int count, ref int mismatchCount, bool mismatch)
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
		//IL_0139: Unknown result type (might be due to invalid IL or missing references)
		//IL_0147: Unknown result type (might be due to invalid IL or missing references)
		if (map == null || map.Disposed || pawn == null || !((Thing)pawn).Spawned || ((Thing)pawn).Map != map || !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled("parallel.reachProfile"))
		{
			return;
		}
		long mainThreadFrames = RimMTRuntime.MainThreadFrames;
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
			return;
		}
		long timestamp = Stopwatch.GetTimestamp();
		bool[] array = new bool[topologySnapshot.RegionRefs.Length];
		bool[] array2 = new bool[topologySnapshot.RegionRefs.Length];
		try
		{
			for (int i = 0; i < topologySnapshot.RegionRefs.Length; i++)
			{
				Region val = topologySnapshot.RegionRefs[i];
				if (val == null || !val.valid)
				{
					Volatile.Write(ref slot.BuildScheduled, 0);
					Interlocked.Increment(ref buildsStale);
					return;
				}
				array[i] = val.Allows(traverseParams, false);
				array2[i] = val.Allows(traverseParams, true);
			}
		}
		catch
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsRejected);
			return;
		}
		finally
		{
			long value = Stopwatch.GetTimestamp() - timestamp;
			Interlocked.Increment(ref profileCaptures);
			Interlocked.Add(ref profileCaptureTicks, value);
			UpdateMax(ref profileCaptureTicksMax, value);
		}
		if (Interlocked.Read(ref mapState.RegionGeneration) != num2)
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsStale);
			return;
		}
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler == null)
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsRejected);
			return;
		}
		ProfileBuildContext context = new ProfileBuildContext(topologySnapshot.MapId, topologySnapshot.Width, topologySnapshot.Height, topologySnapshot.RegionGeneration, mainThreadFrames, key, topologySnapshot.CellRegion, topologySnapshot.DistrictByRegion, topologySnapshot.EdgeOffsets, topologySnapshot.Edges, array, array2);
		if (!scheduler.TryEnqueue("parallel.reachProfile", JobPriority.Normal, delegate
		{
			BuildAndPublishProfile(mapState, slot, context);
		}))
		{
			Volatile.Write(ref slot.BuildScheduled, 0);
			Interlocked.Increment(ref buildsRejected);
		}
		else
		{
			Interlocked.Exchange(ref slot.LastScheduleFrame, mainThreadFrames);
			Interlocked.Increment(ref buildsScheduled);
		}
	}

	private static TopologySnapshot EnsureTopology(Map map, MapState state)
	{
		//IL_005b: Unknown result type (might be due to invalid IL or missing references)
		//IL_0066: Unknown result type (might be due to invalid IL or missing references)
		//IL_0039: Unknown result type (might be due to invalid IL or missing references)
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0348: Unknown result type (might be due to invalid IL or missing references)
		//IL_0353: Unknown result type (might be due to invalid IL or missing references)
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
		long timestamp = Stopwatch.GetTimestamp();
		try
		{
			long num3 = Interlocked.Read(ref state.RegionGeneration);
			Region[] directGrid = map.regionGrid.DirectGrid;
			if (directGrid == null || directGrid.Length != num2)
			{
				return null;
			}
			Dictionary<Region, int> dictionary = new Dictionary<Region, int>(ReferenceEqualityComparer<Region>.Instance);
			List<Region> list = new List<Region>();
			int[] array = new int[num2];
			for (int i = 0; i < array.Length; i++)
			{
				array[i] = -1;
			}
			for (int j = 0; j < directGrid.Length; j++)
			{
				Region val = directGrid[j];
				if (val != null && val.valid)
				{
					if (!dictionary.TryGetValue(val, out var value))
					{
						value = list.Count;
						dictionary.Add(val, value);
						list.Add(val);
					}
					array[j] = value;
				}
			}
			Dictionary<District, int> dictionary2 = new Dictionary<District, int>(ReferenceEqualityComparer<District>.Instance);
			int[] array2 = new int[list.Count];
			int num4 = 1;
			for (int k = 0; k < list.Count; k++)
			{
				District district = list[k].District;
				if (district != null)
				{
					if (!dictionary2.TryGetValue(district, out var value2))
					{
						value2 = num4++;
						dictionary2.Add(district, value2);
					}
					array2[k] = value2;
				}
			}
			List<int>[] array3 = new List<int>[list.Count];
			for (int l = 0; l < array3.Length; l++)
			{
				array3[l] = new List<int>(4);
			}
			for (int m = 0; m < list.Count; m++)
			{
				Region val2 = list[m];
				List<RegionLink> links = val2.links;
				for (int n = 0; n < links.Count; n++)
				{
					RegionLink val3 = links[n];
					if (val3 == null)
					{
						continue;
					}
					for (int num5 = 0; num5 < 2; num5++)
					{
						Region val4 = val3.regions[num5];
						if (val4 != null && val4 != val2 && val4.valid && dictionary.TryGetValue(val4, out var value3) && !array3[m].Contains(value3))
						{
							array3[m].Add(value3);
						}
					}
				}
			}
			int[] array4 = new int[list.Count + 1];
			int num6 = 0;
			for (int num7 = 0; num7 < array3.Length; num7++)
			{
				array4[num7] = num6;
				num6 += array3[num7].Count;
			}
			array4[list.Count] = num6;
			int[] array5 = new int[num6];
			int num8 = 0;
			for (int num9 = 0; num9 < array3.Length; num9++)
			{
				for (int num10 = 0; num10 < array3[num9].Count; num10++)
				{
					array5[num8++] = array3[num9][num10];
				}
			}
			long num11 = Interlocked.Read(ref state.RegionGeneration);
			if (num11 != num3)
			{
				Interlocked.Increment(ref topologyStale);
				return null;
			}
			TopologySnapshot topologySnapshot2 = new TopologySnapshot(map.uniqueID, map.Size.x, map.Size.z, num11, list.ToArray(), array, array2, array4, array5);
			Volatile.Write(ref state.Topology, topologySnapshot2);
			Interlocked.Increment(ref topologyBuilds);
			return topologySnapshot2;
		}
		catch (Exception exception)
		{
			Interlocked.Increment(ref topologyFailures);
			CircuitBreaker.RecordFailure("parallel.reachProfile", exception);
			return null;
		}
		finally
		{
			RecordElapsed(ref topologyCaptureTicks, ref topologyCaptureTicksMax, timestamp);
		}
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
				HarmonyMethod val = new HarmonyMethod(typeof(AggressiveReachabilityProfiles), "RegionDirtyPostfix", (Type[])null);
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
			Interlocked.Increment(ref regionDirtyEvents);
		}
	}

	private static void RecordElapsed(ref long total, ref long max, long started)
	{
		long value = Stopwatch.GetTimestamp() - started;
		Interlocked.Add(ref total, value);
		UpdateMax(ref max, value);
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
		long num2 = Interlocked.Read(ref profileCaptures);
		long num3 = Interlocked.Read(ref buildsPublished);
		long num4 = Interlocked.Read(ref profileHits);
		double num5 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref topologyCaptureTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num6 = (double)Interlocked.Read(ref topologyCaptureTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num7 = ((num2 == 0L) ? 0.0 : ((double)Interlocked.Read(ref profileCaptureTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num2));
		double num8 = (double)Interlocked.Read(ref profileCaptureTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num9 = ((num3 == 0L) ? 0.0 : ((double)Interlocked.Read(ref workerBuildTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num3));
		double num10 = (double)Interlocked.Read(ref workerBuildTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		double num11 = ((num4 == 0L) ? 0.0 : ((double)Interlocked.Read(ref queryTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num4));
		double num12 = (double)Interlocked.Read(ref queryTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		return "Aggressive reachability profile V0.4.16 + JS1.1R: compatibilityReady=" + compatibilityReady + ", observed=" + Interlocked.Read(ref observed) + ", eligible=" + Interlocked.Read(ref eligible) + ", priorPrefixOwned=" + Interlocked.Read(ref priorPrefixOwned) + ", immediateHits=" + Interlocked.Read(ref immediateHits) + ", unsupported=" + Interlocked.Read(ref unsupported) + ", profileHits=" + num4 + ", profileMisses=" + Interlocked.Read(ref profileMisses) + ", profileExpired=" + Interlocked.Read(ref profileExpired) + ", cooldownBypass=" + Interlocked.Read(ref profileCooldownBypass) + ", topologyBuilds=" + num + ", topologyStale=" + Interlocked.Read(ref topologyStale) + ", topologyFailures=" + Interlocked.Read(ref topologyFailures) + ", regionDirtyEvents=" + Interlocked.Read(ref regionDirtyEvents) + ", profileCaptures=" + num2 + ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) + ", buildsPublished=" + num3 + ", buildsRejected=" + Interlocked.Read(ref buildsRejected) + ", buildsStale=" + Interlocked.Read(ref buildsStale) + ", workerFailures=" + Interlocked.Read(ref workerFailures) + ", predictedReachable=" + Interlocked.Read(ref predictedReachable) + ", predictedUnreachable=" + Interlocked.Read(ref predictedUnreachable) + ", authoritativeTrue=" + Interlocked.Read(ref authoritativeTrue) + ", authoritativeFalse=" + Interlocked.Read(ref authoritativeFalse) + ", shadowSamples=" + Interlocked.Read(ref shadowSamples) + ", shadowMatches=" + Interlocked.Read(ref shadowMatches) + ", parityMismatches=" + Interlocked.Read(ref parityMismatches) + " (predTrue/liveFalse=" + Interlocked.Read(ref mismatchReachableToFalse) + ", predFalse/liveTrue=" + Interlocked.Read(ref mismatchUnreachableToTrue) + "), rollingMode=" + reachFuseMode.ToString() + ", rollingSamples=" + rollingSamples + ", rollingMismatches=" + rollingMismatches + ", globalWindow=" + globalWindowMismatches + "/" + globalWindowCount + ", emergencyWindow=" + emergencyWindowMismatches + "/" + emergencyWindowCount + ", softFuses=" + softFuses + ", cooldownUntilFrame=" + cooldownUntilFrame + ", cooldownLiveBypass=" + cooldownLiveBypass + ", probationRemaining=" + probationRemaining + ", probationMatches=" + probationMatches + ", probationForcedShadow=" + probationForcedShadow + ", probationPasses=" + probationPasses + ", probationFailures=" + probationFailures + ", hardFuses=" + hardFuses + ", unknown=" + Interlocked.Read(ref queriesUnknown) + ", warmupSamples=" + 8 + ", sampleEvery=" + 128 + ", maxProfileAgeFrames=" + 180L + ", avgTopologyCaptureUs=" + num5.ToString("F2") + ", maxTopologyCaptureUs=" + num6.ToString("F2") + ", avgProfileCaptureUs=" + num7.ToString("F2") + ", maxProfileCaptureUs=" + num8.ToString("F2") + ", avgWorkerBuildUs=" + num9.ToString("F2") + ", maxWorkerBuildUs=" + num10.ToString("F2") + ", avgQueryUs=" + num11.ToString("F2") + ", maxQueryUs=" + num12.ToString("F2") + ". Profile prediction/build semantics remain JS1.1; rolling fuse is embedded in the existing Reachability Prefix/Postfix with no extra hot-path Harmony wrapper.";
	}
}
