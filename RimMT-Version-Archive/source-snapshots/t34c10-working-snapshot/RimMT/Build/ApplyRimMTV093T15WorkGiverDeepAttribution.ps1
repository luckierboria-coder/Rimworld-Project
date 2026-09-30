$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T15 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T15 WorkGiver Deep Attribution
# Diagnostic child of T14/T8. Production behavior and authority are unchanged.
# T15 reuses T2's already-bounded DetermineNextJob timing. A >=20ms sampled DetermineNextJob
# requests exactly one deferred WorkGiver detail burst. The burst is installed at the next
# TickManager frame boundary, captures 64 outer JobGiver_Work packages, then auto-unpatches.
# No permanent WorkGiver/Reachability/GenClosest profiling detours are added.
# Storyteller catastrophic-tail telemetry reuses T1 timing and adds no new Stopwatch calls.

$coordPath = 'RimMT/Source/RimMT/Diagnostics/WorkGiverDeepAttribution093T15.cs'
$coord = @'
using System;
using System.Text;
using System.Threading;
using Verse;

namespace RimMT
{
    /// <summary>
    /// T15 coordinator. Slow DetermineNextJob observation happens only inside T2 deep windows.
    /// Harmony mutation is deferred to RimMT's existing TickManager frame boundary.
    /// Exactly one bounded detail session is allowed per runtime so diagnostic cost cannot become resident.
    /// </summary>
    internal static class WorkGiverDeepAttribution093T15
    {
        private const long TriggerUs = 20000L;
        private const int MaxStartAttempts = 3;

        private static long triggerCount;
        private static long maxTriggerUs;
        private static int requested;
        private static int captureStarted;
        private static int captureCompleted;
        private static int startAttempts;
        private static int startFailures;
        private static long startFrame = -1L;
        private static long completionFrame = -1L;
        private static int startTick = -1;
        private static int completionTick = -1;

        internal static void ObserveDetermine(long us)
        {
            if (us < TriggerUs || !RimMTThreadGuard.IsMainThread) return;
            triggerCount++;
            if (us > maxTriggerUs) maxTriggerUs = us;

            if (Volatile.Read(ref captureStarted) != 0 || WorkGiverDetailPatches.CaptureActive)
                return;
            if (Volatile.Read(ref startAttempts) >= MaxStartAttempts)
                return;
            Interlocked.Exchange(ref requested, 1);
        }

        internal static void OnMainThreadFrame()
        {
            if (!RimMTThreadGuard.IsMainThread) return;

            // Existing detail profiler removes its temporary Harmony detours only here.
            WorkGiverDetailPatches.OnMainThreadFrame();

            if (Volatile.Read(ref captureStarted) != 0)
            {
                if (Volatile.Read(ref captureCompleted) == 0 &&
                    !WorkGiverDetailPatches.CaptureActive && WorkGiverDetailPatches.PackagesRemaining == 0)
                {
                    Interlocked.Exchange(ref captureCompleted, 1);
                    completionFrame = RimMTRuntime.MainThreadFrames;
                    completionTick = CurrentTick();
                }
                return;
            }

            if (Interlocked.Exchange(ref requested, 0) == 0)
                return;

            int attempt = Interlocked.Increment(ref startAttempts);
            bool started = false;
            try { started = WorkGiverDetailPatches.StartCapture(); }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T15 deferred WorkGiver detail start failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }

