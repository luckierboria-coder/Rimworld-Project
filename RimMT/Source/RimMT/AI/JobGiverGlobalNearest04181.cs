using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Unified JobGiver global-nearest layer.
    /// Combines the validated V0.4.18.1 nearest-first transformation with the useful part of JS2:
    /// exact source+root search plans that live only for one synchronous TryIssueJobPackage call.
    /// Every plan reuse revalidates source membership, spawn state and position. No state survives
    /// the package and no final validator/Reachability decision is cached.
    /// </summary>
    internal static class JobGiverGlobalNearest04181
    {
        private const int MinSourceCount = 64;
        private const int MaxSourceCount = 16384;
        private const int MaxPlansPerPackage = 64;
        private const int MaxPrefixesPerPlan = 16;

        [ThreadStatic] private static int jobGiverDepth;
        [ThreadStatic] private static long jobGiverStartTicks;
        [ThreadStatic] private static PackageContext current;

        internal static bool InJobGiverScope { get { return jobGiverDepth > 0; } }
        internal static long CurrentScopeStartTicks { get { return jobGiverDepth > 0 ? jobGiverStartTicks : 0L; } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodBase jobGiver = AccessTools.Method(typeof(JobGiver_Work), "TryIssueJobPackage");
                if (jobGiver == null) return;

                harmony.Patch(jobGiver,
                    prefix: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(JobGiverFinalizer)) { priority = Priority.Last });

                bool global = false;
                bool reachable = false;
                MethodInfo[] methods = typeof(GenClosest).GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null) continue;
                    ParameterInfo[] p = method.GetParameters();
                    if (method.Name == "ClosestThing_Global" && p.Length == 5 && p[0].ParameterType == typeof(IntVec3))
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(GlobalPrefix)) { priority = Priority.First + 75 });
                        global = true;
                    }
                    else if (method.Name == "ClosestThing_Global_Reachable" && p.Length == 8 && p[0].ParameterType == typeof(IntVec3))
                    {
                        harmony.Patch(method, prefix: new HarmonyMethod(typeof(JobGiverGlobalNearest04181), nameof(GlobalReachablePrefix)) { priority = Priority.First + 75 });
                        reachable = true;
                    }
                }

                Log.Message("[RimMT] Unified nearest-first + JS2 package-local search-plan reuse active: global=" + global + ", reachable=" + reachable + ".");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] Unified nearest-first install failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void JobGiverPrefix(Pawn __0)
        {
            if (jobGiverDepth == 0)
            {
                jobGiverStartTicks = Stopwatch.GetTimestamp();
                current = new PackageContext(__0);
            }
            jobGiverDepth++;
        }

        public static Exception JobGiverFinalizer(Exception __exception)
        {
            if (jobGiverDepth > 0) jobGiverDepth--;
            if (jobGiverDepth == 0)
            {
                jobGiverStartTicks = 0L;
                current = null;
            }
            return __exception;
        }

        public static void GlobalPrefix(object[] __args)
        {
            if (__args != null && __args.Length >= 5)
                TryReorder(__args, 0, 1, 2, 4);
        }

        public static void GlobalReachablePrefix(object[] __args)
        {
            if (__args != null && __args.Length >= 8)
                TryReorder(__args, 0, 2, 5, 7);
        }

        private static void TryReorder(object[] args, int centerIndex, int setIndex, int maxDistanceIndex, int priorityIndex)
        {
            if (!InJobGiverScope || !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing) return;
            if (args[priorityIndex] != null) return;

            IList source = args[setIndex] as IList;
            if (source == null) return;

            int count;
            try { count = source.Count; }
            catch { return; }
            if (count < MinSourceCount || count > MaxSourceCount) return;

            IntVec3 center;
            float maxDistance;
            try
            {
                center = (IntVec3)args[centerIndex];
                maxDistance = Convert.ToSingle(args[maxDistanceIndex]);
            }
            catch { return; }
            if (float.IsNaN(maxDistance) || maxDistance < 0f) return;

            PackageContext context = current;
            if (context == null) return;

            PlanKey key = new PlanKey(source, center.x, center.z);
            SearchPlan plan;
            if (context.Plans.TryGetValue(key, out plan))
            {
                if (!ValidatePlan(source, plan))
                {
                    context.Plans.Remove(key);
                    plan = null;
                }
            }

            if (plan == null)
            {
                if (context.Plans.Count >= MaxPlansPerPackage) return;
                plan = BuildPlan(source, center, count);
                if (plan == null) return;
                context.Plans[key] = plan;
            }

            Thing[] prefix;
            if (!plan.Prefixes.TryGetValue(maxDistance, out prefix))
            {
                prefix = BuildPrefix(plan, maxDistance);
                if (plan.Prefixes.Count < MaxPrefixesPerPlan)
                    plan.Prefixes[maxDistance] = prefix;
            }

            args[setIndex] = prefix;
        }

        private static SearchPlan BuildPlan(IList source, IntVec3 center, int count)
        {
            try
            {
                Thing[] members = new Thing[count];
                bool[] spawned = new bool[count];
                IntVec3[] positions = new IntVec3[count];
                Candidate[] candidates = new Candidate[count];
                int kept = 0;

                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i] as Thing;
                    if (thing == null) return null;
                    members[i] = thing;
                    spawned[i] = thing.Spawned;
                    positions[i] = thing.Position;
                    if (!spawned[i] || !positions[i].IsValid) continue;

                    long dx = (long)positions[i].x - center.x;
                    long dz = (long)positions[i].z - center.z;
                    candidates[kept++] = new Candidate(thing, dx * dx + dz * dz, i);
                }

                if (kept > 1) Array.Sort(candidates, 0, kept, CandidateComparer.Instance);
                Candidate[] ordered = new Candidate[kept];
                Array.Copy(candidates, ordered, kept);
                return new SearchPlan(members, spawned, positions, ordered);
            }
            catch { return null; }
        }

        private static bool ValidatePlan(IList source, SearchPlan plan)
        {
            if (source == null || plan == null) return false;
            int count;
            try { count = source.Count; }
            catch { return false; }
            if (count != plan.Members.Length) return false;

            for (int i = 0; i < count; i++)
            {
                Thing thing = source[i] as Thing;
                if (!ReferenceEquals(thing, plan.Members[i]) || thing == null) return false;
                bool spawned = thing.Spawned;
                if (spawned != plan.Spawned[i]) return false;
                if (spawned && thing.Position != plan.Positions[i]) return false;
            }
            return true;
        }

        private static Thing[] BuildPrefix(SearchPlan plan, float maxDistance)
        {
            double maxSq = (double)maxDistance * maxDistance;
            Candidate[] ordered = plan.Ordered;
            int count = 0;
            while (count < ordered.Length && ordered[count].DistanceSquared <= maxSq) count++;
            Thing[] result = new Thing[count];
            for (int i = 0; i < count; i++) result[i] = ordered[i].Thing;
            return result;
        }

        private sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly Dictionary<PlanKey, SearchPlan> Plans = new Dictionary<PlanKey, SearchPlan>();
            internal PackageContext(Pawn pawn) { Pawn = pawn; }
        }

        private sealed class SearchPlan
        {
            internal readonly Thing[] Members;
            internal readonly bool[] Spawned;
            internal readonly IntVec3[] Positions;
            internal readonly Candidate[] Ordered;
            internal readonly Dictionary<float, Thing[]> Prefixes = new Dictionary<float, Thing[]>();
            internal SearchPlan(Thing[] members, bool[] spawned, IntVec3[] positions, Candidate[] ordered)
            {
                Members = members;
                Spawned = spawned;
                Positions = positions;
                Ordered = ordered;
            }
        }

        private struct PlanKey : IEquatable<PlanKey>
        {
            internal readonly object Source;
            internal readonly int X;
            internal readonly int Z;
            internal PlanKey(object source, int x, int z) { Source = source; X = x; Z = z; }
            public bool Equals(PlanKey other) { return ReferenceEquals(Source, other.Source) && X == other.X && Z == other.Z; }
            public override bool Equals(object obj) { return obj is PlanKey && Equals((PlanKey)obj); }
            public override int GetHashCode()
            {
                unchecked { return (RuntimeHelpers.GetHashCode(Source) * 397 ^ X) * 397 ^ Z; }
            }
        }

        private struct Candidate
        {
            internal readonly Thing Thing;
            internal readonly long DistanceSquared;
            internal readonly int SourceIndex;
            internal Candidate(Thing thing, long distanceSquared, int sourceIndex)
            {
                Thing = thing; DistanceSquared = distanceSquared; SourceIndex = sourceIndex;
            }
        }

        private sealed class CandidateComparer : IComparer<Candidate>
        {
            internal static readonly CandidateComparer Instance = new CandidateComparer();
            public int Compare(Candidate a, Candidate b)
            {
                int d = a.DistanceSquared.CompareTo(b.DistanceSquared);
                return d != 0 ? d : a.SourceIndex.CompareTo(b.SourceIndex);
            }
        }
    }
}
