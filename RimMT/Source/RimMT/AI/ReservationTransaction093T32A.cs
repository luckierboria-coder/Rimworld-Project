using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// T32-A package-local false-only ReservationManager.CanReserve transaction memo.
    ///
    /// Scope:
    /// - only the current outer JobGiver_Work.TryIssueJobPackage owned by T28;
    /// - only the package pawn;
    /// - exact manager + pawn + target + reservation-parameter shape;
    /// - false results only;
    /// - target fingerprint must remain stable;
    /// - any ReservationManager mutation invalidates the entire package memo.
    ///
    /// Foreign prefixes/postfixes on CanReserve remain in-chain. This prefix runs late and
    /// refuses replay when an earlier prefix already skipped the original. Final result
    /// observation happens in a finalizer so foreign postfix result changes are included.
    /// Foreign transpilers/finalizers are treated as authority-unsafe and force live fallback.
    ///
    /// No reservation is created/released by this module. No Job, priority, reachability or
    /// cross-package result is cached. T32-A deliberately starts false-only; T31 showed most
    /// CanReserve results were true, so runtime counters will decide whether a later dual-result
    /// transaction is worth the larger proof surface.
    /// </summary>
    internal static class ReservationTransaction093T32A
    {
        internal const string FeatureId = "ai.reservationTransaction";

        private const int Capacity = 8192;
        private const int WarmupMatches = 16;
        private const int VerifyMask = 63; // 1/64 after warmup.

        [ThreadStatic] private static PackageContext current;

        private static bool installed;
        private static bool canReservePatched;
        private static bool chainAuthoritativeSafe;
        private static bool runtimeQuarantined;
        private static int installFailures;
        private static MethodBase canReserveTarget;
        private static long chainAudits;
        private static long lateUnsafeTransitions;

        private static int foreignPrefixes;
        private static int foreignPostfixes;
        private static int foreignTranspilers;
        private static int foreignFinalizers;
        private static bool chainAuditUnknown;

        private static int mutationMethodsPatched;
        private static int mutationMethodsMissing;

        private static long packages;
        private static long observed;
        private static long inScope;
        private static long stores;
        private static long memoCandidates;
        private static long authoritativeHits;
        private static long verifyRuns;
        private static long verifyMatches;
        private static long mismatches;
        private static long quarantines;
        private static long positiveLive;
        private static long negativeLive;
        private static long fingerprintBypass;
        private static long mutationInvalidations;
        private static long capacityBypass;
        private static long runOriginalBypass;
        private static long pawnBypass;
        private static long invalidTargetBypass;
        private static long authorityBypass;
        private static long exceptions;

        internal static bool Installed { get { return installed; } }

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;

            try
            {
                MethodBase target = AccessTools.Method(
                    typeof(ReservationManager),
                    nameof(ReservationManager.CanReserve),
                    new Type[]
                    {
                        typeof(Pawn),
                        typeof(LocalTargetInfo),
                        typeof(int),
                        typeof(int),
                        typeof(ReservationLayerDef),
                        typeof(bool)
                    });

                if (target == null)
                {
                    installFailures++;
                    Log.Warning("[RimMT] T32-A Reservation transaction unavailable: exact ReservationManager.CanReserve signature not found.");
                    return;
                }

                canReserveTarget = target;
                AuditChain(target);
                chainAuthoritativeSafe = !chainAuditUnknown &&
                    foreignTranspilers == 0 &&
                    foreignFinalizers == 0;

                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(ReservationTransaction093T32A), nameof(CanReservePrefix))
                    { priority = Priority.Last - 400 },
                    finalizer: new HarmonyMethod(typeof(ReservationTransaction093T32A), nameof(CanReserveFinalizer))
                    { priority = Priority.Last - 400 });

                canReservePatched = true;
                PatchMutationMethods(harmony);
                installed = canReservePatched && mutationMethodsPatched > 0 && mutationMethodsMissing == 0;

                Log.Message("[RimMT] T32-A Reservation transaction installed=" + installed +
                    ", canReserve=" + canReservePatched +
                    ", authoritySafe=" + chainAuthoritativeSafe +
                    ", chain[audits=" + Interlocked.Read(ref chainAudits) +
                ", lateUnsafeTransitions=" + Interlocked.Read(ref lateUnsafeTransitions) +
                ", foreignPrefixes=" + foreignPrefixes +
                    ", foreignPostfixes=" + foreignPostfixes +
                    ", foreignTranspilers=" + foreignTranspilers +
                    ", foreignFinalizers=" + foreignFinalizers +
                    ", auditUnknown=" + chainAuditUnknown + "]" +
                    ", mutationMethods=" + mutationMethodsPatched +
                    ", mutationMissing=" + mutationMethodsMissing +
                    ". False-only; one synchronous T28 package; mutation invalidation; Vanilla/live parity remains authority.");
            }
            catch (Exception ex)
            {
                installFailures++;
                installed = false;
                Log.Warning("[RimMT] T32-A Reservation transaction failed closed: " +
                    ex.GetType().Name + ": " + ex.Message);
            }
        }

        internal static void BeginPackage(Pawn pawn, long generation)
        {
            current = null;
            if (!installed || pawn == null) return;

            long packageNumber = Interlocked.Increment(ref packages);
            if (packageNumber == 1L || (packageNumber & 1023L) == 0L)
                ReauditChain();

            current = new PackageContext(pawn, generation);
        }

        internal static void EndPackage()
        {
            current = null;
        }

        public static bool CanReservePrefix(
            ReservationManager __instance,
            Pawn __0,
            LocalTargetInfo __1,
            int __2,
            int __3,
            ReservationLayerDef __4,
            bool __5,
            bool __runOriginal,
            ref bool __result,
            ref CallState __state)
        {
            __state = default(CallState);
            Interlocked.Increment(ref observed);

            PackageContext context = current;
            if (context == null || !JobSearchPackageContext093T28.InScope)
                return true;

            Interlocked.Increment(ref inScope);

            if (!__runOriginal)
            {
                Interlocked.Increment(ref runOriginalBypass);
                return true;
            }

            Pawn claimant = __0;
            LocalTargetInfo target = __1;
            int maxPawns = __2;
            int stackCount = __3;
            ReservationLayerDef layer = __4;
            bool ignoreOtherReservations = __5;

            if (__instance == null || claimant == null ||
                !ReferenceEquals(claimant, context.Pawn))
            {
                Interlocked.Increment(ref pawnBypass);
                return true;
            }

            if (!target.IsValid)
            {
                Interlocked.Increment(ref invalidTargetBypass);
                return true;
            }

            ReserveKey key = new ReserveKey(
                __instance, claimant, target, maxPawns, stackCount, layer, ignoreOtherReservations);

            NegativeEntry entry;
            if (!context.Negatives.TryGetValue(key, out entry))
            {
                __state = CallState.ForStore(context, key, target);
                return true;
            }

            if (entry.MutationEpoch != context.MutationEpoch ||
                !entry.Fingerprint.Matches(target))
            {
                context.Negatives.Remove(key);
                Interlocked.Increment(ref fingerprintBypass);
                __state = CallState.ForStore(context, key, target);
                return true;
            }

            Interlocked.Increment(ref memoCandidates);

            if (!chainAuthoritativeSafe || runtimeQuarantined)
            {
                Interlocked.Increment(ref authorityBypass);
                __state = CallState.ForVerify(context, key, target);
                Interlocked.Increment(ref verifyRuns);
                return true;
            }

            context.HitSerial++;
            bool verify = context.ValidatedMatches < WarmupMatches ||
                (context.HitSerial & VerifyMask) == 0;

            if (verify)
            {
                __state = CallState.ForVerify(context, key, target);
                Interlocked.Increment(ref verifyRuns);
                return true;
            }

            __result = false;
            __state = CallState.ForAuthoritative(context);
            Interlocked.Increment(ref authoritativeHits);
            return false;
        }

        public static Exception CanReserveFinalizer(
            Exception __exception,
            bool __result,
            CallState __state)
        {
            if (__exception != null)
            {
                Interlocked.Increment(ref exceptions);
                return __exception;
            }

            PackageContext context = __state.Context;
            if (context == null || !ReferenceEquals(current, context))
                return __exception;

            if (__state.AuthoritativeHit)
                return __exception;

            if (__state.Verify)
            {
                if (!__result)
                {
                    context.ValidatedMatches++;
                    Interlocked.Increment(ref verifyMatches);
                }
                else
                {
                    context.Negatives.Clear();
                    context.ValidatedMatches = 0;
                    runtimeQuarantined = true;
                    Interlocked.Increment(ref mismatches);
                    Interlocked.Increment(ref quarantines);
                }
                return __exception;
            }

            if (!__state.Store)
                return __exception;

            if (__result)
            {
                Interlocked.Increment(ref positiveLive);
                return __exception;
            }

            Interlocked.Increment(ref negativeLive);

            if (context.Negatives.Count >= Capacity)
            {
                Interlocked.Increment(ref capacityBypass);
                return __exception;
            }

            if (!context.Negatives.ContainsKey(__state.Key))
            {
                context.Negatives.Add(
                    __state.Key,
                    new NegativeEntry(
                        context.MutationEpoch,
                        TargetFingerprint.Capture(__state.Target)));
                Interlocked.Increment(ref stores);
            }

            return __exception;
        }

        public static void ReservationMutationPrefix()
        {
            PackageContext context = current;
            if (context == null) return;

            context.MutationEpoch++;
            context.Negatives.Clear();
            context.ValidatedMatches = 0;
            Interlocked.Increment(ref mutationInvalidations);
        }

        private static void PatchMutationMethods(Harmony harmony)
        {
            HashSet<MethodBase> unique = new HashSet<MethodBase>();
            string[] names = new string[]
            {
                "Reserve",
                "Release",
                "ReleaseClaimedBy",
                "ReleaseAllClaimedBy",
                "ReleaseAllForTarget"
            };

            MethodInfo[] methods;
            try
            {
                methods = typeof(ReservationManager).GetMethods(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic |
                    BindingFlags.DeclaredOnly);
            }
            catch
            {
                installFailures++;
                return;
            }

            for (int n = 0; n < names.Length; n++)
            {
                int matched = 0;
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != names[n] || !unique.Add(method))
                        continue;

                    matched++;
                    try
                    {
                        harmony.Patch(
                            method,
                            prefix: new HarmonyMethod(
                                typeof(ReservationTransaction093T32A),
                                nameof(ReservationMutationPrefix))
                            { priority = Priority.First + 300 });
                        mutationMethodsPatched++;
                    }
                    catch
                    {
                        installFailures++;
                    }
                }

                if (matched == 0)
                    mutationMethodsMissing++;
            }
        }

        private static void ReauditChain()
        {
            MethodBase target = canReserveTarget;
            if (target == null)
            {
                chainAuditUnknown = true;
                chainAuthoritativeSafe = false;
                return;
            }

            bool wasSafe = chainAuthoritativeSafe;
            AuditChain(target);
            Interlocked.Increment(ref chainAudits);

            bool nowSafe = !chainAuditUnknown &&
                foreignTranspilers == 0 &&
                foreignFinalizers == 0;

            if (wasSafe && !nowSafe)
            {
                chainAuthoritativeSafe = false;
                PackageContext context = current;
                if (context != null)
                    context.Negatives.Clear();
                Interlocked.Increment(ref lateUnsafeTransitions);
            }
            else if (!runtimeQuarantined)
            {
                chainAuthoritativeSafe = nowSafe;
            }
        }

        private static void AuditChain(MethodBase target)
        {
            try
            {
                chainAuditUnknown = false;
                Patches info = Harmony.GetPatchInfo(target);
                if (info == null)
                {
                    foreignPrefixes = foreignPostfixes = foreignTranspilers = foreignFinalizers = 0;
                    return;
                }

                foreignPrefixes = CountForeign(info.Prefixes);
                foreignPostfixes = CountForeign(info.Postfixes);
                foreignTranspilers = CountForeign(info.Transpilers);
                foreignFinalizers = CountForeign(info.Finalizers);
            }
            catch
            {
                chainAuditUnknown = true;
            }
        }

        private static int CountForeign(IEnumerable<Patch> patches)
        {
            int count = 0;
            if (patches == null) return count;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(
                    patch.owner,
                    RimMTBootstrap.HarmonyId,
                    StringComparison.Ordinal))
                    count++;
            }
            return count;
        }

        internal static string Summary()
        {
            return "T32-A Reservation transaction: installed=" + installed +
                ", canReservePatched=" + canReservePatched +
                ", chainAuthoritativeSafe=" + chainAuthoritativeSafe +
                ", runtimeQuarantined=" + runtimeQuarantined +
                ", chain[foreignPrefixes=" + foreignPrefixes +
                ", foreignPostfixes=" + foreignPostfixes +
                ", foreignTranspilers=" + foreignTranspilers +
                ", foreignFinalizers=" + foreignFinalizers +
                ", auditUnknown=" + chainAuditUnknown + "]" +
                ", mutationMethods[patched/missing]=" + mutationMethodsPatched + "/" +
                mutationMethodsMissing +
                ", packages=" + Interlocked.Read(ref packages) +
                ", calls[observed/inScope]=" + Interlocked.Read(ref observed) + "/" +
                Interlocked.Read(ref inScope) +
                ", negative[stores/memoCandidates/authoritativeHits]=" +
                Interlocked.Read(ref stores) + "/" +
                Interlocked.Read(ref memoCandidates) + "/" +
                Interlocked.Read(ref authoritativeHits) +
                ", verify[runs/matches/mismatches/quarantines]=" +
                Interlocked.Read(ref verifyRuns) + "/" +
                Interlocked.Read(ref verifyMatches) + "/" +
                Interlocked.Read(ref mismatches) + "/" +
                Interlocked.Read(ref quarantines) +
                ", live[positive/negative]=" +
                Interlocked.Read(ref positiveLive) + "/" +
                Interlocked.Read(ref negativeLive) +
                ", invalidation[reservationMutation/fingerprint]=" +
                Interlocked.Read(ref mutationInvalidations) + "/" +
                Interlocked.Read(ref fingerprintBypass) +
                ", bypass[authority/runOriginal/pawn/invalidTarget/capacity]=" +
                Interlocked.Read(ref authorityBypass) + "/" +
                Interlocked.Read(ref runOriginalBypass) + "/" +
                Interlocked.Read(ref pawnBypass) + "/" +
                Interlocked.Read(ref invalidTargetBypass) + "/" +
                Interlocked.Read(ref capacityBypass) +
                ", exceptions=" + Interlocked.Read(ref exceptions) +
                ", installFailures=" + installFailures +
                ". False-only exact CanReserve memo; reservation mutations clear the package memo; " +
                "foreign prefixes/postfixes remain in-chain; foreign transpiler/finalizer => live fallback; " +
                "no reservation/Job/priority/reachability/cross-package result is created or cached.";
        }

        internal struct CallState
        {
            internal PackageContext Context;
            internal ReserveKey Key;
            internal LocalTargetInfo Target;
            internal bool Store;
            internal bool Verify;
            internal bool AuthoritativeHit;

            internal static CallState ForStore(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Store = true
                };
            }

            internal static CallState ForVerify(
                PackageContext context,
                ReserveKey key,
                LocalTargetInfo target)
            {
                return new CallState
                {
                    Context = context,
                    Key = key,
                    Target = target,
                    Verify = true
                };
            }

            internal static CallState ForAuthoritative(PackageContext context)
            {
                return new CallState
                {
                    Context = context,
                    AuthoritativeHit = true
                };
            }
        }

        internal sealed class PackageContext
        {
            internal readonly Pawn Pawn;
            internal readonly long Generation;
            internal readonly Dictionary<ReserveKey, NegativeEntry> Negatives =
                new Dictionary<ReserveKey, NegativeEntry>();

            internal long MutationEpoch;
            internal long HitSerial;
            internal int ValidatedMatches;

            internal PackageContext(Pawn pawn, long generation)
            {
                Pawn = pawn;
                Generation = generation;
            }
        }

        internal struct NegativeEntry
        {
            internal readonly long MutationEpoch;
            internal readonly TargetFingerprint Fingerprint;

            internal NegativeEntry(long mutationEpoch, TargetFingerprint fingerprint)
            {
                MutationEpoch = mutationEpoch;
                Fingerprint = fingerprint;
            }
        }

        internal struct ReserveKey : IEquatable<ReserveKey>
        {
            internal readonly ReservationManager Manager;
            internal readonly Pawn Pawn;
            internal readonly bool HasThing;
            internal readonly Thing Thing;
            internal readonly IntVec3 Cell;
            internal readonly int MaxPawns;
            internal readonly int StackCount;
            internal readonly ReservationLayerDef Layer;
            internal readonly bool IgnoreOtherReservations;

            internal ReserveKey(
                ReservationManager manager,
                Pawn pawn,
                LocalTargetInfo target,
                int maxPawns,
                int stackCount,
                ReservationLayerDef layer,
                bool ignoreOtherReservations)
            {
                Manager = manager;
                Pawn = pawn;
                HasThing = target.HasThing;
                Thing = target.HasThing ? target.Thing : null;
                Cell = target.Cell;
                MaxPawns = maxPawns;
                StackCount = stackCount;
                Layer = layer;
                IgnoreOtherReservations = ignoreOtherReservations;
            }

            public bool Equals(ReserveKey other)
            {
                return ReferenceEquals(Manager, other.Manager) &&
                    ReferenceEquals(Pawn, other.Pawn) &&
                    HasThing == other.HasThing &&
                    ReferenceEquals(Thing, other.Thing) &&
                    Cell == other.Cell &&
                    MaxPawns == other.MaxPawns &&
                    StackCount == other.StackCount &&
                    ReferenceEquals(Layer, other.Layer) &&
                    IgnoreOtherReservations == other.IgnoreOtherReservations;
            }

            public override bool Equals(object obj)
            {
                return obj is ReserveKey && Equals((ReserveKey)obj);
            }

            public override int GetHashCode()
            {
                unchecked
                {
                    int h = Manager == null ? 0 : RuntimeHelpers.GetHashCode(Manager);
                    h = h * 397 ^ (Pawn == null ? 0 : RuntimeHelpers.GetHashCode(Pawn));
                    h = h * 397 ^ (HasThing ? 1 : 0);
                    h = h * 397 ^ (Thing == null ? 0 : RuntimeHelpers.GetHashCode(Thing));
                    h = h * 397 ^ Cell.GetHashCode();
                    h = h * 397 ^ MaxPawns;
                    h = h * 397 ^ StackCount;
                    h = h * 397 ^ (Layer == null ? 0 : RuntimeHelpers.GetHashCode(Layer));
                    h = h * 397 ^ (IgnoreOtherReservations ? 1 : 0);
                    return h;
                }
            }
        }

        internal struct TargetFingerprint
        {
            internal readonly bool HasThing;
            internal readonly Thing Thing;
            internal readonly Map MapHeld;
            internal readonly IntVec3 PositionHeld;
            internal readonly bool Spawned;
            internal readonly int StackCount;

            internal TargetFingerprint(
                bool hasThing,
                Thing thing,
                Map mapHeld,
                IntVec3 positionHeld,
                bool spawned,
                int stackCount)
            {
                HasThing = hasThing;
                Thing = thing;
                MapHeld = mapHeld;
                PositionHeld = positionHeld;
                Spawned = spawned;
                StackCount = stackCount;
            }

            internal static TargetFingerprint Capture(LocalTargetInfo target)
            {
                if (!target.HasThing)
                    return new TargetFingerprint(
                        false, null, null, IntVec3.Invalid, false, 0);

                Thing thing = target.Thing;
                if (thing == null)
                    return new TargetFingerprint(
                        true, null, null, IntVec3.Invalid, false, 0);

                return new TargetFingerprint(
                    true,
                    thing,
                    thing.MapHeld,
                    thing.PositionHeld,
                    thing.Spawned,
                    thing.stackCount);
            }

            internal bool Matches(LocalTargetInfo target)
            {
                if (HasThing != target.HasThing)
                    return false;

                if (!HasThing)
                    return true;

                Thing thing = target.Thing;
                return ReferenceEquals(Thing, thing) &&
                    thing != null &&
                    ReferenceEquals(MapHeld, thing.MapHeld) &&
                    PositionHeld == thing.PositionHeld &&
                    Spawned == thing.Spawned &&
                    StackCount == thing.stackCount;
            }
        }
    }
}
