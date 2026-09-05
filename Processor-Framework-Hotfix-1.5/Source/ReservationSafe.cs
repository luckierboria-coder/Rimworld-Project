using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.ProcessorFrameworkHotfix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        public const string Version = "0.1.3-reservation-safe";

        static Bootstrap()
        {
            try
            {
                new Harmony("allen.processorframework.hotfix15").PatchAll();
                Log.Message("[Processor Framework Hotfix 1.5 v0.1.3 Reservation Safe] Active. Final Ingredient Thing caching removed; live per-def ListerThings candidate index + current-pawn reservation validation enabled. Processor reservation authority aligned to maxPawns=1.");
            }
            catch (Exception ex)
            {
                Log.Error("[Processor Framework Hotfix 1.5 v0.1.3 Reservation Safe] Initialization failed; Processor Framework remains authoritative. " + ex);
            }
        }
    }

    internal static class ProcessorReflection
    {
        internal static readonly Type WorkGiverType = AccessTools.TypeByName("ProcessorFramework.WorkGiver_FillProcessor");
        internal static readonly Type CompProcessorType = AccessTools.TypeByName("ProcessorFramework.CompProcessor");
        internal static readonly Type ProcessFilterType = AccessTools.TypeByName("ProcessorFramework.ProcessFilter");

        internal static readonly FieldInfo EnabledProcessesField = CompProcessorType == null ? null : AccessTools.Field(CompProcessorType, "enabledProcesses");
        internal static readonly MethodInfo SpaceLeftForMethod = CompProcessorType == null ? null : AccessTools.Method(CompProcessorType, "SpaceLeftFor");
        internal static readonly FieldInfo AllowedIngredientsField = ProcessFilterType == null ? null : AccessTools.Field(ProcessFilterType, "allowedIngredients");

        internal static bool Ready => WorkGiverType != null && CompProcessorType != null && ProcessFilterType != null && EnabledProcessesField != null && SpaceLeftForMethod != null && AllowedIngredientsField != null;
    }

    [HarmonyPatch]
    internal static class FindIngredientReservationSafePatch
    {
        [ThreadStatic] private static Dictionary<ThingDef, object> processByIngredient;
        [ThreadStatic] private static List<Thing> candidates;
        [ThreadStatic] private static object[] oneArg;
        private static bool warned;

        private static MethodBase TargetMethod()
        {
            if (ProcessorReflection.WorkGiverType == null || ProcessorReflection.CompProcessorType == null) return null;
            return AccessTools.Method(ProcessorReflection.WorkGiverType, "FindIngredient", new[] { typeof(Pawn), ProcessorReflection.CompProcessorType });
        }

        private static bool Prefix(Pawn __0, object __1, ref Thing __result)
        {
            if (!ProcessorReflection.Ready || __0 == null || __1 == null) return true;

            try
            {
                ThingComp comp = __1 as ThingComp;
                Map map = __0.Map;
                if (comp?.parent == null || map == null || comp.parent.Map != map) return true;

                IDictionary enabled = ProcessorReflection.EnabledProcessesField.GetValue(__1) as IDictionary;
                if (enabled == null || enabled.Count == 0)
                {
                    __result = null;
                    return false;
                }

                Dictionary<ThingDef, object> processMap = processByIngredient ?? (processByIngredient = new Dictionary<ThingDef, object>());
                processMap.Clear();

                foreach (DictionaryEntry entry in enabled)
                {
                    if (entry.Key == null || entry.Value == null) continue;
                    IEnumerable allowed = ProcessorReflection.AllowedIngredientsField.GetValue(entry.Value) as IEnumerable;
                    if (allowed == null) continue;
                    foreach (object value in allowed)
                    {
                        ThingDef def = value as ThingDef;
                        if (def != null && !processMap.ContainsKey(def))
                        {
                            // Match Processor Framework's FirstOrDefault semantics when one ingredient is allowed by multiple processes.
                            processMap.Add(def, entry.Key);
                        }
                    }
                }

                if (processMap.Count == 0)
                {
                    __result = null;
                    return false;
                }

                List<Thing> source = candidates ?? (candidates = new List<Thing>(128));
                source.Clear();

                // Reservation Safe v0.1.3 deliberately has NO cached final Thing and NO negative-result cache.
                // We use RimWorld's own live per-ThingDef lister buckets as the index, so newly spawned/despawned
                // ingredients and reservation changes are visible on every call.
                foreach (ThingDef def in processMap.Keys)
                {
                    List<Thing> bucket = map.listerThings.ThingsOfDef(def);
                    if (bucket != null && bucket.Count != 0) source.AddRange(bucket);
                }

                if (source.Count == 0)
                {
                    __result = null;
                    return false;
                }

                Predicate<Thing> validator = delegate(Thing x)
                {
                    if (x == null || x.Destroyed || !x.Spawned || x.Map != map) return false;
                    if (x.IsForbidden(__0)) return false;

                    object processDef;
                    if (!processMap.TryGetValue(x.def, out processDef) || processDef == null) return false;

                    int spaceLeft;
                    try
                    {
                        object[] args = oneArg ?? (oneArg = new object[1]);
                        args[0] = processDef;
                        object raw = ProcessorReflection.SpaceLeftForMethod.Invoke(__1, args);
                        spaceLeft = raw is int ? (int)raw : Convert.ToInt32(raw);
                        args[0] = null;
                    }
                    catch
                    {
                        return false;
                    }

                    if (spaceLeft < 1) return false;
                    int carrySpace = __0.carryTracker.AvailableStackSpace(x.def);
                    int reserveCount = Math.Min(spaceLeft, Math.Min(x.stackCount, carrySpace));
                    if (reserveCount < 1) return false;

                    // Critical fix: this must be evaluated for the CURRENT pawn on every lookup.
                    if (!__0.CanReserve(x, 1, reserveCount)) return false;
                    return true;
                };

                __result = GenClosest.ClosestThingReachable(
                    __0.Position,
                    map,
                    ThingRequest.ForGroup(ThingRequestGroup.HaulableEver),
                    PathEndMode.ClosestTouch,
                    TraverseParms.For(__0),
                    9999f,
                    validator,
                    source);

                return false;
            }
            catch (Exception ex)
            {
                if (!warned)
                {
                    warned = true;
                    Log.Warning("[Processor Framework Hotfix 1.5 v0.1.3 Reservation Safe] Safe ingredient lookup failed once; falling back to Processor Framework original method. " + ex);
                }
                return true;
            }
        }
    }

    [HarmonyPatch]
    internal static class HasJobOnThingProcessorReservationPatch
    {
        private static MethodBase TargetMethod()
        {
            if (ProcessorReflection.WorkGiverType == null) return null;
            return AccessTools.Method(ProcessorReflection.WorkGiverType, "HasJobOnThing", new[] { typeof(Pawn), typeof(Thing), typeof(bool) });
        }

        private static void Postfix(Pawn __0, Thing __1, bool __2, ref bool __result)
        {
            if (!__result || __0 == null || __1 == null) return;
            try
            {
                // Processor Framework's JobDriver reserves the processor with maxPawns=1.
                // Keep WorkGiver admission consistent with that real authority instead of the original maxPawns=10 probe.
                if (!__0.CanReserveAndReach(__1, PathEndMode.Touch, __0.NormalMaxDanger(), 1, 0, null, __2))
                {
                    __result = false;
                }
            }
            catch
            {
                // Fail-open to Processor Framework if another mod changes reservation semantics unexpectedly.
            }
        }
    }

    [HarmonyPatch]
    internal static class JobOnThingFinalReservationGuardPatch
    {
        private static MethodBase TargetMethod()
        {
            if (ProcessorReflection.WorkGiverType == null) return null;
            return AccessTools.Method(ProcessorReflection.WorkGiverType, "JobOnThing", new[] { typeof(Pawn), typeof(Thing), typeof(bool) });
        }

        private static void Postfix(Pawn __0, bool __2, ref Job __result)
        {
            if (__result == null || __0 == null) return;
            try
            {
                Thing processor = __result.targetA.Thing;
                Thing ingredient = __result.targetB.Thing;
                int count = Math.Max(1, __result.count);

                if (processor == null || ingredient == null || ingredient.Destroyed || !ingredient.Spawned || ingredient.Map != __0.Map)
                {
                    __result = null;
                    return;
                }

                if (!__0.CanReserveAndReach(processor, PathEndMode.Touch, __0.NormalMaxDanger(), 1, 0, null, __2)
                    || ingredient.IsForbidden(__0)
                    || !__0.CanReserve(ingredient, 1, count))
                {
                    // Do not emit a stale FillProcessor Job that the JobDriver will immediately fail to reserve.
                    __result = null;
                }
            }
            catch
            {
                // Fail-open: JobDriver_FillProcessor still performs authoritative reservations before toil execution.
            }
        }
    }
}
