$ErrorActionPreference='Stop'

function Replace-OrThrow {
  param([string]$Text,[string]$Old,[string]$New,[string]$Label)
  if(-not $Text.Contains($Old)){ throw "T34-B anchor missing: $Label" }
  return $Text.Replace($Old,$New)
}

$root=(Resolve-Path (Join-Path $PSScriptRoot '../..')).Path

# ---------------------------------------------------------------------------
# Materialize T34-B source only after the T34-A prerequisite build has finished.
# This keeps earlier staged builds from compiling code whose bridge APIs do not exist yet.
# ---------------------------------------------------------------------------
$template=Join-Path $root 'RimMT/Build/Templates/ScannerParallelFabric093T34B.cs.txt'
$scannerPath=Join-Path $root 'RimMT/Source/RimMT/AI/ScannerParallelFabric093T34B.cs'
if(-not (Test-Path $template)){ throw 'T34-B scanner template missing' }
Copy-Item $template $scannerPath -Force

# ---------------------------------------------------------------------------
# Bootstrap/version.
# ---------------------------------------------------------------------------
$bootPath=Join-Path $root 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot=Get-Content $bootPath -Raw
$boot=Replace-OrThrow $boot 'internal const string Version = "0.9.3-t34a-async-candidate-fabric";' 'internal const string Version = "0.9.3-t34b-scanner-parallel-fabric";' 'bootstrap version'

if(-not $boot.Contains('ScannerParallelFabric093T34B.Apply(harmony);')){
  $anchor='                CandidateFabric093T34A.Apply(harmony);'
  if(-not $boot.Contains($anchor)){ throw 'T34-B anchor missing: scanner bootstrap' }
  $boot=$boot.Replace($anchor,
    $anchor + [Environment]::NewLine + '                ScannerParallelFabric093T34B.Apply(harmony);')
}
$boot=$boot.Replace('[RimMT] V0.9.3-T34A Async Candidate Fabric initialized.',
                    '[RimMT] V0.9.3-T34B Scanner Parallel Fabric initialized.')
Set-Content $bootPath $boot -Encoding UTF8

# ---------------------------------------------------------------------------
# Runtime feature gate. This is foreground parallel assist: HIGH-priority worker
# plans are allowed under Critical pressure; only background tasks use adaptive budget.
# ---------------------------------------------------------------------------
$runtimePath=Join-Path $root 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime=Get-Content $runtimePath -Raw
if(-not $runtime.Contains('FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId')){
  $anchor='            FeatureGate.Register(CandidateFabric093T34A.FeatureId, true, "T34-A no-wait worker-maintained candidate spatial fabric");'
  if(-not $runtime.Contains($anchor)){ throw 'T34-B runtime register anchor missing' }
  $runtime=$runtime.Replace($anchor,
    $anchor + [Environment]::NewLine +
    '            FeatureGate.Register(ScannerParallelFabric093T34B.FeatureId, true, "T34-B same-package scanner candidate parallel planning");')
}
if(-not $runtime.Contains('FeatureGate.SetEnabled(ScannerParallelFabric093T34B.FeatureId, work);')){
  $anchor='            FeatureGate.SetEnabled(CandidateFabric093T34A.FeatureId, work);'
  if(-not $runtime.Contains($anchor)){ throw 'T34-B runtime settings anchor missing' }
  $runtime=$runtime.Replace($anchor,
    $anchor + [Environment]::NewLine +
    '            FeatureGate.SetEnabled(ScannerParallelFabric093T34B.FeatureId, work);')
}
Set-Content $runtimePath $runtime -Encoding UTF8

# ---------------------------------------------------------------------------
# T34-A becomes the stable source-registration/snapshot service for T34-B.
# The bridge methods are internal only and preserve the existing T34-A authority path.
# ---------------------------------------------------------------------------
$candidatePath=Join-Path $root 'RimMT/Source/RimMT/AI/CandidateFabric093T34A.cs'
$candidate=Get-Content $candidatePath -Raw

