using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    /// <summary>
    /// Stage 4B: bounded Pick Up And Haul candidate contraction.
    ///
    /// The v1.5 PUAH WorkGiver_HaulToInventory performs its expensive
    /// StoreUtility.TryFindBestBetterStorageFor call from HasJobOnThing. This layer
    /// never replaces HasJobOnThing/JobOnThing. It only removes candidates that are
    /// already provably rejected by PUAH's own pre-storage predicates, plus the
    /// mathematically terminal StoragePriority.Critical case (there is no better
    /// storage priority than Critical).
    ///
    /// To avoid paying for a second full scan when contraction would not help, only
    /// >=256 candidate sources are considered. The first 32 candidates are probed;
    /// the full source is copied/filtered only if >=25% of the probe is hard-negative.
    /// Candidate order is preserved. Any foreign authority patch on PUAH source or
    /// HasJobOnThing disables this feature entirely.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class PickUpAndHaulCandidateContraction
    {
        private const string HarmonyId = "allen.rimmt";
        private const int MinSource = 256;
        private const int ProbeCount = 32;
        private const int ProbeRejectThreshold = 8;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        private static Type puahType;
        private static MethodInfo sourceMethod;
        private static MethodInfo hasThingMethod;
        private static MethodInfo corpseAllowedMethod;
        private static bool installed;
        private static bool active;
        private static string inactiveReason = "not installed";

        private static long observed;
        private static long sourceCandidates;
        private static long smallBypass;
        private static long lowYieldBypass;
        private static long contractedCalls;
        private static long keptCandidates;
        private static long prunedCandidates;
        private static long prunedNullOrMap;
        private static long prunedForbidden;
        private static long prunedReserve;
        private static long prunedCorpse;
        private static long prunedHaulFast;
        private static long prunedCritical;
        private static long failures;

        static PickUpAndHaulCandidateContraction()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;

            try
            {
                puahType = AccessTools.TypeByName("PickUpAndHaul.WorkGiver_HaulToInventory");
                if (puahType == null)
                {
                    inactiveReason = "PUAH type not found";
                    return;
                }

                sourceMethod = puahType.GetMethod("PotentialWorkThingsGlobal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                hasThingMethod = puahType.GetMethod("HasJobOnThing", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                corpseAllowedMethod = puahType.GetMethod("IsNotCorpseOrAllowed", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (sourceMethod == null || hasThingMethod == null)
                {
                    inactiveReason = "PUAH source/HasJobOnThing shape not found";
                    return;
                }

                if (HasUnsafeForeignPatch(sourceMethod) || HasUnsafeForeignPatch(hasThingMethod))
                {
                    inactiveReason = "foreign PUAH source/HasJobOnThing authority patch";
                    return;
                }

                Harmony.Patch(sourceMethod,
                    postfix: new HarmonyMethod(typeof(PickUpAndHaulCandidateContraction), nameof(SourcePostfix)) { priority = Priority.Last });

                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    Harmony.Patch(report, postfix: new HarmonyMethod(typeof(PickUpAndHaulCandidateContraction), nameof(ReportPostfix)) { priority = Priority.Last });

                active = true;
                inactiveReason = "active";
                Log.Message("[RimMT] RC2-T2 Stage 4B PickUpAndHaul Candidate Contraction installed: >=256 source, 32-candidate probe, >=25% hard-negative admission. PUAH HasJobOnThing/JobOnThing remain authoritative.");
            }
            catch (Exception ex)
            {
                active = false;
                inactiveReason = ex.GetType().Name + ": " + ex.Message;
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 Stage 4B PUAH contraction failed closed: " + inactiveReason);
            }
        }

        private static bool HasUnsafeForeignPatch(MethodBase target)
        {
            Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null) continue;
                if (string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        public static void SourcePostfix(object __instance, Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (!active || pawn == null || pawn.Map == null || __result == null) return;
            if (!PreTailStructureProfiler.IsJobScopeActive) return;

            try
            {
                IList<Thing> source = __result as IList<Thing>;
                if (source == null)
                {
                    List<Thing> materialized = new List<Thing>();
                    foreach (Thing thing in __result) materialized.Add(thing);
                    source = materialized;
                }

                int count = source.Count;
                Interlocked.Increment(ref observed);
                Interlocked.Add(ref sourceCandidates, count);
                if (count < MinSource)
                {
                    Interlocked.Increment(ref smallBypass);
                    return;
                }

                int probe = Math.Min(ProbeCount, count);
                bool[] probeReject = new bool[probe];
                int rejected = 0;
                for (int i = 0; i < probe; i++)
                {
                    int reason;
                    bool reject = IsHardNegative(source[i], pawn, out reason);
                    probeReject[i] = reject;
                    if (reject) rejected++;
                }

                if (rejected < ProbeRejectThreshold)
                {
                    Interlocked.Increment(ref lowYieldBypass);
                    return;
                }

                List<Thing> filtered = new List<Thing>(Math.Max(16, count - rejected));
                for (int i = 0; i < count; i++)
                {
                    Thing thing = source[i];
                    int reason = 0;
                    bool reject;
                    if (i < probe)
                    {
                        reject = probeReject[i];
                        if (reject)
                        {
                            IsHardNegative(thing, pawn, out reason);
                        }
                    }
                    else
                    {
                        reject = IsHardNegative(thing, pawn, out reason);
                    }

                    if (reject)
                    {
                        CountReason(reason);
                        Interlocked.Increment(ref prunedCandidates);
                    }
                    else
                    {
                        filtered.Add(thing);
                        Interlocked.Increment(ref keptCandidates);
                    }
                }

                Interlocked.Increment(ref contractedCalls);
                __result = filtered;
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                if (Interlocked.Read(ref failures) <= 4)
                    Log.Warning("[RimMT] RC2-T2 Stage 4B PUAH contraction bypassed after failure: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        private static bool IsHardNegative(Thing thing, Pawn pawn, out int reason)
        {
            reason = 0;
            if (thing == null || !thing.Spawned || thing.Map != pawn.Map)
            {
                reason = 1;
                return true;
            }

            if (thing.IsForbidden(pawn))
            {
                reason = 2;
                return true;
            }

            if (!pawn.CanReserve(thing))
            {
                reason = 3;
                return true;
            }

            if (corpseAllowedMethod != null && thing is Corpse)
            {
                object allowed = corpseAllowedMethod.Invoke(null, new object[] { thing });
                if (allowed is bool && !(bool)allowed)
                {
                    reason = 4;
                    return true;
                }
            }

            if (!HaulAIUtility.PawnCanAutomaticallyHaulFast(pawn, thing, false))
            {
                reason = 5;
                return true;
            }

            if (StoreUtility.CurrentStoragePriorityOf(thing) == StoragePriority.Critical)
            {
                reason = 6;
                return true;
            }

            return false;
        }

        private static void CountReason(int reason)
        {
            if (reason == 1) Interlocked.Increment(ref prunedNullOrMap);
            else if (reason == 2) Interlocked.Increment(ref prunedForbidden);
            else if (reason == 3) Interlocked.Increment(ref prunedReserve);
            else if (reason == 4) Interlocked.Increment(ref prunedCorpse);
            else if (reason == 5) Interlocked.Increment(ref prunedHaulFast);
            else if (reason == 6) Interlocked.Increment(ref prunedCritical);
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 Stage 4B PUAH Contraction report: active=" + active +
                ", state=" + inactiveReason +
                ", minSource=" + MinSource +
                ", probe=" + ProbeCount + "/" + ProbeRejectThreshold +
                ", observed=" + Interlocked.Read(ref observed) +
                ", sourceCandidates=" + Interlocked.Read(ref sourceCandidates) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", lowYieldBypass=" + Interlocked.Read(ref lowYieldBypass) +
                ", contractedCalls=" + Interlocked.Read(ref contractedCalls) +
                ", kept/pruned=" + Interlocked.Read(ref keptCandidates) + "/" + Interlocked.Read(ref prunedCandidates) +
                ", pruneReasons(nullMap/forbidden/reserve/corpse/haulFast/critical)=" +
                Interlocked.Read(ref prunedNullOrMap) + "/" +
                Interlocked.Read(ref prunedForbidden) + "/" +
                Interlocked.Read(ref prunedReserve) + "/" +
                Interlocked.Read(ref prunedCorpse) + "/" +
                Interlocked.Read(ref prunedHaulFast) + "/" +
                Interlocked.Read(ref prunedCritical) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
