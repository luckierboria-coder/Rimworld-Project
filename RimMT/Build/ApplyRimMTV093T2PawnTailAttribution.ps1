$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T2 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T2 Pawn Tail Attribution
# Diagnostic-only child of T1A. Keeps production optimizers unchanged.
# Deep Stopwatch timing is active only on 1/64 periodic ticks and a four-tick burst after >=50 ms ticks.
# Normal ticks pay only Harmony dispatch + one boolean guard on the selected Pawn/AI methods.

$diagPath = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$diag = @'
using System;
using System.Diagnostics;
using System.Text;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal enum PawnTailPhase093T2
    {
        PawnTick = 0,
        JobTrackerTick = 1,
        DetermineNextJob = 2,
        CheckForJobOverride = 3,
        PatherTick = 4,
        Count = 5
    }

    /// <summary>
    /// Bounded deep attribution for TickListNormal. Stopwatch work is disabled on ordinary ticks.
    /// A periodic 1/64 sample plus a short post-spike burst provides enough coverage to identify
    /// recurring pawn/AI tails without turning production into a per-Pawn profiler.
    /// </summary>
    internal static class TailPawnAttribution093T2
    {
        private const int PhaseCount = (int)PawnTailPhase093T2.Count;
        private const int RecentCapacity = 16;
        private const int BurstTicksAfterSevere = 4;

        private static readonly string[] PhaseNames = new string[]
        {
            "PawnTick", "JobTrackerTick", "DetermineNextJob", "CheckForJobOverride", "PatherTick"
        };

        private static readonly long[] Calls = new long[PhaseCount];
        private static readonly long[] TotalUs = new long[PhaseCount];
        private static readonly long[] MaxUs = new long[PhaseCount];
        private static readonly long[] Over5 = new long[PhaseCount];
        private static readonly long[] Over10 = new long[PhaseCount];
        private static readonly long[] Over20 = new long[PhaseCount];
        private static readonly long[] CurrentUs = new long[PhaseCount];
        private static readonly DeepFrame[] Recent = new DeepFrame[RecentCapacity];

        [ThreadStatic] private static bool deepActive;
        [ThreadStatic] private static Pawn topPawn1;
        [ThreadStatic] private static Pawn topPawn2;
        [ThreadStatic] private static Pawn topPawn3;
        [ThreadStatic] private static long topPawn1Us;
        [ThreadStatic] private static long topPawn2Us;
        [ThreadStatic] private static long topPawn3Us;

        private static int burstRemaining;
        private static long observedTicks;
        private static long deepTicks;
        private static long deepTail20;
        private static long deepTail50;
        private static int recentPos;
        private static int recentCount;

        internal static bool DeepActive { get { return deepActive; } }

        internal static void BeginTick()
        {
            observedTicks++;
            int gameTick = 0;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }

            bool periodic = (gameTick & 63) == 0;
            bool burst = burstRemaining > 0;
            if (burstRemaining > 0) burstRemaining--;
            deepActive = periodic || burst;
            if (!deepActive) return;

            deepTicks++;
            Array.Clear(CurrentUs, 0, CurrentUs.Length);
            topPawn1 = topPawn2 = topPawn3 = null;
            topPawn1Us = topPawn2Us = topPawn3Us = 0L;
        }

        internal static long BeginPhase()
        {
            if (!deepActive || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndPhase(long started, PawnTailPhase093T2 phase)
        {
            if (started == 0L || !deepActive) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            int p = (int)phase;
            if (p < 0 || p >= PhaseCount) return;

            Calls[p]++;
            TotalUs[p] += us;
            CurrentUs[p] += us;
            if (us > MaxUs[p]) MaxUs[p] = us;
            if (us >= 5000L) Over5[p]++;
            if (us >= 10000L) Over10[p]++;
            if (us >= 20000L) Over20[p]++;
        }

        internal static void EndPawn(long started, Pawn pawn)
        {
            if (started == 0L || !deepActive) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed <= 0L) return;
            long us = TicksToUs(elapsed);
            int p = (int)PawnTailPhase093T2.PawnTick;
            Calls[p]++;
            TotalUs[p] += us;
            CurrentUs[p] += us;
            if (us > MaxUs[p]) MaxUs[p] = us;
            if (us >= 5000L) Over5[p]++;
            if (us >= 10000L) Over10[p]++;
            if (us >= 20000L) Over20[p]++;

            if (us > topPawn1Us)
            {
                topPawn3 = topPawn2; topPawn3Us = topPawn2Us;
                topPawn2 = topPawn1; topPawn2Us = topPawn1Us;
                topPawn1 = pawn; topPawn1Us = us;
            }
            else if (us > topPawn2Us)
            {
                topPawn3 = topPawn2; topPawn3Us = topPawn2Us;
                topPawn2 = pawn; topPawn2Us = us;
            }
            else if (us > topPawn3Us)
            {
                topPawn3 = pawn; topPawn3Us = us;
            }
        }

        internal static void EndTick(long totalUs)
        {
            bool wasDeep = deepActive;
            deepActive = false;

            if (totalUs >= 50000L && burstRemaining < BurstTicksAfterSevere)
                burstRemaining = BurstTicksAfterSevere;

            if (!wasDeep || totalUs < 20000L) return;
            deepTail20++;
            if (totalUs >= 50000L) deepTail50++;

            long pawnUs = CurrentUs[(int)PawnTailPhase093T2.PawnTick];
            long jobsUs = CurrentUs[(int)PawnTailPhase093T2.JobTrackerTick];
            long determineUs = CurrentUs[(int)PawnTailPhase093T2.DetermineNextJob];
            long overrideUs = CurrentUs[(int)PawnTailPhase093T2.CheckForJobOverride];
            long patherUs = CurrentUs[(int)PawnTailPhase093T2.PatherTick];

            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }

            Recent[recentPos] = new DeepFrame(
                RimMTRuntime.MainThreadFrames, gameTick, totalUs,
                pawnUs, jobsUs, determineUs, overrideUs, patherUs,
                topPawn1, topPawn1Us, topPawn2, topPawn2Us, topPawn3, topPawn3Us);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T2 pawn-tail attribution: observedTicks=").Append(observedTicks)
                .Append(", deepTicks=").Append(deepTicks)
                .Append(", deepRate=").Append(observedTicks == 0 ? "0.00" : (deepTicks * 100.0 / observedTicks).ToString("F2")).Append('%')
                .Append(", deepTail20=").Append(deepTail20)
                .Append(", deepTail50=").Append(deepTail50)
                .Append(". Phase stats: ");
            for (int i = 0; i < PhaseCount; i++)
            {
                if (i != 0) sb.Append("; ");
                long calls = Calls[i];
                double avgUs = calls == 0L ? 0.0 : TotalUs[i] / (double)calls;
                sb.Append(PhaseNames[i]).Append("(calls=").Append(calls)
                    .Append(",avgUs=").Append(avgUs.ToString("F1"))
                    .Append(",>5/10/20=").Append(Over5[i]).Append('/').Append(Over10[i]).Append('/').Append(Over20[i])
                    .Append(",maxMs=").Append((MaxUs[i] / 1000.0).ToString("F2")).Append(')');
            }
            return sb.ToString();
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "T2 recent deep >=20ms tails: none.";
            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T2 recent deep >=20ms tails (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                DeepFrame e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TotalUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",pawn=").Append((e.PawnUs / 1000.0).ToString("F2"))
                    .Append(",jobTracker=").Append((e.JobTrackerUs / 1000.0).ToString("F2"))
                    .Append(",determine=").Append((e.DetermineUs / 1000.0).ToString("F2"))
                    .Append(",override=").Append((e.OverrideUs / 1000.0).ToString("F2"))
                    .Append(",pather=").Append((e.PatherUs / 1000.0).ToString("F2"))
                    .Append(",topPawns=").Append(PawnText(e.Pawn1, e.Pawn1Us))
                    .Append('/').Append(PawnText(e.Pawn2, e.Pawn2Us))
                    .Append('/').Append(PawnText(e.Pawn3, e.Pawn3Us));
            }
            return sb.ToString();
        }

        private static string PawnText(Pawn pawn, long us)
        {
            if (pawn == null || us <= 0L) return "None:0";
            string defName = pawn.def == null ? "Pawn" : pawn.def.defName;
            string job = "none";
            try { if (pawn.CurJobDef != null) job = pawn.CurJobDef.defName; }
            catch { }
            return defName + "#" + pawn.thingIDNumber + "[" + job + "]:" + (us / 1000.0).ToString("F2");
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct DeepFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TotalUs;
            internal readonly long PawnUs;
            internal readonly long JobTrackerUs;
            internal readonly long DetermineUs;
            internal readonly long OverrideUs;
            internal readonly long PatherUs;
            internal readonly Pawn Pawn1;
            internal readonly long Pawn1Us;
            internal readonly Pawn Pawn2;
            internal readonly long Pawn2Us;
            internal readonly Pawn Pawn3;
            internal readonly long Pawn3Us;

            internal DeepFrame(long frame, int gameTick, long totalUs,
                long pawnUs, long jobTrackerUs, long determineUs, long overrideUs, long patherUs,
                Pawn pawn1, long pawn1Us, Pawn pawn2, long pawn2Us, Pawn pawn3, long pawn3Us)
            {
                Frame = frame; GameTick = gameTick; TotalUs = totalUs;
                PawnUs = pawnUs; JobTrackerUs = jobTrackerUs; DetermineUs = determineUs;
                OverrideUs = overrideUs; PatherUs = patherUs;
                Pawn1 = pawn1; Pawn1Us = pawn1Us; Pawn2 = pawn2; Pawn2Us = pawn2Us; Pawn3 = pawn3; Pawn3Us = pawn3Us;
            }
        }
    }
}
'@
Set-Content $diagPath $diag -Encoding UTF8

