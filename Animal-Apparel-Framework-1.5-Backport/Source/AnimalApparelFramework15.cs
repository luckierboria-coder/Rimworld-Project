using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

    [DefOf]
    public static class AAF15JobDefOf
    {
        public static JobDef AAF15_EquipAnimal;
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
            RaceProperties race = pawn == null || pawn.def == null ? null : pawn.def.race;
            return pawn != null && pawn.Faction != null && pawn.Faction.IsPlayer && race != null && race.intelligence == Intelligence.Animal && race.FleshType != FleshTypeDefOf.Mechanoid;
        }

        public static void EnsureInitApparelTrackers(this Pawn pawn)
        {
            if (pawn == null) return;
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

        public static bool IsAnimalApparel(ThingDef thing)
        {
            if (thing == null || thing.apparel == null || thing.apparel.tags == null) return false;
            List<string> tags = thing.apparel.tags;
            return tags.Contains("AnimalApparel") || tags.Contains("AnimalOnly") || tags.Any(x => x.StartsWith("defName", StringComparison.Ordinal));
        }

        public static bool CanEquipApparel(ThingDef thing, Pawn pawn, ref string cantReason)
        {
            return thing != null && CanEquipApparel(thing.apparel, pawn, ref cantReason);
        }

        public static bool CanEquipApparel(ApparelProperties properties, Pawn pawn, ref string cantReason)
        {
            if (properties == null || pawn == null) return false;
            List<string> tags = properties.tags;
            if (tags == null || tags.Count == 0)
            {
                if (pawn.IsAnimal()) { cantReason = "ANG_WrongBodyType".Translate(); return false; }
                return true;
            }

            List<ThingDef> required = RequiredThingDefFromTags(properties);
            if (required.Count > 0)
            {
                if (!required.Contains(pawn.def))
                {
                    cantReason = "ANG_WrongBodyType".Translate();
                    return false;
                }
                return true;
            }

            bool animalApparel = tags.Contains("AnimalApparel");
            bool animalOnly = tags.Contains("AnimalOnly");
            if (pawn.IsAnimal() && !(animalApparel || animalOnly))
            {
                cantReason = "ANG_WrongBodyType".Translate();
                return false;
            }
            if (animalOnly && !pawn.IsAnimal())
            {
                cantReason = "ANG_WrongBodyType".Translate();
                return false;
            }
            return true;
        }

        public static BodyDef GetBodyDefForCoverageInfo(ThingDef thing)
        {
            BodyDef body = thing == null ? null : thing.race == null ? null : thing.race.body;
            if (body != null) return body;
            AnimalApparelDefExtension ext = thing == null ? null : thing.GetModExtension<AnimalApparelDefExtension>();
            return ext != null && ext.showCoverageForBodyType != null ? ext.showCoverageForBodyType : BodyDefOf.Human;
        }

        public static string EquippableByString(ThingDef thing)
        {
            if (thing == null || thing.apparel == null) return "ANG_SuitableHuman".Translate();
            List<string> tags = thing.apparel.tags ?? new List<string>();
            bool specific = tags.Any(x => x.StartsWith("defName", StringComparison.Ordinal));
            bool animalOnly = tags.Contains("AnimalOnly");
            bool animal = tags.Contains("AnimalApparel");
            if (specific) return (animalOnly ? "ANG_SuitableSpecificAnimal" : "ANG_SuitableSpecific").Translate();
            if (animalOnly) return "ANG_SuitableAnimal".Translate();
            if (animal) return "ANG_SuitableAnimalHuman".Translate();
            return "ANG_SuitableHuman".Translate();
        }

        public static string EquippableByStringFull(ThingDef thing)
        {
            return "ANG_SuitableFor".Translate() + ": " + EquippableByString(thing);
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

        public static IEnumerable<Pawn> EligibleAnimalsFor(Apparel apparel, Pawn worker)
        {
            Map map = worker == null ? null : worker.Map;
            if (apparel == null || map == null) yield break;
            IReadOnlyList<Pawn> pawns = map.mapPawns.AllPawnsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Pawn animal = pawns[i];
                if (!animal.IsAnimalOfColony() || animal.Dead) continue;
                animal.EnsureInitApparelTrackers();
                string reason = null;
                if (!CanEquipApparel(apparel.def, animal, ref reason)) continue;
                if (!ApparelUtility.HasPartsToWear(animal, apparel.def)) continue;
                if (!worker.CanReserveAndReach(apparel, PathEndMode.Touch, Danger.Deadly)) continue;
                if (!worker.CanReserveAndReach(animal, PathEndMode.Touch, Danger.Deadly)) continue;
                yield return animal;
            }
        }

        public static bool CanEquipThing(bool result, ThingDef thing, Pawn pawn, ref string cantReason)
        {
            if (!result || thing == null || pawn == null || !thing.IsApparel) return result;
            return CanEquipApparel(thing, pawn, ref cantReason);
        }

        public static void StartEquipAnimalJob(Pawn worker, Pawn animal, Apparel apparel)
        {
            if (worker == null || animal == null || apparel == null) return;
            Job job = JobMaker.MakeJob(AAF15JobDefOf.AAF15_EquipAnimal, apparel, animal);
            job.count = 1;
            worker.jobs.TryTakeOrderedJob(job, JobTag.Misc);
        }
    }

    public class JobDriver_EquipAnimal : JobDriver
    {
        private Apparel Apparel => job.GetTarget(TargetIndex.A).Thing as Apparel;
        private Pawn Animal => job.GetTarget(TargetIndex.B).Pawn;

        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.GetTarget(TargetIndex.A), job, 1, 1, null, errorOnFailed)
                && pawn.Reserve(job.GetTarget(TargetIndex.B), job, 1, -1, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDestroyedOrNull(TargetIndex.A);
            this.FailOnDestroyedOrNull(TargetIndex.B);
            this.FailOnForbidden(TargetIndex.A);

            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.Touch);
            yield return Toils_Haul.StartCarryThing(TargetIndex.A, false, false, false);
            yield return Toils_Goto.GotoThing(TargetIndex.B, PathEndMode.Touch);
            yield return Toils_General.Wait(60).WithProgressBarToilDelay(TargetIndex.B);

            Toil equip = new Toil();
            equip.initAction = delegate
            {
                Pawn animal = Animal;
                Apparel apparel = pawn.carryTracker.CarriedThing as Apparel ?? Apparel;
                if (animal == null || apparel == null) return;
                animal.EnsureInitApparelTrackers();
                string reason = null;
                if (!AnimalGearHelper.CanEquipApparel(apparel.def, animal, ref reason) || !ApparelUtility.HasPartsToWear(animal, apparel.def))
                {
                    Messages.Message(reason.NullOrEmpty() ? "ANG_WrongBodyType".Translate() : reason, animal, MessageTypeDefOf.RejectInput, false);
                    return;
                }
                animal.apparel.Wear(apparel, true, false);
                animal.Drawer.renderer.SetAllGraphicsDirty();
                Messages.Message("AAF15_OrderedWear".Translate(animal.LabelShortCap, apparel.LabelShort), animal, MessageTypeDefOf.TaskCompletion, false);
            };
            equip.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return equip;
        }
    }

    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("Ingendum.AnimalApparelFramework.Backport15").PatchAll();
            int apparelDefs = DefDatabase<ThingDef>.AllDefsListForReading.Count(AnimalGearHelper.IsAnimalApparel);
            Log.Message("[Animal Apparel Framework 1.5 Backport] Active. Animal apparel defs detected: " + apparelDefs + ".");
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

    [HarmonyPatch(typeof(Pawn), "SpawnSetup")]
    public static class PatchPawnSpawnSetup
    {
        public static void Postfix(Pawn __instance) { if (__instance.IsAnimalOfColony()) __instance.EnsureInitApparelTrackers(); }
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
            Pawn pawn = Find.Selector.SingleSelectedThing as Pawn;
            if (pawn.IsAnimalOfColony())
            {
                pawn.EnsureInitApparelTrackers();
                __result = true;
            }
        }
    }

    [HarmonyPatch(typeof(ITab_Pawn_Gear), "get_CanControlColonist")]
    public static class PatchGearTabControl
    {
        public static bool Prefix(ITab_Pawn_Gear __instance, ref bool __result)
        {
            MethodInfo selGetter = AccessTools.PropertyGetter(typeof(ITab_Pawn_Gear), "SelPawnForGear");
            Pawn pawn = selGetter == null ? null : selGetter.Invoke(__instance, null) as Pawn;
            if (!pawn.IsAnimalOfColony()) return true;
            pawn.EnsureInitApparelTrackers();
            __result = pawn.Spawned;
            return false;
        }
    }

    [HarmonyPatch(typeof(Apparel), "PawnCanWear")]
    public static class PatchApparelPawnCanWear
    {
        public static void Postfix(Apparel __instance, Pawn pawn, bool ignoreGender, ref bool __result)
        {
            if (!__result) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipThing(__result, __instance.def, pawn, ref reason);
        }
    }

    [HarmonyPatch(typeof(ApparelProperties), "PawnCanWear", new Type[] { typeof(Pawn), typeof(bool) })]
    public static class PatchApparelPropertiesPawnCanWear
    {
        public static void Postfix(ApparelProperties __instance, Pawn pawn, bool ignoreGender, ref bool __result)
        {
            if (!__result) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipApparel(__instance, pawn, ref reason);
        }
    }

    [HarmonyPatch(typeof(ApparelRequirement), "AllowedForPawn")]
    public static class PatchApparelRequirementAllowed
    {
        public static void Postfix(Pawn p, ThingDef apparel, bool ignoreGender, ref bool __result)
        {
            if (!__result) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipThing(__result, apparel, p, ref reason);
        }
    }

    [HarmonyPatch(typeof(ApparelRequirement), "RequiredForPawn")]
    public static class PatchApparelRequirementRequired
    {
        public static void Postfix(Pawn p, ThingDef apparel, bool ignoreGender, ref bool __result)
        {
            if (!__result) return;
            string reason = null;
            __result = AnimalGearHelper.CanEquipThing(__result, apparel, p, ref reason);
        }
    }

    [HarmonyPatch(typeof(ThingDef), "SpecialDisplayStats")]
    public static class PatchSpecialDisplayStats
    {
        public static IEnumerable<StatDrawEntry> Postfix(IEnumerable<StatDrawEntry> values, StatRequest req, ThingDef __instance)
        {
            bool inserted = false;
            foreach (StatDrawEntry entry in values)
            {
                if (!inserted && __instance.apparel != null && entry.category == StatCategoryDefOf.Apparel)
                {
                    yield return new StatDrawEntry(StatCategoryDefOf.Apparel, "ANG_SuitableFor".Translate(), AnimalGearHelper.EquippableByString(__instance), AnimalGearHelper.EquippableByStringFull(__instance), 2750);
                    List<ThingDef> required = AnimalGearHelper.RequiredThingDefFromTags(__instance.apparel);
                    if (required.Count > 0)
                        yield return new StatDrawEntry(StatCategoryDefOf.Apparel, "ANG_RequireDefName".Translate(), required.Select(d => d.LabelCap.ToString()).ToCommaList(), "ANG_RequiresBodyTypeDesc".Translate(), 2751);
                    inserted = true;
                }
                yield return entry;
            }
        }
    }

    [HarmonyPatch(typeof(FloatMenuMakerMap), "ChoicesAtFor")]
    public static class PatchChoicesAtFor
    {
        public static void Postfix(Vector3 clickPos, Pawn pawn, bool suppressAutoTakeableGoto, ref List<FloatMenuOption> __result)
        {
            if (pawn == null || pawn.Map == null || __result == null || !pawn.IsColonistPlayerControlled || pawn.Downed) return;
            IntVec3 cell = IntVec3.FromVector3(clickPos);
            if (!cell.InBounds(pawn.Map)) return;
            Apparel apparel = pawn.Map.thingGrid.ThingAt<Apparel>(cell);
            if (apparel == null || !AnimalGearHelper.IsAnimalApparel(apparel.def)) return;

            List<Pawn> animals = AnimalGearHelper.EligibleAnimalsFor(apparel, pawn).OrderBy(x => x.LabelShortCap).ToList();
            if (animals.Count == 0)
            {
                __result.Add(new FloatMenuOption("AAF15_NoEligibleAnimals".Translate(), null));
                return;
            }

            __result.Add(new FloatMenuOption("AAF15_ForceEquipOn".Translate(apparel.LabelShort), delegate
            {
                List<FloatMenuOption> animalOptions = new List<FloatMenuOption>();
                foreach (Pawn animal in animals)
                {
                    Pawn chosen = animal;
                    animalOptions.Add(new FloatMenuOption(chosen.LabelShortCap, delegate
                    {
                        apparel.SetForbidden(false, false);
                        AnimalGearHelper.StartEquipAnimalJob(pawn, chosen, apparel);
                    }));
                }
                Find.WindowStack.Add(new FloatMenu(animalOptions));
            }, MenuOptionPriority.High));
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
            Shader shader = ShaderDatabase.Cutout;
            if (apparel.StyleDef != null && apparel.StyleDef.graphicData != null && apparel.StyleDef.graphicData.shaderType != null)
                shader = apparel.StyleDef.graphicData.shaderType.Shader;
            else if (ContentFinder<Texture2D>.Get(chosen + "_eastm", false) != null)
                shader = ShaderDatabase.CutoutComplex;
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
