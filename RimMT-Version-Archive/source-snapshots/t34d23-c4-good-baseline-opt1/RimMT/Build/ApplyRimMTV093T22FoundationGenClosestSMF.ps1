$ErrorActionPreference = 'Stop'

function Replace-OrThrow {
    param([string]$Text,[string]$Old,[string]$New,[string]$Label)
    if (-not $Text.Contains($Old)) { throw "RimMT V0.9.3-T22 transform anchor not found: $Label" }
    return $Text.Replace($Old,$New)
}

# RimMT V0.9.3-T22 Foundation III
# 1) generic package-local GenClosest_Global_NewTemp distance/source-order index
# 2) dispatcher bridge moved off TickManagerUpdate to Root_Play.Update for SimplyMoreFPS coexistence
# 3) direct WorldTick/WorldComponentUtility timing so giant world-side freezes are no longer unattributed

$genPath = 'RimMT/Source/RimMT/AI/GenClosestTransactionIndex093T22.cs'
$gen = @'
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

namespace RimMT
{
    internal static class GenClosestTransactionIndex093T22
    {
        private const int MinSourceCount = 32;
        private const int MaxSourceCount = 4096;
        private const int ReplayPrefixPriority = Priority.Last - 500;

        [ThreadStatic] private static int packageDepth;
        [ThreadStatic] private static PackageContext current;

        private static bool installed;
        private static bool packagePatched;
        private static bool globalPatched;
        private static bool chainAuthoritativeSafe;
        private static int foreignPrefixes;
        private static int foreignPostfixes;
        private static int foreignTranspilers;
        private static int foreignFinalizers;
        private static int runOriginalReaders;
        private static int installFailures;

