using System;
using System.Collections;
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
    // V0.4.19-JS1.1S3 Learned Admission
    //
    // S2 proved that residual JobGiver tails contain many repeated small/medium searches, but also
    // proved that blindly replacing those calls regresses the average. S3 therefore returns to the
    // validated S1 >=256 ThingRequest fast path and learns admission for 16-255 ThingRequest/custom
    // searches from sampled live Vanilla timings. Learned shapes periodically return to Vanilla for
    // re-sampling and automatically cool down if the accelerated EMA is not materially faster.
    //
    // No worker wait is introduced. Candidate membership is unchanged for supported explicit sets;
    // the original validator and live CanReach remain main-thread authoritative.
    internal static class JobGiverLearnedAdmission0419S3
    {
        internal const string FeatureId = "ai.jobLearnedSearch";

        private const int StaticLargeThreshold = 256;
        private const int LearnMinSource = 16;
        private const int MaxSourceCount = 16384;
        private const int MaxLearnedShapes = 512;
        private const int WarmupVanillaSamples = 8;
        private const int VanillaSampleMask = 31;      // 1/32 while learning
        private const int AdmittedShadowMask = 63;     // 1/64 while admitted
        private const int MinFastSamplesForJudgement = 32;
        private const int CooldownFrames = 3600;
        private const double AdmissionEmaMs = 1.50;
        private const double SlowVanillaMs = 2.00;
        private const double StrongSlowVanillaMs = 4.00;
        private const double RequiredFastRatio = 0.90; // fast EMA must be < 90% of Vanilla EMA

        [ThreadStatic] private static Candidate[] candidateScratch;
        [ThreadStatic] private static Candidate[] globalScratch;

        private static readonly Dictionary<ShapeKey, ShapeStats> Shapes =
            new Dictionary<ShapeKey, ShapeStats>(128);

        private static volatile bool enabled = true;
        private static volatile bool patched;
        private static volatile bool global32Patched;
        private static volatile bool globalReachable32Patched;

        private static long observed;
        private static long inScope;
        private static long staticLargeAccelerated;
        private static long learnedAccelerated;
        private static long learnedThingRequestAccelerated;
        private static long learnedCustomAccelerated;
        private static long acceleratedFound;
        private static long acceleratedNoResult;
        private static long vanillaSamples;
        private static long learnedAdmissions;
        private static long learnedDeAdmissions;
        private static long admittedShadowSamples;
        private static long shapeCapBypass;
        private static long customUnsupportedBypass;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long unsupportedTraverseBypass;
        private static long invalidContextBypass;
        private static long sourceFaultBypass;
        private static long source0To15;
        private static long source16To31;
        private static long source32To63;
        private static long source64To127;
        private static long source128To255;
        private static long source256To383;
        private static long source384To511;
        private static long source512Plus;
        private static long sourceCandidates;
        private static long keptCandidates;
        private static long examinedCandidates;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long acceleratedTicks;
        private static long maxAcceleratedTicks;
        private static long sortTicks;
        private static long maxSortTicks;
        private static long maxSource;
        private static long maxExamined;
        private static long failures;

        private static long global32Observed;
        private static long global32Reordered;
        private static long global32Candidates;
        private static long global32SortTicks;
        private static long global32MaxSortTicks;
        private static long global32Failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null)
                return;

            try
            {
                MethodInfo prefixMethod = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Prefix));
                MethodInfo postfixMethod = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Postfix));
                MethodInfo globalPrefix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Global32Prefix));
                MethodInfo reachablePrefix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(GlobalReachable32Prefix));

                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int ctrPatched = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (IsSupportedCtrOverload(method))
                    {
                        HarmonyMethod prefix = new HarmonyMethod(prefixMethod) { priority = Priority.First + 100 };
                        HarmonyMethod postfix = new HarmonyMethod(postfixMethod) { priority = Priority.Last - 100 };
                        harmony.Patch(method, prefix: prefix, postfix: postfix);
                        ctrPatched++;
                        continue;
                    }

                    ParameterInfo[] p = method == null ? null : method.GetParameters();
                    if (method != null && p != null && string.Equals(method.Name, "ClosestThing_Global", StringComparison.Ordinal) &&
                        p.Length == 5 && p[0].ParameterType == typeof(IntVec3))
                    {
                        HarmonyMethod prefix = new HarmonyMethod(globalPrefix) { priority = Priority.First + 80 };
                        harmony.Patch(method, prefix: prefix);
                        global32Patched = true;
                    }
                    else if (method != null && p != null && string.Equals(method.Name, "ClosestThing_Global_Reachable", StringComparison.Ordinal) &&
                        p.Length == 8 && p[0].ParameterType == typeof(IntVec3))
                    {
                        HarmonyMethod prefix = new HarmonyMethod(reachablePrefix) { priority = Priority.First + 80 };
                        harmony.Patch(method, prefix: prefix);
                        globalReachable32Patched = true;
                    }
                }

                patched = ctrPatched > 0;
                if (patched)
                {
                    Log.Message("[RimMT] V0.4.19-JS1.1S3 Learned Admission installed on " + ctrPatched +
                        " ClosestThingReachable overload(s). >=256 ThingRequest searches keep the validated S1 fast path; 16-255 ThingRequest/custom IList searches default to Vanilla and are admitted only after sampled live timings show sustained cost. Admitted shapes periodically shadow Vanilla and auto-cooldown if acceleration is not materially faster. Supplemental Global nearest-first covers 32-63 candidates; >=64 remains owned by V0.4.18.1.");
                }
                else
                {
                    Log.Warning("[RimMT] V0.4.19-JS1.1S3 found no compatible ClosestThingReachable overload; learned admission is inert.");
                }
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                patched = false;
                Log.Warning("[RimMT] V0.4.19-JS1.1S3 install failed; S1/Vanilla paths remain authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value)
        {
            enabled = value;
        }

        private static bool IsSupportedCtrOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) ||
                !string.Equals(method.Name, "ClosestThingReachable", StringComparison.Ordinal))
                return false;

            ParameterInfo[] p = method.GetParameters();
            if (p.Length < 8)
                return false;

            return p[0].ParameterType == typeof(IntVec3) &&
                p[1].ParameterType == typeof(Map) &&
                p[2].ParameterType == typeof(ThingRequest) &&
                p[3].ParameterType == typeof(PathEndMode) &&
                p[4].ParameterType == typeof(TraverseParms) &&
                p[5].ParameterType == typeof(float) &&
                p[6].ParameterType == typeof(Predicate<Thing>) &&
                typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool Prefix(
            IntVec3 __0,
            Map __1,
            ThingRequest __2,
            PathEndMode __3,
            TraverseParms __4,
            float __5,
            Predicate<Thing> __6,
            IEnumerable<Thing> __7,
            ref Thing __result,
            ref SampleState __state)
        {
            __state = default(SampleState);
            Interlocked.Increment(ref observed);

            if (!enabled || !FeatureGate.IsEnabled(FeatureId) || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return true;

            Interlocked.Increment(ref inScope);

            Map map = __1;
            Pawn pawn = __4.pawn;
            if (map == null || map.Disposed || pawn == null || !pawn.Spawned || pawn.Map != map ||
                !__0.IsValid || !__0.InBounds(map) || __5 <= 0f)
            {
                Interlocked.Increment(ref invalidContextBypass);
                return true;
            }

            TraverseMode mode = __4.mode;
            if (mode != TraverseMode.ByPawn && mode != TraverseMode.PassDoors && mode != TraverseMode.NoPassClosedDoors)
            {
                Interlocked.Increment(ref unsupportedTraverseBypass);
                return true;
            }

            bool custom = __7 != null;
            IList source;
            if (custom)
            {
                source = __7 as IList;
                if (source == null)
                {
                    Interlocked.Increment(ref customUnsupportedBypass);
                    return true;
                }
            }
            else
            {
                try
                {
                    source = map.listerThings.ThingsMatching(__2);
                }
                catch
                {
                    Interlocked.Increment(ref sourceFaultBypass);
                    return true;
                }
                if (source == null)
                {
                    Interlocked.Increment(ref sourceFaultBypass);
                    return true;
                }
            }

            int count = source.Count;
            RecordSourceBucket(count);
            if (count > MaxSourceCount)
            {
                Interlocked.Increment(ref tooLargeBypass);
                return true;
            }

            // Preserve the validated S1 static path exactly for normal ThingRequest-backed >=256 searches.
            if (!custom && count >= StaticLargeThreshold)
                return Accelerate(source, count, false, null, __0, map, __3, __4, __5, __6, ref __result);

            if (count < LearnMinSource)
            {
                Interlocked.Increment(ref smallBypass);
                return true;
            }

            ShapeStats stats = GetOrCreateShape(new ShapeKey(
                custom,
                BucketFor(count),
                __3,
                mode,
                __6 == null ? null : __6.Method,
                __6 == null || __6.Target == null ? null : __6.Target.GetType(),
                custom ? source.GetType() : null));

            if (stats == null)
            {
                Interlocked.Increment(ref shapeCapBypass);
                return true;
            }

            stats.Seen++;
            long frame = RimMTRuntime.MainThreadFrames;
            if (stats.CooldownUntilFrame > frame)
            {
                if (ShouldSampleLearning(stats))
                    BeginVanillaSample(stats, ref __state, false);
                return true;
            }

            if (stats.CooldownUntilFrame != 0 && stats.CooldownUntilFrame <= frame)
            {
                stats.CooldownUntilFrame = 0;
                ResetLearningWindow(stats);
            }

            if (!stats.Admitted)
            {
                if (ShouldSampleLearning(stats))
                    BeginVanillaSample(stats, ref __state, false);
                return true;
            }

            stats.AdmittedCalls++;
            if ((stats.AdmittedCalls & AdmittedShadowMask) == 0)
            {
                BeginVanillaSample(stats, ref __state, true);
                return true;
            }

            return Accelerate(source, count, true, stats, __0, map, __3, __4, __5, __6, ref __result);
        }

        public static void Postfix(SampleState __state)
        {
            if (!__state.Sample || __state.Stats == null || __state.Started == 0L)
                return;

            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            ShapeStats stats = __state.Stats;
            stats.VanillaSamples++;
            stats.VanillaEmaTicks = stats.VanillaSamples == 1 ? elapsed : Ema(stats.VanillaEmaTicks, elapsed, 0.20);
            if (elapsed > stats.VanillaMaxTicks)
                stats.VanillaMaxTicks = elapsed;
            if (TicksToMs(elapsed) >= SlowVanillaMs)
                stats.VanillaSlowSamples++;
            Interlocked.Increment(ref vanillaSamples);
            if (__state.AdmittedShadow)
                Interlocked.Increment(ref admittedShadowSamples);

            if (!stats.Admitted && stats.CooldownUntilFrame == 0 && IsAdmissionReady(stats))
            {
                stats.Admitted = true;
                stats.AdmittedCalls = 0;
                stats.FastSamples = 0;
                stats.FastEmaTicks = 0.0;
                stats.FastMaxTicks = 0L;
                stats.Admissions++;
                Interlocked.Increment(ref learnedAdmissions);
            }
        }

        private static bool Accelerate(
            IList source,
            int count,
            bool learned,
            ShapeStats stats,
            IntVec3 root,
            Map map,
            PathEndMode endMode,
            TraverseParms traverseParms,
            float maxDistance,
            Predicate<Thing> validator,
            ref Thing result)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] candidates = EnsureCandidateScratch(count);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                int kept = 0;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !thing.Spawned || thing.Map != map)
                        continue;

                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        continue;

                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long dist = dx * dx + dz * dz;
                    if (dist > maxDistanceSquared)
                        continue;

                    candidates[kept++] = new Candidate(thing, dist, i);
                }

                long sortStarted = Stopwatch.GetTimestamp();
                if (kept > 1)
                    Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long sortElapsed = Stopwatch.GetTimestamp() - sortStarted;
                Interlocked.Add(ref sortTicks, sortElapsed);
                UpdateMax(ref maxSortTicks, sortElapsed);

                int examined = 0;
                int localValidatorCalls = 0;
                int localValidatorRejected = 0;
                int localReachChecks = 0;
                int localReachRejected = 0;

                for (int i = 0; i < kept; i++)
                {
                    Thing thing = candidates[i].Thing;
                    examined++;

                    if (validator != null)
                    {
                        localValidatorCalls++;
                        if (!validator(thing))
                        {
                            localValidatorRejected++;
                            continue;
                        }
                    }

                    localReachChecks++;
                    if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, traverseParms))
                    {
                        localReachRejected++;
                        continue;
                    }

                    result = thing;
                    CommitAcceleration(count, kept, examined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, learned);
                    Interlocked.Increment(ref acceleratedFound);
                    RecordFastElapsed(started, learned ? stats : null);
                    return false;
                }

                result = null;
                CommitAcceleration(count, kept, examined, localValidatorCalls, localValidatorRejected, localReachChecks, localReachRejected, learned);
                Interlocked.Increment(ref acceleratedNoResult);
                RecordFastElapsed(started, learned ? stats : null);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (stats != null)
                    PutShapeOnCooldown(stats);
                if (Interlocked.Read(ref failures) <= 8)
                    Log.Warning("[RimMT] V0.4.19-JS1.1S3 fast search failed; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static void CommitAcceleration(int source, int kept, int examined, int validators, int validatorFalse, int reaches, int reachFalse, bool learned)
        {
            if (learned)
                Interlocked.Increment(ref learnedAccelerated);
            else
                Interlocked.Increment(ref staticLargeAccelerated);
            Interlocked.Add(ref sourceCandidates, source);
            Interlocked.Add(ref keptCandidates, kept);
            Interlocked.Add(ref examinedCandidates, examined);
            Interlocked.Add(ref validatorCalls, validators);
            Interlocked.Add(ref validatorRejected, validatorFalse);
            Interlocked.Add(ref reachChecks, reaches);
            Interlocked.Add(ref reachRejected, reachFalse);
            UpdateMax(ref maxSource, source);
            UpdateMax(ref maxExamined, examined);
        }

        private static void RecordFastElapsed(long started, ShapeStats stats)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref acceleratedTicks, elapsed);
            UpdateMax(ref maxAcceleratedTicks, elapsed);

            if (stats == null)
                return;

            stats.FastSamples++;
            stats.FastEmaTicks = stats.FastSamples == 1 ? elapsed : Ema(stats.FastEmaTicks, elapsed, 0.20);
            if (elapsed > stats.FastMaxTicks)
                stats.FastMaxTicks = elapsed;

            if (stats.FastSamples >= MinFastSamplesForJudgement && stats.VanillaSamples >= WarmupVanillaSamples &&
                stats.FastEmaTicks >= stats.VanillaEmaTicks * RequiredFastRatio)
            {
                stats.Admitted = false;
                stats.CooldownUntilFrame = RimMTRuntime.MainThreadFrames + CooldownFrames;
                stats.DeAdmissions++;
                Interlocked.Increment(ref learnedDeAdmissions);
            }
        }

        private static ShapeStats GetOrCreateShape(ShapeKey key)
        {
            ShapeStats stats;
            if (Shapes.TryGetValue(key, out stats))
                return stats;
            if (Shapes.Count >= MaxLearnedShapes)
                return null;
            stats = new ShapeStats(key.Custom);
            Shapes.Add(key, stats);
            return stats;
        }

        private static bool ShouldSampleLearning(ShapeStats stats)
        {
            return stats.VanillaSamples < WarmupVanillaSamples || (stats.Seen & VanillaSampleMask) == 0;
        }

        private static void BeginVanillaSample(ShapeStats stats, ref SampleState state, bool admittedShadow)
        {
            state.Sample = true;
            state.Stats = stats;
            state.Started = Stopwatch.GetTimestamp();
            state.AdmittedShadow = admittedShadow;
        }

        private static bool IsAdmissionReady(ShapeStats stats)
        {
            if (stats.VanillaSamples < WarmupVanillaSamples)
                return false;
            double emaMs = TicksToMs(stats.VanillaEmaTicks);
            double maxMs = TicksToMs(stats.VanillaMaxTicks);
            return emaMs >= AdmissionEmaMs && (maxMs >= StrongSlowVanillaMs || stats.VanillaSlowSamples >= 2);
        }

        private static void PutShapeOnCooldown(ShapeStats stats)
        {
            stats.Admitted = false;
            stats.CooldownUntilFrame = RimMTRuntime.MainThreadFrames + CooldownFrames;
            stats.DeAdmissions++;
            Interlocked.Increment(ref learnedDeAdmissions);
        }

        private static void ResetLearningWindow(ShapeStats stats)
        {
            stats.VanillaSamples = 0;
            stats.VanillaEmaTicks = 0.0;
            stats.VanillaMaxTicks = 0L;
            stats.VanillaSlowSamples = 0;
            stats.FastSamples = 0;
            stats.FastEmaTicks = 0.0;
            stats.FastMaxTicks = 0L;
            stats.AdmittedCalls = 0;
        }

        public static void Global32Prefix(object[] __args)
        {
            Interlocked.Increment(ref global32Observed);
            if (__args == null || __args.Length < 5)
                return;
            TryGlobal32(__args, 0, 1, 2, 4);
        }

        public static void GlobalReachable32Prefix(object[] __args)
        {
            Interlocked.Increment(ref global32Observed);
            if (__args == null || __args.Length < 8)
                return;
            TryGlobal32(__args, 0, 2, 5, 7);
        }

        private static void TryGlobal32(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex)
        {
            if (!enabled || !FeatureGate.IsEnabled(FeatureId) || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || args[priorityIndex] != null)
                return;

            IList source = args[setIndex] as IList;
            if (source == null)
                return;
            int count = source.Count;
            if (count < 32 || count >= 64)
                return; // >=64 remains owned by JobGiverGlobalNearest04181

            try
            {
                IntVec3 center = (IntVec3)args[centerIndex];
                float maxDistance = Convert.ToSingle(args[maxDistanceIndex]);
                double maxDistanceSquared = (double)maxDistance * maxDistance;
                Candidate[] candidates = EnsureGlobalScratch(count);
                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !thing.Spawned)
                        continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid)
                        return;
                    long dx = (long)pos.x - center.x;
                    long dz = (long)pos.z - center.z;
                    long dist = dx * dx + dz * dz;
                    if (dist > maxDistanceSquared)
                        continue;
                    candidates[kept++] = new Candidate(thing, dist, i);
                }

                long started = Stopwatch.GetTimestamp();
                if (kept > 1)
                    Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long elapsed = Stopwatch.GetTimestamp() - started;
                Thing[] ordered = new Thing[kept];
                for (int i = 0; i < kept; i++)
                    ordered[i] = candidates[i].Thing;
                args[setIndex] = ordered;
                Interlocked.Increment(ref global32Reordered);
                Interlocked.Add(ref global32Candidates, count);
                Interlocked.Add(ref global32SortTicks, elapsed);
                UpdateMax(ref global32MaxSortTicks, elapsed);
            }
            catch
            {
                Interlocked.Increment(ref global32Failures);
            }
        }

        private static Candidate[] EnsureCandidateScratch(int required)
        {
            Candidate[] current = candidateScratch;
            if (current != null && current.Length >= required)
                return current;
            int capacity = 256;
            while (capacity < required && capacity < MaxSourceCount)
                capacity <<= 1;
            if (capacity < required)
                capacity = required;
            current = new Candidate[capacity];
            candidateScratch = current;
            return current;
        }

        private static Candidate[] EnsureGlobalScratch(int required)
        {
            Candidate[] current = globalScratch;
            if (current != null && current.Length >= required)
                return current;
            int capacity = 64;
            while (capacity < required)
                capacity <<= 1;
            current = new Candidate[capacity];
            globalScratch = current;
            return current;
        }

        private static int BucketFor(int count)
        {
            if (count < 32) return 0;
            if (count < 64) return 1;
            if (count < 128) return 2;
            if (count < 256) return 3;
            return 4;
        }

        private static void RecordSourceBucket(int count)
        {
            if (count < 16) Interlocked.Increment(ref source0To15);
            else if (count < 32) Interlocked.Increment(ref source16To31);
            else if (count < 64) Interlocked.Increment(ref source32To63);
            else if (count < 128) Interlocked.Increment(ref source64To127);
            else if (count < 256) Interlocked.Increment(ref source128To255);
            else if (count < 384) Interlocked.Increment(ref source256To383);
            else if (count < 512) Interlocked.Increment(ref source384To511);
            else Interlocked.Increment(ref source512Plus);
        }

        private static double Ema(double prior, double sample, double alpha)
        {
            return prior + (sample - prior) * alpha;
        }

        private static double TicksToMs(double ticks)
        {
            return ticks * 1000.0 / Stopwatch.Frequency;
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
            int admitted = 0;
            int cooling = 0;
            long frame = RimMTRuntime.MainThreadFrames;
            foreach (KeyValuePair<ShapeKey, ShapeStats> pair in Shapes)
            {
                ShapeStats stats = pair.Value;
                if (stats.Admitted) admitted++;
                else if (stats.CooldownUntilFrame > frame) cooling++;
            }

            long staticCalls = Interlocked.Read(ref staticLargeAccelerated);
            long learnedCalls = Interlocked.Read(ref learnedAccelerated);
            long calls = staticCalls + learnedCalls;
            double avgMs = calls == 0 ? 0.0 : TicksToMs(Interlocked.Read(ref acceleratedTicks)) / calls;
            double maxMs = TicksToMs(Interlocked.Read(ref maxAcceleratedTicks));
            double avgSortUs = calls == 0 ? 0.0 :
                (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / calls;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;
            long gCalls = Interlocked.Read(ref global32Reordered);
            double gAvgUs = gCalls == 0 ? 0.0 :
                (Interlocked.Read(ref global32SortTicks) * 1000000.0 / Stopwatch.Frequency) / gCalls;
            double gMaxUs = Interlocked.Read(ref global32MaxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            long learnedThing = 0;
            long learnedCustom = 0;
            foreach (KeyValuePair<ShapeKey, ShapeStats> pair in Shapes)
            {
                if (pair.Value.FastSamples <= 0)
                    continue;
                if (pair.Key.Custom) learnedCustom += pair.Value.FastSamples;
                else learnedThing += pair.Value.FastSamples;
            }
            Interlocked.Exchange(ref learnedThingRequestAccelerated, learnedThing);
            Interlocked.Exchange(ref learnedCustomAccelerated, learnedCustom);

            return "JobGiver learned-search JS1.1S3: patched=" + patched +
                ", enabled=" + enabled +
                ", staticThreshold=" + StaticLargeThreshold +
                ", learnMin=" + LearnMinSource +
                ", admission[vanillaSamples/emaMs/slowMs/strongMs]=" + WarmupVanillaSamples + "/" + AdmissionEmaMs.ToString("F2") + "/" + SlowVanillaMs.ToString("F2") + "/" + StrongSlowVanillaMs.ToString("F2") +
                ", shadowEvery=64, judgeFastSamples=" + MinFastSamplesForJudgement +
                ", requiredFastRatio=" + RequiredFastRatio.ToString("F2") +
                ", cooldownFrames=" + CooldownFrames +
                ", observed=" + Interlocked.Read(ref observed) +
                ", inScope=" + Interlocked.Read(ref inScope) +
                ", shapes=" + Shapes.Count +
                ", admitted/cooling=" + admitted + "/" + cooling +
                ", admissions/deAdmissions=" + Interlocked.Read(ref learnedAdmissions) + "/" + Interlocked.Read(ref learnedDeAdmissions) +
                ", vanillaSamples=" + Interlocked.Read(ref vanillaSamples) +
                ", admittedShadowSamples=" + Interlocked.Read(ref admittedShadowSamples) +
                ", accelerated=" + calls +
                ", static/learned=" + staticCalls + "/" + learnedCalls +
                ", learnedThing/custom~=" + learnedThing + "/" + learnedCustom +
                ", found/noResult=" + Interlocked.Read(ref acceleratedFound) + "/" + Interlocked.Read(ref acceleratedNoResult) +
                ", sourceBuckets0-15/16-31/32-63/64-127/128-255/256-383/384-511/512+=" +
                    Interlocked.Read(ref source0To15) + "/" + Interlocked.Read(ref source16To31) + "/" + Interlocked.Read(ref source32To63) + "/" +
                    Interlocked.Read(ref source64To127) + "/" + Interlocked.Read(ref source128To255) + "/" + Interlocked.Read(ref source256To383) + "/" +
                    Interlocked.Read(ref source384To511) + "/" + Interlocked.Read(ref source512Plus) +
                ", shapeCapBypass=" + Interlocked.Read(ref shapeCapBypass) +
                ", customUnsupportedBypass=" + Interlocked.Read(ref customUnsupportedBypass) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
                ", unsupportedTraverseBypass=" + Interlocked.Read(ref unsupportedTraverseBypass) +
                ", invalidContextBypass=" + Interlocked.Read(ref invalidContextBypass) +
                ", sourceFaultBypass=" + Interlocked.Read(ref sourceFaultBypass) +
                ", avgSource=" + (calls == 0 ? 0.0 : Interlocked.Read(ref sourceCandidates) / (double)calls).ToString("F1") +
                ", avgExamined=" + (calls == 0 ? 0.0 : Interlocked.Read(ref examinedCandidates) / (double)calls).ToString("F1") +
                ", maxSource=" + Interlocked.Read(ref maxSource) +
                ", maxExamined=" + Interlocked.Read(ref maxExamined) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", validatorRejected=" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", reachRejected=" + Interlocked.Read(ref reachRejected) +
                ", avgAcceleratedMs=" + avgMs.ToString("F3") +
                ", maxAcceleratedMs=" + maxMs.ToString("F3") +
                ", avgSortUs=" + avgSortUs.ToString("F2") +
                ", maxSortUs=" + maxSortUs.ToString("F2") +
                ", global32[patched=" + global32Patched + "/" + globalReachable32Patched +
                    ", observed=" + Interlocked.Read(ref global32Observed) +
                    ", reordered=" + gCalls +
                    ", candidates=" + Interlocked.Read(ref global32Candidates) +
                    ", avgSortUs=" + gAvgUs.ToString("F2") +
                    ", maxSortUs=" + gMaxUs.ToString("F2") +
                    ", failures=" + Interlocked.Read(ref global32Failures) + "]" +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Small/medium searches remain Vanilla until their sampled live shape is measurably slow; admitted shapes periodically re-sample Vanilla and self-revoke when the fast path stops winning.";
        }

        internal struct SampleState
        {
            internal ShapeStats Stats;
            internal long Started;
            internal bool Sample;
            internal bool AdmittedShadow;
        }

        private sealed class ShapeStats
        {
            internal readonly bool Custom;
            internal long Seen;
            internal int VanillaSamples;
            internal double VanillaEmaTicks;
            internal long VanillaMaxTicks;
            internal int VanillaSlowSamples;
            internal bool Admitted;
            internal long AdmittedCalls;
            internal int FastSamples;
            internal double FastEmaTicks;
            internal long FastMaxTicks;
            internal long CooldownUntilFrame;
            internal int Admissions;
            internal int DeAdmissions;

            internal ShapeStats(bool custom)
            {
                Custom = custom;
            }
        }

        private struct ShapeKey : IEquatable<ShapeKey>
        {
            internal readonly bool Custom;
            private readonly int bucket;
            private readonly PathEndMode endMode;
            private readonly TraverseMode traverseMode;
            private readonly MethodBase validatorMethod;
            private readonly Type validatorTargetType;
            private readonly Type sourceType;

            internal ShapeKey(bool custom, int bucket, PathEndMode endMode, TraverseMode traverseMode,
                MethodBase validatorMethod, Type validatorTargetType, Type sourceType)
            {
                Custom = custom;
                this.bucket = bucket;
                this.endMode = endMode;
                this.traverseMode = traverseMode;
                this.validatorMethod = validatorMethod;
                this.validatorTargetType = validatorTargetType;
                this.sourceType = sourceType;
            }

            public bool Equals(ShapeKey other)
            {
                return Custom == other.Custom && bucket == other.bucket && endMode == other.endMode && traverseMode == other.traverseMode &&
                    ReferenceEquals(validatorMethod, other.validatorMethod) && ReferenceEquals(validatorTargetType, other.validatorTargetType) &&
                    ReferenceEquals(sourceType, other.sourceType);
            }

            public override bool Equals(object obj)
            {
                return obj is ShapeKey && Equals((ShapeKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = Custom ? 1 : 0;
                    hash = hash * 397 ^ bucket;
                    hash = hash * 397 ^ (int)endMode;
                    hash = hash * 397 ^ (int)traverseMode;
                    hash = hash * 397 ^ (validatorMethod == null ? 0 : RuntimeHelpers.GetHashCode(validatorMethod));
                    hash = hash * 397 ^ (validatorTargetType == null ? 0 : RuntimeHelpers.GetHashCode(validatorTargetType));
                    hash = hash * 397 ^ (sourceType == null ? 0 : RuntimeHelpers.GetHashCode(sourceType));
                    return hash;
                }
            }
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;

            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b)
            {
                int distance = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return distance != 0 ? distance : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
