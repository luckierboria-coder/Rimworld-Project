using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AnimalGear
{
    public class AnimalApparelDefExtension : DefModExtension
    {
        public BodyDef showCoverageForBodyType;
    }

    public static class AnimalGearHelper
    {
        private static MethodInfo isSapientAnimalMethod;
        private static MethodInfo animalSourceForMethod;
        private static readonly Dictionary<ThingDef, bool> invisibleCache = new Dictionary<ThingDef, bool>();

        public static bool IsAnimal(this Pawn pawn)
        {
            return pawn != null && pawn.def != null && pawn.def.race != null && pawn.def.race.intelligence == Intelligence.Animal;
        }

        public static bool IsAnimalOfColony(this Pawn pawn)
        {
            return pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.IsAnimal() && pawn.RaceProps.FleshType != FleshTypeDefOf.Mechanoid;
        }

        public static bool IsAnimalOfAFaction(this Pawn pawn)
        {
            return pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && pawn.IsAnimal() && pawn.RaceProps.FleshType != FleshTypeDefOf.Mechanoid;
        }

        public static void EnsureInitApparelTrackers(this Pawn pawn)
        {
            if (pawn.outfits == null) pawn.outfits = new Pawn_OutfitTracker(pawn);
            if (pawn.equipment == null) pawn.equipment = new Pawn_EquipmentTracker(pawn);
            if (pawn.apparel == null) pawn.apparel = new Pawn_ApparelTracker(pawn);
        }

        public static bool IsSapientAnimal(this Pawn pawn)
        {
            if (!ModsConfig.IsActive("RedMattis.BetterPrerequisites")) return false;
            if (isSapientAnimalMethod == null)
                isSapientAnimalMethod = AccessTools.Method(AccessTools.TypeByName("BigAndSmall.HumanlikeAnimals"), "IsHumanlikeAnimal");
            return isSapientAnimalMethod != null && (bool)isSapientAnimalMethod.Invoke(null, new object[] { pawn.def });
        }

        public static ThingDef AnimalSourceFor(this Pawn pawn)
        {
            if (!ModsConfig.IsActive("RedMattis.BetterPrerequisites")) return null;
            if (animalSourceForMethod == null)
                animalSourceForMethod = AccessTools.Method(AccessTools.TypeByName("BigAndSmall.HumanlikeAnimals"), "AnimalSourceFor");
            return animalSourceForMethod == null ? null : (ThingDef)animalSourceForMethod.Invoke(null, new object[] { pawn.def });
        }

        public static List<ThingDef> RequiredThingDefFromTags(ApparelProperties props)
        {
            if (props == null || props.tags == null) return new List<ThingDef>();
            return props.tags.Where(x => x.StartsWith("defName", StringComparison.Ordinal))
                .Select(x => x.Substring("defName".Length))
                .Select(DefDatabase<ThingDef>.GetNamedSilentFail)
                .Where(x => x != null).ToList();
        }

        public static bool CanEquipApparel(ThingDef thing, Pawn pawn, ref string cantReason)
        {
            if (thing == null || thing.apparel == null) return false;
            return CanEquipApparel(thing.apparel, pawn, ref cantReason);
        }

        public static bool CanEquipApparel(ApparelProperties props, Pawn pawn, ref string cantReason)
        {
            if (props == null || pawn == null) return false;
            List<string> tags = props.tags;
            bool animal = pawn.IsAnimal() || pawn.IsSapientAnimal();
            if (tags == null || tags.Count == 0)
            {
                if (animal) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
                return true;
            }

            bool hasSpecific = tags.Any(x => x.StartsWith("defName", StringComparison.Ordinal));
            if (hasSpecific)
            {
                List<ThingDef> req = RequiredThingDefFromTags(props);
                bool ok = req.Contains(pawn.def) || (pawn.IsSapientAnimal() && req.Contains(pawn.AnimalSourceFor()));
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
            if (def == null) return false;
            bool value;
            if (!invisibleCache.TryGetValue(def, out value))
            {
                value = def.apparel != null && def.apparel.tags != null && def.apparel.tags.Contains("AnimalInvisible");
                invisibleCache[def] = value;
            }
            return value;
        }
    }

    [StaticConstructorOnStartup]
    public static class AnimalGearBootstrap
    {
        static AnimalGearBootstrap()
        {
            new Harmony("Ingendum.AnimalApparelFramework.Backport15").PatchAll();
            Log.Message("[Animal Apparel Framework 1.5 Backport] Active on RimWorld 1.5.4063.");
        }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility), "CreateInitialComponents")]
    public static class Patch_CreateInitialComponents
    {
        public static void Postfix(Pawn pawn)
        {
            if (pawn.IsAnimalOfAFaction()) pawn.EnsureInitApparelTrackers();
        }
    }

    [HarmonyPatch(typeof(PawnComponentsUtility), "AddAndRemoveDynamicComponents")]
    public static class Patch_AddAndRemoveDynamicComponents
    {
        public static void Postfix(Pawn pawn, bool actAsIfSpawned)
        {
            if (pawn.IsAnimalOfAFaction()) pawn.EnsureInitApparelTrackers();
        }
    }

    [HarmonyPatch(typeof(Pawn_ApparelTracker), "Notify_ApparelChanged")]
    public static class Patch_NotifyApparelChanged
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
    public static class Patch_GearTabVisible
    {
        public static void Postfix(ref bool __result)
        {
            if (__result) return;
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn.IsAnimalOfAFaction()) __result = true;
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "CanTakeOrder")]
    public static class Patch_CanTakeOrder
    {
        public static void Postfix(Pawn pawn, ref bool __result)
        {
            if (pawn.IsAnimalOfColony()) __result = true;
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "ChoicesAtFor")]
    public static class Patch_ChoicesAtFor
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
            if (!AnimalGearHelper.CanEquipApparel(apparel.def, pawn, ref reason))
            {
                __result.Add(new FloatMenuOption("CannotWear".Translate(apparel.Label, apparel) + ": " + reason, null));
                return;
            }
            if (!ApparelUtility.HasPartsToWear(pawn, apparel.def))
            {
                __result.Add(new FloatMenuOption("CannotWear".Translate(apparel.Label, apparel) + ": " + "CannotWearBecauseOfMissingBodyParts".Translate().CapitalizeFirst(), null));
                return;
            }
            if (!pawn.CanReach(apparel, PathEndMode.Touch, Danger.Deadly))
            {
                __result.Add(new FloatMenuOption("CannotWear".Translate(apparel.Label, apparel) + ": " + "NoPath".Translate().CapitalizeFirst(), null));
                return;
            }

            FloatMenuOption option = new FloatMenuOption("ForceWear".Translate(apparel.LabelShort, apparel), delegate
            {
                apparel.SetForbidden(false, false);
                Job job = JobMaker.MakeJob(JobDefOf.Wear, apparel);
                pawn.jobs.TryTakeOrderedJob(job, JobTag.Misc);
            }, MenuOptionPriority.High);
            __result.Add(FloatMenuUtility.DecoratePrioritizedTask(option, pawn, apparel, "ReservedBy"));
        }
    }

    [HarmonyPatch(typeof(Apparel), "PawnCanWear")]
    public static class Patch_ApparelPawnCanWear
    {
        public static void Postfix(Apparel __instance, Pawn pawn, ref bool __result)
        {
            if (!__result || __instance == null || __instance.def == null || __instance.def.apparel == null) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipApparel(__instance.def, pawn, ref reason);
        }
    }

    [HarmonyPatch(typeof(ApparelProperties), "PawnCanWear")]
    public static class Patch_ApparelPropertiesPawnCanWear
    {
        public static void Postfix(ApparelProperties __instance, Pawn pawn, ref bool __result)
        {
            if (!__result) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipApparel(__instance, pawn, ref reason);
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

    public static class RenderHelpers
    {
        public static bool TryGetGraphicApparelForAnimal(Apparel apparel, Pawn pawn, out ApparelGraphicRecord rec)
        {
            rec = default(ApparelGraphicRecord);
            if (apparel == null || pawn == null || apparel.WornGraphicPath.NullOrEmpty()) return false;
            string basePath = apparel.WornGraphicPath;
            Shader shader = ShaderDatabase.Cutout;
            if (apparel.StyleDef != null && apparel.StyleDef.graphicData != null && apparel.StyleDef.graphicData.shaderType != null)
                shader = apparel.StyleDef.graphicData.shaderType.Shader;
            else if (apparel.def.apparel.useWornGraphicMask)
                shader = ShaderDatabase.CutoutComplex;

            ApparelProperties props = apparel.def.apparel;
            bool perAnimal = props != null && props.tags != null && (props.tags.Any(t => t.StartsWith("defName", StringComparison.Ordinal)) || props.tags.Contains("AnimalFallbackInvisible"));
            string chosenPath = basePath;
            if (perAnimal)
            {
                string defName = pawn.def.defName;
                ThingDef source = pawn.IsSapientAnimal() ? pawn.AnimalSourceFor() : null;
                if (source != null) defName = source.defName;
                string cap = defName.CapitalizeFirst();
                string specific = basePath + "/" + cap + "/" + cap;
                if (ContentFinder<Texture2D>.Get(specific + "_east", false) != null)
                    chosenPath = specific;
                else if (ContentFinder<Texture2D>.Get(basePath + "_east", false) == null)
                {
                    if (props.tags.Contains("AnimalFallbackInvisible")) return false;
                    Log.Error("[Animal Apparel Framework 1.5] Missing graphic for " + apparel.def.defName + " at " + specific + " or " + basePath);
                    return false;
                }
            }
            if (ContentFinder<Texture2D>.Get(chosenPath + "_eastm", false) != null)
                shader = ShaderDatabase.CutoutComplex;
            Graphic graphic = GraphicDatabase.Get<Graphic_Multi>(chosenPath, shader, apparel.def.graphicData.drawSize, apparel.DrawColor);
            rec = new ApparelGraphicRecord(graphic, apparel);
            return true;
        }
    }

    public class PawnRenderNode_Animal_Apparel : PawnRenderNode
    {
        public PawnRenderNode_Animal_Apparel(Pawn pawn, PawnRenderNodeProperties props, PawnRenderTree tree, Apparel apparel) : base(pawn, props, tree)
        {
            this.apparel = apparel;
        }

        public override GraphicMeshSet MeshSetFor(Pawn pawn)
        {
            PawnRenderNode body;
            tree.TryGetNodeByTag(PawnRenderNodeTagDefOf.Body, out body);
            if (body != null) return body.MeshSetFor(pawn);
            float size = 1f;
            if (pawn.ageTracker != null && pawn.ageTracker.CurKindLifeStage != null && pawn.ageTracker.CurKindLifeStage.bodyGraphicData != null)
                size = pawn.ageTracker.CurKindLifeStage.bodyGraphicData.drawSize.x;
            return MeshPool.GetMeshSetForSize(size, size);
        }

        protected override IEnumerable<Graphic> GraphicsFor(Pawn pawn)
        {
            ApparelGraphicRecord rec;
            if (RenderHelpers.TryGetGraphicApparelForAnimal(apparel, pawn, out rec) && rec.graphic != null)
                yield return rec.graphic;
        }
    }

    public class PawnRenderNodeWorker_Animal_Apparel : PawnRenderNodeWorker
    {
        public override bool CanDrawNow(PawnRenderNode node, PawnDrawParms parms)
        {
            return base.CanDrawNow(node, parms);
        }
    }

    [HarmonyPatch(typeof(PawnRenderTree), "SetupDynamicNodes")]
    public static class Patch_SetupDynamicNodes
    {
        private delegate void AddChildDelegate(PawnRenderTree tree, PawnRenderNode child, PawnRenderNode parent);
        private static readonly AddChildDelegate AddChild = AccessTools.MethodDelegate<AddChildDelegate>(AccessTools.Method(typeof(PawnRenderTree), "AddChild"));

        public static void Postfix(PawnRenderTree __instance)
        {
            Pawn pawn = __instance == null ? null : __instance.pawn;
            if (pawn == null || !pawn.IsAnimalOfAFaction() || pawn.apparel == null || pawn.apparel.WornApparelCount == 0) return;
            PawnRenderNode root;
            __instance.TryGetNodeByTag(AnimalPawnRenderNodeTagDefOf.AnimalApparel, out root);
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
