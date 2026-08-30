using System;
using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI.Group;

namespace FireSteelLTSAmmoCompat
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            new Harmony("local.firesteel.ltsammo.cannon15").PatchAll();
        }
    }

    public class CompProperties_FieldCannonReload : CompProperties
    {
        public int reloadTicks = 720;

        public CompProperties_FieldCannonReload()
        {
            compClass = typeof(CompFieldCannonReload);
        }
    }

    public class CompFieldCannonReload : ThingComp
    {
        private int reloadTicksLeft;
        private bool roundReady;
        private Effecter progressBarEffecter;

        public CompProperties_FieldCannonReload Props => (CompProperties_FieldCannonReload)props;
        public CompRefuelable FuelComp => parent.GetComp<CompRefuelable>();
        public CompMannable MannableComp => parent.GetComp<CompMannable>();
        public bool HasRound => FuelComp != null && FuelComp.HasFuel;
        public bool MannedNow => MannableComp == null || MannableComp.MannedNow;
        public bool ReadyToFire => HasRound && roundReady && reloadTicksLeft <= 0;
        public bool Reloading => HasRound && !roundReady;
        public float ReloadProgress => Props.reloadTicks <= 0 ? 1f : Mathf.Clamp01(1f - (float)reloadTicksLeft / Props.reloadTicks);

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref reloadTicksLeft, "TA_cannonReloadTicksLeft", 0);
            Scribe_Values.Look(ref roundReady, "TA_cannonRoundReady", false);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (!HasRound)
                {
                    reloadTicksLeft = 0;
                    roundReady = false;
                }
                else if (!roundReady && reloadTicksLeft <= 0)
                {
                    reloadTicksLeft = Props.reloadTicks;
                }
            }
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            if (!HasRound)
            {
                reloadTicksLeft = 0;
                roundReady = false;
                return;
            }

            if (!respawningAfterLoad)
                StartReload();
            else if (!roundReady && reloadTicksLeft <= 0)
                reloadTicksLeft = Props.reloadTicks;
        }

        public override void ReceiveCompSignal(string signal)
        {
            base.ReceiveCompSignal(signal);
            if (signal == CompRefuelable.RefueledSignal)
            {
                if (HasRound && !roundReady)
                    StartReload();
            }
            else if (signal == CompRefuelable.RanOutOfFuelSignal)
            {
                ResetEmpty();
            }
        }

        public override void CompTick()
        {
            base.CompTick();

            if (!HasRound)
            {
                ResetEmpty();
                return;
            }

            if (roundReady)
            {
                CleanupProgressBar();
                return;
            }

            if (reloadTicksLeft <= 0)
                reloadTicksLeft = Props.reloadTicks;

            if (MannedNow && reloadTicksLeft > 0)
                reloadTicksLeft--;

            if (reloadTicksLeft <= 0)
            {
                reloadTicksLeft = 0;
                roundReady = true;
                CleanupProgressBar();
                return;
            }

            UpdateProgressBar();
        }

        public override string CompInspectStringExtra()
        {
            if (!HasRound)
                return "TA_Cannon_WaitingAmmo".Translate();
            if (ReadyToFire)
                return "TA_Cannon_Loaded".Translate();

            string time = reloadTicksLeft.ToStringSecondsFromTicks();
            return MannedNow
                ? "TA_Cannon_Loading".Translate(time)
                : "TA_Cannon_LoadPaused".Translate(time);
        }

        public override void PostDeSpawn(Map map)
        {
            CleanupProgressBar();
            base.PostDeSpawn(map);
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            CleanupProgressBar();
            base.PostDestroy(mode, previousMap);
        }

        private void StartReload()
        {
            roundReady = false;
            reloadTicksLeft = Props.reloadTicks;
        }

        private void ResetEmpty()
        {
            reloadTicksLeft = 0;
            roundReady = false;
            CleanupProgressBar();
        }

        private void UpdateProgressBar()
        {
            if (!parent.Spawned || !Reloading)
            {
                CleanupProgressBar();
                return;
            }

            if (progressBarEffecter == null)
                progressBarEffecter = EffecterDefOf.ProgressBar.Spawn();

            progressBarEffecter.EffectTick((TargetInfo)parent, TargetInfo.Invalid);
            if (progressBarEffecter.children.Count > 0 && progressBarEffecter.children[0] is SubEffecter_ProgressBar child && child.mote != null)
            {
                child.mote.progress = ReloadProgress;
                child.mote.offsetZ = -0.8f;
            }
        }

        private void CleanupProgressBar()
        {
            if (progressBarEffecter == null)
                return;
            progressBarEffecter.Cleanup();
            progressBarEffecter = null;
        }
    }

    [HarmonyPatch(typeof(Building_TurretGun), nameof(Building_TurretGun.TryStartShootSomething))]
    public static class Patch_BuildingTurretGun_TryStartShootSomething
    {
        public static bool Prefix(Building_TurretGun __instance)
        {
            CompFieldCannonReload comp = __instance.GetComp<CompFieldCannonReload>();
            return comp == null || comp.ReadyToFire;
        }
    }

    internal static class SiegeCompatUtility
    {
        public const float CannonChancePerArtillerySlot = 0.33f;
        private const float SupplyRadius = 40f;
        private const int ReplenishAt = 4;
        private const int ReplenishCount = 10;
        private const int InitialAmmoPerCannon = 5;

        public static ThingDef CannonDef => DefDatabase<ThingDef>.GetNamedSilentFail("TA_FourPounderCannon_Turret");
        public static ThingDef MedievalAmmoDef => DefDatabase<ThingDef>.GetNamedSilentFail("AmmoMedieval");
        public static ThingDef StoneBoulderDef => DefDatabase<ThingDef>.GetNamedSilentFail("DankPyon_StoneBoulder");

        public static bool IsBaseDestroyerArtillery(ThingDef def)
        {
            return def != null && def.building != null && def.building.buildingTags != null && def.building.buildingTags.Contains("Artillery_BaseDestroyer");
        }

        public static bool IsOurCannon(ThingDef def)
        {
            ThingDef cannon = CannonDef;
            return cannon != null && def == cannon;
        }

        public static int CountNearby(Map map, IntVec3 center, ThingDef def, float radius)
        {
            if (map == null || def == null)
                return 0;

            int count = 0;
            List<Thing> things = map.listerThings.ThingsOfDef(def);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (!thing.Destroyed && thing.Spawned && thing.Position.InHorDistOf(center, radius))
                    count += thing.stackCount;
            }
            return count;
        }

        public static bool HasLiveCannon(LordToil_Siege siege)
        {
            ThingDef cannon = CannonDef;
            if (cannon == null || siege == null || siege.Map == null || siege.lord == null)
                return false;

            List<Thing> things = siege.Map.listerThings.ThingsOfDef(cannon);
            for (int i = 0; i < things.Count; i++)
            {
                Thing thing = things[i];
                if (!thing.Destroyed && thing.Spawned && thing.Faction == siege.lord.faction && thing.Position.InHorDistOf(siege.FlagLoc, SupplyRadius))
                    return true;
            }
            return false;
        }

        public static bool HasLiveBoulderArtillery(LordToil_Siege siege)
        {
            ThingDef boulder = StoneBoulderDef;
            if (boulder == null || siege == null || siege.Map == null || siege.lord == null)
                return false;

            List<Thing> buildings = siege.Map.listerThings.ThingsInGroup(ThingRequestGroup.BuildingArtificial);
            for (int i = 0; i < buildings.Count; i++)
            {
                Thing thing = buildings[i];
                if (thing.Destroyed || !thing.Spawned || thing.Faction != siege.lord.faction || !thing.Position.InHorDistOf(siege.FlagLoc, SupplyRadius))
                    continue;

                ThingDef def = thing.def;
                if (def == null || def.building == null || def.building.buildingTags == null || !def.building.buildingTags.Contains("Artillery"))
                    continue;

                if (ArtilleryCanUseShell(def, boulder, siege.lord.faction.def.techLevel))
                    return true;
            }
            return false;
        }

        public static int CountCannonBlueprints(LordToil_Siege siege)
        {
            ThingDef cannon = CannonDef;
            if (cannon == null || siege == null || siege.Map == null || siege.lord == null)
                return 0;

            int count = 0;
            List<Thing> allThings = siege.Map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                Blueprint_Build blue = allThings[i] as Blueprint_Build;
                if (blue == null || blue.Destroyed || blue.Faction != siege.lord.faction || !blue.Position.InHorDistOf(siege.FlagLoc, SupplyRadius))
                    continue;

                ThingDef builtDef = blue.def.entityDefToBuild as ThingDef;
                if (builtDef == cannon)
                    count++;
            }
            return count;
        }

        public static bool HasBoulderArtilleryBlueprint(LordToil_Siege siege)
        {
            ThingDef boulder = StoneBoulderDef;
            if (boulder == null || siege == null || siege.Map == null || siege.lord == null)
                return false;

            List<Thing> allThings = siege.Map.listerThings.AllThings;
            for (int i = 0; i < allThings.Count; i++)
            {
                Blueprint_Build blue = allThings[i] as Blueprint_Build;
                if (blue == null || blue.Destroyed || blue.Faction != siege.lord.faction || !blue.Position.InHorDistOf(siege.FlagLoc, SupplyRadius))
                    continue;

                ThingDef builtDef = blue.def.entityDefToBuild as ThingDef;
                if (builtDef != null && IsBaseDestroyerArtillery(builtDef) && ArtilleryCanUseShell(builtDef, boulder, siege.lord.faction.def.techLevel))
                    return true;
            }
            return false;
        }

        private static bool ArtilleryCanUseShell(ThingDef artilleryDef, ThingDef shellDef, TechLevel techLevel)
        {
            if (artilleryDef == null || shellDef == null)
                return false;

            try
            {
                ThingDef selected = TurretGunUtility.TryFindRandomShellDef(artilleryDef, false, true, techLevel, false, 250f);
                return selected == shellDef;
            }
            catch
            {
                return false;
            }
        }

        public static void DropSupplies(LordToil_Siege siege, ThingDef def, int count)
        {
            if (siege == null || siege.Map == null || def == null || count <= 0)
                return;

            List<Thing> things = new List<Thing>();
            int left = count;
            while (left > 0)
            {
                int stack = Mathf.Min(left, def.stackLimit);
                Thing thing = ThingMaker.MakeThing(def);
                thing.stackCount = stack;
                things.Add(thing);
                left -= stack;
            }

            DropPodUtility.DropThingsNear(siege.FlagLoc, siege.Map, things, 110, false, false, true);
        }

        public static void EnsureInitialSupplies(LordToil_Siege siege)
        {
            ThingDef ammo = MedievalAmmoDef;
            int cannonBlueprints = CountCannonBlueprints(siege);
            if (ammo != null && cannonBlueprints > 0 && CountNearby(siege.Map, siege.FlagLoc, ammo, 120f) == 0)
                DropSupplies(siege, ammo, InitialAmmoPerCannon * cannonBlueprints);

            ThingDef boulder = StoneBoulderDef;
            if (boulder != null && HasBoulderArtilleryBlueprint(siege) && CountNearby(siege.Map, siege.FlagLoc, boulder, 120f) == 0)
                DropSupplies(siege, boulder, InitialAmmoPerCannon);
        }

        public static void EnsureReplenishment(LordToil_Siege siege)
        {
            if (siege == null || siege.Map == null || siege.lord == null || Find.TickManager.TicksGame % 500 != 0)
                return;

            ThingDef ammo = MedievalAmmoDef;
            if (ammo != null && HasLiveCannon(siege) && CountNearby(siege.Map, siege.FlagLoc, ammo, SupplyRadius) < ReplenishAt)
                DropSupplies(siege, ammo, ReplenishCount);

            ThingDef boulder = StoneBoulderDef;
            if (boulder != null && HasLiveBoulderArtillery(siege) && CountNearby(siege.Map, siege.FlagLoc, boulder, SupplyRadius) < ReplenishAt)
                DropSupplies(siege, boulder, ReplenishCount);
        }
    }

    [HarmonyPatch(typeof(SiegeBlueprintPlacer), nameof(SiegeBlueprintPlacer.PlaceBlueprints))]
    public static class Patch_SiegeBlueprintPlacer_PlaceBlueprints
    {
        public static IEnumerable<Blueprint_Build> Postfix(IEnumerable<Blueprint_Build> __result, Map map)
        {
            foreach (Blueprint_Build blue in __result)
            {
                if (blue == null || blue.Destroyed)
                {
                    yield return blue;
                    continue;
                }

                ThingDef builtDef = blue.def.entityDefToBuild as ThingDef;
                ThingDef cannon = SiegeCompatUtility.CannonDef;
                if (cannon == null || !SiegeCompatUtility.IsBaseDestroyerArtillery(builtDef) || !Rand.Chance(SiegeCompatUtility.CannonChancePerArtillerySlot))
                {
                    yield return blue;
                    continue;
                }

                IntVec3 position = blue.Position;
                Rot4 rotation = blue.Rotation;
                Faction faction = blue.Faction;
                ThingDef oldStuff = blue.stuffToUse;
                blue.Destroy(DestroyMode.Cancel);

                Blueprint_Build replacement = null;
                try
                {
                    replacement = GenConstruct.PlaceBlueprintForBuild(cannon, position, map, rotation, faction, null);
                }
                catch (Exception ex)
                {
                    Log.Warning("[4-Pounder Non-CE] Could not replace siege artillery blueprint with 4-pounder; retaining original artillery. " + ex.Message);
                }

                if (replacement != null)
                {
                    yield return replacement;
                    continue;
                }

                Blueprint_Build restored = GenConstruct.PlaceBlueprintForBuild(builtDef, position, map, rotation, faction, oldStuff);
                yield return restored;
            }
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.Init))]
    public static class Patch_LordToilSiege_Init
    {
        public static void Postfix(LordToil_Siege __instance)
        {
            SiegeCompatUtility.EnsureInitialSupplies(__instance);
        }
    }

    [HarmonyPatch(typeof(LordToil_Siege), nameof(LordToil_Siege.LordToilTick))]
    public static class Patch_LordToilSiege_LordToilTick
    {
        public static void Postfix(LordToil_Siege __instance)
        {
            SiegeCompatUtility.EnsureReplenishment(__instance);
        }
    }
}
