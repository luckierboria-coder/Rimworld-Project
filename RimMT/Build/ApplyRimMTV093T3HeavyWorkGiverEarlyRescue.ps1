$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T3 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T3 Heavy WorkGiver Early Rescue
# Tail-first behavior change, deliberately narrow:
# - Existing S4 32ms rescue remains the generic fallback.
# - Only WorkGivers that have ALREADY produced >=2 heavy S4 calls and >=512 true validator rejects
#   may enter the same S4 live-validator/live-CanReach path at 8ms on later calls.
# - No continuation, no prediction from source size, no worker wait, no authority changes.
# Diagnostic addition:
# - PathFinder.FindPath timing is collected only during T2 bounded deep-sample ticks.

$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw

$s4 = Replace-OrThrow $s4 @'
        private const int TailRescueThresholdMs = 32;
        private const int MaxSourceCount = 16384;
'@ @'
        private const int TailRescueThresholdMs = 32;
        private const int EarlyKnownHeavyThresholdMs = 8;
        private const long EarlyKnownHeavyMinCalls = 2;
        private const long EarlyKnownHeavyMinRejects = 512;
        private const int MaxSourceCount = 16384;
'@ 'S4 T3 early-rescue constants'

$s4 = Replace-OrThrow $s4 @'
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);
'@ @'
        private static readonly long TailRescueThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailRescueThresholdMs / 1000L);
        private static readonly long EarlyKnownHeavyThresholdTicks = Math.Max(1L, Stopwatch.Frequency * EarlyKnownHeavyThresholdMs / 1000L);
'@ 'S4 T3 early-rescue tick budget'

$s4 = Replace-OrThrow $s4 @'
        private static long heavyWorkGiverUnresolved;
'@ @'
        private static long heavyWorkGiverUnresolved;
        private static long earlyKnownChecks;
        private static long earlyKnownHits;
        private static long earlyKnownListAdmissions;
        private static long earlyKnownCustomAdmissions;
'@ 'S4 T3 early-rescue counters'