if(-not $candidate.Contains('TryEnsureSourceSnapshotT34B')){
$bridge=@'

        // T34-B bridge: reuse T34-A's proven source membership + persistent fabric ownership
        // without duplicating mutable-world tracking. These methods are main-thread only.
        internal static bool TryEnsureSourceSnapshotT34B(
            Map map,
            object source,
            int minCount,
            int maxCount,
            out PersistentMapSearchFabric.SourceSnapshot snapshot,
            out int sourceId,
            out int count)
        {
            snapshot = null;
            sourceId = 0;
            count = 0;

            if (map == null || map.Disposed || source == null ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return false;

            SourceKind kind;
            if (!TryGetSourceShape(source, out kind, out count) ||
                kind == SourceKind.Pawn ||
                count < minCount || count > maxCount)
                return false;

            SourceState state = States.GetValue(source, CreateState);
            if (state.MapId != map.uniqueID)
            {
                state.MapId = map.uniqueID;
                state.Members = null;
            }

            if (!MembershipMatches(source, kind, count, state.Members))
            {
                Thing[] members;
                CaptureFailure failure;
                if (!TryCaptureMembers(source, kind, count, map, out members, out failure))
                    return false;

                state.Members = members;
                if (!PersistentMapSearchFabric.RegisterOrUpdateSource(
                    map, state.SourceId, members))
                    return false;

                // Publication is asynchronous. T34-B never waits for this first observation.
                return false;
            }

            PersistentMapSearchFabric.SourceSnapshot current;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(
                map, state.SourceId, out current) ||
                current == null || current.Count != count)
                return false;

            sourceId = state.SourceId;
            snapshot = current;
            return true;
        }

        // Package-start prefetch intentionally skips O(N) membership revalidation. A stale
        // prefetch is harmless because consumption always calls TryValidateSourceSnapshotT34B.
        internal static bool TryGetKnownSourceSnapshotFastT34B(
            Map map,
            object source,
            out PersistentMapSearchFabric.SourceSnapshot snapshot,
            out int sourceId,
            out int count)
        {
            snapshot = null;
            sourceId = 0;
            count = 0;

            if (map == null || map.Disposed || source == null ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return false;

            SourceState state;
            if (!States.TryGetValue(source, out state) ||
                state == null || state.MapId != map.uniqueID ||
                state.Members == null)
                return false;

            count = state.Members.Length;
            PersistentMapSearchFabric.SourceSnapshot current;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(
                map, state.SourceId, out current) ||
                current == null || current.Count != count)
                return false;

            sourceId = state.SourceId;
            snapshot = current;
            return true;
        }

        internal static bool TryValidateSourceSnapshotT34B(
            Map map,
            object source,
            PersistentMapSearchFabric.SourceSnapshot expected,
            out int count)
        {
            count = 0;
            if (map == null || map.Disposed || source == null || expected == null ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return false;

            SourceKind kind;
            if (!TryGetSourceShape(source, out kind, out count) ||
                kind == SourceKind.Pawn)
                return false;

            SourceState state;
            if (!States.TryGetValue(source, out state) ||
                state == null || state.MapId != map.uniqueID ||
                !MembershipMatches(source, kind, count, state.Members))
                return false;

            PersistentMapSearchFabric.SourceSnapshot current;
            if (!PersistentMapSearchFabric.TryGetSourceSnapshot(
                map, state.SourceId, out current) ||
                current == null || current.Count != count)
                return false;

            return ReferenceEquals(current, expected);
        }
'@
  $anchor='        internal static string Summary()'
  if(-not $candidate.Contains($anchor)){ throw 'T34-B candidate bridge insertion anchor missing' }
  $candidate=$candidate.Replace($anchor,$bridge + [Environment]::NewLine + $anchor)
}
Set-Content $candidatePath $candidate -Encoding UTF8

# ---------------------------------------------------------------------------
# Persistent fabric T34-B:
# 1) batch map mutation events until a logical tick flush,
# 2) foreground/high-priority worker drain,
# 3) rebuild only source snapshots touched by changed Things/source membership,
# 4) preserve SourceSnapshot object identity for unchanged sources so worker plans can survive.
# ---------------------------------------------------------------------------
$fabricPath=Join-Path $root 'RimMT/Source/RimMT/AI/PersistentMapSearchFabric.cs'
$fabric=Get-Content $fabricPath -Raw

$fabric=$fabric.Replace('internal static class PersistentMapSearchFabric',
                        'internal static partial class PersistentMapSearchFabric')
$fabric=$fabric.Replace('internal sealed class SourceSnapshot',
                        'internal sealed partial class SourceSnapshot')

if(-not $fabric.Contains('PendingStatesT34B')){
  $anchor=@'
        private static readonly ConditionalWeakTable<Map, MapState> States =
            new ConditionalWeakTable<Map, MapState>();
'@
  $replace=$anchor + [Environment]::NewLine +
'        private static readonly ConcurrentQueue<MapState> PendingStatesT34B =' + [Environment]::NewLine +
'            new ConcurrentQueue<MapState>();'
  $fabric=Replace-OrThrow $fabric $anchor $replace 'fabric pending states'
}

$oldQueue=@'
        private static void QueueEvent(MapState state, FabricEvent ev)
        {
            state.Events.Enqueue(ev);
            if (Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) != 0)
                return;

            ScheduleDrain(state);
        }

        private static void ScheduleDrain(MapState state)
