using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace LTSAmmoInventoryFallback15
{
    internal static class InventoryAmmoUtility
    {
        private static readonly MethodInfo CanUseAmmo = AccessTools.Method(PatchRegistry.AmmoLogic, "WeaponDefCanUseAmmoDef");
        private static readonly Dictionary<ThingDef, List<ThingDef>> CompatibleCache = new Dictionary<ThingDef, List<ThingDef>>();
        private static readonly List<ThingDef> EmptyList = new List<ThingDef>();

        internal static List<ThingDef> CompatibleAmmoDefs(ThingDef weaponDef)
        {
            if (weaponDef == null || CanUseAmmo == null) return EmptyList;
            if (CompatibleCache.TryGetValue(weaponDef, out var cached)) return cached;

            var result = new List<ThingDef>();
            try
            {
                var p = AccessTools.Property(PatchRegistry.Settings, "AvailableAmmo");
                var available = p?.GetValue(null, null) as IEnumerable;
                if (available != null)
                {
                    foreach (var obj in available)
                    {
                        var ammo = obj as ThingDef;
                        if (ammo == null) continue;
                        try
                        {
                            if ((bool)CanUseAmmo.Invoke(null, new object[] { weaponDef, ammo })) result.Add(ammo);
                        }
                        catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory System 1.5] compatibility cache failed: " + e);
            }

            CompatibleCache[weaponDef] = result;
            return result;
        }

        internal static Thing FindInventoryAmmo(Pawn pawn, ThingDef weaponDef)
        {
            if (pawn?.inventory?.innerContainer == null) return null;
            var compatible = CompatibleAmmoDefs(weaponDef);
            if (compatible.Count == 0) return null;

            foreach (var thing in pawn.inventory.innerContainer)
            {
                if (thing == null || thing.Destroyed || thing.stackCount <= 0) continue;
                if (compatible.Contains(thing.def)) return thing;
            }
            return null;
        }

        internal static bool HasInventoryAmmo(Pawn pawn, ThingDef weaponDef)
        {
            return FindInventoryAmmo(pawn, weaponDef) != null;
        }

        internal static bool RequiresAmmo(Thing weapon)
        {
            return weapon != null && CompatibleAmmoDefs(weapon.def).Count > 0;
        }

        internal static float AmmoMass(ThingDef ammo)
        {
            if (ammo == null) return 0f;
            try
            {
                float mass = ammo.GetStatValueAbstract(StatDefOf.Mass);
                return mass > 0f && !float.IsNaN(mass) && !float.IsInfinity(mass) ? mass : 0f;
            }
            catch { return 0f; }
        }

        internal static float CarryCapacity(Pawn pawn)
        {
            if (pawn == null) return 0f;
            try { return Math.Max(0f, pawn.GetStatValue(StatDefOf.CarryingCapacity)); }
            catch { return 0f; }
        }

        internal static int RoundsForMassBudget(Pawn pawn, ThingDef ammo, float capacityFraction)
        {
            float perRound = AmmoMass(ammo);
            if (perRound <= 0f) return 0;
            float budget = CarryCapacity(pawn) * Math.Max(0f, capacityFraction);
            return Math.Max(0, (int)Math.Floor((budget + 0.0001f) / perRound));
        }

        internal static int AddAmmoToInventory(Pawn pawn, ThingDef ammo, int count)
        {
            if (pawn?.inventory?.innerContainer == null || ammo == null || count <= 0) return 0;
            int added = 0;
            while (count > 0)
            {
                int take = Math.Min(count, Math.Max(1, ammo.stackLimit));
                Thing thing = ThingMaker.MakeThing(ammo);
                thing.stackCount = take;
                if (!pawn.inventory.innerContainer.TryAdd(thing))
                {
                    thing.Destroy(DestroyMode.Vanish);
                    break;
                }
                added += take;
                count -= take;
            }
            return added;
        }
    }

    internal static class LegacyKitBypass
    {
        private static readonly MethodInfo GetWornKits = AccessTools.Method(PatchRegistry.AmmoLogic, "GetWornKits");

        public static bool LoadSpawnedKitPrefix() => false;

        public static bool WorkGiverShouldSkipPrefix(ref bool __result)
        {
            __result = true;
            return false;
        }

        public static bool WorkGiverPotentialPrefix(ref IEnumerable<Thing> __result)
        {
            __result = Enumerable.Empty<Thing>();
            return false;
        }

        public static void FilterKitGizmosPostfix(ref IEnumerable<Gizmo> __result)
        {
            if (__result == null) return;
            __result = __result.Where(g => g == null || g.GetType().FullName != "Ammunition.Gizmos.GizmoAmmunition");
        }

        public static void AmmoCheckPrefix(Pawn pawn)
        {
            try { MigrateLegacyKitAmmo(pawn); }
            catch (Exception e) { Log.Error("[LTS Ammo Inventory System 1.5] legacy kit migration failed: " + e); }
        }

        private static void MigrateLegacyKitAmmo(Pawn pawn)
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
                    var type = bag.GetType();
                    var chosenP = AccessTools.Property(type, "ChosenAmmo");
                    var countP = AccessTools.Property(type, "Count");
                    if (chosenP == null || countP == null) continue;
                    var ammo = chosenP.GetValue(bag, null) as ThingDef;
                    int count = (int)countP.GetValue(bag, null);
                    if (ammo == null || count <= 0) continue;

                    countP.SetValue(bag, 0, null);
                    InventoryAmmoUtility.AddAmmoToInventory(pawn, ammo, count);
                }
            }
        }
    }

    internal static class NpcAmmoGeneration
    {
        private const float BaseFraction = 0.05f;
        private const float Variation = 0.15f;

        public static void GeneratePawnPostfix(Pawn __result)
        {
            try
            {
                var pawn = __result;
                if (pawn?.RaceProps == null || !pawn.RaceProps.Humanlike || pawn.RaceProps.IsMechanoid) return;
                if (pawn.Faction == Faction.OfPlayer || pawn.IsColonist) return;
                if (pawn.inventory?.innerContainer == null || pawn.equipment?.Primary == null) return;

                Thing weapon = pawn.equipment.Primary;
                var compatible = InventoryAmmoUtility.CompatibleAmmoDefs(weapon.def)
                    .Where(a => a != null && InventoryAmmoUtility.AmmoMass(a) > 0f)
                    .ToList();
                if (compatible.Count == 0) return;
                if (InventoryAmmoUtility.HasInventoryAmmo(pawn, weapon.def)) return;

                ThingDef ammo = compatible.RandomElement();
                float fraction = BaseFraction * Rand.Range(1f - Variation, 1f + Variation);
                int count = InventoryAmmoUtility.RoundsForMassBudget(pawn, ammo, fraction);
                if (count <= 0) return;
                InventoryAmmoUtility.AddAmmoToInventory(pawn, ammo, count);
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Inventory System 1.5] NPC initial ammo generation failed: " + e);
            }
        }
    }

    internal sealed class PendingReload
    {
        internal Pawn Pawn;
        internal Thing Weapon;
        internal int DueTick;
    }

    internal class AmmoReloadManager : GameComponent
    {
        private const int SearchRadiusSquared = 20 * 20;
        private const float ReloadFraction = 0.01f;
        private const int RetryTicks = 180;
        private const int MaxChecksPerTick = 4;
        private static AmmoReloadManager _instance;
        private readonly Dictionary<int, PendingReload> pending = new Dictionary<int, PendingReload>();
        private readonly List<int> scratch = new List<int>();

        public AmmoReloadManager(Game game) { _instance = this; }

        internal static void Request(Pawn pawn, Thing weapon)
        {
            if (_instance == null || pawn == null || weapon == null || pawn.Dead || pawn.Destroyed) return;
            int id = pawn.thingIDNumber;
            if (_instance.pending.TryGetValue(id, out var existing))
            {
                if (existing.Weapon == weapon) return;
                _instance.pending.Remove(id);
            }

            int delay = pawn.Faction == Faction.OfPlayer ? 0 : Rand.RangeInclusive(0, 60);
            _instance.pending[id] = new PendingReload
            {
                Pawn = pawn,
                Weapon = weapon,
                DueTick = Find.TickManager.TicksGame + delay
            };
        }

        public override void GameComponentTick()
        {
            if (pending.Count == 0) return;
            int tick = Find.TickManager.TicksGame;
            scratch.Clear();
            int checks = 0;

            foreach (var kv in pending)
            {
                if (checks >= MaxChecksPerTick) break;
                var p = kv.Value;
                if (p == null || p.DueTick > tick) continue;
                scratch.Add(kv.Key);
                checks++;
            }

            foreach (int key in scratch)
            {
                if (!pending.TryGetValue(key, out var entry)) continue;
                if (TryStartReload(entry)) pending.Remove(key);
                else if (!StillNeedsReload(entry)) pending.Remove(key);
                else entry.DueTick = tick + RetryTicks;
            }
        }

        private static bool StillNeedsReload(PendingReload entry)
        {
            var pawn = entry?.Pawn;
            var weapon = entry?.Weapon;
            if (pawn == null || weapon == null || pawn.Dead || pawn.Destroyed || pawn.Downed || !pawn.Spawned) return false;
            if (pawn.equipment?.Primary != weapon) return false;
            if (!InventoryAmmoUtility.RequiresAmmo(weapon)) return false;
            return !InventoryAmmoUtility.HasInventoryAmmo(pawn, weapon.def);
        }

        private static bool TryStartReload(PendingReload entry)
        {
            if (!StillNeedsReload(entry)) return false;
            Pawn pawn = entry.Pawn;
            Thing weapon = entry.Weapon;
            if (pawn.Map == null || pawn.jobs == null) return false;
            JobDef reloadDef = DefDatabase<JobDef>.GetNamedSilentFail("LTSIF_TakeAmmoInventory");
            if (reloadDef == null) return false;
            if (pawn.CurJob?.def == reloadDef) return true;

            Thing target = FindVisibleReachableAmmo(pawn, weapon.def);
            if (target == null) return false;

            int wanted = InventoryAmmoUtility.RoundsForMassBudget(pawn, target.def, ReloadFraction);
            if (wanted <= 0) return false;
            int count = Math.Min(wanted, target.stackCount);
            if (count <= 0 || !pawn.CanReserve(target, 1, count, null, false)) return false;

            Job job = JobMaker.MakeJob(reloadDef, target);
            job.count = count;
            job.playerForced = false;
            pawn.jobs.StartJob(job, JobCondition.InterruptForced, null, true, true, null, JobTag.Misc, false);
            return pawn.CurJob == job;
        }

        private static Thing FindVisibleReachableAmmo(Pawn pawn, ThingDef weaponDef)
        {
            var map = pawn.Map;
            if (map == null) return null;
            var candidates = new List<Thing>();

            foreach (ThingDef ammoDef in InventoryAmmoUtility.CompatibleAmmoDefs(weaponDef))
            {
                var things = map.listerThings.ThingsOfDef(ammoDef);
                for (int i = 0; i < things.Count; i++)
                {
                    Thing t = things[i];
                    if (t == null || !t.Spawned || t.Destroyed || t.stackCount <= 0) continue;
                    int dx = pawn.Position.x - t.Position.x;
                    int dz = pawn.Position.z - t.Position.z;
                    if (dx * dx + dz * dz > SearchRadiusSquared) continue;
                    if (t.IsForbidden(pawn)) continue;
                    if (!GenSight.LineOfSight(pawn.Position, t.Position, map)) continue;
                    candidates.Add(t);
                }
            }

            foreach (Thing t in candidates.OrderBy(x => pawn.Position.DistanceToSquared(x.Position)).Take(8))
            {
                if (!pawn.CanReserve(t, 1, 1, null, false)) continue;
                if (!pawn.CanReach(t, PathEndMode.ClosestTouch, Danger.Deadly)) continue;
                return t;
            }
            return null;
        }
    }

    public class JobDriver_TakeAmmoInventory : JobDriver
    {
        public override bool TryMakePreToilReservations(bool errorOnFailed)
        {
            return pawn.Reserve(job.targetA, job, 1, job.count, null, errorOnFailed);
        }

        protected override IEnumerable<Toil> MakeNewToils()
        {
            this.FailOnDespawnedNullOrForbidden(TargetIndex.A);
            yield return Toils_Goto.GotoThing(TargetIndex.A, PathEndMode.ClosestTouch);

            Toil take = new Toil();
            take.initAction = delegate
            {
                Thing target = job.targetA.Thing;
                if (target == null || target.Destroyed || pawn.inventory?.innerContainer == null) return;
                int count = Math.Min(Math.Max(0, job.count), target.stackCount);
                if (count <= 0) return;
                Thing split = target.SplitOff(count);
                if (split == null || split.Destroyed) return;
                if (!pawn.inventory.innerContainer.TryAdd(split))
                    GenPlace.TryPlaceThing(split, pawn.Position, pawn.Map, ThingPlaceMode.Near);
            };
            take.defaultCompleteMode = ToilCompleteMode.Instant;
            yield return take;
        }
    }
}
