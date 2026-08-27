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

        public override string SettingsCategory()
        {
            return "Drafted Command Priority";
        }

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

                MethodBase interruptible = AccessTools.Method(typeof(Pawn_JobTracker), "IsCurrentJobPlayerInterruptible");
                if (interruptible == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.IsCurrentJobPlayerInterruptible not found; absolute drafted player override will remain inert.");
                else
                    harmony.Patch(interruptible, postfix: new HarmonyMethod(typeof(AbsolutePlayerInterrupt), "Postfix"));

                MethodBase startJob = AccessTools.Method(typeof(Pawn_JobTracker), "StartJob");
                if (startJob == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.StartJob not found; strict command guard will remain inert.");
                else
                    harmony.Patch(startJob, prefix: new HarmonyMethod(typeof(DraftedOrderGuard), "Prefix"));

                MethodBase jobTrackerTick = AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick");
                if (jobTrackerTick == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.JobTrackerTick not found; melee auto attack will remain inert.");
                else
                    harmony.Patch(jobTrackerTick, postfix: new HarmonyMethod(typeof(MeleeAutoAttack), "Postfix"));

                MethodBase pawnGetGizmos = AccessTools.Method(typeof(Pawn), "GetGizmos");
                if (pawnGetGizmos == null)
                    Log.Error("[Drafted Command Priority] Pawn.GetGizmos not found; melee auto attack toggle gizmo will remain inert.");
                else
                    harmony.Patch(pawnGetGizmos, postfix: new HarmonyMethod(typeof(MeleeAutoAttackGizmo), "Postfix"));

                Log.Message("[Drafted Command Priority] V0.1 active. Drafted player orders have absolute job priority while the pawn remains player-controllable, including while burning. Player command chains remain authoritative until completion; autonomous AI and melee auto attack resume only afterwards.");
            }
            catch (Exception ex)
            {
                Log.Error("[Drafted Command Priority] Failed to install patches. " + ex);
            }
        }
    }

    internal static class DcpControlRules
    {
        internal static bool HasAbsolutePlayerControl(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.Drafted && !pawn.Downed && !pawn.InMentalState && pawn.IsColonistPlayerControlled;
        }
    }

    internal static class AbsolutePlayerInterrupt
    {
        public static void Postfix(Pawn ___pawn, ref bool __result)
        {
            if (__result)
                return;

            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled)
                return;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
                return;

            // Vanilla deliberately makes a burning pawn non-player-interruptible by
            // checking !pawn.HasAttachment(ThingDefOf.Fire). DCP overrides that rule,
            // and also any ordinary job-level playerInterruptible=false state, while
            // the pawn is drafted and still genuinely player-controllable.
            // Physical hard control (downed/mental/uncontrollable) remains outside DCP.
            __result = true;
        }
    }

    internal static class PlayerCommandGate
    {
        private static readonly HashSet<int> activePawnIds = new HashSet<int>();

        internal static void Mark(Pawn pawn)
        {
            if (pawn != null)
                activePawnIds.Add(pawn.thingIDNumber);
        }

        internal static void Clear(Pawn pawn)
        {
            if (pawn != null)
                activePawnIds.Remove(pawn.thingIDNumber);
        }

        internal static bool IsActive(Pawn pawn)
        {
            return pawn != null && activePawnIds.Contains(pawn.thingIDNumber);
        }

        internal static bool HasQueuedPlayerOrder(Pawn_JobTracker tracker)
        {
            return tracker != null && tracker.jobQueue != null && tracker.jobQueue.AnyPlayerForced;
        }

        internal static bool IsIdleOrDraftWait(Pawn_JobTracker tracker)
        {
            if (tracker == null || tracker.curJob == null)
                return true;

            return tracker.curJob.def == JobDefOf.Wait_Combat;
        }

        internal static bool ReleaseIfCommandFinished(Pawn pawn, Pawn_JobTracker tracker)
        {
            if (pawn == null || tracker == null)
                return false;

            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
            {
                Clear(pawn);
                return true;
            }

            if (HasQueuedPlayerOrder(tracker))
                return false;

            if (IsIdleOrDraftWait(tracker))
            {
                Clear(pawn);
                return true;
            }

            return !IsActive(pawn);
        }
    }

    internal static class DraftedOrderGuard
    {
        private static long blockedJobs;
        internal static long BlockedJobs
        {
            get { return Interlocked.Read(ref blockedJobs); }
        }

        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn, Job newJob)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null || newJob == null)
                return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
            {
                PlayerCommandGate.Clear(pawn);
                return true;
            }

            // Any explicit player command starts/refreshes the command gate.
            if (newJob.playerForced)
            {
                PlayerCommandGate.Mark(pawn);
                return true;
            }

            Job current = __instance.curJob;

            // Recover the gate after loading a save or if another mod created the
            // player-forced job before DCP observed its StartJob call.
            if (current != null && current.playerForced)
                PlayerCommandGate.Mark(pawn);

            if (!PlayerCommandGate.IsActive(pawn))
                return true;

            // While a player command chain is active, ALL non-player StartJob attempts
            // are rejected: fire panic/extinguish, flee, ThinkTree AI, auto melee, etc.
            // AI is released only after the player chain genuinely reaches drafted idle.
            if (PlayerCommandGate.ReleaseIfCommandFinished(pawn, __instance))
                return true;

            Interlocked.Increment(ref blockedJobs);
            if (settings.logBlockedJobs)
            {
                string pawnLabel = pawn.LabelShortCap;
                string currentDef = current == null || current.def == null ? "<null>" : current.def.defName;
                string incomingDef = newJob.def == null ? "<null>" : newJob.def.defName;
                Log.Message("[Drafted Command Priority] Blocked autonomous StartJob during active player command chain for " + pawnLabel +
                    ": incoming=" + incomingDef + ", current=" + currentDef + ".");
            }

            return false;
        }
    }

    internal static class MeleeAutoAttack
    {
        private const int ScanIntervalTicks = 15;
        private static long autoAttackJobs;
        internal static long AutoAttackJobs
        {
            get { return Interlocked.Read(ref autoAttackJobs); }
        }

        public static void Postfix(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || !settings.meleeAutoAttack || __instance == null)
                return;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn) || pawn.Map == null)
            {
                PlayerCommandGate.Clear(pawn);
                return;
            }

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon)
                return;

            if (pawn.drafter == null || !pawn.drafter.FireAtWill)
                return;

            // Auto melee may not participate until the entire player command chain has
            // completed. A new player order always takes control back immediately.
            if (PlayerCommandGate.IsActive(pawn) && !PlayerCommandGate.ReleaseIfCommandFinished(pawn, __instance))
                return;

            Job current = __instance.curJob;
            if (current != null && current.def != JobDefOf.Wait_Combat)
                return;

            if (PlayerCommandGate.HasQueuedPlayerOrder(__instance))
                return;

            int ticks = Find.TickManager == null ? 0 : Find.TickManager.TicksGame;
            if ((ticks + pawn.thingIDNumber) % ScanIntervalTicks != 0)
                return;

            float radius = Mathf.Clamp(settings.meleeAutoAttackRadius, 1f, 20f);
            float maxDistSq = radius * radius;
            Pawn nearest = null;
            float nearestDistSq = float.MaxValue;

            var pawns = pawn.Map.mapPawns.AllPawnsSpawned;
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

            if (!pawn.CanReach(nearest, PathEndMode.Touch, Danger.Deadly))
                return;

            current = __instance.curJob;
            if ((current != null && current.def != JobDefOf.Wait_Combat) ||
                PlayerCommandGate.IsActive(pawn) || PlayerCommandGate.HasQueuedPlayerOrder(__instance))
                return;

            Job attack = JobMaker.MakeJob(JobDefOf.AttackMelee, nearest);
            attack.playerForced = false;
            attack.expiryInterval = 120;
            attack.checkOverrideOnExpire = true;
            __instance.StartJob(attack, JobCondition.InterruptOptional);
            Interlocked.Increment(ref autoAttackJobs);
        }
    }

    internal static class MeleeAutoAttackGizmo
    {
        public static void Postfix(Pawn __instance, ref IEnumerable<Gizmo> __result)
        {
            if (__result == null)
                return;

            __result = Append(__result, __instance);
        }

        private static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> original, Pawn pawn)
        {
            foreach (Gizmo gizmo in original)
                yield return gizmo;

            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || !settings.meleeAutoAttack)
                yield break;

            if (!DcpControlRules.HasAbsolutePlayerControl(pawn) || pawn.drafter == null)
                yield break;

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon)
                yield break;

            Command_Toggle toggle = new Command_Toggle();
            toggle.hotKey = KeyBindingDefOf.Misc6;
            toggle.isActive = delegate { return pawn.drafter != null && pawn.drafter.FireAtWill; };
            toggle.toggleAction = delegate
            {
                if (pawn.drafter != null)
                    pawn.drafter.FireAtWill = !pawn.drafter.FireAtWill;
            };
            toggle.icon = TexCommand.AttackMelee;
            toggle.defaultLabel = "DCP_MeleeAutoAttackToggleLabel".Translate();
            toggle.defaultDesc = "DCP_MeleeAutoAttackToggleDesc".Translate();
            toggle.tutorTag = "DCP_MeleeAutoAttackToggle";
            yield return toggle;
        }
    }
}
