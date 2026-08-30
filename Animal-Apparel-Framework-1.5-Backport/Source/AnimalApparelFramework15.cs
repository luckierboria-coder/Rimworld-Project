using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace AnimalGear
{
    public class AnimalApparelDefExtension : DefModExtension
    {
        public BodyDef showCoverageForBodyType;
    }

    public static class AnimalGearHelper
    {
        private static readonly Dictionary<ThingDef, bool> invisibleCache = new Dictionary<ThingDef, bool>();

        public static bool IsAnimal(this Pawn pawn)
        {
            return pawn != null && pawn.def != null && pawn.def.race != null && pawn.def.race.intelligence == Intelligence.Animal;
        }

        public static bool IsAnimalOfColony(this Pawn pawn)
        {
            return pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.IsAnimal() && pawn.RaceProps.FleshType != FleshTypeDefOf.Mechanoid;
        }

        public static void EnsureInitApparelTrackers(this Pawn pawn)
        {
            if (pawn.outfits == null) pawn.outfits = new Pawn_OutfitTracker(pawn);
            if (pawn.equipment == null) pawn.equipment = new Pawn_EquipmentTracker(pawn);
            if (pawn.apparel == null) pawn.apparel = new Pawn_ApparelTracker(pawn);
        }

        public static List<ThingDef> RequiredThingDefFromTags(ApparelProperties props)
        {
            if (props == null || props.tags == null) return new List<ThingDef>();
            return props.tags.Where(x => x.StartsWith("defName", StringComparison.Ordinal))
                .Select(x => DefDatabase<ThingDef>.GetNamedSilentFail(x.Substring(7)))
                .Where(x => x != null).ToList();
        }

        public static bool CanEquipApparel(ThingDef thing, Pawn pawn, ref string cantReason)
        {
            if (thing == null || thing.apparel == null || pawn == null) return false;
            List<string> tags = thing.apparel.tags;
            bool animal = pawn.IsAnimal();
            if (tags == null || tags.Count == 0)
            {
                if (animal) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
                return true;
            }
            if (tags.Any(x => x.StartsWith("defName", StringComparison.Ordinal)))
            {
                bool ok = RequiredThingDefFromTags(thing.apparel).Contains(pawn.def);
                if (!ok) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
                return true;
            }
            bool animalApparel = tags.Contains("AnimalApparel");
            bool animalOnly = tags.Contains("AnimalOnly");
            if (animal && !(animalApparel || animalOnly)) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
            if (animalOnly && !animal) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
            return true;
        }

        public static bool InvisibleForAnimal(ThingDef def)
        {
            bool value;
            if (!invisibleCache.TryGetValue(def, out value))
            {
                value = def != null && def.apparel != null && def.apparel.tags != null && def.apparel.tags.Contains("AnimalInvisible");
                invisibleCache[def] = value;
            }
            return value;
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("Ingendum.AnimalApparelFramework.Backport15").PatchAll();
            Log.Message("[Animal Apparel Framework 1.5 Backport] Active.");
        }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility), "CreateInitialComponents")]
    public static class PatchCreateComponents
    {
        public static void Postfix(Pawn pawn) { if (pawn.IsAnimalOfColony()) pawn.EnsureInitApparelTrackers(); }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility), "AddAndRemoveDynamicComponents")]
    public static class PatchDynamicComponents
    {
        public static void Postfix(Pawn pawn, bool actAsIfSpawned) { if (pawn.IsAnimalOfColony()) pawn.EnsureInitApparelTrackers(); }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), "Notify_ApparelChanged")]
    public static class PatchApparelChanged
    {
        public static bool Prefix(Pawn_ApparelTracker __instance)
        {
            if (__instance != null && __instance.pawn.IsAnimal())
            {
                if (__instance.pawn.Drawer != null && __instance.pawn.Drawer.renderer != null)
                    __instance.pawn.Drawer.renderer.SetAllGraphicsDirty();
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch(typeof(ITab_Pawn_Gear), "get_IsVisible")]
    public static class PatchGearTabVisible
    {
        public static void Postfix(ref bool __result)
        {
            if (__result) return;
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn.IsAnimalOfColony()) __result = true;
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "CanTakeOrder")]
    public static class PatchCanTakeOrder
    {
        public static void Postfix(Pawn pawn, ref bool __result) { if (pawn.IsAnimalOfColony()) __result = true; }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "ChoicesAtFor")]
    public static class PatchChoicesAtFor
    {
        public static void Postfix(Vector3 clickPos, Pawn pawn, bool suppressAutoTakeableGoto, ref List<FloatMenuOption> __result)
        {
            if (!pawn.IsAnimalOfColony() || pawn.Map == null) return;
            pawn.EnsureInitApparelTrackers();
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (!cell.InBounds(pawn.Map)) return;
            Apparel apparel = pawn.Map.thingGrid.ThingAt<Apparel>(cell);
            if (apparel == null) return;
            string reason = null;
            if (!AnimalGearHelper.CanEquipApparel(apparel.def, pawn, ref reason)) return;
            if (!ApparelUtility.HasPartsToWear(pawn, apparel.def)) return;
            if (!pawn.CanReach(apparel, PathEndMode.Touch, Danger.Deadly)) return;
            FloatMenuOption option = new FloatMenuOption("ForceWear".Translate(apparel.LabelShort, apparel), delegate
            {
                apparel.SetForbidden(false, false);
                Job job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }, MenuOptionPriority.High);
            __result.Add(FloatMenuUtility.DecoratePrioritizedTask(option, pawn, apparel, "ReservedBy"));
        }
    }
}