            if (started)
            {
                Interlocked.Exchange(ref captureStarted, 1);
                startFrame = RimMTRuntime.MainThreadFrames;
                startTick = CurrentTick();
                Log.Message("[RimMT] T15 WorkGiver deep attribution burst started after sampled DetermineNextJob >=20ms. Temporary detail detours will auto-remove after the bounded package window.");
            }
            else
            {
                Interlocked.Increment(ref startFailures);
                if (attempt < MaxStartAttempts)
                    Log.Warning("[RimMT] T15 WorkGiver detail burst could not start; a later >=20ms sampled DetermineNextJob may retry. attempt=" + attempt + "/" + MaxStartAttempts + ".");
            }
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("T15 WorkGiver deep attribution coordinator: trigger>=20ms, triggers=").Append(triggerCount)
              .Append(", maxTriggerMs=").Append((maxTriggerUs / 1000.0).ToString("F2"))
              .Append(", requested=").Append(Volatile.Read(ref requested) != 0)
              .Append(", captureStarted=").Append(Volatile.Read(ref captureStarted) != 0)
              .Append(", captureActive=").Append(WorkGiverDetailPatches.CaptureActive)
              .Append(", packagesRemaining=").Append(WorkGiverDetailPatches.PackagesRemaining)
              .Append(", captureCompleted=").Append(Volatile.Read(ref captureCompleted) != 0)
              .Append(", startAttempts=").Append(startAttempts)
              .Append(", startFailures=").Append(startFailures)
              .Append(", startFrame/tick=").Append(startFrame).Append('/').Append(startTick)
              .Append(", completionFrame/tick=").Append(completionFrame).Append('/').Append(completionTick)
              .Append(". Trigger uses T2's existing sampled DetermineNextJob Stopwatch; Harmony mutation is deferred to the frame boundary; one bounded session per runtime; measurement-only.");
            return sb.ToString();
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? -1 : Find.TickManager.TicksGame; }
            catch { return -1; }
        }
    }
}
'@
Set-Content $coordPath $coord -Encoding UTF8

$storyPath = 'RimMT/Source/RimMT/Diagnostics/StorytellerCatastrophic093T15.cs'
$story = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Records only catastrophic Storyteller phases already timed by T1. No extra hot-path timer.
    /// Harmony census is generated on demand only.
    /// </summary>
    internal static class StorytellerCatastrophic093T15
    {
        private const long ThresholdUs = 100000L;
        private const int RecentCapacity = 12;
        private static readonly Entry[] Recent = new Entry[RecentCapacity];
        private static long events;
        private static long maxUs;
        private static int recentPos;
        private static int recentCount;

        internal static void Observe(long us)
        {
            if (us < ThresholdUs || !RimMTThreadGuard.IsMainThread) return;
            events++;
            if (us > maxUs) maxUs = us;
            int tick = -1;
            try { if (Find.TickManager != null) tick = Find.TickManager.TicksGame; }
            catch { }
            Recent[recentPos] = new Entry(RimMTRuntime.MainThreadFrames, tick, us);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T15 Storyteller catastrophic tails >=100ms: events=").Append(events)
              .Append(", maxMs=").Append((maxUs / 1000.0).ToString("F2"));
            if (recentCount == 0) return sb.Append(", recent=none. T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.").ToString();

            sb.Append(", recent(oldest->newest)=");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append(';');
                Entry e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.Tick)
                  .Append(",ms=").Append((e.Us / 1000.0).ToString("F2"));
            }
            sb.Append(". T15 reuses T1 elapsed time; no extra Storyteller Stopwatch.");
            return sb.ToString();
        }

        internal static string HarmonyCensus()
        {
            MethodBase target = AccessTools.Method(typeof(Storyteller), "StorytellerTick");
            if (target == null) return "T15 Storyteller Harmony census: target missing.";
            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return "T15 Storyteller Harmony census: no patches.";

            StringBuilder sb = new StringBuilder(3072);
            int foreign = 0;
            Append(sb, "Prefix", info.Prefixes, ref foreign);
            Append(sb, "Postfix", info.Postfixes, ref foreign);
            Append(sb, "Transpiler", info.Transpilers, ref foreign);
            Append(sb, "Finalizer", info.Finalizers, ref foreign);
            sb.Insert(0, "T15 Storyteller Harmony census: foreignPatches=" + foreign + ". ");
            sb.Append(" Census is on-demand only; T15 does not wrap foreign Storyteller patches.");
            return sb.ToString();
        }

        private static void Append(StringBuilder sb, string kind, IEnumerable<Patch> patches, ref int foreign)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                bool ours = string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal);
                if (!ours) foreign++;
                MethodInfo method = patch.PatchMethod;
                sb.Append(kind).Append("[owner=").Append(patch.owner ?? "<null>")
                  .Append(",priority=").Append(patch.priority)
                  .Append(",method=").Append(method == null ? "<null>" : method.DeclaringType.FullName + "." + method.Name)
                  .Append(ours ? ",RimMT] " : ",FOREIGN] ");
            }
        }

        private struct Entry
        {
            internal readonly long Frame;
            internal readonly int Tick;
            internal readonly long Us;
            internal Entry(long frame, int tick, long us) { Frame = frame; Tick = tick; Us = us; }
        }
    }
}
'@
Set-Content $storyPath $story -Encoding UTF8

