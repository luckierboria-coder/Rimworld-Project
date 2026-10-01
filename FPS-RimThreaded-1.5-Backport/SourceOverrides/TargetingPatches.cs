using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimThreadedTTR
{
    /// <summary>
    /// Parallel combat targeting (v1.2). When a ranged pawn picks a target,
    /// vanilla scores every candidate on the main thread:
    /// AttackTargetFinder.GetAvailableShootingTargetsByScore runs, per enemy,
    /// line-of-sight walks, cover math and friendly-fire scans. In big fights
    /// (many shooters x many targets) this is a top TPS cost.
    ///
    /// This patch computes the same per-candidate results across worker
    /// threads (fork-join: the main thread waits, so game state is frozen -
    /// no staleness is possible), then assembles the final weighted list on
    /// the main thread with vanilla's exact rules. Same inputs, same scores,
    /// same target choice - just computed on more cores.
    ///
    /// Safety model:
    /// - Candidates were already validated by vanilla ON the main thread
    ///   (including mod-supplied validators) before this function is called;
    ///   no foreign code runs on our workers except the scoring chain below.
    /// - Only verbs whose class comes from the vanilla assembly are
    ///   parallelized (Combat Extended etc. fall through to vanilla code).
    /// - Verb's static scratch buffers (tempLeanShootSources/tempDestList)
    ///   are the reason vanilla's own CanHitTargetFrom is not thread-safe;
    ///   we replicate that short call chain with [ThreadStatic] buffers,
    ///   byte-for-byte identical logic (verified against 1.6.4871 source).
    /// - Apparel checks (which can call modded code) are hoisted to a single
    ///   main-thread pre-pass - they do not depend on the target.
    /// - Stat reads off-thread bypass the non-thread-safe temporary stat
    ///   cache via StatWorkerPatches (identical values, just uncached).
    /// - Any exception disables the feature for the session; vanilla takes
    ///   over mid-call with no harm done (our compute wrote nothing yet).
    /// </summary>
    public static class TargetingPatches
    {
        public static bool runtimeDisabled;

        // Diagnostic counters (main thread only).
        public static int parallelRunCount;
        public static int prefixCallCount;
        public static int maxCandidatesSeen;

        // Verification mode: when true, every parallel run is cross-checked
        // against vanilla's own can-hit result on the main thread, and any
        // disagreement is counted and logged. Enabled by -ttrverifytargeting;
        // used to prove the parallel path chooses identical targets. Off in
        // normal play (adds a full serial pass, defeating the point).
        public static bool verifyMode;
        public static int verifyChecks;
        public static int verifyMismatches;

        private static Assembly vanillaAssembly;

        // Open delegate for the private static vanilla scoring method, so the
        // score math is vanilla's own code, not a copy.
        private delegate float ScoreDelegate(IAttackTarget target, IAttackTargetSearcher searcher, Verb verb);

        private static ScoreDelegate scoreTarget;

        [ThreadStatic]
        private static List<IntVec3> tlsLeanSources;

        [ThreadStatic]
        private static List<IntVec3> tlsDestCells;

        public static void Apply(Harmony harmony)
        {
            vanillaAssembly = typeof(Verb).Assembly;
            verifyMode = GenCommandLine.CommandLineArgPassed("ttrverifytargeting");
            MethodInfo scoreMethod = AccessTools.Method(typeof(AttackTargetFinder), "GetShootingTargetScore");
            scoreTarget = AccessTools.MethodDelegate<ScoreDelegate>(scoreMethod);

            MethodInfo targetMethod = AccessTools.Method(typeof(AttackTargetFinder), "GetAvailableShootingTargetsByScore");
            harmony.Patch(targetMethod,
                new HarmonyMethod(typeof(TargetingPatches).GetMethod("ScoreTargetsPrefix")), null, null, null);
        }

        // Replaces AttackTargetFinder.GetAvailableShootingTargetsByScore when
        // the candidate list is large enough to be worth parallelizing.
        public static bool ScoreTargetsPrefix(List<IAttackTarget> rawTargets, IAttackTargetSearcher searcher, Verb verb,
            ref List<Pair<IAttackTarget, float>> __result)
        {
            prefixCallCount++;
            if (rawTargets != null && rawTargets.Count > maxCandidatesSeen)
            {
                maxCandidatesSeen = rawTargets.Count;
            }
            TTRSettings settings = TTRMod.Instance.settings;
            if (runtimeDisabled || !settings.parallelTargeting)
            {
                return true;
            }
            if (rawTargets == null || rawTargets.Count < settings.targetingThreshold)
            {
                return true;
            }
            if (verb == null || verb.caster == null || verb.GetType().Assembly != vanillaAssembly)
            {
                return true;
            }
            try
            {
                __result = ScoreTargetsParallel(rawTargets, searcher, verb);
                parallelRunCount++;
                if (verifyMode)
                {
                    VerifyAgainstVanilla(rawTargets, searcher, verb, __result);
                }
                return false;
            }
            catch (Exception ex)
            {
                runtimeDisabled = true;
                Log.Warning("[RimThreadedTTR] Parallel targeting failed and has been disabled for this session: " + ex);
                return true;
            }
        }

        // Set by a worker if it throws. GenThreading.ParallelFor swallows worker
        // exceptions (it only logs them), so we must surface failures ourselves;
        // otherwise a crashing parallel run would still look successful. Safe as
        // a shared field because the main thread blocks inside ParallelFor, so
        // this method is never re-entered concurrently.
        private static volatile Exception workerError;

        private static List<Pair<IAttackTarget, float>> ScoreTargetsParallel(List<IAttackTarget> rawTargets,
            IAttackTargetSearcher searcher, Verb verb)
        {
            int count = rawTargets.Count;
            bool[] canShoot = new bool[count];
            IntVec3 casterPos = verb.caster.Position;

            // Main-thread pre-pass: target-independent checks that could touch
            // modded code (apparel AllowVerbCast overrides).
            bool apparelPrevents = verb.ApparelPreventsShooting();

            // Phase 1 (parallel, thread-safe): the line-of-sight / can-hit
            // filter. This is the dominant cost with many candidates. Scoring is
            // NOT done here: vanilla's GetShootingTargetScore reaches into
            // FriendlyFireConeTargetScoreOffset -> ShotReport.HitReportFor ->
            // Verb.TryFindShootLineFromTo, which uses Verb's shared static
            // buffers and is not thread-safe. Only our thread-safe LOS replica
            // (verified against GenSight/ShootLeanUtility source) runs off-thread.
            workerError = null;
            int workers = TTRMod.Instance.settings.MaxThreadsClamped;
            GenThreading.ParallelFor(0, count, delegate(int i)
            {
                try
                {
                    IAttackTarget target = rawTargets[i];
                    if (target != searcher && CanHitTargetFromThreaded(verb, casterPos, target.Thing, apparelPrevents))
                    {
                        canShoot[i] = true;
                    }
                }
                catch (Exception ex)
                {
                    workerError = ex;
                }
            }, workers);
            if (workerError != null)
            {
                throw new Exception("parallel LOS worker failed", workerError);
            }

            // Phase 2 (serial, main thread): score only the shootable candidates
            // with vanilla's own code, then assemble the weighted list exactly as
            // vanilla's post-loop does. Same inputs, same scores, same choice.
            float[] scores = new float[count];
            float bestScore = 0f;
            IAttackTarget bestTarget = null;
            for (int i = 0; i < count; i++)
            {
                scores[i] = float.MinValue;
                if (canShoot[i])
                {
                    scores[i] = scoreTarget(rawTargets[i], searcher, verb);
                    if (bestTarget == null || scores[i] > bestScore)
                    {
                        bestTarget = rawTargets[i];
                        bestScore = scores[i];
                    }
                }
            }
            List<Pair<IAttackTarget, float>> result = new List<Pair<IAttackTarget, float>>();
            if (bestScore < 1f)
            {
                if (bestTarget != null)
                {
                    result.Add(new Pair<IAttackTarget, float>(bestTarget, 1f));
                }
            }
            else
            {
                float cutoff = bestScore - 30f;
                for (int j = 0; j < count; j++)
                {
                    if (rawTargets[j] != searcher && canShoot[j])
                    {
                        float score = scores[j];
                        if (score >= cutoff)
                        {
                            float weight = UnityEngine.Mathf.InverseLerp(bestScore - 30f, bestScore, score);
                            result.Add(new Pair<IAttackTarget, float>(rawTargets[j], weight));
                        }
                    }
                }
            }
            return result;
        }

        // Recomputes the weighted list the vanilla way (using vanilla's real
        // CanHitTargetFrom as ground truth) and compares it to the parallel
        // result. Any difference means the LOS replica diverged, which would be
        // a visible behavior change - exactly what must never happen.
        private static void VerifyAgainstVanilla(List<IAttackTarget> rawTargets, IAttackTargetSearcher searcher,
            Verb verb, List<Pair<IAttackTarget, float>> parallelResult)
        {
            int count = rawTargets.Count;
            float[] scores = new float[count];
            bool[] canShoot = new bool[count];
            float bestScore = 0f;
            IAttackTarget bestTarget = null;
            IntVec3 pos = searcher.Thing.Position;
            for (int i = 0; i < count; i++)
            {
                scores[i] = float.MinValue;
                if (rawTargets[i] == searcher)
                {
                    continue;
                }
                // Vanilla ground truth: exactly what CanShootAtFromCurrentPosition calls.
                canShoot[i] = verb.CanHitTargetFrom(pos, rawTargets[i].Thing);
                if (canShoot[i])
                {
                    scores[i] = scoreTarget(rawTargets[i], searcher, verb);
                    if (bestTarget == null || scores[i] > bestScore)
                    {
                        bestTarget = rawTargets[i];
                        bestScore = scores[i];
                    }
                }
            }
            List<Pair<IAttackTarget, float>> vanilla = new List<Pair<IAttackTarget, float>>();
            if (bestScore < 1f)
            {
                if (bestTarget != null)
                {
                    vanilla.Add(new Pair<IAttackTarget, float>(bestTarget, 1f));
                }
            }
            else
            {
                float cutoff = bestScore - 30f;
                for (int j = 0; j < count; j++)
                {
                    if (rawTargets[j] != searcher && canShoot[j] && scores[j] >= cutoff)
                    {
                        float weight = UnityEngine.Mathf.InverseLerp(bestScore - 30f, bestScore, scores[j]);
                        vanilla.Add(new Pair<IAttackTarget, float>(rawTargets[j], weight));
                    }
                }
            }

            verifyChecks++;
            bool mismatch = vanilla.Count != parallelResult.Count;
            if (!mismatch)
            {
                for (int i = 0; i < vanilla.Count; i++)
                {
                    if (vanilla[i].First != parallelResult[i].First
                        || UnityEngine.Mathf.Abs(vanilla[i].Second - parallelResult[i].Second) > 0.001f)
                    {
                        mismatch = true;
                        break;
                    }
                }
            }
            if (mismatch)
            {
                verifyMismatches++;
                if (verifyMismatches <= 5)
                {
                    Log.Warning("[RimThreadedTTR] Targeting verify MISMATCH: vanilla listed "
                        + vanilla.Count + " targets, parallel listed " + parallelResult.Count
                        + " (candidates=" + count + ").");
                }
            }
        }

        // ---------------------------------------------------------------
        // Thread-safe replica of vanilla Verb.CanHitTargetFrom for vanilla
        // verb classes. Identical logic to 1.6.4871; the only difference is
        // [ThreadStatic] scratch buffers instead of Verb's shared statics,
        // and the apparel check hoisted out (computed once on main thread).
        // ---------------------------------------------------------------

        private static bool CanHitTargetFromThreaded(Verb verb, IntVec3 root, Thing targetThing, bool apparelPrevents)
        {
            if (targetThing == null)
            {
                return false;
            }
            if (targetThing == verb.caster)
            {
                return verb.targetParams.canTargetSelf;
            }
            Pawn targetPawn = targetThing as Pawn;
            if (targetPawn != null && targetPawn.IsPsychologicallyInvisible() && verb.caster.HostileTo(targetPawn))
            {
                return false;
            }
            if (apparelPrevents)
            {
                return false;
            }
            return TryFindShootLineThreaded(verb, root, targetThing);
        }

        private static bool TryFindShootLineThreaded(Verb verb, IntVec3 root, Thing targetThing)
        {
            if (targetThing.Map != verb.caster.Map)
            {
                return false;
            }
            LocalTargetInfo targ = new LocalTargetInfo(targetThing);
            if (verb.verbProps.IsMeleeAttack || verb.EffectiveRange <= 1.42f)
            {
                return ReachabilityImmediate.CanReachImmediate(root, targ, verb.caster.Map, PathEndMode.Touch, null);
            }
            CellRect occupiedRect = targetThing.OccupiedRect();
            if (verb.OutOfRange(root, targ, occupiedRect))
            {
                return false;
            }
            if (!verb.verbProps.requireLineOfSight)
            {
                return true;
            }
            if (verb.CasterIsPawn)
            {
                if (CanHitFromCellThreaded(verb, root, targetThing))
                {
                    return true;
                }
                if (tlsLeanSources == null)
                {
                    tlsLeanSources = new List<IntVec3>();
                }
                ShootLeanUtility.LeanShootingSourcesFromTo(root, occupiedRect.ClosestCellTo(root), verb.caster.Map, tlsLeanSources);
                for (int i = 0; i < tlsLeanSources.Count; i++)
                {
                    if (CanHitFromCellThreaded(verb, tlsLeanSources[i], targetThing))
                    {
                        return true;
                    }
                }
            }
            else
            {
                foreach (IntVec3 cell in verb.caster.OccupiedRect())
                {
                    if (CanHitFromCellThreaded(verb, cell, targetThing))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool CanHitFromCellThreaded(Verb verb, IntVec3 sourceCell, Thing targetThing)
        {
            if (targetThing.Map != verb.caster.Map)
            {
                return false;
            }
            if (tlsDestCells == null)
            {
                tlsDestCells = new List<IntVec3>();
            }
            ShootLeanUtility.CalcShootableCellsOf(tlsDestCells, targetThing, sourceCell);
            bool includeCorners = targetThing.def.Fillage == FillCategory.Full;
            for (int i = 0; i < tlsDestCells.Count; i++)
            {
                if (CanHitCellFromCellThreaded(verb, sourceCell, tlsDestCells[i], includeCorners))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool CanHitCellFromCellThreaded(Verb verb, IntVec3 sourceSq, IntVec3 targetLoc, bool includeCorners)
        {
            Map map = verb.caster.Map;
            if (verb.verbProps.mustCastOnOpenGround && (!targetLoc.Standable(map) || map.thingGrid.CellContains(targetLoc, ThingCategory.Pawn)))
            {
                return false;
            }
            if (verb.verbProps.requireLineOfSight)
            {
                if (!includeCorners)
                {
                    if (!GenSight.LineOfSight(sourceSq, targetLoc, map, skipFirstCell: true))
                    {
                        return false;
                    }
                }
                else if (!GenSight.LineOfSightToEdges(sourceSq, targetLoc, map, skipFirstCell: true))
                {
                    return false;
                }
            }
            return true;
        }
    }

    /// <summary>
    /// StatWorker's temporary cache is a plain Dictionary and not safe to
    /// write from worker threads. On any background thread, stat reads are
    /// routed to the uncached path - identical values, no cache writes.
    /// The main thread is untouched (single branch check).
    /// </summary>
    public static class StatWorkerPatches
    {
        public static void Apply(Harmony harmony)
        {
            MethodInfo cached = AccessTools.Method(typeof(StatWorker), "GetValue",
                new Type[] { typeof(Thing), typeof(bool), typeof(int) });
            harmony.Patch(cached,
                new HarmonyMethod(typeof(StatWorkerPatches).GetMethod("GetValuePrefix")), null, null, null);
        }

        public static bool GetValuePrefix(StatWorker __instance, Thing thing, bool applyPostProcess, ref float __result)
        {
            if (UnityData.IsInMainThread)
            {
                return true;
            }
            __result = __instance.GetValue(StatRequest.For(thing), applyPostProcess);
            return false;
        }
    }
}
