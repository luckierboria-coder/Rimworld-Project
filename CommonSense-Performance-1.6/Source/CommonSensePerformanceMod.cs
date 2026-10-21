using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace Allen.CommonSensePerformance
{
    public sealed class CommonSensePerformanceMod : Mod
    {
        private const string Id = "allen.commonsense.performance16";
        public CommonSensePerformanceMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Apply);
        }

        private static void Apply()
        {
            try
            {
                Type utility = AccessTools.TypeByName("CommonSense.Utility");
                if (utility == null)
                {
                    Log.Warning("[CS Perf] Common Sense not present; patch skipped.");
                    return;
                }
                MethodInfo target = AccessTools.Method(utility, "SelectAllFilth",
                    new[] { typeof(Pawn), typeof(LocalTargetInfo), typeof(int) });
                if (target == null || !typeof(IEnumerable<Filth>).IsAssignableFrom(target.ReturnType))
                {
                    Log.Warning("[CS Perf] Signature mismatch; patch skipped.");
                    return;
                }
                Patches info = Harmony.GetPatchInfo(target);
                if (info != null && (info.Prefixes.Count > 0 || info.Postfixes.Count > 0 ||
                                     info.Transpilers.Count > 0 || info.Finalizers.Count > 0))
                {
                    Log.Warning("[CS Perf] Another mod patches SelectAllFilth; patch skipped.");
                    return;
                }
                HarmonyMethod prefix = new HarmonyMethod(
                    AccessTools.Method(typeof(FilthSelector), nameof(FilthSelector.Prefix)));
                new Harmony(Id).Patch(target, prefix: prefix);
                Log.Message("[CS Perf] Optimized filth selection enabled.");
            }
            catch (Exception ex)
            {
                Log.Error("[CS Perf] Initialization failed; original logic untouched. " + ex);
            }
            finally
            {
                // M2 is independently optional. An M1 API mismatch must not
                // suppress an otherwise safe M2 patch.
                PathPruning.TryInstall();
            }
        }
    }

    // Standalone single-pass equivalent of Common Sense 1.6 Utility.SelectAllFilth.
    // No throttling, cache of jobs, threads, or changes to worker eligibility rules.
    public static class FilthSelector
    {
        private static WorkGiverDef cleanFilth;
        private static bool disabled;

        public static bool Prefix(Pawn pawn, LocalTargetInfo target, int Limit,
                                  ref IEnumerable<Filth> __result)
        {
            if (disabled) return true;
            try
            {
                __result = Select(pawn, target, Limit);
                return false;
            }
            catch (Exception ex)
            {
                disabled = true;
                Log.Warning("[CS Perf] Selector fault. Falling back to Common Sense for this session. " + ex);
                return true;
            }
        }

        private static IEnumerable<Filth> Select(Pawn pawn, LocalTargetInfo target, int limit)
        {
            Map map = pawn.Map;
            Room room;
            if (target.Thing == null)
            {
                room = GridsUtility.GetRoom(target.Cell, map);
                if (room == null)
                    Log.Error("Invalid target: cell or thing it must be");
            }
            else
            {
                room = target.Thing.GetRoom();
            }
            if (room == null || map.pathing.For(pawn).pathGrid == null)
                return new List<Filth>(0);

            if (cleanFilth == null)
                cleanFilth = DefDatabase<WorkGiverDef>.GetNamed("CleanFilth");
            if (cleanFilth.Worker == null)
                return new List<Filth>(0);

            WorkGiver_Scanner scanner = (WorkGiver_Scanner)cleanFilth.Worker;
            List<Filth> result = new List<Filth>(limit > 0 && limit < 32 ? limit : 16);
            if (limit < 0) return result;

            if (room.IsHuge || room.CellCount > 160)
            {
                // Keep upstream's per-cell Take(limit) and global break semantics.
                for (int i = 0; i < 200; ++i)
                {
                    IntVec3 cell = target.Cell + GenRadial.RadialPattern[i];
                    if (cell.InBounds(map) && cell.InAllowedArea(pawn) &&
                        (cell.GetRoom(map) == room || cell.GetDoor(map) != null))
                    {
                        int countInCell = 0;
                        List<Thing> things = cell.GetThingList(map);
                        for (int j = 0; j < things.Count; ++j)
                        {
                            Filth filth = things[j] as Filth;
                            if (filth == null || filth.Destroyed ||
                                !scanner.HasJobOnThing(pawn, filth)) continue;
                            result.Add(filth);
                            if (limit > 0 && ++countInCell >= limit) break;
                        }
                    }
                    if (limit > 0 && result.Count >= limit) break;
                }
            }
            else
            {
                foreach (Thing thing in room.ContainedAndAdjacentThings)
                {
                    Filth filth = thing as Filth;
                    if (filth == null || filth.Destroyed ||
                        !filth.Position.InAllowedArea(pawn) ||
                        !scanner.HasJobOnThing(pawn, filth)) continue;
                    Room foundRoom = filth.GetRoom();
                    if (foundRoom == null ||
                        (foundRoom != room && !foundRoom.IsDoorway)) continue;
                    result.Add(filth);
                    if (limit > 0 && result.Count >= limit) break;
                }
            }
            return result;
        }
    }
}