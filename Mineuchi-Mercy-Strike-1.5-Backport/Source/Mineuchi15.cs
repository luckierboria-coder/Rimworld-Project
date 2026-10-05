using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.MercyStrike15
{
    [StaticConstructorOnStartup]
    public static class Mineuchi15Startup
    {
        static Mineuchi15Startup()
        {
            new Harmony("allen.mineuchi.mercystrike15").PatchAll();
        }
    }

    [DefOf]
    public static class Mineuchi15DefOf
    {
        public static HediffDef Allen_MercyStrike_Mode;
        public static HediffDef Allen_MercyStrike_Suppression;

        static Mineuchi15DefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(Mineuchi15DefOf));
        }
    }

    public class Hediff_MercyStrikeMode : Hediff
    {
        public override bool Visible
        {
            get { return false; }
        }
    }

    public class Hediff_MercyStrikeSuppression : Hediff
    {
        private const int DecayIntervalTicks = 5000;
        private const float DecayAmount = 0.1f;

        public override void Tick()
        {
            base.Tick();

            if (ageTicks > 0 && ageTicks % DecayIntervalTicks == 0)
            {
                Severity = Mathf.Max(0f, Severity - DecayAmount);
            }
        }
    }

    public static class MercyStrikeUtility
    {
        private const float HumanDamageCap = 5f;

        public static bool IsEnabled(Pawn pawn)
        {
            return pawn != null
                && pawn.health != null
                && pawn.health.hediffSet != null
                && pawn.health.hediffSet.HasHediff(Mineuchi15DefOf.Allen_MercyStrike_Mode);
        }

        public static bool IsActive(Pawn pawn)
        {
            return IsEnabled(pawn) && pawn.Drafted;
        }

        public static int MeleeLevel(Pawn pawn)
        {
            if (pawn == null || pawn.skills == null)
            {
                return 0;
            }

            SkillRecord skill = pawn.skills.GetSkill(SkillDefOf.Melee);
            return skill == null ? 0 : skill.Level;
        }

        public static void SetEnabled(Pawn pawn, bool enabled)
        {
            if (pawn == null || pawn.health == null || pawn.health.hediffSet == null)
            {
                return;
            }

            Hediff existing = pawn.health.hediffSet.GetFirstHediffOfDef(Mineuchi15DefOf.Allen_MercyStrike_Mode);

            if (enabled)
            {
                if (existing == null)
                {
                    pawn.health.AddHediff(Mineuchi15DefOf.Allen_MercyStrike_Mode);
                }
            }
            else if (existing != null)
            {
                pawn.health.RemoveHediff(existing);
            }
        }

        public static bool TryGetMercyAttacker(DamageInfo dinfo, out Pawn attacker)
        {
            attacker = dinfo.Instigator as Pawn;
            if (attacker == null || !IsActive(attacker))
            {
                return false;
            }

            // Vanilla melee DamageInfo carries the melee Tool. Extra melee
            // damages intentionally do not, matching the original warning that
            // fire/toxic/etc. additional damage can remain dangerous.
            return dinfo.Tool != null;
        }

        public static float AccuracyFactor(int meleeLevel)
        {
            // Public original formula:
            // hit chance reduced by (40 - Melee * 1.5)%.
            float penaltyPercent = Mathf.Max(0f, 40f - meleeLevel * 1.5f);
            return Mathf.Clamp01(1f - penaltyPercent / 100f);
        }

        public static float PartProtectionChance(int meleeLevel)
        {
            // Public original formula: (50 + Melee * 2)%.
            return Mathf.Clamp01((50f + meleeLevel * 2f) / 100f);
        }

        public static float SuppressionChance(int meleeLevel, float pain)
        {
            // Public original formula: (Melee + Pain * 75)%.
            return Mathf.Clamp01((meleeLevel + Mathf.Clamp01(pain) * 75f) / 100f);
        }

        public static float SuppressionGain(int meleeLevel)
        {
            if (meleeLevel <= 0)
            {
                return 0f;
            }

            // Public original formula:
            // Melee * 0.01 + random(0 .. Melee * 0.02).
            return meleeLevel * 0.01f + Rand.Range(0f, meleeLevel * 0.02f);
        }

        public static BodyPartRecord SelectTargetPart(Pawn target)
        {
            if (target == null || target.health == null || target.health.hediffSet == null)
            {
                return null;
            }

            List<BodyPartRecord> all = target.health.hediffSet.GetNotMissingParts().ToList();
            if (all.Count == 0)
            {
                return null;
            }

            // 1.0.2 behavior: body parts sitting at exactly 1 HP are not chosen
            // while other safe options remain.
            List<BodyPartRecord> safe = new List<BodyPartRecord>();
            int i;
            for (i = 0; i < all.Count; i++)
            {
                BodyPartRecord part = all[i];
                if (target.health.hediffSet.GetPartHealth(part) > 1f && IsNormallySafePart(part))
                {
                    safe.Add(part);
                }
            }

            if (safe.Count > 0)
            {
                // Public original targeting rule:
                // 67% mobility/lower-limb preference.
                if (Rand.Chance(0.67f))
                {
                    List<BodyPartRecord> mobility = new List<BodyPartRecord>();
                    for (i = 0; i < safe.Count; i++)
                    {
                        if (IsMobilityPart(safe[i]))
                        {
                            mobility.Add(safe[i]);
                        }
                    }

                    if (mobility.Count > 0)
                    {
                        // If mobility targeting triggered, 50% chooses the
                        // candidate with the highest current HP.
                        if (Rand.Chance(0.5f))
                        {
                            BodyPartRecord best = mobility[0];
                            float bestHp = target.health.hediffSet.GetPartHealth(best);
                            for (i = 1; i < mobility.Count; i++)
                            {
                                float hp = target.health.hediffSet.GetPartHealth(mobility[i]);
                                if (hp > bestHp)
                                {
                                    best = mobility[i];
                                    bestHp = hp;
                                }
                            }
                            return best;
                        }

                        return mobility[Rand.Range(0, mobility.Count)];
                    }
                }

                return safe[Rand.Range(0, safe.Count)];
            }

            // 1.0.2 edge-case behavior: if no safe part above 1 HP remains,
            // inevitably fall back to random surviving parts; the separate
            // part-protection logic still attempts to preserve 1 HP.
            List<BodyPartRecord> fallback = new List<BodyPartRecord>();
            for (i = 0; i < all.Count; i++)
            {
                if (target.health.hediffSet.GetPartHealth(all[i]) > 0f)
                {
                    fallback.Add(all[i]);
                }
            }

            if (fallback.Count == 0)
            {
                return null;
            }

            return fallback[Rand.Range(0, fallback.Count)];
        }

        private static bool IsMobilityPart(BodyPartRecord part)
        {
            if (part == null || part.def == null || part.def.tags == null)
            {
                return false;
            }

            List<BodyPartTagDef> tags = part.def.tags;
            return tags.Contains(BodyPartTagDefOf.MovingLimbCore)
                || tags.Contains(BodyPartTagDefOf.MovingLimbSegment)
                || tags.Contains(BodyPartTagDefOf.MovingLimbDigit);
        }

        private static bool IsNormallySafePart(BodyPartRecord part)
        {
            if (part == null || part.def == null)
            {
                return false;
            }

            if (part.IsCorePart || part.depth != BodyPartDepth.Outside)
            {
                return false;
            }

            if (part.groups != null && part.groups.Contains(BodyPartGroupDefOf.FullHead))
            {
                return false;
            }

            List<BodyPartTagDef> tags = part.def.tags;
            if (tags == null)
            {
                return true;
            }

            // Avoid organs/pathways where a single blunt injury can directly
            // threaten life. Limbs/digits remain eligible.
            return !tags.Contains(BodyPartTagDefOf.BloodPumpingSource)
                && !tags.Contains(BodyPartTagDefOf.BreathingSource)
                && !tags.Contains(BodyPartTagDefOf.BreathingPathway)
                && !tags.Contains(BodyPartTagDefOf.ConsciousnessSource)
                && !tags.Contains(BodyPartTagDefOf.MetabolismSource)
                && !tags.Contains(BodyPartTagDefOf.BloodFiltrationSource)
                && !tags.Contains(BodyPartTagDefOf.BloodFiltrationLiver)
                && !tags.Contains(BodyPartTagDefOf.BloodFiltrationKidney);
        }

        public static void ProtectInjury(Pawn target, Hediff_Injury injury, DamageInfo dinfo)
        {
            Pawn attacker;
            if (target == null || injury == null || !TryGetMercyAttacker(dinfo, out attacker))
            {
                return;
            }

            int meleeLevel = MeleeLevel(attacker);

            // Mercy Strike injuries themselves cannot remove a body part.
            injury.destroysBodyParts = false;

            if (target.RaceProps.Humanlike)
            {
                injury.Severity = Mathf.Min(HumanDamageCap, injury.Severity);
            }

            BodyPartRecord part = injury.Part;
            if (part == null)
            {
                return;
            }

            float partHealth = target.health.hediffSet.GetPartHealth(part);
            if (injury.Severity >= partHealth && Rand.Chance(PartProtectionChance(meleeLevel)))
            {
                injury.Severity = Mathf.Max(0f, partHealth - 1f);
                injury.destroysBodyParts = false;
            }
        }

        public static void TryApplySuppression(Pawn target, DamageInfo dinfo)
        {
            Pawn attacker;
            if (target == null
                || target.Dead
                || !target.RaceProps.Humanlike
                || !TryGetMercyAttacker(dinfo, out attacker))
            {
                return;
            }

            int meleeLevel = MeleeLevel(attacker);
            float pain = target.health.hediffSet.PainTotal;
            if (!Rand.Chance(SuppressionChance(meleeLevel, pain)))
            {
                return;
            }

            float gain = SuppressionGain(meleeLevel);
            if (gain <= 0f)
            {
                return;
            }

            Hediff suppression = target.health.hediffSet.GetFirstHediffOfDef(Mineuchi15DefOf.Allen_MercyStrike_Suppression);
            if (suppression == null)
            {
                suppression = HediffMaker.MakeHediff(Mineuchi15DefOf.Allen_MercyStrike_Suppression, target);
                suppression.Severity = Mathf.Min(0.6f, gain);
                target.health.AddHediff(suppression);
            }
            else
            {
                suppression.Severity = Mathf.Min(0.6f, suppression.Severity + gain);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn), "GetGizmos")]
    public static class Patch_Pawn_GetGizmos_MercyStrike
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            Pawn pawn = __instance;
            if (pawn == null
                || pawn.Faction != Faction.OfPlayer
                || pawn.drafter == null
                || !pawn.Drafted
                || !pawn.RaceProps.Humanlike)
            {
                return;
            }

            Command_Toggle command = new Command_Toggle();
            command.defaultLabel = "MercyStrike15_CommandLabel".Translate();
            command.defaultDesc = "MercyStrike15_CommandDesc".Translate();
            command.icon = TexCommand.AttackMelee;
            command.isActive = delegate
            {
                return MercyStrikeUtility.IsEnabled(pawn);
            };
            command.toggleAction = delegate
            {
                MercyStrikeUtility.SetEnabled(pawn, !MercyStrikeUtility.IsEnabled(pawn));
            };

            __result = __result.Concat(new Gizmo[] { command });
        }
    }

    [HarmonyPatch(typeof(Pawn_DraftController), "set_Drafted")]
    public static class Patch_PawnDraftController_Drafted_MercyStrike
    {
        [HarmonyPostfix]
        public static void Postfix(Pawn_DraftController __instance, bool value)
        {
            if (!value && __instance != null)
            {
                MercyStrikeUtility.SetEnabled(__instance.pawn, false);
            }
        }
    }

    [HarmonyPatch(typeof(Verb_MeleeAttack), "GetNonMissChance")]
    public static class Patch_Verb_MeleeAttack_GetNonMissChance_MercyStrike
    {
        [HarmonyPostfix]
        public static void Postfix(Verb_MeleeAttack __instance, ref float __result)
        {
            Pawn pawn = __instance == null ? null : __instance.CasterPawn;
            if (!MercyStrikeUtility.IsActive(pawn))
            {
                return;
            }

            __result = Mathf.Clamp01(__result * MercyStrikeUtility.AccuracyFactor(MercyStrikeUtility.MeleeLevel(pawn)));
        }
    }

    [HarmonyPatch(typeof(Thing), "TakeDamage")]
    public static class Patch_Thing_TakeDamage_MercyStrike
    {
        [HarmonyPrefix]
        public static void Prefix(Thing __instance, ref DamageInfo dinfo)
        {
            Pawn target = __instance as Pawn;
            Pawn attacker;
            if (target == null || !MercyStrikeUtility.TryGetMercyAttacker(dinfo, out attacker))
            {
                return;
            }

            // Convert the primary melee strike to blunt. Additional damage
            // packets generally have no Tool and therefore deliberately bypass
            // this conversion, matching the original mod's warning.
            dinfo.Def = DamageDefOf.Blunt;
            dinfo.SetAllowDamagePropagation(false);

            BodyPartRecord part = MercyStrikeUtility.SelectTargetPart(target);
            if (part != null)
            {
                dinfo.SetHitPart(part);
            }
        }
    }

    [HarmonyPatch(
        typeof(DamageWorker_AddInjury),
        "FinalizeAndAddInjury",
        new Type[]
        {
            typeof(Pawn),
            typeof(Hediff_Injury),
            typeof(DamageInfo),
            typeof(DamageWorker.DamageResult)
        })]
    public static class Patch_DamageWorker_FinalizeAndAddInjury_Hediff_MercyStrike
    {
        [HarmonyPrefix]
        public static void Prefix(Pawn pawn, Hediff_Injury injury, DamageInfo dinfo)
        {
            MercyStrikeUtility.ProtectInjury(pawn, injury, dinfo);
        }

        [HarmonyPostfix]
        public static void Postfix(Pawn pawn, Hediff_Injury injury, DamageInfo dinfo)
        {
            MercyStrikeUtility.TryApplySuppression(pawn, dinfo);
        }
    }
}
