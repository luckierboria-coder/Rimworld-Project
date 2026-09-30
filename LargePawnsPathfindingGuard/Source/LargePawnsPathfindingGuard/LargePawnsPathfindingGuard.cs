using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace Allen.LargePawnsPathfindingGuard
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        internal const string HarmonyId = "allen.largepawns.pathfindingguard";
        internal const string Version = "1.0.0";

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                PathStormGuard.AuditCompatibility();
                Log.Message("[LargePawns Pathfinding Guard] v" + Version + " loaded. " + PathStormGuard.Summary());
            }
            catch (Exception ex)
            {
                Log.Error("[LargePawns Pathfinding Guard] bootstrap failed: " + ex);
            }
        }
    }

    internal static class PathStormGuard
    {
        private const float LargeBodySize = 2.0f;
        private const long SlowPathUs = 100000L;
        private const int WindowTicks = 600;
        private const int StormScore = 3;
        private const int ForceExitScore = 4;
        private const int StormDurationTicks = 1800;
        private const int RetryIntervalTicks = 600;
        private const int ForceExitEdgeDistance = 64;
        private const int MaxStates = 256;
        private const int StateExpireTicks = 6000;

        private static readonly FieldInfo PatherPawnField = AccessTools.Field(typeof(Pawn_PathFollower), "pawn");
        private static readonly FieldInfo ExitMapOnArrivalField = AccessTools.Field(typeof(Job), "exitMapOnArrival");
        private static readonly Dictionary<int, PawnState> States = new Dictionary<int, PawnState>();

        private static bool audited;
        private static bool largePawnsDetected;
        private static bool pathfindingFrameworkDetected;
        private static long observed;
        private static long slowCalls;
        private static long failures;
        private static long storms;
        private static long suppressed;
        private static long forcedExits;
        private static long forceExitFailures;

        internal static void AuditCompatibility()
        {
            if (audited) return;
            audited = true;
            largePawnsDetected = HasOwner(AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"), "neku.largepawns") ||
                                 HasOwner(AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell"), "neku.largepawns");
            pathfindingFrameworkDetected = HasOwner(AccessTools.Method(typeof(Pawn_PathFollower), "TryEnterNextPathCell"), "pathfinding.framework");
        }

        private static bool HasOwner(MethodBase method, string owner)
        {
            if (method == null) return false;
            try
            {
                Patches p = Harmony.GetPatchInfo(method);
                if (p == null) return false;
                return p.Prefixes.Concat(p.Postfixes).Concat(p.Transpilers).Concat(p.Finalizers)
                    .Any(x => x != null && string.Equals(x.owner, owner, StringComparison.OrdinalIgnoreCase));
            }
            catch { return false; }
        }

        internal static bool Before(object pather, ref CallState state)
        {
            state = default(CallState);
            if (!largePawnsDetected || !pathfindingFrameworkDetected) return true;

            Pawn pawn = PawnOf(pather);
            if (!IsCandidate(pawn) || !IsExitIntent(pawn)) return true;

            observed++;
            int tick = CurrentTick();
            PawnState ps = GetState(pawn, tick);
            if (ps == null) return true;

            if (ps.StormUntilTick > tick)
            {
                if (ps.Score >= ForceExitScore && DistanceToEdge(pawn) <= ForceExitEdgeDistance)
                {
                    if (TryForceExit(pawn))
                    {
                        forcedExits++;
                        States.Remove(pawn.thingIDNumber);
                        return false;
                    }
                    forceExitFailures++;
                }

                if (tick < ps.NextAllowedTick)
                {
                    ps.Suppressed++;
                    ps.LastSeenTick = tick;
                    suppressed++;
                    return false;
                }

                ps.NextAllowedTick = tick + RetryIntervalTicks;
            }

            state = new CallState
            {
                Started = Stopwatch.GetTimestamp(),
                PawnId = pawn.thingIDNumber,
                Tick = tick
            };
            return true;
        }

        internal static void After(object pather, CallState state)
        {
            if (state.Started == 0L) return;
            long dt = Stopwatch.GetTimestamp() - state.Started;
            if (dt <= 0L) return;
            long us = dt * 1000000L / Stopwatch.Frequency;
            if (us < SlowPathUs) return;

            Pawn pawn = PawnOf(pather);
            if (pawn == null || pawn.thingIDNumber != state.PawnId || !IsCandidate(pawn) || !IsExitIntent(pawn)) return;
            RecordBadAttempt(pawn, state.Tick, us >= 1000000L ? 2 : 1);
            slowCalls++;
        }

        internal static void OnPatherFailed(object pather)
        {
            if (!largePawnsDetected || !pathfindingFrameworkDetected) return;
            Pawn pawn = PawnOf(pather);
            if (!IsCandidate(pawn) || !IsExitIntent(pawn)) return;
            failures++;
            RecordBadAttempt(pawn, CurrentTick(), 1);
        }

        private static void RecordBadAttempt(Pawn pawn, int tick, int weight)
        {
            PawnState ps = GetState(pawn, tick);
            if (ps == null) return;

            if (ps.WindowStartTick < 0 || tick - ps.WindowStartTick > WindowTicks)
            {
                ps.WindowStartTick = tick;
                ps.Score = 0;
            }

            ps.Score += Math.Max(1, weight);
            ps.LastSeenTick = tick;
            if (ps.Score >= StormScore)
            {
                if (ps.StormUntilTick <= tick) storms++;
                ps.StormUntilTick = Math.Max(ps.StormUntilTick, tick + StormDurationTicks);
                ps.NextAllowedTick = Math.Max(ps.NextAllowedTick, tick + RetryIntervalTicks);
            }
        }

        private static PawnState GetState(Pawn pawn, int tick)
        {
            if (pawn == null) return null;
            Cleanup(tick);
            PawnState ps;
            if (!States.TryGetValue(pawn.thingIDNumber, out ps))
            {
                if (States.Count >= MaxStates) RemoveOldest();
                ps = new PawnState { WindowStartTick = tick, LastSeenTick = tick };
                States[pawn.thingIDNumber] = ps;
            }
            ps.LastSeenTick = tick;
            return ps;
        }

        private static void Cleanup(int tick)
        {
            if (States.Count == 0 || (observed & 255L) != 0L) return;
            List<int> remove = null;
            foreach (KeyValuePair<int, PawnState> kv in States)
            {
                if (tick - kv.Value.LastSeenTick <= StateExpireTicks) continue;
                if (remove == null) remove = new List<int>();
                remove.Add(kv.Key);
            }
            if (remove != null) for (int i = 0; i < remove.Count; i++) States.Remove(remove[i]);
        }

        private static void RemoveOldest()
        {
            int id = -1;
            int oldest = int.MaxValue;
            foreach (KeyValuePair<int, PawnState> kv in States)
            {
                if (kv.Value.LastSeenTick >= oldest) continue;
                oldest = kv.Value.LastSeenTick;
                id = kv.Key;
            }
            if (id >= 0) States.Remove(id);
        }

        private static Pawn PawnOf(object pather)
        {
            if (pather == null || PatherPawnField == null) return null;
            try { return PatherPawnField.GetValue(pather) as Pawn; }
            catch { return null; }
        }

        private static bool IsCandidate(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map == null || pawn.RaceProps == null) return false;
            if (pawn.Faction == Faction.OfPlayer) return false;
            float size;
            try { size = pawn.BodySize; }
            catch { return false; }
            return size >= LargeBodySize;
        }

        private static bool IsExitIntent(Pawn pawn)
        {
            Job job;
            try { job = pawn.CurJob; }
            catch { return false; }
            if (job == null || job.def == null || job.def.defName != "Goto") return false;

            bool exitFlag = false;
            if (ExitMapOnArrivalField != null)
            {
                try { exitFlag = (bool)ExitMapOnArrivalField.GetValue(job); }
                catch { }
            }

            bool edgeTarget = false;
            try
            {
                LocalTargetInfo target = job.targetA;
                if (target.IsValid)
                {
                    IntVec3 cell = target.Cell;
                    if (cell.IsValid && cell.InBounds(pawn.Map))
                        edgeTarget = DistanceToEdge(cell, pawn.Map) <= 1;
                }
            }
            catch { }

            return exitFlag || edgeTarget;
        }

        private static int DistanceToEdge(Pawn pawn)
        {
            if (pawn == null || pawn.Map == null) return int.MaxValue;
            return DistanceToEdge(pawn.Position, pawn.Map);
        }

        private static int DistanceToEdge(IntVec3 cell, Map map)
        {
            if (map == null || !cell.IsValid) return int.MaxValue;
            int dx = Math.Min(cell.x, map.Size.x - 1 - cell.x);
            int dz = Math.Min(cell.z, map.Size.z - 1 - cell.z);
            return Math.Min(dx, dz);
        }

        private static bool TryForceExit(Pawn pawn)
        {
            if (pawn == null || pawn.Destroyed || !pawn.Spawned || pawn.Map == null) return false;
            try
            {
                Rot4 dir = CellRect.WholeMap(pawn.Map).GetClosestEdge(pawn.Position);
                pawn.ExitMap(false, dir);
                return !pawn.Spawned;
            }
            catch (Exception ex)
            {
                if (forceExitFailures < 4)
                    Log.Warning("[LargePawns Pathfinding Guard] fail-safe ExitMap failed for " + pawn + ": " + ex.GetType().Name + ": " + ex.Message);
                return false;
            }
        }

        private static int CurrentTick()
        {
            try { return Find.TickManager == null ? 0 : Find.TickManager.TicksGame; }
            catch { return 0; }
        }

        internal static string Summary()
        {
            return "compat[LargePawns=" + largePawnsDetected + ",PathfindingFramework=" + pathfindingFrameworkDetected +
                "], observed=" + observed + ", slowCalls=" + slowCalls + ", failures=" + failures +
                ", storms=" + storms + ", suppressed=" + suppressed + ", forcedExits=" + forcedExits +
                ", forceExitFailures=" + forceExitFailures +
                ". Guard applies only to non-player BodySize>=2 exit Goto jobs; normal paths are unchanged.";
        }

        internal struct CallState
        {
            internal long Started;
            internal int PawnId;
            internal int Tick;
        }

        private sealed class PawnState
        {
            internal int WindowStartTick = -1;
            internal int LastSeenTick;
            internal int Score;
            internal int StormUntilTick;
            internal int NextAllowedTick;
            internal long Suppressed;
        }
    }

    [HarmonyPatch]
    internal static class StartPathGuardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower)).Where(m => m != null && m.Name == "StartPath");
        }

        private static bool Prefix(object __instance, ref PathStormGuard.CallState __state)
        {
            return PathStormGuard.Before(__instance, ref __state);
        }

        private static void Postfix(object __instance, PathStormGuard.CallState __state)
        {
            PathStormGuard.After(__instance, __state);
        }
    }

    [HarmonyPatch]
    internal static class TryEnterNextPathCellGuardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower)).Where(m => m != null && m.Name == "TryEnterNextPathCell");
        }

        private static bool Prefix(object __instance, ref PathStormGuard.CallState __state)
        {
            return PathStormGuard.Before(__instance, ref __state);
        }

        private static void Postfix(object __instance, PathStormGuard.CallState __state)
        {
            PathStormGuard.After(__instance, __state);
        }
    }

    [HarmonyPatch]
    internal static class PatherFailedGuardPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            return AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower)).Where(m => m != null && m.Name == "PatherFailed");
        }

        private static void Postfix(object __instance)
        {
            PathStormGuard.OnPatherFailed(__instance);
        }
    }
}
