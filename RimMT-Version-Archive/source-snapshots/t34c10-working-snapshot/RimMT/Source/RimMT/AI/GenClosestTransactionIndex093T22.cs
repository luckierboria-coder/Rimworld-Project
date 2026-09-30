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

