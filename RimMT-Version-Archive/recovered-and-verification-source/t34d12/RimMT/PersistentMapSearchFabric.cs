using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class PersistentMapSearchFabric
{
	private sealed class MapState
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly HashSet<Thing> TrackedThings = new HashSet<Thing>(ThingReferenceComparer.Instance);

		internal readonly HashSet<int> KnownSources = new HashSet<int>();

		internal readonly ConcurrentQueue<FabricEvent> Events = new ConcurrentQueue<FabricEvent>();

		internal readonly WorkerModel Model;

		internal long MainGeneration;

		internal int WorkerScheduled;

		internal int FlushQueuedT34B;

		internal MapFabricSnapshot Published;

		internal MapState(int mapId, int width, int height)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			Model = new WorkerModel(mapId, width, height);
		}
	}

	private enum EventKind
	{
		Upsert,
		Remove,
		Source
	}

	private sealed class FabricEvent
	{
		internal EventKind Kind;

		internal long Generation;

		internal Thing Thing;

		internal int X;

		internal int Z;

		internal int SourceId;

		internal Thing[] Members;

		internal int[] Xs;

		internal int[] Zs;

		internal static FabricEvent Upsert(long generation, Thing thing, int x, int z)
		{
			return new FabricEvent
			{
				Kind = EventKind.Upsert,
				Generation = generation,
				Thing = thing,
				X = x,
				Z = z
			};
		}

		internal static FabricEvent Remove(long generation, Thing thing)
		{
			return new FabricEvent
			{
				Kind = EventKind.Remove,
				Generation = generation,
				Thing = thing
			};
		}

		internal static FabricEvent Source(long generation, int sourceId, Thing[] members, int[] xs, int[] zs)
		{
			return new FabricEvent
			{
				Kind = EventKind.Source,
				Generation = generation,
				SourceId = sourceId,
				Members = members,
				Xs = xs,
				Zs = zs
			};
		}
	}

	private struct PositionEntry
	{
		internal int X;

		internal int Z;

		internal PositionEntry(int x, int z)
		{
			X = x;
			Z = z;
		}
	}

	private sealed class SourceModel
	{
		internal Thing[] Members;

		internal SourceModel(Thing[] members)
		{
			Members = members;
		}
	}

	private sealed class WorkerModel
	{
		private readonly int mapId;

		private readonly int width;

		private readonly int height;

		private readonly Dictionary<Thing, PositionEntry> positions = new Dictionary<Thing, PositionEntry>(ThingReferenceComparer.Instance);

		private readonly Dictionary<int, SourceModel> sources = new Dictionary<int, SourceModel>();

		private readonly Dictionary<Thing, HashSet<int>> sourceIdsByThing = new Dictionary<Thing, HashSet<int>>(ThingReferenceComparer.Instance);

		private readonly HashSet<int> dirtySources = new HashSet<int>();

		private Dictionary<int, SourceSnapshot> publishedSources = new Dictionary<int, SourceSnapshot>();

		private long appliedGeneration;

		internal WorkerModel(int mapId, int width, int height)
		{
			this.mapId = mapId;
			this.width = width;
			this.height = height;
		}

		internal void Apply(FabricEvent ev)
		{
			if (ev == null)
			{
				return;
			}
			appliedGeneration = ev.Generation;
			switch (ev.Kind)
			{
			case EventKind.Upsert:
				if (ev.Thing != null)
				{
					positions[ev.Thing] = new PositionEntry(ev.X, ev.Z);
					MarkThingSourcesDirty(ev.Thing);
				}
				break;
			case EventKind.Remove:
				if (ev.Thing != null)
				{
					positions.Remove(ev.Thing);
					MarkThingSourcesDirty(ev.Thing);
				}
				break;
			case EventKind.Source:
			{
				if (ev.Members == null)
				{
					break;
				}
				if (sources.TryGetValue(ev.SourceId, out var value) && value != null && value.Members != null)
				{
					for (int i = 0; i < value.Members.Length; i++)
					{
						RemoveMembership(value.Members[i], ev.SourceId);
					}
				}
				for (int j = 0; j < ev.Members.Length; j++)
				{
					Thing val = ev.Members[j];
					if (val != null)
					{
						positions[val] = new PositionEntry(ev.Xs[j], ev.Zs[j]);
						AddMembership(val, ev.SourceId);
					}
				}
				sources[ev.SourceId] = new SourceModel(ev.Members);
				dirtySources.Add(ev.SourceId);
				break;
			}
			}
		}

		private void AddMembership(Thing thing, int sourceId)
		{
			if (thing != null)
			{
				if (!sourceIdsByThing.TryGetValue(thing, out var value))
				{
					value = new HashSet<int>();
					sourceIdsByThing.Add(thing, value);
				}
				value.Add(sourceId);
			}
		}

		private void RemoveMembership(Thing thing, int sourceId)
		{
			if (thing != null && sourceIdsByThing.TryGetValue(thing, out var value))
			{
				value.Remove(sourceId);
				if (value.Count == 0)
				{
					sourceIdsByThing.Remove(thing);
				}
			}
		}

		private void MarkThingSourcesDirty(Thing thing)
		{
			if (thing == null || !sourceIdsByThing.TryGetValue(thing, out var value))
			{
				return;
			}
			foreach (int item in value)
			{
				dirtySources.Add(item);
			}
		}

		internal MapFabricSnapshot BuildSnapshot()
		{
			Dictionary<int, SourceSnapshot> dictionary = new Dictionary<int, SourceSnapshot>(publishedSources);
			foreach (int dirtySource in dirtySources)
			{
				if (sources.TryGetValue(dirtySource, out var value) && value != null)
				{
					dictionary[dirtySource] = BuildSourceSnapshot(value);
				}
			}
			dirtySources.Clear();
			publishedSources = dictionary;
			return new MapFabricSnapshot(mapId, width, height, appliedGeneration, dictionary);
		}

		private SourceSnapshot BuildSourceSnapshot(SourceModel source)
		{
			int num = Math.Max(1, (width + 12 - 1) / 12);
			int num2 = Math.Max(1, (height + 12 - 1) / 12);
			List<FabricEntry>[] array = new List<FabricEntry>[num * num2];
			bool complete = true;
			for (int i = 0; i < source.Members.Length; i++)
			{
				Thing val = source.Members[i];
				if (val == null || !positions.TryGetValue(val, out var value) || value.X < 0 || value.Z < 0 || value.X >= width || value.Z >= height)
				{
					complete = false;
					continue;
				}
				int num3 = value.X / 12 + value.Z / 12 * num;
				List<FabricEntry> list = array[num3];
				if (list == null)
				{
					list = (array[num3] = new List<FabricEntry>());
				}
				list.Add(new FabricEntry(val, value.X, value.Z, i));
			}
			FabricEntry[][] array2 = new FabricEntry[array.Length][];
			for (int j = 0; j < array.Length; j++)
			{
				array2[j] = ((array[j] == null) ? EmptyEntries : array[j].ToArray());
			}
			return new SourceSnapshot(mapId, width, height, num, num2, source.Members.Length, complete, array2);
		}
	}

	private sealed class MapFabricSnapshot
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly long AppliedGeneration;

		internal readonly Dictionary<int, SourceSnapshot> Sources;

		internal MapFabricSnapshot(int mapId, int width, int height, long appliedGeneration, Dictionary<int, SourceSnapshot> sources)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			AppliedGeneration = appliedGeneration;
			Sources = sources;
		}
	}

	internal sealed class SourceSnapshot
	{
		private readonly int bucketCols;

		private readonly int bucketRows;

		private readonly FabricEntry[][] buckets;

		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly int Count;

		internal readonly bool Complete;

		internal SourceSnapshot(int mapId, int width, int height, int bucketCols, int bucketRows, int count, bool complete, FabricEntry[][] buckets)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			Count = count;
			Complete = complete;
			this.bucketCols = bucketCols;
			this.bucketRows = bucketRows;
			this.buckets = buckets;
		}

		internal int EstimateCandidates(IntVec3 root, float maxDistance, int stopAfter)
		{
			//IL_002e: Unknown result type (might be due to invalid IL or missing references)
			if (stopAfter < 1)
			{
				stopAfter = 1;
			}
			float num = maxDistance * maxDistance;
			int num2 = 0;
			for (int i = 0; i < bucketRows; i++)
			{
				for (int j = 0; j < bucketCols; j++)
				{
					FabricEntry[] array = buckets[j + i * bucketCols];
					if (array.Length != 0 && !(MinimumDistanceToBucketSquared(root, j, i) > num))
					{
						num2 += array.Length;
						if (num2 > stopAfter)
						{
							return num2;
						}
					}
				}
			}
			return num2;
		}

		internal bool TryFindClosest(IntVec3 root, Map map, PathEndMode peMode, TraverseParms traverseParams, float maxDistance, Predicate<Thing> validator, int maxLiveChecks, out Thing chosen, out int visited, out int bucketsSeen, out int reaches, out int validations, out bool staleDetected)
		{
			//IL_002a: Unknown result type (might be due to invalid IL or missing references)
			//IL_003d: Unknown result type (might be due to invalid IL or missing references)
			//IL_004f: Unknown result type (might be due to invalid IL or missing references)
			//IL_006c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0076: Unknown result type (might be due to invalid IL or missing references)
			//IL_0106: Unknown result type (might be due to invalid IL or missing references)
			//IL_0238: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a0: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a5: Unknown result type (might be due to invalid IL or missing references)
			//IL_01a7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ce: Unknown result type (might be due to invalid IL or missing references)
			//IL_01b7: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e5: Unknown result type (might be due to invalid IL or missing references)
			//IL_01e8: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ed: Unknown result type (might be due to invalid IL or missing references)
			//IL_01ee: Unknown result type (might be due to invalid IL or missing references)
			chosen = null;
			visited = 0;
			bucketsSeen = 0;
			reaches = 0;
			validations = 0;
			staleDetected = false;
			if (map == null || map.uniqueID != MapId || map.Size.x != Width || map.Size.z != Height || !GenGrid.InBounds(root, map))
			{
				return false;
			}
			float num = maxDistance * maxDistance;
			float num2 = float.MaxValue;
			int num3 = int.MaxValue;
			int num4 = root.x / 12;
			int num5 = root.z / 12;
			int num6 = Math.Max(Math.Max(num4, bucketCols - 1 - num4), Math.Max(num5, bucketRows - 1 - num5));
			List<Candidate> value = CandidateScratch.Value;
			for (int i = 0; i <= num6; i++)
			{
				value.Clear();
				int minBx = Math.Max(0, num4 - i);
				int maxBx = Math.Min(bucketCols - 1, num4 + i);
				int minBz = Math.Max(0, num5 - i);
				int maxBz = Math.Min(bucketRows - 1, num5 + i);
				AddRingCandidates(root, i, minBx, maxBx, minBz, maxBz, num, value, ref bucketsSeen);
				value.Sort(CandidateComparer.Instance);
				for (int j = 0; j < value.Count; j++)
				{
					Candidate candidate = value[j];
					if (candidate.DistanceSquared > num2)
					{
						break;
					}
					FabricEntry entry = candidate.Entry;
					if (candidate.DistanceSquared == num2 && entry.SourceIndex >= num3)
					{
						continue;
					}
					if (visited >= maxLiveChecks)
					{
						return false;
					}
					Thing thing = entry.Thing;
					visited++;
					if (thing == null || !thing.Spawned || thing.MapHeld != map)
					{
						staleDetected = true;
						return false;
					}
					IntVec3 position = thing.Position;
					if (position.x != entry.X || position.z != entry.Z)
					{
						staleDetected = true;
						NotifyDetectedPosition(map, thing, position);
						return false;
					}
					reaches++;
					if (!map.reachability.CanReach(root, LocalTargetInfo.op_Implicit(thing), peMode, traverseParams))
					{
						continue;
					}
					if (validator != null)
					{
						validations++;
						if (!validator(thing))
						{
							continue;
						}
					}
					chosen = thing;
					num2 = candidate.DistanceSquared;
					num3 = entry.SourceIndex;
				}
				float num7 = MinimumOutsideDistanceSquared(root, minBx, maxBx, minBz, maxBz);
				if (chosen != null && num7 > num2)
				{
					return true;
				}
				if (chosen == null && num7 > num)
				{
					return true;
				}
			}
			return true;
		}

		private void AddRingCandidates(IntVec3 root, int ring, int minBx, int maxBx, int minBz, int maxBz, float maxDistanceSquared, List<Candidate> output, ref int bucketsSeen)
		{
			//IL_0004: Unknown result type (might be due to invalid IL or missing references)
			//IL_0005: Unknown result type (might be due to invalid IL or missing references)
			//IL_000e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_003e: Unknown result type (might be due to invalid IL or missing references)
			//IL_005e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0072: Unknown result type (might be due to invalid IL or missing references)
			if (ring == 0)
			{
				AddBucket(root, root.x / 12, root.z / 12, maxDistanceSquared, output, ref bucketsSeen);
				return;
			}
			for (int i = minBx; i <= maxBx; i++)
			{
				AddBucket(root, i, minBz, maxDistanceSquared, output, ref bucketsSeen);
				if (maxBz != minBz)
				{
					AddBucket(root, i, maxBz, maxDistanceSquared, output, ref bucketsSeen);
				}
			}
			for (int j = minBz + 1; j < maxBz; j++)
			{
				AddBucket(root, minBx, j, maxDistanceSquared, output, ref bucketsSeen);
				if (maxBx != minBx)
				{
					AddBucket(root, maxBx, j, maxDistanceSquared, output, ref bucketsSeen);
				}
			}
		}

		private void AddBucket(IntVec3 root, int bx, int bz, float maxDistanceSquared, List<Candidate> output, ref int bucketsSeen)
		{
			//IL_0046: Unknown result type (might be due to invalid IL or missing references)
			//IL_0054: Unknown result type (might be due to invalid IL or missing references)
			if (bx < 0 || bz < 0 || bx >= bucketCols || bz >= bucketRows)
			{
				return;
			}
			FabricEntry[] array = buckets[bx + bz * bucketCols];
			if (array.Length == 0)
			{
				return;
			}
			bucketsSeen++;
			for (int i = 0; i < array.Length; i++)
			{
				FabricEntry entry = array[i];
				long num = root.x - entry.X;
				long num2 = root.z - entry.Z;
				float num3 = num * num + num2 * num2;
				if (num3 <= maxDistanceSquared)
				{
					output.Add(new Candidate(entry, num3));
				}
			}
		}

		private float MinimumDistanceToBucketSquared(IntVec3 root, int bx, int bz)
		{
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0052: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			//IL_005a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0047: Unknown result type (might be due to invalid IL or missing references)
			//IL_007a: Unknown result type (might be due to invalid IL or missing references)
			//IL_0063: Unknown result type (might be due to invalid IL or missing references)
			//IL_006f: Unknown result type (might be due to invalid IL or missing references)
			int num = bx * 12;
			int num2 = Math.Min(Width - 1, num + 12 - 1);
			int num3 = bz * 12;
			int num4 = Math.Min(Height - 1, num3 + 12 - 1);
			long num5 = ((root.x < num) ? (num - root.x) : ((root.x > num2) ? (root.x - num2) : 0));
			long num6 = ((root.z < num3) ? (num3 - root.z) : ((root.z > num4) ? (root.z - num4) : 0));
			return num5 * num5 + num6 * num6;
		}

		private float MinimumOutsideDistanceSquared(IntVec3 root, int minBx, int maxBx, int minBz, int maxBz)
		{
			//IL_0047: Unknown result type (might be due to invalid IL or missing references)
			//IL_0070: Unknown result type (might be due to invalid IL or missing references)
			//IL_008d: Unknown result type (might be due to invalid IL or missing references)
			//IL_00b7: Unknown result type (might be due to invalid IL or missing references)
			int num = minBx * 12;
			int num2 = Math.Min(Width - 1, (maxBx + 1) * 12 - 1);
			int num3 = minBz * 12;
			int num4 = Math.Min(Height - 1, (maxBz + 1) * 12 - 1);
			long num5 = long.MaxValue;
			if (minBx > 0)
			{
				long num6 = root.x - (num - 1);
				num5 = Math.Min(num5, num6 * num6);
			}
			if (maxBx < bucketCols - 1)
			{
				long num7 = num2 + 1 - root.x;
				num5 = Math.Min(num5, num7 * num7);
			}
			if (minBz > 0)
			{
				long num8 = root.z - (num3 - 1);
				num5 = Math.Min(num5, num8 * num8);
			}
			if (maxBz < bucketRows - 1)
			{
				long num9 = num4 + 1 - root.z;
				num5 = Math.Min(num5, num9 * num9);
			}
			if (num5 != long.MaxValue)
			{
				return num5;
			}
			return float.MaxValue;
		}

		internal DistancePlan BuildDistancePlan(int rootX, int rootZ)
		{
			List<DistancePlanCandidate> list = new List<DistancePlanCandidate>(Count);
			for (int i = 0; i < buckets.Length; i++)
			{
				FabricEntry[] array = buckets[i];
				for (int j = 0; j < array.Length; j++)
				{
					FabricEntry entry = array[j];
					long num = (long)entry.X - (long)rootX;
					long num2 = (long)entry.Z - (long)rootZ;
					list.Add(new DistancePlanCandidate(entry, num * num + num2 * num2));
				}
			}
			if (list.Count > 1)
			{
				list.Sort(DistancePlanCandidateComparer.Instance);
			}
			DistancePlanEntry[] array2 = new DistancePlanEntry[list.Count];
			Thing[] array3 = (Thing[])(object)new Thing[list.Count];
			for (int k = 0; k < list.Count; k++)
			{
				DistancePlanCandidate distancePlanCandidate = list[k];
				FabricEntry entry2 = distancePlanCandidate.Entry;
				array2[k] = new DistancePlanEntry(entry2.Thing, entry2.X, entry2.Z, entry2.SourceIndex, distancePlanCandidate.DistanceSquared);
				array3[k] = entry2.Thing;
			}
			return new DistancePlan(MapId, Width, Height, rootX, rootZ, array2, array3);
		}
	}

	internal struct FabricEntry
	{
		internal readonly Thing Thing;

		internal readonly int X;

		internal readonly int Z;

		internal readonly int SourceIndex;

		internal FabricEntry(Thing thing, int x, int z, int sourceIndex)
		{
			Thing = thing;
			X = x;
			Z = z;
			SourceIndex = sourceIndex;
		}
	}

	private sealed class ThingReferenceComparer : IEqualityComparer<Thing>
	{
		internal static readonly ThingReferenceComparer Instance = new ThingReferenceComparer();

		public bool Equals(Thing x, Thing y)
		{
			return x == y;
		}

		public int GetHashCode(Thing obj)
		{
			if (obj != null)
			{
				return RuntimeHelpers.GetHashCode(obj);
			}
			return 0;
		}
	}

	private struct Candidate
	{
		internal readonly FabricEntry Entry;

		internal readonly float DistanceSquared;

		internal Candidate(FabricEntry entry, float distanceSquared)
		{
			Entry = entry;
			DistanceSquared = distanceSquared;
		}
	}

	private sealed class CandidateComparer : IComparer<Candidate>
	{
		internal static readonly CandidateComparer Instance = new CandidateComparer();

		public int Compare(Candidate a, Candidate b)
		{
			int num = a.DistanceSquared.CompareTo(b.DistanceSquared);
			if (num != 0)
			{
				return num;
			}
			return a.Entry.SourceIndex.CompareTo(b.Entry.SourceIndex);
		}
	}

	internal sealed class DistancePlan
	{
		internal readonly int MapId;

		internal readonly int Width;

		internal readonly int Height;

		internal readonly int RootX;

		internal readonly int RootZ;

		internal readonly DistancePlanEntry[] Entries;

		internal readonly Thing[] OrderedThings;

		internal int Count
		{
			get
			{
				if (Entries != null)
				{
					return Entries.Length;
				}
				return 0;
			}
		}

		internal DistancePlan(int mapId, int width, int height, int rootX, int rootZ, DistancePlanEntry[] entries, Thing[] orderedThings)
		{
			MapId = mapId;
			Width = width;
			Height = height;
			RootX = rootX;
			RootZ = rootZ;
			Entries = entries;
			OrderedThings = orderedThings;
		}
	}

	internal struct DistancePlanEntry
	{
		internal readonly Thing Thing;

		internal readonly int X;

		internal readonly int Z;

		internal readonly int SourceIndex;

		internal readonly long DistanceSquared;

		internal DistancePlanEntry(Thing thing, int x, int z, int sourceIndex, long distanceSquared)
		{
			Thing = thing;
			X = x;
			Z = z;
			SourceIndex = sourceIndex;
			DistanceSquared = distanceSquared;
		}
	}

	private struct DistancePlanCandidate
	{
		internal readonly FabricEntry Entry;

		internal readonly long DistanceSquared;

		internal DistancePlanCandidate(FabricEntry entry, long distanceSquared)
		{
			Entry = entry;
			DistanceSquared = distanceSquared;
		}
	}

	private sealed class DistancePlanCandidateComparer : IComparer<DistancePlanCandidate>
	{
		internal static readonly DistancePlanCandidateComparer Instance = new DistancePlanCandidateComparer();

		public int Compare(DistancePlanCandidate a, DistancePlanCandidate b)
		{
			int num = a.DistanceSquared.CompareTo(b.DistanceSquared);
			if (num != 0)
			{
				return num;
			}
			return a.Entry.SourceIndex.CompareTo(b.Entry.SourceIndex);
		}
	}

	private const string FeatureId = "parallel.candidateFabric";

	internal const int BucketSize = 12;

	private const int MaxSourcesPerMap = 96;

	private static readonly ConditionalWeakTable<Map, MapState> States = new ConditionalWeakTable<Map, MapState>();

	private static readonly ConcurrentQueue<MapState> PendingStatesT34B = new ConcurrentQueue<MapState>();

	private static long sourceRegistrations;

	private static long sourceRegistrationRejected;

	private static long trackedAdds;

	private static long gridUpserts;

	private static long gridRemoves;

	private static long workerBatches;

	private static long workerEvents;

	private static long snapshotsPublished;

	private static long schedulerRejected;

	private static long snapshotHits;

	private static long snapshotMisses;

	private static long staleGenerationBypasses;

	private static long incompleteSourceBypasses;

	private static long failures;

	private static long publishTicks;

	private static long publishTicksMax;

	private static readonly FabricEntry[] EmptyEntries = new FabricEntry[0];

	private static readonly ThreadLocal<List<Candidate>> CandidateScratch = new ThreadLocal<List<Candidate>>(() => new List<Candidate>(128));

	internal static void Apply(Harmony harmony)
	{
		//IL_00a2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00a8: Expected O, but got Unknown
		//IL_00bf: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c5: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(ThingGrid), "RegisterInCell", new Type[2]
			{
				typeof(Thing),
				typeof(IntVec3)
			}, (Type[])null);
			MethodBase methodBase2 = AccessTools.Method(typeof(ThingGrid), "DeregisterInCell", new Type[2]
			{
				typeof(Thing),
				typeof(IntVec3)
			}, (Type[])null);
			if (methodBase == null || methodBase2 == null)
			{
				Log.Warning("[RimMT] V0.4.14 map fabric ThingGrid hooks unavailable; persistent source views stay fail-closed.");
				return;
			}
			HarmonyMethod val = new HarmonyMethod(typeof(PersistentMapSearchFabric), "RegisterInCellPostfix", (Type[])null);
			val.priority = 0;
			HarmonyMethod val2 = new HarmonyMethod(typeof(PersistentMapSearchFabric), "DeregisterInCellPrefix", (Type[])null);
			val2.priority = 800;
			harmony.Patch(methodBase, (HarmonyMethod)null, val, (HarmonyMethod)null, (HarmonyMethod)null);
			harmony.Patch(methodBase2, val2, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			Log.Message("[RimMT] V0.4.14 persistent map search fabric installed. Tracked source positions are maintained incrementally on worker cores; the main thread never waits for fabric publication.");
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Log.Warning("[RimMT] V0.4.14 map fabric hooks failed; GenClosest remains fail-closed. " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static void RegisterInCellPostfix(Thing t, IntVec3 c)
	{
		//IL_0044: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		if (t != null && RimMTThreadGuard.IsMainThread)
		{
			Map mapHeld = t.MapHeld;
			if (mapHeld != null && !mapHeld.Disposed && States.TryGetValue(mapHeld, out var value) && value.TrackedThings.Contains(t))
			{
				QueueEvent(value, FabricEvent.Upsert(NextGeneration(value), t, c.x, c.z));
				Interlocked.Increment(ref gridUpserts);
			}
		}
	}

	public static void DeregisterInCellPrefix(Thing t, IntVec3 c)
	{
		if (t != null && RimMTThreadGuard.IsMainThread)
		{
			Map mapHeld = t.MapHeld;
			if (mapHeld != null && !mapHeld.Disposed && States.TryGetValue(mapHeld, out var value) && value.TrackedThings.Contains(t))
			{
				QueueEvent(value, FabricEvent.Remove(NextGeneration(value), t));
				Interlocked.Increment(ref gridRemoves);
			}
		}
	}

	internal static bool RegisterOrUpdateSource(Map map, int sourceId, Thing[] members)
	{
		//IL_0019: Unknown result type (might be due to invalid IL or missing references)
		//IL_001f: Invalid comparison between Unknown and I4
		//IL_00be: Unknown result type (might be due to invalid IL or missing references)
		//IL_00c3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ce: Unknown result type (might be due to invalid IL or missing references)
		//IL_00e3: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ef: Unknown result type (might be due to invalid IL or missing references)
		if (map == null || map.Disposed || members == null || members.Length == 0 || !RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2)
		{
			return false;
		}
		MapState value = States.GetValue(map, CreateMapState);
		if (value.KnownSources.Add(sourceId) && value.KnownSources.Count > 96)
		{
			value.KnownSources.Remove(sourceId);
			Interlocked.Increment(ref sourceRegistrationRejected);
			return false;
		}
		int num = members.Length;
		Thing[] array = (Thing[])(object)new Thing[num];
		int[] array2 = new int[num];
		int[] array3 = new int[num];
		for (int i = 0; i < num; i++)
		{
			Thing val = members[i];
			if (val == null || val is Pawn || !val.Spawned || val.MapHeld != map)
			{
				return false;
			}
			IntVec3 position = val.Position;
			if (!((IntVec3)(ref position)).IsValid || !GenGrid.InBounds(position, map))
			{
				return false;
			}
			array[i] = val;
			array2[i] = position.x;
			array3[i] = position.z;
			if (value.TrackedThings.Add(val))
			{
				Interlocked.Increment(ref trackedAdds);
			}
		}
		long generation = NextGeneration(value);
		QueueEvent(value, FabricEvent.Source(generation, sourceId, array, array2, array3));
		Interlocked.Increment(ref sourceRegistrations);
		return true;
	}

	internal static void NotifyDetectedPosition(Map map, Thing thing, IntVec3 pos)
	{
		//IL_003c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0042: Unknown result type (might be due to invalid IL or missing references)
		if (map != null && thing != null && !map.Disposed && RimMTThreadGuard.IsMainThread && States.TryGetValue(map, out var value) && value.TrackedThings.Contains(thing))
		{
			QueueEvent(value, FabricEvent.Upsert(NextGeneration(value), thing, pos.x, pos.z));
			Interlocked.Increment(ref gridUpserts);
		}
	}

	internal static bool TryGetSourceSnapshot(Map map, int sourceId, out SourceSnapshot source)
	{
		source = null;
		if (map == null || map.Disposed)
		{
			return false;
		}
		if (!States.TryGetValue(map, out var value))
		{
			Interlocked.Increment(ref snapshotMisses);
			return false;
		}
		MapFabricSnapshot mapFabricSnapshot = Volatile.Read(ref value.Published);
		if (mapFabricSnapshot == null)
		{
			Interlocked.Increment(ref snapshotMisses);
			return false;
		}
		long num = Volatile.Read(ref value.MainGeneration);
		if (mapFabricSnapshot.AppliedGeneration != num)
		{
			Interlocked.Increment(ref staleGenerationBypasses);
			return false;
		}
		if (!mapFabricSnapshot.Sources.TryGetValue(sourceId, out source) || source == null)
		{
			Interlocked.Increment(ref snapshotMisses);
			return false;
		}
		if (!source.Complete)
		{
			Interlocked.Increment(ref incompleteSourceBypasses);
			source = null;
			return false;
		}
		Interlocked.Increment(ref snapshotHits);
		return true;
	}

	private static MapState CreateMapState(Map map)
	{
		//IL_0007: Unknown result type (might be due to invalid IL or missing references)
		//IL_0012: Unknown result type (might be due to invalid IL or missing references)
		return new MapState(map.uniqueID, map.Size.x, map.Size.z);
	}

	private static long NextGeneration(MapState state)
	{
		return Interlocked.Increment(ref state.MainGeneration);
	}

	private static void QueueEvent(MapState state, FabricEvent ev)
	{
		state.Events.Enqueue(ev);
		MarkPendingT34B(state);
	}

	private static void MarkPendingT34B(MapState state)
	{
		if (state != null && Interlocked.CompareExchange(ref state.FlushQueuedT34B, 1, 0) == 0)
		{
			PendingStatesT34B.Enqueue(state);
		}
	}

	internal static void FlushPendingT34B()
	{
		int num = 64;
		MapState result;
		while (num-- > 0 && PendingStatesT34B.TryDequeue(out result))
		{
			Volatile.Write(ref result.FlushQueuedT34B, 0);
			if (!result.Events.IsEmpty)
			{
				if (Interlocked.CompareExchange(ref result.WorkerScheduled, 1, 0) != 0)
				{
					MarkPendingT34B(result);
				}
				else
				{
					ScheduleDrain(result);
				}
			}
		}
	}

	private static void ScheduleDrain(MapState state)
	{
		JobScheduler scheduler = RimMTRuntime.Scheduler;
		if (scheduler == null)
		{
			Volatile.Write(ref state.WorkerScheduled, 0);
			Interlocked.Increment(ref schedulerRejected);
		}
		else if (!scheduler.TryEnqueue("parallel.candidateFabric", JobPriority.High, delegate
		{
			DrainWorker(state);
		}))
		{
			Volatile.Write(ref state.WorkerScheduled, 0);
			Interlocked.Increment(ref schedulerRejected);
			if (!state.Events.IsEmpty)
			{
				MarkPendingT34B(state);
			}
		}
	}

	private static void DrainWorker(MapState state)
	{
		try
		{
			int num = 0;
			FabricEvent result;
			while (state.Events.TryDequeue(out result))
			{
				state.Model.Apply(result);
				num++;
			}
			if (num > 0)
			{
				long timestamp = Stopwatch.GetTimestamp();
				MapFabricSnapshot value = state.Model.BuildSnapshot();
				long value2 = Stopwatch.GetTimestamp() - timestamp;
				Interlocked.Add(ref publishTicks, value2);
				UpdateMax(ref publishTicksMax, value2);
				Volatile.Write(ref state.Published, value);
				Interlocked.Increment(ref workerBatches);
				Interlocked.Add(ref workerEvents, num);
				Interlocked.Increment(ref snapshotsPublished);
			}
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			CircuitBreaker.RecordFailure("parallel.candidateFabric", ex);
			Log.Warning("[RimMT] V0.4.14 map fabric worker failure; published data is ignored until a later synchronized snapshot. " + ex.GetType().Name + ": " + ex.Message);
		}
		finally
		{
			Volatile.Write(ref state.WorkerScheduled, 0);
			if (!state.Events.IsEmpty)
			{
				MarkPendingT34B(state);
			}
		}
	}

	internal static string Summary()
	{
		long num = Interlocked.Read(ref snapshotsPublished);
		double num2 = ((num == 0L) ? 0.0 : ((double)Interlocked.Read(ref publishTicks) * 1000000.0 / (double)Stopwatch.Frequency / (double)num));
		double num3 = (double)Interlocked.Read(ref publishTicksMax) * 1000000.0 / (double)Stopwatch.Frequency;
		return "Persistent map search fabric V0.4.14: sourceRegisters=" + Interlocked.Read(ref sourceRegistrations) + ", sourceRegisterRejected=" + Interlocked.Read(ref sourceRegistrationRejected) + ", trackedAdds=" + Interlocked.Read(ref trackedAdds) + ", gridUpserts=" + Interlocked.Read(ref gridUpserts) + ", gridRemoves=" + Interlocked.Read(ref gridRemoves) + ", workerBatches=" + Interlocked.Read(ref workerBatches) + ", workerEvents=" + Interlocked.Read(ref workerEvents) + ", published=" + num + ", snapshotHits=" + Interlocked.Read(ref snapshotHits) + ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) + ", staleGenerationBypass=" + Interlocked.Read(ref staleGenerationBypasses) + ", incompleteSourceBypass=" + Interlocked.Read(ref incompleteSourceBypasses) + ", avgPublishUs=" + num2.ToString("F2") + ", maxPublishUs=" + num3.ToString("F2") + ", schedulerRejected=" + Interlocked.Read(ref schedulerRejected) + ", failures=" + Interlocked.Read(ref failures) + ". Worker snapshots contain only Thing references plus primitive positions/source order; no Verse object is dereferenced off-thread.";
	}

	private static void UpdateMax(ref long field, long value)
	{
		long num;
		while (value > (num = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, num) != num)
		{
		}
	}
}