namespace AnimalGear.Graphics
{
    [DefOf]
    public static class AnimalPawnRenderNodeTagDefOf
    {
        public static PawnRenderNodeTagDef AnimalApparel;
    }

    public static class RenderTree15
    {
        public static PawnRenderNode FindNode(PawnRenderNode node, PawnRenderNodeTagDef tag)
        {
            if (node == null || tag == null) return null;
            if (node.Props != null && node.Props.tagDef == tag) return node;
            PawnRenderNode[] children = node.children;
            if (children == null) return null;
            for (int i = 0; i < children.Length; i++)
            {
                PawnRenderNode found = FindNode(children[i], tag);
                if (found != null) return found;
            }
            return null;
        }
    }

    public static class RenderHelpers
    {
        public static Graphic GetGraphic(Apparel apparel, Pawn pawn)
        {
            if (apparel == null || pawn == null || apparel.WornGraphicPath.NullOrEmpty()) return null;
            string basePath = apparel.WornGraphicPath;
            string chosen = basePath;
            ApparelProperties props = apparel.def.apparel;
            if (props != null && props.tags != null && (props.tags.Any(t => t.StartsWith("defName", StringComparison.Ordinal)) || props.tags.Contains("AnimalFallbackInvisible")))
            {
                string cap = pawn.def.defName.CapitalizeFirst();
                string specific = basePath + "/" + cap + "/" + cap;
                if (ContentFinder<Texture2D>.Get(specific + "_east", false) != null) chosen = specific;
                else if (ContentFinder<Texture2D>.Get(basePath + "_east", false) == null)
                {
                    if (props.tags.Contains("AnimalFallbackInvisible")) return null;
                    Log.Error("[AAF15] Missing worn graphic for " + apparel.def.defName + " / " + pawn.def.defName);
                    return null;
                }
            }
            Shader shader = ContentFinder<Texture2D>.Get(chosen + "_eastm", false) != null ? ShaderDatabase.CutoutComplex : ShaderDatabase.Cutout;
            return GraphicDatabase.Get<Graphic_Multi>(chosen, shader, apparel.def.graphicData.drawSize, apparel.DrawColor);
        }
    }

    public class PawnRenderNode_Animal_Apparel : PawnRenderNode
    {
        public PawnRenderNode_Animal_Apparel(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree, Apparel apparel) : base(pawn, props, tree)
        {
            this.apparel = apparel;
        }

        public override Graphic GraphicFor(Pawn pawn)
        {
            return RenderHelpers.GetGraphic(apparel, pawn);
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            PawnRenderNode body = RenderTree15.FindNode(tree.rootNode, PawnRenderNodeTagDefOf.Body);
            if (body != null) return body.MeshSetFor(pawn);
            float size = 1f;
            if (pawn.ageTracker != null && pawn.ageTracker.CurKindLifeStage != null && pawn.ageTracker.CurKindLifeStage.bodyGraphicData != null)
                size = pawn.ageTracker.CurKindLifeStage.bodyGraphicData.drawSize.x;
            return MeshPool.GetMeshSetForSize(size, size);
        }
    }

    public class PawnRenderNodeWorker_Animal_Apparel : PawnRenderNodeWorker
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms) { return base.CanDrawNow(node, parms); }
    }

    [HarmonyPatch(typeof(PawnRenderTree), "SetupDynamicNodes")]
    public static class PatchSetupDynamicNodes
    {
        private delegate void AddChildDelegate(PawnRenderTree tree, PawnRenderNode child, PawnRenderNode parent);
        private static readonly AddChildDelegate AddChild = AccessTools.MethodDelegate<AddChildDelegate>(AccessTools.Method(typeof(PawnRenderTree), "AddChild"));

        public static void Postfix(PawnRenderTree __instance)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (!pawn.IsAnimalOfColony() || pawn.apparel == null || pawn.apparel.WornApparelCount == 0) return;
            PawnRenderNode root = RenderTree15.FindNode(__instance.rootNode, AnimalPawnRenderNodeTagDefOf.AnimalApparel);
            if (root == null) return;
            foreach (Apparel apparel in pawn.apparel.WornApparel)
            {
                if (apparel == null || apparel.def.IsWeapon || AnimalGearHelper.InvisibleForAnimal(apparel.def)) continue;
                PawnRenderNodeProperties props = new PawnRenderNodeProperties
                {
                    debugLabel = apparel.def.defName,
                    workerClass = typeof(PawnRenderNodeWorker_Animal_Apparel),
                    baseLayer = root.Props.baseLayer,
                    drawData = apparel.def.apparel.drawData
                };
                AddChild(__instance, new PawnRenderNode_Animal_Apparel(pawn, props, __instance, apparel), root);
            }
        }
    }
}
