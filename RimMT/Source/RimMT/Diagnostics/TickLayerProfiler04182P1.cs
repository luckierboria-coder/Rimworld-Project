using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    // V0.4.18.2-P1: diagnostic-only hierarchical profiler.
    // No gameplay result, execution order, cache policy, worker policy or authority boundary is changed.
    internal static class TickLayerProfiler04182P1
    {
        internal const string FeatureId = "diagnostics.tickLayers";
        private const int DetailSampleMask = 15; // 1/16 ticks for per-Pawn detail probes.

        private const int TotalTick = 0;
        private const int TickList = 1;
        private const int MapPreTick = 2;
        private const int MapPostTick = 3;
        private const int MapComponents = 4;
        private const int GameComponents = 5;
        private const int WorldComponents = 6;
        private const int WorldPawns = 7;
        private const int WorldObjects = 8;
        private const int Storyteller = 9;
        private const int Lords = 10;
        private const int PawnTick = 11;
        private const int JobTracker = 12;
        private const int Pather = 13;
        private const int TickLayerCount = 14;

        private const int RootPlayUpdate = 0;
        private const int MapUpdate = 1;
        private const int MapComponentUpdate = 2;
        private const int GameComponentUpdate = 3;
        private const int WorldComponentUpdate = 4;
        private const int DynamicDraw = 5;
        private const int UpdateLayerCount = 6;

        private static readonly string[] TickLayerNames =
        {
            "DoSingleTick.total",
            "Thing.TickLists(inclusive)",
            "Map.MapPreTick(inclusive)",
            "Map.MapPostTick(inclusive)",
            "MapComponents.Tick(inclusive)",
            "GameComponents.Tick(inclusive)",
            "WorldComponents.Tick(inclusive)",
            "WorldPawns.Tick(inclusive)",
            "WorldObjects.Tick(inclusive)",
            "Storyteller.Tick(inclusive)",
            "LordManager.Tick(inclusive)",
            "Pawn.Tick[1/16 sampled ticks]",
            "Pawn_JobTracker.JobTrackerTick[1/16 sampled ticks]",
            "Pawn_PathFollower.PatherTick[1/16 sampled ticks]"
        };

        private static readonly string[] UpdateLayerNames =
        {
            "Root_Play.Update",
            "Map.MapUpdate",
            "MapComponents.Update",
            "GameComponents.Update",
            "WorldComponents.Update",
            "DynamicDrawManager.DrawDynamicThings"
        };

        private static readonly object Sync = new object();
        private static readonly FrameStat[] TickStats = CreateStats(TickLayerCount);
        private static readonly DirectStat[] UpdateStats = CreateDirectStats(UpdateLayerCount);

        [ThreadStatic] private static long[] currentTicks;
        [ThreadStatic] private static int[] currentCalls;
        [ThreadStatic] private static bool frameActive;
        [ThreadStatic] private static bool detailActive;
        [ThreadStatic] private static long frameStarted;

        private static long tickSerial;
        private static long detailedTicks;
        private static int patchedTargets;
        private static int missingTargets;
        private static int patchFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            Patch(harmony, "Verse.TickManager", "DoSingleTick", nameof(FramePrefix), nameof(FramePostfix), Priority.First, Priority.Last);

            Patch(harmony, "Verse.TickList", "Tick", nameof(TickListPrefix), nameof(TickListPostfix));
            Patch(harmony, "Verse.Map", "MapPreTick", nameof(MapPrePrefix), nameof(MapPrePostfix));
            Patch(harmony, "Verse.Map", "MapPostTick", nameof(MapPostPrefix), nameof(MapPostPostfix));
            Patch(harmony, "Verse.MapComponentUtility", "MapComponentTick", nameof(MapComponentsPrefix), nameof(MapComponentsPostfix));
            Patch(harmony, "Verse.GameComponentUtility", "GameComponentTick", nameof(GameComponentsPrefix), nameof(GameComponentsPostfix));
            Patch(harmony, "Verse.WorldComponentUtility", "WorldComponentTick", nameof(WorldComponentsPrefix), nameof(WorldComponentsPostfix));
            Patch(harmony, "RimWorld.Planet.WorldPawns", "WorldPawnsTick", nameof(WorldPawnsPrefix), nameof(WorldPawnsPostfix));
            Patch(harmony, "RimWorld.Planet.WorldObjectsHolder", "WorldObjectsHolderTick", nameof(WorldObjectsPrefix), nameof(WorldObjectsPostfix));
            Patch(harmony, "RimWorld.Storyteller", "StorytellerTick", nameof(StorytellerPrefix), nameof(StorytellerPostfix));
            PatchFirstAvailable(harmony, new[] { "Verse.AI.Group.LordManager", "RimWorld.LordManager" }, "LordManagerTick", nameof(LordsPrefix), nameof(LordsPostfix));

            // Per-Pawn detail probes remain resident but only start Stopwatch on 1/16 game ticks.
            // Their Harmony call overhead is intentionally bounded to a diagnostics-only build.
            Patch(harmony, "Verse.Pawn", "Tick", nameof(PawnPrefix), nameof(PawnPostfix));
            Patch(harmony, "Verse.AI.Pawn_JobTracker", "JobTrackerTick", nameof(JobTrackerPrefix), nameof(JobTrackerPostfix));
            Patch(harmony, "Verse.AI.Pawn_PathFollower", "PatherTick", nameof(PatherPrefix), nameof(PatherPostfix));

            // Frame-side probes help separate simulation stalls from rendered-frame/update stalls.
            Patch(harmony, "Verse.Root_Play", "Update", nameof(RootUpdatePrefix), nameof(RootUpdatePostfix));
            Patch(harmony, "Verse.Map", "MapUpdate", nameof(MapUpdatePrefix), nameof(MapUpdatePostfix));
            Patch(harmony, "Verse.MapComponentUtility", "MapComponentUpdate", nameof(MapComponentUpdatePrefix), nameof(MapComponentUpdatePostfix));
            Patch(harmony, "Verse.GameComponentUtility", "GameComponentUpdate", nameof(GameComponentUpdatePrefix), nameof(GameComponentUpdatePostfix));
            Patch(harmony, "Verse.WorldComponentUtility", "WorldComponentUpdate", nameof(WorldComponentUpdatePrefix), nameof(WorldComponentUpdatePostfix));
            Patch(harmony, "Verse.DynamicDrawManager", "DrawDynamicThings", nameof(DynamicDrawPrefix), nameof(DynamicDrawPostfix));

            Log.Message("[RimMT] V0.4.18.2-P1 tick-layer profiler installed. Diagnostic-only: no gameplay authority or worker behavior is changed. High-level tick/update layers are measured continuously; Pawn/JobTracker/Pather detail is sampled 1/16 game ticks.");
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder();
            lock (Sync)
            {
                sb.Append("Tick-layer profiler V0.4.18.2-P1: patchedTargets=").Append(patchedTargets)
                    .Append(", missingTargets=").Append(missingTargets)
                    .Append(", patchFailures=").Append(patchFailures)
                    .Append(", tickFrames=").Append(TickStats[TotalTick].Frames)
                    .Append(", detailSampleEvery=16, detailedTicks=").Append(detailedTicks)
                    .AppendLine(". Timings are inclusive; nested rows must not be summed blindly.");

                for (int i = 0; i < TickStats.Length; i++)
                {
                    bool sampled = i >= PawnTick;
                    AppendFrameStat(sb, TickLayerNames[i], TickStats[i], sampled);
                }

                sb.AppendLine("Frame/update-side profiler V0.4.18.2-P1:");
                for (int i = 0; i < UpdateStats.Length; i++)
                    AppendDirectStat(sb, UpdateLayerNames[i], UpdateStats[i]);
            }
            return sb.ToString();
        }

        public static void FramePrefix()
        {
            frameActive = false;
            detailActive = false;
            frameStarted = 0L;

            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || RuntimeCompatibility.ButterPlusPlusActive)
                return;

            EnsureThreadBuffers();
            Array.Clear(currentTicks, 0, currentTicks.Length);
            Array.Clear(currentCalls, 0, currentCalls.Length);

            long serial = ++tickSerial;
            detailActive = (serial & DetailSampleMask) == 0;
            if (detailActive)
                System.Threading.Interlocked.Increment(ref detailedTicks);
            frameStarted = Stopwatch.GetTimestamp();
            frameActive = true;
        }

        public static void FramePostfix()
        {
            if (!frameActive || frameStarted == 0L)
                return;

            long total = Stopwatch.GetTimestamp() - frameStarted;
            currentTicks[TotalTick] = total;
            currentCalls[TotalTick] = 1;

            lock (Sync)
            {
                for (int i = 0; i < TickLayerCount; i++)
                {
                    if (i >= PawnTick && !detailActive)
                        continue;
                    TickStats[i].Record(currentTicks[i], currentCalls[i]);
                }
            }

            frameActive = false;
            detailActive = false;
            frameStarted = 0L;
        }

        public static void TickListPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void TickListPostfix(long __state) { EndLayer(TickList, __state); }
        public static void MapPrePrefix(ref long __state) { __state = BeginLayer(false); }
        public static void MapPrePostfix(long __state) { EndLayer(MapPreTick, __state); }
        public static void MapPostPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void MapPostPostfix(long __state) { EndLayer(MapPostTick, __state); }
        public static void MapComponentsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void MapComponentsPostfix(long __state) { EndLayer(MapComponents, __state); }
        public static void GameComponentsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void GameComponentsPostfix(long __state) { EndLayer(GameComponents, __state); }
        public static void WorldComponentsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void WorldComponentsPostfix(long __state) { EndLayer(WorldComponents, __state); }
        public static void WorldPawnsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void WorldPawnsPostfix(long __state) { EndLayer(WorldPawns, __state); }
        public static void WorldObjectsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void WorldObjectsPostfix(long __state) { EndLayer(WorldObjects, __state); }
        public static void StorytellerPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void StorytellerPostfix(long __state) { EndLayer(Storyteller, __state); }
        public static void LordsPrefix(ref long __state) { __state = BeginLayer(false); }
        public static void LordsPostfix(long __state) { EndLayer(Lords, __state); }

        public static void PawnPrefix(ref long __state) { __state = BeginLayer(true); }
        public static void PawnPostfix(long __state) { EndLayer(PawnTick, __state); }
        public static void JobTrackerPrefix(ref long __state) { __state = BeginLayer(true); }
        public static void JobTrackerPostfix(long __state) { EndLayer(JobTracker, __state); }
        public static void PatherPrefix(ref long __state) { __state = BeginLayer(true); }
        public static void PatherPostfix(long __state) { EndLayer(Pather, __state); }

        public static void RootUpdatePrefix(ref long __state) { __state = BeginDirect(); }
        public static void RootUpdatePostfix(long __state) { EndDirect(RootPlayUpdate, __state); }
        public static void MapUpdatePrefix(ref long __state) { __state = BeginDirect(); }
        public static void MapUpdatePostfix(long __state) { EndDirect(MapUpdate, __state); }
        public static void MapComponentUpdatePrefix(ref long __state) { __state = BeginDirect(); }
        public static void MapComponentUpdatePostfix(long __state) { EndDirect(MapComponentUpdate, __state); }
        public static void GameComponentUpdatePrefix(ref long __state) { __state = BeginDirect(); }
        public static void GameComponentUpdatePostfix(long __state) { EndDirect(GameComponentUpdate, __state); }
        public static void WorldComponentUpdatePrefix(ref long __state) { __state = BeginDirect(); }
        public static void WorldComponentUpdatePostfix(long __state) { EndDirect(WorldComponentUpdate, __state); }
        public static void DynamicDrawPrefix(ref long __state) { __state = BeginDirect(); }
        public static void DynamicDrawPostfix(long __state) { EndDirect(DynamicDraw, __state); }

        private static long BeginLayer(bool detailOnly)
        {
            if (!frameActive || (detailOnly && !detailActive))
                return 0L;
            return Stopwatch.GetTimestamp();
        }

        private static void EndLayer(int layer, long start)
        {
            if (start == 0L || !frameActive || currentTicks == null || layer < 0 || layer >= currentTicks.Length)
                return;
            currentTicks[layer] += Stopwatch.GetTimestamp() - start;
            currentCalls[layer]++;
        }

        private static long BeginDirect()
        {
            if (!FeatureGate.IsEnabled(FeatureId) || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || RuntimeCompatibility.ButterPlusPlusActive)
                return 0L;
            return Stopwatch.GetTimestamp();
        }

        private static void EndDirect(int layer, long start)
        {
            if (start == 0L || layer < 0 || layer >= UpdateStats.Length)
                return;
            long elapsed = Stopwatch.GetTimestamp() - start;
            lock (Sync)
                UpdateStats[layer].Record(elapsed);
        }

        private static void EnsureThreadBuffers()
        {
            if (currentTicks == null || currentTicks.Length != TickLayerCount)
                currentTicks = new long[TickLayerCount];
            if (currentCalls == null || currentCalls.Length != TickLayerCount)
                currentCalls = new int[TickLayerCount];
        }

        private static void Patch(Harmony harmony, string typeName, string methodName, string prefixName, string postfixName, int prefixPriority = Priority.Normal, int postfixPriority = Priority.Normal)
        {
            try
            {
                Type type = AccessTools.TypeByName(typeName);
                MethodBase target = type == null ? null : AccessTools.Method(type, methodName);
                if (target == null)
                {
                    missingTargets++;
                    Log.Message("[RimMT] tick-layer profiler optional target not found: " + typeName + "." + methodName);
                    return;
                }

                HarmonyMethod prefix = string.IsNullOrEmpty(prefixName) ? null : new HarmonyMethod(typeof(TickLayerProfiler04182P1), prefixName);
                HarmonyMethod postfix = string.IsNullOrEmpty(postfixName) ? null : new HarmonyMethod(typeof(TickLayerProfiler04182P1), postfixName);
                if (prefix != null) prefix.priority = prefixPriority;
                if (postfix != null) postfix.priority = postfixPriority;
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patchedTargets++;
            }
            catch (Exception ex)
            {
                patchFailures++;
                Log.Warning("[RimMT] tick-layer profiler could not patch " + typeName + "." + methodName + ": " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static void PatchFirstAvailable(Harmony harmony, string[] typeNames, string methodName, string prefixName, string postfixName)
        {
            if (typeNames == null)
                return;
            for (int i = 0; i < typeNames.Length; i++)
            {
                Type type = AccessTools.TypeByName(typeNames[i]);
                MethodBase target = type == null ? null : AccessTools.Method(type, methodName);
                if (target == null)
                    continue;
                try
                {
                    harmony.Patch(target,
                        prefix: new HarmonyMethod(typeof(TickLayerProfiler04182P1), prefixName),
                        postfix: new HarmonyMethod(typeof(TickLayerProfiler04182P1), postfixName));
                    patchedTargets++;
                    return;
                }
                catch (Exception ex)
                {
                    patchFailures++;
                    Log.Warning("[RimMT] tick-layer profiler could not patch " + typeNames[i] + "." + methodName + ": " + ex.GetType().Name + ": " + ex.Message);
                    return;
                }
            }
            missingTargets++;
            Log.Message("[RimMT] tick-layer profiler optional target not found: LordManagerTick");
        }

        private static FrameStat[] CreateStats(int count)
        {
            FrameStat[] result = new FrameStat[count];
            for (int i = 0; i < count; i++) result[i] = new FrameStat();
            return result;
        }

        private static DirectStat[] CreateDirectStats(int count)
        {
            DirectStat[] result = new DirectStat[count];
            for (int i = 0; i < count; i++) result[i] = new DirectStat();
            return result;
        }

        private static void AppendFrameStat(StringBuilder sb, string name, FrameStat stat, bool sampled)
        {
            sb.Append(" * ").Append(name).Append(": frames=").Append(stat.Frames)
                .Append(", calls=").Append(stat.Calls)
                .Append(", avgCalls/frame=").Append(stat.Frames == 0 ? "0.00" : ((double)stat.Calls / stat.Frames).ToString("F2"))
                .Append(", avgMs/frame=").Append(stat.AverageMs.ToString("F3"))
                .Append(", p50~=").Append(stat.Percentile(0.50).ToString("F3"))
                .Append(", p95~=").Append(stat.Percentile(0.95).ToString("F3"))
                .Append(", p99~=").Append(stat.Percentile(0.99).ToString("F3"))
                .Append(", maxMs=").Append(stat.MaxMs.ToString("F3"))
                .Append(", >16/32/64ms=").Append(stat.Over16).Append('/').Append(stat.Over32).Append('/').Append(stat.Over64);
            if (sampled) sb.Append(" [sampled tick distribution]");
            sb.AppendLine();
        }

        private static void AppendDirectStat(StringBuilder sb, string name, DirectStat stat)
        {
            sb.Append(" * ").Append(name).Append(": calls=").Append(stat.Calls)
                .Append(", avgMs=").Append(stat.AverageMs.ToString("F3"))
                .Append(", p50~=").Append(stat.Percentile(0.50).ToString("F3"))
                .Append(", p95~=").Append(stat.Percentile(0.95).ToString("F3"))
                .Append(", p99~=").Append(stat.Percentile(0.99).ToString("F3"))
                .Append(", maxMs=").Append(stat.MaxMs.ToString("F3"))
                .Append(", >16/32/64ms=").Append(stat.Over16).Append('/').Append(stat.Over32).Append('/').Append(stat.Over64)
                .AppendLine();
        }

        private static readonly double[] BucketUpperMs =
        {
            0.125, 0.25, 0.5, 1.0, 2.0, 4.0, 8.0, 12.0, 16.0, 24.0,
            32.0, 48.0, 64.0, 96.0, 128.0, 192.0, 256.0, 384.0, 512.0, 768.0, 1024.0, 2048.0, 4096.0
        };

        private static int BucketFor(long ticks)
        {
            double ms = ticks * 1000.0 / Stopwatch.Frequency;
            for (int i = 0; i < BucketUpperMs.Length; i++)
                if (ms <= BucketUpperMs[i]) return i;
            return BucketUpperMs.Length - 1;
        }

        private sealed class FrameStat
        {
            internal long Frames;
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long Over16;
            internal long Over32;
            internal long Over64;
            internal readonly long[] Buckets = new long[BucketUpperMs.Length];

            internal double AverageMs { get { return Frames == 0 ? 0.0 : TotalTicks * 1000.0 / Stopwatch.Frequency / Frames; } }
            internal double MaxMs { get { return MaxTicks * 1000.0 / Stopwatch.Frequency; } }

            internal void Record(long ticks, int calls)
            {
                Frames++;
                Calls += calls;
                TotalTicks += ticks;
                if (ticks > MaxTicks) MaxTicks = ticks;
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                if (ms > 16.0) Over16++;
                if (ms > 32.0) Over32++;
                if (ms > 64.0) Over64++;
                Buckets[BucketFor(ticks)]++;
            }

            internal double Percentile(double percentile)
            {
                if (Frames == 0) return 0.0;
                long target = (long)Math.Ceiling(Frames * percentile);
                long cumulative = 0;
                for (int i = 0; i < Buckets.Length; i++)
                {
                    cumulative += Buckets[i];
                    if (cumulative >= target) return BucketUpperMs[i];
                }
                return BucketUpperMs[BucketUpperMs.Length - 1];
            }
        }

        private sealed class DirectStat
        {
            internal long Calls;
            internal long TotalTicks;
            internal long MaxTicks;
            internal long Over16;
            internal long Over32;
            internal long Over64;
            internal readonly long[] Buckets = new long[BucketUpperMs.Length];

            internal double AverageMs { get { return Calls == 0 ? 0.0 : TotalTicks * 1000.0 / Stopwatch.Frequency / Calls; } }
            internal double MaxMs { get { return MaxTicks * 1000.0 / Stopwatch.Frequency; } }

            internal void Record(long ticks)
            {
                Calls++;
                TotalTicks += ticks;
                if (ticks > MaxTicks) MaxTicks = ticks;
                double ms = ticks * 1000.0 / Stopwatch.Frequency;
                if (ms > 16.0) Over16++;
                if (ms > 32.0) Over32++;
                if (ms > 64.0) Over64++;
                Buckets[BucketFor(ticks)]++;
            }

            internal double Percentile(double percentile)
            {
                if (Calls == 0) return 0.0;
                long target = (long)Math.Ceiling(Calls * percentile);
                long cumulative = 0;
                for (int i = 0; i < Buckets.Length; i++)
                {
                    cumulative += Buckets[i];
                    if (cumulative >= target) return BucketUpperMs[i];
                }
                return BucketUpperMs[BucketUpperMs.Length - 1];
            }
        }
    }
}
