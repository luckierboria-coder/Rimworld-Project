$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T0 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T0 Tail Observatory
# Measurement-only release on top of the exact V0.9.3 Consolidated Stable production composition.
# No optimizer/admission/budget/authority behavior is changed here.
# Hot-path cost is bounded to one DoSingleTick timestamp pair + O(1) fixed histogram update.
# Existing ReachProfile timers are reused for component attribution; no extra ReachProfile stopwatch is added.

$tailPath = 'RimMT/Source/RimMT/Diagnostics/TailObservatory093T0.cs'
$tail = @'
using System;
using System.Diagnostics;
using System.Text;
using Verse;

namespace RimMT
{
    [Flags]
    internal enum TailSignal093T0
    {
        None = 0,
        ReachQuery = 1,
        ReachCapture = 2,
        ReachTopologySlice = 4,
        S4HeavyValidator = 8
    }

    /// <summary>
    /// Measurement-only long-tail observer. The hot path is main-thread owned, allocation-free and
    /// lock-free: one fixed histogram increment, a few threshold counters and (only for >=20 ms
    /// ticks) one write into a fixed recent-tail ring. It never changes scheduling or game state.
    /// </summary>
    internal static class TailObservatory093T0
    {
        private const int BucketUs = 250;
        private const int HistogramCeilingUs = 250000;
        private const int HistogramBuckets = HistogramCeilingUs / BucketUs + 2; // final bucket is overflow
        private const int RecentCapacity = 16;

        private static readonly long FiveMsTicks = Math.Max(1L, Stopwatch.Frequency * 5L / 1000L);
        private static readonly long TenMsTicks = Math.Max(1L, Stopwatch.Frequency * 10L / 1000L);
        private static readonly long TwentyMsTicks = Math.Max(1L, Stopwatch.Frequency * 20L / 1000L);

        private static readonly long[] Histogram = new long[HistogramBuckets];
        private static readonly TailFrame[] Recent = new TailFrame[RecentCapacity];

        private static long samples;
        private static long totalUs;
        private static long maxUs;
        private static long over20;
        private static long over30;
        private static long over50;
        private static long over100;
        private static int recentPos;
        private static int recentCount;

        private static long reachQueryOver5;
        private static long reachQueryOver10;
        private static long reachQueryOver20;
        private static long reachQueryMaxUs;
        private static long reachCaptureOver5;
        private static long reachCaptureOver10;
        private static long reachCaptureOver20;
        private static long reachCaptureMaxUs;
        private static long topologyOver5;
        private static long topologyOver10;
        private static long topologyOver20;
        private static long topologyMaxUs;
        private static long s4HeavyEvents;
        private static int s4MaxRejects;

        [ThreadStatic] private static TailSignal093T0 currentSignals;
        [ThreadStatic] private static long currentReachQueryMaxUs;
        [ThreadStatic] private static long currentReachCaptureMaxUs;
        [ThreadStatic] private static long currentTopologyMaxUs;
        [ThreadStatic] private static int currentS4MaxRejects;

        internal static void BeginTick()
        {
            currentSignals = TailSignal093T0.None;
            currentReachQueryMaxUs = 0L;
            currentReachCaptureMaxUs = 0L;
            currentTopologyMaxUs = 0L;
            currentS4MaxRejects = 0;
        }

        internal static void RecordTick(long startTimestamp, long endTimestamp)
        {
            long elapsedTicks = endTimestamp - startTimestamp;
            if (elapsedTicks <= 0L) return;

            long us = TicksToUs(elapsedTicks);
            samples++;
            totalUs += us;
            if (us > maxUs) maxUs = us;

            int bucket = (int)(us / BucketUs);
            if (bucket >= HistogramBuckets - 1) bucket = HistogramBuckets - 1;
            Histogram[bucket]++;

            if (us >= 20000L) over20++;
            if (us >= 30000L) over30++;
            if (us >= 50000L) over50++;
            if (us >= 100000L) over100++;

            if (us < 20000L) return;

            int gameTick = -1;
            try
            {
                if (Find.TickManager != null) gameTick = Find.TickManager.TicksGame;
            }
            catch { }

            Recent[recentPos] = new TailFrame(
                RimMTRuntime.MainThreadFrames,
                gameTick,
                us,
                currentSignals,
                currentReachQueryMaxUs,
                currentReachCaptureMaxUs,
                currentTopologyMaxUs,
                currentS4MaxRejects);
            recentPos = (recentPos + 1) % RecentCapacity;
            if (recentCount < RecentCapacity) recentCount++;
        }

