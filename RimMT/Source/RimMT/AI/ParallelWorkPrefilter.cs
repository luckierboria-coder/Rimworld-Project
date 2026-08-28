using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    // V0.4.17: asynchronous WorkGiver cell prefilter.
    //
    // JobGiver_Work performs cell scanners sequentially on the main thread. For each candidate
    // cell it calls HasJobOnCell (or, for GrowerSow, the base HasJobOnCell which immediately
    // calls JobOnCell). In a large colony those calls can contain plant-grid lookups,
    // temperature/growth checks, reservations and additional reachability work.
    //
    // This module does NOT replace JobGiver_Work, alter priority ordering, create jobs, reserve
    // targets or wait for worker results. The original PotentialWorkCellsGlobal enumeration is
    // allowed to run normally; a tiny wrapper records the exact cells already being enumerated.
    // After that scan finishes the recorded IntVec3 list is split across RimMT workers. Workers
    // perform a deliberately bounded set of read-only live checks and publish only
    // "definitely negative" hints. A later main-thread HasJob/JobOnCell call may skip Vanilla
    // only for a published negative. Every new slot warms up against Vanilla and continues to
    // shadow-sample; a false negative clears the snapshot, cools the slot down and contributes
    // to a global fuse.
    //
    // Unlike the earlier immutable-only modules, V0.4.17 intentionally allows a narrow set of
    // live Verse reads on worker threads (plant/zone/roof/temperature state). This is the
    // bounded-risk step requested for real CPU offload. Worker exceptions are contained per
    // cell and turn that cell into "unknown"; workers never write Verse state.
    internal static class ParallelWorkPrefilter
    {
        internal const string FeatureId = "parallel.workPrefilter";

        private const int MinCapturedCells = 24;
        private const int MaxCapturedCells = 160000;
        private const long BuildCooldownFrames = 8;
        private const long MaxSnapshotAgeFrames = 45;
        private const long PublishAgeLimitFrames = 90;
        private const long MismatchCooldownFrames = 300;
        private const int WarmupSamples = 16;
        private const int SampleMask = 31; // 1/32 after warmup.
        private const int GlobalMismatchFuse = 8;
        private const int MaxMismatchLogs = 8;

        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();

        private static volatile bool compatibilityReady;
        private static int sowCompatible = 1;
        private static int harvestCompatible = 1;
        private static int roofCompatible = 1;

        private static MethodBase growerCellsTarget;
        private static MethodBase roofCellsTarget;
        private static MethodBase sowJobTarget;
        private static MethodBase harvestHasJobTarget;
        private static MethodBase roofHasJobTarget;
        private static MethodBase baseHasJobTarget;

        private static long sourceEnumerations;
        private static long sourceCellsCaptured;
        private static long sourceOverflow;
        private static long buildsScheduled;
        private static long buildsPublished;
        private static long buildsRejected;
        private static long buildsStale;
        private static long workerBatches;
        private static long workerCellsEvaluated;
        private static long workerReadFaults;
        private static long workerTicks;
        private static long workerTicksMax;
        private static long lookups;
        private static long snapshotMisses;
        private static long snapshotExpired;
        private static long negativeHits;
        private static long cooldownBypass;
        private static long authoritativeFalse;
        private static long shadowSamples;
        private static long shadowMatches;
        private static long parityMismatches;
        private static long sowNegatives;
        private static long harvestNegatives;
        private static long roofNegatives;
        private static long sowMismatches;
        private static long harvestMismatches;
        private static long roofMismatches;
        private static long mismatchLogs;

        [Flags]
        private enum NegativeReason : ushort
        {
            None = 0,

            // GrowerSow
            NoDesiredPlant = 1 << 0,
            SameDesiredPlantPresent = 1 << 1,
            OutOfGrowthSeason = 1 << 2,

            // GrowerHarvest
            MissingPlant = 1 << 4,
            HarvestNotReady = 1 << 5,
            ManualHarvestOnly = 1 << 6,

            // BuildRoof
            AlreadyRoofed = 1 << 8,
            NotInBuildRoofArea = 1 << 9
        }

        private enum WorkKind
        {
            Sow = 0,
            Harvest = 1,
            BuildRoof = 2
        }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                growerCellsTarget = AccessTools.Method(typeof(WorkGiver_Grower), nameof(WorkGiver_Grower.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
                roofCellsTarget = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.PotentialWorkCellsGlobal), new Type[] { typeof(Pawn) });
                sowJobTarget = AccessTools.Method(typeof(WorkGiver_GrowerSow), nameof(WorkGiver_GrowerSow.JobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                harvestHasJobTarget = AccessTools.Method(typeof(WorkGiver_GrowerHarvest), nameof(WorkGiver_GrowerHarvest.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                roofHasJobTarget = AccessTools.Method(typeof(WorkGiver_BuildRoof), nameof(WorkGiver_BuildRoof.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });
                baseHasJobTarget = AccessTools.Method(typeof(WorkGiver_Scanner), nameof(WorkGiver_Scanner.HasJobOnCell), new Type[] { typeof(Pawn), typeof(IntVec3), typeof(bool) });

                if (growerCellsTarget == null || roofCellsTarget == null || sowJobTarget == null ||
                    harvestHasJobTarget == null || roofHasJobTarget == null || baseHasJobTarget == null)
                {
                    FeatureGate.Suppress(FeatureId, "V0.4.17 WorkGiver target lookup failed");
                    Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 unavailable: one or more WorkGiver targets were not found for RimWorld 1.5.4063.");
                    return;
                }

                harmony.Patch(growerCellsTarget,
                    postfix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(GrowerCellsPostfix)) { priority = Priority.Last });
                harmony.Patch(roofCellsTarget,
                    postfix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(RoofCellsPostfix)) { priority = Priority.Last });

                harmony.Patch(sowJobTarget,
                    prefix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(SowJobPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(SowJobPostfix)) { priority = Priority.Last });
                harmony.Patch(harvestHasJobTarget,
                    prefix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(HarvestHasJobPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(BoolHasJobPostfix)) { priority = Priority.Last });
                harmony.Patch(roofHasJobTarget,
                    prefix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(RoofHasJobPrefix)) { priority = Priority.First },
                    postfix: new HarmonyMethod(typeof(ParallelWorkPrefilter), nameof(BoolHasJobPostfix)) { priority = Priority.Last });

                Log.Message("[RimMT] parallel.workPrefilter V0.4.17 installed. GrowerSow, GrowerHarvest and BuildRoof cell sources are recorded without blocking; live read-only negative classification runs on worker batches; Vanilla keeps ordering, final HasJob/JobOnCell authority for every unknown/positive, reservations and job commit.");
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress(FeatureId, "V0.4.17 Work prefilter patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 patch failed; Vanilla WorkGiver scanning remains authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void MarkCompatibilityReady()
        {
            if (compatibilityReady)
                return;

            sowCompatible = IsTargetCompatible(sowJobTarget, "GrowerSow.JobOnCell") &&
                            IsTargetCompatible(baseHasJobTarget, "WorkGiver_Scanner.HasJobOnCell") ? 1 : 0;
            harvestCompatible = IsTargetCompatible(harvestHasJobTarget, "GrowerHarvest.HasJobOnCell") ? 1 : 0;
            roofCompatible = IsTargetCompatible(roofHasJobTarget, "BuildRoof.HasJobOnCell") ? 1 : 0;
            compatibilityReady = true;

            Log.Message("[RimMT] parallel.workPrefilter V0.4.17 compatibility: sow=" + (sowCompatible != 0) +
                ", harvest=" + (harvestCompatible != 0) + ", buildRoof=" + (roofCompatible != 0) +
                ". Foreign Harmony patches on an authoritative HasJob/JobOnCell target disable that kind only; source-enumeration postfixes remain non-authoritative.");
        }

        private static bool IsTargetCompatible(MethodBase target, string label)
        {
            if (target == null)
                return false;

            try
            {
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                    return true;

                string blocker;
                if (FindForeign(info.Prefixes, out blocker) || FindForeign(info.Postfixes, out blocker) ||
                    FindForeign(info.Transpilers, out blocker) || FindForeign(info.Finalizers, out blocker))
                {
                    Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 disables " + label +
                        " fast-negative authority because foreign Harmony patch '" + blocker + "' also owns the method. Vanilla remains authoritative for this WorkGiver kind.");
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 compatibility check failed for " + label +
                    "; this kind stays Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static bool FindForeign(IList<Patch> patches, out string blocker)
        {
            blocker = null;
            if (patches == null)
                return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                    continue;
                MethodInfo method = patch.PatchMethod;
                blocker = (patch.owner ?? "<unknown-owner>") + " :: " +
                    (method == null || method.DeclaringType == null ? "<unknown-method>" : method.DeclaringType.FullName + "." + method.Name);
                return true;
            }
            return false;
        }

        public static void GrowerCellsPostfix(WorkGiver_Grower __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (__result == null || __instance == null || pawn == null || !CanCapture())
                return;

            WorkKind kind;
            Type type = __instance.GetType();
            if (type == typeof(WorkGiver_GrowerSow))
                kind = WorkKind.Sow;
            else if (type == typeof(WorkGiver_GrowerHarvest))
                kind = WorkKind.Harvest;
            else
                return;

            if (!KindCompatible(kind) || pawn.Map == null)
                return;
            __result = CaptureCells(__result, pawn.Map, kind);
        }

        public static void RoofCellsPostfix(WorkGiver_BuildRoof __instance, Pawn pawn, ref IEnumerable<IntVec3> __result)
        {
            if (__result == null || __instance == null || pawn == null || pawn.Map == null || !CanCapture() || !KindCompatible(WorkKind.BuildRoof))
                return;
            if (__instance.GetType() != typeof(WorkGiver_BuildRoof))
                return;
            __result = CaptureCells(__result, pawn.Map, WorkKind.BuildRoof);
        }

        private static bool CanCapture()
        {
            return compatibilityReady && FeatureGate.IsEnabled(FeatureId) && !CircuitBreaker.IsOpen(FeatureId) &&
                RimMTThreadGuard.IsMainThread && Current.ProgramState == ProgramState.Playing;
        }

        private static IEnumerable<IntVec3> CaptureCells(IEnumerable<IntVec3> source, Map map, WorkKind kind)
        {
            List<IntVec3> captured = new List<IntVec3>(256);
            bool overflow = false;
            try
            {
                foreach (IntVec3 cell in source)
                {
                    if (!overflow)
                    {
                        if (captured.Count < MaxCapturedCells)
                            captured.Add(cell);
                        else
                        {
                            overflow = true;
                            Interlocked.Increment(ref sourceOverflow);
                        }
                    }
                    yield return cell;
                }
            }
            finally
            {
                Interlocked.Increment(ref sourceEnumerations);
                Interlocked.Add(ref sourceCellsCaptured, captured.Count);
                if (!overflow && captured.Count >= MinCapturedCells && map != null && !map.Disposed && CanCapture())
                    ScheduleBuild(map, kind, captured.ToArray());
            }
        }

        private static void ScheduleBuild(Map map, WorkKind kind, IntVec3[] cells)
        {
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            if (scheduler == null || cells == null || cells.Length < MinCapturedCells || !KindCompatible(kind))
                return;

            MapState mapState = MapStates.GetValue(map, delegate(Map m)
            {
                return new MapState(m.uniqueID, m.Size.x, m.Size.z);
            });
            Slot slot = mapState.GetSlot(kind);
            long now = RimMTRuntime.MainThreadFrames;
            if (Interlocked.Read(ref slot.DisabledUntilFrame) > now)
                return;

            long last = Interlocked.Read(ref slot.LastScheduleFrame);
            if (last != 0L && now - last < BuildCooldownFrames)
                return;
            if (Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0)
                return;

            Interlocked.Exchange(ref slot.LastScheduleFrame, now);
            Interlocked.Increment(ref buildsScheduled);

            int width = map.Size.x;
            int height = map.Size.z;
            int mapId = map.uniqueID;
            ushort[] reasons = new ushort[width * height];
            int workers = Math.Max(1, scheduler.WorkerCount);
            int desiredBatches = Math.Max(1, Math.Min(cells.Length, workers * 2));
            int batchSize = (cells.Length + desiredBatches - 1) / desiredBatches;
            if (batchSize < 16) batchSize = 16;
            if (batchSize > 256) batchSize = 256;
            long captureFrame = now;

            bool accepted = scheduler.ParallelFor(
                FeatureId,
                0,
                cells.Length,
                batchSize,
                delegate(int from, int to)
                {
                    long started = Stopwatch.GetTimestamp();
                    long localFaults = 0L;
                    try
                    {
                        for (int i = from; i < to; i++)
                        {
                            IntVec3 c = cells[i];
                            if (c.x < 0 || c.z < 0 || c.x >= width || c.z >= height)
                                continue;

                            try
                            {
                                NegativeReason reason = EvaluateLiveNegative(map, kind, c);
                                if (reason != NegativeReason.None)
                                    reasons[c.x + c.z * width] = (ushort)reason;
                            }
                            catch
                            {
                                // A concurrent ThingGrid/zone/roof mutation must never escape
                                // the worker. Unknown means the main thread simply runs Vanilla.
                                localFaults++;
                            }
                        }
                    }
                    catch
                    {
                        // Body must not throw: JobScheduler.ParallelFor completion bookkeeping is
                        // intentionally kept intact even if an unexpected read path fails.
                        localFaults += Math.Max(1, to - from);
                    }
                    finally
                    {
                        long elapsed = Stopwatch.GetTimestamp() - started;
                        Interlocked.Increment(ref workerBatches);
                        Interlocked.Add(ref workerCellsEvaluated, Math.Max(0, to - from));
                        if (localFaults != 0L)
                            Interlocked.Add(ref workerReadFaults, localFaults);
                        Interlocked.Add(ref workerTicks, elapsed);
                        UpdateMax(ref workerTicksMax, elapsed);
                    }
                },
                delegate
                {
                    Volatile.Write(ref slot.BuildScheduled, 0);
                    if (!FeatureGate.IsEnabled(FeatureId) || map == null || map.Disposed || map.uniqueID != mapId ||
                        map.Size.x != width || map.Size.z != height || RimMTRuntime.MainThreadFrames - captureFrame > PublishAgeLimitFrames)
                    {
                        Interlocked.Increment(ref buildsStale);
                        return;
                    }

                    Volatile.Write(ref slot.Published, new Snapshot(mapId, width, height, captureFrame, kind, reasons));
                    Interlocked.Increment(ref buildsPublished);
                },
                JobPriority.Normal);

            if (!accepted)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsRejected);
            }
        }

        private static NegativeReason EvaluateLiveNegative(Map map, WorkKind kind, IntVec3 c)
        {
            if (map == null || map.Disposed)
                return NegativeReason.None;

            switch (kind)
            {
                case WorkKind.Sow:
                {
                    // All three conditions are early hard-fail paths in Vanilla
                    // WorkGiver_GrowerSow.JobOnCell. No reservation/job state is touched.
                    ThingDef wanted = WorkGiver_Grower.CalculateWantedPlantDef(c, map);
                    if (wanted == null)
                        return NegativeReason.NoDesiredPlant;

                    NegativeReason result = NegativeReason.None;
                    if (!PlantUtility.GrowthSeasonNow(c, map, true))
                        result |= NegativeReason.OutOfGrowthSeason;
                    Plant plant = c.GetPlant(map);
                    if (plant != null && plant.def == wanted)
                        result |= NegativeReason.SameDesiredPlantPresent;
                    return result;
                }

                case WorkKind.Harvest:
                {
                    Plant plant = c.GetPlant(map);
                    if (plant == null)
                        return NegativeReason.MissingPlant;

                    NegativeReason result = NegativeReason.None;
                    if (!plant.HarvestableNow || plant.LifeStage != PlantLifeStage.Mature || !plant.CanYieldNow())
                        result |= NegativeReason.HarvestNotReady;
                    if (plant.def != null && plant.def.plant != null && !plant.def.plant.autoHarvestable)
                        result |= NegativeReason.ManualHarvestOnly;
                    return result;
                }

                case WorkKind.BuildRoof:
                {
                    NegativeReason result = NegativeReason.None;
                    if (!map.areaManager.BuildRoof[c])
                        result |= NegativeReason.NotInBuildRoofArea;
                    if (c.Roofed(map))
                        result |= NegativeReason.AlreadyRoofed;
                    return result;
                }
            }

            return NegativeReason.None;
        }

        public static bool SowJobPrefix(Pawn pawn, IntVec3 c, bool forced, ref Job __result, out SampleState __state)
        {
            __state = default(SampleState);
            if (!KindCompatible(WorkKind.Sow))
                return true;

            FastNegativeDecision decision;
            if (!TryFastNegative(pawn, c, forced, WorkKind.Sow, out decision))
                return true;

            if (decision.Sample)
            {
                __state = decision.State;
                return true;
            }

            __result = null;
            Interlocked.Increment(ref authoritativeFalse);
            Interlocked.Increment(ref sowNegatives);
            return false;
        }

        public static void SowJobPostfix(Job __result, SampleState __state)
        {
            if (!__state.Active)
                return;
            RecordSample(__state, __result != null);
        }

        public static bool HarvestHasJobPrefix(Pawn pawn, IntVec3 c, bool forced, ref bool __result, out SampleState __state)
        {
            __state = default(SampleState);
            if (!KindCompatible(WorkKind.Harvest))
                return true;

            FastNegativeDecision decision;
            if (!TryFastNegative(pawn, c, forced, WorkKind.Harvest, out decision))
                return true;

            if (decision.Sample)
            {
                __state = decision.State;
                return true;
            }

            __result = false;
            Interlocked.Increment(ref authoritativeFalse);
            Interlocked.Increment(ref harvestNegatives);
            return false;
        }

        public static bool RoofHasJobPrefix(Pawn pawn, IntVec3 c, bool forced, ref bool __result, out SampleState __state)
        {
            __state = default(SampleState);
            if (!KindCompatible(WorkKind.BuildRoof))
                return true;

            FastNegativeDecision decision;
            if (!TryFastNegative(pawn, c, forced, WorkKind.BuildRoof, out decision))
                return true;

            if (decision.Sample)
            {
                __state = decision.State;
                return true;
            }

            __result = false;
            Interlocked.Increment(ref authoritativeFalse);
            Interlocked.Increment(ref roofNegatives);
            return false;
        }

        public static void BoolHasJobPostfix(bool __result, SampleState __state)
        {
            if (!__state.Active)
                return;
            RecordSample(__state, __result);
        }

        private static bool TryFastNegative(Pawn pawn, IntVec3 c, bool forced, WorkKind kind, out FastNegativeDecision decision)
        {
            decision = default(FastNegativeDecision);
            Interlocked.Increment(ref lookups);

            if (!compatibilityReady || !FeatureGate.IsEnabled(FeatureId) || CircuitBreaker.IsOpen(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || pawn == null)
                return false;

            Map map = pawn.Map;
            if (map == null || map.Disposed || !c.IsValid || !c.InBounds(map))
                return false;

            MapState mapState;
            if (!MapStates.TryGetValue(map, out mapState))
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            Slot slot = mapState.GetSlot(kind);
            long now = RimMTRuntime.MainThreadFrames;
            if (Interlocked.Read(ref slot.DisabledUntilFrame) > now)
            {
                Interlocked.Increment(ref cooldownBypass);
                return false;
            }

            Snapshot snapshot = Volatile.Read(ref slot.Published);
            if (snapshot == null || snapshot.MapId != map.uniqueID || snapshot.Width != map.Size.x ||
                snapshot.Height != map.Size.z || snapshot.Kind != kind)
            {
                Interlocked.Increment(ref snapshotMisses);
                return false;
            }

            if (now - snapshot.CaptureFrame > MaxSnapshotAgeFrames)
            {
                Interlocked.Increment(ref snapshotExpired);
                return false;
            }

            int index = c.x + c.z * snapshot.Width;
            if (index < 0 || index >= snapshot.Reasons.Length)
                return false;
            NegativeReason reason = (NegativeReason)snapshot.Reasons[index];
            if (reason == NegativeReason.None)
                return false;

            // Forced harvests are allowed to harvest plants whose def is not autoHarvestable.
            // If that is the only worker-negative reason, forced callers must stay Vanilla.
            if (kind == WorkKind.Harvest && forced)
            {
                reason &= ~NegativeReason.ManualHarvestOnly;
                if (reason == NegativeReason.None)
                    return false;
            }

            Interlocked.Increment(ref negativeHits);
            int validated = Volatile.Read(ref slot.ValidatedMatches);
            int serial = Interlocked.Increment(ref slot.PredictionSerial);
            bool sample = validated < WarmupSamples || (serial & SampleMask) == 0;
            if (sample)
                Interlocked.Increment(ref shadowSamples);

            SampleState state = new SampleState(true, kind, c, reason, slot, snapshot.CaptureFrame);
            decision = new FastNegativeDecision(sample, state);
            return true;
        }

        private static void RecordSample(SampleState state, bool actualHasJob)
        {
            if (!state.Active || state.Slot == null)
                return;

            if (!actualHasJob)
            {
                Interlocked.Increment(ref shadowMatches);
                Interlocked.Increment(ref state.Slot.ValidatedMatches);
                return;
            }

            long mismatches = Interlocked.Increment(ref parityMismatches);
            switch (state.Kind)
            {
                case WorkKind.Sow: Interlocked.Increment(ref sowMismatches); break;
                case WorkKind.Harvest: Interlocked.Increment(ref harvestMismatches); break;
                case WorkKind.BuildRoof: Interlocked.Increment(ref roofMismatches); break;
            }

            Interlocked.Exchange(ref state.Slot.ValidatedMatches, 0);
            Interlocked.Exchange(ref state.Slot.DisabledUntilFrame, RimMTRuntime.MainThreadFrames + MismatchCooldownFrames);
            Volatile.Write(ref state.Slot.Published, null);

            long logIndex = Interlocked.Increment(ref mismatchLogs);
            if (logIndex <= MaxMismatchLogs)
            {
                long age = RimMTRuntime.MainThreadFrames - state.CaptureFrame;
                Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 false-negative parity mismatch #" + mismatches +
                    ": kind=" + state.Kind + ", cell=" + state.Cell + ", reason=" + state.Reason +
                    ", snapshotAgeFrames=" + age + ". This slot is cooled for " + MismatchCooldownFrames +
                    " frames and the current result remains Vanilla-authoritative.");
            }

            if (mismatches >= GlobalMismatchFuse)
            {
                FeatureGate.Suppress(FeatureId, "V0.4.17 Work prefilter parity fuse: " + mismatches + " sampled false negatives");
                Log.Warning("[RimMT] parallel.workPrefilter V0.4.17 disabled by parity fuse after " + mismatches +
                    " sampled false negatives. Vanilla WorkGiver scanning is authoritative again.");
            }
        }

        private static bool KindCompatible(WorkKind kind)
        {
            switch (kind)
            {
                case WorkKind.Sow: return Volatile.Read(ref sowCompatible) != 0;
                case WorkKind.Harvest: return Volatile.Read(ref harvestCompatible) != 0;
                case WorkKind.BuildRoof: return Volatile.Read(ref roofCompatible) != 0;
                default: return false;
            }
        }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, seen) == seen)
                    break;
            }
        }

        internal static string Summary()
        {
            long evaluated = Interlocked.Read(ref workerCellsEvaluated);
            long batches = Interlocked.Read(ref workerBatches);
            double avgCellUs = evaluated == 0 ? 0.0 : (Interlocked.Read(ref workerTicks) * 1000000.0 / Stopwatch.Frequency) / evaluated;
            double avgBatchUs = batches == 0 ? 0.0 : (Interlocked.Read(ref workerTicks) * 1000000.0 / Stopwatch.Frequency) / batches;
            double maxBatchUs = Interlocked.Read(ref workerTicksMax) * 1000000.0 / Stopwatch.Frequency;

            return "Parallel Work prefilter V0.4.17: compatibilityReady=" + compatibilityReady +
                ", kindCompat(sow/harvest/roof)=" + (sowCompatible != 0) + "/" + (harvestCompatible != 0) + "/" + (roofCompatible != 0) +
                ", sourceEnumerations=" + Interlocked.Read(ref sourceEnumerations) +
                ", sourceCellsCaptured=" + Interlocked.Read(ref sourceCellsCaptured) +
                ", sourceOverflow=" + Interlocked.Read(ref sourceOverflow) +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
                ", buildsPublished=" + Interlocked.Read(ref buildsPublished) +
                ", buildsRejected=" + Interlocked.Read(ref buildsRejected) +
                ", buildsStale=" + Interlocked.Read(ref buildsStale) +
                ", workerBatches=" + batches +
                ", workerCells=" + evaluated +
                ", workerReadFaults=" + Interlocked.Read(ref workerReadFaults) +
                ", lookups=" + Interlocked.Read(ref lookups) +
                ", snapshotMisses=" + Interlocked.Read(ref snapshotMisses) +
                ", snapshotExpired=" + Interlocked.Read(ref snapshotExpired) +
                ", negativeHits=" + Interlocked.Read(ref negativeHits) +
                ", cooldownBypass=" + Interlocked.Read(ref cooldownBypass) +
                ", authoritativeFalse=" + Interlocked.Read(ref authoritativeFalse) +
                ", fastNegatives(sow/harvest/roof)=" + Interlocked.Read(ref sowNegatives) + "/" + Interlocked.Read(ref harvestNegatives) + "/" + Interlocked.Read(ref roofNegatives) +
                ", shadowSamples=" + Interlocked.Read(ref shadowSamples) +
                ", shadowMatches=" + Interlocked.Read(ref shadowMatches) +
                ", parityMismatches=" + Interlocked.Read(ref parityMismatches) +
                " (sow/harvest/roof=" + Interlocked.Read(ref sowMismatches) + "/" + Interlocked.Read(ref harvestMismatches) + "/" + Interlocked.Read(ref roofMismatches) + ")" +
                ", warmupSamples=" + WarmupSamples +
                ", sampleEvery=" + (SampleMask + 1) +
                ", maxSnapshotAgeFrames=" + MaxSnapshotAgeFrames +
                ", avgWorkerCellUs=" + avgCellUs.ToString("F3") +
                ", avgWorkerBatchUs=" + avgBatchUs.ToString("F2") +
                ", maxWorkerBatchUs=" + maxBatchUs.ToString("F2") +
                ". No worker job creation/reservation/state mutation; unknown/positive cells and every sampled negative remain Vanilla main-thread authoritative.";
        }

        private sealed class MapState
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly Slot Sow = new Slot();
            internal readonly Slot Harvest = new Slot();
            internal readonly Slot Roof = new Slot();

            internal MapState(int mapId, int width, int height)
            {
                MapId = mapId;
                Width = width;
                Height = height;
            }

            internal Slot GetSlot(WorkKind kind)
            {
                switch (kind)
                {
                    case WorkKind.Sow: return Sow;
                    case WorkKind.Harvest: return Harvest;
                    case WorkKind.BuildRoof: return Roof;
                    default: return Sow;
                }
            }
        }

        private sealed class Slot
        {
            internal int BuildScheduled;
            internal long LastScheduleFrame;
            internal long DisabledUntilFrame;
            internal int ValidatedMatches;
            internal int PredictionSerial;
            internal Snapshot Published;
        }

        private sealed class Snapshot
        {
            internal readonly int MapId;
            internal readonly int Width;
            internal readonly int Height;
            internal readonly long CaptureFrame;
            internal readonly WorkKind Kind;
            internal readonly ushort[] Reasons;

            internal Snapshot(int mapId, int width, int height, long captureFrame, WorkKind kind, ushort[] reasons)
            {
                MapId = mapId;
                Width = width;
                Height = height;
                CaptureFrame = captureFrame;
                Kind = kind;
                Reasons = reasons;
            }
        }

        public struct SampleState
        {
            internal readonly bool Active;
            internal readonly WorkKind Kind;
            internal readonly IntVec3 Cell;
            internal readonly NegativeReason Reason;
            internal readonly Slot Slot;
            internal readonly long CaptureFrame;

            internal SampleState(bool active, WorkKind kind, IntVec3 cell, NegativeReason reason, Slot slot, long captureFrame)
            {
                Active = active;
                Kind = kind;
                Cell = cell;
                Reason = reason;
                Slot = slot;
                CaptureFrame = captureFrame;
            }
        }

        private struct FastNegativeDecision
        {
            internal readonly bool Sample;
            internal readonly SampleState State;

            internal FastNegativeDecision(bool sample, SampleState state)
            {
                Sample = sample;
                State = state;
            }
        }
    }
}
