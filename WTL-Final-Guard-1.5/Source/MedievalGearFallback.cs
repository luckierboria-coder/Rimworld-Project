using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using WorldTechLevel;

namespace WTLFinalGuard
{
    /// <summary>
    /// V3 generic gear fallback for NPC pawn generation.
    ///
    /// World Tech Level remains authoritative. This patch runs after WTL's GenerateGearFor postfix.
    /// It only fills genuine apparel/weapon holes left when an over-tech item was filtered and WTL
    /// could not find a registered low-tech alternative. No quest, incident, faction or Anomaly
    /// event is special-cased.
    /// </summary>
    [StaticConstructorOnStartup]
    public static class MedievalGearFallback
    {
        private const string HarmonyId = "allen.wtl.finalguard.medievalgearfallback.v3";
        private static readonly HashSet<int> ErrorKeys = new HashSet<int>();

        static MedievalGearFallback()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                if (!ModsConfig.IsActive("m00nl1ght.WorldTechLevel") &&
                    !ModsConfig.IsActive("m00nl1ght.WorldTechLevel_steam"))
                {
                    return;
                }

                MethodInfo target = AccessTools.Method(typeof(PawnGenerator), "GenerateGearFor");
                MethodInfo postfix = AccessTools.Method(typeof(MedievalGearFallback), nameof(GenerateGearFor_Postfix));
                if (target == null || postfix == null)
                {
                    Log.Warning("[WTL Final Guard V3] Medieval gear fallback target not found; fallback inactive.");
                    return;
                }

