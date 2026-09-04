using System;
using System.Collections;
using System.Linq;
using HarmonyLib;
using Verse;

namespace LTSAmmoInventoryFallback15
{
    internal static class InventoryKitRefill
    {
        private static readonly System.Reflection.MethodInfo GetWornKits = AccessTools.Method(PatchRegistry.AmmoLogic, "GetWornKits");

        public static void RefillPrefix(Pawn pawn)
        {
            try { Refill(pawn); }
            catch (Exception e) { Log.Error("[LTS Ammo Inventory Fallback 1.5] kit refill failed: " + e); }
        }

        private static void Refill(Pawn pawn)
        {
            if (pawn?.inventory?.innerContainer == null || GetWornKits == null) return;
            var kits = GetWornKits.Invoke(null, new object[] { pawn }) as IEnumerable;
            if (kits == null) return;

            foreach (var kit in kits)
            {
                if (kit == null) continue;
                var comp = AccessTools.Property(kit.GetType(), "KitComp")?.GetValue(kit, null);
                if (comp == null) continue;
                var bags = AccessTools.Property(comp.GetType(), "Bags")?.GetValue(comp, null) as IEnumerable;
                if (bags == null) continue;

                foreach (var bag in bags)
                {
                    if (bag == null) continue;
                    var bt = bag.GetType();
                    var chosenP = AccessTools.Property(bt, "ChosenAmmo");
                    var countP = AccessTools.Property(bt, "Count");
                    var maxP = AccessTools.Property(bt, "MaxCount");
                    var chosen = chosenP?.GetValue(bag, null) as ThingDef;
                    if (chosen == null || countP == null || maxP == null) continue;

                    int count = (int)countP.GetValue(bag, null);
                    int max = (int)maxP.GetValue(bag, null);
                    int room = max - count;
                    if (room <= 0) continue;

                    var stacks = pawn.inventory.innerContainer
                        .Where(t => t != null && !t.Destroyed && t.stackCount > 0 && t.def == chosen)
                        .ToList();

                    foreach (var stack in stacks)
                    {
                        if (room <= 0) break;
                        int take = Math.Min(room, stack.stackCount);
                        var used = stack.SplitOff(take);
                        used.Destroy(DestroyMode.Vanish);
                        count += take;
                        room -= take;
                        countP.SetValue(bag, count, null);
                    }
                }
            }
        }
    }
}
