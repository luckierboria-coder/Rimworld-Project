using System;
using System.Collections.Generic;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;
using Verse.AI.Group;

namespace SensibleNPCEvacuation
{
    public class SensibleNPCEvacuationSettings : ModSettings
    {
        public float searchRadius = 20f;
        public bool rescueOnDeparture = true;
        public float departureRescueChance = 1.0f;
        public bool rescueWhileFleeing = true;
        public float fleeRescueChance = 0.50f;
        public float minFleeRescuerHealth = 0.35f;
        public bool scaleFleeRadiusByHealth = true;
        public bool rescueAnimals = true;
        public bool debugLogging = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref searchRadius, "searchRadius", 20f);
            Scribe_Values.Look(ref rescueOnDeparture, "rescueOnDeparture", true);
            Scribe_Values.Look(ref departureRescueChance, "departureRescueChance", 1.0f);
            Scribe_Values.Look(ref rescueWhileFleeing, "rescueWhileFleeing", true);
            Scribe_Values.Look(ref fleeRescueChance, "fleeRescueChance", 0.50f);
            Scribe_Values.Look(ref minFleeRescuerHealth, "minFleeRescuerHealth", 0.35f);
            Scribe_Values.Look(ref scaleFleeRadiusByHealth, "scaleFleeRadiusByHealth", true);
            Scribe_Values.Look(ref rescueAnimals, "rescueAnimals", true);
            Scribe_Values.Look(ref debugLogging, "debugLogging", false);
            base.ExposeData();
        }
    }

    public class SensibleNPCEvacuationMod : Mod
    {
        public static SensibleNPCEvacuationSettings Settings;

        public SensibleNPCEvacuationMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<SensibleNPCEvacuationSettings>();
        }

        public override string SettingsCategory()
        {
            return "SNE_SettingsCategory".Translate().ToString();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            SensibleNPCEvacuationSettings settings = Settings;
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);

            listing.Label("SNE_SearchRadius".Translate(settings.searchRadius.ToString("0")), -1f, "SNE_SearchRadiusDesc".Translate().ToString());
            settings.searchRadius = Widgets.HorizontalSlider(listing.GetRect(24f), settings.searchRadius, 5f, 80f, false, null, "5", "80", 1f);
            listing.Gap(6f);

            listing.CheckboxLabeled("SNE_RescueOnDeparture".Translate().ToString(), ref settings.rescueOnDeparture, "SNE_RescueOnDepartureDesc".Translate().ToString());
            listing.Label("SNE_DepartureChance".Translate((settings.departureRescueChance * 100f).ToString("0")).ToString());
            settings.departureRescueChance = Widgets.HorizontalSlider(listing.GetRect(24f), settings.departureRescueChance, 0f, 1f, false, null, "0%", "100%", 0.05f);
            listing.Gap(8f);

            listing.CheckboxLabeled("SNE_RescueWhileFleeing".Translate().ToString(), ref settings.rescueWhileFleeing, "SNE_RescueWhileFleeingDesc".Translate().ToString());
            listing.Label("SNE_FleeChance".Translate((settings.fleeRescueChance * 100f).ToString("0")).ToString());
            settings.fleeRescueChance = Widgets.HorizontalSlider(listing.GetRect(24f), settings.fleeRescueChance, 0f, 1f, false, null, "0%", "100%", 0.05f);

            listing.Label("SNE_MinFleeHealth".Translate((settings.minFleeRescuerHealth * 100f).ToString("0")), -1f, "SNE_MinFleeHealthDesc".Translate().ToString());
            settings.minFleeRescuerHealth = Widgets.HorizontalSlider(listing.GetRect(24f), settings.minFleeRescuerHealth, 0f, 0.90f, false, null, "0%", "90%", 0.05f);

            listing.CheckboxLabeled("SNE_ScaleFleeRadius".Translate().ToString(), ref settings.scaleFleeRadiusByHealth, "SNE_ScaleFleeRadiusDesc".Translate().ToString());
            listing.CheckboxLabeled("SNE_RescueAnimals".Translate().ToString(), ref settings.rescueAnimals, "SNE_RescueAnimalsDesc".Translate().ToString());
            listing.CheckboxLabeled("SNE_DebugLogging".Translate().ToString(), ref settings.debugLogging, "SNE_DebugLoggingDesc".Translate().ToString());

            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    public static class Startup
    {
        static Startup()
        {
            Log.Message("[Sensible NPC Evacuation] Loaded for RimWorld 1.5 (friendly NOLB mode).");
        }
    }

    internal static class RescueUtility
    {
        private const float DefaultSearchRadius = 20f;

        private static SensibleNPCEvacuationSettings CurrentSettings
        {
            get
            {
                if (SensibleNPCEvacuationMod.Settings == null)
                {
                    return new SensibleNPCEvacuationSettings();
                }
                return SensibleNPCEvacuationMod.Settings;
            }
        }

        public static bool IsFriendlyNonPlayer(Pawn pawn)
        {
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Faction == null)
            {
                return false;
            }

            if (pawn.Faction.IsPlayer || pawn.Faction.HostileTo(Faction.OfPlayer))
            {
                return false;
            }

            if (pawn.IsPrisoner)
            {
                return false;
            }

            return true;
        }

        public static Job TryMakePickupJob(Pawn rescuer, bool fleeing)
        {
            if (!IsFriendlyNonPlayer(rescuer))
            {
                return null;
            }

            SensibleNPCEvacuationSettings settings = CurrentSettings;
            if (fleeing)
            {
                if (!settings.rescueWhileFleeing)
                {
                    return null;
                }
            }
            else if (!settings.rescueOnDeparture)
            {
                return null;
            }

            if (rescuer.RaceProps == null || !rescuer.RaceProps.Humanlike || rescuer.Downed)
            {
                return null;
            }

            if (rescuer.health == null || rescuer.health.capacities == null ||
                !rescuer.health.capacities.CapableOf(PawnCapacityDefOf.Manipulation))
            {
                return null;
            }

            if (rescuer.carryTracker != null && rescuer.carryTracker.CarriedThing != null)
            {
                return null;
            }

            float rescuerHealth = 1f;
            if (rescuer.health.summaryHealth != null)
            {
                rescuerHealth = rescuer.health.summaryHealth.SummaryHealthPercent;
            }

            if (fleeing && rescuerHealth < settings.minFleeRescuerHealth)
            {
                DebugLog(rescuer.LabelShort + " is too injured to attempt a rescue while fleeing.");
                return null;
            }

            float chance = fleeing ? settings.fleeRescueChance : settings.departureRescueChance;
            if (chance <= 0f || (chance < 1f && Rand.Value > chance))
            {
                return null;
            }

            Lord lord = rescuer.GetLord();
            if (lord == null || lord.ownedPawns == null)
            {
                return null;
            }

            float radius = settings.searchRadius > 0f ? settings.searchRadius : DefaultSearchRadius;
            if (fleeing && settings.scaleFleeRadiusByHealth)
            {
                float healthFactor = rescuerHealth;
                if (healthFactor < 0.25f)
                {
                    healthFactor = 0.25f;
                }
                if (healthFactor > 1f)
                {
                    healthFactor = 1f;
                }
                radius *= healthFactor;
            }

            Pawn target = FindBestDownedPawn(rescuer, lord, radius, true);
            if (target == null && settings.rescueAnimals)
            {
                target = FindBestDownedPawn(rescuer, lord, radius, false);
            }

            if (target == null)
            {
                return null;
            }

            JobDef pickupDef = DefDatabase<JobDef>.GetNamedSilentFail("SNE_PickUpDownedAlly");
            if (pickupDef == null)
            {
                Log.Error("[Sensible NPC Evacuation] Missing JobDef SNE_PickUpDownedAlly.");
                return null;
            }

            Job job = JobMaker.MakeJob(pickupDef, target);
            job.count = 1;
            job.locomotionUrgency = LocomotionUrgency.Jog;

            DebugLog(rescuer.LabelShort + " will pick up " + target.LabelShort +
                     (fleeing ? " while fleeing." : " before leaving."));
            return job;
        }

        private static Pawn FindBestDownedPawn(Pawn rescuer, Lord lord, float searchRadius, bool humanlikeOnly)
        {
            Pawn best = null;
            float bestScore = -1f;
            float maxDistSquared = searchRadius * searchRadius;

            for (int i = 0; i < lord.ownedPawns.Count; i++)
            {
                Pawn candidate = lord.ownedPawns[i];

                if (candidate == null || candidate == rescuer || !candidate.Spawned)
                {
                    continue;
                }

                if (candidate.Map != rescuer.Map || candidate.Dead || !candidate.Downed)
                {
                    continue;
                }

                if (candidate.Faction != rescuer.Faction || candidate.IsPrisoner)
                {
                    continue;
                }

                if (candidate.RaceProps == null)
                {
                    continue;
                }

                if (humanlikeOnly)
                {
                    if (!candidate.RaceProps.Humanlike)
                    {
                        continue;
                    }
                }
                else if (candidate.RaceProps.Humanlike)
                {
                    continue;
                }

                int distSquared = rescuer.Position.DistanceToSquared(candidate.Position);
                if (distSquared > maxDistSquared)
                {
                    continue;
                }

                if (!rescuer.CanReserveAndReach(candidate, PathEndMode.ClosestTouch, Danger.Some))
                {
                    continue;
                }

                float distance = rescuer.Position.DistanceTo(candidate.Position);
                float score = 1f / (distance + 1f);

                if (humanlikeOnly)
                {
                    if (rescuer.relations != null && candidate.relations != null)
                    {
                        float opinion = rescuer.relations.OpinionOf(candidate);
                        score *= 1f + ((opinion + 100f) / 200f) * 0.35f;
                    }

                    float value = candidate.MarketValue;
                    if (value < 200f)
                    {
                        value = 200f;
                    }
                    if (value > 3000f)
                    {
                        value = 3000f;
                    }
                    score *= 1f + (value - 200f) / 2800f * 0.20f;
                }

                if (best == null || score > bestScore)
                {
                    best = candidate;
                    bestScore = score;
                }
            }

            return best;
        }

        private static void DebugLog(string text)
        {
            SensibleNPCEvacuationSettings settings = CurrentSettings;
            if (settings.debugLogging)
            {
                Log.Message("[Sensible NPC Evacuation] " + text);
            }
        }
    }

    public class JobGiver_FriendlyRescue : ThinkNode_JobGiver
    {
        protected bool fleeing = false;

        public override ThinkNode DeepCopy(bool resolve = true)
        {
            JobGiver_FriendlyRescue copy = (JobGiver_FriendlyRescue)base.DeepCopy(resolve);
            copy.fleeing = fleeing;
            return copy;
        }

        protected override Job TryGiveJob(Pawn pawn)
        {
            return RescueUtility.TryMakePickupJob(pawn, fleeing);
        }
    }

    public class JobDriver_PickUpDownedAlly : JobDriver
    {
        private const TargetIndex TakeeIndex = TargetIndex.A;

        protected Pawn Takee
        {
            get { return (Pawn)job.GetTarget(TakeeIndex).Thing; }
        }

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(Takee, job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TakeeIndex);

            Toil gotoPawn = Toils_Goto.GotoThing(TakeeIndex, PathEndMode.ClosestTouch);
            gotoPawn.FailOn(new Func<bool>(delegate
            {
                return Takee == null || Takee.Dead || !Takee.Downed;
            }));
            gotoPawn.FailOnSomeonePhysicallyInteracting(TakeeIndex);
            yield return gotoPawn;

            Toil carry = Toils_Haul.StartCarryThing(TakeeIndex);
            yield return carry;
        }
    }
}