$s4 = Replace-OrThrow $s4 @'
            if (__7 != null)
            {
                if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ @'
            if (__7 != null)
            {
                long elapsedScope = Stopwatch.GetTimestamp() - scopeStart;
                if (elapsedScope < TailRescueThresholdTicks)
                {
                    if (elapsedScope < EarlyKnownHeavyThresholdTicks || !IsKnownHeavyWorkGiver(__6)) return true;
                    earlyKnownCustomAdmissions++;
                }
                customTailEligible++;
                return TryAccelerateCustom(__7, __0, map, __3, __4, __5, __6, RescueRoute.CustomTail, ref __result);
            }
'@ 'S4 T3 custom early rescue'

$s4 = Replace-OrThrow $s4 @'
            if (count < TailMinSourceCount) return true;
            if (Stopwatch.GetTimestamp() - scopeStart < TailRescueThresholdTicks) return true;

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ @'
            if (count < TailMinSourceCount) return true;
            long elapsedSmallList = Stopwatch.GetTimestamp() - scopeStart;
            if (elapsedSmallList < TailRescueThresholdTicks)
            {
                if (elapsedSmallList < EarlyKnownHeavyThresholdTicks || !IsKnownHeavyWorkGiver(__6)) return true;
                earlyKnownListAdmissions++;
            }

            tailEligible++;
            return TryAccelerateList(source, count, __0, map, __3, __4, __5, __6, RescueRoute.TailList, ref __result);
'@ 'S4 T3 list early rescue'

$s4 = Replace-OrThrow $s4 @'
        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
'@ @'
        private static bool IsKnownHeavyWorkGiver(Predicate<Thing> validator)
        {
            earlyKnownChecks++;
            WorkGiver_Scanner scanner = TryResolveScanner(validator);
            if (scanner == null) return false;

            string key = scanner.def == null || string.IsNullOrEmpty(scanner.def.defName)
                ? scanner.GetType().FullName
                : scanner.def.defName;
            if (string.IsNullOrEmpty(key)) return false;

            HeavyValidatorStats stats;
            if (!HeavyWorkGivers.TryGetValue(key, out stats) || stats == null) return false;
            if (stats.Calls < EarlyKnownHeavyMinCalls || stats.Rejects < EarlyKnownHeavyMinRejects) return false;
            earlyKnownHits++;
            return true;
        }

        private static PenPrefilterKind ResolvePenPrefilter(WorkGiver_Scanner scanner)
'@ 'S4 T3 known-heavy resolver'

$s4 = Replace-OrThrow $s4 @'
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", failures=" + failures +
'@ @'
                   ", heavyWorkGiverUnresolved=" + heavyWorkGiverUnresolved +
                   ", earlyKnownChecks=" + earlyKnownChecks +
                   ", earlyKnownHits=" + earlyKnownHits +
                   ", earlyKnownAdmissions=" + (earlyKnownListAdmissions + earlyKnownCustomAdmissions) +
                   " [list=" + earlyKnownListAdmissions + ", custom=" + earlyKnownCustomAdmissions + "]" +
                   ", earlyKnownPolicy=" + EarlyKnownHeavyThresholdMs + "ms/" + EarlyKnownHeavyMinCalls + "calls/" + EarlyKnownHeavyMinRejects + "rejects" +
                   ", failures=" + failures +
'@ 'S4 T3 summary counters'

Set-Content $s4Path $s4 -Encoding UTF8

# Bounded PathFinder attribution. This does not enable the old path profiler feature gate.
$pathDiagPath = 'RimMT/Source/RimMT/Diagnostics/TailPathfinderAttribution093T3.cs'
$pathDiag = @'
using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    internal static class TailPathfinderAttribution093T3
    {
        private const int RecentCapacity = 16;
        private static readonly PathFrame[] Recent = new PathFrame[RecentCapacity];

        [ThreadStatic] private static bool active;
        [ThreadStatic] private static long currentUs;
        [ThreadStatic] private static long currentMaxCallUs;
        [ThreadStatic] private static int currentCalls;

        private static long calls;
        private static long totalUs;
        private static long maxUs;
        private static long over5;
        private static long over10;
        private static long over20;
        private static long over50;
        private static int recentPos;
        private static int recentCount;

        internal static void BeginTick()
        {
            active = true;
            currentUs = 0L;
            currentMaxCallUs = 0L;
            currentCalls = 0;
        }

        internal static long BeginCall()
        {
            if (!active || !TailPawnAttribution093T2.DeepActive || !RimMTThreadGuard.IsMainThread) return 0L;
            return Stopwatch.GetTimestamp();
        }

        internal static void EndCall(long started)
        {
            if (started == 0L || !active) return;
            long elapsedTicks = Stopwatch.GetTimestamp() - started;
            if (elapsedTicks <= 0L) return;
            long us = TicksToUs(elapsedTicks);
            calls++;
            totalUs += us;
            currentUs += us;
            currentCalls++;
            if (us > currentMaxCallUs) currentMaxCallUs = us;
            if (us > maxUs) maxUs = us;
            if (us >= 5000L) over5++;
            if (us >= 10000L) over10++;
            if (us >= 20000L) over20++;
            if (us >= 50000L) over50++;
        }

        internal static void EndTick(long tickUs)
        {
            if (!active) return;
            active = false;
            if (tickUs < 20000L || currentCalls <= 0) return;

            int gameTick = -1;
            try { if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame; }
            catch { }
            Recent[recentPos] = new PathFrame(RimMTRuntime.MainThreadFrames, gameTick, tickUs, currentCalls, currentUs, currentMaxCallUs);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static string Summary()
        {
            double avg = calls == 0L ? 0.0 : totalUs / (double)calls;
            return "T3 PathFinder deep attribution: calls=" + calls +
                   ", avgUs=" + avg.ToString("F1") +
                   ", >5/10/20/50=" + over5 + "/" + over10 + "/" + over20 + "/" + over50 +
                   ", maxMs=" + (maxUs / 1000.0).ToString("F2") + ".";
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "T3 recent deep >=20ms ticks with FindPath: none.";
            StringBuilder sb = new StringBuilder(2048);
            sb.Append("T3 recent deep >=20ms ticks with FindPath (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                if (i != 0) sb.Append("; ");
                PathFrame e = Recent[(start + i) % RecentCapacity];
                sb.Append("frame=").Append(e.Frame).Append(",tick=").Append(e.GameTick)
                    .Append(",total=").Append((e.TickUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",findCalls=").Append(e.Calls)
                    .Append(",findTotal=").Append((e.FindUs / 1000.0).ToString("F2")).Append("ms")
                    .Append(",findMax=").Append((e.MaxCallUs / 1000.0).ToString("F2")).Append("ms");
            }
            return sb.ToString();
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct PathFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long TickUs;
            internal readonly int Calls;
            internal readonly long FindUs;
            internal readonly long MaxCallUs;
            internal PathFrame(long frame, int gameTick, long tickUs, int calls, long findUs, long maxCallUs)
            {
                Frame = frame; GameTick = gameTick; TickUs = tickUs; Calls = calls; FindUs = findUs; MaxCallUs = maxCallUs;
            }
        }
    }
}
'@
Set-Content $pathDiagPath $pathDiag -Encoding UTF8

$pathPatchPath = 'RimMT/Source/RimMT/Patches/TailPathfinderPatches093T3.cs'
$pathPatch = @'
using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class TailPathfinderPatches093T3
    {
        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            int patched = 0;
            try
            {
                MethodInfo[] methods = typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != "FindPath") continue;
                    HarmonyMethod prefix = new HarmonyMethod(typeof(TailPathfinderPatches093T3), nameof(Prefix)) { priority = Priority.First };
                    HarmonyMethod postfix = new HarmonyMethod(typeof(TailPathfinderPatches093T3), nameof(Postfix)) { priority = Priority.Last };
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    patched++;
                }
                Log.Message("[RimMT] T3 bounded PathFinder attribution installed on " + patched + " FindPath overload(s); Stopwatch is active only inside T2 deep windows.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T3 PathFinder attribution failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPathfinderAttribution093T3.BeginCall() : 0L;
        }

        public static void Postfix(long __state)
        {
            if (__state != 0L) TailPathfinderAttribution093T3.EndCall(__state);
        }
    }
}
'@
Set-Content $pathPatchPath $pathPatch -Encoding UTF8

