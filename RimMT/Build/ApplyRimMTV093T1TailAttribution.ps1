$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T1 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T1 Tail Attribution
# Measurement-only child of T0. No optimizer/admission/budget/authority behavior changes.
# Adds bounded top-level DoSingleTick phase timing and GC generation-change correlation.
# No per-Pawn/per-Thing profiler is installed.

$attrPath = 'RimMT/Source/RimMT/Diagnostics/TailAttribution093T1.cs'
$attr = @'
using System;
using System.Diagnostics;
using System.Text;

namespace RimMT
{
    internal enum TailPhase093T1
    {
        TickListNormal = 0,
        TickListRare = 1,
        TickListLong = 2,
        TickListExtra = 3,
        MapPreTick = 4,
        WorldTick = 5,
        StoryWatcher = 6,
        GameEnd = 7,
        Storyteller = 8,
        Tales = 9,
        WorldPostTick = 10,
        MapPostTick = 11,
        History = 12,
        GameComponents = 13,
        Autosaver = 14,
        Scenario = 15,
        DateNotifier = 16,
        Letters = 17,
        Filth = 18,
        Count = 19
    }

    /// <summary>
    /// Top-level attribution only. Hot-path state is fixed-size and main-thread owned.
    /// No allocations, reflection or sorting occur during a game tick.
    /// </summary>
    internal static class TailAttribution093T1
    {
        private const int PhaseCount = (int)TailPhase093T1.Count;
        private const int RecentCapacity = 16;

        private static readonly string[] PhaseNames = new string[]
        {
            "TickListNormal", "TickListRare", "TickListLong", "TickListExtra",
            "MapPreTick", "WorldTick", "StoryWatcher", "GameEnd", "Storyteller",
            "Tales", "WorldPostTick", "MapPostTick", "History", "GameComponents",
            "Autosaver", "Scenario", "DateNotifier", "Letters", "Filth"
        };

        private static readonly long[] Calls = new long[PhaseCount];
        private static readonly long[] TotalUs = new long[PhaseCount];
        private static readonly long[] MaxUs = new long[PhaseCount];
        private static readonly long[] Over5 = new long[PhaseCount];
        private static readonly long[] Over10 = new long[PhaseCount];
        private static readonly long[] Over20 = new long[PhaseCount];
        private static readonly long[] Over50 = new long[PhaseCount];
        private static readonly long[] CurrentPhaseUs = new long[PhaseCount];
        private static readonly TailFrame[] RecentSevere = new TailFrame[RecentCapacity];

        [ThreadStatic] private static bool activeTick;
        [ThreadStatic] private static int tickListOrdinal;
        [ThreadStatic] private static int gc0Start;
        [ThreadStatic] private static int gc1Start;
        [ThreadStatic] private static int gc2Start;

        private static int recentPos;
        private static int recentCount;
        private static long tail20;
        private static long tail50;
        private static long tail50WithGc0;
        private static long tail50WithGc1;
        private static long tail50WithGc2;
        private static long maxUnattributedUs;

        internal static void BeginTick()
        {
            activeTick = true;
            tickListOrdinal = 0;
            Array.Clear(CurrentPhaseUs, 0, CurrentPhaseUs.Length);
            gc0Start = GC.CollectionCount(0);
            gc1Start = GC.CollectionCount(1);
            gc2Start = GC.CollectionCount(2);
        }

