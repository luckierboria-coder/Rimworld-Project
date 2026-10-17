using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace AllenVMFColonistBarContext
{
    // v1.2: filter the ACTUAL cached blank entries before vanilla/CBA arrange them,
    // rather than patch Find.Maps or VMF's optional Sensitive transpiler.
    public sealed class ContextFilterMod : Mod
    {
        private static int firstCalls;
        private static int suppressed;
        private static int errors;

        public ContextFilterMod(ModContentPack content) : base(content)
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            try
            {
                Type finder = typeof(ColonistBarDrawLocsFinder);
                MethodInfo target = AccessTools.Method(
                    finder, "CalculateDrawLocs",
                    new[] { typeof(List<Vector2>), typeof(float).MakeByRefType(), typeof(int) });
                if (target == null)
                {
                    Log.Error("[VMFContextFilter] v1.2: ColonistBarDrawLocsFinder.CalculateDrawLocs(List<Vector2>, out float, int) missing; disabled.");
                    return;
                }

                new Harmony("allen.vmf.colonistbar.contextfilter.rw16")
                    .Patch(target, prefix: new HarmonyMethod(
                        AccessTools.Method(typeof(ContextFilterMod), nameof(BeforeArrange))));
                Log.Message("[VMFContextFilter] v1.2 active: direct bar-layout prefilter; world caravan groups retained.");
            }
            catch (Exception ex)
            {
                Log.Error("[VMFContextFilter] v1.2 setup failure: " + ex);
            }
        }

        // This method runs ONCE on each bar recache, not per rendered frame.
        // The group count is adjusted before the vanilla/CBA slot calculations.
        private static void BeforeArrange(ref int groupsCount)
        {
            try
            {
                ColonistBar bar = Find.ColonistBar;
                if (bar == null) return;

                FieldInfo entriesField = AccessTools.Field(typeof(ColonistBar), "cachedEntries");
                var entries = entriesField?.GetValue(bar) as List<ColonistBar.Entry>;
                if (entries == null)
                {
                    if (errors++ < 3)
                        Log.Warning("[VMFContextFilter] v1.2: cachedEntries field unavailable.");
                    return;
                }

                if (firstCalls++ < 4)
                {
                    Log.Message("[VMFContextFilter] v1.2 LAYOUT hook invoked; entries=" +
                        entries.Count + " groupsCount=" + groupsCount);
                    foreach (var e in entries)
                    {
                        if (e.pawn != null || e.map == null) continue;
                        Log.Message("[VMFContextFilter] v1.2 empty entry group=" + e.group +
                            " map=" + e.map +
                            " parentType=" + e.map.Parent?.GetType().FullName +
                            " colonyVehicleMap=" + IsColonyVehicleMap(e.map));
                    }
                }

                List<int> removed = null;
                for (int i = entries.Count - 1; i >= 0; i--)
                {
                    var e = entries[i];
                    // NEVER remove a living pawn icon, ordinary map or world caravan entry.
                    if (e.pawn != null || e.map == null || !IsColonyVehicleMap(e.map))
                        continue;

                    if (removed == null) removed = new List<int>();
                    removed.Add(e.group);
                    entries.RemoveAt(i);
                    if (suppressed++ < 20)
                        Log.Message("[VMFContextFilter] v1.2 REMOVED blank colony vehicle group=" +
                            e.group + " map=" + e.map);
                }
                if (removed == null) return;

                // Every retained entry gets a correctly rebound group action. In RimWorld 1.6
                // Entry is a struct whose two callbacks capture its original group number.
                int lastOldGroup = int.MinValue;
                int nextGroup = -1;
                for (int i = 0; i < entries.Count; i++)
                {
                    ColonistBar.Entry e = entries[i];
                    if (e.group != lastOldGroup)
                    {
                        lastOldGroup = e.group;
                        nextGroup++;
                    }
                    if (e.group != nextGroup)
                        entries[i] = new ColonistBar.Entry(e.pawn, e.map, nextGroup);
                }
                groupsCount = nextGroup + 1;

                // Match vanilla's cachedReorderableGroups one-to-one with filtered entries.
                FieldInfo reorderField =
                    AccessTools.Field(typeof(ColonistBar), "cachedReorderableGroups");
                var reorder = reorderField?.GetValue(bar) as List<int>;
                if (reorder != null)
                {
                    reorder.Clear();
                    for (int i = 0; i < entries.Count; i++) reorder.Add(-1);
                }

                bar.drawer.Notify_RecachedEntries();
                Log.Message("[VMFContextFilter] v1.2 recached: entries=" +
                    entries.Count + " groups=" + groupsCount +
                    " suppressed=" + removed.Count);
            }
            catch (Exception ex)
            {
                if (errors++ < 5)
                    Log.Error("[VMFContextFilter] v1.2 bar prefiler failure: " + ex);
            }
        }

        // Read only: determine whether this is the interior map of a vehicle that
        // is physically spawned on a player home colony map.
        private static bool IsColonyVehicleMap(Map map)
        {
            if (map == null) return false;
            Map current = map;
            bool sawVehicle = false;
            for (int depth = 0; depth < 12; depth++)
            {
                object parent = current.Parent;
                if (parent == null ||
                    !IsVehicleMapParent(parent.GetType()))
                    return sawVehicle && current.IsPlayerHome;

                sawVehicle = true;
                FieldInfo field = AccessTools.Field(parent.GetType(), "vehicle");
                var pawn = field?.GetValue(parent) as Pawn;
                if (pawn == null || !pawn.Spawned || pawn.Map == null)
                    return false; // World-map caravan or despawned vehicle: never hide.
                current = pawn.Map;
            }
            return false;
        }

        private static bool IsVehicleMapParent(Type parentType)
        {
            while (parentType != null)
            {
                if (parentType.FullName == "VehicleMapFramework.MapParent_Vehicle")
                    return true;
                parentType = parentType.BaseType;
            }
            return false;
        }
    }

    // In large mod setups the bar may have been cached before our UI hook
    // first gets control. Force one inexpensive recache after map load.
    public sealed class ContextBarRecacheOnLoad : GameComponent
    {
        private bool requested;
        public ContextBarRecacheOnLoad(Game game) { }

        public override void GameComponentTick()
        {
            if (requested || Find.TickManager == null ||
                Find.TickManager.TicksGame % 60 != 0 ||
                Find.Maps == null || Find.Maps.Count == 0 ||
                Find.ColonistBar == null)
                return;

            requested = true;
            Find.ColonistBar.MarkColonistsDirty();
            Log.Message("[VMFContextFilter] v1.2 requested initial colonist-bar recache after map load.");
        }
    }
}