        private static long packages;
        private static long observed;
        private static long eligible;
        private static long firstObservationBypass;
        private static long builds;
        private static long reuses;
        private static long authoritative;
        private static long authoritativeNull;
        private static long candidatesVisited;
        private static long validatorCalls;
        private static long sourceShapeBypass;
        private static long sizeBypass;
        private static long priorityBypass;
        private static long haulSourceBypass;
        private static long foreignChainBypass;
        private static long runOriginalBypass;
        private static long mutationRebuilds;
        private static long buildTicks;
        private static long maxBuildTicks;
        private static long failures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase package = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage",
                    new Type[] { typeof(Pawn), typeof(JobIssueParams) });
                if (package != null)
                {
                    harmony.Patch(package,
                        prefix: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackagePrefix))
                        { priority = Priority.First + 200 },
                        finalizer: new HarmonyMethod(typeof(GenClosestTransactionIndex093T22), nameof(PackageFinalizer))
                        { priority = Priority.Last - 200 });
                    packagePatched = true;
                }

                MethodBase global = AccessTools.Method(typeof(GenClosest), "ClosestThing_Global_NewTemp",
                    new Type[]
                    {
                        typeof(IntVec3), typeof(IEnumerable), typeof(float), typeof(Predicate<Thing>),
                        typeof(Func<Thing, float>), typeof(bool)
                    });

                if (global != null)
                {
                    ChainAudit audit = InspectChain(global);
                    foreignPrefixes = audit.ForeignPrefixes;
                    foreignPostfixes = audit.ForeignPostfixes;
                    foreignTranspilers = audit.ForeignTranspilers;
                    foreignFinalizers = audit.ForeignFinalizers;
                    runOriginalReaders = audit.RunOriginalPostfixes;
                    chainAuthoritativeSafe = !audit.Unknown &&
                        audit.ForeignTranspilers == 0 &&
                        audit.ForeignFinalizers == 0 &&
                        audit.RunOriginalPostfixes == 0;

                    HarmonyMethod prefix = new HarmonyMethod(
                        typeof(GenClosestTransactionIndex093T22), nameof(GlobalPrefix))
                    {
                        priority = ReplayPrefixPriority,
                        after = audit.ForeignPrefixOwners
                    };
                    harmony.Patch(global, prefix: prefix);
                    globalPatched = true;
                }

                installed = packagePatched && globalPatched;
                Log.Message("[RimMT] T22 generic GenClosest transaction index installed=" + installed +
                    ", chainSafe=" + chainAuthoritativeSafe +
                    ", foreignPrefix/postfix/transpiler/finalizer=" +
                    foreignPrefixes + "/" + foreignPostfixes + "/" +
                    foreignTranspilers + "/" + foreignFinalizers +
                    ". Index lifetime=one synchronous JobGiver_Work package; live validator remains authority.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T22 GenClosest transaction index failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void PackagePrefix(ref PackageState __state)
        {
            __state = default(PackageState);
            if (!RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing)
                return;

            __state.Entered = true;
            __state.Outermost = packageDepth == 0;
            packageDepth++;
            if (__state.Outermost)
            {
                current = new PackageContext();
                __state.Context = current;
                Interlocked.Increment(ref packages);
            }
            else
            {
                __state.Context = current;
            }
        }

        public static Exception PackageFinalizer(Exception __exception, PackageState __state)
        {
            if (!__state.Entered) return __exception;
            if (packageDepth > 0) packageDepth--;
            if (__state.Outermost && ReferenceEquals(current, __state.Context))
                current = null;
            return __exception;
        }

        public static bool GlobalPrefix(
            IntVec3 center,
            IEnumerable searchSet,
            float maxDistance,
            Predicate<Thing> validator,
            Func<Thing, float> priorityGetter,
            bool lookInHaulSources,
            bool __runOriginal,
            ref Thing __result)
        {
            Interlocked.Increment(ref observed);

            PackageContext context = current;
            if (context == null || packageDepth <= 0 || !RimMTThreadGuard.IsMainThread)
                return true;

            if (!__runOriginal)
            {
                Interlocked.Increment(ref runOriginalBypass);
                return true;
            }

            if (!chainAuthoritativeSafe)
            {
                Interlocked.Increment(ref foreignChainBypass);
                return true;
            }

            if (priorityGetter != null)
            {
                Interlocked.Increment(ref priorityBypass);
                return true;
            }

            if (lookInHaulSources)
            {
                Interlocked.Increment(ref haulSourceBypass);
                return true;
            }

            IList list = searchSet as IList;
            if (list == null)
            {
                Interlocked.Increment(ref sourceShapeBypass);
                return true;
            }

            int count = list.Count;
            if (count < MinSourceCount || count > MaxSourceCount)
            {
                Interlocked.Increment(ref sizeBypass);
                return true;
            }

            Interlocked.Increment(ref eligible);
            IndexKey key = new IndexKey(searchSet, center);

            CandidateIndex index;
            if (!context.Indexes.TryGetValue(key, out index))
            {
                int seen;
                if (!context.Seen.TryGetValue(key, out seen))
                {
                    context.Seen.Add(key, 1);
                    Interlocked.Increment(ref firstObservationBypass);
                    return true;
                }

                long started = Stopwatch.GetTimestamp();
                try
                {
                    index = CandidateIndex.Build(list, center);
                }
                catch
                {
                    Interlocked.Increment(ref failures);
                    return true;
                }

                if (index == null)
                {
                    Interlocked.Increment(ref sourceShapeBypass);
                    return true;
                }

                long elapsed = Stopwatch.GetTimestamp() - started;
                if (elapsed > 0)
                {
                    Interlocked.Add(ref buildTicks, elapsed);
                    UpdateMax(ref maxBuildTicks, elapsed);
                }
                context.Indexes[key] = index;
                Interlocked.Increment(ref builds);
            }
            else
            {
                if (!index.FingerprintMatches(list))
                {
                    Interlocked.Increment(ref mutationRebuilds);
                    long started = Stopwatch.GetTimestamp();
                    CandidateIndex rebuilt;
                    try { rebuilt = CandidateIndex.Build(list, center); }
                    catch
                    {
                        Interlocked.Increment(ref failures);
                        return true;
                    }

                    if (rebuilt == null)
                    {
                        context.Indexes.Remove(key);
                        return true;
                    }

                    long elapsed = Stopwatch.GetTimestamp() - started;
                    if (elapsed > 0)
                    {
                        Interlocked.Add(ref buildTicks, elapsed);
                        UpdateMax(ref maxBuildTicks, elapsed);
                    }
                    context.Indexes[key] = rebuilt;
                    index = rebuilt;
                    Interlocked.Increment(ref builds);
                }
                else
                {
                    Interlocked.Increment(ref reuses);
                }
            }

            float maxDistanceSquared = maxDistance * maxDistance;
            Candidate[] candidates = index.Candidates;
            for (int i = 0; i < candidates.Length; i++)
            {
                Candidate c = candidates[i];
                if (c.DistanceSquared > maxDistanceSquared)
                    break;

                Interlocked.Increment(ref candidatesVisited);
                Thing thing = c.Thing;
                if (thing == null || !thing.Spawned)
                {
                    context.Indexes.Remove(key);
                    Interlocked.Increment(ref mutationRebuilds);
                    return true;
                }

                if (validator != null)
                {
                    Interlocked.Increment(ref validatorCalls);
                    if (!validator(thing))
                        continue;
                }

                __result = thing;
                Interlocked.Increment(ref authoritative);
                return false;
            }

            __result = null;
            Interlocked.Increment(ref authoritative);
            Interlocked.Increment(ref authoritativeNull);
            return false;
        }

        internal static string Summary()
        {
            long buildCount = Interlocked.Read(ref builds);
            double avgBuildUs = buildCount == 0 ? 0.0 :
                Interlocked.Read(ref buildTicks) * 1000000.0 / Stopwatch.Frequency / buildCount;
            double maxBuildUs = Interlocked.Read(ref maxBuildTicks) * 1000000.0 / Stopwatch.Frequency;

            return "T22 generic GenClosest transaction index: installed=" + installed +
                ", packagePatched=" + packagePatched +
                ", globalNewTempPatched=" + globalPatched +
                ", chainAuthoritativeSafe=" + chainAuthoritativeSafe +
                ", chain[foreignPrefixes=" + foreignPrefixes +
                ", foreignPostfixes=" + foreignPostfixes +
                ", transpilers=" + foreignTranspilers +
                ", finalizers=" + foreignFinalizers +
                ", runOriginalReaders=" + runOriginalReaders + "]" +
                ", packages=" + Interlocked.Read(ref packages) +
                ", observed=" + Interlocked.Read(ref observed) +
                ", eligible=" + Interlocked.Read(ref eligible) +
                ", firstObservationBypass=" + Interlocked.Read(ref firstObservationBypass) +
                ", builds=" + buildCount +
                ", reuses=" + Interlocked.Read(ref reuses) +
                ", authoritative=" + Interlocked.Read(ref authoritative) +
                ", authoritativeNull=" + Interlocked.Read(ref authoritativeNull) +
                ", candidatesVisited=" + Interlocked.Read(ref candidatesVisited) +
                ", validatorCalls=" + Interlocked.Read(ref validatorCalls) +
                ", bypass[shape/size/priority/haul/foreign/runOriginal]=" +
                Interlocked.Read(ref sourceShapeBypass) + "/" +
                Interlocked.Read(ref sizeBypass) + "/" +
                Interlocked.Read(ref priorityBypass) + "/" +
                Interlocked.Read(ref haulSourceBypass) + "/" +
                Interlocked.Read(ref foreignChainBypass) + "/" +
                Interlocked.Read(ref runOriginalBypass) +
                ", mutationRebuilds=" + Interlocked.Read(ref mutationRebuilds) +
                ", avgBuildUs=" + avgBuildUs.ToString("F2") +
                ", maxBuildUs=" + maxBuildUs.ToString("F2") +
                ", failures=" + Interlocked.Read(ref failures) +
                ", installFailures=" + installFailures +
                ". Scope=one synchronous JobGiver_Work package, repeated IList source+center only, priorityGetter=null, lookInHaulSources=false, all members spawned; sorted by distance then source order; original live validator is called exactly once per visited candidate.";
        }

        private static ChainAudit InspectChain(MethodBase method)
        {
            ChainAudit audit = new ChainAudit();
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return audit;

                HashSet<string> prefixOwners = new HashSet<string>();
                foreach (Patch patch in info.Prefixes)
                {
                    if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        continue;
                    audit.ForeignPrefixes++;
                    if (!string.IsNullOrEmpty(patch.owner))
                        prefixOwners.Add(patch.owner);
                }

                foreach (Patch patch in info.Postfixes)
                {
                    if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        continue;
                    audit.ForeignPostfixes++;
                    if (PatchReadsRunOriginal(patch))
                        audit.RunOriginalPostfixes++;
                }

                foreach (Patch patch in info.Transpilers)
                    if (patch != null && !string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        audit.ForeignTranspilers++;

                foreach (Patch patch in info.Finalizers)
                    if (patch != null && !string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal))
                        audit.ForeignFinalizers++;

                audit.ForeignPrefixOwners = new string[prefixOwners.Count];
                prefixOwners.CopyTo(audit.ForeignPrefixOwners);
                return audit;
            }
            catch
            {
                audit.Unknown = true;
                return audit;
            }
        }

        private static bool PatchReadsRunOriginal(Patch patch)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            if (method == null) return true;
            ParameterInfo[] pars = method.GetParameters();
            for (int i = 0; i < pars.Length; i++)
                if (pars[i].Name == "__runOriginal")
                    return true;
            return false;
        }

        private static void UpdateMax(ref long field, long value)
        {
            long observedValue;
            while (value > (observedValue = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observedValue) == observedValue)
                    break;
            }
        }

        internal struct PackageState
        {
            internal bool Entered;
            internal bool Outermost;
            internal PackageContext Context;
        }

        internal sealed class PackageContext
        {
            internal readonly Dictionary<IndexKey, int> Seen = new Dictionary<IndexKey, int>();
            internal readonly Dictionary<IndexKey, CandidateIndex> Indexes =
                new Dictionary<IndexKey, CandidateIndex>();
        }

        internal struct IndexKey : IEquatable<IndexKey>
        {
            private readonly object source;
            private readonly IntVec3 center;

            internal IndexKey(object source, IntVec3 center)
            {
                this.source = source;
                this.center = center;
            }

            public bool Equals(IndexKey other)
            {
                return ReferenceEquals(source, other.source) && center == other.center;
            }

            public override bool Equals(object obj)
            {
                return obj is IndexKey && Equals((IndexKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    return RuntimeHelpers.GetHashCode(source) * 397 ^ center.GetHashCode();
                }
            }
        }

        internal sealed class CandidateIndex
        {
            internal readonly Candidate[] Candidates;
            private readonly Sample[] samples;
            private readonly int count;

            private CandidateIndex(Candidate[] candidates, Sample[] samples, int count)
            {
                Candidates = candidates;
                this.samples = samples;
                this.count = count;
            }

            internal static CandidateIndex Build(IList list, IntVec3 center)
            {
                if (list == null) return null;
                int count = list.Count;
                Candidate[] candidates = new Candidate[count];

                for (int i = 0; i < count; i++)
                {
                    Thing thing = list[i] as Thing;
                    if (thing == null || !thing.Spawned)
                        return null;
                    int distance = (center - thing.PositionHeld).LengthHorizontalSquared;
                    candidates[i] = new Candidate(thing, distance, i);
                }

                Array.Sort(candidates, delegate(Candidate a, Candidate b)
                {
                    int cmp = a.DistanceSquared.CompareTo(b.DistanceSquared);
                    if (cmp != 0) return cmp;
                    return a.SourceIndex.CompareTo(b.SourceIndex);
                });

                int sampleCount = Math.Min(8, count);
                Sample[] samples = new Sample[sampleCount];
                if (sampleCount > 0)
                {
                    for (int s = 0; s < sampleCount; s++)
                    {
                        int index = sampleCount == 1 ? 0 :
                            (int)((long)s * (count - 1) / (sampleCount - 1));
                        Thing thing = list[index] as Thing;
                        samples[s] = new Sample(index, thing, thing.PositionHeld);
                    }
                }

                return new CandidateIndex(candidates, samples, count);
            }

            internal bool FingerprintMatches(IList list)
            {
                if (list == null || list.Count != count) return false;
                for (int i = 0; i < samples.Length; i++)
                {
                    Sample sample = samples[i];
                    Thing currentThing = list[sample.Index] as Thing;
                    if (!ReferenceEquals(currentThing, sample.Thing) ||
                        currentThing == null || !currentThing.Spawned ||
                        currentThing.PositionHeld != sample.Position)
                        return false;
                }
                return true;
            }
        }

        internal struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly int DistanceSquared;
            internal readonly int SourceIndex;

            internal Candidate(Thing thing, int distanceSquared, int sourceIndex)
            {
                Thing = thing;
                DistanceSquared = distanceSquared;
                SourceIndex = sourceIndex;
            }
        }

        internal struct Sample
        {
            internal readonly int Index;
            internal readonly Thing Thing;
            internal readonly IntVec3 Position;

            internal Sample(int index, Thing thing, IntVec3 position)
            {
                Index = index;
                Thing = thing;
                Position = position;
            }
        }

        internal sealed class ChainAudit
        {
            internal int ForeignPrefixes;
            internal int ForeignPostfixes;
            internal int ForeignTranspilers;
            internal int ForeignFinalizers;
            internal int RunOriginalPostfixes;
            internal string[] ForeignPrefixOwners = new string[0];
            internal bool Unknown;
        }
    }
}
'@
Set-Content $genPath $gen -Encoding UTF8

