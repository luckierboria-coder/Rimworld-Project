using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTRC2T2
{
    /// <summary>
    /// Extremely low-overhead structural timing for slow JobGiver packages.
    ///
    /// This deliberately does NOT patch individual WorkGiver methods, Reachability,
    /// validators, or candidate enumerators. It reuses only the two structural seams
    /// that RC2-T2 already cares about: JobGiver_Work.TryIssueJobPackage and the
    /// GenClosest global/reachable entry points.
    ///
    /// Per-call work is ThreadStatic timestamp arithmetic only. Global atomics are
    /// touched once per completed outer JobPackage, and only slow-package aggregates
    /// are retained. No Verse object is dereferenced off-thread and no gameplay result
    /// is ever changed.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class PreTailStructureProfiler
    {
        private const string HarmonyId = "allen.rimmt";
        private const double SampleThresholdMs = 16.0;
        private const double TailThresholdMs = 32.0;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);
        private static readonly double TimestampToMs = 1000.0 / Stopwatch.Frequency;

        [ThreadStatic] private static int jobDepth;
        [ThreadStatic] private static long jobStarted;
        [ThreadStatic] private static long firstSearchStarted;
        [ThreadStatic] private static long lastSearchEnded;
        [ThreadStatic] private static long outerSearchStarted;
        [ThreadStatic] private static long searchTicks;
        [ThreadStatic] private static long gapTicks;
        [ThreadStatic] private static int searchDepth;
        [ThreadStatic] private static int searchCalls;

        private static bool installed;
        private static int patchedSearchMethods;
        private static long installFailures;

        private static long packages;
        private static long sampled16;
        private static long tail32;
        private static long noSearchTail;
        private static long bucket16_31;
        private static long bucket32_63;
        private static long bucket64_127;
        private static long bucket128Plus;

        private static long dominantPreFirst;
        private static long dominantSearch;
        private static long dominantGap;
        private static long dominantPostLast;
        private static long dominantNoSearch;

        private static readonly object AggregateLock = new object();
        private static double totalTailMs;
        private static double preFirstTailMs;
        private static double searchTailMs;
        private static double gapTailMs;
        private static double postLastTailMs;
        private static long tailSearchCalls;
        private static double maxTailMs;
        private static double maxPreFirstMs;
        private static double maxSearchMs;
        private static double maxGapMs;
        private static double maxPostLastMs;

        static PreTailStructureProfiler()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;

            try
            {
                MethodInfo job = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (job != null)
                {
                    Harmony.Patch(job,
                        prefix: new HarmonyMethod(typeof(PreTailStructureProfiler), nameof(JobPrefix)) { priority = Priority.First },
                        finalizer: new HarmonyMethod(typeof(PreTailStructureProfiler), nameof(JobFinalizer)) { priority = Priority.Last });
                }

                foreach (MethodInfo method in typeof(GenClosest).GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method == null || method.ReturnType != typeof(Thing)) continue;
                    if (method.Name != "ClosestThing_Global_Reachable" && method.Name != "ClosestThingReachable") continue;

                    Harmony.Patch(method,
                        prefix: new HarmonyMethod(typeof(PreTailStructureProfiler), nameof(SearchPrefix)) { priority = Priority.First },
                        postfix: new HarmonyMethod(typeof(PreTailStructureProfiler), nameof(SearchPostfix)) { priority = Priority.Last });
                    patchedSearchMethods++;
                }

                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    Harmony.Patch(report, postfix: new HarmonyMethod(typeof(PreTailStructureProfiler), nameof(ReportPostfix)) { priority = Priority.Last });

                Log.Message("[RimMT] RC2-T2 PreTail Structure V0.1 installed. It records only JobPackage/GenClosest structural timestamps; no individual WorkGiver, validator or Reachability timing patches are added.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref installFailures);
                Log.Warning("[RimMT] RC2-T2 PreTail Structure telemetry failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static bool IsJobScopeActive
        {
            get { return jobDepth > 0 && jobStarted != 0L; }
        }

        internal static bool IsTailActive
        {
            get
            {
                long start = jobStarted;
                return jobDepth > 0 && start != 0L && (Stopwatch.GetTimestamp() - start) * TimestampToMs >= TailThresholdMs;
            }
        }

        internal static double CurrentJobElapsedMs
        {
            get
            {
                long start = jobStarted;
                if (jobDepth <= 0 || start == 0L) return 0.0;
                return (Stopwatch.GetTimestamp() - start) * TimestampToMs;
            }
        }

        public static void JobPrefix()
        {
            if (jobDepth == 0)
            {
                jobStarted = Stopwatch.GetTimestamp();
                firstSearchStarted = 0L;
                lastSearchEnded = 0L;
                outerSearchStarted = 0L;
                searchTicks = 0L;
                gapTicks = 0L;
                searchDepth = 0;
                searchCalls = 0;
            }
            jobDepth++;
        }

        public static Exception JobFinalizer(Exception __exception)
        {
            if (jobDepth > 0) jobDepth--;
            if (jobDepth != 0 || jobStarted == 0L)
                return __exception;

            long ended = Stopwatch.GetTimestamp();
            long totalTicks = ended - jobStarted;
            double totalMs = totalTicks * TimestampToMs;
            Interlocked.Increment(ref packages);

            if (totalMs >= SampleThresholdMs)
            {
                Interlocked.Increment(ref sampled16);
                if (totalMs < 32.0) Interlocked.Increment(ref bucket16_31);
                else if (totalMs < 64.0) Interlocked.Increment(ref bucket32_63);
                else if (totalMs < 128.0) Interlocked.Increment(ref bucket64_127);
                else Interlocked.Increment(ref bucket128Plus);
            }

            if (totalMs >= TailThresholdMs)
                CommitTail(ended, totalMs);

            jobStarted = 0L;
            firstSearchStarted = 0L;
            lastSearchEnded = 0L;
            outerSearchStarted = 0L;
            searchTicks = 0L;
            gapTicks = 0L;
            searchDepth = 0;
            searchCalls = 0;
            return __exception;
        }

        public static void SearchPrefix()
        {
            if (jobDepth <= 0 || jobStarted == 0L)
                return;

            if (searchDepth == 0)
            {
                long now = Stopwatch.GetTimestamp();
                if (firstSearchStarted == 0L)
                    firstSearchStarted = now;
                else if (lastSearchEnded != 0L && now > lastSearchEnded)
                    gapTicks += now - lastSearchEnded;

                outerSearchStarted = now;
                searchCalls++;
            }
            searchDepth++;
        }

        public static void SearchPostfix()
        {
            if (jobDepth <= 0 || jobStarted == 0L || searchDepth <= 0)
                return;

            searchDepth--;
            if (searchDepth != 0 || outerSearchStarted == 0L)
                return;

            long now = Stopwatch.GetTimestamp();
            if (now > outerSearchStarted)
                searchTicks += now - outerSearchStarted;
            lastSearchEnded = now;
            outerSearchStarted = 0L;
        }

        private static void CommitTail(long ended, double totalMs)
        {
            Interlocked.Increment(ref tail32);

            if (firstSearchStarted == 0L)
            {
                Interlocked.Increment(ref noSearchTail);
                Interlocked.Increment(ref dominantNoSearch);
                lock (AggregateLock)
                {
                    totalTailMs += totalMs;
                    if (totalMs > maxTailMs) maxTailMs = totalMs;
                }
                return;
            }

            long preTicks = firstSearchStarted > jobStarted ? firstSearchStarted - jobStarted : 0L;
            long postTicks = lastSearchEnded != 0L && ended > lastSearchEnded ? ended - lastSearchEnded : 0L;
            double preMs = preTicks * TimestampToMs;
            double searchesMs = searchTicks * TimestampToMs;
            double gapsMs = gapTicks * TimestampToMs;
            double postMs = postTicks * TimestampToMs;

            double max = preMs;
            int dominant = 0;
            if (searchesMs > max) { max = searchesMs; dominant = 1; }
            if (gapsMs > max) { max = gapsMs; dominant = 2; }
            if (postMs > max) { dominant = 3; }

            if (dominant == 0) Interlocked.Increment(ref dominantPreFirst);
            else if (dominant == 1) Interlocked.Increment(ref dominantSearch);
            else if (dominant == 2) Interlocked.Increment(ref dominantGap);
            else Interlocked.Increment(ref dominantPostLast);

            lock (AggregateLock)
            {
                totalTailMs += totalMs;
                preFirstTailMs += preMs;
                searchTailMs += searchesMs;
                gapTailMs += gapsMs;
                postLastTailMs += postMs;
                tailSearchCalls += searchCalls;
                if (totalMs > maxTailMs) maxTailMs = totalMs;
                if (preMs > maxPreFirstMs) maxPreFirstMs = preMs;
                if (searchesMs > maxSearchMs) maxSearchMs = searchesMs;
                if (gapsMs > maxGapMs) maxGapMs = gapsMs;
                if (postMs > maxPostLastMs) maxPostLastMs = postMs;
            }
        }

        public static void ReportPostfix()
        {
            long tails = Interlocked.Read(ref tail32);
            long tailsWithSearch = tails - Interlocked.Read(ref noSearchTail);
            double avgTotal;
            double avgPre;
            double avgSearch;
            double avgGap;
            double avgPost;
            double avgSearchCalls;
            double localMaxTotal;
            double localMaxPre;
            double localMaxSearch;
            double localMaxGap;
            double localMaxPost;

            lock (AggregateLock)
            {
                avgTotal = tails == 0 ? 0.0 : totalTailMs / tails;
                avgPre = tailsWithSearch <= 0 ? 0.0 : preFirstTailMs / tailsWithSearch;
                avgSearch = tailsWithSearch <= 0 ? 0.0 : searchTailMs / tailsWithSearch;
                avgGap = tailsWithSearch <= 0 ? 0.0 : gapTailMs / tailsWithSearch;
                avgPost = tailsWithSearch <= 0 ? 0.0 : postLastTailMs / tailsWithSearch;
                avgSearchCalls = tailsWithSearch <= 0 ? 0.0 : (double)tailSearchCalls / tailsWithSearch;
                localMaxTotal = maxTailMs;
                localMaxPre = maxPreFirstMs;
                localMaxSearch = maxSearchMs;
                localMaxGap = maxGapMs;
                localMaxPost = maxPostLastMs;
            }

            Log.Message("[RimMT] RC2-T2 PreTail Structure V0.1: patchedSearch=" + patchedSearchMethods +
                ", packages=" + Interlocked.Read(ref packages) +
                ", sampled>=16/tail>=32=" + Interlocked.Read(ref sampled16) + "/" + tails +
                ", buckets16-31/32-63/64-127/128+=" + Interlocked.Read(ref bucket16_31) + "/" + Interlocked.Read(ref bucket32_63) + "/" + Interlocked.Read(ref bucket64_127) + "/" + Interlocked.Read(ref bucket128Plus) +
                ", noSearchTail=" + Interlocked.Read(ref noSearchTail) +
                ", avgTailMs=" + avgTotal.ToString("F2") +
                ", avgPhaseMs(preFirst/searchInclusive/interSearch/postLast)=" + avgPre.ToString("F2") + "/" + avgSearch.ToString("F2") + "/" + avgGap.ToString("F2") + "/" + avgPost.ToString("F2") +
                ", avgSearchCalls=" + avgSearchCalls.ToString("F1") +
                ", maxPhaseMs(total/pre/search/gap/post)=" + localMaxTotal.ToString("F2") + "/" + localMaxPre.ToString("F2") + "/" + localMaxSearch.ToString("F2") + "/" + localMaxGap.ToString("F2") + "/" + localMaxPost.ToString("F2") +
                ", dominant(pre/search/gap/post/noSearch)=" + Interlocked.Read(ref dominantPreFirst) + "/" + Interlocked.Read(ref dominantSearch) + "/" + Interlocked.Read(ref dominantGap) + "/" + Interlocked.Read(ref dominantPostLast) + "/" + Interlocked.Read(ref dominantNoSearch) +
                ", failures=" + Interlocked.Read(ref installFailures) + ".");
        }
    }
}
