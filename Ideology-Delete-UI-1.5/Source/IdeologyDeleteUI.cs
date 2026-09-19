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

            Log.Message("[Ideology Delete UI] Active. Row delete buttons + debug delete action enabled.");
        }
    }

    internal static class DrawIdeoRowPatch
    {
        public static void Postfix(Ideo ideo, ref float curY, Rect fillRect, List<Pawn> pawns)
        {
            if (ideo == null || Current.ProgramState != ProgramState.Playing)
                return;

            // Do not alter the pre-game ideology configuration pages.
            if (Find.WindowStack.WindowOfType<Page_ConfigureIdeo>() != null)
                return;

            float extraPawnHeight = pawns.NullOrEmpty() ? 0f : 32f;
            float rowHeight = 46f + extraPawnHeight;
            float rowTop = curY - rowHeight;

            Rect deleteRect = new Rect(fillRect.width - 30f, rowTop + 10f, 22f, 22f);
            TooltipHandler.TipRegion(deleteRect, "Delete ideoligion");

            if (Widgets.ButtonImage(deleteRect, TexButton.Delete, Color.white, GenUI.SubtleMouseoverColor))
            {
                IdeologyDeletion.RequestDelete(ideo);
            }
        }
    }

    internal static class IdeologyDeletion
    {
        private static readonly FieldInfo BabyExposureField =
            AccessTools.Field(typeof(Pawn_IdeoTracker), "babyIdeoExposure");

        public static void RequestDelete(Ideo ideo)
        {
            if (ideo == null || Find.IdeoManager == null)
                return;

            List<Ideo> all = Find.IdeoManager.IdeosListForReading;
            if (!all.Contains(ideo))
            {
                Messages.Message(
                    "Ideology Delete UI: this ideoligion is no longer present.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            if (all.Count <= 1)
            {
                Messages.Message(
                    "Ideology Delete UI: the last remaining ideoligion cannot be deleted.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            List<Faction> factionRefs = Find.FactionManager.AllFactions
                .Where(f => f?.ideos != null && f.ideos.AllIdeos.Contains(ideo))
                .ToList();

            List<Pawn> pawnRefs = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
                .Where(p => p?.ideo?.Ideo == ideo)
                .ToList();

            if (factionRefs.Count > 0 || pawnRefs.Count > 0)
            {
                string details = BuildReferenceReport(ideo, factionRefs, pawnRefs);
                Find.WindowStack.Add(new Dialog_MessageBox(
                    details,
                    "OK",
                    null,
                    null,
                    null,
                    "Cannot delete ideoligion"));
                return;
            }

            string confirm =
                "Delete ideoligion '" + ideo.name + "'?\n\n" +
                "No faction currently uses it and no pawn currently believes in it. " +
                "Historical pawn references will be cleaned by RimWorld's own IdeoManager.Remove().\n\n" +
                "This cannot be undone without reloading the save.";

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                confirm,
                delegate { DeleteNow(ideo); },
                destructive: true));
        }

        private static string BuildReferenceReport(Ideo ideo, List<Faction> factions, List<Pawn> pawns)
        {
            var lines = new List<string>
            {
                "Cannot delete ideoligion '" + ideo.name + "' because it is still in use.",
                "",
                "Faction references: " + factions.Count,
                "Pawn believers: " + pawns.Count
            };

            if (factions.Count > 0)
            {
                lines.Add("");
                lines.Add("Factions:");
                foreach (Faction faction in factions.Take(12))
                {
                    string role = faction.ideos.PrimaryIdeo == ideo ? "primary" : "minor";
                    lines.Add(" - " + faction.Name + " (" + role + ")");
                }

                if (factions.Count > 12)
                    lines.Add(" - ... +" + (factions.Count - 12) + " more");
            }

            if (pawns.Count > 0)
            {
                lines.Add("");
                lines.Add("Pawns:");
                foreach (Pawn pawn in pawns.Take(16))
                    lines.Add(" - " + pawn.LabelShortCap);

                if (pawns.Count > 16)
                    lines.Add(" - ... +" + (pawns.Count - 16) + " more");
            }

            lines.Add("");
            lines.Add("Change those faction/pawn ideologies first, then press Delete again.");
            return string.Join("\n", lines);
        }

        private static void DeleteNow(Ideo ideo)
        {
            if (ideo == null || Find.IdeoManager == null)
                return;

            // Re-check immediately before mutation.
            bool factionStillUses = Find.FactionManager.AllFactions
                .Any(f => f?.ideos != null && f.ideos.AllIdeos.Contains(ideo));

            bool pawnStillUses = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
                .Any(p => p?.ideo?.Ideo == ideo);

            if (factionStillUses || pawnStillUses)
            {
                Messages.Message(
                    "Ideology Delete UI: references changed; deletion cancelled.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            // Vanilla IdeoManager.Remove cleans current/previous pawn ideology references
            // and play-log references. Baby exposure history is not covered there, so clean
            // that transient reference here before removal.
            CleanBabyExposureReferences(ideo);

            Ideo fallback = Find.IdeoManager.IdeosInViewOrder.FirstOrDefault(i => i != ideo)
                ?? Find.IdeoManager.IdeosListForReading.FirstOrDefault(i => i != ideo);

            bool removed = Find.IdeoManager.Remove(ideo);
            if (!removed)
            {
                Messages.Message(
                    "Ideology Delete UI: RimWorld refused to remove the ideoligion.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            if (IdeoUIUtility.selected == null || IdeoUIUtility.selected == ideo)
                IdeoUIUtility.SetSelected(fallback);

            Find.IdeoManager.SortIdeos();

            Messages.Message(
                "Deleted ideoligion: " + ideo.name,
                MessageTypeDefOf.PositiveEvent,
                historical: false);
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
            "Delete ideoligion...",
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
                    delegate { IdeologyDeletion.RequestDelete(local); }));
            }

            if (options.Count == 0)
            {
                Messages.Message(
                    "Ideology Delete UI: no ideoligions found.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            Find.WindowStack.Add(new Dialog_DebugOptionListLister(options));
        }
    }
}
