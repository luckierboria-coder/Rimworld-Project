using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.IdeologyDeleteUI
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            var harmony = new Harmony("allen.ideology.deleteui.1_5");
            var target = AccessTools.Method(typeof(IdeoUIUtility), "DrawIdeoRow");

            if (target == null)
            {
                Log.Error("[Ideology Delete UI] Could not find IdeoUIUtility.DrawIdeoRow.");
                return;
            }

            harmony.Patch(
                target,
                postfix: new HarmonyMethod(typeof(DrawIdeoRowPatch), nameof(DrawIdeoRowPatch.Postfix)));

            Log.Message("[Ideology Delete UI] v1.2 FORCE DELETE active.");
        }
    }

    internal static class DrawIdeoRowPatch
    {
        public static void Postfix(Ideo ideo, ref float curY, Rect fillRect, List<Pawn> pawns)
        {
            if (ideo == null || Current.ProgramState != ProgramState.Playing)
                return;

            if (Find.WindowStack.WindowOfType<Page_ConfigureIdeo>() != null)
                return;

            float extraPawnHeight = pawns.NullOrEmpty() ? 0f : 32f;
            float rowHeight = 46f + extraPawnHeight;
            float rowTop = curY - rowHeight;

            Rect deleteRect = new Rect(fillRect.width - 30f, rowTop + 10f, 22f, 22f);
            TooltipHandler.TipRegion(deleteRect, "Force delete ideoligion");

            if (Widgets.ButtonImage(deleteRect, TexButton.Delete, Color.white, GenUI.SubtleMouseoverColor))
                IdeologyDeletion.RequestForceDelete(ideo);
        }
    }

    internal static class IdeologyDeletion
    {
        private static readonly FieldInfo PawnIdeoField =
            AccessTools.Field(typeof(Pawn_IdeoTracker), "ideo");

        private static readonly FieldInfo BabyExposureField =
            AccessTools.Field(typeof(Pawn_IdeoTracker), "babyIdeoExposure");

        public static void RequestForceDelete(Ideo ideo)
        {
            if (ideo == null || Find.IdeoManager == null || !Find.IdeoManager.IdeosListForReading.Contains(ideo))
                return;

            int primaryFactions = Find.FactionManager.AllFactions
                .Count(f => f?.ideos?.PrimaryIdeo == ideo);

            int minorFactions = Find.FactionManager.AllFactions
                .Count(f => f?.ideos != null && f.ideos.IsMinor(ideo));

            int believers = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
                .Count(p => p?.ideo?.Ideo == ideo);

            string confirm =
                "FORCE DELETE ideoligion '" + ideo.name + "'?\n\n" +
                "This operation will automatically resolve every vanilla core reference it can find:\n" +
                " - primary factions: " + primaryFactions + "\n" +
                " - minor faction references: " + minorFactions + "\n" +
                " - pawn believers: " + believers + "\n\n" +
                "Primary factions and believers will be reassigned automatically. Minor references will be removed. " +
                "Active rituals belonging to this ideoligion will be cancelled. " +
                "If this is the last ideoligion, a replacement fallback ideoligion will be generated automatically.\n\n" +
                "The selected ideoligion itself will then be removed from IdeoManager.";

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                confirm,
                delegate { ForceDeleteNow(ideo); },
                destructive: true));
        }

        private static void ForceDeleteNow(Ideo removedIdeo)
        {
            if (removedIdeo == null || Find.IdeoManager == null ||
                !Find.IdeoManager.IdeosListForReading.Contains(removedIdeo))
                return;

            Ideo globalFallback = EnsureFallbackIdeo(removedIdeo);
            if (globalFallback == null)
            {
                Messages.Message(
                    "Ideology Delete UI: could not create/find a fallback ideoligion.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            int ritualsCancelled = CancelActiveRituals(removedIdeo);
            int pawnsMoved = ReassignPawnBelievers(removedIdeo, globalFallback);
            int primaryMoved = 0;
            int minorDetached = 0;

            // Final faction scrub is intentionally AFTER pawn reassignment because
            // player pawn ideology changes can recalculate the player faction tracker.
            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                if (faction?.ideos == null)
                    continue;

                if (faction.ideos.PrimaryIdeo == removedIdeo)
                {
                    Ideo replacement = faction.ideos.IdeosMinorListForReading
                        .FirstOrDefault(i => i != null && i != removedIdeo)
                        ?? globalFallback;

                    faction.ideos.IdeosMinorListForReading.Remove(replacement);
                    faction.ideos.SetPrimary(replacement);
                    primaryMoved++;
                }

                while (faction.ideos.IdeosMinorListForReading.Remove(removedIdeo))
                    minorDetached++;
            }

            CleanBabyExposureReferences(removedIdeo);

            // One last direct scrub covers babies/dead/modded pawns for which SetIdeo
            // may refuse or perform no transition.
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                if (pawn?.ideo == null || pawn.ideo.Ideo != removedIdeo)
                    continue;

                Ideo replacement = GetPawnFallback(pawn, removedIdeo, globalFallback);
                PawnIdeoField?.SetValue(pawn.ideo, replacement);
            }

            bool removed = Find.IdeoManager.Remove(removedIdeo);
            if (!removed)
            {
                Messages.Message(
                    "Ideology Delete UI: IdeoManager.Remove returned false.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            if (IdeoUIUtility.selected == null || IdeoUIUtility.selected == removedIdeo)
                IdeoUIUtility.SetSelected(globalFallback);

            Find.IdeoManager.SortIdeos();

            Messages.Message(
                "Force-deleted ideoligion: " + removedIdeo.name +
                " | pawns reassigned: " + pawnsMoved +
                " | primary factions reassigned: " + primaryMoved +
                " | minor refs removed: " + minorDetached +
                " | rituals cancelled: " + ritualsCancelled,
                MessageTypeDefOf.PositiveEvent,
                historical: false);
        }

        private static Ideo EnsureFallbackIdeo(Ideo removedIdeo)
        {
            Ideo existing = Find.IdeoManager.IdeosInViewOrder.FirstOrDefault(i => i != removedIdeo)
                ?? Find.IdeoManager.IdeosListForReading.FirstOrDefault(i => i != removedIdeo);

            if (existing != null)
                return existing;

            Faction player = Faction.OfPlayerSilentFail;
            FactionDef factionDef = player?.def
                ?? Find.FactionManager.AllFactions.FirstOrDefault(f => f?.def != null)?.def;

            if (factionDef == null)
                return null;

            Ideo generated = IdeoGenerator.GenerateIdeo(new IdeoGenerationParms(factionDef));
            Find.IdeoManager.Add(generated);
            return generated;
        }

        private static Ideo GetPawnFallback(Pawn pawn, Ideo removedIdeo, Ideo globalFallback)
        {
            Ideo factionPrimary = pawn?.Faction?.ideos?.PrimaryIdeo;
            if (factionPrimary != null && factionPrimary != removedIdeo)
                return factionPrimary;

            return globalFallback;
        }

        private static int ReassignPawnBelievers(Ideo removedIdeo, Ideo globalFallback)
        {
            int count = 0;
            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead.ToList())
            {
                if (pawn?.ideo == null || pawn.ideo.Ideo != removedIdeo)
                    continue;

                Ideo replacement = GetPawnFallback(pawn, removedIdeo, globalFallback);

                try
                {
                    pawn.ideo.SetIdeo(replacement);
                }
                catch (Exception ex)
                {
                    Log.Warning("[Ideology Delete UI] SetIdeo failed for " + pawn.ToStringSafe() +
                                "; forcing tracker field instead. " + ex.GetType().Name + ": " + ex.Message);
                }

                if (pawn.ideo.Ideo == removedIdeo)
                    PawnIdeoField?.SetValue(pawn.ideo, replacement);

                count++;
            }
            return count;
        }

        private static int CancelActiveRituals(Ideo removedIdeo)
        {
            int count = 0;

            foreach (Map map in Find.Maps.ToList())
            {
                if (map?.lordManager?.lords == null)
                    continue;

                foreach (var lord in map.lordManager.lords.ToList())
                {
                    if (!(lord?.LordJob is LordJob_Ritual ritualJob))
                        continue;

                    if (ritualJob.Ritual?.ideo != removedIdeo)
                        continue;

                    try
                    {
                        ritualJob.ApplyOutcome(
                            ritualJob.Progress,
                            showFinishedMessage: false,
                            showFailedMessage: false,
                            cancelled: true);
                        count++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warning("[Ideology Delete UI] Failed to cancel an active ritual before force deletion: " +
                                    ex.GetType().Name + ": " + ex.Message);
                    }
                }
            }

            return count;
        }

        private static void CleanBabyExposureReferences(Ideo removedIdeo)
        {
            if (BabyExposureField == null)
                return;

            foreach (Pawn pawn in PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead)
            {
                Pawn_IdeoTracker tracker = pawn?.ideo;
                if (tracker == null)
                    continue;

                var list = BabyExposureField.GetValue(tracker)
                    as List<Pawn_IdeoTracker.IdeoExposureWeight>;

                list?.RemoveAll(x => x == null || x.ideo == removedIdeo);
            }
        }
    }

    internal static class IdeologyDeleteDebugAction
    {
        [DebugAction(
            "Ideoligion",
            "Force delete ideoligion...",
            requiresIdeology: true,
            actionType = DebugActionType.Action,
            allowedGameStates = AllowedGameStates.PlayingOnMap)]
        private static void DeleteIdeoligion()
        {
            var options = new List<DebugMenuOption>();

            foreach (Ideo ideo in Find.IdeoManager.IdeosInViewOrder.ToList())
            {
                Ideo local = ideo;
                options.Add(new DebugMenuOption(
                    local.name,
                    DebugMenuOptionMode.Action,
                    delegate { IdeologyDeletion.RequestForceDelete(local); }));
            }

            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }
    }
}
