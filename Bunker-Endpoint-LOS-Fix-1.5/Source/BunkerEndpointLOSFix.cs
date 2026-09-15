using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace BunkerEndpointLOSFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony("allen.ra2bunker.walllos.v5");

                MethodInfo canSeeOver = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.CanBeSeenOver),
                    new[] { typeof(IntVec3), typeof(Map) });
                MethodInfo canSeeOverFast = AccessTools.Method(typeof(GenGrid), nameof(GenGrid.CanBeSeenOverFast),
                    new[] { typeof(IntVec3), typeof(Map) });

                MethodInfo losStandard = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(bool),
                    typeof(Func<IntVec3, bool>), typeof(int), typeof(int)
                });

                MethodInfo losRect = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSight), new[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map), typeof(CellRect), typeof(CellRect),
                    typeof(Func<IntVec3, bool>), typeof(bool)
                });

                MethodInfo losToThing = AccessTools.Method(typeof(GenSight), nameof(GenSight.LineOfSightToThing), new[]
                {
                    typeof(IntVec3), typeof(Thing), typeof(Map), typeof(bool), typeof(Func<IntVec3, bool>)
                });

                MethodInfo cellCanSeeCell = AccessTools.Method(typeof(ShootLeanUtility), nameof(ShootLeanUtility.CellCanSeeCell), new[]
                {
                    typeof(IntVec3), typeof(IntVec3), typeof(Map)
                });

                MethodInfo attackCanSee = AccessTools.Method(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee), new[]
                {
                    typeof(Thing), typeof(Thing), typeof(Func<IntVec3, bool>)
                });

                MethodInfo tryFindShootLine = AccessTools.Method(typeof(Verb), nameof(Verb.TryFindShootLineFromTo), new[]
                {
                    typeof(IntVec3), typeof(LocalTargetInfo), typeof(ShootLine).MakeByRefType(), typeof(bool)
                });

                if (canSeeOver == null || canSeeOverFast == null || losStandard == null || losRect == null ||
                    losToThing == null || cellCanSeeCell == null || attackCanSee == null || tryFindShootLine == null)
                {
                    Log.Error("[Ra2Bunker Endpoint LOS Fix V5] One or more RimWorld 1.5 LOS method lookups failed; no patches were installed.");
                    return;
                }

                HarmonyMethod cellPrefix = new HarmonyMethod(AccessTools.Method(typeof(CanBeSeenOverPatch), nameof(CanBeSeenOverPatch.Prefix)));
                harmony.Patch(canSeeOver, prefix: cellPrefix);
                harmony.Patch(canSeeOverFast, prefix: cellPrefix);

                HarmonyMethod losPrefix = new HarmonyMethod(AccessTools.Method(typeof(LineScopePatch), nameof(LineScopePatch.Prefix)));
                HarmonyMethod losPostfix = new HarmonyMethod(AccessTools.Method(typeof(LineScopePatch), nameof(LineScopePatch.Postfix)));
                harmony.Patch(losStandard, prefix: losPrefix, postfix: losPostfix);

                HarmonyMethod rectPrefix = new HarmonyMethod(AccessTools.Method(typeof(RectLineScopePatch), nameof(RectLineScopePatch.Prefix)));
                HarmonyMethod rectPostfix = new HarmonyMethod(AccessTools.Method(typeof(RectLineScopePatch), nameof(RectLineScopePatch.Postfix)));
                harmony.Patch(losRect, prefix: rectPrefix, postfix: rectPostfix);

                HarmonyMethod thingPrefix = new HarmonyMethod(AccessTools.Method(typeof(LineToThingScopePatch), nameof(LineToThingScopePatch.Prefix)));
                HarmonyMethod thingPostfix = new HarmonyMethod(AccessTools.Method(typeof(LineToThingScopePatch), nameof(LineToThingScopePatch.Postfix)));
                harmony.Patch(losToThing, prefix: thingPrefix, postfix: thingPostfix);

                HarmonyMethod cellSeePrefix = new HarmonyMethod(AccessTools.Method(typeof(CellCanSeeCellScopePatch), nameof(CellCanSeeCellScopePatch.Prefix)));
                HarmonyMethod cellSeePostfix = new HarmonyMethod(AccessTools.Method(typeof(CellCanSeeCellScopePatch), nameof(CellCanSeeCellScopePatch.Postfix)));
                harmony.Patch(cellCanSeeCell, prefix: cellSeePrefix, postfix: cellSeePostfix);

                HarmonyMethod attackPrefix = new HarmonyMethod(AccessTools.Method(typeof(AttackCanSeeScopePatch), nameof(AttackCanSeeScopePatch.Prefix)));
                HarmonyMethod attackPostfix = new HarmonyMethod(AccessTools.Method(typeof(AttackCanSeeScopePatch), nameof(AttackCanSeeScopePatch.Postfix)));
                harmony.Patch(attackCanSee, prefix: attackPrefix, postfix: attackPostfix);

                HarmonyMethod verbPrefix = new HarmonyMethod(AccessTools.Method(typeof(TryFindShootLineScopePatch), nameof(TryFindShootLineScopePatch.Prefix)));
                HarmonyMethod verbPostfix = new HarmonyMethod(AccessTools.Method(typeof(TryFindShootLineScopePatch), nameof(TryFindShootLineScopePatch.Postfix)));
                harmony.Patch(tryFindShootLine, prefix: verbPrefix, postfix: verbPostfix);

                Log.Message("[Ra2Bunker Endpoint LOS Fix V5] Active. Ra2_Bunker uses original partial fill, is treated as opaque by default, and only the specific bunker acting as LOS source/target is temporarily exempted.");
            }
            catch (Exception ex)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5] Failed to install LOS patches: " + ex);
            }
        }
    }

    internal static class BunkerLosContext
    {
        private const string BunkerDefName = "Ra2_Bunker";

        [ThreadStatic]
        private static List<Building> exemptBunkers;

        internal struct ScopeState
        {
            public int Marker;
            public bool Active;
        }

        internal static Building FindBunkerAt(IntVec3 cell, Map map)
        {
            if (map == null || !cell.InBounds(map))
                return null;

            Building edifice = cell.GetEdifice(map);
            return IsBunker(edifice) ? edifice : null;
        }

        internal static bool IsBunker(Building building)
        {
            return building != null && building.def != null && building.def.defName == BunkerDefName;
        }

        internal static bool IsExempt(Building bunker)
        {
            if (bunker == null || exemptBunkers == null)
                return false;

            for (int i = 0; i < exemptBunkers.Count; i++)
            {
                if (ReferenceEquals(exemptBunkers[i], bunker))
                    return true;
            }

            return false;
        }

        internal static ScopeState Push(Map map, IntVec3 a, IntVec3 b)
        {
            if (map == null)
                return default(ScopeState);

            Building first = FindBunkerAt(a, map);
            Building second = FindBunkerAt(b, map);
            if (first == null && second == null)
                return default(ScopeState);

            if (exemptBunkers == null)
                exemptBunkers = new List<Building>(4);

            ScopeState state = new ScopeState
            {
                Marker = exemptBunkers.Count,
                Active = true
            };

            AddUnique(first);
            AddUnique(second);
            return state;
        }

        internal static ScopeState Push(Thing a, Thing b)
        {
            Map map = a?.Map ?? b?.Map;
            if (map == null)
                return default(ScopeState);

            IntVec3 aCell = a != null ? a.Position : IntVec3.Invalid;
            IntVec3 bCell = b != null ? b.Position : IntVec3.Invalid;
            if (!aCell.IsValid) aCell = bCell;
            if (!bCell.IsValid) bCell = aCell;
            return Push(map, aCell, bCell);
        }

        private static void AddUnique(Building bunker)
        {
            if (bunker == null)
                return;

            for (int i = 0; i < exemptBunkers.Count; i++)
            {
                if (ReferenceEquals(exemptBunkers[i], bunker))
                    return;
            }

            exemptBunkers.Add(bunker);
        }

        internal static void Pop(ScopeState state)
        {
            if (!state.Active || exemptBunkers == null)
                return;

            if (state.Marker < 0 || state.Marker > exemptBunkers.Count)
            {
                exemptBunkers.Clear();
                return;
            }

            int remove = exemptBunkers.Count - state.Marker;
            if (remove > 0)
                exemptBunkers.RemoveRange(state.Marker, remove);
        }
    }

    internal static class CanBeSeenOverPatch
    {
        public static bool Prefix(IntVec3 c, Map map, ref bool __result)
        {
            Building bunker = BunkerLosContext.FindBunkerAt(c, map);
            if (bunker == null)
                return true;

            __result = BunkerLosContext.IsExempt(bunker);
            return false;
        }
    }

    internal static class LineScopePatch
    {
        public static void Prefix(IntVec3 start, IntVec3 end, Map map, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(map, start, end);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }

    internal static class RectLineScopePatch
    {
        public static void Prefix(IntVec3 start, IntVec3 end, Map map, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(map, start, end);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }

    internal static class LineToThingScopePatch
    {
        public static void Prefix(IntVec3 start, Thing t, Map map, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(map, start, t != null ? t.Position : start);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }

    internal static class CellCanSeeCellScopePatch
    {
        public static void Prefix(IntVec3 source, IntVec3 dest, Map map, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(map, source, dest);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }

    internal static class AttackCanSeeScopePatch
    {
        public static void Prefix(Thing seer, Thing target, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(seer, target);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }

    internal static class TryFindShootLineScopePatch
    {
        public static void Prefix(Verb __instance, IntVec3 root, LocalTargetInfo targ, out BunkerLosContext.ScopeState __state)
        {
            Map map = __instance?.caster?.Map;
            if (map == null)
            {
                __state = default(BunkerLosContext.ScopeState);
                return;
            }

            IntVec3 targetCell = targ.IsValid ? targ.Cell : root;
            __state = BunkerLosContext.Push(map, root, targetCell);
        }

        public static void Postfix(BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
        }
    }
}