$patchPath = 'RimMT/Source/RimMT/Patches/TailPawnPatches093T2.cs'
$patch = @'
using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class TailPawnPatches093T2
    {
        private static int patched;
        private static int missing;
        private static string missingNames = string.Empty;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            PatchOne(harmony, AccessTools.Method(typeof(Pawn), "Tick"), nameof(PawnPrefix), nameof(PawnPostfix), "Pawn.Tick");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"), nameof(PhasePrefix), nameof(JobTrackerPostfix), "Pawn_JobTracker.JobTrackerTick");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "DetermineNextJob"), nameof(PhasePrefix), nameof(DeterminePostfix), "Pawn_JobTracker.DetermineNextJob");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_JobTracker), "CheckForJobOverride_NewTemp"), nameof(PhasePrefix), nameof(OverridePostfix), "Pawn_JobTracker.CheckForJobOverride_NewTemp");
            PatchOne(harmony, AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), nameof(PhasePrefix), nameof(PatherPostfix), "Pawn_PathFollower.PatherTick");
            Log.Message("[RimMT] T2 Pawn Tail Attribution installed: patched=" + patched + ", missing=" + missing +
                (missing == 0 ? "." : ", missingTargets=" + missingNames + ".") +
                " Stopwatch timing only on bounded deep-sample ticks; optimizer behavior unchanged.");
        }

        private static void PatchOne(Harmony harmony, MethodBase target, string prefixName, string postfixName, string label)
        {
            if (target == null)
            {
                missing++;
                if (missingNames.Length != 0) missingNames += ",";
                missingNames += label;
                return;
            }
            try
            {
                HarmonyMethod prefix = new HarmonyMethod(typeof(TailPawnPatches093T2), prefixName) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(TailPawnPatches093T2), postfixName) { priority = Priority.Last };
                harmony.Patch(target, prefix: prefix, postfix: postfix);
                patched++;
            }
            catch (Exception ex)
            {
                missing++;
                Log.Warning("[RimMT] T2 pawn attribution target failed closed: " + label + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PhasePrefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }

        public static void PawnPrefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPawnAttribution093T2.BeginPhase() : 0L;
        }

        public static void PawnPostfix(Pawn __instance, long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPawn(__state, __instance);
        }

        public static void JobTrackerPostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.JobTrackerTick);
        }

        public static void DeterminePostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.DetermineNextJob);
        }

        public static void OverridePostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.CheckForJobOverride);
        }

        public static void PatherPostfix(long __state)
        {
            if (__state != 0L) TailPawnAttribution093T2.EndPhase(__state, PawnTailPhase093T2.PatherTick);
        }
    }
}
'@
Set-Content $patchPath $patch -Encoding UTF8

