using HarmonyLib;
using Verse;
using Verse.AI;
using RimWorld;
using System;
using System.Collections.Generic;
using System.Reflection.Emit;

namespace DoorClearance15Fixed
{
    [StaticConstructorOnStartup]
    public static class Setup
    {
        static Setup()
        {
            new Harmony("local.doorclearance15.fixed").PatchAll();
            List<ThingDef> list = DefDatabase<ThingDef>.AllDefsListForReading;
            Type buildingDoor = typeof(Building_Door);
            int nullThingClassCount = 0;
            for (int i = list.Count; i-- > 0; )
            {
                ThingDef def = list[i];
                if (def == null) continue;
                Type thingClass = def.thingClass;
                if (thingClass == null) { nullThingClassCount++; continue; }
                if (thingClass == buildingDoor || thingClass.IsSubclassOf(buildingDoor) || thingClass.Name.IndexOf("DoorsExpanded", StringComparison.Ordinal) >= 0)
                    HarmonyPatches.Doors.Add(def.shortHash);
            }
            Log.Message("[Door Clearance 1.5 Fixed] Active. Cached " + HarmonyPatches.Doors.Count + " door defs; safely skipped " + nullThingClassCount + " ThingDefs with null thingClass.");
        }
    }

    public static class HarmonyPatches
    {
        public static readonly HashSet<ushort> Doors = new HashSet<ushort>();

        [HarmonyPatch(typeof(HaulAIUtility), "HaulablePlaceValidator")]
        private static class Patch_HaulAIUtility_HaulablePlaceValidator
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                var getEdifice = AccessTools.Method(typeof(GridsUtility), "GetEdifice");
                var validate = AccessTools.Method(typeof(Patch_HaulAIUtility_HaulablePlaceValidator), "Validate");
                var editor = new CodeMatcher(instructions);
                editor.Start().MatchEndForward(new CodeMatch(OpCodes.Call, getEdifice), new CodeMatch(OpCodes.Stloc_0), new CodeMatch(OpCodes.Ldloc_0));
                if (!editor.IsInvalid)
                {
                    return editor.Advance(1).InsertAndAdvance(new CodeInstruction(OpCodes.Call, validate)).Advance(1).RemoveInstructions(3).InstructionEnumeration();
                }
                Log.Error("[Door Clearance 1.5 Fixed] HaulablePlaceValidator transpiler could not find its target; leaving vanilla method unchanged.");
                return editor.InstructionEnumeration();
            }

            public static bool Validate(Building edifice)
            {
                if (edifice == null) return false;
                return Doors.Contains(edifice.def.shortHash) || edifice is Building_Trap;
            }
        }

        [HarmonyPatch(typeof(GenPlace), "PlaceSpotQualityAt")]
        private static class Patch_GenPlace_PlaceSpotQualityAt
        {
            private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
            {
                return instructions.MethodReplacer(AccessTools.Method(typeof(GenGrid), "Walkable"), AccessTools.Method(typeof(Patch_GenPlace_PlaceSpotQualityAt), "Validate"));
            }

            public static bool Validate(IntVec3 cell, Map map)
            {
                if (map == null || !cell.InBounds(map)) return false;
                int index = map.cellIndices.CellToIndex(cell);
                if (!map.pathing.Normal.pathGrid.WalkableFast(index) || !map.pathing.FenceBlocked.pathGrid.WalkableFast(index)) return false;
                List<Thing> things = map.thingGrid.ThingsListAtFast(index);
                for (int i = things.Count; i-- > 0; )
                {
                    Thing thing = things[i];
                    if (thing != null && thing.def != null && Doors.Contains(thing.def.shortHash)) return false;
                }
                return true;
            }
        }
    }
}
