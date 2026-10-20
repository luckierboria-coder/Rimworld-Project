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

        public DcpGameState(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Collections.Look(ref meleeAutoAttackByPawn, "meleeAutoAttackByPawn", LookMode.Value, LookMode.Value);
            if (meleeAutoAttackByPawn == null)
                meleeAutoAttackByPawn = new Dictionary<int, bool>();
            base.ExposeData();
        }

        internal bool GetMeleeAutoAttack(Pawn pawn)
        {
            if (pawn == null)
                return true;

            bool enabled;
            if (meleeAutoAttackByPawn.TryGetValue(pawn.thingIDNumber, out enabled))
                return enabled;
            return true;
        }

        internal void SetMeleeAutoAttack(Pawn pawn, bool enabled)
        {
            if (pawn != null)
                meleeAutoAttackByPawn[pawn.thingIDNumber] = enabled;
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
            listing.Label("DCP_BlockedCount".Translate(ThinkTreeCommandGate.BlockedJobs));
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
                {
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.IsCurrentJobPlayerInterruptible not found; burning-order override will remain inert.");
                }
                else
                {
                    HarmonyMethod interruptPostfix = new HarmonyMethod(typeof(BurningPlayerInterrupt), "Postfix");
                    // Run early among postfixes. Other mods that deliberately veto later
                    // still retain the final say, reducing compatibility risk.
                    interruptPostfix.priority = Priority.First;
                    harmony.Patch(interruptible, postfix: interruptPostfix);
                }

                MethodBase startJob = AccessTools.Method(typeof(Pawn_JobTracker), "StartJob");
                if (startJob == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.StartJob not found; player-command tracking will remain inert.");
                else
                    harmony.Patch(startJob, prefix: new HarmonyMethod(typeof(PlayerCommandObserver), "Prefix"));

                MethodBase shouldStart = AccessTools.Method(typeof(Pawn_JobTracker), "ShouldStartJobFromThinkTree");
                if (shouldStart == null)
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.ShouldStartJobFromThinkTree not found; autonomous ThinkTree gate will remain inert.");
                else
                    harmony.Patch(shouldStart, prefix: new HarmonyMethod(typeof(ThinkTreeCommandGate), "Prefix"));

                MethodBase tryFind = AccessTools.Method(typeof(Pawn_JobTracker), "TryFindAndStartJob");
                if (tryFind == null)
                    Log.Warning("[Drafted Command Priority] Pawn_JobTracker.TryFindAndStartJob not found; command gate will rely on drafted idle release.");
                else
                    harmony.Patch(tryFind, prefix: new HarmonyMethod(typeof(AiHandoffGate), "Prefix"));

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

                Log.Message("[Drafted Command Priority] V0.1 compatibility-safe mode active. Burning no longer blocks drafted player orders; player command chains gate ordinary ThinkTree AI until handoff. No ThinkTree replacement, PathFinder patch, or broad StartJob veto is installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[Drafted Command Priority] Failed to install patches. " + ex);
            }
        }
    }

    internal static class DcpControlRules
    {
        internal static bool IsHardControlled(Pawn pawn)
        {
            if (pawn == null || pawn.Dead || pawn.Downed || pawn.InMentalState)
                return true;

            if (pawn.stances != null && pawn.stances.stunner != null && pawn.stances.stunner.Stunned)
                return true;

            return false;
        }

        internal static bool HasAbsolutePlayerControl(Pawn pawn)
        {
            return pawn != null && pawn.Spawned && pawn.Drafted && !IsHardControlled(pawn) && pawn.IsColonistPlayerControlled;
        }
    }

    internal static class DcpPerPawnState
    {
        internal static DcpGameState GameState
        {
            get
            {
                if (Verse.Current.Game == null)
                    return null;
                return Verse.Current.Game.GetComponent<DcpGameState>();
            }
        }

        internal static bool GetMeleeAutoAttack(Pawn pawn)
        {
            DcpGameState state = GameState;
            return state == null || state.GetMeleeAutoAttack(pawn);
        }

        internal static void ToggleMeleeAutoAttack(Pawn pawn)
        {
            DcpGameState state = GameState;
            if (state != null)
                state.SetMeleeAutoAttack(pawn, !state.GetMeleeAutoAttack(pawn));
        }
    }

    internal static class DcpKeyBindings
    {
        private static KeyBindingDef toggleMeleeAutoAttack;
        private static bool resolved;

        internal static KeyBindingDef ToggleMeleeAutoAttack
        {
            get
            {
                if (!resolved)
                {
                    resolved = true;
                    toggleMeleeAutoAttack = DefDatabase<KeyBindingDef>.GetNamedSilentFail("DCP_ToggleMeleeAutoAttack");
                }
                return toggleMeleeAutoAttack;
            }
        }
    }

    internal static class BurningPlayerInterrupt
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

            // Narrow compatibility override: only remove Vanilla's special rule that
            // makes an otherwise player-interruptible job non-interruptible because the
            // pawn has a Fire attachment. Do NOT globally rewrite foreign/noninterruptible
            // jobs; other mods can still impose their own restrictions after this postfix.
            Job current = pawn.jobs == null ? null : pawn.jobs.curJob;
            bool jobNormallyInterruptible = current == null || (current.def != null && current.def.playerInterruptible);
            if (jobNormallyInterruptible && pawn.HasAttachment(ThingDefOf.Fire))
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

    internal static class PlayerCommandObserver
    {
        // Observation only. Never veto StartJob here; broad StartJob prefixes are a
        // high-risk compatibility surface for modded finalizer/continuation jobs.
        public static void Prefix(Pawn ___pawn, Job newJob)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || newJob == null)
                return;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
            {
                PlayerCommandGate.Clear(pawn);
                return;
            }

            if (newJob.playerForced)
                PlayerCommandGate.Mark(pawn);
        }
    }

    internal static class ThinkTreeCommandGate
    {
        private static long blockedJobs;
        internal static long BlockedJobs
        {
            get { return Interlocked.Read(ref blockedJobs); }
        }

        // Veto only ordinary ThinkTree AI while a player command chain owns the pawn.
        // Mod-internal direct StartJob continuation/finalizer calls are intentionally
        // left untouched for compatibility.
        public static bool Prefix(Pawn ___pawn, ThinkResult thinkResult, ref bool __result)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled)
                return true;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
            {
                PlayerCommandGate.Clear(pawn);
                return true;
            }

            if (!PlayerCommandGate.IsActive(pawn))
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
        // Vanilla calls this when the current command has actually finished and it is
        // ready to ask AI for a new job. That is the clean handoff point: release DCP
        // ownership here instead of blocking arbitrary StartJob calls from other mods.
        public static void Prefix(Pawn_JobTracker __instance, Pawn ___pawn)
        {
            DcpSettings settings = DcpMod.Settings;
            if (settings == null || !settings.enabled || __instance == null)
                return;

            Pawn pawn = ___pawn;
            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
            {
                PlayerCommandGate.Clear(pawn);
                return;
            }

            if (PlayerCommandGate.IsActive(pawn) && __instance.curJob == null && !PlayerCommandGate.HasQueuedPlayerOrder(__instance))
                PlayerCommandGate.Clear(pawn);
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

            if (!DcpPerPawnState.GetMeleeAutoAttack(pawn))
                return;

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

            if (!DcpControlRules.HasAbsolutePlayerControl(pawn))
                yield break;

            ThingWithComps primary = pawn.equipment == null ? null : pawn.equipment.Primary;
            if (primary == null || primary.def == null || !primary.def.IsMeleeWeapon)
                yield break;

            Command_Toggle toggle = new Command_Toggle();
            toggle.hotKey = DcpKeyBindings.ToggleMeleeAutoAttack;
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
