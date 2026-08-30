using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace AnimalGear.Graphics
{
    [HarmonyPatch(typeof(Pawn_ApparelTracker), "SortWornApparelIntoDrawOrder")]
    public static class AAF15_SafeAnimalApparelSort
    {
        public static bool Prefix(Pawn_ApparelTracker __instance)
        {
            if (__instance == null || __instance.pawn == null || !__instance.pawn.IsAnimal()) return true;
            List<Apparel> worn = __instance.WornApparel;
            if (worn == null || worn.Count < 2) return false;
            worn.Sort(CompareSafe);
            return false;
        }

        private static int CompareSafe(Apparel a, Apparel b)
        {
            if (ReferenceEquals(a, b)) return 0;
            if (a == null) return -1;
            if (b == null) return 1;

            float ad = SafeDrawOrder(a);
            float bd = SafeDrawOrder(b);
            int cmp = ad.CompareTo(bd);
            if (cmp != 0) return cmp;

            string an = a.def == null ? string.Empty : a.def.defName;
            string bn = b.def == null ? string.Empty : b.def.defName;
            return string.CompareOrdinal(an, bn);
        }

        private static float SafeDrawOrder(Apparel apparel)
        {
            try
            {
                ApparelProperties props = apparel == null || apparel.def == null ? null : apparel.def.apparel;
                ApparelLayerDef layer = props == null ? null : props.LastLayer;
                return layer == null ? 0f : layer.drawOrder;
            }
            catch
            {
                return 0f;
            }
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), "SetupDynamicNodes")]
    [HarmonyPriority(Priority.Last)]
    public static class AAF15_RenderTreeFallback
    {
        private delegate void AddChildDelegate(PawnRenderTree tree, PawnRenderNode child, PawnRenderNode parent);
        private static readonly AddChildDelegate AddChild = AccessTools.MethodDelegate<AddChildDelegate>(AccessTools.Method(typeof(PawnRenderTree), "AddChild"));
        private static readonly HashSet<int> logged = new HashSet<int>();

        public static void Postfix(PawnRenderTree __instance)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (pawn == null || !pawn.IsAnimalOfColony() || pawn.apparel == null || pawn.apparel.WornApparelCount == 0) return;

            PawnRenderNode root = RenderTree15.FindNode(__instance.rootNode, AnimalPawnRenderNodeTagDefOf.AnimalApparel);
            bool createdRoot = false;
            if (root == null)
            {
                PawnRenderNode body = RenderTree15.FindNode(__instance.rootNode, PawnRenderNodeTagDefOf.Body);
                if (body == null)
                {
                    Log.Warning("[AAF15] Animal apparel render fallback could not find Body node for " + pawn.ToStringSafe());
                    return;
                }

                PawnRenderNodeProperties_Parent props = new PawnRenderNodeProperties_Parent
                {
                    debugLabel = "Animal apparel root (1.5 fallback)",
                    tagDef = AnimalPawnRenderNodeTagDefOf.AnimalApparel,
                    baseLayer = 20f
                };
                root = new PawnRenderNode_Parent(pawn, props, __instance);
                AddChild(__instance, root, body);
                createdRoot = true;
            }

            // If the original postfix already had a valid root, it already inserted the worn nodes.
            // Only insert here when this fallback had to create the missing root, avoiding duplicates.
            if (createdRoot)
            {
                foreach (Apparel apparel in pawn.apparel.WornApparel)
                {
                    if (apparel == null || apparel.def == null || apparel.def.IsWeapon || AnimalGearHelper.InvisibleForAnimal(apparel.def)) continue;
                    PawnRenderNodeProperties props = new PawnRenderNodeProperties
                    {
                        debugLabel = apparel.def.defName,
                        workerClass = typeof(PawnRenderNodeWorker_Animal_Apparel),
                        baseLayer = root.Props == null ? 20f : root.Props.baseLayer,
                        drawData = apparel.def.apparel == null ? null : apparel.def.apparel.drawData
                    };
                    AddChild(__instance, new PawnRenderNode_Animal_Apparel(pawn, props, __instance, apparel), root);
                }
            }

            if (logged.Add(pawn.thingIDNumber))
            {
                Log.Message("[AAF15] RenderTree15 pawn=" + pawn.LabelShortCap + " def=" + (pawn.def == null ? "null" : pawn.def.defName) +
                    " worn=" + pawn.apparel.WornApparelCount + " root=" + (root != null) + " fallbackRoot=" + createdRoot);
                foreach (Apparel apparel in pawn.apparel.WornApparel)
                {
                    if (apparel == null) continue;
                    string layer = "null";
                    try
                    {
                        ApparelLayerDef last = apparel.def == null || apparel.def.apparel == null ? null : apparel.def.apparel.LastLayer;
                        layer = last == null ? "null" : last.defName + "/" + last.drawOrder;
                    }
                    catch { }
                    Graphic graphic = null;
                    try { graphic = RenderHelpers.GetGraphic(apparel, pawn); } catch (Exception ex) { Log.Warning("[AAF15] GetGraphic failed for " + apparel.ToStringSafe() + ": " + ex); }
                    Log.Message("[AAF15] worn=" + apparel.def.defName + " lastLayer=" + layer + " graphic=" + (graphic == null ? "null" : graphic.path));
                }
            }
        }
    }
}
