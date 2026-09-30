using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal enum CarrierPrunerKind093T8
    {
        None = 0,
        HaulToCarrier = 1,
        HaulMechsToCharger = 2
    }

    /// <summary>
    /// V0.9.3-T8: deterministic negative pruning for two heavy vanilla Biotech hauling
    /// WorkGivers, rebased directly on T5. Called only from the existing S4 accelerated loop
    /// after its 32ms tail threshold. It never builds a query-time collection/index, creates Jobs,
    /// or replaces live reservation/reachability, ingredient search, charger search, or the
    /// original validator for survivors.
    /// </summary>
    internal static class CarrierMechCheapNegative093T8
    {
        private const string HarmonyOwner = "allen.rimmt";
        private const int AuthorityRecheckMask = 1023;
        private const int MaxSlowDetermineKeys = 24;

        private static readonly Type[] JobArgs = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
        private static readonly MethodInfo HaulToCarrierHasJob = AccessTools.Method(
            typeof(WorkGiver_HaulResourcesToCarrier), "HasJobOnThing", JobArgs);
        private static readonly MethodInfo HaulToCarrierJobOnThing = AccessTools.Method(
            typeof(WorkGiver_HaulResourcesToCarrier), "JobOnThing", JobArgs);
        private static readonly MethodInfo HaulMechToChargerHasJob = AccessTools.Method(
            typeof(WorkGiver_HaulMechToCharger), "HasJobOnThing", JobArgs);
        private static readonly MethodInfo HaulMechToChargerJobOnThing = AccessTools.Method(
            typeof(WorkGiver_HaulMechToCharger), "JobOnThing", JobArgs);
        private static readonly Dictionary<string, SlowDetermineStats> SlowDetermines = new Dictionary<string, SlowDetermineStats>();

        // 0 unknown, 1 safe, -1 foreign authority present.
        private static int carrierAuthorityState;
        private static int chargerAuthorityState;

        private static long prepareCalls;
        private static long scannerUnresolved;
        private static long carrierPrepares;
        private static long carrierAuthorityBypass;
        private static long carrierChecks;
        private static long carrierRejects;
        private static long carrierSurvivors;
        private static long chargerPrepares;
        private static long chargerAuthorityBypass;
        private static long chargerChecks;
        private static long chargerRejects;
        private static long chargerSurvivors;
        private static long failures;

        private static long slowDetermine20;
        private static long slowDetermineWithHeavyEvidence;
        private static long slowDetermineWithoutHeavyEvidence;

        internal static bool TryPrepare(WorkGiver_Scanner scanner, out CarrierPrunerKind093T8 kind)
        {
            kind = CarrierPrunerKind093T8.None;
            prepareCalls++;
            try
            {
                if (scanner == null)
                {
                    scannerUnresolved++;
                    return false;
                }

                Type scannerType = scanner.GetType();
                string defName = scanner.def == null ? null : scanner.def.defName;
                if (scannerType == typeof(WorkGiver_HaulResourcesToCarrier) &&
                    string.Equals(defName, "HaulToCarrier", StringComparison.Ordinal))
                {
                    carrierPrepares++;
                    if (!AuthoritySafe(CarrierPrunerKind093T8.HaulToCarrier))
                    {
                        carrierAuthorityBypass++;
                        return false;
                    }
                    kind = CarrierPrunerKind093T8.HaulToCarrier;
                    return true;
                }

                if (scannerType == typeof(WorkGiver_HaulMechToCharger) &&
                    string.Equals(defName, "HaulMechsToCharger", StringComparison.Ordinal))
                {
                    chargerPrepares++;
                    if (!AuthoritySafe(CarrierPrunerKind093T8.HaulMechsToCharger))
                    {
                        chargerAuthorityBypass++;
                        return false;
                    }
                    kind = CarrierPrunerKind093T8.HaulMechsToCharger;
                    return true;
                }
            }
            catch
            {
                failures++;
            }
            return false;
        }

        internal static bool Reject(CarrierPrunerKind093T8 kind, Pawn worker, Thing thing)
        {
            try
            {
                Pawn target = thing as Pawn;
                if (target == null || worker == null) return false;

                if (kind == CarrierPrunerKind093T8.HaulToCarrier)
                {
                    carrierChecks++;

                    // Exact early negatives from vanilla WorkGiver_HaulResourcesToCarrier.HasJobOnThing.
                    // Stop before IsForbidden/CanReserve/FindFixedIngredientCount.
                    if (!target.IsColonyMech || !target.Spawned || target.Downed)
                    {
                        carrierRejects++;
                        return true;
                    }

                    CompMechCarrier comp = target.GetComp<CompMechCarrier>();
                    if (comp == null || comp.AmountToAutofill <= 0)
                    {
                        carrierRejects++;
                        return true;
                    }

                    carrierSurvivors++;
                    return false;
                }

                if (kind == CarrierPrunerKind093T8.HaulMechsToCharger)
                {
                    chargerChecks++;

                    // Exact early negatives from vanilla WorkGiver_HaulMechToCharger.HasJobOnThing.
                    // Stop before control-group/max-recharge/forbidden/reservation/GetClosestCharger.
                    if (target.RaceProps == null || !target.RaceProps.IsMechanoid || !target.IsColonyMech)
                    {
                        chargerRejects++;
                        return true;
                    }

                    if (target.needs != null && target.needs.energy != null &&
                        !target.Downed && !target.needs.energy.IsLowEnergySelfShutdown)
                    {
                        chargerRejects++;
                        return true;
                    }

                    if (target.CurJobDef == JobDefOf.MechCharge)
                    {
                        chargerRejects++;
                        return true;
                    }

                    chargerSurvivors++;
                    return false;
                }
            }
            catch
            {
                failures++;
            }
            return false;
        }

        internal static void RecordSlowDetermine(string workGiver, int rejects, double elapsedMs)
        {
            slowDetermine20++;
            if (string.IsNullOrEmpty(workGiver) || rejects <= 0)
            {
                slowDetermineWithoutHeavyEvidence++;
                return;
            }

            slowDetermineWithHeavyEvidence++;
            if (SlowDetermines.Count >= MaxSlowDetermineKeys && !SlowDetermines.ContainsKey(workGiver)) return;

            SlowDetermineStats stats;
            if (!SlowDetermines.TryGetValue(workGiver, out stats))
            {
                stats = new SlowDetermineStats();
                SlowDetermines[workGiver] = stats;
            }
            stats.Calls++;
            stats.Rejects += rejects;
            if (elapsedMs > stats.MaxMs) stats.MaxMs = elapsedMs;
        }

        private static bool AuthoritySafe(CarrierPrunerKind093T8 kind)
        {
            if (kind == CarrierPrunerKind093T8.HaulToCarrier)
            {
                long c = carrierPrepares;
                if (carrierAuthorityState != 0 && (c & AuthorityRecheckMask) != 1)
                    return carrierAuthorityState > 0;
                carrierAuthorityState = HasForeignPatch(HaulToCarrierHasJob) || HasForeignPatch(HaulToCarrierJobOnThing) ? -1 : 1;
                return carrierAuthorityState > 0;
            }

            if (kind == CarrierPrunerKind093T8.HaulMechsToCharger)
            {
                long c = chargerPrepares;
                if (chargerAuthorityState != 0 && (c & AuthorityRecheckMask) != 1)
                    return chargerAuthorityState > 0;
                chargerAuthorityState = HasForeignPatch(HaulMechToChargerHasJob) || HasForeignPatch(HaulMechToChargerJobOnThing) ? -1 : 1;
                return chargerAuthorityState > 0;
            }

            return false;
        }

        private static bool HasForeignPatch(MethodBase method)
        {
            if (method == null) return true;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) ||
                   HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IEnumerable<Patch> patches)
        {
            if (patches == null) return false;
            foreach (Patch patch in patches)
            {
                if (patch == null) continue;
                if (!string.Equals(patch.owner, HarmonyOwner, StringComparison.Ordinal)) return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T8 carrier/mech cheap-negative (T5 baseline): prepares=" + prepareCalls +
                   ", scannerUnresolved=" + scannerUnresolved +
                   "; HaulToCarrier[authoritySafe=" + (carrierAuthorityState > 0) +
                   ", prepares=" + carrierPrepares +
                   ", authorityBypass=" + carrierAuthorityBypass +
                   ", checks=" + carrierChecks +
                   ", rejects=" + carrierRejects +
                   ", survivors=" + carrierSurvivors +
                   ", rejectRate=" + Rate(carrierRejects, carrierChecks) + "%]" +
                   "; HaulMechsToCharger[authoritySafe=" + (chargerAuthorityState > 0) +
                   ", prepares=" + chargerPrepares +
                   ", authorityBypass=" + chargerAuthorityBypass +
                   ", checks=" + chargerChecks +
                   ", rejects=" + chargerRejects +
                   ", survivors=" + chargerSurvivors +
                   ", rejectRate=" + Rate(chargerRejects, chargerChecks) + "%]" +
                   "; failures=" + failures +
                   ". S4-only after the existing 32ms tail threshold; no query-time collection/index build; survivors run original live validator.";
        }

        internal static string SlowDetermineSummary()
        {
            return "T8 sampled DetermineNextJob >=20ms WorkGiver evidence: calls=" + slowDetermine20 +
                   ", withHeavyS4Evidence=" + slowDetermineWithHeavyEvidence +
                   ", withoutHeavyS4Evidence=" + slowDetermineWithoutHeavyEvidence +
                   ", top=" + BuildTopSlowDetermineSummary() +
                   ". Bounded to existing T2 deep windows; selects the >=64-reject S4 WorkGiver with the most rejects inside that DetermineNextJob call.";
        }

        private static string BuildTopSlowDetermineSummary()
        {
            if (SlowDetermines.Count == 0) return "none";
            List<KeyValuePair<string, SlowDetermineStats>> rows = new List<KeyValuePair<string, SlowDetermineStats>>(SlowDetermines);
            rows.Sort(delegate(KeyValuePair<string, SlowDetermineStats> a, KeyValuePair<string, SlowDetermineStats> b)
            {
                int byCalls = b.Value.Calls.CompareTo(a.Value.Calls);
                if (byCalls != 0) return byCalls;
                return b.Value.Rejects.CompareTo(a.Value.Rejects);
            });

            int take = Math.Min(8, rows.Count);
            string text = string.Empty;
            for (int i = 0; i < take; i++)
            {
                if (i != 0) text += "; ";
                SlowDetermineStats s = rows[i].Value;
                text += rows[i].Key + "(calls=" + s.Calls + ",rejects=" + s.Rejects + ",maxMs=" + s.MaxMs.ToString("F2") + ")";
            }
            return text;
        }

        private static string Rate(long numerator, long denominator)
        {
            return denominator <= 0L ? "0.00" : (numerator * 100.0 / denominator).ToString("F2");
        }

        private sealed class SlowDetermineStats
        {
            internal long Calls;
            internal long Rejects;
            internal double MaxMs;
        }
    }
}
