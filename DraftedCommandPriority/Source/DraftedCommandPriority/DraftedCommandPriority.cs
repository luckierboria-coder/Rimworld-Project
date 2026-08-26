using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DraftedCommandPriority
{
    public sealed class DcpSettings : ModSettings
    {
        public bool enabled = true;
        public bool meleeAutoAttack = true;
        public float meleeAutoAttackRadius = 4f;
        public bool logBlockedJobs = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref meleeAutoAttack, "meleeAutoAttack", true);
            Scribe_Values.Look(ref meleeAutoAttackRadius, "meleeAutoAttackRadius", 4f);
            Scribe_Values.Look(ref logBlockedJobs, "logBlockedJobs", false);
            meleeAutoAttackRadius = Mathf.Clamp(meleeAutoAttackRadius, 1f, 20f);
            base.ExposeData();
        }
    }

    public sealed class DcpMod : Mod
    {
        internal static DcpSettings Settings;

        public DcpMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<DcpSettings>();
        }

        public override string SettingsCategory() => "Drafted Command Priority";

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Listing_Standard listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.Label("DCP_Intro".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("DCP_Enable".Translate(), ref Settings.enabled, "DCP_EnableDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("DCP_MeleeAutoAttack".Translate(), ref Settings.meleeAutoAttack, "DCP_MeleeAutoAttackDesc".Translate());
            listing.Label("DCP_MeleeAutoAttackRadius".Translate(Settings.meleeAutoAttackRadius.ToString("F0")));
            Settings.meleeAutoAttackRadius = Mathf.Round(listing.Slider(Settings.meleeAutoAttackRadius, 1f, 20f));
            listing.Label("DCP_MeleeAutoAttackRadiusDesc".Translate());
            listing.GapLine();
            listing.CheckboxLabeled("DCP_LogBlocked".Translate(), ref Settings.logBlockedJobs, "DCP_LogBlockedDesc".Translate());
            listing.GapLine();
            listing.Label("DCP_BlockedCount".Translate(DraftedOrderGuard.BlockedJobs));
            listing.Label("DCP_AutoAttackCount".Translate(MeleeAutoAttack.AutoAttackJobs));
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    internal static class DcpBootstrap
    {
        private const string HarmonyId = "allen.draftedcommandpriority";

        static DcpBootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                MethodBase startJob = AccessTools.Method(typeof(Pawn_JobTracker), "StartJob");
                if (startJob == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.StartJob not found; strict command guard will remain inert.");
                else
                    harmony.Patch(startJob, prefix: new HarmonyMethod(typeof(DraftedOrderGuard), nameof(DraftedOrderGuard.Prefix)));

                MethodBase jobTrackerTick = AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick");
                if (jobTrackerTick == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.JobTrackerTick not found; melee auto attack will remain inert.");
                else
                    harmony.Patch(jobTrackerTick, postfix: new HarmonyMethod(typeof(MeleeAutoAttack), nameof(MeleeAutoAttack.Postfix)));

                Log.Message("[Drafted Command Priority] V0.1 active. Player-forced drafted jobs have strict priority; optional drafted melee auto attack is enabled by settings with a configurable 1-20 cell radius (default 4). Explicit player-forced jobs always suppress auto attack.");
            }
            catch (Exception ex)
            {
                Log.Error("[Drafted Command Priority] Failed to install patches. " + ex);
            }
        }
    }

    internal static class DraftedOrderGuard
    {
        private static long blockedJobs;
        internal static long BlockedJobs => Interlocked.Read(ref blockedJobs);

        public static bool Prefix(Pawn_JobTracker __instance, Job newJob)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null || newJob == null)
                return true;

            Pawn pawn = __instance.pawn;
            if (pawn == null || !pawn.Drafted || pawn.Downed || pawn.InMentalState)
                return true;

            if (!pawn.IsColonistPlayerControlled)
                return true;

            Job current = __instance.curJob;
            if (current == null || !current.playerForced)
                return true;

            if (newJob.playerForced)
                return true;

            Interlocked.Increment(ref blockedJobs);
            if (settings.logBlockedJobs)
            {
                string pawnLabel = pawn.LabelShortCap;
                string currentDef = current.def == null ? "<null>" : current.def.defName;
                string incomingDef = newJob.def == null ? "<null>" : newJob.def.defName;
                Log.Message("[Drafted Command Priority] Blocked autonomous StartJob for " + pawnLabel +
                    ": incoming=" + incomingDef + ", currentForced=" + currentDef + ".");
            }

            return false;
        }
    }

    internal static class MeleeAutoAttack
    {
        private const int ScanIntervalTicks = 15;
        private static long autoAttackJobs;
        internal static long AutoAttackJobs => Interlocked.Read(ref autoAttackJobs);

        public static void Postfix(Pawn_JobTracker __instance)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || !settings.meleeAutoAttack || __instance == null)
                return;

            Pawn pawn = __instance.pawn;
            if (pawn == null || !pawn.Spawned || pawn.Map == null || !pawn.Drafted || pawn.Downed || pawn.InMentalState || !pawn.IsColonistPlayerControlled)
                return;

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon)
                return;

            Job current = __instance.curJob;
            if (current != null && current.playerForced)
                return;

            int ticks = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if ((ticks + pawn.thingIDNumber) % ScanIntervalTicks != 0)
                return;

            float radius = Mathf.Clamp(settings.meleeAutoAttackRadius, 1f, 20f);
            float maxDistSq = radius * radius;
            Pawn nearest = null;
            float nearestDistSq = float.MaxValue;

            List<Pawn> pawns = pawn.Map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn other = pawns[i];
                if (other == null || other == pawn || other.Dead || other.Downed || !other.Spawned || !other.HostileTo(pawn))
                    continue;

                float distSq = pawn.Position.DistanceToSquared(other.Position);
                if (distSq > maxDistSq || distSq >= nearestDistSq)
                    continue;

                nearest = other;
                nearestDistSq = distSq;
            }

            if (nearest == null)
                return;

            if (current != null && current.def == JobDefOf.AttackMelee && current.targetA.HasThing && current.targetA.Thing == nearest)
                return;

            // One reachability check only after the cheap nearest-hostile scan. This keeps
            // the feature from becoming another GenClosest/CanReach fan-out hotspot.
            if (!pawn.CanReach(nearest, PathEndMode.Touch, Danger.Deadly))
                return;

            Job attack = JobMaker.MakeJob(JobDefOf.AttackMelee, nearest);
            attack.playerForced = false;
            attack.expiryInterval = 120;
            attack.checkOverrideOnExpire = true;
            __instance.StartJob(attack, JobCondition.InterruptOptional);
            Interlocked.Increment(ref autoAttackJobs);
        }
    }
}