'@
$newQueue=@'
        private static void QueueEvent(MapState state, FabricEvent ev)
        {
            state.Events.Enqueue(ev);
            MarkPendingT34B(state);
        }

        private static void MarkPendingT34B(MapState state)
        {
            if (state == null)
                return;
            if (Interlocked.CompareExchange(ref state.FlushQueuedT34B, 1, 0) == 0)
                PendingStatesT34B.Enqueue(state);
        }

        // Called once at the logical tick boundary by T34-B. No wait/join/spin occurs here:
        // it only converts coalesced map events into one foreground worker drain per dirty map.
        internal static void FlushPendingT34B()
        {
            int budget = 64;
            MapState state;
            while (budget-- > 0 && PendingStatesT34B.TryDequeue(out state))
            {
                Volatile.Write(ref state.FlushQueuedT34B, 0);
                if (state.Events.IsEmpty)
                    continue;

                if (Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) != 0)
                {
                    MarkPendingT34B(state);
                    continue;
                }

                ScheduleDrain(state);
            }
        }

        private static void ScheduleDrain(MapState state)
'@
$fabric=Replace-OrThrow $fabric $oldQueue $newQueue 'fabric queue batching'

# Foreground spatial service: critical-load background budget must not suppress it.
$fabric=$fabric.Replace('scheduler.TryEnqueue(FeatureId, JobPriority.Normal, delegate',
                        'scheduler.TryEnqueue(FeatureId, JobPriority.High, delegate')

# Scheduler rejection must preserve queued events for a later tick.
$oldReject=@'
            if (!accepted)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
'@
$newReject=@'
            if (!accepted)
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
                if (!state.Events.IsEmpty)
                    MarkPendingT34B(state);
            }
'@
$fabric=Replace-OrThrow $fabric $oldReject $newReject 'fabric scheduler reject requeue'

$oldFinally=@'
            finally
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                if (!state.Events.IsEmpty && Interlocked.CompareExchange(ref state.WorkerScheduled, 1, 0) == 0)
                    ScheduleDrain(state);
            }
'@
$newFinally=@'
            finally
            {
                Volatile.Write(ref state.WorkerScheduled, 0);
                if (!state.Events.IsEmpty)
                    MarkPendingT34B(state);
            }
'@
$fabric=Replace-OrThrow $fabric $oldFinally $newFinally 'fabric worker finalizer'

# MapState gets a separate flush-queued bit; WorkerScheduled keeps its old meaning.
$mapAnchor='            internal int WorkerScheduled;'
if(-not $fabric.Contains('FlushQueuedT34B')){
  throw 'T34-B internal error: queue batching insertion missing'
}
if(-not $fabric.Contains('internal int FlushQueuedT34B;')){
  $fabric=Replace-OrThrow $fabric $mapAnchor ($mapAnchor + [Environment]::NewLine + '            internal int FlushQueuedT34B;') 'fabric map flush field'
}

$workerPattern='(?s)        private sealed class SourceModel.*?        private static readonly FabricEntry\[\] EmptyEntries = new FabricEntry\[0\];'
if(-not [regex]::IsMatch($fabric,$workerPattern)){ throw 'T34-B fabric WorkerModel block not found' }

