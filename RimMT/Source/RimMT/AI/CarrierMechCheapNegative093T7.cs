using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal enum CarrierPrunerKind093T7
    {
        None = 0,
        HaulToCarrier = 1,
        HaulMechsToCharger = 2
    }

    /// <summary>
    /// V0.9.3-T7: authority-safe deterministic negative pruning for the two remaining
    /// heavy vanilla Biotech hauling WorkGivers observed after T6.
    ///
    /// This helper is called only from the existing S4 accelerated candidate loop after the
    /// 32ms tail threshold has already been crossed. It never creates a Job, never performs
    /// reservation/reachability/charger/ingredient searches, and never returns a positive result.
    /// A true rejection means only that the exact vanilla HasJobOnThing must return false before
    /// reaching its expensive live checks. Every survivor still runs the original validator.
    ///
    /// Foreign Harmony patches on the exact HasJobOnThing method disable the corresponding pruner.
    /// </summary>
    internal static class CarrierMechCheapNegative093T7
    {
        private const string HarmonyOwner = "allen.rimmt";
        private const int AuthorityRecheckMask = 1023;
        private const int MaxSlowDetermineKeys = 24;

        private static readonly MethodInfo HaulToCarrierHasJob = AccessTools.Method(
            typeof(WorkGiver_HaulResourcesToCarrier), "HasJobOnThing",
            new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
        private static readonly MethodInfo HaulMechToChargerHasJob = AccessTools.Method(
            typeof(WorkGiver_HaulMechToCharger), "HasJobOnThing",
            new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });

        private static readonly Dictionary<Type, FieldInfo> ScannerFieldCache = new Dictionary<Type, FieldInfo>();
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

        internal static bool TryPrepare(Predicate<Thing> validator, out CarrierPrunerKind093T7 kind)
        {
            kind = CarrierPrunerKind093T7.None;
            prepareCalls++;
            try
            {
                WorkGiver_Scanner scanner = ResolveScanner(validator);
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
                    if (!AuthoritySafe(CarrierPrunerKind093T7.HaulToCarrier))
                    {
                        carrierAuthorityBypass++;
                        return false;
                    }
                    kind = CarrierPrunerKind093T7.HaulToCarrier;
                    return true;
                }

                if (scannerType == typeof(WorkGiver_HaulMechToCharger) &&
                    string.Equals(defName, "HaulMechsToCharger", StringComparison.Ordinal))
                {
                    chargerPrepares++;
                    if (!AuthoritySafe(CarrierPrunerKind093T7.HaulMechsToCharger))
                    {
                        chargerAuthorityBypass++;
                        return false;
                    }
                    kind = CarrierPrunerKind093T7.HaulMechsToCharger;
                    return true;
                }
            }
            catch
            {
                failures++;
            }
            return false;
        }

        internal static bool Reject(CarrierPrunerKind093T7 kind, Pawn worker, Thing thing)
        {
            try
            {
                Pawn target = thing as Pawn;
                if (target == null || worker == null) return false;

                if (kind == CarrierPrunerKind093T7.HaulToCarrier)
                {
                    carrierChecks++;

                    // Exact early negatives from vanilla WorkGiver_HaulResourcesToCarrier.HasJobOnThing.
                    // We intentionally stop before IsForbidden/CanReserve/FindFixedIngredientCount.
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

                if (kind == CarrierPrunerKind093T7.HaulMechsToCharger)
                {
                    chargerChecks++;

                    // Exact early negatives from vanilla WorkGiver_HaulMechToCharger.HasJobOnThing.
                    // We intentionally stop before control-group/max-recharge/forbidden/reservation/charger search.
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
            if (SlowDetermines.Count >= MaxSlowDetermineKeys && !SlowDetermines.ContainsKey(workGiver))
                return;

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

        private static WorkGiver_Scanner ResolveScanner(Predicate<Thing> validator)
        {
            if (validator == null) return null;
            object target = validator.Target;
            if (target == null) return null;

            Type targetType = target.GetType();
            FieldInfo scannerField;
            if (!ScannerFieldCache.TryGetValue(targetType, out scannerField))
            {
                scannerField = ResolveScannerField(targetType);
                ScannerFieldCache[targetType] = scannerField;
            }
            return scannerField == null ? null : scannerField.GetValue(target) as WorkGiver_Scanner;
        }

        private static FieldInfo ResolveScannerField(Type targetType)
        {
            if (targetType == null) return null;
            FieldInfo[] fields = targetType.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            FieldInfo fallback = null;
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo field = fields[i];
                if (typeof(WorkGiver_Scanner).IsAssignableFrom(field.FieldType)) return field;
                if (fallback == null && typeof(WorkGiver).IsAssignableFrom(field.FieldType)) fallback = field;
            }
            return fallback;
        }

        private static bool AuthoritySafe(CarrierPrunerKind093T7 kind)
        {
            if (kind == CarrierPrunerKind093T7.HaulToCarrier)
            {
                long c = carrierPrepares;
                if (carrierAuthorityState != 0 && (c & AuthorityRecheckMask) != 1)
                    return carrierAuthorityState > 0;
                carrierAuthorityState = HasForeignPatch(HaulToCarrierHasJob) ? -1 : 1;
                return carrierAuthorityState > 0;
            }

            if (kind == CarrierPrunerKind093T7.HaulMechsToCharger)
            {
                long c = chargerPrepares;
                if (chargerAuthorityState != 0 && (c & AuthorityRecheckMask) != 1)
                    return chargerAuthorityState > 0;
                chargerAuthorityState = HasForeignPatch(HaulMechToChargerHasJob) ? -1 : 1;
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
            return "T7 carrier/mech cheap-negative: prepares=" + prepareCalls +
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
                   ". Pruner is S4-only after the existing 32ms tail threshold; survivors run original live validator.";
        }

        internal static string SlowDetermineSummary()
        {
            string top = BuildTopSlowDetermineSummary();
            return "T7 sampled DetermineNextJob >=20ms WorkGiver evidence: calls=" + slowDetermine20 +
                   ", withHeavyS4Evidence=" + slowDetermineWithHeavyEvidence +
                   ", withoutHeavyS4Evidence=" + slowDetermineWithoutHeavyEvidence +
                   ", top=" + top +
                   ". Attribution is bounded to existing T2 deep windows and chooses the >=64-reject S4 WorkGiver with the most rejects inside that DetermineNextJob call.";
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
