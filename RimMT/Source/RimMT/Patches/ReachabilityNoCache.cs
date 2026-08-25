using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class ReachabilityNoCache
    {
        private const int MaxEntries = 8192;
        private static readonly object Sync = new object();
        private static readonly Dictionary<ReachKey,int> NoUntilTick = new Dictionary<ReachKey,int>();
        private static long hits; private static long stores; private static int topologyGeneration;
        internal static long Hits { get { lock (Sync) return hits; } }
        internal static long Stores { get { lock (Sync) return stores; } }
        internal static int TopologyGeneration { get { return Volatile.Read(ref topologyGeneration); } }
        internal static void InvalidateTopology() { Interlocked.Increment(ref topologyGeneration); }

        public static bool Prefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, ref bool __result, ref bool __state)
        {
            __state = false;
            if (!FeatureGate.IsEnabled("ai.reachNoCache") || Find.TickManager == null || !dest.IsValid || dest.HasThing) return true;
            ReachKey key = new ReachKey(__instance,start,dest.Cell,peMode,traverseParams,TopologyGeneration); int now = Find.TickManager.TicksGame;
            lock (Sync)
            {
                int until;
                if (NoUntilTick.TryGetValue(key,out until)) { if (now <= until) { hits++; __result = false; __state = true; return false; } NoUntilTick.Remove(key); }
            }
            return true;
        }
        public static void Postfix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, bool __result, bool __state)
        {
            if (__state || __result || !FeatureGate.IsEnabled("ai.reachNoCache") || Find.TickManager == null || !dest.IsValid || dest.HasThing) return;
            int ttl = RimMTMod.Settings == null ? 20 : RimMTMod.Settings.ReachNoCacheTtl;
            ReachKey key = new ReachKey(__instance,start,dest.Cell,peMode,traverseParams,TopologyGeneration);
            lock (Sync) { if (NoUntilTick.Count >= MaxEntries) NoUntilTick.Clear(); NoUntilTick[key] = Find.TickManager.TicksGame + ttl; stores++; }
        }
        private struct ReachKey : IEquatable<ReachKey>
        {
            private readonly int reachabilityId; private readonly IntVec3 start; private readonly IntVec3 dest; private readonly PathEndMode mode; private readonly int parmsHash; private readonly int generation;
            internal ReachKey(Reachability reachability, IntVec3 start, IntVec3 dest, PathEndMode mode, TraverseParms parms, int generation) { reachabilityId = RuntimeHelpers.GetHashCode(reachability); this.start=start; this.dest=dest; this.mode=mode; parmsHash=parms.GetHashCode(); this.generation=generation; }
            public bool Equals(ReachKey other) { return reachabilityId==other.reachabilityId && start==other.start && dest==other.dest && mode==other.mode && parmsHash==other.parmsHash && generation==other.generation; }
            public override bool Equals(object obj) { return obj is ReachKey && Equals((ReachKey)obj); }
            public override int GetHashCode() { unchecked { int hash=reachabilityId; hash=hash*397^start.GetHashCode(); hash=hash*397^dest.GetHashCode(); hash=hash*397^(int)mode; hash=hash*397^parmsHash; hash=hash*397^generation; return hash; } }
        }
    }
}
