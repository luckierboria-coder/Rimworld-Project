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

            Log.Message("[Ideology Delete UI] v1.1 active. Minor faction references are auto-detached on delete.");
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
            TooltipHandler.TipRegion(deleteRect, "Delete ideoligion");

            if (Widgets.ButtonImage(deleteRect, TexButton.Delete, Color.white, GenUI.SubtleMouseoverColor))
                IdeologyDeletion.RequestDelete(ideo);
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

            List<Faction> primaryRefs = Find.FactionManager.AllFactions
                .Where(f => f?.ideos != null && f.ideos.PrimaryIdeo == ideo)
                .ToList();

            List<Faction> minorRefs = Find.FactionManager.AllFactions
                .Where(f => f?.ideos != null && f.ideos.IsMinor(ideo))
                .ToList();

            List<Pawn> pawnRefs = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
                .Where(p => p?.ideo?.Ideo == ideo)
                .ToList();

            // Primary faction ownership and current believers are hard blockers.
            // Minor faction references are only bookkeeping and are safe to detach.
            if (primaryRefs.Count > 0 || pawnRefs.Count > 0)
            {
                string details = BuildBlockingReferenceReport(ideo, primaryRefs, minorRefs, pawnRefs);
                Find.WindowStack.Add(new Dialog_MessageBox(
                    details,
                    "OK",
                    null,
                    null,
                    null,
                    "Cannot delete ideoligion"));
                return;
            }

            string minorText = minorRefs.Count > 0
                ? "\n\nMinor faction references to remove automatically: " + minorRefs.Count +
                  "\n" + string.Join("\n", minorRefs.Take(10).Select(f => " - " + f.Name)) +
                  (minorRefs.Count > 10 ? "\n - ... +" + (minorRefs.Count - 10) + " more" : "")
                : "";

            string confirm =
                "Delete ideoligion '" + ideo.name + "'?" +
                minorText +
                "\n\nNo faction uses it as a primary ideoligion and no pawn currently believes in it." +
                "\nMinor faction references will be detached automatically before deletion." +
                "\nHistorical pawn references will be cleaned by RimWorld's own IdeoManager.Remove()." +
                "\n\nThis cannot be undone without reloading the save.";

            Find.WindowStack.Add(Dialog_MessageBox.CreateConfirmation(
                confirm,
                delegate { DeleteNow(ideo); },
                destructive: true));
        }

        private static string BuildBlockingReferenceReport(
            Ideo ideo,
            List<Faction> primaryRefs,
            List<Faction> minorRefs,
            List<Pawn> pawns)
        {
            var lines = new List<string>
            {
                "Cannot delete ideoligion '" + ideo.name + "' because it still has blocking references.",
                "",
                "Primary faction references: " + primaryRefs.Count,
                "Pawn believers: " + pawns.Count,
                "Minor faction references: " + minorRefs.Count + " (these are auto-removable)"
            };

            if (primaryRefs.Count > 0)
            {
                lines.Add("");
                lines.Add("Primary factions:");
                foreach (Faction faction in primaryRefs.Take(12))
                    lines.Add(" - " + faction.Name);
                if (primaryRefs.Count > 12)
                    lines.Add(" - ... +" + (primaryRefs.Count - 12) + " more");
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

            if (minorRefs.Count > 0)
            {
                lines.Add("");
                lines.Add("Minor references (not blocking once the above are cleared):");
                foreach (Faction faction in minorRefs.Take(12))
                    lines.Add(" - " + faction.Name);
                if (minorRefs.Count > 12)
                    lines.Add(" - ... +" + (minorRefs.Count - 12) + " more");
            }

            lines.Add("");
            lines.Add("Change primary faction / pawn ideologies first, then press Delete again.");
            return string.Join("\n", lines);
        }

        private static void DeleteNow(Ideo ideo)
        {
            if (ideo == null || Find.IdeoManager == null)
                return;

            bool primaryStillUses = Find.FactionManager.AllFactions
                .Any(f => f?.ideos != null && f.ideos.PrimaryIdeo == ideo);

            bool pawnStillUses = PawnsFinder.AllMapsWorldAndTemporary_AliveOrDead
                .Any(p => p?.ideo?.Ideo == ideo);

            if (primaryStillUses || pawnStillUses)
            {
                Messages.Message(
                    "Ideology Delete UI: blocking references changed; deletion cancelled.",
                    MessageTypeDefOf.RejectInput,
                    historical: false);
                return;
            }

            int detachedMinorRefs = 0;
            foreach (Faction faction in Find.FactionManager.AllFactions)
            {
                if (faction?.ideos == null || !faction.ideos.IsMinor(ideo))
                    continue;

                if (faction.ideos.IdeosMinorListForReading.Remove(ideo))
                    detachedMinorRefs++;
            }

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

            string suffix = detachedMinorRefs > 0
                ? " (detached " + detachedMinorRefs + " minor faction reference(s))"
                : "";

            Messages.Message(
                "Deleted ideoligion: " + ideo.name + suffix,
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
