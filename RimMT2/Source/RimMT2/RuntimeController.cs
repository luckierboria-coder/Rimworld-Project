using System;
using System.Collections.Concurrent;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT2
{
    internal static class RuntimeController
    {
        private static WorkerFabric fabric;
        private static readonly ConcurrentQueue<int> pendingMapDiscards = new ConcurrentQueue<int>();
        private static int startRequested;
        private static int censusConfigurationRequested;

        internal static WorkerFabric Fabric => fabric;
        internal static bool Running => fabric != null;

        internal static void RequestStart()
        {
            Volatile.Write(ref startRequested, 1);
        }

        internal static void RequestCensusConfiguration()
        {
            Volatile.Write(ref censusConfigurationRequested, 1);
        }

        internal static void RequestMapDiscard(int mapId)
        {
            pendingMapDiscards.Enqueue(mapId);
        }

        internal static void PumpMainThread()
        {
            MainThreadGuard.RequireMainThread("RuntimeController.PumpMainThread");
            if (Volatile.Read(ref startRequested) == 0 &&
                Volatile.Read(ref censusConfigurationRequested) == 0 && pendingMapDiscards.IsEmpty)
            {
                return;
            }
            if (Interlocked.Exchange(ref startRequested, 0) != 0)
            {
                ApplySettings();
            }
            if (Interlocked.Exchange(ref censusConfigurationRequested, 0) != 0)
            {
                Diagnostics.JobSearchCensus.Configure(RimMT2Mod.Settings?.enableJobSearchCensus ?? false);
            }
            while (pendingMapDiscards.TryDequeue(out int mapId))
            {
                Spatial.SpatialSnapshotModule.DiscardMap(mapId);
            }
        }

        internal static void ApplySettings()
        {
            if (!MainThreadGuard.IsMainThread)
            {
                return;
            }
            RimMT2Settings settings = RimMT2Mod.Settings;
            bool enabled = settings == null || settings.enableWorkerFabric;
            bool hasActiveModule = settings != null && settings.enableSpatialProof;
            if (!enabled || !hasActiveModule)
            {
                Stop();
                if (enabled)
                {
                    Log.Message("[RimMT 2.0] worker fabric remains dormant because no snapshot module is enabled.");
                }
                return;
            }
            if (fabric != null)
            {
                return;
            }
            fabric = new WorkerFabric();
            Log.Message("[RimMT 2.0] Unity job fabric active; engine worker capacity=" + fabric.WorkerCount + ".");
        }

        internal static void TickMainThread()
        {
            if (fabric == null)
            {
                return;
            }
            fabric.DrainCommits(0.40);
            Spatial.SpatialSnapshotModule.Tick();
            fabric.Flush();
        }

        internal static void Stop()
        {
            WorkerFabric previous = fabric;
            fabric = null;
            previous?.Dispose();
            Spatial.SpatialSnapshotModule.Reset();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.FinalizeInit))]
    internal static class GameFinalizeInitPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            RuntimeController.RequestStart();
        }
    }

    [HarmonyPatch(typeof(Game), nameof(Game.DeinitAndRemoveMap))]
    internal static class GameDeinitMapPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Map map)
        {
            if (map != null)
            {
                RuntimeController.RequestMapDiscard(map.uniqueID);
            }
        }
    }

    [HarmonyPatch(typeof(Root), nameof(Root.Update))]
    internal static class UnityMainThreadPumpPatch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static void Prefix()
        {
            MainThreadGuard.CaptureFromUnityUpdate();
            RuntimeController.PumpMainThread();
        }
    }

    [HarmonyPatch(typeof(TickManager), nameof(TickManager.DoSingleTick))]
    internal static class TickManagerCommitPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            RuntimeController.TickMainThread();
        }
    }
}
