using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT
{
    /// <summary>
    /// Exact cheap-negative subset of vanilla WorkGiver_Merge + HaulAIUtility for V0.9.4.
    /// It never returns true as authority for a job; true means only "keep candidate and let
    /// Vanilla decide". Any unexpected state or foreign Harmony authority fails open.
    /// </summary>
    internal static class HaulMergeCheapNegative094
    {
        private static readonly Dictionary<Type, bool> AuthorityCache = new Dictionary<Type, bool>();

        internal static bool IsCandidate(WorkGiver_Scanner scanner)
        {
            return scanner != null && scanner.GetType() == typeof(WorkGiver_Merge) &&
                   scanner.def != null && scanner.def.defName == "HaulMerge";
        }

        internal static bool IsAuthoritySafe(WorkGiver_Scanner scanner)
        {
            if (!IsCandidate(scanner)) return false;
            Type type = scanner.GetType();
            bool cached;
            if (AuthorityCache.TryGetValue(type, out cached)) return cached;

            bool safe = true;
            try
            {
                Type[] scannerArgs = new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) };
                string[] scannerMethods = new string[] { "HasJobOnThing", "JobOnThing" };
                for (int ni = 0; ni < scannerMethods.Length && safe; ni++)
                {
                    Type current = type;
                    while (current != null && typeof(WorkGiver).IsAssignableFrom(current))
                    {
                        MethodInfo method = current.GetMethod(scannerMethods[ni],
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                            null, scannerArgs, null);
                        if (HasHarmonyAuthority(method))
                        {
                            safe = false;
                            break;
                        }
                        current = current.BaseType;
                    }
                }

                if (safe)
                {
                    MethodInfo autoHaul = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaul),
                        new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                    MethodInfo autoHaulFast = AccessTools.Method(typeof(HaulAIUtility), nameof(HaulAIUtility.PawnCanAutomaticallyHaulFast),
                        new Type[] { typeof(Pawn), typeof(Thing), typeof(bool) });
                    if (autoHaul == null || autoHaulFast == null || HasHarmonyAuthority(autoHaul) || HasHarmonyAuthority(autoHaulFast))
                        safe = false;
                }
            }
            catch
            {
                safe = false;
            }

            AuthorityCache[type] = safe;
            return safe;
        }

        private static bool HasHarmonyAuthority(MethodBase method)
        {
            if (method == null) return false;
            Patches info = Harmony.GetPatchInfo(method);
            return info != null && (info.Prefixes.Count != 0 || info.Postfixes.Count != 0 ||
                                    info.Transpilers.Count != 0 || info.Finalizers.Count != 0);
        }

        internal static bool Pass(Pawn worker, Thing thing)
        {
            try
            {
                if (worker == null || thing == null || thing.def == null) return true;

                // WorkGiver_Merge.JobOnThing first negative.
                if (thing.stackCount == thing.def.stackLimit) return false;

                // PawnCanAutomaticallyHaul negatives before it delegates to the expensive Fast path.
                if (!thing.def.EverHaulable) return false;
                if (thing.IsForbidden(worker)) return false;
                Map map = thing.Map;
                if (map == null || map.designationManager == null) return true;
                if (!thing.def.alwaysHaulable &&
                    map.designationManager.DesignationOn(thing, DesignationDefOf.Haul) == null &&
                    !thing.IsInValidStorage())
                    return false;

                // PawnCanAutomaticallyHaulFast deterministic negatives that do not require reachability.
                UnfinishedThing unfinished = thing as UnfinishedThing;
                if (unfinished != null && unfinished.BoundBill != null)
                {
                    Building billGiver = unfinished.BoundBill.billStack == null
                        ? null
                        : unfinished.BoundBill.billStack.billGiver as Building;
                    if (billGiver == null ||
                        (billGiver.Spawned && billGiver.OccupiedRect().ExpandedBy(1).Contains(unfinished.Position)))
                        return false;
                }

                if (worker.health == null || worker.health.capacities == null) return true;
                if (!worker.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation)) return false;

                if (thing.def.IsNutritionGivingIngestible && thing.def.ingestible != null &&
                    thing.def.ingestible.HumanEdible && !thing.IsSociallyProper(worker, false, true))
                    return false;

                if (thing.IsBurning()) return false;
                return true;
            }
            catch
            {
                // Unexpected mod state: keep the candidate and let original validator decide.
                return true;
            }
        }
    }
}