$workerReplacement=@'
        private sealed class SourceModel
        {
            internal Thing[] Members;
            internal SourceModel(Thing[] members) { Members = members; }
        }

        private sealed class WorkerModel
        {
            private readonly int mapId;
            private readonly int width;
            private readonly int height;
            private readonly Dictionary<Thing, PositionEntry> positions =
                new Dictionary<Thing, PositionEntry>(ThingReferenceComparer.Instance);
            private readonly Dictionary<int, SourceModel> sources =
                new Dictionary<int, SourceModel>();
            private readonly Dictionary<Thing, HashSet<int>> sourceIdsByThing =
                new Dictionary<Thing, HashSet<int>>(ThingReferenceComparer.Instance);
            private readonly HashSet<int> dirtySources = new HashSet<int>();
            private Dictionary<int, SourceSnapshot> publishedSources =
                new Dictionary<int, SourceSnapshot>();
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
                    return;

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
                        if (ev.Members == null)
                            break;

                        SourceModel old;
                        if (sources.TryGetValue(ev.SourceId, out old) && old != null && old.Members != null)
                        {
                            for (int i = 0; i < old.Members.Length; i++)
                                RemoveMembership(old.Members[i], ev.SourceId);
                        }

                        for (int i = 0; i < ev.Members.Length; i++)
                        {
                            Thing thing = ev.Members[i];
                            if (thing == null)
                                continue;
                            positions[thing] = new PositionEntry(ev.Xs[i], ev.Zs[i]);
                            AddMembership(thing, ev.SourceId);
                        }

                        sources[ev.SourceId] = new SourceModel(ev.Members);
                        dirtySources.Add(ev.SourceId);
                        break;
                }
            }

            private void AddMembership(Thing thing, int sourceId)
            {
                if (thing == null)
                    return;

                HashSet<int> ids;
                if (!sourceIdsByThing.TryGetValue(thing, out ids))
                {
                    ids = new HashSet<int>();
                    sourceIdsByThing.Add(thing, ids);
                }
                ids.Add(sourceId);
            }

            private void RemoveMembership(Thing thing, int sourceId)
            {
                if (thing == null)
                    return;

                HashSet<int> ids;
                if (!sourceIdsByThing.TryGetValue(thing, out ids))
                    return;

                ids.Remove(sourceId);
                if (ids.Count == 0)
                    sourceIdsByThing.Remove(thing);
            }

            private void MarkThingSourcesDirty(Thing thing)
            {
                if (thing == null)
                    return;

                HashSet<int> ids;
                if (!sourceIdsByThing.TryGetValue(thing, out ids))
                    return;

                foreach (int id in ids)
                    dirtySources.Add(id);
            }

            internal MapFabricSnapshot BuildSnapshot()
            {
                Dictionary<int, SourceSnapshot> next =
                    new Dictionary<int, SourceSnapshot>(publishedSources);

                foreach (int sourceId in dirtySources)
                {
                    SourceModel source;
                    if (sources.TryGetValue(sourceId, out source) && source != null)
                        next[sourceId] = BuildSourceSnapshot(source);
                }

                dirtySources.Clear();
                publishedSources = next;
                return new MapFabricSnapshot(
                    mapId, width, height, appliedGeneration, next);
            }

            private SourceSnapshot BuildSourceSnapshot(SourceModel source)
            {
                int cols = Math.Max(1, (width + BucketSize - 1) / BucketSize);
                int rows = Math.Max(1, (height + BucketSize - 1) / BucketSize);
                List<FabricEntry>[] temp = new List<FabricEntry>[cols * rows];
                bool complete = true;

                for (int i = 0; i < source.Members.Length; i++)
                {
                    Thing thing = source.Members[i];
                    PositionEntry pos;
                    if (thing == null || !positions.TryGetValue(thing, out pos) ||
                        pos.X < 0 || pos.Z < 0 || pos.X >= width || pos.Z >= height)
                    {
                        complete = false;
                        continue;
                    }

                    int key = (pos.X / BucketSize) + (pos.Z / BucketSize) * cols;
                    List<FabricEntry> list = temp[key];
                    if (list == null)
                        temp[key] = list = new List<FabricEntry>();
                    list.Add(new FabricEntry(thing, pos.X, pos.Z, i));
                }

                FabricEntry[][] buckets = new FabricEntry[temp.Length][];
                for (int i = 0; i < temp.Length; i++)
                    buckets[i] = temp[i] == null ? EmptyEntries : temp[i].ToArray();

                return new SourceSnapshot(
                    mapId, width, height, cols, rows,
                    source.Members.Length, complete, buckets);
            }
        }

        private static readonly FabricEntry[] EmptyEntries = new FabricEntry[0];
'@
$fabric=[regex]::Replace($fabric,$workerPattern,[System.Text.RegularExpressions.MatchEvaluator]{ param($m) $workerReplacement },1)
Set-Content $fabricPath $fabric -Encoding UTF8