# Bound the dormant V0.4.8 detail profiler for T15: larger enough to catch recurring 20-60ms
# work tails, still one short session and fully removed afterward.
$detailPath = 'RimMT/Source/RimMT/Patches/WorkGiverDetailPatches.cs'
$detail = Get-Content $detailPath -Raw
$detail = Replace-OrThrow $detail 'private const int CaptureJobPackages = 32;' 'private const int CaptureJobPackages = 64;' 'T15 bounded detail package count'
$detail = Replace-OrThrow $detail 'JobGiver detail capture V0.4.8 started for up to ' 'T15 JobGiver detail capture started for up to ' 'T15 detail start log marker'
$detail = Replace-OrThrow $detail 'JobGiver detail capture V0.4.8 stopped and temporary detours were removed. ' 'T15 JobGiver detail capture stopped and temporary detours were removed. ' 'T15 detail stop log marker'
Set-Content $detailPath $detail -Encoding UTF8

$profPath = 'RimMT/Source/RimMT/Diagnostics/WorkGiverProfiler.cs'
$prof = Get-Content $profPath -Raw
$prof = Replace-OrThrow $prof 'private const int MaxSlowTraces = 8;' 'private const int MaxSlowTraces = 16;' 'T15 retained slow trace capacity'
$prof = Replace-OrThrow $prof @'
        private static readonly long Threshold16Ticks = Math.Max(1L, Stopwatch.Frequency * 16L / 1000L);
        private static readonly long Threshold64Ticks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);
'@ @'
        private static readonly long Threshold16Ticks = Math.Max(1L, Stopwatch.Frequency * 16L / 1000L);
        private static readonly long Threshold20Ticks = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);
        private static readonly long Threshold64Ticks = Math.Max(1L, Stopwatch.Frequency * 64L / 1000L);
'@ 'T15 20ms slow package threshold'
$prof = Replace-OrThrow $prof @'
                if (elapsed >= Threshold64Ticks)
                {
                    slowJobPackages++;
                    SaveSlowTrace(elapsed);
                }
'@ @'
                if (elapsed >= Threshold20Ticks)
                {
                    slowJobPackages++;
                    SaveSlowTrace(elapsed);
                }
'@ 'T15 retain >=20ms package traces'
$prof = Replace-OrThrow $prof '.Append(", slowPackages>=64ms=").Append(slowJobPackages)' '.Append(", slowPackages>=20ms=").Append(slowJobPackages)' 'T15 profiler summary threshold label'
Set-Content $profPath $prof -Encoding UTF8

# Trigger the deferred burst from the already-bounded T2 DetermineNextJob timing.
$t2DiagPath = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$t2Diag = Get-Content $t2DiagPath -Raw
$t2Diag = Replace-OrThrow $t2Diag @'
            long us = TicksToUs(elapsed);
            int p = (int)phase;
'@ @'
            long us = TicksToUs(elapsed);
            if (phase == PawnTailPhase093T2.DetermineNextJob && us >= 20000L)
                WorkGiverDeepAttribution093T15.ObserveDetermine(us);
            int p = (int)phase;
'@ 'T15 reuse T2 DetermineNextJob elapsed timing'
Set-Content $t2DiagPath $t2Diag -Encoding UTF8

# Reuse T1 Storyteller timing; no second timer or Storyteller hot patch.
$t1DiagPath = 'RimMT/Source/RimMT/Diagnostics/TailAttribution093T1.cs'
$t1Diag = Get-Content $t1DiagPath -Raw
$t1Diag = Replace-OrThrow $t1Diag @'
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;

            Calls[p]++;
