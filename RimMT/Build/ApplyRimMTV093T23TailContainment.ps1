$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T23 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

function Replace-Between-OrThrow {
    param([string]$Text,[string]$Start,[string]$End,[string]$Replacement,[string]$Label)
    $a = $Text.IndexOf($Start, [System.StringComparison]::Ordinal)
    if ($a -lt 0) { throw "RimMT V0.9.3-T23 start anchor not found: $Label" }
    $b = $Text.IndexOf($End, $a + $Start.Length, [System.StringComparison]::Ordinal)
    if ($b -lt 0) { throw "RimMT V0.9.3-T23 end anchor not found: $Label" }
    return $Text.Substring(0, $a) + $Replacement + $Text.Substring($b)
}

# T23 goals:
# 1) preserve T22 Foundation III GenClosest + Root_Play SMF dispatcher bridge;
# 2) bring World Root attribution into the same build;
# 3) remove monolithic Region.Allows profile capture from Reachability.CanReach;
# 4) slice profile capture at Root_Play.Update with fail-open stale/watchdog handling;
# 5) fix the WorldRoot TraitDef reflection overload ambiguity without allowing one optional probe to abort WTL setup.

$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach = Get-Content $reachPath -Raw

$reach = Replace-OrThrow $reach @'
        private const int SliceCheckMask = 63;
'@ @'
        private const int SliceCheckMask = 63;
        private const int CaptureCheckMask = 3;
        private const long MaxCaptureQueueAgeFrames = 8;
        private const int CaptureWatchdogMicroseconds = 5000;
'@ 'ReachProfile capture constants'

$reach = Replace-OrThrow $reach @'
        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();
'@ @'
        private static readonly ConditionalWeakTable<Map, MapState> MapStates =
            new ConditionalWeakTable<Map, MapState>();
        // Main-thread only. A pending capture never publishes partial data; stale work is dropped.
        private static readonly Queue<ProfileCaptureState> PendingProfileCaptures =
            new Queue<ProfileCaptureState>();
'@ 'ReachProfile capture queue'

$reach = Replace-OrThrow $reach @'
        private static long profileCaptureTicksMax;
'@ @'
        private static long profileCaptureTicksMax;
        private static long profileCaptureQueued;
        private static long profileCaptureSlices;
        private static long profileCaptureAborted;
        private static long profileCaptureWatchdogTrips;
'@ 'ReachProfile capture counters'

$reach = Replace-OrThrow $reach @'
            PatchRegionDirtySignals(harmony);
'@ @'
            PatchRegionDirtySignals(harmony);
            PatchProfileCaptureDrain(harmony);
'@ 'ReachProfile frame drain install'

