using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.CommonSensePerformance
{
    // Only patches Common Sense's OWN private helpers, never the global
    // WorkGiver_DoBill or RimWorld pathfinder implementation.
    internal static class M3Ingredients
    {
        private static FieldInfo preferred;
        private static readonly Harmony Harmony = new Harmony("allen.commonsense.performance16.m3");
        private static bool sortFaulted, scanFaulted;

        internal static void TryInstall()
        {
            try
            {
                Type settings = AccessTools.TypeByName("CommonSense.Settings");
                if (settings == null) { Log.Warning("[CS Perf M3] Common Sense settings missing; skipped."); return; }
                preferred = AccessTools.Field(settings, "prefer_spoiling_ingredients");
                if (preferred == null || preferred.FieldType != typeof(bool))
                { Log.Warning("[CS Perf M3] Common Sense preference field mismatch; skipped."); return; }

                if (CommonSensePerformanceMod.Options.SortIngredientKeys)
                {
                    Type sortType = AccessTools.TypeByName("CommonSense.WorkGiver_DoBill_TryFindBestBillIngredientsInSet_AllowMix_CommonSensePatch");
                    MethodInfo sorter = sortType == null ? null : AccessTools.Method(sortType,
                        "DoSort", new[] { typeof(List<Thing>), typeof(Bill) });
                    InstallOne(sorter, AccessTools.Method(typeof(M3Ingredients), nameof(SortPrefix)), "M3-A");
                }

                if (CommonSensePerformanceMod.Options.ScanStorageGroups)
                {
                    Type scanType = AccessTools.TypeByName("CommonSense.WorkGiver_DoBill_TryFindBestIngredientsHelper_CommonSensePatch");
                    MethodInfo scan = scanType == null ? null : AccessTools.Method(scanType, "PreProcess",
                        new[] { typeof(Pawn), typeof(Predicate<Thing>), typeof(bool),
                            typeof(List<Thing>), typeof(HashSet<Thing>) });
                    InstallOne(scan, AccessTools.Method(typeof(M3Ingredients), nameof(StoragePrefix)), "M3-B");
                }
            }
            catch (Exception ex) { Log.Warning("[CS Perf M3] Install failed; original Common Sense methods retained. " + ex); }
        }

        private static void InstallOne(MethodInfo target, MethodInfo prefix, string label)
        {
            if (target == null || target.ReturnType != typeof(void) || prefix == null)
            {
                Log.Warning("[CS Perf "+label+"] Source signature mismatch; skipped."); return;
            }
            Patches p = HarmonyLib.Harmony.GetPatchInfo(target);
            if (p != null && (p.Prefixes.Count > 0 || p.Postfixes.Count > 0
                || p.Transpilers.Count > 0 || p.Finalizers.Count > 0))
            {
                Log.Warning("[CS Perf "+label+"] Existing Harmony patch on target; skipped."); return;
            }
            Harmony.Patch(target, prefix: new HarmonyMethod(prefix));
            Log.Message("[CS Perf "+label+"] Active.");
        }

        private struct IngredientKey
        {
            internal float Potency;
            internal bool Rottable;
            internal int RotBucket;
        }

        // Synchronous List.Sort never ticks the game between comparisons.
        // Preserve exact comparator expression and original List.Sort.
        private static bool SortPrefix(List<Thing> availableThings, Bill bill)
        {
            if (sortFaulted) return true;
            if (!(bool)preferred.GetValue(null) || bill.recipe.addsHediff != null
                || availableThings.Count < 8) return true;
            try
            {
                var cache = new Dictionary<Thing, IngredientKey>(availableThings.Count);
                foreach (Thing item in availableThings)
                {
                    // In case a mod feeds null, defer to upstream behavior.
                    if (item == null) return true;
                    if (cache.ContainsKey(item)) continue;
                    IngredientKey k = new IngredientKey();
                    k.Potency = item.GetStatValue(StatDefOf.MedicalPotency);
                    CompRottable rot = item.TryGetComp<CompRottable>();
                    k.Rottable = rot != null;
                    if (rot != null)
                        k.RotBucket = (int)(rot.PropsRot.TicksToRotStart - rot.RotProgress) / 2500;
                    cache[item] = k;
                }
                availableThings.Sort((a, b) =>
                {
                    IngredientKey x = cache[a], y = cache[b];
                    float p = x.Potency - y.Potency;
                    if (p > 0) return -1;
                    if (p < 0) return 1;
                    if (!x.Rottable) return y.Rottable ? 1 : 0;
                    if (!y.Rottable) return -1;
                    return x.RotBucket - y.RotBucket;
                });
                return false;
            }
            catch (Exception ex)
            {
                sortFaulted = true;
                Log.Warning("[CS Perf M3-A] Snapshot sorting disabled for session; upstream fallback. " + ex);
                return true;
            }
        }

        private static bool StoragePrefix(Pawn pawn, Predicate<Thing> baseValidator,
            bool billGiverIsPawn, List<Thing> newRelevantThings, HashSet<Thing> processedThings)
        {
            if (scanFaulted) return true;
            if (!(bool)preferred.GetValue(null) || billGiverIsPawn) return true;
            try
            {
                HashSet<ISlotGroup> stores = new HashSet<ISlotGroup>();
                foreach (Thing thing in newRelevantThings)
                {
                    if (thing.TryGetComp<CompRottable>() == null) continue;
                    ISlotGroup group = thing.GetSlotGroup();
                    if (group == null) continue;
                    stores.Add(group.StorageGroup ?? group);
                }
                foreach (ISlotGroup store in stores)
                {
                    foreach (Thing thing in store.HeldThings)
                    {
                        if (!thing.def.IsMedicine && !processedThings.Contains(thing)
                            && baseValidator(thing) && pawn.CanReach(thing, PathEndMode.OnCell, Danger.Deadly))
                        {
                            newRelevantThings.Add(thing);
                            processedThings.Add(thing);
                        }
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                scanFaulted = true;
                Log.Warning("[CS Perf M3-B] Storage scan disabled for session; upstream fallback. " + ex);
                return true;
            }
        }
    }
}