'@ @'
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;
            if (phase == TailPhase093T1.Storyteller && us >= 100000L)
                StorytellerCatastrophic093T15.Observe(us);

            Calls[p]++;
'@ 'T15 Storyteller catastrophic event hook'
Set-Content $t1DiagPath $t1Diag -Encoding UTF8

# Deferred Harmony install/uninstall is driven from the existing main-thread frame boundary.
$runtimePath = 'RimMT/Source/RimMT/Core/RimMTRuntime.cs'
$runtime = Get-Content $runtimePath -Raw
$runtime = Replace-OrThrow $runtime @'
            Interlocked.Increment(ref mainThreadFrames);
            if (scheduler != null) scheduler.SampleProductionConcurrency();

            bool logicalTickBoundary = true;
'@ @'
            Interlocked.Increment(ref mainThreadFrames);
            if (scheduler != null) scheduler.SampleProductionConcurrency();
            WorkGiverDeepAttribution093T15.OnMainThreadFrame();

            bool logicalTickBoundary = true;
'@ 'T15 deferred detail lifecycle at frame boundary'
Set-Content $runtimePath $runtime -Encoding UTF8

# Initialize the dormant detail patch manager, but do not install WorkGiver detail detours at startup.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPawnPatches093T2.Apply(harmony);
            PlayerHumanResidualPatches093T14.Apply(harmony);
'@ @'
            TailPawnPatches093T2.Apply(harmony);
            PlayerHumanResidualPatches093T14.Apply(harmony);
            WorkGiverDetailPatches.Initialize(harmony);
'@ 'T15 initialize on-demand WorkGiver detail manager'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t14-playerhuman-residual-attribution";' 'internal const string Version = "0.9.3-t15-workgiver-deep-attribution";' 'T15 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T14 PlayerHuman Residual Attribution initialized. T8 production behavior retained; T13 aggregate attribution retained; T14 measures PlayerHumanlike original residual using a bounded Pawn.Tick transpiler at 1/256 periodic samples.' '[RimMT] V0.9.3-T15 WorkGiver Deep Attribution initialized. T14/T13/T8 production and diagnostic behavior retained; a sampled DetermineNextJob >=20ms requests one deferred 64-package WorkGiver detail burst that auto-unpatches; Storyteller >=100ms events reuse T1 timing.' 'T15 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(PlayerHumanResidualAttribution093T14.RecentSummary());
'@ @'
            sb.AppendLine(PlayerHumanResidualAttribution093T14.RecentSummary());
            sb.AppendLine(WorkGiverDeepAttribution093T15.Summary());
            sb.AppendLine(WorkGiverProfiler.Summary(24));
            sb.AppendLine(JobGiverInfrastructureProfiler.Summary(20));
            sb.AppendLine(StorytellerCatastrophic093T15.Summary());
            sb.AppendLine(StorytellerCatastrophic093T15.HarmonyCensus());
'@ 'T15 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T14 PlayerHuman Residual Attribution' 'V0.9.3-T15 WorkGiver Deep Attribution' 'T15 report title'
$report = Replace-OrThrow $report 'T8 production behavior retained + T13 aggregate attribution + T14 PlayerHuman residual decomposition via one Pawn.Tick measurement-only transpiler; T14 Stopwatch work only at 1/256 periodic PlayerHuman samples; no tracker method Harmony timers; no T9/T10/T11/T12 probe chain;' 'T8 production behavior retained + T13/T14 attribution retained + T15 one-shot WorkGiver deep burst; T15 trigger reuses T2 sampled DetermineNextJob >=20ms timing, installs temporary detail detours only at the next frame boundary for 64 outer JobGiver_Work packages, then auto-unpatches; Storyteller >=100ms recorder reuses T1 elapsed time; no resident WorkGiver profiler; no T9/T10/T11/T12 probe chain;' 'T15 production policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T14 PlayerHuman Residual Attribution', 'V0.9.3-T15 WorkGiver Deep Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T15: T14/T13/T8 retained; one deferred bounded WorkGiver detail burst after sampled DetermineNextJob >=20ms; temporary detours auto-unpatch; Storyteller >=100ms recorder reuses T1 timing.'