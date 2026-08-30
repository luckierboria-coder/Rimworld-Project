using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace RimMT
{
    // S4-CD1: diagnostic-only A/B sampling for custom IEnumerable<Thing> searches after S4 tail rescue activates.
    //
    // Every 32nd eligible tail custom search is forced through live Vanilla ClosestThingReachable; an offset
    // every-32nd sample is allowed to run through S4 normally. Both paths are timed from the same high-priority
    // prefix, and RegionTraverser / Reachability.CanReach calls are counted only while a sampled call is active.
    // The other 30/32 calls keep ordinary S4 behavior. This branch is for attribution, not clean benchmarking.
    [StaticConstructorOnStartup]
    internal static class JobGiverCustomABCD1
    {
        private const string HarmonyId = "allen.rimmt.s4cd1";
        private const int TailThresholdMs = 32;
        private const int SampleModulo = 32;
        private const int VanillaSlot = 0;
        private const int FastSlot = 16;
        private const int MaxSamplesPerArm = 1024;
        private static readonly long TailThresholdTicks = Math.Max(1L, Stopwatch.Frequency * TailThresholdMs / 1000L);

        private const int ModeVanilla = 1;
        private const int ModeFast = 2;

        [ThreadStatic]
        private static ProbeState activeProbe;

        private static readonly BucketStats[] vanillaBuckets = NewBuckets();
        private static readonly BucketStats[] fastBuckets = NewBuckets();

        private static volatile bool patched;
        private static volatile bool samplingActive = true;
        private static long tailCustomSeen;
        private static long vanillaSamples;
        private static long fastSamples;
        private static long unknownCountSamples;
        private static long probeExceptions;
        private static long reachHooks;
        private static long regionHooks;
        private static long patchFailures;
        private static int completionAnnounced;

        static JobGiverCustomABCD1()
        {
            try
            {
                Apply(new Harmony(HarmonyId));
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                Log.Warning("[RimMT] S4-CD1 CustomSet A/B static initialization failed. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static BucketStats[] NewBuckets()
        {
            return new[] { new BucketStats(), new BucketStats(), new BucketStats(), new BucketStats() };
        }

        private static void Apply(Harmony harmony)
        {
            int closestPatched = 0;
            int reachPatched = 0;
            int regionPatched = 0;

            try
            {
                MethodInfo[] closest = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < closest.Length; i++)
                {
                    MethodInfo method = closest[i];
                    if (!IsSupportedClosest(method))
                        continue;

                    HarmonyMethod sample = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(SamplePrefix));
                    sample.priority = Priority.First + 200;
                    HarmonyMethod restore = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(RestorePrefix));
                    restore.priority = Priority.Last;
                    HarmonyMethod postfix = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(SamplePostfix));
                    postfix.priority = Priority.Last;
                    HarmonyMethod finalizer = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(SampleFinalizer));
                    finalizer.priority = Priority.Last;
                    harmony.Patch(method, prefix: sample, postfix: postfix, finalizer: finalizer);
                    // A second prefix is needed after S4: Vanilla samples temporarily poison maxDistance so the
                    // S4 prefix returns true, then this final prefix restores the original value before Vanilla runs.
                    harmony.Patch(method, prefix: restore);
                    closestPatched++;
                }

                MethodInfo[] reachMethods = typeof(Reachability).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                for (int i = 0; i < reachMethods.Length; i++)
                {
                    MethodInfo method = reachMethods[i];
                    if (!string.Equals(method.Name, "CanReach", StringComparison.Ordinal))
                        continue;
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(ReachPrefix));
                    prefix.priority = Priority.First;
                    harmony.Patch(method, prefix: prefix);
                    reachPatched++;
                }

                MethodInfo[] regionMethods = typeof(RegionTraverser).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < regionMethods.Length; i++)
                {
                    MethodInfo method = regionMethods[i];
                    if (!string.Equals(method.Name, "BreadthFirstTraverse", StringComparison.Ordinal))
                        continue;
                    HarmonyMethod prefix = new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(RegionPrefix));
                    prefix.priority = Priority.First;
                    harmony.Patch(method, prefix: prefix);
                    regionPatched++;
                }

                MethodBase runtimeReport = AccessTools.Method(typeof(RimMTDiagnostics), nameof(RimMTDiagnostics.LogRuntimeReport));
                if (runtimeReport != null)
                    harmony.Patch(runtimeReport, postfix: new HarmonyMethod(typeof(JobGiverCustomABCD1), nameof(ReportPostfix)));

                patched = closestPatched > 0;
                Log.Message("[RimMT] V0.4.19-JS1.1S4-CD1 CustomSet A/B Trace installed: ClosestThingReachable=" + closestPatched +
                    ", Reachability.CanReach=" + reachPatched + ", RegionTraverser.BreadthFirstTraverse=" + regionPatched +
                    ". Diagnostic only: every 32 eligible tail custom searches sample Vanilla at slot 0 and S4 Fast at slot 16; other calls retain S4 behavior.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref patchFailures);
                patched = false;
                Log.Warning("[RimMT] S4-CD1 CustomSet A/B patch failed. S4 remains usable; A/B data may be incomplete. " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsSupportedClosest(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) ||
                !string.Equals(method.Name, "ClosestThingReachable", StringComparison.Ordinal))
                return false;

            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 &&
                p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(Map) &&
                p[5].ParameterType == typeof(float) &&
                typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static void SamplePrefix(ref float __5, ref IEnumerable<Thing> __7, ref ProbeState __state)
        {
            __state = null;
            if (!samplingActive || __7 == null || !RimMTThreadGuard.IsMainThread ||
                Current.ProgramState != ProgramState.Playing || !JobGiverGlobalNearest04181.InJobGiverScope)
                return;

            // Do not start a nested sample while an outer sampled ClosestThingReachable is still executing.
            if (activeProbe != null)
                return;

            long scopeStart = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
            if (scopeStart <= 0L || Stopwatch.GetTimestamp() - scopeStart < TailThresholdTicks)
                return;

            long seen = Interlocked.Increment(ref tailCustomSeen);
            int slot = (int)(seen & (SampleModulo - 1));
            int mode = 0;
            if (slot == VanillaSlot && Interlocked.Read(ref vanillaSamples) < MaxSamplesPerArm)
                mode = ModeVanilla;
            else if (slot == FastSlot && Interlocked.Read(ref fastSamples) < MaxSamplesPerArm)
                mode = ModeFast;
            if (mode == 0)
                return;

            ProbeState state = new ProbeState();
            state.Mode = mode;
            state.Started = Stopwatch.GetTimestamp();
            state.OriginalMaxDistance = __5;
            state.KnownCount = TryKnownCount(__7);
            state.Wrapper = new CountingEnumerable(__7, state);
            __7 = state.Wrapper;
            activeProbe = state;
            __state = state;

            if (mode == ModeVanilla)
            {
                // S4 validates maxDistance before its custom-set branch. Poisoning it makes S4 fail closed to
                // Vanilla for this sampled call; RestorePrefix restores the exact original argument afterward.
                state.MaxDistancePoisoned = true;
                __5 = -1f;
            }
        }

        public static void RestorePrefix(ref float __5)
        {
            ProbeState state = activeProbe;
            if (state == null || state.Mode != ModeVanilla || !state.MaxDistancePoisoned)
                return;

            __5 = state.OriginalMaxDistance;
            state.MaxDistancePoisoned = false;
        }

        public static void SamplePostfix(Thing __result, ProbeState __state)
        {
            if (__state == null || __state.Completed)
                return;

            CompleteSample(__state, __result == null);
        }

        public static Exception SampleFinalizer(Exception __exception, ProbeState __state)
        {
            if (__state != null && !__state.Completed)
            {
                __state.Completed = true;
                Interlocked.Increment(ref probeExceptions);
                if (ReferenceEquals(activeProbe, __state))
                    activeProbe = null;
            }
            return __exception;
        }

        public static void ReachPrefix()
        {
            ProbeState state = activeProbe;
            if (state != null)
            {
                state.ReachCalls++;
                Interlocked.Increment(ref reachHooks);
            }
        }

        public static void RegionPrefix()
        {
            ProbeState state = activeProbe;
            if (state != null)
            {
                state.RegionCalls++;
                Interlocked.Increment(ref regionHooks);
            }
        }

        private static int TryKnownCount(IEnumerable<Thing> source)
        {
            ICollection<Thing> generic = source as ICollection<Thing>;
            if (generic != null)
                return generic.Count;

            ICollection nongeneric = source as ICollection;
            if (nongeneric != null)
                return nongeneric.Count;

            return -1;
        }

        private static void CompleteSample(ProbeState state, bool noResult)
        {
            state.Completed = true;
            long elapsed = Stopwatch.GetTimestamp() - state.Started;
            int count = state.KnownCount >= 0 ? state.KnownCount : state.MaxObservedElements;
            if (state.KnownCount < 0)
                Interlocked.Increment(ref unknownCountSamples);

            int bucket = BucketFor(count);
            BucketStats stats = state.Mode == ModeVanilla ? vanillaBuckets[bucket] : fastBuckets[bucket];
            stats.Record(elapsed, noResult, state.RegionCalls, state.ReachCalls, count);

            if (state.Mode == ModeVanilla)
                Interlocked.Increment(ref vanillaSamples);
            else
                Interlocked.Increment(ref fastSamples);

            if (ReferenceEquals(activeProbe, state))
                activeProbe = null;

            TryComplete();
        }

        private static int BucketFor(int count)
        {
            if (count <= 31) return 0;
            if (count <= 63) return 1;
            if (count <= 127) return 2;
            return 3;
        }

        private static void TryComplete()
        {
            if (Interlocked.Read(ref vanillaSamples) < MaxSamplesPerArm || Interlocked.Read(ref fastSamples) < MaxSamplesPerArm)
                return;

            samplingActive = false;
            if (Interlocked.Exchange(ref completionAnnounced, 1) == 0)
                Log.Message("[RimMT] S4-CD1 CUSTOM A/B COMPLETE; further sampling disabled. " + Summary());
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] S4-CD1 supplemental report: " + Summary());
        }

        internal static string Summary()
        {
            return "JobGiver S4-CD1 CustomSet A/B: patched=" + patched +
                ", samplingActive=" + samplingActive +
                ", sampleEvery=" + SampleModulo +
                ", tailCustomSeen=" + Interlocked.Read(ref tailCustomSeen) +
                ", samples(V/F)=" + Interlocked.Read(ref vanillaSamples) + "/" + Interlocked.Read(ref fastSamples) +
                ", unknownCountSamples=" + Interlocked.Read(ref unknownCountSamples) +
                ", infraHooks(region/reach)=" + Interlocked.Read(ref regionHooks) + "/" + Interlocked.Read(ref reachHooks) +
                ", probeExceptions=" + Interlocked.Read(ref probeExceptions) +
                ", patchFailures=" + Interlocked.Read(ref patchFailures) +
                "; buckets: 16-31 " + FormatBucket(0) +
                " | 32-63 " + FormatBucket(1) +
                " | 64-127 " + FormatBucket(2) +
                " | 128+ " + FormatBucket(3) +
                ". V=sampled live Vanilla ClosestThingReachable; F=sampled S4 custom fast path. Overall JobGiver timing on CD1 is diagnostic-contaminated and must not be used as a clean S4 benchmark.";
        }

        private static string FormatBucket(int index)
        {
            return "V[" + vanillaBuckets[index].Format() + "] F[" + fastBuckets[index].Format() + "]";
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

        internal sealed class ProbeState
        {
            internal int Mode;
            internal long Started;
            internal float OriginalMaxDistance;
            internal bool MaxDistancePoisoned;
            internal bool Completed;
            internal int KnownCount;
            internal int MaxObservedElements;
            internal long RegionCalls;
            internal long ReachCalls;
            internal CountingEnumerable Wrapper;
        }

        internal sealed class CountingEnumerable : IEnumerable<Thing>
        {
            private readonly IEnumerable<Thing> inner;
            private readonly ProbeState state;

            internal CountingEnumerable(IEnumerable<Thing> inner, ProbeState state)
            {
                this.inner = inner;
                this.state = state;
            }

            public IEnumerator<Thing> GetEnumerator()
            {
                return new CountingEnumerator(inner.GetEnumerator(), state);
            }

            IEnumerator IEnumerable.GetEnumerator()
            {
                return GetEnumerator();
            }
        }

        private sealed class CountingEnumerator : IEnumerator<Thing>
        {
            private readonly IEnumerator<Thing> inner;
            private readonly ProbeState state;
            private int localCount;

            internal CountingEnumerator(IEnumerator<Thing> inner, ProbeState state)
            {
                this.inner = inner;
                this.state = state;
            }

            public Thing Current { get { return inner.Current; } }
            object IEnumerator.Current { get { return Current; } }

            public bool MoveNext()
            {
                bool moved = inner.MoveNext();
                if (moved)
                {
                    localCount++;
                    if (localCount > state.MaxObservedElements)
                        state.MaxObservedElements = localCount;
                }
                return moved;
            }

            public void Reset()
            {
                inner.Reset();
                localCount = 0;
            }

            public void Dispose()
            {
                inner.Dispose();
            }
        }

        private sealed class BucketStats
        {
            private long calls;
            private long ticks;
            private long maxTicks;
            private long noResult;
            private long regionCalls;
            private long reachCalls;
            private long elements;

            internal void Record(long elapsed, bool wasNoResult, long regions, long reaches, int count)
            {
                Interlocked.Increment(ref calls);
                Interlocked.Add(ref ticks, elapsed);
                UpdateMax(ref maxTicks, elapsed);
                if (wasNoResult)
                    Interlocked.Increment(ref noResult);
                Interlocked.Add(ref regionCalls, regions);
                Interlocked.Add(ref reachCalls, reaches);
                Interlocked.Add(ref elements, Math.Max(0, count));
            }

            internal string Format()
            {
                long n = Interlocked.Read(ref calls);
                if (n == 0)
                    return "n=0";

                double avgMs = (Interlocked.Read(ref ticks) * 1000.0 / Stopwatch.Frequency) / n;
                double maxMs = Interlocked.Read(ref maxTicks) * 1000.0 / Stopwatch.Frequency;
                double noResultPct = Interlocked.Read(ref noResult) * 100.0 / n;
                double avgRegion = Interlocked.Read(ref regionCalls) / (double)n;
                double avgReach = Interlocked.Read(ref reachCalls) / (double)n;
                double avgElements = Interlocked.Read(ref elements) / (double)n;
                return "n=" + n +
                    ",avgMs=" + avgMs.ToString("F3") +
                    ",maxMs=" + maxMs.ToString("F3") +
                    ",noResult=" + noResultPct.ToString("F1") + "%" +
                    ",avgRegion=" + avgRegion.ToString("F2") +
                    ",avgReach=" + avgReach.ToString("F2") +
                    ",avgSource=" + avgElements.ToString("F1");
            }
        }
    }
}