$t2Path = 'RimMT/Source/RimMT/Diagnostics/TailPawnAttribution093T2.cs'
$t2 = Get-Content $t2Path -Raw
$t2 = Replace-OrThrow $t2 @'
            deepActive = periodic || burst;
            if (!deepActive) return;

            deepTicks++;
'@ @'
            deepActive = periodic || burst;
            if (!deepActive) return;

            TailPathfinderAttribution093T3.BeginTick();
            deepTicks++;
'@ 'T3 PathFinder begin deep window'
$t2 = Replace-OrThrow $t2 @'
        internal static void EndTick(long totalUs)
        {
            bool wasDeep = deepActive;
            deepActive = false;
'@ @'
        internal static void EndTick(long totalUs)
        {
            TailPathfinderAttribution093T3.EndTick(totalUs);
            bool wasDeep = deepActive;
            deepActive = false;
'@ 'T3 PathFinder end deep window'
Set-Content $t2Path $t2 -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot @'
            TailPawnPatches093T2.Apply(harmony);
'@ @'
            TailPawnPatches093T2.Apply(harmony);
            TailPathfinderPatches093T3.Apply(harmony);
'@ 'install T3 PathFinder attribution patches'
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t2-pawn-tail-attribution";' 'internal const string Version = "0.9.3-t3-heavy-workgiver-early-rescue";' 'T3 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T2 Pawn Tail Attribution initialized.' '[RimMT] V0.9.3-T3 Heavy WorkGiver Early Rescue initialized.' 'T3 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = Replace-OrThrow $report @'
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
'@ @'
            sb.AppendLine(TailPawnAttribution093T2.Summary());
            sb.AppendLine(TailPawnAttribution093T2.RecentSummary());
            sb.AppendLine(TailPathfinderAttribution093T3.Summary());
            sb.AppendLine(TailPathfinderAttribution093T3.RecentSummary());
'@ 'T3 PathFinder report lines'
$report = Replace-OrThrow $report 'V0.9.3-T2 Pawn Tail Attribution' 'V0.9.3-T3 Heavy WorkGiver Early Rescue' 'T3 report title'
$report = Replace-OrThrow $report 'T0 histogram + T1 top-level phase/GC attribution + T2 bounded Pawn/AI deep attribution;' 'T0 histogram + T1 top-level + T2 bounded Pawn/AI + T3 bounded FindPath attribution; S4 proven-heavy early rescue=8ms;' 'T3 policy marker'
$report = Replace-OrThrow $report 'S4 tail=32ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'S4 tail=32ms + proven-heavy early rescue=8ms + true-validator attribution + authority-safe corpse/holding/feed/visit pruners;' 'T3 S4 policy marker'
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T2 Pawn Tail Attribution', 'V0.9.3-T3 Heavy WorkGiver Early Rescue')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T3: proven-heavy WorkGivers may use existing S4 rescue from 8ms after observed evidence; bounded PathFinder deep attribution added.'