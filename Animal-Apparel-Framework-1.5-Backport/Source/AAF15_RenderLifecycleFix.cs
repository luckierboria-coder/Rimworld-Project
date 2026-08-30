using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalGear.Graphics
{
    internal static class AAF15_RenderLifecycle
    {
        private static readonly FieldInfo RenderTreeField = AccessTools.Field(typeof(PawnRenderer), "renderTree");
        private static readonly PropertyInfo RenderTreeProperty = AccessTools.Property(typeof(PawnRenderer), "RenderTree");
        private static readonly MethodInfo RenderTreeSetDirty = AccessTools.Method(typeof(PawnRenderTree), "SetDirty");
        private static readonly MethodInfo RenderTreeSetupDynamicNodes = AccessTools.Method(typeof(PawnRenderTree), "SetupDynamicNodes");
        private static readonly HashSet<int> logged = new HashSet<int>();

        public static void Rebuild(Pawn pawn, string reason)
        {
            if (pawn == null || !pawn.IsAnimalOfColony() || pawn.Drawer == null || pawn.Drawer.renderer == null) return;

            PawnRenderer renderer = pawn.Drawer.renderer;
            try { renderer.SetAllGraphicsDirty(); } catch { }

            PawnRenderTree tree = null;
            try
            {
                if (RenderTreeField != null) tree = RenderTreeField.GetValue(renderer) as PawnRenderTree;
                if (tree == null && RenderTreeProperty != null) tree = RenderTreeProperty.GetValue(renderer, null) as PawnRenderTree;
            }
            catch (Exception ex)
            {
                Log.Warning("[AAF15] Failed reading PawnRenderTree for " + pawn.ToStringSafe() + ": " + ex.Message);
            }

            if (tree == null)
            {
                if (logged.Add(pawn.thingIDNumber))
                    Log.Warning("[AAF15] Render lifecycle: no PawnRenderTree for " + pawn.LabelShortCap + " reason=" + reason);
                return;
            }

            bool dirtied = false;
            try
            {
                if (RenderTreeSetDirty != null)
                {
                    RenderTreeSetDirty.Invoke(tree, null);
                    dirtied = true;
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[AAF15] PawnRenderTree.SetDirty failed for " + pawn.ToStringSafe() + ": " + ex.Message);
            }

            // RimWorld 1.5 does not consistently rebuild dynamic nodes after apparel changes on animals.
            // Invoke SetupDynamicNodes directly as a fallback; it is the same target used by our render-node patches.
            try
            {
                if (RenderTreeSetupDynamicNodes != null)
                    RenderTreeSetupDynamicNodes.Invoke(tree, null);
            }
            catch (Exception ex)
            {
                Log.Warning("[AAF15] PawnRenderTree.SetupDynamicNodes rebuild failed for " + pawn.ToStringSafe() + ": " + ex);
            }

            if (logged.Add(pawn.thingIDNumber))
            {
                int worn = pawn.apparel == null ? -1 : pawn.apparel.WornApparelCount;
                Log.Message("[AAF15] Render lifecycle rebuild pawn=" + pawn.LabelShortCap + " worn=" + worn + " reason=" + reason + " setDirty=" + dirtied);
            }
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), "Notify_ApparelChanged")]
    [HarmonyPriority(Priority.Last)]
    public static class AAF15_NotifyApparelChanged_RenderLifecycle
    {
        public static void Postfix(Pawn_ApparelTracker __instance)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (pawn != null && pawn.IsAnimal())
                AAF15_RenderLifecycle.Rebuild(pawn, "Notify_ApparelChanged");
        }
    }

    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    [HarmonyPriority(Priority.Last)]
    public static class AAF15_SpawnSetup_RenderLifecycle
    {
        public static void Postfix(Pawn __instance, Map map, bool respawningAfterLoad)
        {
            if (__instance != null && __instance.IsAnimalOfColony() && __instance.apparel != null && __instance.apparel.WornApparelCount > 0)
                LongEventHandler.ExecuteWhenFinished(delegate { AAF15_RenderLifecycle.Rebuild(__instance, "SpawnSetup"); });
        }
    }

    [HarmonyPatch(typeof(Game), "FinalizeInit")]
    [HarmonyPriority(Priority.Last)]
    public static class AAF15_FinalizeInit_RenderLifecycle
    {
        public static void Postfix()
        {
            LongEventHandler.ExecuteWhenFinished(delegate
            {
                try
                {
                    foreach (Map map in Find.Maps)
                    {
                        if (map == null || map.mapPawns == null) continue;
                        IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
                        for (int i = 0; i < pawns.Count; i++)
                        {
                            Pawn pawn = pawns[i];
                            if (pawn != null && pawn.IsAnimalOfColony() && pawn.apparel != null && pawn.apparel.WornApparelCount > 0)
                                AAF15_RenderLifecycle.Rebuild(pawn, "Game.FinalizeInit");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[AAF15] FinalizeInit animal render rebuild failed: " + ex);
                }
            });
        }
    }
}
