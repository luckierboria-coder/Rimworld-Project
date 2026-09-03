using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace Allen.PerformanceFishListerHotfix
{
    [StaticConstructorOnStartup]
    public static class PerformanceFishListerHotfixBootstrap
    {
        private const string HarmonyId = "allen.performancefish.listerhotfix";
        private const BindingFlags AllInstance = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

        private static readonly FieldInfo ListsByDefField = AccessTools.Field(typeof(ListerThings), "listsByDef");
        private static readonly FieldInfo ListsByGroupField = AccessTools.Field(typeof(ListerThings), "listsByGroup");
        private static readonly FieldInfo StateHashByGroupField = AccessTools.Field(typeof(ListerThings), "stateHashByGroup");

        private static FieldInfo defIndexMapField;
        private static FieldInfo groupIndexMapField;
        private static FieldInfo thingsCacheField;
        private static FieldInfo thingsByTypeField;
        private static MethodInfo initializeThingsListMethod;
        private static int recoveryCount;
        private static bool fieldsScanned;

        static PerformanceFishListerHotfixBootstrap()
        {
            try
            {
                ApplyPatches();
                Log.Message("[Performance Fish Lister Hotfix] " + "PFHotfix_Active".Translate());
            }
            catch (Exception ex)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed to initialize: " + ex);
            }
        }

        private static void ApplyPatches()
        {
            Type prepatches = AccessTools.TypeByName("PerformanceFish.Listers.ThingsPrepatches");
            if (prepatches == null)
            {
                Log.Warning("[Performance Fish Lister Hotfix] Performance Fish Lister prepatches were not found; hotfix is inactive.");
                return;
            }

            Harmony harmony = new Harmony(HarmonyId);
            PatchFinalizer(harmony, prepatches, "AddToDefList", nameof(AddToDefListFinalizer), 2);
            PatchFinalizer(harmony, prepatches, "AddToGroupList", nameof(AddToGroupListFinalizer), 3);
            PatchFinalizer(harmony, prepatches, "RemoveFromDefList", nameof(RemoveFromDefListFinalizer), 2);
            PatchFinalizer(harmony, prepatches, "RemoveFromGroupList", nameof(RemoveFromGroupListFinalizer), 3);
            PatchFinalizer(harmony, prepatches, "AddToTypeList", nameof(AddToTypeListFinalizer), 2);
            PatchFinalizer(harmony, prepatches, "RemoveFromTypeList", nameof(RemoveFromTypeListFinalizer), 2);

            MethodInfo clear = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Clear));
            MethodInfo contains = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Contains), new[] { typeof(Thing) });
            harmony.Patch(clear, finalizer: new HarmonyMethod(typeof(PerformanceFishListerHotfixBootstrap), nameof(ListerThingsClearFinalizer)));
            harmony.Patch(contains, finalizer: new HarmonyMethod(typeof(PerformanceFishListerHotfixBootstrap), nameof(ListerThingsContainsFinalizer)));
        }

        private static void PatchFinalizer(Harmony harmony, Type type, string methodName, string finalizerName, int parameterCount)
        {
            MethodInfo target = null;
            foreach (MethodInfo method in type.GetMethods(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (method.Name == methodName && method.GetParameters().Length == parameterCount)
                {
                    target = method;
                    break;
                }
            }

            if (target == null)
            {
                Log.Warning("[Performance Fish Lister Hotfix] Could not find Performance Fish method: " + methodName);
                return;
            }

            harmony.Patch(target, finalizer: new HarmonyMethod(typeof(PerformanceFishListerHotfixBootstrap), finalizerName));
        }

        public static Exception AddToDefListFinalizer(ListerThings __0, Thing __1, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            return RecoverIndexMaps(__0, "AddToDefList", __exception) ? null : __exception;
        }

        public static Exception AddToGroupListFinalizer(ListerThings __0, Thing __1, RimWorld.ThingRequestGroup __2, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            return RecoverIndexMaps(__0, "AddToGroupList", __exception) ? null : __exception;
        }

        public static Exception RemoveFromDefListFinalizer(ListerThings __0, Thing __1, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            try
            {
                EnsureThingRemovedFromDefList(__0, __1);
            }
            catch (Exception repairEx)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed repairing def list semantics: " + repairEx);
                return __exception;
            }

            return RecoverIndexMaps(__0, "RemoveFromDefList", __exception) ? null : __exception;
        }

        public static Exception RemoveFromGroupListFinalizer(ListerThings __0, Thing __1, RimWorld.ThingRequestGroup __2, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            try
            {
                EnsureThingRemovedFromGroupList(__0, __1, __2);
            }
            catch (Exception repairEx)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed repairing group list semantics: " + repairEx);
                return __exception;
            }

            return RecoverIndexMaps(__0, "RemoveFromGroupList", __exception) ? null : __exception;
        }

        public static Exception AddToTypeListFinalizer(ListerThings __0, Thing __1, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            return RecoverTypeCache(__0, "AddToTypeList", __exception) ? null : __exception;
        }

        public static Exception RemoveFromTypeListFinalizer(ListerThings __0, Thing __1, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            return RecoverTypeCache(__0, "RemoveFromTypeList", __exception) ? null : __exception;
        }

        public static Exception ListerThingsClearFinalizer(ListerThings __instance, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            bool indexOk = ResetIndexMaps(__instance);
            bool typeOk = ResetTypeCacheEmpty(__instance);
            if (indexOk && typeOk)
            {
                ReportRecovery("ListerThings.Clear", __exception);
                return null;
            }

            return __exception;
        }

        public static Exception ListerThingsContainsFinalizer(ListerThings __instance, Thing __0, ref bool __result, Exception __exception)
        {
            if (!IsFishTableCorruption(__exception))
                return __exception;

            try
            {
                __result = ContainsInVanillaDefList(__instance, __0);
                if (ResetIndexMaps(__instance))
                {
                    ReportRecovery("ListerThings.Contains", __exception);
                    return null;
                }
            }
            catch (Exception repairEx)
            {
                Log.Error("[Performance Fish Lister Hotfix] Contains fallback failed: " + repairEx);
            }

            return __exception;
        }

        private static bool RecoverIndexMaps(ListerThings lister, string stage, Exception original)
        {
            if (!ResetIndexMaps(lister))
                return false;

            ReportRecovery(stage, original);
            return true;
        }

        private static bool RecoverTypeCache(ListerThings lister, string stage, Exception original)
        {
            if (!RebuildTypeCache(lister))
                return false;

            ReportRecovery(stage, original);
            return true;
        }

        private static bool ResetIndexMaps(ListerThings lister)
        {
            try
            {
                DiscoverInjectedFields();
                if (defIndexMapField == null || groupIndexMapField == null)
                {
                    Log.Error("[Performance Fish Lister Hotfix] Could not locate injected Performance Fish index map fields on ListerThings.");
                    return false;
                }

                if (!ReplaceOrClearField(lister, defIndexMapField))
                    return false;
                if (!ReplaceOrClearField(lister, groupIndexMapField))
                    return false;

                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed resetting Lister index maps: " + ex);
                return false;
            }
        }

        private static bool ReplaceOrClearField(object owner, FieldInfo field)
        {
            try
            {
                object fresh = Activator.CreateInstance(field.FieldType);
                field.SetValue(owner, fresh);
                return true;
            }
            catch
            {
                try
                {
                    object current = field.GetValue(owner);
                    IDictionary dictionary = current as IDictionary;
                    if (dictionary == null)
                        return false;
                    dictionary.Clear();
                    return true;
                }
                catch
                {
                    return false;
                }
            }
        }

        private static void DiscoverInjectedFields()
        {
            if (fieldsScanned)
                return;

            fieldsScanned = true;
            foreach (FieldInfo field in typeof(ListerThings).GetFields(AllInstance))
            {
                Type fieldType = field.FieldType;
                if (fieldType.IsGenericType && fieldType.GetGenericTypeDefinition().FullName == "FisheryLib.Collections.FishTable`2")
                {
                    Type[] args = fieldType.GetGenericArguments();
                    if (args.Length == 2 && args[1] == typeof(int))
                    {
                        if (args[0] == typeof(int))
                            defIndexMapField = field;
                        else if (args[0].FullName == "PerformanceFish.GroupThingPair")
                            groupIndexMapField = field;
                    }
                }
                else if (fieldType.FullName == "PerformanceFish.Listers.Things+Cache")
                {
                    thingsCacheField = field;
                }
            }

            if (thingsCacheField != null)
            {
                Type cacheType = thingsCacheField.FieldType;
                thingsByTypeField = cacheType.GetField("ThingsByType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                initializeThingsListMethod = cacheType.GetMethod("InitializeThingsList", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            }
        }

        private static bool RebuildTypeCache(ListerThings lister)
        {
            try
            {
                DiscoverInjectedFields();
                if (thingsCacheField == null || thingsByTypeField == null || initializeThingsListMethod == null)
                {
                    Log.Error("[Performance Fish Lister Hotfix] Could not locate Performance Fish ThingsByType cache metadata.");
                    return false;
                }

                object cache = Activator.CreateInstance(thingsCacheField.FieldType);
                IDictionary thingsByType = thingsByTypeField.GetValue(cache) as IDictionary;
                if (thingsByType == null)
                    return false;

                List<Thing> allThings = lister.AllThings;
                if (allThings != null)
                {
                    for (int i = 0; i < allThings.Count; i++)
                    {
                        Thing thing = allThings[i];
                        if (thing == null)
                            continue;

                        Type thingType = thing.GetType();
                        while (thingType != null && thingType != typeof(Thing))
                        {
                            IList typedList;
                            if (thingsByType.Contains(thingType))
                            {
                                typedList = thingsByType[thingType] as IList;
                            }
                            else
                            {
                                MethodInfo closedInitializer = initializeThingsListMethod.MakeGenericMethod(thingType);
                                typedList = closedInitializer.Invoke(null, null) as IList;
                                if (typedList == null)
                                    return false;
                                thingsByType.Add(thingType, typedList);
                            }

                            if (!typedList.Contains(thing))
                                typedList.Add(thing);

                            thingType = thingType.BaseType;
                        }
                    }
                }

                thingsCacheField.SetValue(lister, cache);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed rebuilding ThingsByType cache: " + ex);
                return false;
            }
        }

        private static bool ResetTypeCacheEmpty(ListerThings lister)
        {
            try
            {
                DiscoverInjectedFields();
                if (thingsCacheField == null)
                    return false;
                thingsCacheField.SetValue(lister, Activator.CreateInstance(thingsCacheField.FieldType));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Performance Fish Lister Hotfix] Failed resetting ThingsByType cache: " + ex);
                return false;
            }
        }

        private static void EnsureThingRemovedFromDefList(ListerThings lister, Thing thing)
        {
            IDictionary listsByDef = ListsByDefField.GetValue(lister) as IDictionary;
            if (listsByDef == null || thing == null || thing.def == null)
                return;

            IList list = listsByDef[thing.def] as IList;
            if (list == null)
                return;

            while (list.Contains(thing))
                list.Remove(thing);
        }

        private static void EnsureThingRemovedFromGroupList(ListerThings lister, Thing thing, RimWorld.ThingRequestGroup group)
        {
            Array listsByGroup = ListsByGroupField.GetValue(lister) as Array;
            int[] stateHashes = StateHashByGroupField.GetValue(lister) as int[];
            int index = (int)group;

            if (listsByGroup != null && index >= 0 && index < listsByGroup.Length)
            {
                IList list = listsByGroup.GetValue(index) as IList;
                if (list != null)
                {
                    while (list.Contains(thing))
                        list.Remove(thing);
                }
            }

            // Performance Fish increments this only after its FishTable operations.
            // Therefore any FishTable exception from RemoveFromGroupList occurs before this increment.
            if (stateHashes != null && index >= 0 && index < stateHashes.Length)
                stateHashes[index]++;
        }

        private static bool ContainsInVanillaDefList(ListerThings lister, Thing thing)
        {
            if (thing == null || thing.def == null)
                return false;

            IDictionary listsByDef = ListsByDefField.GetValue(lister) as IDictionary;
            if (listsByDef == null)
                return false;

            IList list = listsByDef[thing.def] as IList;
            return list != null && list.Contains(thing);
        }

        private static bool IsFishTableCorruption(Exception exception)
        {
            Exception current = exception;
            while (current != null)
            {
                string text = current.ToString();
                if (current is InvalidOperationException
                    && text.IndexOf("FishTable", StringComparison.OrdinalIgnoreCase) >= 0
                    && (text.IndexOf("Failed to find parent index", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("Concurrent operations", StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    return true;
                }

                current = current.InnerException;
            }

            return false;
        }

        private static void ReportRecovery(string stage, Exception original)
        {
            recoveryCount++;
            string translated = "PFHotfix_Recovered".Translate(stage, recoveryCount);
            Log.Warning("[Performance Fish Lister Hotfix] " + translated);

            if (Prefs.DevMode && recoveryCount <= 5)
                Log.Message("[Performance Fish Lister Hotfix] Original exception suppressed after successful recovery:\n" + original);
        }
    }
}