        internal static void NoteReachQueryTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachQuery;
            if (us > currentReachQueryMaxUs) currentReachQueryMaxUs = us;
            reachQueryOver5++;
            if (elapsedTicks >= TenMsTicks) reachQueryOver10++;
            if (elapsedTicks >= TwentyMsTicks) reachQueryOver20++;
            if (us > reachQueryMaxUs) reachQueryMaxUs = us;
        }

        internal static void NoteReachCaptureTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachCapture;
            if (us > currentReachCaptureMaxUs) currentReachCaptureMaxUs = us;
            reachCaptureOver5++;
            if (elapsedTicks >= TenMsTicks) reachCaptureOver10++;
            if (elapsedTicks >= TwentyMsTicks) reachCaptureOver20++;
            if (us > reachCaptureMaxUs) reachCaptureMaxUs = us;
        }

        internal static void NoteTopologySliceTicks(long elapsedTicks)
        {
            if (elapsedTicks < FiveMsTicks) return;
            long us = TicksToUs(elapsedTicks);
            currentSignals |= TailSignal093T0.ReachTopologySlice;
            if (us > currentTopologyMaxUs) currentTopologyMaxUs = us;
            topologyOver5++;
            if (elapsedTicks >= TenMsTicks) topologyOver10++;
            if (elapsedTicks >= TwentyMsTicks) topologyOver20++;
            if (us > topologyMaxUs) topologyMaxUs = us;
        }

        internal static void NoteS4HeavyValidator(int rejects)
        {
            if (rejects <= 0) return;
            currentSignals |= TailSignal093T0.S4HeavyValidator;
            if (rejects > currentS4MaxRejects) currentS4MaxRejects = rejects;
            s4HeavyEvents++;
            if (rejects > s4MaxRejects) s4MaxRejects = rejects;
        }

        internal static string Summary()
        {
            long n = samples;
            double avgMs = n == 0L ? 0.0 : totalUs / (double)n / 1000.0;
            return "Tail Observatory V0.9.3-T0: samples=" + n +
                ", avgMs=" + avgMs.ToString("F3") +
                ", P50ms=" + (PercentileUs(0.50) / 1000.0).ToString("F3") +
                ", P95ms=" + (PercentileUs(0.95) / 1000.0).ToString("F3") +
                ", P99ms=" + (PercentileUs(0.99) / 1000.0).ToString("F3") +
                ", P99.9ms=" + (PercentileUs(0.999) / 1000.0).ToString("F3") +
                ", >20ms=" + over20 +
                ", >30ms=" + over30 +
                ", >50ms=" + over50 +
                ", >100ms=" + over100 +
                ", maxMs=" + (maxUs / 1000.0).ToString("F3") +
                ", spike20Per10k=" + RatePer10k(over20, n).ToString("F2") +
                ", spike50Per10k=" + RatePer10k(over50, n).ToString("F2") +
                ". Histogram resolution=" + BucketUs + "us; final bucket >=" + HistogramCeilingUs + "us.";
        }

        internal static string ComponentSummary()
        {
            return "Tail component signals (measurement-only): ReachQuery >5/>10/>20ms=" +
                reachQueryOver5 + "/" + reachQueryOver10 + "/" + reachQueryOver20 +
                ", maxMs=" + (reachQueryMaxUs / 1000.0).ToString("F3") +
                "; ReachCapture=" + reachCaptureOver5 + "/" + reachCaptureOver10 + "/" + reachCaptureOver20 +
                ", maxMs=" + (reachCaptureMaxUs / 1000.0).ToString("F3") +
                "; TopologySlice=" + topologyOver5 + "/" + topologyOver10 + "/" + topologyOver20 +
                ", maxMs=" + (topologyMaxUs / 1000.0).ToString("F3") +
                "; S4HeavyValidatorEvents=" + s4HeavyEvents + ", maxRejects=" + s4MaxRejects + ".";
        }

        internal static string RecentSummary()
        {
            if (recentCount <= 0) return "Recent >=20ms tail frames: none.";
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("Recent >=20ms tail frames (oldest->newest): ");
            int start = recentCount == RecentCapacity ? recentPos : 0;
            for (int i = 0; i < recentCount; i++)
            {
                int idx = (start + i) % RecentCapacity;
                TailFrame e = Recent[idx];
                if (i != 0) sb.Append("; ");
                sb.Append("frame=").Append(e.Frame)
                    .Append(",tick=").Append(e.GameTick)
                    .Append(",ms=").Append((e.DurationUs / 1000.0).ToString("F2"))
                    .Append(",signals=").Append(e.Signals);
                if (e.ReachQueryMaxUs > 0) sb.Append(",reachQ=").Append((e.ReachQueryMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.ReachCaptureMaxUs > 0) sb.Append(",capture=").Append((e.ReachCaptureMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.TopologyMaxUs > 0) sb.Append(",topology=").Append((e.TopologyMaxUs / 1000.0).ToString("F2")).Append("ms");
                if (e.S4MaxRejects > 0) sb.Append(",s4Rejects=").Append(e.S4MaxRejects);
            }
            return sb.ToString();
        }

        private static long PercentileUs(double percentile)
        {
            long n = samples;
            if (n <= 0L) return 0L;
            long target = (long)Math.Ceiling(n * percentile);
            if (target < 1L) target = 1L;
            long cumulative = 0L;
            for (int i = 0; i < Histogram.Length; i++)
            {
                cumulative += Histogram[i];
                if (cumulative >= target)
                {
                    if (i >= Histogram.Length - 1) return Math.Max(HistogramCeilingUs, maxUs);
                    return (long)i * BucketUs + BucketUs / 2;
                }
            }
            return maxUs;
        }

        private static double RatePer10k(long value, long n)
        {
            return n <= 0L ? 0.0 : value * 10000.0 / n;
        }

        private static long TicksToUs(long ticks)
        {
            return ticks <= 0L ? 0L : (long)(ticks * (1000000.0 / Stopwatch.Frequency));
        }

        private struct TailFrame
        {
            internal readonly long Frame;
            internal readonly int GameTick;
            internal readonly long DurationUs;
            internal readonly TailSignal093T0 Signals;
            internal readonly long ReachQueryMaxUs;
            internal readonly long ReachCaptureMaxUs;
            internal readonly long TopologyMaxUs;
            internal readonly int S4MaxRejects;

            internal TailFrame(long frame, int gameTick, long durationUs, TailSignal093T0 signals,
                long reachQueryMaxUs, long reachCaptureMaxUs, long topologyMaxUs, int s4MaxRejects)
            {
                Frame = frame;
                GameTick = gameTick;
                DurationUs = durationUs;
                Signals = signals;
                ReachQueryMaxUs = reachQueryMaxUs;
                ReachCaptureMaxUs = reachCaptureMaxUs;
                TopologyMaxUs = topologyMaxUs;
                S4MaxRejects = s4MaxRejects;
            }
        }
    }
}
'@
Set-Content $tailPath $tail -Encoding UTF8

