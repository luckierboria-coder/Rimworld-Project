using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using WorldTechLevel;

namespace WTLFinalGuard
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        private const string HarmonyId = "allen.wtl.finalguard";
        private static Harmony harmony;

        static Bootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(Initialize);
        }

        private static void Initialize()
        {
            try
            {
                if (!ModsConfig.IsActive("m00nl1ght.WorldTechLevel") && !ModsConfig.IsActive("m00nl1ght.WorldTechLevel_steam"))
                {
                    Log.Warning("[WTL Final Guard] World Tech Level is not active; guard not installed.");
                    return;
                }

                harmony = new Harmony(HarmonyId);

                Patch(typeof(PawnGenerator), nameof(PawnGenerator.GeneratePawn), new[] { typeof(PawnGenerationRequest) },
                    prefix: nameof(FinalGuard.GeneratePawn_LatePrefix), finalizer: nameof(FinalGuard.GeneratePawn_Finalizer));

                Patch(typeof(PawnGenerator), nameof(PawnGenerator.GenerateGearFor), null,
                    finalizer: nameof(FinalGuard.GenerateGearFor_Finalizer));

                Patch(typeof(ThingSetMaker), nameof(ThingSetMaker.Generate), new[] { typeof(ThingSetMakerParams) },
                    finalizer: nameof(FinalGuard.ThingSetMaker_Finalizer));

                Patch(typeof(StockGenerator_Category), nameof(StockGenerator.GenerateThings), null,
                    finalizer: nameof(FinalGuard.StockGenerator_Finalizer));
                Patch(typeof(StockGenerator_MiscItems), nameof(StockGenerator.GenerateThings), null,
                    finalizer: nameof(FinalGuard.StockGenerator_Finalizer));
                Patch(typeof(StockGenerator_Tag), nameof(StockGenerator.GenerateThings), null,
                    finalizer: nameof(FinalGuard.StockGenerator_Finalizer));

                Patch(typeof(QuestManager), nameof(QuestManager.Add), null,
                    prefix: nameof(FinalGuard.QuestManager_Add_LatePrefix));

                Patch(typeof(IncidentWorker), nameof(IncidentWorker.TryExecute), new[] { typeof(IncidentParms) },
                    prefix: nameof(FinalGuard.IncidentWorker_TryExecute_LatePrefix));

                PatchPawnSpawnEntrypoints();

                Log.Message("[WTL Final Guard] RimWorld 1.5 final-generation guards installed.");
            }
            catch (Exception e)
            {
                Log.Error("[WTL Final Guard] Failed to initialize: " + e);
            }
        }

        private static void Patch(Type type, string methodName, Type[] argumentTypes, string prefix = null, string postfix = null, string finalizer = null)
        {
            MethodInfo original = argumentTypes == null
                ? AccessTools.Method(type, methodName)
                : AccessTools.Method(type, methodName, argumentTypes);

            if (original == null)
            {
                Log.Warning($"[WTL Final Guard] Target not found: {type.FullName}.{methodName}");
                return;
            }

            harmony.Patch(original,
                MakePatch(prefix, Priority.Last),
                MakePatch(postfix, Priority.Last),
                null,
                MakePatch(finalizer, Priority.Last));
        }

        private static HarmonyMethod MakePatch(string name, int priority)
        {
            if (name.NullOrEmpty()) return null;
            MethodInfo method = AccessTools.Method(typeof(FinalGuard), name);
            if (method == null) throw new MissingMethodException(typeof(FinalGuard).FullName, name);
            return new HarmonyMethod(method) { priority = priority };
        }

        private static void PatchPawnSpawnEntrypoints()
        {
            foreach (MethodInfo method in typeof(GenSpawn).GetMethods(AccessTools.all)
                         .Where(m => m.Name == nameof(GenSpawn.Spawn)))
            {
                ParameterInfo[] parameters = method.GetParameters();
                if (parameters.Length == 0 || parameters[0].ParameterType != typeof(Thing)) continue;

                harmony.Patch(method,
                    prefix: new HarmonyMethod(AccessTools.Method(typeof(FinalGuard), nameof(FinalGuard.GenSpawn_PawnPrefix)))
                    {
                        priority = Priority.Last
                    });
            }
        }
    }

    public static class FinalGuard
    {
        private static readonly BindingFlags StaticFlags = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        private static readonly FieldInfo WtlSettingsField = typeof(WorldTechLevel.WorldTechLevel).GetField("Settings", StaticFlags);
        private static readonly FieldInfo KindDefInnerField = typeof(PawnGenerationRequest).GetField("kindDefInner", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly HashSet<int> ErrorKeys = new HashSet<int>();

        public static void GeneratePawn_LatePrefix(ref PawnGenerationRequest request)
        {
            try
            {
                if (!FilterEnabled("Filter_PawnKinds")) return;
                if (request.Context == PawnGenerationContext.PlayerStarter) return;

                PawnKindDef kind = request.KindDef;
                TechLevel limit = request.Faction.CurrentFilterLevel();
                if (kind == null || limit == TechLevel.Archotech || kind.MinRequiredTechLevel() <= limit) return;

                TechLevel minLevel = TechLevelUtility.Min(limit, TechLevel.Medieval);
                PawnKindDef replacement = kind.GetAlternative(limit, minLevel);
                if (replacement == null || KindDefInnerField == null) return;

                object boxed = request;
                KindDefInnerField.SetValue(boxed, replacement);
                request = (PawnGenerationRequest)boxed;

                DevLog($"late PawnKind replacement: {kind.defName} -> {replacement.defName}");
            }
            catch (Exception e)
            {
                GuardError("GeneratePawn late prefix", e);
            }
        }

        public static Exception GeneratePawn_Finalizer(ref Pawn __result, Exception __exception)
        {
            if (__exception == null && __result != null) SafeSanitizePawn(__result, "GeneratePawn");
            return __exception;
        }

        public static Exception GenerateGearFor_Finalizer(Pawn pawn, Exception __exception)
        {
            if (__exception == null && pawn != null) SafeSanitizePawn(pawn, "GenerateGearFor");
            return __exception;
        }

        public static void GenSpawn_PawnPrefix(Thing newThing)
        {
            Pawn pawn = newThing as Pawn;
            if (pawn == null || pawn.Faction == null || pawn.Faction.IsPlayer) return;
            SafeSanitizePawn(pawn, "GenSpawn");
        }

        public static Exception ThingSetMaker_Finalizer(ThingSetMakerParams parms, ref List<Thing> __result, Exception __exception)
        {
            if (__exception != null || __result == null || !FilterEnabled("Filter_Items")) return __exception;

            try
            {
                TechLevel limit = parms.makingFaction.CurrentFilterLevel();
                if (limit == TechLevel.Archotech) return __exception;

                if (parms.traderDef != null && parms.traderDef.orbital && SettingBool("AlwaysAllowOffworld", false))
                    return __exception;

                SanitizeThingList(__result, limit, null);
            }
            catch (Exception e)
            {
                GuardError("ThingSetMaker finalizer", e);
            }

            return __exception;
        }

        public static Exception StockGenerator_Finalizer(StockGenerator __instance, Faction faction, ref IEnumerable<Thing> __result, Exception __exception)
        {
            if (__exception != null || __result == null || !FilterEnabled("Filter_Items")) return __exception;

            try
            {
                if (__instance.trader != null && __instance.trader.orbital && SettingBool("AlwaysAllowOffworld", false))
                    return __exception;

                TechLevel limit = faction.CurrentFilterLevel();
                if (limit != TechLevel.Archotech)
                    __result = SanitizeThingEnumerable(__result, limit);
            }
            catch (Exception e)
            {
                GuardError("StockGenerator finalizer", e);
            }

            return __exception;
        }

        public static bool QuestManager_Add_LatePrefix(Quest quest)
        {
            try
            {
                if (!FilterEnabled("Filter_Quests") || quest?.root == null) return true;
                return quest.root.MinRequiredTechLevel() <= WorldTechLevel.WorldTechLevel.Current;
            }
            catch (Exception e)
            {
                GuardError("QuestManager.Add late prefix", e);
                return true;
            }
        }

        public static bool IncidentWorker_TryExecute_LatePrefix(IncidentWorker __instance)
        {
            try
            {
                if (!FilterEnabled("Filter_Incidents") || __instance?.def == null) return true;
                return __instance.def.MinRequiredTechLevel() <= WorldTechLevel.WorldTechLevel.Current;
            }
            catch (Exception e)
            {
                GuardError("IncidentWorker.TryExecute late prefix", e);
                return true;
            }
        }

        private static void SafeSanitizePawn(Pawn pawn, string source)
        {
            try
            {
                if (pawn == null || pawn.Faction == null) return;
                if (pawn.IsStartingPawnGen()) return;

                TechLevel limit = pawn.Faction.CurrentFilterLevel();
                if (limit == TechLevel.Archotech) return;

                if (FilterEnabled("Filter_Possessions")) SanitizeInventory(pawn, limit);
                if (FilterEnabled("Filter_Apparel")) SanitizeApparel(pawn, limit);
                if (FilterEnabled("Filter_Weapons")) SanitizeEquipment(pawn, limit);
                if (FilterEnabled("Filter_Prosthetics")) SanitizeArtificialHediffs(pawn, limit);
                if (FilterEnabled("Filter_Traits")) SanitizeTraits(pawn, limit);
                if (FilterEnabled("Filter_Xenotypes")) SanitizeXenotype(pawn, limit);

                if (FilterEnabled("Filter_PawnKinds") && pawn.kindDef != null && pawn.kindDef.MinRequiredTechLevel() > limit)
                {
                    DevLog($"{source}: pawn {pawn.LabelShortCap} still has over-tech PawnKind {pawn.kindDef.defName}; request replacement was unavailable or bypassed.");
                }
            }
            catch (Exception e)
            {
                GuardError("pawn sanitization from " + source, e);
            }
        }

        private static void SanitizeInventory(Pawn pawn, TechLevel limit)
        {
            if (pawn.inventory?.innerContainer == null) return;
            List<Thing> bad = pawn.inventory.innerContainer.Where(t => IsOverTech(t, limit)).ToList();

            foreach (Thing thing in bad)
            {
                pawn.inventory.innerContainer.Remove(thing);
                Thing replacement = SafeReplacement(thing, pawn, limit);
                if (replacement != null) pawn.inventory.innerContainer.TryAdd(replacement);
                DevLog($"inventory: removed {thing.def.defName}" + (replacement == null ? "" : $", replaced with {replacement.def.defName}"));
            }
        }

        private static void SanitizeApparel(Pawn pawn, TechLevel limit)
        {
            if (pawn.apparel == null) return;
            List<Apparel> bad = pawn.apparel.WornApparel.Where(t => IsOverTech(t, limit)).ToList();

            foreach (Apparel apparel in bad)
            {
                pawn.apparel.Remove(apparel);
                Apparel replacement = SafeReplacement(apparel, pawn, limit) as Apparel;
                if (replacement != null)
                {
                    try { pawn.apparel.Wear(replacement, false); }
                    catch { replacement.Destroy(); }
                }
                DevLog($"apparel: removed {apparel.def.defName}" + (replacement == null ? "" : $", replaced with {replacement.def.defName}"));
            }
        }

        private static void SanitizeEquipment(Pawn pawn, TechLevel limit)
        {
            if (pawn.equipment == null) return;
            List<ThingWithComps> bad = pawn.equipment.AllEquipmentListForReading.Where(t => IsOverTech(t, limit)).ToList();

            foreach (ThingWithComps equipment in bad)
            {
                pawn.equipment.Remove(equipment);
                ThingWithComps replacement = SafeReplacement(equipment, pawn, limit) as ThingWithComps;
                if (replacement != null) pawn.equipment.AddEquipment(replacement);
                DevLog($"equipment: removed {equipment.def.defName}" + (replacement == null ? "" : $", replaced with {replacement.def.defName}"));
            }
        }

        private static void SanitizeArtificialHediffs(Pawn pawn, TechLevel limit)
        {
            if (pawn.health?.hediffSet?.hediffs == null) return;

            List<Hediff> bad = new List<Hediff>();
            foreach (Hediff hediff in pawn.health.hediffSet.hediffs)
            {
                if (!LooksArtificial(hediff)) continue;
                ThingDef sourceDef = HediffSourceThing(hediff);
                if (sourceDef != null && sourceDef.MinRequiredTechLevel() > limit)
                    bad.Add(hediff);
            }

            foreach (Hediff hediff in bad)
            {
                string name = hediff.def.defName;
                pawn.health.RemoveHediff(hediff);
                DevLog("prosthetic/implant: removed " + name);
            }
        }

        private static void SanitizeTraits(Pawn pawn, TechLevel limit)
        {
            if (pawn.story?.traits?.allTraits == null) return;
            List<Trait> bad = pawn.story.traits.allTraits.Where(t => t.def.MinRequiredTechLevel() > limit).ToList();
            foreach (Trait trait in bad)
            {
                pawn.story.traits.RemoveTrait(trait);
                DevLog("trait: removed " + trait.def.defName);
            }
        }

        private static void SanitizeXenotype(Pawn pawn, TechLevel limit)
        {
            if (!ModsConfig.BiotechActive || pawn.genes == null || pawn.genes.Xenotype == null) return;
            XenotypeDef xenotype = pawn.genes.Xenotype;
            if (xenotype.MinRequiredTechLevel() <= limit) return;

            MethodInfo setter = AccessTools.Method(pawn.genes.GetType(), "SetXenotype", new[] { typeof(XenotypeDef) });
            if (setter != null)
            {
                setter.Invoke(pawn.genes, new object[] { XenotypeDefOf.Baseliner });
                DevLog($"xenotype: replaced {xenotype.defName} -> {XenotypeDefOf.Baseliner.defName}");
            }
        }

        private static bool LooksArtificial(Hediff hediff)
        {
            if (hediff == null || hediff.def == null) return false;
            if (hediff is Hediff_AddedPart) return true;
            if (hediff.def.spawnThingOnRemoved != null) return true;
            string typeName = hediff.GetType().Name;
            return typeName.IndexOf("Implant", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   typeName.IndexOf("Prosthetic", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static ThingDef HediffSourceThing(Hediff hediff)
        {
            if (hediff?.def == null) return null;
            if (hediff.def.spawnThingOnRemoved != null) return hediff.def.spawnThingOnRemoved;
            return DefDatabase<ThingDef>.GetNamedSilentFail(hediff.def.defName);
        }

        private static bool IsOverTech(Thing thing, TechLevel limit)
        {
            return thing != null && thing.MinRequiredTechLevel() > limit;
        }

        private static Thing SafeReplacement(Thing thing, Pawn owner, TechLevel limit)
        {
            try
            {
                Thing replacement = ReplacementUtility.TryMakeReplacementFor(thing, owner);
                if (replacement != null && !IsOverTech(replacement, limit)) return replacement;
                replacement?.Destroy();
            }
            catch (Exception e)
            {
                GuardError("replacement for " + thing?.def?.defName, e);
            }
            return null;
        }

        private static void SanitizeThingList(List<Thing> things, TechLevel limit, Pawn owner)
        {
            for (int i = things.Count - 1; i >= 0; i--)
            {
                Thing thing = things[i];
                Pawn pawn = thing as Pawn;
                if (pawn != null)
                {
                    SafeSanitizePawn(pawn, "ThingSetMaker");
                    continue;
                }

                if (!IsOverTech(thing, limit)) continue;
                Thing replacement = SafeReplacement(thing, owner, limit);
                if (replacement == null) things.RemoveAt(i);
                else things[i] = replacement;
            }
        }

        private static IEnumerable<Thing> SanitizeThingEnumerable(IEnumerable<Thing> source, TechLevel limit)
        {
            foreach (Thing thing in source)
            {
                if (thing == null) continue;
                if (!IsOverTech(thing, limit))
                {
                    yield return thing;
                    continue;
                }

                Thing replacement = SafeReplacement(thing, null, limit);
                if (replacement != null) yield return replacement;
            }
        }

        private static bool FilterEnabled(string fieldName)
        {
            return SettingBool(fieldName, true);
        }

        private static bool SettingBool(string fieldName, bool fallback)
        {
            try
            {
                object settings = WtlSettingsField?.GetValue(null);
                if (settings == null) return fallback;
                FieldInfo field = settings.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object entry = field?.GetValue(settings);
                if (entry == null) return fallback;

                Type entryType = entry.GetType();
                PropertyInfo valueProperty = entryType.GetProperty("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (valueProperty?.GetValue(entry, null) is bool propertyValue) return propertyValue;

                FieldInfo valueField = entryType.GetField("Value", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (valueField?.GetValue(entry) is bool fieldValue) return fieldValue;
            }
            catch (Exception e)
            {
                GuardError("reading WTL setting " + fieldName, e);
            }
            return fallback;
        }

        private static void DevLog(string message)
        {
            if (Prefs.DevMode) Log.Message("[WTL Final Guard] " + message);
        }

        private static void GuardError(string context, Exception e)
        {
            int key = Gen.HashCombineInt(context.GetHashCode(), e.GetType().FullName.GetHashCode());
            lock (ErrorKeys)
            {
                if (!ErrorKeys.Add(key)) return;
            }
            Log.Error($"[WTL Final Guard] Error during {context}: {e}");
        }
    }
}
