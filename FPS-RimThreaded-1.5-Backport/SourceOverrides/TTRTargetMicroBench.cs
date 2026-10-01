using System;
using System.Collections.Generic;
using System.Diagnostics;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.AI;

namespace RimThreadedTTR
{
    /// <summary>
    /// Controlled micro-benchmark for parallel targeting. Inert unless launched
    /// with -ttrtargetmicro. Spawns ONE shooter and a fixed cluster of hostile
    /// targets on a single map, then times the real vanilla scoring function
    /// (GetAvailableShootingTargetsByScore) over many iterations with parallel
    /// ON vs OFF - back to back on the identical map/pawns, so map variance
    /// cancels and only the mod's effect remains.
    /// </summary>
    public class TTRTargetMicroBench : GameComponent
    {
        private const int TargetCount = 40;
        private const int Iterations = 4000;

        private readonly bool active;
        private int startTick = -1;
        private bool done;

        private delegate List<Pair<IAttackTarget, float>> ScoreListDelegate(
            List<IAttackTarget> rawTargets, IAttackTargetSearcher searcher, Verb verb);
        private static ScoreListDelegate scoreList;

        public TTRTargetMicroBench(Game game)
        {
            active = GenCommandLine.CommandLineArgPassed("ttrtargetmicro");
        }

        public override void GameComponentTick()
        {
            if (!active || done)
            {
                return;
            }
            Map map = Find.CurrentMap;
            if (map == null)
            {
                return;
            }
            if (startTick < 0)
            {
                startTick = Find.TickManager.TicksGame;
            }
            // Wait for the map to settle, then run once.
            if (Find.TickManager.TicksGame - startTick < 60)
            {
                return;
            }
            done = true;
            try
            {
                Run(map);
            }
            catch (Exception ex)
            {
                Log.Error("[TTRTargetMicroBench] failed: " + ex);
            }
            Application.Quit();
        }

        private void Run(Map map)
        {
            scoreList = AccessTools.MethodDelegate<ScoreListDelegate>(
                AccessTools.Method(typeof(AttackTargetFinder), "GetAvailableShootingTargetsByScore"));

            Faction hostile = null;
            List<Faction> all = Find.FactionManager.AllFactionsListForReading;
            for (int i = 0; i < all.Count; i++)
            {
                if (!all[i].IsPlayer && all[i].def.humanlikeFaction && all[i].HostileTo(Faction.OfPlayer))
                {
                    hostile = all[i];
                    break;
                }
            }
            if (hostile == null)
            {
                Log.Warning("[TTRTargetMicroBench] no hostile faction.");
                return;
            }

            // One player-faction shooter with a ranged weapon at the center.
            IntVec3 shooterCell = map.Center;
            Pawn shooter = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, Faction.OfPlayer);
            GenSpawn.Spawn(shooter, shooterCell, map);
            EquipRanged(shooter);
            Verb verb = shooter.CurrentEffectiveVerb;
            if (verb == null || verb.verbProps.IsMeleeAttack)
            {
                Log.Warning("[TTRTargetMicroBench] shooter has no ranged verb.");
                return;
            }

            // A fixed ring of hostile targets, all within weapon range and LOS.
            List<IAttackTarget> targets = new List<IAttackTarget>();
            int placed = 0;
            int radius = 2;
            while (placed < TargetCount && radius < 25)
            {
                foreach (IntVec3 c in GenRadial.RadialCellsAround(shooterCell, radius, useCenter: false))
                {
                    if (placed >= TargetCount)
                    {
                        break;
                    }
                    if (c.InBounds(map) && c.Standable(map) && c != shooterCell)
                    {
                        Pawn t = PawnGenerator.GeneratePawn(PawnKindDefOf.Colonist, hostile);
                        GenSpawn.Spawn(t, c, map);
                        targets.Add(t);
                        placed++;
                    }
                }
                radius++;
            }

            TTRSettings settings = TTRMod.Instance.settings;
            settings.targetingThreshold = 1; // always parallelize during the ON pass

            // Warm up both paths (JIT, caches).
            settings.parallelTargeting = false;
            for (int i = 0; i < 200; i++) { scoreList(targets, shooter, verb); }
            settings.parallelTargeting = true;
            for (int i = 0; i < 200; i++) { scoreList(targets, shooter, verb); }

            // Measure vanilla (serial).
            settings.parallelTargeting = false;
            Stopwatch swSerial = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++) { scoreList(targets, shooter, verb); }
            swSerial.Stop();

            // Measure parallel.
            settings.parallelTargeting = true;
            TargetingPatches.runtimeDisabled = false;
            Stopwatch swParallel = Stopwatch.StartNew();
            for (int i = 0; i < Iterations; i++) { scoreList(targets, shooter, verb); }
            swParallel.Stop();

            double serialUs = swSerial.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
            double parallelUs = swParallel.Elapsed.TotalMilliseconds * 1000.0 / Iterations;
            double speedup = serialUs / parallelUs;
            Log.Message(string.Format(
                "[TTRTargetMicroBench] RESULT targets={0} iters={1} | serial={2:F1}us/call | parallel={3:F1}us/call | speedup={4:F2}x | parallelRuns={5} disabled={6}",
                targets.Count, Iterations, serialUs, parallelUs, speedup,
                TargetingPatches.parallelRunCount, TargetingPatches.runtimeDisabled));
        }

        private void EquipRanged(Pawn pawn)
        {
            ThingDef gunDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_AssaultRifle");
            if (gunDef == null)
            {
                gunDef = DefDatabase<ThingDef>.GetNamedSilentFail("Gun_BoltActionRifle");
            }
            if (gunDef == null)
            {
                return;
            }
            ThingWithComps gun = (ThingWithComps)ThingMaker.MakeThing(gunDef);
            pawn.equipment.DestroyAllEquipment();
            pawn.equipment.AddEquipment(gun);
        }
    }
}
