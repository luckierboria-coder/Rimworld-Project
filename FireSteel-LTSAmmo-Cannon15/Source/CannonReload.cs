using System;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

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
}