# Make the existing minimal DoSingleTick patch independent of AdaptiveBurst for measurement only.
# AdaptiveLoadBalancer.RecordTick remains gated exactly as before, so scheduler behavior is unchanged.
$patchPath = 'RimMT/Source/RimMT/Patches/RimMTPatches.cs'
$patch = Get-Content $patchPath -Raw
$patch = Replace-OrThrow $patch @'
        public static void AdaptiveTickPrefix(ref long __state)
        {
            __state = 0L;
            if (!FeatureGate.IsEnabled("runtime.adaptiveBurst") || RuntimeCompatibility.ButterPlusPlusActive)
                return;
            __state = Stopwatch.GetTimestamp();
        }

        public static void AdaptiveTickPostfix(long __state)
        {
            if (__state != 0L)
                AdaptiveLoadBalancer.RecordTick(__state);
        }
'@ @'
        public static void AdaptiveTickPrefix(ref long __state)
        {
            __state = 0L;
            if (Current.ProgramState != ProgramState.Playing)
                return;
            TailObservatory093T0.BeginTick();
            __state = Stopwatch.GetTimestamp();
        }

        public static void AdaptiveTickPostfix(long __state)
        {
            if (__state == 0L) return;
            long end = Stopwatch.GetTimestamp();
            TailObservatory093T0.RecordTick(__state, end);

            // Preserve the stable AdaptiveBurst behavior exactly. T0 measurement is independent.
            if (FeatureGate.IsEnabled("runtime.adaptiveBurst") && !RuntimeCompatibility.ButterPlusPlusActive)
                AdaptiveLoadBalancer.RecordTick(__state);
        }
'@ 'independent T0 DoSingleTick sampler'
Set-Content $patchPath $patch -Encoding UTF8

