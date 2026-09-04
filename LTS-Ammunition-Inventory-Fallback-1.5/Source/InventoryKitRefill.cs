using System;
using System.Linq;
using Verse;

namespace LTSAmmoInventoryFallback15
{
    internal static class InventoryKitRefill
    {
        public static void RefillPrefix(Pawn pawn)
        {
            try { Refill(pawn); }
            catch (Exception e) { Log.Error("[LTS Ammo Inventory Fallback 1.5] kit refill failed: " + e); }
        }

        private static void Refill(Pawn pawn)
        {
            if (pawn?.inventory?.innerContainer == null) return;

            foreach (var kit in KitMassLimit.WornKits(pawn))
            {
                foreach (var bag in KitMassLimit.Bags(kit))
                {
                    if (!KitMassLimit.TryReadBag(bag, out var chosen, out var count, out var max)) continue;
                    if (chosen == null || count >= max) continue;

                    var stacks = pawn.inventory.innerContainer
                        .Where(t => t != null && !t.Destroyed && t.stackCount > 0 && t.def == chosen)
                        .ToList();

                    foreach (var stack in stacks)
                    {
                        if (count >= max) break;
                        int byMass = KitMassLimit.MaxAdditionalRounds(kit, chosen);
                        if (byMass <= 0) break;

                        int take = Math.Min(max - count, Math.Min(stack.stackCount, byMass));
                        if (take <= 0) break;

                        var used = stack.SplitOff(take);
                        if (used != null && !used.Destroyed) used.Destroy(DestroyMode.Vanish);
                        count += take;
                        KitMassLimit.SetBagCount(bag, count);
                    }
                }
            }
        }
    }
}