$worldPath = 'RimMT/Source/RimMT/Diagnostics/WorldTailBoundary093T22.cs'
$world = @'
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld.Planet;

namespace RimMT
{
    internal static class WorldTailBoundary093T22
    {
        private static bool installed;
        private static int patched;
        private static int installFailures;

        private static long worldCalls;
        private static long worldTicks;
        private static long worldMaxTicks;
        private static long worldOver50;
        private static long worldOver100;
        private static long worldOver1000;

        private static long componentCalls;
        private static long componentTicks;
        private static long componentMaxTicks;
        private static long componentOver50;
        private static long componentOver100;
        private static long componentOver1000;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase worldTick = AccessTools.Method(typeof(World), "WorldTick");
                if (worldTick != null)
                {
                    harmony.Patch(worldTick,
                        prefix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(WorldPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(WorldPostfix)));
                    patched++;
                }

                MethodBase components = AccessTools.Method(typeof(WorldComponentUtility), "WorldComponentTick",
                    new Type[] { typeof(World) });
                if (components != null)
                {
                    harmony.Patch(components,
                        prefix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(ComponentPrefix)),
                        postfix: new HarmonyMethod(typeof(WorldTailBoundary093T22), nameof(ComponentPostfix)));
                    patched++;
                }

                installed = patched > 0;
            }
            catch
            {
                installFailures++;
                installed = false;
            }
        }

        public static void WorldPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void WorldPostfix(long __state)
        {
            Record(__state, ref worldCalls, ref worldTicks, ref worldMaxTicks,
                ref worldOver50, ref worldOver100, ref worldOver1000);
        }

        public static void ComponentPrefix(ref long __state)
        {
            __state = Stopwatch.GetTimestamp();
        }

        public static void ComponentPostfix(long __state)
        {
            Record(__state, ref componentCalls, ref componentTicks, ref componentMaxTicks,
                ref componentOver50, ref componentOver100, ref componentOver1000);
        }

        private static void Record(long started,
            ref long calls, ref long total, ref long max,
            ref long over50, ref long over100, ref long over1000)
        {
            if (started == 0L) return;
            long elapsed = Stopwatch.GetTimestamp() - started;
            if (elapsed < 0L) return;
            Interlocked.Increment(ref calls);
            Interlocked.Add(ref total, elapsed);
            UpdateMax(ref max, elapsed);
            long ms50 = Stopwatch.Frequency / 20;
            long ms100 = Stopwatch.Frequency / 10;
            long ms1000 = Stopwatch.Frequency;
            if (elapsed >= ms50) Interlocked.Increment(ref over50);
            if (elapsed >= ms100) Interlocked.Increment(ref over100);
            if (elapsed >= ms1000) Interlocked.Increment(ref over1000);
        }

        private static void UpdateMax(ref long field, long value)
        {
            long observed;
            while (value > (observed = Interlocked.Read(ref field)))
            {
                if (Interlocked.CompareExchange(ref field, value, observed) == observed)
                    break;
            }
        }

        internal static string Summary()
        {
            long wc = Interlocked.Read(ref worldCalls);
            long cc = Interlocked.Read(ref componentCalls);
            double wTotalMs = Interlocked.Read(ref worldTicks) * 1000.0 / Stopwatch.Frequency;
            double cTotalMs = Interlocked.Read(ref componentTicks) * 1000.0 / Stopwatch.Frequency;
            return "T22 world-tail direct boundary: installed=" + installed +
                ", patched=" + patched +
                ", World.WorldTick[calls=" + wc +
                ", avgUs=" + (wc == 0 ? 0.0 : wTotalMs * 1000.0 / wc).ToString("F1") +
                ", maxMs=" + (Interlocked.Read(ref worldMaxTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3") +
                ", >=50/100/1000ms=" + Interlocked.Read(ref worldOver50) + "/" +
                Interlocked.Read(ref worldOver100) + "/" + Interlocked.Read(ref worldOver1000) + "]" +
                ", WorldComponentUtility[calls=" + cc +
                ", avgUs=" + (cc == 0 ? 0.0 : cTotalMs * 1000.0 / cc).ToString("F1") +
                ", maxMs=" + (Interlocked.Read(ref componentMaxTicks) * 1000.0 / Stopwatch.Frequency).ToString("F3") +
                ", >=50/100/1000ms=" + Interlocked.Read(ref componentOver50) + "/" +
                Interlocked.Read(ref componentOver100) + "/" + Interlocked.Read(ref componentOver1000) + "]" +
                ", installFailures=" + installFailures +
                ". Direct Harmony timing closes the T1 WorldTick attribution hole; measurement-only.";
        }
    }
}
'@
Set-Content $worldPath $world -Encoding UTF8