# Reuse existing ReachProfile stopwatch measurements. No additional per-query timestamp pair is added.
$reachPath = 'RimMT/Source/RimMT/AI/AggressiveReachabilityProfilesV17.cs'
$reach = Get-Content $reachPath -Raw
$reach = Replace-OrThrow $reach @'
            finally
            {
                RecordElapsed(ref queryTicks, ref queryTicksMax, started);
            }
'@ @'
            finally
            {
                TailObservatory093T0.NoteReachQueryTicks(RecordElapsed(ref queryTicks, ref queryTicksMax, started));
            }
'@ 'ReachProfile query tail signal'
$reach = Replace-OrThrow $reach @'
                Interlocked.Increment(ref profileCaptures);
                Interlocked.Add(ref profileCaptureTicks, elapsed);
                UpdateMax(ref profileCaptureTicksMax, elapsed);
'@ @'
                Interlocked.Increment(ref profileCaptures);
                Interlocked.Add(ref profileCaptureTicks, elapsed);
                UpdateMax(ref profileCaptureTicksMax, elapsed);
                TailObservatory093T0.NoteReachCaptureTicks(elapsed);
'@ 'ReachProfile capture tail signal'
$reach = Replace-OrThrow $reach @'
                Interlocked.Increment(ref topologySlices);
                Interlocked.Add(ref topologySliceTicks, elapsed);
                UpdateMax(ref topologySliceTicksMax, elapsed);
'@ @'
                Interlocked.Increment(ref topologySlices);
                Interlocked.Add(ref topologySliceTicks, elapsed);
                UpdateMax(ref topologySliceTicksMax, elapsed);
                TailObservatory093T0.NoteTopologySliceTicks(elapsed);
'@ 'ReachProfile topology slice tail signal'
$reach = Replace-OrThrow $reach @'
        private static void RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
        }
'@ @'
        private static long RecordElapsed(ref long total, ref long max, long started)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
            return elapsed;
        }
'@ 'return existing ReachProfile elapsed ticks'
Set-Content $reachPath $reach -Encoding UTF8

# S4 already identifies genuinely heavy validator calls. Piggyback one integer signal only there.
$s4Path = 'RimMT/Source/RimMT/AI/JobGiverSlowSearch0419S.cs'
$s4 = Get-Content $s4Path -Raw
$s4 = Replace-OrThrow $s4 @'
            if (validatorRejects < HeavyRejectThreshold || validator == null) return;
            heavyValidatorCalls++;
            heavyValidatorRejects += validatorRejects;
'@ @'
            if (validatorRejects < HeavyRejectThreshold || validator == null) return;
            TailObservatory093T0.NoteS4HeavyValidator(validatorRejects);
            heavyValidatorCalls++;
            heavyValidatorRejects += validatorRejects;
'@ 'S4 heavy-tail correlation signal'
Set-Content $s4Path $s4 -Encoding UTF8

# Report the observatory only on-demand/startup report generation; no periodic logging is introduced.
$diagPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$diag = Get-Content $diagPath -Raw
$diag = Replace-OrThrow $diag '[RimMT] V0.9.3 Consolidated Stable on-demand report' '[RimMT] V0.9.3-T0 Tail Observatory on-demand report' 'T0 report title'
$diag = Replace-OrThrow $diag @'
            AppendLoad(sb);
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
'@ @'
            AppendLoad(sb);
            sb.AppendLine(TailObservatory093T0.Summary());
            sb.AppendLine(TailObservatory093T0.ComponentSummary());
            sb.AppendLine(TailObservatory093T0.RecentSummary());
            sb.AppendLine("Text cache: hits=" + TextMetricCache.Hits + ", misses=" + TextMetricCache.Misses);
'@ 'T0 report lines'
$diag = Replace-OrThrow $diag 'V0.9.3 Consolidated Stable; S4 tail=32ms' 'V0.9.3-T0 Tail Observatory; baseline=V0.9.3 Consolidated Stable; S4 tail=32ms' 'T0 policy marker'
Set-Content $diagPath $diag -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-consolidated-stable";' 'internal const string Version = "0.9.3-t0-tail-observatory";' 'T0 bootstrap version'
$boot = Replace-OrThrow $boot '[RimMT] V0.9.3 Consolidated Stable initialized.' '[RimMT] V0.9.3-T0 Tail Observatory initialized.' 'T0 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('<name>RimMT V0.9.3 Consolidated Stable</name>', '<name>RimMT V0.9.3-T0 Tail Observatory</name>')
    $about = $about.Replace('V0.9.3 Consolidated Stable', 'V0.9.3-T0 Tail Observatory')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T0 Tail Observatory: measurement only; stable optimizer behavior unchanged.'