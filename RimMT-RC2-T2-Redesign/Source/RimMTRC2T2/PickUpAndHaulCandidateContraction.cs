using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMTRC2T2
{
    /// <summary>
    /// Stage 4B.1: cheap-only Pick Up And Haul candidate contraction.
    ///
    /// This layer intentionally avoids CanReserve, Reachability, StoreUtility,
    /// PawnCanAutomaticallyHaulFast and reflection-based corpse-policy calls while
    /// building the candidate shortlist. Those checks were too expensive at the
    /// PotentialWorkThingsGlobal layer and mostly shifted HasJobOnThing cost earlier.
    ///
    /// Only O(1)/very-cheap hard negatives are considered:
    /// - null/despawned/wrong-map;
    /// - forbidden to this pawn;
    /// - already stored at StoragePriority.Critical, where no better priority exists.
    ///
    /// Sources smaller than 256 are untouched. Large sources are sampled over the
    /// first 32 candidates and only contracted when at least 8 are cheaply rejected.
    /// Candidate order is preserved and PUAH HasJobOnThing/JobOnThing remain final
    /// authority. Any foreign authority patch on PUAH source/HasJobOnThing disables
    /// this feature entirely.
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
        private static long prunedCritical;
        private static long probeCandidates;
        private static long probeRejected;
        private static long elapsedTicks;
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
                inactiveReason = "active-cheap-only";
                Log.Message("[RimMT] RC2-T2 Stage 4B.1 PUAH Cheap-Only Contraction installed: >=256 source, 32-candidate probe, >=25% cheap hard-negative admission. No CanReserve/Reachability/StoreUtility/haul-fast calls are made by the contraction layer.");
            }
            catch (Exception ex)
            {
                active = false;
                inactiveReason = ex.GetType().Name + ": " + ex.Message;
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 Stage 4B.1 PUAH contraction failed closed: " + inactiveReason);
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

            long start = Stopwatch.GetTimestamp();
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
                Interlocked.Add(ref probeCandidates, probe);
                for (int i = 0; i < probe; i++)
                {
                    int reason;
                    bool reject = IsCheapHardNegative(source[i], pawn, out reason);
                    probeReject[i] = reject;
                    if (reject) rejected++;
                }
                Interlocked.Add(ref probeRejected, rejected);

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
                        if (reject) IsCheapHardNegative(thing, pawn, out reason);
                    }
                    else
                    {
                        reject = IsCheapHardNegative(thing, pawn, out reason);
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
                    Log.Warning("[RimMT] RC2-T2 Stage 4B.1 PUAH contraction bypassed after failure: " + ex.GetType().Name + ": " + ex.Message);
            }
            finally
            {
                Interlocked.Add(ref elapsedTicks, Stopwatch.GetTimestamp() - start);
            }
        }

        // Reasons: 1 null/map, 2 forbidden, 3 terminal Critical storage priority.
        private static bool IsCheapHardNegative(Thing thing, Pawn pawn, out int reason)
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

            if (StoreUtility.CurrentStoragePriorityOf(thing) == StoragePriority.Critical)
            {
                reason = 3;
                return true;
            }

            return false;
        }

        private static void CountReason(int reason)
        {
            if (reason == 1) Interlocked.Increment(ref prunedNullOrMap);
            else if (reason == 2) Interlocked.Increment(ref prunedForbidden);
            else if (reason == 3) Interlocked.Increment(ref prunedCritical);
        }

        public static void ReportPostfix()
        {
            long obs = Interlocked.Read(ref observed);
            double totalMs = Interlocked.Read(ref elapsedTicks) * 1000.0 / Stopwatch.Frequency;
            double avgUs = obs > 0 ? totalMs * 1000.0 / obs : 0.0;
            Log.Message("[RimMT] RC2-T2 Stage 4B.1 PUAH Cheap-Only report: active=" + active +
                ", state=" + inactiveReason +
                ", minSource=" + MinSource +
                ", probe=" + ProbeCount + "/" + ProbeRejectThreshold +
                ", observed=" + obs +
                ", sourceCandidates=" + Interlocked.Read(ref sourceCandidates) +
                ", smallBypass=" + Interlocked.Read(ref smallBypass) +
                ", lowYieldBypass=" + Interlocked.Read(ref lowYieldBypass) +
                ", contractedCalls=" + Interlocked.Read(ref contractedCalls) +
                ", kept/pruned=" + Interlocked.Read(ref keptCandidates) + "/" + Interlocked.Read(ref prunedCandidates) +
                ", probeCandidates/rejected=" + Interlocked.Read(ref probeCandidates) + "/" + Interlocked.Read(ref probeRejected) +
                ", pruneReasons(nullMap/forbidden/critical)=" +
                Interlocked.Read(ref prunedNullOrMap) + "/" +
                Interlocked.Read(ref prunedForbidden) + "/" +
                Interlocked.Read(ref prunedCritical) +
                ", ownTotalMs=" + totalMs.ToString("F2") +
                ", ownAvgUs=" + avgUs.ToString("F1") +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