$bootPath = 'RimMT/Source/RimMT/Bootstrap/RimMTBootstrap.cs'
$boot = Get-Content $bootPath -Raw
$boot = Replace-OrThrow $boot 'internal const string Version = "0.9.3-t21-foundation-reach-chain";' 'internal const string Version = "0.9.3-t22-foundation-genclosest-smf";' 'T22 bootstrap version'

$boot = Replace-OrThrow $boot @'
                QuestDeepAttribution093T19.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ @'
                QuestDeepAttribution093T19.Apply(harmony);
                JobSearchTransaction093T20.Apply(harmony);
                GenClosestTransactionIndex093T22.Apply(harmony);
                WorldTailBoundary093T22.Apply(harmony);
                DoBillTailFabric092.Apply(harmony);
'@ 'T22 foundation installs'

$oldDispatcher = @'
        private static void TryPatchDispatcher(Harmony harmony)
        {
            try
            {
                MethodBase update = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
                if (update == null)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "TickManagerUpdate was not found");
                    return;
                }

                CompatibilityGuard.RegisterTarget("runtime.dispatcher", update);
                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePrefix)) { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(TickManagerUpdatePostfix)) { priority = Priority.Last };
                harmony.Patch(update, prefix: prefix, postfix: postfix);
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "dispatcher patch failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] dispatcher patch failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void TickManagerUpdatePrefix(ref long __state)
        {
            __state = 0L;
            if (RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                __state = Stopwatch.GetTimestamp();
        }

        public static void TickManagerUpdatePostfix(long __state)
        {
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                AdaptiveLoadBalancer.RecordButterFrameSlice(__state);

            RimMTRuntime.OnMainThreadFrame();
        }
'@

$newDispatcher = @'
        internal static bool DispatcherBridgePatched { get; private set; }

        private static void TryPatchDispatcher(Harmony harmony)
        {
            try
            {
                // T22 deliberately leaves TickManager.TickManagerUpdate alone. Simply More FPS owns
                // a transpiler there; draining at Root_Play.Update is a rendered-frame/main-thread
                // boundary and avoids competing with frame-budget/tick-loop rewriting.
                MethodBase update = AccessTools.Method(typeof(Root_Play), "Update");
                if (update == null)
                {
                    FeatureGate.Suppress("runtime.dispatcher", "Root_Play.Update was not found");
                    return;
                }

                HarmonyMethod prefix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePrefix))
                    { priority = Priority.First };
                HarmonyMethod postfix = new HarmonyMethod(typeof(RimMTBootstrap), nameof(RootPlayUpdatePostfix))
                    { priority = Priority.Last };
                harmony.Patch(update, prefix: prefix, postfix: postfix);
                DispatcherBridgePatched = true;
            }
            catch (Exception ex)
            {
                FeatureGate.Suppress("runtime.dispatcher", "Root_Play dispatcher bridge failed: " + ex.GetType().Name);
                Log.Warning("[RimMT] Root_Play dispatcher bridge failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void RootPlayUpdatePrefix(ref long __state)
        {
            __state = 0L;
            if (RuntimeCompatibility.ButterPlusPlusActive && FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                __state = Stopwatch.GetTimestamp();
        }

        public static void RootPlayUpdatePostfix(long __state)
        {
            if (__state != 0L && RuntimeCompatibility.ButterPlusPlusActive &&
                FeatureGate.IsEnabled("runtime.adaptiveBurst"))
                AdaptiveLoadBalancer.RecordButterFrameSlice(__state);

            RimMTRuntime.OnMainThreadFrame();
        }
'@
$boot = Replace-OrThrow $boot $oldDispatcher $newDispatcher 'T22 Root_Play dispatcher bridge'

$boot = Replace-OrThrow $boot '[RimMT] V0.9.3-T21 Foundation II Reach Chain initialized. T20 generic validator transaction retained; package-local Reachability now memoizes only the pre-postfix base result while every foreign prefix/postfix remains live. T18/T19 diagnostics retained; SMF dispatcher policy unchanged.' '[RimMT] V0.9.3-T22 Foundation III initialized. T20/T21 transaction core retained; repeated package-local GenClosest_Global_NewTemp IList sources use a distance/source-order index; dispatcher drain moved from TickManagerUpdate to Root_Play.Update for SimplyMoreFPS coexistence; direct WorldTick boundary timing added.' 'T22 bootstrap log'
Set-Content $bootPath $boot -Encoding UTF8

$smfPath = 'RimMT/Source/RimMT/Diagnostics/SMFDispatcherCoexistence093T16.cs'
$smf = @'
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using Verse;

namespace RimMT
{
    internal static class SMFDispatcherCoexistence093T16
    {
        internal static string Summary()
        {
            MethodBase tickTarget = AccessTools.Method(typeof(TickManager), "TickManagerUpdate");
            MethodBase bridgeTarget = AccessTools.Method(typeof(Root_Play), "Update");
            Dictionary<string, FeatureGate.FeatureState> gates = FeatureGate.Snapshot();
            FeatureGate.FeatureState dispatcher;
            gates.TryGetValue("runtime.dispatcher", out dispatcher);

            StringBuilder sb = new StringBuilder(4096);
            sb.Append("T22 SMF dispatcher bridge audit: dispatcherEnabled=")
              .Append(FeatureGate.IsEnabled("runtime.dispatcher"))
              .Append(", bridgeTarget=Root_Play.Update")
              .Append(", bridgePatched=").Append(RimMTBootstrap.DispatcherBridgePatched);

            if (dispatcher != null)
                sb.Append(", suppressed=").Append(dispatcher.Suppressed)
                  .Append(", reason=").Append(string.IsNullOrEmpty(dispatcher.Reason) ? "<none>" : dispatcher.Reason);

            int tickForeign = 0, tickSmf = 0;
            int bridgeForeign = 0, bridgeSmf = 0, bridgeRimMT = 0;
            Append(tickTarget, sb, "TickManager", ref tickForeign, ref tickSmf, ref bridgeRimMT, false);
            Append(bridgeTarget, sb, "RootPlay", ref bridgeForeign, ref bridgeSmf, ref bridgeRimMT, true);

            sb.Append(", TickManager[foreign=").Append(tickForeign).Append(",smf=").Append(tickSmf).Append("]")
              .Append(", RootPlay[foreign=").Append(bridgeForeign).Append(",smf=").Append(bridgeSmf)
              .Append(",rimmt=").Append(bridgeRimMT).Append("]")
              .Append(". T22 does not register TickManagerUpdate as runtime.dispatcher authority; SMF frame-budget transpilers remain untouched.");
            return sb.ToString();
        }

        private static void Append(MethodBase target, StringBuilder sb, string label,
            ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
        {
            if (target == null)
            {
                sb.Append(" ").Append(label).Append("[missing]");
                return;
            }

            Patches info = Harmony.GetPatchInfo(target);
            if (info == null) return;
            AppendPatches(info.Prefixes, sb, label + ".Prefix", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Postfixes, sb, label + ".Postfix", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Transpilers, sb, label + ".Transpiler", ref foreign, ref smf, ref rimmt, countRimMT);
            AppendPatches(info.Finalizers, sb, label + ".Finalizer", ref foreign, ref smf, ref rimmt, countRimMT);
        }

        private static void AppendPatches(IEnumerable<Patch> patches, StringBuilder sb, string kind,
            ref int foreign, ref int smf, ref int rimmt, bool countRimMT)
        {
            if (patches == null) return;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                string owner = patch.owner ?? "<null>";
                MethodInfo method = patch.PatchMethod;
                string methodName = method == null || method.DeclaringType == null
                    ? "<null>" : method.DeclaringType.FullName + "." + method.Name;
                bool ours = string.Equals(owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal);
                if (ours)
                {
                    if (countRimMT) rimmt++;
                    continue;
                }

                foreign++;
                bool isSmf = owner.IndexOf("simplymorefps", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             owner.IndexOf("game-frame-budget", StringComparison.OrdinalIgnoreCase) >= 0 ||
                             methodName.IndexOf("SimplyMoreFPS", StringComparison.OrdinalIgnoreCase) >= 0;
                if (isSmf) smf++;
                sb.Append(" ").Append(kind).Append("[owner=").Append(owner)
                  .Append(",method=").Append(methodName)
                  .Append(isSmf ? ",SMF]" : "]");
            }
        }
    }
}
'@
Set-Content $smfPath $smf -Encoding UTF8

$reportPath = 'RimMT/Source/RimMT/Diagnostics/RimMTDiagnostics.cs'
$report = Get-Content $reportPath -Raw
$report = $report.Replace('V0.9.3-T21 Foundation II Reach Chain', 'V0.9.3-T22 Foundation III GenClosest + SMF Bridge')
$report = Replace-OrThrow $report @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ @'
            sb.AppendLine(JobSearchTransaction093T20.Summary());
            sb.AppendLine(GenClosestTransactionIndex093T22.Summary());
            sb.AppendLine(WorldTailBoundary093T22.Summary());
            sb.AppendLine(SMFDispatcherCoexistence093T16.Summary());
'@ 'T22 report foundation summaries'
$report = $report.Replace('SMF dispatcher coexistence remains census-only;', 'SMF dispatcher drain is bridged at Root_Play.Update and no longer claims TickManagerUpdate;')
$report = $report.Replace('T21 makes package-local CanReach chain-aware by replaying only the pre-postfix base result after all live prefixes, while all foreign postfixes still execute live and remain final authority;', 'T21 package-local CanReach chain-aware replay retained; T22 adds repeated-IList package-local GenClosest_Global_NewTemp distance/source-order indexing with live validator authority, direct WorldTick boundary timing, and Root_Play.Update dispatcher drain for SimplyMoreFPS coexistence;')
Set-Content $reportPath $report -Encoding UTF8

$aboutPath = 'RimMT/About/About.xml'
if (Test-Path $aboutPath)
{
    $about = Get-Content $aboutPath -Raw
    $about = $about.Replace('V0.9.3-T21 Foundation II Reach Chain', 'V0.9.3-T22 Foundation III GenClosest + SMF Bridge')
    Set-Content $aboutPath $about -Encoding UTF8
}

Write-Host 'Applied RimMT V0.9.3-T22 Foundation III: generic repeated-IList GenClosest transaction index, Root_Play dispatcher bridge for SimplyMoreFPS coexistence, and direct WorldTick boundary timing.'