        internal static long BeginPhase()
        {
            if (!activeTick || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndPhase(long started, TailPhase093T1 phase)
        {
            if (started == 0L || !activeTick) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;

            Calls[p]++;
            TotalUs[p] += us;
            CurrentPhaseUs[p] += us;
            if (us > MaxUs[p]) MaxUs[p] = us;
            if (us >= 5000L) Over5[p]++;
            if (us >= 10000L) Over10[p]++;
            if (us >= 20000L) Over20[p]++;
            if (us >= 50000L) Over50[p]++;
        }

        internal static void EndTickList(long started)
        {
            int ordinal = tickListOrdinal++;
            TailPhase093T1 phase = ordinal == 0 ? TailPhase093T1.TickListNormal :
                ordinal == 1 ? TailPhase093T1.TickListRare :
                ordinal == 2 ? TailPhase093T1.TickListLong : TailPhase093T1.TickListExtra;
            EndPhase(started, phase);
        }

        internal static void EndTick(long totalUs)
        {
            if (!activeTick) return;

            int gc0 = Math.Max(0, GC.CollectionCount(0) - gc0Start);
            int gc1 = Math.Max(0, GC.CollectionCount(1) - gc1Start);
            int gc2 = Math.Max(0, GC.CollectionCount(2) - gc2Start);
            activeTick = false;

            if (totalUs >= 20000L) tail20++;
            if (totalUs < 50000L) return;

            tail50++;
            if (gc0 > 0) tail50WithGc0++;
            if (gc1 > 0) tail50WithGc1++;
            if (gc2 > 0) tail50WithGc2++;

            long phaseSum = 0L;
            int top1 = -1, top2 = -1, top3 = -1;
            long top1Us = 0L, top2Us = 0L, top3Us = 0L;
            for (int i = 0; i < PhaseCount; i++)
            {
                long value = CurrentPhaseUs[i];
                phaseSum += value;
                if (value > top1Us)
                {
                    top3 = top2; top3Us = top2Us;
                    top2 = top1; top2Us = top1Us;
                    top1 = i; top1Us = value;
                }
                else if (value > top2Us)
                {
                    top3 = top2; top3Us = top2Us;
                    top2 = i; top2Us = value;
                }
                else if (value > top3Us)
                {
                    top3 = i; top3Us = value;
                }
            }

            long unattributed = totalUs - phaseSum;
            if (unattributed < 0L) unattributed = 0L;
            if (unattributed > maxUnattributedUs) maxUnattributedUs = unattributed;

            int gameTick = -1;
            try
            {
                if (Verse.Find.TickManager != null) gameTick = Verse.Find.TickManager.TicksGame;
            }
            catch { }

            RecentSevere[recentPos] = new TailFrame(
                RimMTRuntime.MainThreadFrames, gameTick, totalUs, phaseSum, unattributed,
                top1, top1Us, top2, top2Us, top3, top3Us, gc0, gc1, gc2);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T1 top-level attribution: tail20=").Append(tail20)
                .Append(", tail50=").Append(tail50)
                .Append(", tail50WithGC[0/1/2]=").Append(tail50WithGc0).Append('/')
                .Append(tail50WithGc1).Append('/').Append(tail50WithGc2)
                .Append(", maxUnattributedMs=").Append((maxUnattributedUs / 1000.0).ToString("F2"))
                .Append(". Phase stats: ");

            for (int i = 0; i < PhaseCount; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = Calls[i];
                double avgUs = calls == 0L ? 0.0 : TotalUs[i] / (double)calls;
                sb.Append(PhaseNames[i]).Append("(calls=").Append(calls)
                    .Append(",avgUs=").Append(avgUs.ToString("F1"))
                    .Append(",>5/10/20/50=").Append(Over5[i]).Append('/')
                    .Append(Over10[i]).Append('/').Append(Over20[i]).Append('/').Append(Over50[i])
                    .Append(",maxMs=").Append((MaxUs[i] / 1000.0).ToString("F2")).Append(')');
            }
            return sb.ToString();
        }

        internal static string RecentSevereSummary()
        {
            if (recentCount <= 0) return "T1 recent >=50ms tails: none.";
            StringBuilder sb = new StringBuilder(3072);
            sb.Append("T1 recent >=50ms tails (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                TailFrame e = RecentSevere[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",covered=").Append((e.PhaseSumUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",other=").Append((e.UnattributedUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",top=").Append(PhaseName(e.Top1)).Append(':').Append((e.Top1Us / 1000.0).ToString("F2"))
                    .Append('/').Append(PhaseName(e.Top2)).Append(':').Append((e.Top2Us / 1000.0).ToString("F2"))
                    .Append('/').Append(PhaseName(e.Top3)).Append(':').Append((e.Top3Us / 1000.0).ToString("F2"))
                    .Append(",gc=").Append(e.Gc0).Append('/').Append(e.Gc1).Append('/').Append(e.Gc2);
            }
            return sb.ToString();
        }

        private static string PhaseName(int index)
        {
            return index >= 0 && index < PhaseNames.Length ? PhaseNames[index] : "None";
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct TailFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TotalUs;
            internal readonly long PhaseSumUs;
            internal readonly long UnattributedUs;
            internal readonly int Top1;
            internal readonly long Top1Us;
            internal readonly int Top2;
            internal readonly long Top2Us;
            internal readonly int Top3;
            internal readonly long Top3Us;
            internal readonly int Gc0;
            internal readonly int Gc1;
            internal readonly int Gc2;

            internal TailFrame(long frame, int gameTick, long totalUs, long phaseSumUs, long unattributedUs,
                int top1, long top1Us, int top2, long top2Us, int top3, long top3Us,
                int gc0, int gc1, int gc2)
            {
                Frame = frame; GameTick = gameTick; TotalUs = totalUs; PhaseSumUs = phaseSumUs;
                UnattributedUs = unattributedUs; Top1 = top1; Top1Us = top1Us;
                Top2 = top2; Top2Us = top2Us; Top3 = top3; Top3Us = top3Us;
                Gc0 = gc0; Gc1 = gc1; Gc2 = gc2;
            }
        }
    }
}
'@
Set-Content $attrPath $attr -Encoding UTF8

$patchesPath = 'RimMT/Source/RimMT/Patches/TailAttributionPatches093T1.cs'
$patches = @'
using System;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace RimMT
{
    /// <summary>Observational top-level Tick phase patches only; never changes __result or skips originals.</summary>
    internal static class TailAttributionPatches093T1
    {
        private static int patched;
        private static int missing;
        private static string missingNames = string.Empty;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchAny(harmony, nameof(TickListPostfix), "Verse.TickList:Tick");
            PatchAny(harmony, nameof(MapPrePostfix), "Verse.Map:MapPreTick");
            PatchAny(harmony, nameof(WorldPostfix), "Verse.World:WorldTick");
            PatchAny(harmony, nameof(StoryWatcherPostfix), "RimWorld.StoryWatcher:StoryWatcherTick");
            PatchAny(harmony, nameof(GameEndPostfix), "RimWorld.GameEnder:GameEndTick");
            PatchAny(harmony, nameof(StorytellerPostfix), "RimWorld.Storyteller:StorytellerTick");
            PatchAny(harmony, nameof(TalesPostfix), "RimWorld.TaleManager:TaleManagerTick");
            PatchAny(harmony, nameof(WorldPostTickPostfix), "Verse.World:WorldPostTick");
            PatchAny(harmony, nameof(MapPostPostfix), "Verse.Map:MapPostTick");
            PatchAny(harmony, nameof(HistoryPostfix), "RimWorld.History:HistoryTick", "Verse.History:HistoryTick");
            PatchAny(harmony, nameof(GameComponentsPostfix), "Verse.GameComponentUtility:GameComponentTick");
            PatchAny(harmony, nameof(AutosaverPostfix), "RimWorld.Autosaver:AutosaverTick", "Verse.Autosaver:AutosaverTick");
            PatchAny(harmony, nameof(ScenarioPostfix), "RimWorld.Scenario:TickScenario", "Verse.Scenario:TickScenario");
            PatchAny(harmony, nameof(DateNotifierPostfix), "RimWorld.DateNotifier:DateNotifierTick", "Verse.DateNotifier:DateNotifierTick");
            PatchAny(harmony, nameof(LettersPostfix), "RimWorld.LetterStack:LetterStackTick", "Verse.LetterStack:LetterStackTick");
            PatchAny(harmony, nameof(FilthPostfix), "RimWorld.FilthMonitor:FilthMonitorTick", "Verse.FilthMonitor:FilthMonitorTick");
            Log.Message("[RimMT] T1 Tail Attribution installed: patched=" + patched + ", missing=" + missing +
                (missing == 0 ? "." : ", missingTargets=" + missingNames + ".") +
                " Measurement-only top-level timing; optimizer behavior unchanged.");
        }

        private static void PatchAny(Harmony harmony, string postfixName, params string[] specs)
        {
            MethodBase target = null;
            string resolved = null;
            for (int i = 0; i < specs.Length; i++)
            {
                try { target = AccessTools.Method(specs[i]); }
                catch { target = null; }
                if (target != null) { resolved = specs[i]; break; }
            }
            if (target == null)
            {
                missing++;
                if (missingNames.Length < 512)
                {
                    if (missingNames.Length != 0) missingNames += ",";
                    missingNames += specs.Length == 0 ? "<empty>" : specs[0];
                }
                return;
            }

            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(TailAttributionPatches093T1), nameof(PhasePrefix)) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(TailAttributionPatches093T1), postfixName) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT] T1 attribution target failed closed: " + resolved + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PhasePrefix(ref long __state) { __state = TailAttribution093T1.BeginPhase(); }
        public static void TickListPostfix(long __state) { TailAttribution093T1.EndTickList(__state); }
        public static void MapPrePostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPreTick); }
        public static void WorldPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldTick); }
        public static void StoryWatcherPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.StoryWatcher); }
        public static void GameEndPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameEnd); }
        public static void StorytellerPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Storyteller); }
        public static void TalesPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Tales); }
        public static void WorldPostTickPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.WorldPostTick); }
        public static void MapPostPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.MapPostTick); }
        public static void HistoryPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.History); }
        public static void GameComponentsPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.GameComponents); }
        public static void AutosaverPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Autosaver); }
        public static void ScenarioPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Scenario); }
        public static void DateNotifierPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.DateNotifier); }
        public static void LettersPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Letters); }
        public static void FilthPostfix(long __state) { TailAttribution093T1.EndPhase(__state, TailPhase093T1.Filth); }
    }
}
'@
Set-Content $patchesPath $patches -Encoding UTF8