$newSchedule = @'
        private static void EnsureProfileScheduled(
            Map map,
            MapState mapState,
            Pawn pawn,
            TraverseParms traverseParams,
            TraverseKey key,
            ProfileSlot slot)
        {
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !RimMTThreadGuard.IsMainThread || !FeatureGate.IsEnabled(FeatureId))
                return;

            long now = RimMTRuntime.MainThreadFrames;
            long last = Interlocked.Read(ref slot.LastScheduleFrame);
            if (last != 0 && now - last < BuildCooldownFrames) return;
            if (Interlocked.CompareExchange(ref slot.BuildScheduled, 1, 0) != 0) return;

            TopologySnapshot topology = EnsureTopology(map, mapState);
            if (topology == null)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                return;
            }

            long generation = Interlocked.Read(ref mapState.RegionGeneration);
            if (topology.RegionGeneration != generation)
            {
                Volatile.Write(ref slot.BuildScheduled, 0);
                Interlocked.Increment(ref buildsStale);
                return;
            }

            // T23: do not execute Region.Allows over every region from the CanReach hot path.
            // Only allocate state and enqueue a main-thread capture. Root_Play.Update advances the
            // queue under one global frame budget; Vanilla remains authoritative until publication.
            try
            {
                ProfileCaptureState capture = new ProfileCaptureState(
                    map, mapState, pawn, traverseParams, key, slot, topology, generation, now);
                PendingProfileCaptures.Enqueue(capture);
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
            try
            {
                MethodBase rootUpdate = AccessTools.Method(typeof(Root_Play), "Update");
                if (rootUpdate == null) return;
                HarmonyMethod postfix = new HarmonyMethod(
                    typeof(AggressiveReachabilityProfilesV17), nameof(ProfileCaptureDrainPostfix));
                postfix.priority = Priority.Last - 100;
                harmony.Patch(rootUpdate, postfix: postfix);
            }
            catch (Exception ex)
            {
                // This is an optimization-only drain. Failure leaves Vanilla Reachability authoritative.
                Log.Warning("[RimMT] T23 ReachProfile capture drain unavailable; profile misses remain Vanilla-authoritative. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void ProfileCaptureDrainPostfix()
        {
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing) return;
            if (PendingProfileCaptures.Count == 0) return;

            if (!FeatureGate.IsEnabled(FeatureId))
            {
                while (PendingProfileCaptures.Count != 0)
                    DropCurrentCapture(false, false);
                return;
            }

            DrainProfileCaptureBudget();
        }

        private static void DrainProfileCaptureBudget()
        {
            long frame = RimMTRuntime.MainThreadFrames;
            long globalStart = Stopwatch.GetTimestamp();
            long budgetTicks = ProfileCaptureSliceBudgetTicks();
            long watchdogTicks = Math.Max(1L,
                Stopwatch.Frequency * CaptureWatchdogMicroseconds / 1000000L);

            while (PendingProfileCaptures.Count != 0)
            {
                ProfileCaptureState capture = PendingProfileCaptures.Peek();
                if (capture == null || capture.Slot == null || capture.MapState == null || capture.Topology == null)
                {
                    DropCurrentCapture(false, false);
                    continue;
                }

                if (frame - capture.QueuedFrame > MaxCaptureQueueAgeFrames ||
                    capture.Map == null || capture.Map.Disposed || capture.Pawn == null ||
                    !capture.Pawn.Spawned || capture.Pawn.Map != capture.Map ||
                    Interlocked.Read(ref capture.MapState.RegionGeneration) != capture.RegionGeneration ||
                    capture.Topology.RegionGeneration != capture.RegionGeneration)
                {
                    DropCurrentCapture(true, false);
                    continue;
                }

                long sliceStart = Stopwatch.GetTimestamp();
                bool dropped = false;
                while (capture.Cursor < capture.Topology.RegionRefs.Length)
                {
                    Region region = capture.Topology.RegionRefs[capture.Cursor];
                    if (region == null || !region.valid)
                    {
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(true, false);
                        dropped = true;
                        break;
                    }

                    long pairStart = Stopwatch.GetTimestamp();
                    try
                    {
                        capture.TraverseAllowed[capture.Cursor] = region.Allows(capture.TraverseParams, false);
                        capture.DestinationAllowed[capture.Cursor] = region.Allows(capture.TraverseParams, true);
                    }
                    catch
                    {
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(false, false);
                        dropped = true;
                        break;
                    }
                    long pairTicks = Stopwatch.GetTimestamp() - pairStart;
                    capture.Cursor++;

                    // We cannot pre-empt one foreign/Vanilla Region.Allows call, but once a single
                    // region exceeds the watchdog we stop the capture and quarantine the slot briefly
                    // rather than allowing the remainder of the map to amplify the stall.
                    if (pairTicks >= watchdogTicks)
                    {
                        Interlocked.Exchange(ref capture.Slot.DisabledUntilFrame, frame + 120);
                        RecordProfileCaptureSlice(sliceStart);
                        DropCurrentCapture(false, true);
                        dropped = true;
                        break;
                    }

                    if ((capture.Cursor & CaptureCheckMask) == 0 && BudgetSpent(globalStart, budgetTicks))
                    {
                        RecordProfileCaptureSlice(sliceStart);
                        return;
                    }
                }

                if (dropped) continue;

                RecordProfileCaptureSlice(sliceStart);
                if (capture.Cursor < capture.Topology.RegionRefs.Length)
                    return;

                if (Interlocked.Read(ref capture.MapState.RegionGeneration) != capture.RegionGeneration)
                {
                    DropCurrentCapture(true, false);
                    continue;
                }

                JobScheduler scheduler = RimMTRuntime.Scheduler;
                if (scheduler == null)
                {
                    DropCurrentCapture(false, false);
                    continue;
                }

                ProfileBuildContext context = new ProfileBuildContext(
                    capture.Topology.MapId,
                    capture.Topology.Width,
                    capture.Topology.Height,
                    capture.RegionGeneration,
                    frame,
                    capture.Key,
                    capture.Topology.CellRegion,
                    capture.Topology.DistrictByRegion,
                    capture.Topology.EdgeOffsets,
                    capture.Topology.Edges,
                    capture.TraverseAllowed,
                    capture.DestinationAllowed);

                bool accepted;
                try
                {
                    MapState mapState = capture.MapState;
                    ProfileSlot slot = capture.Slot;
                    accepted = scheduler.TryEnqueue(FeatureId, AdaptiveLoadBalancer.RecommendedOffloadPriority, delegate
                    {
                        BuildAndPublishProfile(mapState, slot, context);
                    });
                }
                catch
                {
                    accepted = false;
                }

                PendingProfileCaptures.Dequeue();
                if (!accepted)
                {
                    Volatile.Write(ref capture.Slot.BuildScheduled, 0);
                    Interlocked.Increment(ref buildsRejected);
                    Interlocked.Increment(ref profileCaptureAborted);
                }
                else
                {
                    Interlocked.Exchange(ref capture.Slot.LastScheduleFrame, frame);
                    Interlocked.Increment(ref profileCaptures);
                    Interlocked.Increment(ref buildsScheduled);
                }

                if (BudgetSpent(globalStart, budgetTicks)) return;
            }
        }

        private static void DropCurrentCapture(bool stale, bool watchdog)
        {
            if (PendingProfileCaptures.Count == 0) return;
            ProfileCaptureState capture = PendingProfileCaptures.Dequeue();
            if (capture != null && capture.Slot != null)
                Volatile.Write(ref capture.Slot.BuildScheduled, 0);
            Interlocked.Increment(ref profileCaptureAborted);
            if (stale) Interlocked.Increment(ref buildsStale);
            else Interlocked.Increment(ref buildsRejected);
            if (watchdog) Interlocked.Increment(ref profileCaptureWatchdogTrips);
        }

        private static void RecordProfileCaptureSlice(long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < 0L) return;
            Interlocked.Increment(ref profileCaptureSlices);
            Interlocked.Add(ref profileCaptureTicks, elapsed);
            UpdateMax(ref profileCaptureTicksMax, elapsed);
        }

        private static long ProfileCaptureSliceBudgetTicks()
        {
            int microseconds = ProfileCaptureSliceBudgetMicroseconds();
            return Math.Max(1L, Stopwatch.Frequency * microseconds / 1000000L);
        }

        private static int ProfileCaptureSliceBudgetMicroseconds()
        {
            switch (AdaptiveLoadBalancer.Pressure)
            {
                case LoadPressure.Low: return 2000;
                case LoadPressure.Normal: return 1500;
                case LoadPressure.High: return 1000;
                default: return 500;
            }
        }

'@

$reach = Replace-Between-OrThrow $reach `
    '        private static void EnsureProfileScheduled(' `
    '        private static TopologySnapshot EnsureTopology(' `
    $newSchedule `
    'Replace monolithic profile capture with T23 sliced queue'

$reach = Replace-OrThrow $reach @'
        private sealed class ProfileBuildContext
'@ @'
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

            internal ProfileCaptureState(Map map, MapState mapState, Pawn pawn, TraverseParms traverseParams,
                TraverseKey key, ProfileSlot slot, TopologySnapshot topology, long generation, long queuedFrame)
            {
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
'@ 'Insert T23 profile capture state'

$reach = Replace-OrThrow $reach @'
                ", profileCaptures=" + captures +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
'@ @'
                ", profileCaptures=" + captures +
                ", captureQueued=" + Interlocked.Read(ref profileCaptureQueued) +
                ", captureSlices=" + Interlocked.Read(ref profileCaptureSlices) +
                ", captureAborted=" + Interlocked.Read(ref profileCaptureAborted) +
                ", captureWatchdogTrips=" + Interlocked.Read(ref profileCaptureWatchdogTrips) +
                ", capturePending=" + PendingProfileCaptures.Count +
                ", captureBudgetUs=" + ProfileCaptureSliceBudgetMicroseconds() +
                ", buildsScheduled=" + Interlocked.Read(ref buildsScheduled) +
'@ 'ReachProfile T23 summary counters'

$reach = $reach.Replace('", avgProfileCaptureUs=" + avgCaptureUs.ToString("F2") +', '", avgProfileCaptureWorkUs=" + avgCaptureUs.ToString("F2") +')
$reach = $reach.Replace('", maxProfileCaptureUs=" + maxCaptureUs.ToString("F2") +', '", maxProfileCaptureSliceUs=" + maxCaptureUs.ToString("F2") +')
$reach = $reach.Replace('Topology is captured incrementally on the main thread; workers consume primitive immutable arrays only.', 'Topology and Region.Allows profile capture are frame-budgeted on the main thread; workers consume primitive immutable arrays only.')
Set-Content $reachPath $reach -Encoding UTF8

# Fix WorldRoot source before it is compiled. The old T22 transform also changed the Stopwatch
# field, but T23 intentionally does not run that divergent transform.
$rootPath = 'RimMT/Source/RimMT/Diagnostics/WorldRootAttribution093T22.cs'
$root = Get-Content $rootPath -Raw
$root = Replace-OrThrow $root 'private const double TickToMs = 1000.0 / Stopwatch.Frequency;' 'private static readonly double TickToMs = 1000.0 / Stopwatch.Frequency;' 'WorldRoot Stopwatch runtime frequency'

$newTraitSignals = @'
        private static void PatchTraitDefDatabaseSignals(Harmony harmony)
        {
            Type db = typeof(DefDatabase<TraitDef>);
            // T23: resolve exact overloads inside isolated try/catch blocks. The previous
            // AccessTools.Method(db, "Add") could throw AmbiguousMatchException before the
            // helper's catch and abort the later WorldTechLevel setup entirely.
            TryPatchTraitSignal(harmony, db, "SetIndices", Type.EmptyTypes,
                nameof(TraitSetIndicesPrefix), nameof(TraitSetIndicesPostfix));
            TryPatchTraitSignal(harmony, db, "Add", new Type[] { typeof(TraitDef) },
                nameof(TraitAddPrefix), null);
            TryPatchTraitSignal(harmony, db, "Remove", new Type[] { typeof(TraitDef) },
                nameof(TraitRemovePrefix), null);
        }

        private static void TryPatchTraitSignal(Harmony harmony, Type db, string name, Type[] args,
            string prefixName, string postfixName)
        {
            try
            {
                MethodBase method = AccessTools.Method(db, name, args);
                PatchOptionalNoArgOrAny(harmony, method, prefixName, postfixName);
            }
            catch
            {
                installFailures++;
            }
        }

'@
$root = Replace-Between-OrThrow $root `
    '        private static void PatchTraitDefDatabaseSignals(Harmony harmony)' `
    '        private static void PatchOptionalNoArgOrAny(' `
    $newTraitSignals `
    'WorldRoot TraitDef overload isolation'
Set-Content $rootPath $root -Encoding UTF8

# Foundation III runs before this transform. Keep its GenClosest transaction and Root_Play
# dispatcher bridge, then install WorldRoot attribution beside its lightweight WorldTailBoundary.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t22-foundation-genclosest-smf";' 'internal const string Version = "0.9.3-t23-tail-containment";' 'T23 bootstrap version'
$boot = Replace-OrThrow $boot @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                GenClosestTransactionIndex093T22.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
                WorldRootAttribution093T22.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T23 WorldRoot install beside Foundation III'
$boot = $boot.Replace('V0.9.3-T22 Foundation III initialized.', 'V0.9.3-T23 Tail Containment initialized.')
$boot = $boot.Replace('direct WorldTick boundary timing added.', 'direct WorldTick boundary timing retained; WorldRoot attribution restored; ReachProfile Region.Allows capture moved out of CanReach into a bounded Root_Play frame queue.')
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T22 Foundation III GenClosest + SMF Bridge', 'V0.9.3-T23 Tail Containment')
$report = Replace-OrThrow $report @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
            sb.AppendLine(WorldRootAttribution093T22.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T23 report WorldRoot summary'
$report = $report.Replace('T22 adds repeated-IList package-local GenClosest_Global_NewTemp distance/source-order indexing with live validator authority, direct WorldTick boundary timing, and Root_Play.Update dispatcher drain for SimplyMoreFPS coexistence;', 'T22 Foundation III repeated-IList GenClosest and Root_Play.Update SMF bridge retained; T23 adds WorldRoot attribution and frame-budgeted ReachProfile Region.Allows capture with stale/watchdog fail-open containment;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T22 Foundation III GenClosest + SMF Bridge', 'V0.9.3-T23 Tail Containment')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T23 Tail Containment: Foundation III preserved, WorldRoot restored, ReachProfile Region.Allows capture frame-sliced, TraitDef overload ambiguity isolated.'