# Lifecycle integration into the already-generated T0 observer.
$t0Path = 'RimMT/Source/RimMT/Diagnostics/TailObservatory093T0.cs'
$t0 = Get-Content $t0Path -Raw
$t0 = Replace-OrThrow $t0 @'
        internal static void BeginTick()
        {
            TailAttribution093T1.BeginTick();
            currentSignals = TailSignal093T0.None;
'@ @'
        internal static void BeginTick()
        {
            TailAttribution093T1.BeginTick();
            TailPawnAttribution093T2.BeginTick();
            currentSignals = TailSignal093T0.None;
'@ 'T2 BeginTick lifecycle'
$t0 = Replace-OrThrow $t0 @'
            long us = TicksToUs(elapsedTicks);
            TailAttribution093T1.EndTick(us);
            samples++;
'@ @'
            long us = TicksToUs(elapsedTicks);
            TailAttribution093T1.EndTick(us);
            TailPawnAttribution093T2.EndTick(us);
            samples++;
'@ 'T2 EndTick lifecycle'
Set-Content $t0Path $t0 -Encoding UTF8

# Install after T1A production/diagnostic patch set.
$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailAttributionPatches093T1.Apply(harmony);
'@ @'
            TailAttributionPatches093T1.Apply(harmony);
            TailPawnPatches093T2.Apply(harmony);
'@ 'install T2 pawn attribution patches'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t1a-no-global-fuse";' 'internal const string Version = "0.9.3-t2-pawn-tail-attribution";' 'T2 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T1A Tail Attribution No Global Fuse initialized.' '[RimMT] V0.9.3-T2 Pawn Tail Attribution initialized.' 'T2 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TailAttribution093T1.Summary());
            sb.AppendLine(TailAttribution093T1.RecentSevereSummary());
'@ @'
            sb.AppendLine(TailAttribution093T1.Summary());
            sb.AppendLine(TailAttribution093T1.RecentSevereSummary());
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
'@ 'T2 report lines'
$report = Replace-OrThrow $report 'V0.9.3-T1A Tail Attribution No Global Fuse' 'V0.9.3-T2 Pawn Tail Attribution' 'T2 report title'
$report = Replace-OrThrow $report 'T0 histogram + top-level phase/GC attribution;' 'T0 histogram + T1 top-level phase/GC attribution + T2 bounded Pawn/AI deep attribution;' 'T2 policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T1A Tail Attribution No Global Fuse', 'V0.9.3-T2 Pawn Tail Attribution')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T2 Pawn Tail Attribution: bounded deep sampling only; no optimizer behavior changes.'