                var harmony = new Harmony(HarmonyId);
                harmony.Patch(target, postfix: new HarmonyMethod(postfix) { priority = Priority.Last });
                Log.Message("[WTL Final Guard V3] Medieval NPC gear fallback installed.");
            }
            catch (Exception e)
            {
                Log.Error("[WTL Final Guard V3] Failed to install medieval NPC gear fallback: " + e);
            }
        }

        public static void GenerateGearFor_Postfix(Pawn pawn)
        {
            try
            {
                if (pawn == null || pawn.kindDef == null || pawn.RaceProps == null) return;
                if (pawn.Faction != null && pawn.Faction.IsPlayer) return;
                if (!pawn.RaceProps.ToolUser || !pawn.RaceProps.IsFlesh || pawn.RaceProps.IsAnomalyEntity) return;

                TechLevel limit = pawn.Faction != null
                    ? pawn.Faction.CurrentFilterLevel()
                    : WorldTechLevel.WorldTechLevel.Current;

                if (limit == TechLevel.Archotech) return;

                EnsureFallbackApparel(pawn, limit);
                EnsureFallbackWeapon(pawn, limit);
            }
            catch (Exception e)
            {
                GuardError("GenerateGearFor postfix", e);
            }
        }

        private static void EnsureFallbackApparel(Pawn pawn, TechLevel limit)
        {
            if (pawn.apparel == null) return;

            bool kindNormallyClothed =
                pawn.kindDef.apparelMoney.max > 0.001f ||
                !pawn.kindDef.apparelTags.NullOrEmpty() ||
                !pawn.kindDef.apparelRequired.NullOrEmpty() ||
                !pawn.kindDef.specificApparelRequirements.NullOrEmpty();

            if (!kindNormallyClothed) return;

            bool torsoCovered = Covers(pawn, BodyPartGroupDefOf.Torso);
            bool legsCovered = CoversLegs(pawn);

            if (!torsoCovered)
                TryAddFallbackApparel(pawn, limit, BodyPartGroupDefOf.Torso);

            // A robe/armor added for the torso may also solve leg coverage.
            if (!CoversLegs(pawn))
                TryAddFallbackApparel(pawn, limit, BodyPartGroupDefOf.Legs);
        }

        private static bool Covers(Pawn pawn, BodyPartGroupDef group)
        {
            return pawn.apparel.WornApparel.Any(a =>
                a != null &&
                a.def != null &&
                a.def.apparel != null &&
                a.def.apparel.bodyPartGroups.Contains(group));
        }

        private static bool CoversLegs(Pawn pawn)
        {
            return pawn.apparel.WornApparel.Any(a =>
                a != null &&
                a.def != null &&
                a.def.apparel != null &&
                a.def.apparel.bodyPartGroups.Contains(BodyPartGroupDefOf.Legs) &&
                !a.def.apparel.legsNakedUnlessCoveredBySomethingElse);
        }

        private static bool TryAddFallbackApparel(Pawn pawn, TechLevel limit, BodyPartGroupDef requiredGroup)
        {
            try
            {
                List<ThingDef> candidates = BuildApparelPool(pawn, limit, requiredGroup, true);

                // Event-only/high-tech tags can legitimately have no medieval equivalent.
                // Only the tag restriction is relaxed; all tech/anatomy/wear checks stay active.
                if (candidates.Count == 0 && !pawn.kindDef.apparelTags.NullOrEmpty())
                    candidates = BuildApparelPool(pawn, limit, requiredGroup, false);

                if (candidates.Count == 0) return false;

                ThingDef chosen = candidates.RandomElementByWeight(
                    d => Math.Max(0.01f, d.generateCommonality));

                ThingDef stuff = ReplacementUtility.AppropriateStuffFor(chosen, pawn);
                Apparel apparel = ThingMaker.MakeThing(chosen, stuff) as Apparel;
                if (apparel == null) return false;

                PawnGenerator.PostProcessGeneratedGear(apparel, pawn);
                PawnApparelGenerator.PostProcessApparel(apparel, pawn);

                if (!ApparelUtility.HasPartsToWear(pawn, apparel.def) ||
                    !pawn.apparel.CanWearWithoutDroppingAnything(apparel.def))
                {
                    apparel.Destroy();
                    return false;
                }

                pawn.apparel.Wear(apparel, false);
                DevLog("fallback apparel: " + pawn.kindDef.defName + " -> " + chosen.defName +
                       " (" + requiredGroup.defName + ", <= " + limit + ")");
                return true;
            }
            catch (Exception e)
            {
                GuardError("fallback apparel", e);
                return false;
            }
        }

        private static List<ThingDef> BuildApparelPool(
            Pawn pawn,
            TechLevel limit,
            BodyPartGroupDef requiredGroup,
            bool requireTagMatch)
        {
            return DefDatabase<ThingDef>.AllDefsListForReading.Where(def =>
            {
                if (def == null || !def.IsApparel || def.apparel == null) return false;
                if (def.MinRequiredTechLevel() > limit) return false;
                if (!def.apparel.bodyPartGroups.Contains(requiredGroup)) return false;
                if (!def.apparel.CorrectAgeForWearing(pawn) || !def.apparel.PawnCanWear(pawn)) return false;
                if (!ApparelUtility.HasPartsToWear(pawn, def)) return false;
                if (!pawn.apparel.CanWearWithoutDroppingAnything(def)) return false;
                if (def.generateAllowChance <= 0f) return false;

                if (requireTagMatch && !pawn.kindDef.apparelTags.NullOrEmpty())
                {
                    if (def.apparel.tags.NullOrEmpty() ||
                        !pawn.kindDef.apparelTags.Any(tag => def.apparel.tags.Contains(tag)))
                        return false;
                }

                if (!pawn.kindDef.apparelDisallowTags.NullOrEmpty() &&
                    !def.apparel.tags.NullOrEmpty() &&
                    pawn.kindDef.apparelDisallowTags.Any(tag => def.apparel.tags.Contains(tag)))
                    return false;

                return true;
            }).ToList();
        }

        private static void EnsureFallbackWeapon(Pawn pawn, TechLevel limit)
        {
            if (pawn.equipment == null || pawn.equipment.Primary != null) return;

            bool kindNormallyArmed =
                pawn.kindDef.weaponMoney.max > 0.001f ||
                !pawn.kindDef.weaponTags.NullOrEmpty();

            if (!kindNormallyArmed) return;

            try
            {
                List<ThingDef> candidates = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(def => IsFallbackWeaponCandidate(def, pawn, limit, true))
                    .ToList();

                // Same rule as apparel: preserve PawnKind weapon tags first, then relax only tags.
                if (candidates.Count == 0)
                {
                    candidates = DefDatabase<ThingDef>.AllDefsListForReading
                        .Where(def => IsFallbackWeaponCandidate(def, pawn, limit, false))
                        .ToList();
                }

                if (candidates.Count == 0) return;

                ThingDef chosen = candidates.RandomElementByWeight(
                    d => Math.Max(0.01f, d.generateCommonality));

                ThingDef stuff = ReplacementUtility.AppropriateStuffFor(chosen, pawn);
                ThingWithComps weapon = ThingMaker.MakeThing(chosen, stuff) as ThingWithComps;
                if (weapon == null || weapon.TryGetComp<CompEquippable>() == null)
                {
                    if (weapon != null) weapon.Destroy();
                    return;
                }

                PawnGenerator.PostProcessGeneratedGear(weapon, pawn);
                weapon.StyleDef = pawn.kindDef.weaponStyleDef ?? pawn.Ideo?.GetStyleFor(weapon.def);
                pawn.equipment.AddEquipment(weapon);

                DevLog("fallback weapon: " + pawn.kindDef.defName + " -> " + chosen.defName +
                       " (<= " + limit + ")");
            }
            catch (Exception e)
            {
                GuardError("fallback weapon", e);
            }
        }

        private static bool IsFallbackWeaponCandidate(
            ThingDef def,
            Pawn pawn,
            TechLevel limit,
            bool requireTagMatch)
        {
            if (def == null || !def.IsWeapon) return false;
            if (def.MinRequiredTechLevel() > limit) return false;
            if (def.generateAllowChance <= 0f) return false;
            if (def.GetCompProperties<CompProperties_Equippable>() == null) return false;

            if (requireTagMatch && !pawn.kindDef.weaponTags.NullOrEmpty())
            {
                if (def.weaponTags.NullOrEmpty() ||
                    !pawn.kindDef.weaponTags.Any(tag => def.weaponTags.Contains(tag)))
                    return false;
            }

            return true;
        }

        private static void DevLog(string message)
        {
            if (Prefs.DevMode)
                Log.Message("[WTL Final Guard V3] " + message);
        }

        private static void GuardError(string context, Exception e)
        {
            int key = Gen.HashCombineInt(context.GetHashCode(), e.GetType().FullName.GetHashCode());
            lock (ErrorKeys)
            {
                if (!ErrorKeys.Add(key)) return;
            }
            Log.Error("[WTL Final Guard V3] Error during " + context + ": " + e);
        }
    }
}
