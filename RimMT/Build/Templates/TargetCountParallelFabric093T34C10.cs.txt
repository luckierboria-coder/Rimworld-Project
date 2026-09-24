using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Same-tick, no-wait worker aggregation for the default RecipeWorkerCounter target-count path.
    /// The main thread captures only primitive visibility facts. A result is consumed only while the
    /// game tick and the map membership/fog generation still match. Carried products are counted live.
    /// Custom counters, minified products and non-default filters remain Vanilla.
    /// </summary>
    internal static class TargetCountParallelFabric093T34C10
    {
        internal const string FeatureId = "parallel.targetCountProducts";
        private static readonly ConditionalWeakTable<Map, MapState> Maps =
            new ConditionalWeakTable<Map, MapState>();
        private static AccessTools.FieldRef<FogGrid, Map> fogMap;

        private static bool installed;
        private static int authorityState;
        private static int gameplayAuthorityAudited;
        private static long calls;
        private static long eligible;
        private static long planHits;
        private static long planMisses;
        private static long scheduled;
        private static long schedulerRejected;
        private static long built;
        private static long staleTick;
        private static long staleGeneration;
        private static long capturedThings;
        private static long workerCounted;
        private static long carriedLiveCounted;
        private static long membershipBumps;
        private static long fogBumps;
        private static long unsupportedCounter;
        private static long unsupportedShape;
        private static long workerFailures;

        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            try
            {
                MethodInfo count = AccessTools.Method(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountProducts));
                MethodInfo add = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Add));
                MethodInfo remove = AccessTools.Method(typeof(ListerThings), nameof(ListerThings.Remove));
                MethodInfo unfog = AccessTools.Method(typeof(FogGrid), nameof(FogGrid.Unfog));
                MethodInfo clearFog = AccessTools.Method(typeof(FogGrid), nameof(FogGrid.ClearAllFog));
                MethodInfo refog = AccessTools.Method(typeof(FogGrid), nameof(FogGrid.Refog));
                if (count == null || add == null || remove == null || unfog == null || clearFog == null || refog == null)
                    throw new MissingMethodException("T34-C.10 required method missing");
                fogMap = AccessTools.FieldRefAccess<FogGrid, Map>("map");

                authorityState = AuditAuthority(count) ? 1 : -1;
                harmony.Patch(count, prefix: new HarmonyMethod(typeof(TargetCountParallelFabric093T34C10), nameof(CountProductsPrefix)) { priority = Priority.First });
                harmony.Patch(add, postfix: new HarmonyMethod(typeof(TargetCountParallelFabric093T34C10), nameof(ListerAddPostfix)) { priority = Priority.Last });
                harmony.Patch(remove, prefix: new HarmonyMethod(typeof(TargetCountParallelFabric093T34C10), nameof(ListerRemovePrefix)) { priority = Priority.First });
                HarmonyMethod fogPostfix = new HarmonyMethod(typeof(TargetCountParallelFabric093T34C10), nameof(FogChangedPostfix)) { priority = Priority.Last };
                harmony.Patch(unfog, postfix: fogPostfix);
                harmony.Patch(clearFog, postfix: fogPostfix);
                harmony.Patch(refog, postfix: fogPostfix);
                installed = true;
                Log.Message("[RimMT] T34-C.10 target-count parallel fabric installed: same-tick primitive product visibility aggregation, generation-gated publication, live carried-product completion, no worker wait.");
            }
            catch (Exception ex)
            {
                installed = false;
                Log.Warning("[RimMT] T34-C.10 target-count parallel fabric failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static bool CountProductsPrefix(RecipeWorkerCounter __instance, Bill_Production bill, ref int __result)
        {
            Interlocked.Increment(ref calls);
            if (!installed || authorityState <= 0 || !FeatureGate.IsEnabled(FeatureId) ||
                !RimMTThreadGuard.IsMainThread || Current.ProgramState != ProgramState.Playing ||
                __instance == null || bill == null)
                return true;

            if (Interlocked.CompareExchange(ref gameplayAuthorityAudited, 1, 0) == 0)
            {
                MethodInfo method = AccessTools.Method(typeof(RecipeWorkerCounter), nameof(RecipeWorkerCounter.CountProducts));
                if (!AuditAuthority(method))
                {
                    Volatile.Write(ref authorityState, -1);
                    Log.Warning("[RimMT] T34-C.10 disabled at first gameplay use because a late RecipeWorkerCounter.CountProducts patch was found.");
                    return true;
                }
            }

            ThingDef product;
            Map map;
            if (!TryResolve(__instance, bill, out product, out map)) return true;
            Interlocked.Increment(ref eligible);

            int tick = Find.TickManager == null ? -1 : Find.TickManager.TicksGame;
            if (tick < 0) return true;
            MapState state = Maps.GetValue(map, delegate(Map ignored) { return new MapState(); });
            Slot slot;
            if (!state.ByDef.TryGetValue(product, out slot))
            {
                slot = new Slot();
                state.ByDef.Add(product, slot);
            }

            long generation = Volatile.Read(ref state.Generation);
            ProductPlan plan = slot.Plan;
            if (Volatile.Read(ref slot.Ready) != 0 && plan != null)
            {
                if (plan.Tick != tick) Interlocked.Increment(ref staleTick);
                else if (plan.Generation != generation) Interlocked.Increment(ref staleGeneration);
                else if (map.listerThings.ThingsOfDef(product).Count != plan.SourceCount)
                    Interlocked.Increment(ref staleGeneration);
                else
                {
                    __result = plan.MapCount + CountCarriedLive(__instance, bill, product, map);
                    Interlocked.Increment(ref planHits);
                    return false;
                }
            }

            Interlocked.Increment(ref planMisses);
            TrySchedule(state, slot, product, map, tick, generation);
            return true;
        }

        private static bool TryResolve(RecipeWorkerCounter counter, Bill_Production bill, out ThingDef product, out Map map)
        {
            product = null;
            map = null;
            if (counter.GetType() != typeof(RecipeWorkerCounter) || bill.GetType() != typeof(Bill_Production))
            {
                Interlocked.Increment(ref unsupportedCounter);
                return false;
            }
            RecipeDef recipe = counter.recipe;
            if (recipe == null || !ReferenceEquals(recipe, bill.recipe) || recipe.specialProducts != null ||
                recipe.products == null || recipe.products.Count != 1)
            {
                Interlocked.Increment(ref unsupportedShape);
                return false;
            }
            product = recipe.products[0].thingDef;
            map = bill.Map;
            if (product == null || map == null || product.CountAsResource || product.Minifiable ||
                bill.includeEquipped || bill.GetIncludeSlotGroup() != null || bill.limitToAllowedStuff ||
                bill.hpRange.min != 0f || bill.hpRange.max != 1f ||
                bill.qualityRange.min != QualityCategory.Awful || bill.qualityRange.max != QualityCategory.Legendary ||
                (!bill.includeTainted && product.IsApparel && product.apparel.careIfWornByCorpse))
            {
                Interlocked.Increment(ref unsupportedShape);
                return false;
            }
            return true;
        }

        private static void TrySchedule(MapState state, Slot slot, ThingDef product, Map map, int tick, long generation)
        {
            if (Volatile.Read(ref slot.ScheduledTick) == tick && Volatile.Read(ref slot.ScheduledGeneration) == generation)
                return;
            if (Interlocked.CompareExchange(ref slot.Scheduled, 1, 0) != 0) return;

            List<Thing> source = map.listerThings.ThingsOfDef(product);
            byte[] visible = new byte[source.Count];
            for (int i = 0; i < source.Count; i++)
            {
                Thing thing = source[i];
                if (thing != null && thing.Spawned && thing.MapHeld == map && !thing.Position.Fogged(map))
                    visible[i] = 1;
            }
            Interlocked.Add(ref capturedThings, visible.Length);

            long afterCapture = Volatile.Read(ref state.Generation);
            if (afterCapture != generation)
            {
                Volatile.Write(ref slot.Scheduled, 0);
                Interlocked.Increment(ref staleGeneration);
                return;
            }
            Volatile.Write(ref slot.ScheduledTick, tick);
            Volatile.Write(ref slot.ScheduledGeneration, generation);
            JobScheduler scheduler = RimMTRuntime.Scheduler;
            bool accepted = scheduler != null && scheduler.TryEnqueue(
                FeatureId, JobPriority.High, delegate { Build(slot, visible, tick, generation); });
            if (accepted) Interlocked.Increment(ref scheduled);
            else
            {
                Volatile.Write(ref slot.Scheduled, 0);
                Interlocked.Increment(ref schedulerRejected);
            }
        }

        private static void Build(Slot slot, byte[] visible, int tick, long generation)
        {
            try
            {
                int count = 0;
                for (int i = 0; i < visible.Length; i++) count += visible[i];
                slot.Plan = new ProductPlan(tick, generation, visible.Length, count);
                Interlocked.Add(ref workerCounted, visible.Length);
                Interlocked.Increment(ref built);
                Volatile.Write(ref slot.Ready, 1);
                Volatile.Write(ref slot.Scheduled, 0);
            }
            catch (Exception ex)
            {
                Volatile.Write(ref slot.Scheduled, 0);
                Interlocked.Increment(ref workerFailures);
                CircuitBreaker.RecordFailure(FeatureId, ex);
            }
        }

        private static int CountCarriedLive(RecipeWorkerCounter counter, Bill_Production bill, ThingDef product, Map map)
        {
            int count = 0;
            List<Pawn> pawns = map.mapPawns.FreeColonistsSpawned;
            for (int i = 0; i < pawns.Count; i++)
            {
                Thing outer = pawns[i].carryTracker.CarriedThing;
                if (outer == null) continue;
                int stack = outer.stackCount;
                Thing inner = outer.GetInnerIfMinified();
                if (counter.CountValidThing(inner, bill, product)) count += stack;
            }
            Interlocked.Add(ref carriedLiveCounted, count);
            return count;
        }

        public static void ListerAddPostfix(ListerThings __instance, Thing t)
        {
            if (__instance == null || __instance.use != ListerThingsUse.Global || t == null) return;
            BumpMembership(t.MapHeld);
        }

        public static void ListerRemovePrefix(ListerThings __instance, Thing t)
        {
            if (__instance == null || __instance.use != ListerThingsUse.Global || t == null) return;
            BumpMembership(t.MapHeld);
        }

        public static void FogChangedPostfix(FogGrid __instance)
        {
            if (__instance == null) return;
            Map map = null;
            try { if (fogMap != null) map = fogMap(__instance); } catch { }
            if (map == null) return;
            MapState state;
            if (!Maps.TryGetValue(map, out state) || state == null) return;
            Interlocked.Increment(ref state.Generation);
            Interlocked.Increment(ref fogBumps);
        }

        private static void BumpMembership(Map map)
        {
            if (map == null) return;
            MapState state;
            if (!Maps.TryGetValue(map, out state) || state == null) return;
            Interlocked.Increment(ref state.Generation);
            Interlocked.Increment(ref membershipBumps);
        }

        private static bool AuditAuthority(MethodBase method)
        {
            if (method == null) return false;
            Patches info = Harmony.GetPatchInfo(method);
            if (info == null) return true;
            return !HasForeign(info.Prefixes) && !HasForeign(info.Postfixes) &&
                   !HasForeign(info.Transpilers) && !HasForeign(info.Finalizers);
        }

        private static bool HasForeign(IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null || string.Equals(patch.owner, RimMTBootstrap.HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        internal static string Summary()
        {
            return "T34-C.10 Target-count parallel fabric: installed=" + installed +
                ", authority=" + authorityState +
                ", calls/eligible=" + Interlocked.Read(ref calls) + "/" + Interlocked.Read(ref eligible) +
                ", plans[hit/miss/scheduled/rejected/built]=" + Interlocked.Read(ref planHits) + "/" +
                Interlocked.Read(ref planMisses) + "/" + Interlocked.Read(ref scheduled) + "/" +
                Interlocked.Read(ref schedulerRejected) + "/" + Interlocked.Read(ref built) +
                ", stale[tick/generation]=" + Interlocked.Read(ref staleTick) + "/" + Interlocked.Read(ref staleGeneration) +
                ", primitive[captured/workerCounted]=" + Interlocked.Read(ref capturedThings) + "/" + Interlocked.Read(ref workerCounted) +
                ", carriedLive=" + Interlocked.Read(ref carriedLiveCounted) +
                ", generation[membership/fog]=" + Interlocked.Read(ref membershipBumps) + "/" + Interlocked.Read(ref fogBumps) +
                ", bypass[counter/shape]=" + Interlocked.Read(ref unsupportedCounter) + "/" + Interlocked.Read(ref unsupportedShape) +
                ", workerFailures=" + Interlocked.Read(ref workerFailures) +
                ". Same-tick only; worker input is primitive-only; no wait; custom counters and complex filters stay Vanilla.";
        }

        private sealed class MapState
        {
            internal long Generation;
            internal readonly Dictionary<ThingDef, Slot> ByDef = new Dictionary<ThingDef, Slot>();
        }

        private sealed class Slot
        {
            internal int Scheduled;
            internal int ScheduledTick = -1;
            internal long ScheduledGeneration = -1;
            internal int Ready;
            internal ProductPlan Plan;
        }

        private sealed class ProductPlan
        {
            internal readonly int Tick;
            internal readonly long Generation;
            internal readonly int SourceCount;
            internal readonly int MapCount;
            internal ProductPlan(int tick, long generation, int sourceCount, int mapCount)
            { Tick = tick; Generation = generation; SourceCount = sourceCount; MapCount = mapCount; }
        }
    }
}
