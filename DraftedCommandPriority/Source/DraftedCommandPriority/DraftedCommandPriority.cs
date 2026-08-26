using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using UnityEngine;
using Verse;
using Verse.AI;

namespace DraftedCommandPriority
{
    public sealed class DcpSettings : ModSettings
    {
        public bool enabled = true;
        public bool logBlockedJobs = false;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref enabled, "enabled", true);
            Scribe_Values.Look(ref logBlockedJobs, "logBlockedJobs", false);
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
            listing.CheckboxLabeled("DCP_LogBlocked".Translate(), ref Settings.logBlockedJobs, "DCP_LogBlockedDesc".Translate());
            listing.GapLine();
            listing.Label("DCP_BlockedCount".Translate(DraftedOrderGuard.BlockedJobs));
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
                MethodBase target = AccessTools.Method(typeof(Pawn_JobTracker), "StartJob");
                if (target == null)
                {
                    Log.Error("[Drafted Command Priority] Pawn_JobTracker.StartJob not found; mod will remain inert.");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(target, prefix: new HarmonyMethod(typeof(DraftedOrderGuard), nameof(DraftedOrderGuard.Prefix)));
                Log.Message("[Drafted Command Priority] V0.1 active. Drafted player-forced jobs cannot be replaced by ordinary non-player-forced StartJob requests.");
            }
            catch (Exception ex)
            {
                Log.Error("[Drafted Command Priority] Failed to install StartJob guard. " + ex);
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

            // Restrict this to pawns the player is actually commanding.
            if (!pawn.IsColonistPlayerControlled)
                return true;

            Job current = __instance.curJob;
            if (current == null || !current.playerForced)
                return true;

            // A new explicit player order must always be allowed to replace the old one.
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
}
