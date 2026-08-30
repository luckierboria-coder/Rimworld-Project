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
    // Returns to the validated S1 >=256 path. Small/medium ThingRequest/custom-list searches stay
    // Vanilla until sampled live timings prove that their query shape is persistently expensive.
    // Admitted shapes periodically shadow Vanilla and self-revoke if the fast EMA stops winning.
    internal static class JobGiverLearnedAdmission0419S3
    {
        internal const string FeatureId = "ai.jobLearnedSearch";

        private const int StaticThreshold = 256;
        private const int LearnMinSource = 16;
        private const int MaxSource = 16384;
        private const int MaxShapes = 512;
        private const int WarmupSamples = 8;
        private const int LearnSampleMask = 31;      // 1/32 after warmup
        private const int ShadowSampleMask = 63;     // 1/64 while admitted
        private const int JudgeFastSamples = 32;
        private const int CooldownFrames = 3600;
        private const double AdmitEmaMs = 1.50;
        private const double SlowMs = 2.00;
        private const double StrongSlowMs = 4.00;
        private const double RequiredFastRatio = 0.90;

        [ThreadStatic] private static Candidate[] scratch;
        [ThreadStatic] private static Candidate[] globalScratch;

        private static readonly Dictionary<ShapeKey, ShapeStats> Shapes = new Dictionary<ShapeKey, ShapeStats>(128);

        private static volatile bool enabled = true;
        private static volatile bool patched;
        private static volatile bool global32Patched;
        private static volatile bool globalReachable32Patched;

        private static long observed;
        private static long inScope;
        private static long staticAccelerated;
        private static long learnedAccelerated;
        private static long learnedThingAccelerated;
        private static long learnedCustomAccelerated;
        private static long found;
        private static long noResult;
        private static long vanillaSamples;
        private static long shadowSamples;
        private static long admissions;
        private static long deAdmissions;
        private static long shapeCapBypass;
        private static long customUnsupportedBypass;
        private static long smallBypass;
        private static long tooLargeBypass;
        private static long unsupportedTraverseBypass;
        private static long invalidContextBypass;
        private static long sourceFaultBypass;
        private static long bucket0To15;
        private static long bucket16To31;
        private static long bucket32To63;
        private static long bucket64To127;
        private static long bucket128To255;
        private static long bucket256To383;
        private static long bucket384To511;
        private static long bucket512Plus;
        private static long sourceCandidates;
        private static long keptCandidates;
        private static long examinedCandidates;
        private static long validatorCalls;
        private static long validatorRejected;
        private static long reachChecks;
        private static long reachRejected;
        private static long fastTicks;
        private static long maxFastTicks;
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
            if (harmony == null) return;
            try
            {
                MethodInfo ctrPrefix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Prefix));
                MethodInfo ctrPostfix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Postfix));
                MethodInfo globalPrefix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(Global32Prefix));
                MethodInfo reachablePrefix = AccessTools.Method(typeof(JobGiverLearnedAdmission0419S3), nameof(GlobalReachable32Prefix));

                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                int ctrCount = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (IsCtrOverload(method))
                    {
                        harmony.Patch(method,
                            prefix: new HarmonyMethod(ctrPrefix) { priority = Priority.First + 100 },
                            postfix: new HarmonyMethod(ctrPostfix) { priority = Priority.Last - 100 });
                        ctrCount++;
                        continue;
                    }

                    ParameterInfo[] p = method == null ? null : method.GetParameters();
                    if (method != null && p != null && method.Name == "ClosestThing_Global" && p.Length == 5 && p[0].ParameterType == typeof(IntVec3))
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(globalPrefix) { priority = Priority.First + 80 });
                        global32Patched = true;
                    }
                    else if (method != null && p != null && method.Name == "ClosestThing_Global_Reachable" && p.Length == 8 && p[0].ParameterType == typeof(IntVec3))
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(reachablePrefix) { priority = Priority.First + 80 });
                        globalReachable32Patched = true;
                    }
                }

                patched = ctrCount > 0;
                Log.Message("[RimMT] V0.4.19-JS1.1S3 Learned Admission installed on " + ctrCount +
                    " ClosestThingReachable overload(s). >=256 normal ThingRequest searches keep S1 fast-path behavior. 16-255 normal/custom-list searches stay Vanilla until sampled shape timing admits them; admitted shapes shadow Vanilla every 64 calls and self-revoke when fast EMA is not materially better. Global32 supplement covers only 32-63 candidates.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                patched = false;
                Log.Warning("[RimMT] V0.4.19-JS1.1S3 install failed; S1/Vanilla remain authoritative. " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void SetEnabled(bool value) { enabled = value; }

        private static bool IsCtrOverload(MethodInfo method)
        {
            if (method == null || method.ReturnType != typeof(Thing) || method.Name != "ClosestThingReachable") return false;
            ParameterInfo[] p = method.GetParameters();
            return p.Length >= 8 &&
                p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(Map) &&
                p[2].ParameterType == typeof(ThingRequest) && p[3].ParameterType == typeof(PathEndMode) &&
                p[4].ParameterType == typeof(TraverseParms) && p[5].ParameterType == typeof(float) &&
                p[6].ParameterType == typeof(Predicate<Thing>) && typeof(IEnumerable<Thing>).IsAssignableFrom(p[7].ParameterType);
        }

        public static bool Prefix(
            IntVec3 __0, Map __1, ThingRequest __2, PathEndMode __3, TraverseParms __4,
            float __5, Predicate<Thing> __6, IEnumerable<Thing> __7,
            ref Thing __result, ref SampleState __state)
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
                try { source = map.listerThings.ThingsMatching(__2); }
                catch { Interlocked.Increment(ref sourceFaultBypass); return true; }
                if (source == null) { Interlocked.Increment(ref sourceFaultBypass); return true; }
            }

            int count = source.Count;
            RecordBucket(count);
            if (count > MaxSource) { Interlocked.Increment(ref tooLargeBypass); return true; }

            // Validated S1 path remains unconditional only for normal ThingRequest sources >=256.
            if (!custom && count >= StaticThreshold)
                return RunFast(source, count, null, false, __0, map, __3, __4, __5, __6, ref __result);

            if (count < LearnMinSource)
            {
                Interlocked.Increment(ref smallBypass);
                return true;
            }

            ShapeKey key = new ShapeKey(custom, BucketFor(count), __3, mode,
                __6 == null ? null : __6.Method,
                __6 == null || __6.Target == null ? null : __6.Target.GetType(),
                custom ? source.GetType() : null);
            ShapeStats stats = GetShape(key, custom);
            if (stats == null)
            {
                Interlocked.Increment(ref shapeCapBypass);
                return true;
            }

            stats.Seen++;
            long frame = RimMTRuntime.MainThreadFrames;
            if (stats.CooldownUntilFrame > frame)
            {
                if (ShouldSample(stats)) BeginSample(stats, false, ref __state);
                return true;
            }
            if (stats.CooldownUntilFrame != 0 && stats.CooldownUntilFrame <= frame)
            {
                stats.CooldownUntilFrame = 0;
                ResetWindow(stats);
            }

            if (!stats.Admitted)
            {
                if (ShouldSample(stats)) BeginSample(stats, false, ref __state);
                return true;
            }

            stats.AdmittedCalls++;
            if ((stats.AdmittedCalls & ShadowSampleMask) == 0)
            {
                BeginSample(stats, true, ref __state);
                return true;
            }

            return RunFast(source, count, stats, true, __0, map, __3, __4, __5, __6, ref __result);
        }

        public static void Postfix(SampleState __state)
        {
            if (!__state.Sample || __state.Stats == null || __state.Started == 0) return;
            long elapsed = Stopwatch.GetTimestamp() - __state.Started;
            ShapeStats stats = __state.Stats;
            stats.VanillaSamples++;
            stats.VanillaEmaTicks = stats.VanillaSamples == 1 ? elapsed : Ema(stats.VanillaEmaTicks, elapsed, 0.20);
            if (elapsed > stats.VanillaMaxTicks) stats.VanillaMaxTicks = elapsed;
            if (ToMs(elapsed) >= SlowMs) stats.VanillaSlowSamples++;
            Interlocked.Increment(ref vanillaSamples);
            if (__state.Shadow) Interlocked.Increment(ref shadowSamples);

            if (!stats.Admitted && stats.CooldownUntilFrame == 0 && ReadyToAdmit(stats))
            {
                stats.Admitted = true;
                stats.AdmittedCalls = 0;
                stats.FastSamples = 0;
                stats.FastEmaTicks = 0;
                stats.FastMaxTicks = 0;
                Interlocked.Increment(ref admissions);
            }
        }

        private static bool RunFast(
            IList source, int count, ShapeStats stats, bool learned,
            IntVec3 root, Map map, PathEndMode endMode, TraverseParms parms, float maxDistance,
            Predicate<Thing> validator, ref Thing result)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                Candidate[] candidates = EnsureScratch(count, false);
                double maxDistSq = (double)maxDistance * maxDistance;
                int kept = 0;
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !thing.Spawned || thing.Map != map) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) continue;
                    long dx = (long)pos.x - root.x;
                    long dz = (long)pos.z - root.z;
                    long dist = dx * dx + dz * dz;
                    if (dist > maxDistSq) continue;
                    candidates[kept++] = new Candidate(thing, dist, i);
                }

                long sortStarted = Stopwatch.GetTimestamp();
                if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long sortElapsed = Stopwatch.GetTimestamp() - sortStarted;
                Interlocked.Add(ref sortTicks, sortElapsed);
                UpdateMax(ref maxSortTicks, sortElapsed);

                int examined = 0, vCalls = 0, vReject = 0, rCalls = 0, rReject = 0;
                for (int i = 0; i < kept; i++)
                {
                    Thing thing = candidates[i].Thing;
                    examined++;
                    if (validator != null)
                    {
                        vCalls++;
                        if (!validator(thing)) { vReject++; continue; }
                    }
                    rCalls++;
                    if (!map.reachability.CanReach(root, new LocalTargetInfo(thing), endMode, parms))
                    {
                        rReject++;
                        continue;
                    }

                    result = thing;
                    CommitFast(count, kept, examined, vCalls, vReject, rCalls, rReject, learned, stats);
                    Interlocked.Increment(ref found);
                    RecordFast(started, stats);
                    return false;
                }

                result = null;
                CommitFast(count, kept, examined, vCalls, vReject, rCalls, rReject, learned, stats);
                Interlocked.Increment(ref noResult);
                RecordFast(started, stats);
                return false;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (stats != null) Revoke(stats);
                if (Interlocked.Read(ref failures) <= 8)
                    Log.Warning("[RimMT] V0.4.19-JS1.1S3 fast search failed; this call falls back to Vanilla. " + ex.GetType().Name + ": " + ex.Message);
                return true;
            }
        }

        private static void CommitFast(int source, int kept, int examined, int vCalls, int vReject, int rCalls, int rReject, bool learned, ShapeStats stats)
        {
            if (learned)
            {
                Interlocked.Increment(ref learnedAccelerated);
                if (stats != null && stats.Custom) Interlocked.Increment(ref learnedCustomAccelerated);
                else Interlocked.Increment(ref learnedThingAccelerated);
            }
            else Interlocked.Increment(ref staticAccelerated);

            Interlocked.Add(ref sourceCandidates, source);
            Interlocked.Add(ref keptCandidates, kept);
            Interlocked.Add(ref examinedCandidates, examined);
            Interlocked.Add(ref validatorCalls, vCalls);
            Interlocked.Add(ref validatorRejected, vReject);
            Interlocked.Add(ref reachChecks, rCalls);
            Interlocked.Add(ref reachRejected, rReject);
            UpdateMax(ref maxSource, source);
            UpdateMax(ref maxExamined, examined);
        }

        private static void RecordFast(long started, ShapeStats stats)
        {
            long elapsed = Stopwatch.GetTimestamp() - started;
            Interlocked.Add(ref fastTicks, elapsed);
            UpdateMax(ref maxFastTicks, elapsed);
            if (stats == null) return;

            stats.FastSamples++;
            stats.FastEmaTicks = stats.FastSamples == 1 ? elapsed : Ema(stats.FastEmaTicks, elapsed, 0.20);
            if (elapsed > stats.FastMaxTicks) stats.FastMaxTicks = elapsed;

            if (stats.FastSamples >= JudgeFastSamples && stats.VanillaSamples >= WarmupSamples &&
                stats.FastEmaTicks >= stats.VanillaEmaTicks * RequiredFastRatio)
                Revoke(stats);
        }

        private static ShapeStats GetShape(ShapeKey key, bool custom)
        {
            ShapeStats stats;
            if (Shapes.TryGetValue(key, out stats)) return stats;
            if (Shapes.Count >= MaxShapes) return null;
            stats = new ShapeStats(custom);
            Shapes.Add(key, stats);
            return stats;
        }

        private static bool ShouldSample(ShapeStats stats)
        {
            return stats.VanillaSamples < WarmupSamples || (stats.Seen & LearnSampleMask) == 0;
        }

        private static void BeginSample(ShapeStats stats, bool shadow, ref SampleState state)
        {
            state.Stats = stats;
            state.Started = Stopwatch.GetTimestamp();
            state.Sample = true;
            state.Shadow = shadow;
        }

        private static bool ReadyToAdmit(ShapeStats stats)
        {
            if (stats.VanillaSamples < WarmupSamples) return false;
            return ToMs(stats.VanillaEmaTicks) >= AdmitEmaMs &&
                (ToMs(stats.VanillaMaxTicks) >= StrongSlowMs || stats.VanillaSlowSamples >= 2);
        }

        private static void Revoke(ShapeStats stats)
        {
            if (stats.Admitted || stats.CooldownUntilFrame == 0)
                Interlocked.Increment(ref deAdmissions);
            stats.Admitted = false;
            stats.CooldownUntilFrame = RimMTRuntime.MainThreadFrames + CooldownFrames;
        }

        private static void ResetWindow(ShapeStats stats)
        {
            stats.VanillaSamples = 0;
            stats.VanillaEmaTicks = 0;
            stats.VanillaMaxTicks = 0;
            stats.VanillaSlowSamples = 0;
            stats.FastSamples = 0;
            stats.FastEmaTicks = 0;
            stats.FastMaxTicks = 0;
            stats.AdmittedCalls = 0;
        }

        public static void Global32Prefix(object[] __args)
        {
            Interlocked.Increment(ref global32Observed);
            if (__args != null && __args.Length >= 5) TryGlobal32(__args, 0, 1, 2, 4);
        }

        public static void GlobalReachable32Prefix(object[] __args)
        {
            Interlocked.Increment(ref global32Observed);
            if (__args != null && __args.Length >= 8) TryGlobal32(__args, 0, 2, 5, 7);
        }

        private static void TryGlobal32(object[] args, int centerIndex, int setIndex, int distanceIndex, int priorityIndex)
        {
            if (!enabled || !FeatureGate.IsEnabled(FeatureId) || !JobGiverGlobalNearest04181.InJobGiverScope ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing || args[priorityIndex] != null)
                return;

            IList source = args[setIndex] as IList;
            if (source == null || source.Count < 32 || source.Count >= 64) return;

            try
            {
                IntVec3 center = (IntVec3)args[centerIndex];
                float maxDistance = Convert.ToSingle(args[distanceIndex]);
                double maxDistSq = (double)maxDistance * maxDistance;
                Candidate[] candidates = EnsureScratch(source.Count, true);
                int kept = 0;
                for (int i = 0; i < source.Count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null || !thing.Spawned) continue;
                    IntVec3 pos = thing.Position;
                    if (!pos.IsValid) return;
                    long dx = (long)pos.x - center.x;
                    long dz = (long)pos.z - center.z;
                    long dist = dx * dx + dz * dz;
                    if (dist > maxDistSq) continue;
                    candidates[kept++] = new Candidate(thing, dist, i);
                }

                long started = Stopwatch.GetTimestamp();
                if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                long elapsed = Stopwatch.GetTimestamp() - started;
                Thing[] ordered = new Thing[kept];
                for (int i = 0; i < kept; i++) ordered[i] = candidates[i].Thing;
                args[setIndex] = ordered;
                Interlocked.Increment(ref global32Reordered);
                Interlocked.Add(ref global32Candidates, source.Count);
                Interlocked.Add(ref global32SortTicks, elapsed);
                UpdateMax(ref global32MaxSortTicks, elapsed);
            }
            catch { Interlocked.Increment(ref global32Failures); }
        }

        private static Candidate[] EnsureScratch(int required, bool global)
        {
            Candidate[] current = global ? globalScratch : scratch;
            if (current != null && current.Length >= required) return current;
            int capacity = global ? 64 : 256;
            while (capacity < required) capacity <<= 1;
            current = new Candidate[capacity];
            if (global) globalScratch = current; else scratch = current;
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

        private static void RecordBucket(int count)
        {
            if (count < 16) Interlocked.Increment(ref bucket0To15);
            else if (count < 32) Interlocked.Increment(ref bucket16To31);
            else if (count < 64) Interlocked.Increment(ref bucket32To63);
            else if (count < 128) Interlocked.Increment(ref bucket64To127);
            else if (count < 256) Interlocked.Increment(ref bucket128To255);
            else if (count < 384) Interlocked.Increment(ref bucket256To383);
            else if (count < 512) Interlocked.Increment(ref bucket384To511);
            else Interlocked.Increment(ref bucket512Plus);
        }

        private static double Ema(double prior, double sample, double alpha) { return prior + (sample - prior) * alpha; }
        private static double ToMs(double ticks) { return ticks * 1000.0 / Stopwatch.Frequency; }

        private static void UpdateMax(ref long field, long value)
        {
            long seen;
            while (value > (seen = Interlocked.Read(ref field)))
                if (Interlocked.CompareExchange(ref field, value, seen) == seen) break;
        }

        internal static string Summary()
        {
            int admitted = 0, cooling = 0;
            long frame = RimMTRuntime.MainThreadFrames;
            foreach (KeyValuePair<ShapeKey, ShapeStats> pair in Shapes)
            {
                if (pair.Value.Admitted) admitted++;
                else if (pair.Value.CooldownUntilFrame > frame) cooling++;
            }

            long staticCalls = Interlocked.Read(ref staticAccelerated);
            long learnedCalls = Interlocked.Read(ref learnedAccelerated);
            long totalCalls = staticCalls + learnedCalls;
            double avgFastMs = totalCalls == 0 ? 0 : ToMs(Interlocked.Read(ref fastTicks)) / totalCalls;
            double maxFastMs = ToMs(Interlocked.Read(ref maxFastTicks));
            double avgSortUs = totalCalls == 0 ? 0 : (Interlocked.Read(ref sortTicks) * 1000000.0 / Stopwatch.Frequency) / totalCalls;
            double maxSortUs = Interlocked.Read(ref maxSortTicks) * 1000000.0 / Stopwatch.Frequency;
            long gCalls = Interlocked.Read(ref global32Reordered);
            double gAvgUs = gCalls == 0 ? 0 : (Interlocked.Read(ref global32SortTicks) * 1000000.0 / Stopwatch.Frequency) / gCalls;
            double gMaxUs = Interlocked.Read(ref global32MaxSortTicks) * 1000000.0 / Stopwatch.Frequency;

            return "JobGiver learned-search JS1.1S3: patched=" + patched +
                ", enabled=" + enabled +
                ", staticThreshold=" + StaticThreshold +
                ", learnMin=" + LearnMinSource +
                ", admission[warmup/emaMs/slowMs/strongMs]=" + WarmupSamples + "/" + AdmitEmaMs.ToString("F2") + "/" + SlowMs.ToString("F2") + "/" + StrongSlowMs.ToString("F2") +
                ", shadowEvery=64, judgeFast=" + JudgeFastSamples +
                ", requiredFastRatio=" + RequiredFastRatio.ToString("F2") +
                ", cooldownFrames=" + CooldownFrames +
                ", observed=" + Interlocked.Read(ref observed) +
                ", inScope=" + Interlocked.Read(ref inScope) +
                ", shapes=" + Shapes.Count +
                ", admitted/cooling=" + admitted + "/" + cooling +
                ", admissions/deAdmissions=" + Interlocked.Read(ref admissions) + "/" + Interlocked.Read(ref deAdmissions) +
                ", vanillaSamples=" + Interlocked.Read(ref vanillaSamples) +
                ", shadowSamples=" + Interlocked.Read(ref shadowSamples) +
                ", accelerated=" + totalCalls +
                ", static/learned=" + staticCalls + "/" + learnedCalls +
                ", learnedThing/custom=" + Interlocked.Read(ref learnedThingAccelerated) + "/" + Interlocked.Read(ref learnedCustomAccelerated) +
                ", found/noResult=" + Interlocked.Read(ref found) + "/" + Interlocked.Read(ref noResult) +
                ", sourceBuckets0-15/16-31/32-63/64-127/128-255/256-383/384-511/512+=" +
                    Interlocked.Read(ref bucket0To15) + "/" + Interlocked.Read(ref bucket16To31) + "/" + Interlocked.Read(ref bucket32To63) + "/" +
                    Interlocked.Read(ref bucket64To127) + "/" + Interlocked.Read(ref bucket128To255) + "/" + Interlocked.Read(ref bucket256To383) + "/" +
                    Interlocked.Read(ref bucket384To511) + "/" + Interlocked.Read(ref bucket512Plus) +
                ", shapeCapBypass=" + Interlocked.Read(ref shapeCapBypass) +
                ", customUnsupportedBypass=" + Interlocked.Read(ref customUnsupportedBypass) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", tooLargeBypass=" + Interlocked.Read(ref tooLargeBypass) +
                ", unsupportedTraverseBypass=" + Interlocked.Read(ref unsupportedTraverseBypass) +
                ", invalidContextBypass=" + Interlocked.Read(ref invalidContextBypass) +
                ", sourceFaultBypass=" + Interlocked.Read(ref sourceFaultBypass) +
                ", avgSource=" + (totalCalls == 0 ? 0 : Interlocked.Read(ref sourceCandidates) / (double)totalCalls).ToString("F1") +
                ", avgExamined=" + (totalCalls == 0 ? 0 : Interlocked.Read(ref examinedCandidates) / (double)totalCalls).ToString("F1") +
                ", maxSource=" + Interlocked.Read(ref maxSource) +
                ", maxExamined=" + Interlocked.Read(ref maxExamined) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", validatorRejected=" + Interlocked.Read(ref validatorRejected) +
                ", reachChecks=" + Interlocked.Read(ref reachChecks) +
                ", reachRejected=" + Interlocked.Read(ref reachRejected) +
                ", avgFastMs=" + avgFastMs.ToString("F3") +
                ", maxFastMs=" + maxFastMs.ToString("F3") +
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
                ". Small/medium searches remain Vanilla until their sampled shape is measurably slow; admitted shapes re-sample Vanilla and self-revoke if the fast path stops winning.";
        }

        internal struct SampleState
        {
            internal ShapeStats Stats;
            internal long Started;
            internal bool Sample;
            internal bool Shadow;
        }

        internal sealed class ShapeStats
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

            internal ShapeStats(bool custom) { Custom = custom; }
        }

        private struct ShapeKey : IEquatable<ShapeKey>
        {
            private readonly bool custom;
            private readonly int bucket;
            private readonly PathEndMode endMode;
            private readonly TraverseMode traverseMode;
            private readonly MethodBase validatorMethod;
            private readonly Type validatorTargetType;
            private readonly Type sourceType;

            internal ShapeKey(bool custom, int bucket, PathEndMode endMode, TraverseMode traverseMode,
                MethodBase validatorMethod, Type validatorTargetType, Type sourceType)
            {
                this.custom = custom;
                this.bucket = bucket;
                this.endMode = endMode;
                this.traverseMode = traverseMode;
                this.validatorMethod = validatorMethod;
                this.validatorTargetType = validatorTargetType;
                this.sourceType = sourceType;
            }

            public bool Equals(ShapeKey other)
            {
                return custom == other.custom && bucket == other.bucket && endMode == other.endMode && traverseMode == other.traverseMode &&
                    ReferenceEquals(validatorMethod, other.validatorMethod) && ReferenceEquals(validatorTargetType, other.validatorTargetType) &&
                    ReferenceEquals(sourceType, other.sourceType);
            }

            public override bool Equals(object obj) { return obj is ShapeKey && Equals((ShapeKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = custom ? 1 : 0;
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