# ---------------------------------------------------------------------------
# Production report.
# ---------------------------------------------------------------------------
$reportPath=Join-Path $root 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report=Get-Content $reportPath -Raw
$report=$report.Replace('V0.9.3-T34A Async Candidate Fabric','V0.9.3-T34B Scanner Parallel Fabric')
if(-not $report.Contains('ScannerParallelFabric093T34B.Summary()')){
  $anchor='            sb.AppendLine(CandidateFabric093T34A.Summary());'
  if(-not $report.Contains($anchor)){ throw 'T34-B production report anchor missing' }
  $report=$report.Replace($anchor,
    '            sb.AppendLine(ScannerParallelFabric093T34B.Summary());' +
    [Environment]::NewLine + $anchor)
}
$report=$report.Replace(
  'T34-A persistent candidate fabric reactivated with ThingRequest-backed sources + uncapped live completion; first/missing/stale snapshots always fall through.',
  'T34-B scanner parallel planning=ACTIVE(HIGH priority, same-package overlap); T34-A persistent candidate fabric remains synchronous fallback; fabric mutations are tick-coalesced + dirty-source-only rebuilt.')
Set-Content $reportPath $report -Encoding UTF8

# ---------------------------------------------------------------------------
# About.
# ---------------------------------------------------------------------------
$aboutPath=Join-Path $root 'RimMT/About/About.xml'
if(Test-Path $aboutPath){
  $a=Get-Content $aboutPath -Raw
  $a=[regex]::Replace($a,'<name>.*?</name>','<name>RimMT V0.9.3-T34B Scanner Parallel Fabric</name>',1)
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>RimMT T34-B for RimWorld 1.5. General scanner parallel fabric: hot WorkGiver candidate sources are learned from real GenClosest traffic and their exact distance/source-order plans are computed on HIGH-priority worker tasks in parallel with the current TryIssueJobPackage. Plans are consumed only when the exact immutable source snapshot and root still match; membership, spawn and position are revalidated before live Reachability and validator calls. The main thread never waits. T34-A remains as a synchronous spatial fallback. Persistent-map mutation events are coalesced to logical tick boundaries and only dirty source snapshots are rebuilt, preserving unchanged snapshot identity for cross-package plan reuse. No worker runs WorkGiver callbacks, Reachability, reservations, Rand, Unity, Job creation or gameplay commits. FullParallel remains HARD_OFF.</description>')
  Set-Content $aboutPath $a -Encoding UTF8
}

# ---------------------------------------------------------------------------
# Diagnostics companion v0.18: external only, reflection summary for T34-B.
# ---------------------------------------------------------------------------
$diagPatchPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticsPatches.cs'
$diag=Get-Content $diagPatchPath -Raw
$diag=Replace-OrThrow $diag 'internal const string Version = "0.17.0";' 'internal const string Version = "0.18.0";' 'Diagnostics v0.18'
Set-Content $diagPatchPath $diag -Encoding UTF8

$diagReportPath=Join-Path $root 'RimMTDiagnostics/Source/RimMTDiagnostics/DiagnosticReport.cs'
$dr=Get-Content $diagReportPath -Raw
if(-not $dr.Contains('"RimMT.ScannerParallelFabric093T34B"')){
  $anchor='            "RimMT.CandidateFabric093T34A",'
  if(-not $dr.Contains($anchor)){ throw 'T34-B diagnostics reflection anchor missing' }
  $dr=$dr.Replace($anchor,
    '            "RimMT.ScannerParallelFabric093T34B",' +
    [Environment]::NewLine + $anchor)
}
Set-Content $diagReportPath $dr -Encoding UTF8

$diagAbout=Join-Path $root 'RimMTDiagnostics/About/About.xml'
if(Test-Path $diagAbout){
  $a=Get-Content $diagAbout -Raw
  $a=[regex]::Replace($a,'<name>.*?</name>','<name>RimMT Diagnostics v0.18 - T34B Scanner Parallel</name>',1)
  $a=[regex]::Replace($a,'(?s)<description>.*?</description>',
    '<description>Optional diagnostics companion for RimMT T34-B. Adds external measurement of the general scanner parallel fabric, same-package/cross-package worker plan hits, worker build time, main-thread live-check cost, persistent fabric batching and scheduler utilization. Disable this companion for pure production feel tests.</description>')
  Set-Content $diagAbout $a -Encoding UTF8
}

Write-Host 'Applied RimMT T34-B Scanner Parallel Fabric + tick-coalesced dirty-source persistent fabric + Diagnostics v0.18.'
