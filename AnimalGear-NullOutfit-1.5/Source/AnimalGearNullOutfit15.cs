using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace Allen.AnimalGearNullOutfit15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string HarmonyId = "Allen.AnimalGearNullOutfit15";

        static Bootstrap()
        {
            try
            {
                new Harmony(HarmonyId).PatchAll(Assembly.GetExecutingAssembly());
                Log.Message("[Animal Gear - Null Outfit 1.5] Active. Player animals may use None/无方案; unassigned animals will not optimize apparel.");
            }
            catch (Exception ex)
            {
                Log.Error("[Animal Gear - Null Outfit 1.5] Failed to initialize: " + ex);
            }
        }
    }

    internal static class AnimalOutfitUtility
    {
        private static readonly FieldInfo CurPolicyField = AccessTools.Field(typeof(Pawn_OutfitTracker), "curApparelPolicy");

        public static bool IsPlayerAnimal(Pawn pawn)
        {
            try
            {
                return pawn != null && pawn.Faction == Faction.OfPlayer && pawn.RaceProps != null && pawn.RaceProps.Animal;
            }
            catch
            {
                return false;
            }
        }

        public static ApparelPolicy RawPolicy(Pawn_OutfitTracker tracker)
        {
            if (tracker == null || CurPolicyField == null)
                return null;
            return CurPolicyField.GetValue(tracker) as ApparelPolicy;
        }

        public static void SetRawPolicy(Pawn_OutfitTracker tracker, ApparelPolicy policy)
        {
            if (tracker == null || CurPolicyField == null)
                return;

            CurPolicyField.SetValue(tracker, policy);
            Pawn pawn = tracker.pawn;
            if (pawn != null && pawn.mindState != null)
                pawn.mindState.Notify_OutfitChanged();
        }

        public static string DisplayLabel(ApparelPolicy policy)
        {
            return policy == null ? "AnimalGearNullOutfit_None".Translate().ToString() : policy.label;
        }
    }

    // Vanilla 1.5 converts a null curApparelPolicy to DefaultOutfit/Anything in this getter.
    // For player animals only, preserve a genuine null as the explicit None state.
    [HarmonyPatch(typeof(Pawn_OutfitTracker), "get_CurrentApparelPolicy")]
    public static class PawnOutfitTracker_CurrentPolicy_Patch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Pawn_OutfitTracker __instance, ref ApparelPolicy __result)
        {
            if (!AnimalOutfitUtility.IsPlayerAnimal(__instance != null ? __instance.pawn : null))
                return true;

            ApparelPolicy raw = AnimalOutfitUtility.RawPolicy(__instance);
            if (raw != null)
                return true;

            __result = null;
            return false;
        }
    }

    // Let Animal Gear's own high-priority prefix establish/clear its caches, then skip the
    // vanilla scan at the last prefix priority when the animal is explicitly on None.
    [HarmonyPatch(typeof(JobGiver_OptimizeApparel), "TryGiveJob")]
    [HarmonyAfter(new[] { "AnimalGear" })]
    public static class JobGiverOptimizeApparel_NullPolicy_Patch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.Last)]
        public static bool Prefix(Pawn pawn, ref Job __result)
        {
            if (!AnimalOutfitUtility.IsPlayerAnimal(pawn) || pawn.outfits == null)
                return true;

            if (AnimalOutfitUtility.RawPolicy(pawn.outfits) != null)
                return true;

            __result = null;
            return false;
        }
    }

    // Animal Gear inserts the vanilla Outfit column into the Animals table. The vanilla
    // worker assumes CurrentApparelPolicy is never null, so animals get a null-aware cell.
    [HarmonyPatch(typeof(PawnColumnWorker_Outfit), "DoCell")]
    public static class PawnColumnWorkerOutfit_DoCell_Patch
    {
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        public static bool Prefix(Rect rect, Pawn pawn, PawnTable table)
        {
            if (!AnimalOutfitUtility.IsPlayerAnimal(pawn) || pawn.outfits == null)
                return true;

            DrawAnimalOutfitCell(rect, pawn);
            return false;
        }

        private static void DrawAnimalOutfitCell(Rect rect, Pawn pawn)
        {
            Rect inner = rect.ContractedBy(0f, 2f);
            bool hasForced = pawn.outfits.forcedHandler != null && pawn.outfits.forcedHandler.SomethingIsForced;
            Rect policyRect = inner;
            Rect clearRect = new Rect();

            if (hasForced)
            {
                float clearWidth = Mathf.Min(74f, Mathf.Max(42f, inner.width * 0.28f));
                policyRect.width = Mathf.Max(10f, inner.width - clearWidth - 4f);
                clearRect = new Rect(policyRect.xMax + 4f, inner.y, clearWidth, inner.height);
            }

            ApparelPolicy raw = AnimalOutfitUtility.RawPolicy(pawn.outfits);

            if (pawn.IsQuestLodger())
            {
                Text.Anchor = TextAnchor.MiddleCenter;
                Widgets.Label(policyRect, "Unchangeable".Translate().ToString().Truncate(policyRect.width));
                Text.Anchor = TextAnchor.UpperLeft;
            }
            else
            {
                string label = AnimalOutfitUtility.DisplayLabel(raw);
                if (Widgets.ButtonText(policyRect, label.Truncate(policyRect.width)))
                {
                    List<FloatMenuOption> options = BuildMenu(pawn, raw);
                    Find.WindowStack.Add(new FloatMenu(options));
                }

                TooltipHandler.TipRegion(policyRect, raw == null
                    ? "AnimalGearNullOutfit_NoneDesc".Translate().ToString()
                    : raw.label);
            }

            if (hasForced)
            {
                if (Widgets.ButtonText(clearRect, "ClearForcedApparel".Translate()))
                    pawn.outfits.forcedHandler.Reset();

                TooltipHandler.TipRegion(clearRect, "ClearForcedApparel".Translate().ToString());
            }
        }

        private static List<FloatMenuOption> BuildMenu(Pawn pawn, ApparelPolicy current)
        {
            List<FloatMenuOption> list = new List<FloatMenuOption>();

            // This is a persistent animal-only selectable state, not a database policy.
            // It is always available, so a mistaken policy assignment can be reverted.
            list.Add(new FloatMenuOption("AnimalGearNullOutfit_None".Translate(), delegate
            {
                AnimalOutfitUtility.SetRawPolicy(pawn.outfits, null);
            }));

            if (Current.Game != null && Current.Game.outfitDatabase != null)
            {
                foreach (ApparelPolicy policy in Current.Game.outfitDatabase.AllOutfits)
                {
                    ApparelPolicy captured = policy;
                    list.Add(new FloatMenuOption(captured.label, delegate
                    {
                        pawn.outfits.CurrentApparelPolicy = captured;
                    }));
                }
            }

            list.Add(new FloatMenuOption("AssignTabEdit".Translate() + "...", delegate
            {
                Find.WindowStack.Add(new Dialog_ManageApparelPolicies(current));
            }));

            return list;
        }
    }

    // Existing saves often already serialized Animal Gear animals with vanilla's default
    // Anything policy because the getter had been accessed. Convert only that legacy default
    // once; custom policies are preserved. The migration flag makes later explicit Anything
    // selections stay explicit.
    public sealed class AnimalNullOutfitMigrationComponent : GameComponent
    {
        private bool migratedDefaultAnything;

        public AnimalNullOutfitMigrationComponent(Game game)
        {
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref migratedDefaultAnything, "AnimalGearNullOutfit_migratedDefaultAnything", false);
        }

        public override void StartedNewGame()
        {
            migratedDefaultAnything = true;
        }

        public override void LoadedGame()
        {
            if (migratedDefaultAnything)
                return;

            int converted = 0;
            try
            {
                ApparelPolicy defaultPolicy = Current.Game != null && Current.Game.outfitDatabase != null
                    ? Current.Game.outfitDatabase.DefaultOutfit()
                    : null;

                if (defaultPolicy != null)
                {
                    HashSet<Pawn> seen = new HashSet<Pawn>();
                    ConvertList(PawnsFinder.AllMapsWorldAndTemporary_Alive, defaultPolicy, seen, ref converted);
                    ConvertList(PawnsFinder.AllCaravansAndTravelingTransportPods_Alive, defaultPolicy, seen, ref converted);
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Animal Gear - Null Outfit 1.5] Existing-animal migration encountered an error: " + ex.Message);
            }

            migratedDefaultAnything = true;
            Log.Message("[Animal Gear - Null Outfit 1.5] Existing-animal migration complete. Converted " + converted + " player animal(s) from default Anything to None/无方案. Custom policies were preserved.");
        }

        private static void ConvertList(IEnumerable<Pawn> pawns, ApparelPolicy defaultPolicy, HashSet<Pawn> seen, ref int converted)
        {
            if (pawns == null)
                return;

            foreach (Pawn pawn in pawns)
            {
                if (pawn == null || !seen.Add(pawn) || !AnimalOutfitUtility.IsPlayerAnimal(pawn) || pawn.outfits == null)
                    continue;

                ApparelPolicy raw = AnimalOutfitUtility.RawPolicy(pawn.outfits);
                if (raw == defaultPolicy)
                {
                    AnimalOutfitUtility.SetRawPolicy(pawn.outfits, null);
                    converted++;
                }
            }
        }
    }
}
