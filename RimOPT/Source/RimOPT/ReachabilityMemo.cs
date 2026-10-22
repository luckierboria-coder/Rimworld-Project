using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimOPT
{
    /// <summary>
    /// Reuses an exact Reachability.CanReach result only inside one synchronous
    /// JobGiver_Work package. Nothing survives the package, pawn, or tick.
    /// </summary>
    internal static class ReachabilityMemo
    {
        private const int Capacity = 8192;
        private const int WarmupMatches = 8;
        private const int VerifyMask = 63;

        private static bool installed;
        private static volatile bool authoritativeSafe;
        private static volatile bool finalChainChecked;
        private static MethodBase patchedMethod;
        private static ChainAudit installedChain;
        private static string chainStatus = "not inspected";
        private static long observed;
        private static long stores;
        private static long candidates;
        private static long hits;
        private static long verifies;
        private static long mismatches;
        private static long invalidations;
        private static long capacityBypass;

        internal static string Status
        {
            get
            {
                return "reachMemo=" + installed + "/authoritative=" + authoritativeSafe +
                    "/chain=" + chainStatus;
            }
        }

        internal static void Install(Harmony harmony)
        {
            MethodBase target = AccessTools.Method(typeof(Reachability), nameof(Reachability.CanReach),
                new Type[] { typeof(IntVec3), typeof(LocalTargetInfo), typeof(PathEndMode), typeof(TraverseParms) });
            if (target == null) throw new MissingMethodException("Reachability.CanReach");

            ChainAudit chain = InspectChain(target);
            patchedMethod = target;
            installedChain = chain;
            int prefixPriority = chain.Unknown || chain.MinPrefix == int.MinValue
                ? -1000000 : chain.MinPrefix - 1;
            int postfixPriority = chain.Unknown || chain.MaxPostfix == int.MaxValue
                ? 1000000 : chain.MaxPostfix + 1;

            authoritativeSafe = !chain.Unknown && chain.PriorityRoom &&
                chain.Transpilers == 0 && chain.Finalizers == 0 && chain.RunOriginalPostfixes == 0;
            chainStatus = "pre=" + chain.Prefixes + ",post=" + chain.Postfixes +
                ",resultPost=" + chain.ResultPostfixes + ",trans=" + chain.Transpilers +
                ",final=" + chain.Finalizers + ",runOriginal=" + chain.RunOriginalPostfixes;

            harmony.Patch(target,
                prefix: new HarmonyMethod(typeof(ReachabilityMemo), nameof(Prefix))
                { priority = prefixPriority, after = chain.PrefixOwners },
                postfix: new HarmonyMethod(typeof(ReachabilityMemo), nameof(BasePostfix))
                { priority = postfixPriority, before = chain.PostfixOwners },
                finalizer: new HarmonyMethod(typeof(ReachabilityMemo), nameof(Finalizer))
                { priority = Priority.Last - 300 });

            HarmonyMethod invalidate = new HarmonyMethod(typeof(ReachabilityMemo), nameof(Invalidate))
            { priority = Priority.Last };
            MethodBase clear = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCache));
            MethodBase clearPawn = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCacheFor),
                new Type[] { typeof(Pawn) });
            MethodBase clearHostile = AccessTools.Method(typeof(Reachability), nameof(Reachability.ClearCacheForHostile),
                new Type[] { typeof(Thing) });
            if (clear != null) harmony.Patch(clear, postfix: invalidate);
            if (clearPawn != null) harmony.Patch(clearPawn, postfix: invalidate);
            if (clearHostile != null) harmony.Patch(clearHostile, postfix: invalidate);
            installed = true;
        }

        public static bool Prefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest,
            PathEndMode peMode, TraverseParms traverseParams, bool __runOriginal,
            ref bool __result, ref CallState __state)
        {
            __state = default(CallState);
            PackageScope.Context context = PackageScope.CurrentContext;
            if (context == null) return true;
            EnsureFinalChain();
            Interlocked.Increment(ref observed);

            Pawn pawn = context.Pawn;
            if (!__runOriginal || __instance == null || pawn == null || traverseParams.pawn == null ||
                !ReferenceEquals(traverseParams.pawn, pawn) || pawn.Map == null ||
                !start.IsValid || !dest.IsValid || start != pawn.Position)
                return true;

            ContextData data = ContextData.For(context);
            ReachKey key = new ReachKey(__instance, start, dest, peMode, traverseParams);
            if (data.Quarantined.Contains(key)) return true;

            ReachEntry entry;
            if (!data.Memo.TryGetValue(key, out entry))
            {
                __state = CallState.Store(data, key, dest);
                return true;
            }
            if (!entry.Target.Matches(dest))
            {
                data.Memo.Remove(key);
                __state = CallState.Store(data, key, dest);
                return true;
            }

            Interlocked.Increment(ref candidates);
            if (!authoritativeSafe)
            {
                __state = CallState.Verify(data, key, entry.Result, dest);
                Interlocked.Increment(ref verifies);
                return true;
            }

            data.HitSerial++;
            if (data.ValidatedMatches < WarmupMatches || (data.HitSerial & VerifyMask) == 0)
            {
                __state = CallState.Verify(data, key, entry.Result, dest);
                Interlocked.Increment(ref verifies);
                return true;
            }

            __result = entry.Result;
            __state = CallState.Hit(data, key);
            Interlocked.Increment(ref hits);
            return false;
        }

        public static void BasePostfix(bool __result, ref CallState __state)
        {
            if (__state.AuthoritativeHit || __state.Data == null) return;

            if (__state.VerifyOnly)
            {
                if (__result == __state.Cached)
                {
                    __state.Data.ValidatedMatches++;
                }
                else
                {
                    __state.Data.Memo.Remove(__state.Key);
                    __state.Data.Quarantined.Add(__state.Key);
                    __state.Data.ValidatedMatches = 0;
                    Interlocked.Increment(ref mismatches);
                }
                return;
            }

            if (!__state.StoreResult) return;
            if (__state.Data.Memo.Count >= Capacity)
            {
                Interlocked.Increment(ref capacityBypass);
                return;
            }
            if (!__state.Data.Memo.ContainsKey(__state.Key))
            {
                __state.Data.Memo.Add(__state.Key,
                    new ReachEntry(__result, TargetFingerprint.Capture(__state.Dest)));
                Interlocked.Increment(ref stores);
            }
        }

        public static Exception Finalizer(Exception __exception, CallState __state)
        {
            if (__exception != null && __state.Data != null)
                __state.Data.Memo.Remove(__state.Key);
            return __exception;
        }

        public static void Invalidate()
        {
            PackageScope.Context context = PackageScope.CurrentContext;
            if (context == null) return;
            ContextData data;
            if (!contexts.TryGetValue(context, out data)) return;
            data.Memo.Clear();
            data.Quarantined.Clear();
            data.ValidatedMatches = 0;
            Interlocked.Increment(ref invalidations);
        }

        internal static string Metrics()
        {
            return "reach[observed=" + Interlocked.Read(ref observed) +
                ", stores=" + Interlocked.Read(ref stores) +
                ", candidates=" + Interlocked.Read(ref candidates) +
                ", hits=" + Interlocked.Read(ref hits) +
                ", verify=" + Interlocked.Read(ref verifies) +
                ", mismatch=" + Interlocked.Read(ref mismatches) +
                ", invalidations=" + Interlocked.Read(ref invalidations) +
                ", capBypass=" + Interlocked.Read(ref capacityBypass) + "]";
        }

        private static void EnsureFinalChain()
        {
            if (finalChainChecked) return;
            ChainAudit final = InspectChain(patchedMethod);
            if (installedChain == null || !installedChain.SameShape(final))
            {
                authoritativeSafe = false;
                chainStatus += ",latePatchChange=disabled";
            }
            finalChainChecked = true;
        }

        [ThreadStatic] private static Dictionary<PackageScope.Context, ContextData> contexts;

        internal sealed class ContextData
        {
            internal readonly Dictionary<ReachKey, ReachEntry> Memo = new Dictionary<ReachKey, ReachEntry>();
            internal readonly HashSet<ReachKey> Quarantined = new HashSet<ReachKey>();
            internal long HitSerial;
            internal int ValidatedMatches;

            internal static ContextData For(PackageScope.Context context)
            {
                if (contexts == null) contexts = new Dictionary<PackageScope.Context, ContextData>();
                ContextData data;
                if (!contexts.TryGetValue(context, out data))
                {
                    // Package contexts are short-lived. Keep only the active one on this thread.
                    contexts.Clear();
                    data = new ContextData();
                    contexts.Add(context, data);
                }
                return data;
            }
        }

        internal struct CallState
        {
            internal ContextData Data;
            internal ReachKey Key;
            internal LocalTargetInfo Dest;
            internal bool Cached;
            internal bool StoreResult;
            internal bool VerifyOnly;
            internal bool AuthoritativeHit;

            internal static CallState Store(ContextData data, ReachKey key, LocalTargetInfo dest)
            {
                return new CallState { Data = data, Key = key, Dest = dest, StoreResult = true };
            }
            internal static CallState Verify(ContextData data, ReachKey key, bool cached, LocalTargetInfo dest)
            {
                return new CallState { Data = data, Key = key, Cached = cached, Dest = dest, VerifyOnly = true };
            }
            internal static CallState Hit(ContextData data, ReachKey key)
            {
                return new CallState { Data = data, Key = key, AuthoritativeHit = true };
            }
        }

        internal struct ReachEntry
        {
            internal readonly bool Result;
            internal readonly TargetFingerprint Target;
            internal ReachEntry(bool result, TargetFingerprint target) { Result = result; Target = target; }
        }

        internal struct ReachKey : IEquatable<ReachKey>
        {
            private readonly Reachability reachability;
            private readonly IntVec3 start;
            private readonly bool hasThing;
            private readonly Thing thing;
            private readonly IntVec3 cell;
            private readonly PathEndMode endMode;
            private readonly TraverseParms traverse;

            internal ReachKey(Reachability reachability, IntVec3 start, LocalTargetInfo dest,
                PathEndMode endMode, TraverseParms traverse)
            {
                this.reachability = reachability;
                this.start = start;
                hasThing = dest.HasThing;
                thing = hasThing ? dest.Thing : null;
                cell = dest.Cell;
                this.endMode = endMode;
                this.traverse = traverse;
            }
            public bool Equals(ReachKey other)
            {
                return ReferenceEquals(reachability, other.reachability) && start == other.start &&
                    hasThing == other.hasThing && ReferenceEquals(thing, other.thing) && cell == other.cell &&
                    endMode == other.endMode && traverse.Equals(other.traverse);
            }
            public override bool Equals(object obj) { return obj is ReachKey && Equals((ReachKey)obj); }
            public override int GetHashCode()
            {
                unchecked
                {
                    int hash = RuntimeHelpers.GetHashCode(reachability);
                    hash = hash * 397 ^ start.GetHashCode();
                    hash = hash * 397 ^ (hasThing ? RuntimeHelpers.GetHashCode(thing) : cell.GetHashCode());
                    hash = hash * 397 ^ (int)endMode;
                    return hash * 397 ^ traverse.GetHashCode();
                }
            }
        }

        internal struct TargetFingerprint
        {
            private readonly bool hasThing;
            private readonly Thing thing;
            private readonly Map map;
            private readonly IntVec3 position;
            private readonly bool spawned;
            private readonly IntVec3 cell;

            private TargetFingerprint(bool hasThing, Thing thing, Map map, IntVec3 position,
                bool spawned, IntVec3 cell)
            {
                this.hasThing = hasThing; this.thing = thing; this.map = map;
                this.position = position; this.spawned = spawned; this.cell = cell;
            }
            internal static TargetFingerprint Capture(LocalTargetInfo target)
            {
                if (!target.HasThing)
                    return new TargetFingerprint(false, null, null, IntVec3.Invalid, false, target.Cell);
                Thing targetThing = target.Thing;
                return new TargetFingerprint(true, targetThing, targetThing == null ? null : targetThing.MapHeld,
                    targetThing == null ? IntVec3.Invalid : targetThing.PositionHeld,
                    targetThing != null && targetThing.Spawned, target.Cell);
            }
            internal bool Matches(LocalTargetInfo target)
            {
                if (hasThing != target.HasThing || cell != target.Cell) return false;
                if (!hasThing) return true;
                Thing targetThing = target.Thing;
                return ReferenceEquals(thing, targetThing) && targetThing != null &&
                    ReferenceEquals(map, targetThing.MapHeld) && position == targetThing.PositionHeld &&
                    spawned == targetThing.Spawned;
            }
        }

        private static ChainAudit InspectChain(MethodBase method)
        {
            ChainAudit audit = new ChainAudit();
            try
            {
                Patches info = Harmony.GetPatchInfo(method);
                if (info == null) return audit;
                HashSet<string> prefixOwners = new HashSet<string>();
                HashSet<string> postfixOwners = new HashSet<string>();
                foreach (Patch patch in info.Prefixes)
                {
                    if (patch == null || patch.owner == Bootstrap.HarmonyId) continue;
                    audit.Prefixes++;
                    if (patch.priority < audit.MinPrefix) audit.MinPrefix = patch.priority;
                    if (!string.IsNullOrEmpty(patch.owner)) prefixOwners.Add(patch.owner);
                }
                foreach (Patch patch in info.Postfixes)
                {
                    if (patch == null || patch.owner == Bootstrap.HarmonyId) continue;
                    audit.Postfixes++;
                    if (patch.priority > audit.MaxPostfix) audit.MaxPostfix = patch.priority;
                    if (!string.IsNullOrEmpty(patch.owner)) postfixOwners.Add(patch.owner);
                    if (MutatesResult(patch)) audit.ResultPostfixes++;
                    if (ReadsRunOriginal(patch)) audit.RunOriginalPostfixes++;
                }
                foreach (Patch patch in info.Transpilers)
                    if (patch != null && patch.owner != Bootstrap.HarmonyId) audit.Transpilers++;
                foreach (Patch patch in info.Finalizers)
                    if (patch != null && patch.owner != Bootstrap.HarmonyId) audit.Finalizers++;
                audit.PrefixOwners = new string[prefixOwners.Count];
                prefixOwners.CopyTo(audit.PrefixOwners);
                audit.PostfixOwners = new string[postfixOwners.Count];
                postfixOwners.CopyTo(audit.PostfixOwners);
                audit.PriorityRoom = audit.MinPrefix > int.MinValue && audit.MaxPostfix < int.MaxValue;
            }
            catch
            {
                audit.Unknown = true;
                audit.PriorityRoom = false;
            }
            return audit;
        }

        private static bool MutatesResult(Patch patch)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            if (method == null || method.ReturnType == typeof(bool)) return true;
            foreach (ParameterInfo parameter in method.GetParameters())
                if (parameter.Name == "__result" && parameter.ParameterType.IsByRef &&
                    parameter.ParameterType.GetElementType() == typeof(bool)) return true;
            return false;
        }

        private static bool ReadsRunOriginal(Patch patch)
        {
            MethodInfo method = patch == null ? null : patch.PatchMethod;
            if (method == null) return true;
            foreach (ParameterInfo parameter in method.GetParameters())
                if (parameter.Name == "__runOriginal") return true;
            return false;
        }

        private sealed class ChainAudit
        {
            internal int Prefixes;
            internal int Postfixes;
            internal int ResultPostfixes;
            internal int RunOriginalPostfixes;
            internal int Transpilers;
            internal int Finalizers;
            internal int MinPrefix = int.MaxValue;
            internal int MaxPostfix = int.MinValue;
            internal string[] PrefixOwners = new string[0];
            internal string[] PostfixOwners = new string[0];
            internal bool PriorityRoom = true;
            internal bool Unknown;

            internal bool SameShape(ChainAudit other)
            {
                return other != null && Unknown == other.Unknown && Prefixes == other.Prefixes &&
                    Postfixes == other.Postfixes && ResultPostfixes == other.ResultPostfixes &&
                    RunOriginalPostfixes == other.RunOriginalPostfixes &&
                    Transpilers == other.Transpilers && Finalizers == other.Finalizers &&
                    MinPrefix == other.MinPrefix && MaxPostfix == other.MaxPostfix;
            }
        }
    }
}
