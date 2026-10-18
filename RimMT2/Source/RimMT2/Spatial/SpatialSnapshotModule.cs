using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Verse;

namespace RimMT2.Spatial
{
    internal struct SpatialEntry
    {
        internal int thingId;
        internal int cellIndex;
        internal ushort defHash;
        internal byte category;
    }

    internal sealed class SpatialSnapshot
    {
        internal int mapId;
        internal int captureTick;
        internal int cellCount;
        internal SpatialEntry[] entries;
    }

    internal sealed class SpatialIndex
    {
        internal int mapId;
        internal int captureTick;
        internal SpatialEntry[] orderedEntries;
        internal int[] cellStarts;
    }

    internal static class SpatialIndexStore
    {
        private static readonly Dictionary<int, SpatialIndex> indexes = new Dictionary<int, SpatialIndex>();

        internal static void Publish(SpatialIndex index)
        {
            MainThreadGuard.RequireMainThread("SpatialIndexStore.Publish");
            indexes[index.mapId] = index;
        }

        internal static bool TryGet(int mapId, out SpatialIndex index)
        {
            MainThreadGuard.RequireMainThread("SpatialIndexStore.TryGet");
            return indexes.TryGetValue(mapId, out index);
        }

        internal static void Remove(int mapId)
        {
            MainThreadGuard.RequireMainThread("SpatialIndexStore.Remove");
            indexes.Remove(mapId);
        }

        internal static void Clear()
        {
            MainThreadGuard.RequireMainThread("SpatialIndexStore.Clear");
            indexes.Clear();
        }
    }

    internal sealed class SpatialIndexWorkItem : SnapshotWorkItem<SpatialIndex>
    {
        private readonly SpatialSnapshot snapshot;
        private readonly Action<int> finished;

        internal SpatialIndexWorkItem(SpatialSnapshot snapshot, Action<int> finished)
        {
            this.snapshot = snapshot;
            this.finished = finished;
        }

        public override string ModuleName => "spatial.snapshot";

        protected override SpatialIndex ComputeSnapshot()
        {
            int[] counts = new int[snapshot.cellCount];
            SpatialEntry[] source = snapshot.entries;
            for (int i = 0; i < source.Length; i++)
            {
                int cell = source[i].cellIndex;
                if ((uint)cell < (uint)counts.Length)
                {
                    counts[cell]++;
                }
            }

            int[] starts = new int[snapshot.cellCount + 1];
            for (int i = 0; i < counts.Length; i++)
            {
                starts[i + 1] = starts[i] + counts[i];
            }

            int[] cursor = new int[snapshot.cellCount];
            Array.Copy(starts, cursor, cursor.Length);
            SpatialEntry[] ordered = new SpatialEntry[starts[starts.Length - 1]];
            for (int i = 0; i < source.Length; i++)
            {
                SpatialEntry entry = source[i];
                if ((uint)entry.cellIndex < (uint)cursor.Length)
                {
                    ordered[cursor[entry.cellIndex]++] = entry;
                }
            }

            return new SpatialIndex
            {
                mapId = snapshot.mapId,
                captureTick = snapshot.captureTick,
                orderedEntries = ordered,
                cellStarts = starts
            };
        }

        protected override bool CanCommit(SpatialIndex computed)
        {
            Map map = Find.Maps.FirstOrDefault(candidate => candidate.uniqueID == computed.mapId);
            return map != null && GenTicks.TicksGame - computed.captureTick <= 600;
        }

        protected override void CommitResult(SpatialIndex computed)
        {
            SpatialIndexStore.Publish(computed);
            finished(computed.mapId);
        }

        protected override void OnStaleResult(SpatialIndex computed)
        {
            finished(computed.mapId);
        }

        protected override void OnWorkerFailure(Exception exception)
        {
            finished(snapshot.mapId);
            base.OnWorkerFailure(exception);
        }

        protected override void OnCommitFailure(Exception exception)
        {
            finished(snapshot.mapId);
            base.OnCommitFailure(exception);
        }

        public override void OnQueueRejected()
        {
            finished(snapshot.mapId);
        }
    }

    internal static class SpatialSnapshotModule
    {
        private const int RebuildInterval = 250;
        private static readonly HashSet<int> inFlight = new HashSet<int>();
        private static int lastTick = -1;

        internal static void Tick()
        {
            RimMT2Settings settings = RimMT2Mod.Settings;
            if (settings == null || !settings.enableSpatialProof || RuntimeController.Fabric == null)
            {
                return;
            }

            int tick = GenTicks.TicksGame;
            if (tick == lastTick || tick % RebuildInterval != 0)
            {
                return;
            }
            lastTick = tick;

            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                TrySchedule(maps[i], tick);
            }
        }

        private static void TrySchedule(Map map, int tick)
        {
            MainThreadGuard.RequireMainThread("SpatialSnapshotModule.TrySchedule");
            if (map == null || inFlight.Contains(map.uniqueID))
            {
                return;
            }

            List<Thing> things = map.listerThings.AllThings;
            List<SpatialEntry> entries = new List<SpatialEntry>(things.Count);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (thing == null || thing.Destroyed || !thing.Spawned || thing.def == null)
                {
                    continue;
                }
                entries.Add(new SpatialEntry
                {
                    thingId = thing.thingIDNumber,
                    cellIndex = map.cellIndices.CellToIndex(thing.Position),
                    defHash = thing.def.shortHash,
                    category = (byte)thing.def.category
                });
            }

            SpatialSnapshot snapshot = new SpatialSnapshot
            {
                mapId = map.uniqueID,
                captureTick = tick,
                cellCount = map.cellIndices.NumGridCells,
                entries = entries.ToArray()
            };
            inFlight.Add(map.uniqueID);
            RuntimeController.Fabric.TrySubmit(new SpatialIndexWorkItem(snapshot, Complete));
        }

        private static void Complete(int mapId)
        {
            inFlight.Remove(mapId);
        }

        internal static void DiscardMap(int mapId)
        {
            MainThreadGuard.RequireMainThread("SpatialSnapshotModule.DiscardMap");
            inFlight.Remove(mapId);
            SpatialIndexStore.Remove(mapId);
        }

        internal static void Reset()
        {
            if (!MainThreadGuard.IsMainThread)
            {
                return;
            }
            inFlight.Clear();
            SpatialIndexStore.Clear();
            lastTick = -1;
        }
    }
}
