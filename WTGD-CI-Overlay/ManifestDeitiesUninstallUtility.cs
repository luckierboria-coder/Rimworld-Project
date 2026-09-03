using System;
using System.Collections.Generic;
using System.Linq;
using RimWorld;
using Verse;
using Verse.AI;

namespace ManifestDeities
{
    public sealed class ManifestDeitiesUninstallReport
    {
        public int pawnsScanned;
        public int divinePawnsRemoved;
        public int hediffsRemoved;
        public int memoriesRemoved;
        public int traitsRemoved;
        public int abilitiesRemoved;
        public int jobsInterrupted;
        public int managersRemoved;
    }

    public static class ManifestDeitiesUninstallUtility
    {
        private static readonly HashSet<JobDef> WtgdJobs = new HashSet<JobDef>();
        private static readonly HashSet<HediffDef> WtgdHediffs = new HashSet<HediffDef>();
        private static readonly HashSet<ThoughtDef> WtgdThoughts = new HashSet<ThoughtDef>();
        private static readonly HashSet<TraitDef> WtgdTraits = new HashSet<TraitDef>();
        private static readonly HashSet<AbilityDef> WtgdAbilities = new HashSet<AbilityDef>();

        public static void RequestPrepareForUninstall()
        {
            if (Current.ProgramState != ProgramState.Playing || Verse.Current.Game == null)
            {
                Messages.Message("MD_PrepareUninstallNotInGame".Translate(), MessageTypeDefOf.RejectInput);
                return;
            }

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                "MD_PrepareUninstallConfirm".Translate(),
                PrepareAndNotify,
                destructive: true));
        }

        private static void PrepareAndNotify()
        {
            ManifestDeitiesUninstallReport report;
            try
            {
                report = PrepareForUninstall();
            }
            catch (Exception ex)
            {
                Log.Error("[Manifest Deities] Prepare-for-uninstall failed: " + ex);
                Messages.Message("MD_PrepareUninstallFailed".Translate(), MessageTypeDefOf.NegativeEvent);
                return;
            }

            Find.WindowStack.Add(new Dialog_MessageBox(
                "MD_PrepareUninstallDone".Translate(
                    report.divinePawnsRemoved,
                    report.hediffsRemoved,
                    report.memoriesRemoved,
                    report.traitsRemoved,
                    report.abilitiesRemoved,
                    report.jobsInterrupted,
                    report.managersRemoved)));
        }

        public static ManifestDeitiesUninstallReport PrepareForUninstall()
        {
            ManifestDeitiesUninstallReport report = new ManifestDeitiesUninstallReport();
            Game game = Verse.Current.Game;
            if (game == null) return report;

            BuildDefSets();

            DivineFavorManager manager = game.GetComponent<DivineFavorManager>();
            manager?.PrepareForUninstall();

            List<Pawn> pawns = PawnsFinder.All_AliveOrDead
                .Where(pawn => pawn != null)
                .Distinct()
                .ToList();

            // Map corpses are not guaranteed to remain in mapPawns after death.
            foreach (Map map in Find.Maps)
            {
                foreach (Corpse corpse in map.listerThings.ThingsInGroup(ThingRequestGroup.Corpse).OfType<Corpse>())
                {
                    if (corpse?.InnerPawn != null && !pawns.Contains(corpse.InnerPawn))
                        pawns.Add(corpse.InnerPawn);
                }
            }

            report.pawnsScanned = pawns.Count;

            List<Pawn> divinePawns = new List<Pawn>();
            foreach (Pawn pawn in pawns)
            {
                bool divine = IsWtgdDivinePawn(pawn);
                CleanupPawnState(pawn, report);
                if (divine) divinePawns.Add(pawn);
            }

            foreach (Pawn pawn in divinePawns.Distinct().ToList())
            {
                if (RemoveDivinePawn(pawn)) report.divinePawnsRemoved++;
            }

            report.managersRemoved = game.components.RemoveAll(component => component is DivineFavorManager);
            return report;
        }

        private static void BuildDefSets()
        {
            WtgdJobs.Clear();
            Add(WtgdJobs, MD_DefOf.MD_InvokeDeity);
            Add(WtgdJobs, MD_DefOf.MD_BestowDivineTeaching);
            Add(WtgdJobs, MD_DefOf.MD_PerformDivineMiracle);
            Add(WtgdJobs, MD_DefOf.MD_PrayAtAltar);
            Add(WtgdJobs, MD_DefOf.MD_AttendDivineInvocation);

            WtgdHediffs.Clear();
            Add(WtgdHediffs, MD_DefOf.MD_DivineHealing);
            Add(WtgdHediffs, MD_DefOf.MD_DivineAnimalForm);
            Add(WtgdHediffs, MD_DefOf.MD_BlessingOfRenewal);
            Add(WtgdHediffs, MD_DefOf.MD_DivineWard);
            Add(WtgdHediffs, MD_DefOf.MD_DivineDispleasure);
            Add(WtgdHediffs, MD_DefOf.MD_Wrathbound);

            WtgdThoughts.Clear();
            Add(WtgdThoughts, MD_DefOf.MD_Thought_FaintDivineAnswer);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_DivineBlessing);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_WitnessedManifestation);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_WitnessedTrueGod);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_DivineInspiration);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_DivineSilence);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_DivineDispleasure);
            Add(WtgdThoughts, MD_DefOf.MD_Thought_AvatarDestroyed);

            WtgdTraits.Clear();
            Add(WtgdTraits, MD_DefOf.MD_GodAvatar);
            Add(WtgdTraits, MD_DefOf.MD_TrueGod);

            WtgdAbilities.Clear();
            Add(WtgdAbilities, MD_DefOf.MD_DivineMend);
            Add(WtgdAbilities, MD_DefOf.MD_DivineWardAbility);
            Add(WtgdAbilities, MD_DefOf.MD_DivineJudgment);
        }

        private static void Add<T>(HashSet<T> set, T def) where T : Def
        {
            if (def != null) set.Add(def);
        }

        private static bool IsWtgdDivinePawn(Pawn pawn)
        {
            if (pawn == null) return false;
            if (pawn.kindDef == MD_DefOf.MD_DivineAvatar || pawn.kindDef == MD_DefOf.MD_RealGod) return true;
            if (pawn.def?.defName == "MD_DivineAvatar" || pawn.def?.defName == "MD_RealGod") return true;
            return pawn.TryGetComp<CompDivineAvatar>() != null
                || pawn.TryGetComp<CompRealGod>() != null
                || pawn.health?.hediffSet?.HasHediff(MD_DefOf.MD_DivineAnimalForm) == true;
        }

        private static void CleanupPawnState(Pawn pawn, ManifestDeitiesUninstallReport report)
        {
            if (pawn == null) return;

            if (pawn.jobs != null)
            {
                int queuedBefore = pawn.jobs.jobQueue.Count;
                if (queuedBefore > 0)
                {
                    pawn.jobs.jobQueue.RemoveAll(pawn, job => job?.def != null && WtgdJobs.Contains(job.def));
                    report.jobsInterrupted += queuedBefore - pawn.jobs.jobQueue.Count;
                }

                if (pawn.CurJobDef != null && WtgdJobs.Contains(pawn.CurJobDef))
                {
                    pawn.jobs.EndCurrentJob(JobCondition.InterruptForced, false);
                    report.jobsInterrupted++;
                }
            }

            if (pawn.health?.hediffSet?.hediffs != null)
            {
                List<Hediff> hediffs = pawn.health.hediffSet.hediffs
                    .Where(hediff => hediff?.def != null && WtgdHediffs.Contains(hediff.def))
                    .ToList();
                foreach (Hediff hediff in hediffs)
                {
                    pawn.health.RemoveHediff(hediff);
                    report.hediffsRemoved++;
                }
            }

            MemoryThoughtHandler memories = pawn.needs?.mood?.thoughts?.memories;
            if (memories != null)
            {
                foreach (ThoughtDef thoughtDef in WtgdThoughts)
                {
                    int before = memories.Memories.Count(memory => memory?.def == thoughtDef);
                    if (before == 0) continue;
                    memories.RemoveMemoriesOfDef(thoughtDef);
                    report.memoriesRemoved += before;
                }
            }

            if (pawn.story?.traits?.allTraits != null)
            {
                List<Trait> traits = pawn.story.traits.allTraits
                    .Where(trait => trait?.def != null && WtgdTraits.Contains(trait.def))
                    .ToList();
                foreach (Trait trait in traits)
                {
                    pawn.story.traits.RemoveTrait(trait);
                    report.traitsRemoved++;
                }
            }

            if (pawn.abilities != null)
            {
                foreach (AbilityDef abilityDef in WtgdAbilities)
                {
                    if (pawn.abilities.GetAbility(abilityDef) == null) continue;
                    pawn.abilities.RemoveAbility(abilityDef);
                    report.abilitiesRemoved++;
                }
            }
        }

        private static bool RemoveDivinePawn(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed) return false;

            Corpse corpse = pawn.Corpse;
            if (corpse != null && !corpse.Destroyed)
            {
                corpse.Destroy(DestroyMode.Vanish);
                return true;
            }

            if (pawn.Spawned)
            {
                pawn.Destroy(DestroyMode.Vanish);
                return true;
            }

            if (Find.WorldPawns != null && Find.WorldPawns.Contains(pawn))
            {
                Find.WorldPawns.RemovePawn(pawn);
            }

            if (!pawn.Destroyed)
            {
                pawn.Destroy(DestroyMode.Vanish);
            }
            return true;
        }
    }
}
