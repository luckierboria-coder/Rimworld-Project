using System;
using System.Collections.Generic;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.CombatStandsNPCSmartAI15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            Log.Message("[Combat Stands NPC Smart AI] V1.0 loaded for RimWorld 1.5. NPC stance evaluation is melee-only, low-frequency and Vanilla-authoritative at cast time.");
        }
    }

    internal enum StanceKind
    {
        Endure,
        Swing,
        Berserker,
        Precise,
        Tower,
        Fencer
    }

    internal sealed class StanceEntry
    {
        public readonly StanceKind Kind;
        public readonly string DefName;
        public AbilityDef AbilityDef;
        public HediffDef HediffDef;

        public StanceEntry(StanceKind kind, string defName)
        {
            Kind = kind;
            DefName = defName;
        }
    }

    internal static class StanceCatalog
    {
        public static readonly StanceEntry[] Entries =
        {
            new StanceEntry(StanceKind.Endure, "Seti_Ability_Endure"),
            new StanceEntry(StanceKind.Swing, "Seti_Ability_Swing"),
            new StanceEntry(StanceKind.Berserker, "Seti_Ability_Berserker"),
            new StanceEntry(StanceKind.Precise, "Seti_Ability_Precise"),
            new StanceEntry(StanceKind.Tower, "Seti_Ability_Tower"),
            new StanceEntry(StanceKind.Fencer, "Seti_Ability_Fencer")
        };

        private static bool resolved;

        public static bool EnsureResolved()
        {
            if (resolved)
            {
                return true;
            }

            bool any = false;
            for (int i = 0; i < Entries.Length; i++)
            {
                StanceEntry entry = Entries[i];
                entry.AbilityDef = DefDatabase<AbilityDef>.GetNamedSilentFail(entry.DefName);
                entry.HediffDef = DefDatabase<HediffDef>.GetNamedSilentFail(entry.DefName);
                any |= entry.AbilityDef != null;
            }

            resolved = any;
            return any;
        }
    }

    public sealed class CombatStandsSmartAIMapComponent : MapComponent
    {
        private const int EvaluationIntervalTicks = 120;
        private const float LocalBattleRadius = 6f;
        private const float MinimumUseScore = 38f;
        private const int CastLogLimitPerMap = 80;

        private int castLogCount;

        public CombatStandsSmartAIMapComponent(Map map) : base(map)
        {
        }

        public override void MapComponentTick()
        {
            base.MapComponentTick();

            int tick = Find.TickManager.TicksGame;
            if (tick % EvaluationIntervalTicks != 0)
            {
                return;
            }

            if (!StanceCatalog.EnsureResolved())
            {
                return;
            }

            List<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn pawn = pawns[i];
                if (!EligibleNPC(pawn))
                {
                    continue;
                }

                TryChooseAndCast(pawn, pawns, tick);
            }
        }

        private static bool EligibleNPC(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Dead || pawn.Downed)
            {
                return false;
            }

            if (pawn.Faction == Faction.OfPlayer)
            {
                return false;
            }

            if (pawn.RaceProps == null || !pawn.RaceProps.Humanlike)
            {
                return false;
            }

            if (pawn.InMentalState || pawn.abilities == null || pawn.skills == null || pawn.health == null)
            {
                return false;
            }

            return true;
        }

        private void TryChooseAndCast(Pawn pawn, List<Pawn> mapPawns, int tick)
        {
            if (HasActiveStance(pawn))
            {
                return;
            }

            Thing target = ResolveCombatTarget(pawn);
            if (!ValidHostileTarget(pawn, target))
            {
                return;
            }

            float distance = pawn.Position.DistanceTo(target.Position);
            if (!JobActuallyEngagesTarget(pawn, target, distance))
            {
                return;
            }

            Verb combatVerb = pawn.CurJob?.verbToUse;
            if (combatVerb == null)
            {
                combatVerb = pawn.TryGetAttackVerb(target, allowManualCastWeapons: true);
            }

            // Every Combat Stands stance applies ShootingAccuracyPawn -10.
            // Never activate one for an NPC whose actual current attack verb is ranged.
            if (combatVerb == null || !combatVerb.IsMeleeAttack)
            {
                return;
            }

            int meleeSkill = pawn.skills.GetSkill(SkillDefOf.Melee)?.Level ?? 0;
            float ownHealth = Clamp01(pawn.health.summaryHealth.SummaryHealthPercent);
            float targetHealth = 1f;
            Pawn targetPawn = target as Pawn;
            if (targetPawn != null && targetPawn.health != null)
            {
                targetHealth = Clamp01(targetPawn.health.summaryHealth.SummaryHealthPercent);
            }

            float targetArmor = SafeArmor(target);
            float ownArmor = SafeArmor(pawn);

            int nearbyEnemies;
            int nearbyAllies;
            CountLocalBattle(pawn, mapPawns, out nearbyEnemies, out nearbyAllies);
            int pressure = Math.Max(0, nearbyEnemies - Math.Max(1, nearbyAllies));

            Ability bestAbility = null;
            StanceEntry bestEntry = null;
            float bestScore = float.MinValue;

            for (int i = 0; i < StanceCatalog.Entries.Length; i++)
            {
                StanceEntry entry = StanceCatalog.Entries[i];
                if (entry.AbilityDef == null)
                {
                    continue;
                }

                Ability ability = pawn.abilities.GetAbility(entry.AbilityDef, includeTemporary: false);
                if (ability == null || !ability.CanQueueCast)
                {
                    continue;
                }

                float score = ScoreStance(entry.Kind, ownHealth, targetHealth, targetArmor, ownArmor,
                    distance, meleeSkill, nearbyEnemies, nearbyAllies, pressure, targetPawn != null);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestAbility = ability;
                    bestEntry = entry;
                }
            }

            if (bestAbility == null || bestEntry == null || bestScore < MinimumUseScore)
            {
                return;
            }

            // QueueCastingJob performs the vanilla CanQueueCast + CanApplyOn authority checks again.
            // Combat Stands marks these abilities as nonInterruptingSelfCast, so this does not replace
            // or cancel the NPC's current melee job.
            LocalTargetInfo self = new LocalTargetInfo(pawn);
            bestAbility.QueueCastingJob(self, self);

            if (castLogCount < CastLogLimitPerMap)
            {
                castLogCount++;
                Log.Message("[Combat Stands NPC Smart AI] " + pawn.LabelShortCap + " chose " + bestEntry.Kind +
                    " score=" + bestScore.ToString("0.0") +
                    " hp=" + ownHealth.ToString("0.00") +
                    " target=" + target.LabelShortCap +
                    " dist=" + distance.ToString("0.0") +
                    " local=" + nearbyAllies + "A/" + nearbyEnemies + "E" +
                    " tick=" + tick + ".");
            }
        }

        private static bool HasActiveStance(Pawn pawn)
        {
            HediffSet set = pawn.health?.hediffSet;
            if (set == null)
            {
                return false;
            }

            for (int i = 0; i < StanceCatalog.Entries.Length; i++)
            {
                HediffDef def = StanceCatalog.Entries[i].HediffDef;
                if (def != null && set.HasHediff(def))
                {
                    return true;
                }
            }

            return false;
        }

        private static Thing ResolveCombatTarget(Pawn pawn)
        {
            Thing enemy = pawn.mindState?.enemyTarget;
            if (ValidHostileTarget(pawn, enemy))
            {
                return enemy;
            }

            Job job = pawn.CurJob;
            if (job == null)
            {
                return null;
            }

            Thing a = job.targetA.Thing;
            if (ValidHostileTarget(pawn, a))
            {
                return a;
            }

            Thing b = job.targetB.Thing;
            if (ValidHostileTarget(pawn, b))
            {
                return b;
            }

            Thing c = job.targetC.Thing;
            if (ValidHostileTarget(pawn, c))
            {
                return c;
            }

            return null;
        }

        private static bool ValidHostileTarget(Pawn pawn, Thing target)
        {
            return target != null && !target.Destroyed && target.Spawned && target.Map == pawn.Map && target.HostileTo(pawn);
        }

        private static bool JobActuallyEngagesTarget(Pawn pawn, Thing target, float distance)
        {
            Job job = pawn.CurJob;
            if (job == null)
            {
                return false;
            }

            if (job.targetA.Thing == target || job.targetB.Thing == target || job.targetC.Thing == target)
            {
                return true;
            }

            // Some combat think trees retain enemyTarget while using a cell-target movement job.
            // Only accept that fallback when the pawn is already in immediate melee contact.
            return distance <= 2.2f;
        }

        private static void CountLocalBattle(Pawn pawn, List<Pawn> pawns, out int enemies, out int allies)
        {
            enemies = 0;
            allies = 0;
            float radiusSq = LocalBattleRadius * LocalBattleRadius;
            IntVec3 pos = pawn.Position;

            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn other = pawns[i];
                if (other == null || other == pawn || other.Dead || other.Downed || !other.Spawned)
                {
                    continue;
                }

                IntVec3 d = other.Position - pos;
                if (d.LengthHorizontalSquared > radiusSq)
                {
                    continue;
                }

                if (other.HostileTo(pawn))
                {
                    enemies++;
                }
                else if (other.Faction != null && other.Faction == pawn.Faction)
                {
                    allies++;
                }
            }
        }

        private static float ScoreStance(StanceKind kind, float hp, float targetHp, float targetArmor,
            float ownArmor, float distance, int meleeSkill, int nearbyEnemies, int nearbyAllies,
            int pressure, bool targetIsPawn)
        {
            bool engaged = distance <= 2.2f;
            bool chasing = distance > 2.2f && distance <= 9f;
            bool healthy = hp >= 0.75f;
            bool wounded = hp < 0.60f;
            bool critical = hp < 0.38f;
            bool oneOnOne = nearbyEnemies <= 1 && nearbyAllies <= 1;

            switch (kind)
            {
                case StanceKind.Endure:
                {
                    float score = 18f;
                    score += (1f - hp) * 36f;
                    score += Math.Min(pressure, 3) * 9f;
                    if (engaged) score += 6f;
                    if (critical) score += 8f;
                    if (chasing) score -= 7f;
                    return score;
                }

                case StanceKind.Swing:
                {
                    float score = 24f;
                    if (healthy) score += 8f;
                    if (engaged) score += 6f;
                    if (oneOnOne) score += 7f;
                    score += (1f - targetHp) * 5f;
                    if (wounded) score -= 10f;
                    score -= Math.Min(pressure, 3) * 7f;
                    return score;
                }

                case StanceKind.Berserker:
                {
                    float score = 17f;
                    score += hp * 15f;
                    if (chasing) score += 13f;
                    if (targetHp < 0.55f) score += 10f;
                    if (oneOnOne) score += 5f;
                    score += ownArmor * 5f;
                    score -= Math.Min(pressure, 3) * 12f;
                    score -= (1f - hp) * 18f;
                    if (critical) score -= 15f;
                    return score;
                }

                case StanceKind.Precise:
                {
                    float score = 19f;
                    score += targetArmor * 38f;
                    score += Math.Max(0, meleeSkill - 12) * 1.6f;
                    if (targetIsPawn && targetHp > 0.70f) score += 5f;
                    if (engaged) score += 3f;
                    if (wounded) score -= 7f;
                    score -= Math.Min(pressure, 3) * 4f;
                    return score;
                }

                case StanceKind.Tower:
                {
                    float score = 10f;
                    score += (1f - hp) * 55f;
                    score += Math.Min(pressure, 3) * 15f;
                    if (engaged) score += 11f;
                    if (critical) score += 15f;
                    if (healthy && pressure == 0) score -= 12f;
                    if (distance > 3f) score -= 22f;
                    return score;
                }

                case StanceKind.Fencer:
                {
                    float score = 22f;
                    score += Math.Max(0, meleeSkill - 16) * 4f;
                    score += targetArmor * 14f;
                    if (chasing) score += 11f;
                    if (engaged) score += 5f;
                    if (pressure == 1) score += 5f;
                    if (pressure >= 3) score -= 9f;
                    if (critical) score -= 8f;
                    return score;
                }

                default:
                    return 0f;
            }
        }

        private static float SafeArmor(Thing thing)
        {
            try
            {
                return Clamp01(thing.GetStatValue(StatDefOf.ArmorRating_Sharp, applyPostProcess: true));
            }
            catch
            {
                return 0f;
            }
        }

        private static float Clamp01(float value)
        {
            if (value < 0f) return 0f;
            if (value > 1f) return 1f;
            return value;
        }
    }
}
