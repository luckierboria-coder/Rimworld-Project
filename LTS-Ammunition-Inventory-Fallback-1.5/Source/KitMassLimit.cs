using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LTSAmmoInventoryFallback15
{
    internal static class KitMassLimit
    {
        internal const float SmallLimit = 3f;
        internal const float MediumLimit = 6f;
        internal const float LargeLimit = 10f;
        private const float Epsilon = 0.0001f;

        private static readonly System.Reflection.MethodInfo GetWornKits =
            AccessTools.Method(PatchRegistry.AmmoLogic, "GetWornKits");

        internal static IEnumerable<object> WornKits(Pawn pawn)
        {
            if (pawn == null || GetWornKits == null) yield break;
            var kits = GetWornKits.Invoke(null, new object[] { pawn }) as IEnumerable;
            if (kits == null) yield break;
            foreach (var kit in kits)
                if (kit != null) yield return kit;
        }

        internal static Thing ResolveKitThing(object kitOrComp)
        {
            if (kitOrComp is Thing thing) return thing;
            if (kitOrComp is ThingComp comp) return comp.parent;
            return null;
        }

        internal static object GetKitComp(object kit)
        {
            if (kit == null) return null;
            if (kit is ThingComp) return kit;
            return AccessTools.Property(kit.GetType(), "KitComp")?.GetValue(kit, null);
        }

        internal static IEnumerable<object> Bags(object kitOrComp)
        {
            var comp = GetKitComp(kitOrComp);
            if (comp == null) yield break;
            var bags = AccessTools.Property(comp.GetType(), "Bags")?.GetValue(comp, null) as IEnumerable;
            if (bags == null) yield break;
            foreach (var bag in bags)
                if (bag != null) yield return bag;
        }

        internal static bool TryReadBag(object bag, out ThingDef ammo, out int count, out int maxCount)
        {
            ammo = null;
            count = 0;
            maxCount = 0;
            if (bag == null) return false;
            var type = bag.GetType();
            var chosenP = AccessTools.Property(type, "ChosenAmmo");
            var countP = AccessTools.Property(type, "Count");
            var maxP = AccessTools.Property(type, "MaxCount");
            if (chosenP == null || countP == null || maxP == null) return false;
            ammo = chosenP.GetValue(bag, null) as ThingDef;
            count = (int)countP.GetValue(bag, null);
            maxCount = (int)maxP.GetValue(bag, null);
            return true;
        }

        internal static void SetBagCount(object bag, int count)
        {
            AccessTools.Property(bag.GetType(), "Count")?.SetValue(bag, count, null);
        }

        internal static float GetLimit(object kitOrComp)
        {
            var thing = ResolveKitThing(kitOrComp);
            var defName = thing?.def?.defName ?? string.Empty;
            var lower = defName.ToLowerInvariant();

            if (defName == "LTS_KitSmall" || lower.Contains("kitsmall") || lower.Contains("smallkit"))
                return SmallLimit;
            if (defName == "LTS_KitMedium" || lower.Contains("kitmedium") || lower.Contains("mediumkit"))
                return MediumLimit;
            if (defName == "LTS_KitLarge" || lower.Contains("kitlarge") || lower.Contains("largekit"))
                return LargeLimit;

            int bagCount = 0;
            int totalCapacity = 0;
            foreach (var bag in Bags(kitOrComp))
            {
                bagCount++;
                var type = bag.GetType();
                var capacityP = AccessTools.Property(type, "Capacity");
                var weightP = AccessTools.Property(type, "Weight");
                if (capacityP == null) continue;
                int capacity = (int)capacityP.GetValue(bag, null);
                int weight = weightP == null ? 1 : Math.Max(1, (int)weightP.GetValue(bag, null));
                totalCapacity += capacity * weight;
            }

            if (bagCount >= 2 || totalCapacity > 180) return LargeLimit;
            if (totalCapacity > 90) return MediumLimit;
            if (bagCount > 0) return SmallLimit;
            return float.PositiveInfinity;
        }

        internal static float AmmoMass(ThingDef ammo)
        {
            if (ammo == null) return 0f;
            try
            {
                var mass = ammo.GetStatValueAbstract(StatDefOf.Mass);
                return mass > 0f && !float.IsNaN(mass) && !float.IsInfinity(mass) ? mass : 0f;
            }
            catch
            {
                return 0f;
            }
        }

        internal static float CurrentAmmoMass(object kitOrComp)
        {
            float total = 0f;
            foreach (var bag in Bags(kitOrComp))
            {
                if (!TryReadBag(bag, out var ammo, out var count, out _)) continue;
                if (ammo == null || count <= 0) continue;
                total += AmmoMass(ammo) * count;
            }
            return total;
        }

        internal static int RemainingSlotRounds(object kitOrComp, ThingDef ammo)
        {
            if (ammo == null) return 0;
            int room = 0;
            foreach (var bag in Bags(kitOrComp))
            {
                if (!TryReadBag(bag, out var chosen, out var count, out var max)) continue;
                if (chosen == ammo && max > count) room += max - count;
            }
            return room;
        }

        internal static int MaxAdditionalRounds(object kitOrComp, ThingDef ammo)
        {
            int slotRoom = RemainingSlotRounds(kitOrComp, ammo);
            if (slotRoom <= 0) return 0;

            float limit = GetLimit(kitOrComp);
            if (float.IsPositiveInfinity(limit)) return slotRoom;

            float perRound = AmmoMass(ammo);
            if (perRound <= Epsilon) return slotRoom;

            float remainingMass = limit - CurrentAmmoMass(kitOrComp);
            if (remainingMass + Epsilon < perRound) return 0;

            int byMass = (int)Math.Floor((remainingMass + Epsilon) / perRound);
            return Math.Max(0, Math.Min(slotRoom, byMass));
        }

        internal static bool CanAccept(object kitOrComp, ThingDef ammo)
        {
            return MaxAdditionalRounds(kitOrComp, ammo) > 0;
        }

        internal static void ClampGeneratedKit(object kitOrComp)
        {
            float limit = GetLimit(kitOrComp);
            if (float.IsPositiveInfinity(limit)) return;

            float remaining = limit;
            foreach (var bag in Bags(kitOrComp))
            {
                if (!TryReadBag(bag, out var ammo, out var count, out _)) continue;
                if (count <= 0 || ammo == null) continue;

                float mass = AmmoMass(ammo);
                if (mass <= Epsilon) continue;

                int allowed = Math.Max(0, (int)Math.Floor((remaining + Epsilon) / mass));
                if (count > allowed)
                {
                    count = allowed;
                    SetBagCount(bag, count);
                }
                remaining -= count * mass;
                if (remaining < 0f) remaining = 0f;
            }
        }
    }

    internal static class MassLoading
    {
        public static bool LoadMagazinePrefix(TargetIndex ind, object kit, ref Toil __result)
        {
            try
            {
                var toil = new Toil();
                toil.initAction = delegate
                {
                    var actor = toil.actor;
                    var thing = actor?.CurJob?.GetTarget(ind).Thing;
                    if (actor == null || thing == null || thing.Destroyed) return;

                    int allowed = Math.Min(thing.stackCount, KitMassLimit.MaxAdditionalRounds(kit, thing.def));
                    if (allowed <= 0) return;

                    foreach (var bag in KitMassLimit.Bags(kit))
                    {
                        if (allowed <= 0 || thing.Destroyed) break;
                        if (!KitMassLimit.TryReadBag(bag, out var chosen, out var count, out var max)) continue;
                        if (chosen != thing.def || count >= max) continue;

                        int take = Math.Min(allowed, Math.Min(max - count, thing.stackCount));
                        if (take <= 0) continue;

                        var used = thing.SplitOff(take);
                        if (used != null && !used.Destroyed) used.Destroy(DestroyMode.Vanish);
                        KitMassLimit.SetBagCount(bag, count + take);
                        allowed -= take;
                    }
                };
                __result = toil;
                return false;
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory Fallback 1.5] mass-limited LoadMagazine failed: " + e);
                return true;
            }
        }

        public static bool OpportunisticLoadMagazinePrefix(Toil fetch, TargetIndex fetchInd, ThingDef def, object kit, ref Toil __result)
        {
            try
            {
                var toil = new Toil();
                toil.initAction = delegate
                {
                    var actor = toil.actor;
                    if (actor?.Map == null || def == null) return;
                    if (!KitMassLimit.CanAccept(kit, def)) return;

                    bool Validator(Thing t)
                    {
                        if (t == null || !t.Spawned || t.IsForbidden(actor)) return false;
                        return actor.CanReserve(t) && KitMassLimit.CanAccept(kit, def);
                    }

                    var ammo = GenClosest.ClosestThing_Global_Reachable(
                        actor.Position,
                        actor.Map,
                        actor.Map.listerThings.ThingsOfDef(def),
                        PathEndMode.OnCell,
                        TraverseParms.For(actor),
                        10f,
                        Validator);

                    if (ammo == null) return;
                    actor.jobs.curJob.SetTarget(fetchInd, ammo);
                    actor.jobs.curDriver.JumpToToil(fetch);
                };
                __result = toil;
                return false;
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory Fallback 1.5] mass-limited opportunistic load failed: " + e);
                return true;
            }
        }

        public static void ClampGeneratedKitPostfix(object __0)
        {
            try { KitMassLimit.ClampGeneratedKit(__0); }
            catch (Exception e) { Log.Error("[LTS Ammo Inventory Fallback 1.5] generated kit mass clamp failed: " + e); }
        }
    }

    internal static class MassWorkGiver
    {
        public static void ShouldSkipPostfix(Pawn pawn, ref bool __result)
        {
            if (__result) return;
            __result = !KitMassLimit.WornKits(pawn).Any(kit =>
                KitMassLimit.Bags(kit).Any(bag =>
                    KitMassLimit.TryReadBag(bag, out var ammo, out var count, out var max) &&
                    ammo != null && count < max && KitMassLimit.CanAccept(kit, ammo)));
        }

        public static void PotentialWorkThingsGlobalPostfix(Pawn pawn, ref IEnumerable<Thing> __result)
        {
            if (__result == null) return;
            var kits = KitMassLimit.WornKits(pawn).ToList();
            __result = __result.Where(t => t != null && kits.Any(k => KitMassLimit.CanAccept(k, t.def)));
        }

        public static void HasJobOnThingPostfix(Pawn pawn, Thing t, ref bool __result)
        {
            if (!__result || t == null) return;
            __result = KitMassLimit.WornKits(pawn).Any(k => KitMassLimit.CanAccept(k, t.def));
        }

        public static void JobOnThingPostfix(Pawn pawn, Thing t, ref Job __result)
        {
            if (__result == null || t == null) return;
            var kit = KitMassLimit.WornKits(pawn)
                .Select(KitMassLimit.ResolveKitThing)
                .FirstOrDefault(k => k != null && KitMassLimit.CanAccept(k, t.def));
            if (kit == null)
            {
                __result = null;
                return;
            }
            __result.SetTarget(TargetIndex.B, kit);
        }
    }
}
