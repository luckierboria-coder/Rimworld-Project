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

    public sealed class DcpGameState : GameComponent
    {
        private Dictionary<int, bool> meleeAutoAttackByPawn = new Dictionary<int, bool>();
        public DcpGameState(Game game) { }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref meleeAutoAttackByPawn, "meleeAutoAttackByPawn", LookMode.Value, LookMode.Value);
            if (meleeAutoAttackByPawn == null) meleeAutoAttackByPawn = new Dictionary<int, bool>();
            base.ExposeData();
        }

        internal bool GetMeleeAutoAttack(Pawn pawn)
        {
            if (pawn == null) return true;
            bool value;
            return meleeAutoAttackByPawn.TryGetValue(pawn.thingIDNumber, out value) ? value : true;
        }

        internal void SetMeleeAutoAttack(Pawn pawn, bool value)
        {
            if (pawn != null) meleeAutoAttackByPawn[pawn.thingIDNumber] = value;
        }
    }

    public sealed class DcpMod : Mod
    {
        internal static DcpSettings Settings;
        public DcpMod(ModContentPack content) : base(content) { Settings = GetSettings<DcpSettings>(); }
        public override string SettingsCategory() { return "Drafted Command Priority"; }

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
            listing.Label("Absolute player takeovers: " + PlayerOrderTakeover.Takeovers);
            listing.Label("Blocked AI StartJob calls: " + StartJobAuthorityGate.BlockedStarts);
            listing.Label("Blocked ThinkTree jobs: " + ThinkTreeCommandGate.BlockedJobs);
            listing.Label("DCP_AutoAttackCount".Translate(MeleeAutoAttack.AutoAttackJobs));
            listing.End();
        }
    }

    [StaticConstructorOnStartup]
    internal static class DcpBootstrap
    {
        internal const string HarmonyId = "allen.draftedcommandpriority";

        static DcpBootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);

                Patch(harmony, typeof(Pawn_JobTracker), "TryTakeOrderedJob", typeof(PlayerOrderTakeover), "Prefix", true);
                Patch(harmony, typeof(Pawn_JobTracker), "IsCurrentJobPlayerInterruptible", typeof(InterruptibilityOverride), "Prefix", true);
                Patch(harmony, typeof(Pawn_JobTracker), "StartJob", typeof(StartJobAuthorityGate), "Prefix", true);
                Patch(harmony, typeof(Pawn_JobTracker), "ShouldStartJobFromThinkTree", typeof(ThinkTreeCommandGate), "Prefix", true);
                Patch(harmony, typeof(Pawn_JobTracker), "TryFindAndStartJob", typeof(AiHandoffGate), "Prefix", true);
                Patch(harmony, typeof(Pawn_JobTracker), "JobTrackerTick", typeof(MeleeAutoAttack), "Postfix", false);
                Patch(harmony, typeof(Pawn), "GetGizmos", typeof(MeleeAutoAttackGizmo), "Postfix", false);

                Log.Message("[Drafted Command Priority] V0.2 Absolute Player Authority active. Player orders forcibly take over every interruptible game state/job except hard incapacity (dead/downed/unspawned flyer-knockback/stun). Player-forced chains remain authoritative until current+queued player jobs are exhausted.");
            }
            catch (Exception ex)
            {
                Log.Error("[Drafted Command Priority] Failed to install V0.2 patches. " + ex);
            }
        }

        private static void Patch(Harmony harmony, Type targetType, string targetName, Type patchType, string patchName, bool prefix)
        {
            MethodBase target = AccessTools.Method(targetType, targetName);
            if (target == null)
            {
                Log.Error("[Drafted Command Priority] Missing target: " + targetType.FullName + "." + targetName);
                return;
            }

            HarmonyMethod patch = new HarmonyMethod(patchType, patchName);
            patch.priority = Priority.First + 200;
            if (prefix) harmony.Patch(target, prefix: patch); else harmony.Patch(target, postfix: patch);
        }
    }

    internal static class DcpControlRules
    {
        internal static bool IsPlayerFactionPawn(Pawn pawn)
        {
            return pawn != null && pawn.Faction == Faction.OfPlayer;
        }

        internal static bool IsHardControlled(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || !pawn.Spawned)
                return true;

            if (pawn.stances != null && pawn.stances.stunner != null && pawn.stances.stunner.Stunned)
                return true;

            // Vanilla jump/knockback uses PawnFlyer and temporarily despawns the pawn;
            // !Spawned above deliberately treats that as a hard-control interval.
            return false;
        }

        internal static bool CanPlayerTakeOver(Pawn pawn)
        {
            return IsPlayerFactionPawn(pawn) && !IsHardControlled(pawn);
        }
    }

    internal static class PlayerCommandGate
    {
        private static readonly HashSet<int> Active = new HashSet<int>();

        internal static void Mark(Pawn pawn)
        {
            if (pawn != null) Active.Add(pawn.thingIDNumber);
        }

        internal static void Clear(Pawn pawn)
        {
            if (pawn != null) Active.Remove(pawn.thingIDNumber);
        }

        internal static bool HasQueuedPlayerOrder(Pawn_JobTracker tracker)
        {
            return tracker != null && tracker.jobQueue != null && tracker.jobQueue.AnyPlayerForced;
        }

        internal static bool HasLivePlayerChain(Pawn pawn, Pawn_JobTracker tracker)
        {
            if (pawn == null || tracker == null) return false;
            if (tracker.curJob != null && tracker.curJob.playerForced) return true;
            if (HasQueuedPlayerOrder(tracker)) return true;
            if (Active.Contains(pawn.thingIDNumber)) Active.Remove(pawn.thingIDNumber);
            return false;
        }
    }

    internal static class PlayerOrderTakeover
    {
        private static long takeovers;
        internal static long Takeovers { get { return Interlocked.Read(ref takeovers); } }

        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn, Job job, JobTag? tag, bool requestQueueing, ref bool __result)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null || job == null)
                return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.IsPlayerFactionPawn(pawn) || DcpControlRules.IsHardControlled(pawn))
                return true;

            job.playerForced = true;

            if (__instance.curJob != null && __instance.curJob.JobIsSameAs(pawn, job))
            {
                PlayerCommandGate.Mark(pawn);
                __result = true;
                return false;
            }

            bool queueRequested = requestQueueing || KeyBindingDefOf.QueueOrder.IsDownEvent;
            bool livePlayerChain = PlayerCommandGate.HasLivePlayerChain(pawn, __instance);

            // Shift only queues behind an existing player-command chain. If AI currently owns
            // the pawn, even a Shift order becomes the first authoritative player command.
            if (queueRequested && livePlayerChain)
            {
                if (!job.TryMakePreToilReservations(pawn, true))
                {
                    pawn.ClearReservationsForJob(job);
                    __result = false;
                    return false;
                }

                __instance.jobQueue.EnqueueLast(job, tag);
                PlayerCommandGate.Mark(pawn);
                __result = true;
                return false;
            }

            if (__instance.curJob != null)
                __instance.curJob.playerInterruptedForced = true;

            // Absolute takeover: old AI/old command queue is discarded. Busy combat warmups,
            // flee/fire jobs, hauling, rescue, work, etc. do not get veto power here.
            __instance.ClearQueuedJobs();
            if (!job.TryMakePreToilReservations(pawn, true))
            {
                pawn.ClearReservationsForJob(job);
                __result = false;
                return false;
            }

            __instance.jobQueue.EnqueueFirst(job, tag);
            PlayerCommandGate.Mark(pawn);
            pawn.stances?.CancelBusyStanceHard();

            if (__instance.curJob != null)
            {
                if (__instance.curDriver != null)
                    __instance.curDriver.EndJobWith(JobCondition.InterruptForced);
                else
                    __instance.EndCurrentJob(JobCondition.InterruptForced);
            }
            else
            {
                __instance.CheckForJobOverride_NewTemp(ignoreQueue: false);
            }

            Interlocked.Increment(ref takeovers);
            __result = true;
            return false;
        }
    }

    internal static class InterruptibilityOverride
    {
        public static bool Prefix(Pawn ___pawn, ref bool __result)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled) return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.IsPlayerFactionPawn(pawn) || DcpControlRules.IsHardControlled(pawn))
                return true;

            // Removes burning, JobDriver.PlayerInterruptable and job.def.playerInterruptible
            // as blockers for player-issued commands. TryTakeOrderedJob also bypasses
            // forceCompleteBeforeNextJob.
            __result = true;
            return false;
        }
    }

    internal static class StartJobAuthorityGate
    {
        private static long blockedStarts;
        internal static long BlockedStarts { get { return Interlocked.Read(ref blockedStarts); } }

        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn, Job newJob)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null || newJob == null)
                return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.IsPlayerFactionPawn(pawn))
                return true;

            if (newJob.playerForced)
            {
                PlayerCommandGate.Mark(pawn);
                return true;
            }

            if (DcpControlRules.IsHardControlled(pawn))
                return true;

            if (!PlayerCommandGate.HasLivePlayerChain(pawn, __instance))
                return true;

            Interlocked.Increment(ref blockedStarts);
            if (settings.logBlockedJobs)
                Log.Message("[Drafted Command Priority] Blocked non-player StartJob while player chain owns " + pawn + ": " + newJob);
            return false;
        }
    }

    internal static class ThinkTreeCommandGate
    {
        private static long blockedJobs;
        internal static long BlockedJobs { get { return Interlocked.Read(ref blockedJobs); } }

        public static bool Prefix(Pawn_JobTracker __instance, Pawn ___pawn, ThinkResult thinkResult, ref bool __result)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null)
                return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.IsPlayerFactionPawn(pawn) || DcpControlRules.IsHardControlled(pawn))
                return true;

            if (!PlayerCommandGate.HasLivePlayerChain(pawn, __instance))
                return true;

            if (!thinkResult.IsValid || thinkResult.Job == null || thinkResult.Job.playerForced)
                return true;

            Interlocked.Increment(ref blockedJobs);
            __result = false;
            return false;
        }
    }

    internal static class AiHandoffGate
    {
        public static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            if (__instance == null || ___pawn == null) return;
            PlayerCommandGate.HasLivePlayerChain(___pawn, __instance);
        }
    }

    internal static class DcpPerPawnState
    {
        private static DcpGameState GameState
        {
            get { return Current.Game == null ? null : Current.Game.GetComponent<DcpGameState>(); }
        }

        internal static bool GetMeleeAutoAttack(Pawn pawn)
        {
            DcpGameState state = GameState;
            return state == null || state.GetMeleeAutoAttack(pawn);
        }

        internal static void ToggleMeleeAutoAttack(Pawn pawn)
        {
            DcpGameState state = GameState;
            if (state != null) state.SetMeleeAutoAttack(pawn, !state.GetMeleeAutoAttack(pawn));
        }
    }

    internal static class MeleeAutoAttack
    {
        private const int ScanIntervalTicks = 15;
        private static long autoAttackJobs;
        internal static long AutoAttackJobs { get { return Interlocked.Read(ref autoAttackJobs); } }

        public static void Postfix(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || !settings.meleeAutoAttack || __instance == null)
                return;

            Pawn pawn = ___pawn;
            if (pawn == null || !pawn.Spawned || pawn.Dead || pawn.Downed || pawn.InMentalState || !pawn.Drafted || pawn.Faction != Faction.OfPlayer || pawn.Map == null)
                return;

            if (DcpControlRules.IsHardControlled(pawn) || PlayerCommandGate.HasLivePlayerChain(pawn, __instance))
                return;

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon || !DcpPerPawnState.GetMeleeAutoAttack(pawn))
                return;

            Job current = __instance.curJob;
            if (current != null && current.def != JobDefOf.Wait_Combat)
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
                if (distSq <= maxDistSq && distSq < nearestDistSq)
                {
                    nearest = other;
                    nearestDistSq = distSq;
                }
            }

            if (nearest == null || !pawn.CanReach(nearest, PathEndMode.Touch, Danger.Deadly))
                return;

            if (PlayerCommandGate.HasLivePlayerChain(pawn, __instance))
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
            if (__result != null) __result = Append(__result, __instance);
        }

        private static IEnumerable<Gizmo> Append(IEnumerable<Gizmo> original, Pawn pawn)
        {
            foreach (Gizmo gizmo in original) yield return gizmo;

            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || !settings.meleeAutoAttack || pawn == null || !pawn.Drafted || pawn.Faction != Faction.OfPlayer)
                yield break;

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon)
                yield break;

            Command_Toggle toggle = new Command_Toggle();
            toggle.isActive = delegate { return DcpPerPawnState.GetMeleeAutoAttack(pawn); };
            toggle.toggleAction = delegate { DcpPerPawnState.ToggleMeleeAutoAttack(pawn); };
            toggle.icon = TexCommand.AttackMelee;
            toggle.defaultLabel = "DCP_MeleeAutoAttackToggleLabel".Translate();
            toggle.defaultDesc = "DCP_MeleeAutoAttackToggleDesc".Translate();
            toggle.tutorTag = "DCP_MeleeAutoAttackToggle";
            yield return toggle;
        }
    }
}