# Hook T1 lifecycle into the already-existing T0 DoSingleTick lifecycle.
$t0Path = 'RimMT/Source/RimMT/Diagnostics/TailObservatory093T0.cs'
$t0 = Get-Content $t0Path -Raw
$t0 = Replace-OrThrow $t0 @'
        internal static void BeginTick()
        {
            currentSignals = TailSignal093T0.None;
'@ @'
        internal static void BeginTick()
        {
            TailAttribution093T1.BeginTick();
            currentSignals = TailSignal093T0.None;
'@ 'T1 BeginTick lifecycle'
$t0 = Replace-OrThrow $t0 @'
            long us = TicksToUs(elapsedTicks);
            samples++;
'@ @'
            long us = TicksToUs(elapsedTicks);
            TailAttribution093T1.EndTick(us);
            samples++;
'@ 'T1 EndTick lifecycle'
Set-Content $t0Path $t0 -Encoding UTF8

# Install observational patches after the normal production patch set.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
                RimMTPatches.Apply(harmony);
                PathGridInvalidation.ApplyBulkGuard(harmony);
'@ @'
                RimMTPatches.Apply(harmony);
                TailAttributionPatches093T1.Apply(harmony);
                PathGridInvalidation.ApplyBulkGuard(harmony);
'@ 'T1 bootstrap patch install'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t0-tail-observatory";' 'internal const string Version = "0.9.3-t1-tail-attribution";' 'T1 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T0 Tail Observatory initialized.' '[RimMT] V0.9.3-T1 Tail Attribution initialized.' 'T1 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag @'
            sb.AppendLine(TailObservatory093T0.ComponentSummary());
            sb.AppendLine(TailObservatory093T0.RecentSummary());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
'@ @'
            sb.AppendLine(TailObservatory093T0.ComponentSummary());
            sb.AppendLine(TailObservatory093T0.RecentSummary());
            sb.AppendLine(TailAttribution093T1.Summary());
            sb.AppendLine(TailAttribution093T1.RecentSevereSummary());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
'@ 'T1 report lines'
$diag = Replace-OrThrow $diag '[RimMT] V0.9.3-T0 Tail Observatory on-demand report' '[RimMT] V0.9.3-T1 Tail Attribution on-demand report' 'T1 report title'
$diag = Replace-OrThrow $diag 'V0.9.3-T0 Tail Observatory; baseline=V0.9.3 Consolidated Stable;' 'V0.9.3-T1 Tail Attribution; baseline=V0.9.3 Consolidated Stable; T0 histogram + top-level phase/GC attribution;' 'T1 policy marker'
Set-Content $diagPath $diag -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T0 Tail Observatory', 'V0.9.3-T1 Tail Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T1 Tail Attribution: top-level phase + GC correlation only; stable optimizer behavior unchanged.'