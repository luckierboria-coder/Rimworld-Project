using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace BunkerEndpointLOSFix15
{
    [StaticConstructorOnStartup]
    public static class Bootstrap
    {
        private const BindingFlags AllMethods = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance;

        static Bootstrap()
        {
            Harmony harmony = new Harmony("allen.ra2bunker.walllos.v5.1");
            int installed = 0;
            int missing = 0;

            // Core opacity hook. Vanilla IntVec3.CanBeSeenOver/Fast both delegate to
            // GenGrid.CanBeSeenOver(Building), so this is the narrowest authoritative
            // place to make only Ra2_Bunker opaque without turning it into Full fill.
            MethodInfo buildingCanBeSeenOver = FindMethod(typeof(GenGrid), nameof(GenGrid.CanBeSeenOver), p =>
                p.Length == 1 && p[0].ParameterType == typeof(Building));

            if (TryPatch(harmony, buildingCanBeSeenOver,
                    prefix: AccessTools.Method(typeof(BuildingCanBeSeenOverPatch), nameof(BuildingCanBeSeenOverPatch.Prefix)),
                    label: "GenGrid.CanBeSeenOver(Building)"))
                installed++;
            else
                missing++;

            // Ra2Bunker inherits Building_TurretGun and does not override TryFindNewTarget.
            // Keep this bunker exempt only while its own target-search call is on stack.
            MethodInfo turretFindTarget = FindMethod(typeof(Building_TurretGun), nameof(Building_TurretGun.TryFindNewTarget), p => p.Length == 0);
            if (TryPatch(harmony, turretFindTarget,
                    prefix: AccessTools.Method(typeof(BunkerTurretTargetScopePatch), nameof(BunkerTurretTargetScopePatch.Prefix)),
                    finalizer: AccessTools.Method(typeof(BunkerTurretTargetScopePatch), nameof(BunkerTurretTargetScopePatch.Finalizer)),
                    label: "Building_TurretGun.TryFindNewTarget"))
                installed++;
            else
                missing++;

            // AttackTargetFinder.CanSee is used by target acquisition. This also permits
            // outside attackers to see the bunker itself, while never exempting a bunker
            // merely because it lies between two unrelated external Things.
            MethodInfo attackCanSee = FindMethod(typeof(AttackTargetFinder), nameof(AttackTargetFinder.CanSee), p =>
                p.Length >= 2 && p[0].ParameterType == typeof(Thing) && p[1].ParameterType == typeof(Thing));
            if (TryPatch(harmony, attackCanSee,
                    prefix: AccessTools.Method(typeof(AttackCanSeeScopePatch), nameof(AttackCanSeeScopePatch.Prefix)),
                    finalizer: AccessTools.Method(typeof(AttackCanSeeScopePatch), nameof(AttackCanSeeScopePatch.Finalizer)),
                    label: "AttackTargetFinder.CanSee"))
                installed++;
            else
                missing++;

            // Patch every 1.5 TryStartCastOn overload whose first argument is LocalTargetInfo.
            // Ra2Bunker's Verb_Bunker temporarily assigns the bunker as caster, then calls
            // each contained pawn weapon verb's TryStartCastOn(currentTarget).
            installed += PatchVerbTargetMethods(harmony, nameof(Verb.TryStartCastOn),
                p => p.Length >= 1 && p[0].ParameterType == typeof(LocalTargetInfo),
                typeof(VerbTargetScopePatch), ref missing);

            // CanHitTarget is often evaluated before a cast begins (UI and AI validation).
            installed += PatchVerbTargetMethods(harmony, nameof(Verb.CanHitTarget),
                p => p.Length >= 1 && p[0].ParameterType == typeof(LocalTargetInfo),
                typeof(VerbTargetScopePatch), ref missing);

            // Some callers validate directly from an explicit root cell.
            installed += PatchVerbTargetMethods(harmony, nameof(Verb.CanHitTargetFrom),
                p => p.Length >= 2 && p[0].ParameterType == typeof(IntVec3) && p[1].ParameterType == typeof(LocalTargetInfo),
                typeof(VerbTargetFromScopePatch), ref missing);

            if (buildingCanBeSeenOver == null)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5.1] CRITICAL: core Building CanBeSeenOver hook was not found. The patch is inactive.");
                return;
            }

            Log.Message("[Ra2Bunker Endpoint LOS Fix V5.1] Active. Installed hooks=" + installed +
                        ", missing optional hooks=" + missing +
                        ". Ra2_Bunker is opaque by default; exemption is limited to its own turret targeting or an actual cast/target endpoint.");
        }

        private static int PatchVerbTargetMethods(Harmony harmony, string methodName,
            Func<ParameterInfo[], bool> predicate, Type patchType, ref int missing)
        {
            List<MethodInfo> methods = FindMethods(typeof(Verb), methodName, predicate);
            if (methods.Count == 0)
            {
                Log.Warning("[Ra2Bunker Endpoint LOS Fix V5.1] Optional hook not found: Verb." + methodName);
                missing++;
                return 0;
            }

            MethodInfo prefix = AccessTools.Method(patchType, "Prefix");
            MethodInfo finalizer = AccessTools.Method(patchType, "Finalizer");
            int installed = 0;
            for (int i = 0; i < methods.Count; i++)
            {
                if (TryPatch(harmony, methods[i], prefix, finalizer: finalizer,
                        label: "Verb." + methodName + " overload " + i))
                    installed++;
                else
                    missing++;
            }

            return installed;
        }

        private static MethodInfo FindMethod(Type type, string name, Func<ParameterInfo[], bool> predicate)
        {
            List<MethodInfo> methods = FindMethods(type, name, predicate);
            return methods.Count > 0 ? methods[0] : null;
        }

        private static List<MethodInfo> FindMethods(Type type, string name, Func<ParameterInfo[], bool> predicate)
        {
            List<MethodInfo> result = new List<MethodInfo>();
            MethodInfo[] methods = type.GetMethods(AllMethods);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name != name)
                    continue;

                ParameterInfo[] parameters = method.GetParameters();
                if (predicate(parameters))
                    result.Add(method);
            }

            return result;
        }

        private static bool TryPatch(Harmony harmony, MethodInfo original, MethodInfo prefix = null,
            MethodInfo postfix = null, MethodInfo finalizer = null, string label = null)
        {
            if (original == null)
            {
                Log.Warning("[Ra2Bunker Endpoint LOS Fix V5.1] Hook not found: " + (label ?? "unknown"));
                return false;
            }

            try
            {
                harmony.Patch(original,
                    prefix: prefix != null ? new HarmonyMethod(prefix) : null,
                    postfix: postfix != null ? new HarmonyMethod(postfix) : null,
                    finalizer: finalizer != null ? new HarmonyMethod(finalizer) : null);
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("[Ra2Bunker Endpoint LOS Fix V5.1] Failed to patch " + (label ?? original.Name) + ": " + ex);
                return false;
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

        internal static ScopeState Push(Thing firstThing, Thing secondThing)
        {
            Building first = firstThing as Building;
            Building second = secondThing as Building;
            if (!IsBunker(first)) first = null;
            if (!IsBunker(second)) second = null;

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

        internal static Thing TargetThing(LocalTargetInfo target)
        {
            return target.HasThing ? target.Thing : null;
        }
    }

    internal static class BuildingCanBeSeenOverPatch
    {
        public static bool Prefix(Building b, ref bool __result)
        {
            if (!BunkerLosContext.IsBunker(b))
                return true;

            __result = BunkerLosContext.IsExempt(b);
            return false;
        }
    }

    internal static class BunkerTurretTargetScopePatch
    {
        public static void Prefix(Building_TurretGun __instance, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(__instance, null);
        }

        public static Exception Finalizer(Exception __exception, BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
            return __exception;
        }
    }

    internal static class AttackCanSeeScopePatch
    {
        public static void Prefix(Thing __0, Thing __1, out BunkerLosContext.ScopeState __state)
        {
            __state = BunkerLosContext.Push(__0, __1);
        }

        public static Exception Finalizer(Exception __exception, BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
            return __exception;
        }
    }

    internal static class VerbTargetScopePatch
    {
        public static void Prefix(Verb __instance, LocalTargetInfo __0, out BunkerLosContext.ScopeState __state)
        {
            Thing caster = __instance != null ? __instance.caster : null;
            __state = BunkerLosContext.Push(caster, BunkerLosContext.TargetThing(__0));
        }

        public static Exception Finalizer(Exception __exception, BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
            return __exception;
        }
    }

    internal static class VerbTargetFromScopePatch
    {
        public static void Prefix(Verb __instance, IntVec3 __0, LocalTargetInfo __1, out BunkerLosContext.ScopeState __state)
        {
            Thing caster = __instance != null ? __instance.caster : null;
            __state = BunkerLosContext.Push(caster, BunkerLosContext.TargetThing(__1));
        }

        public static Exception Finalizer(Exception __exception, BunkerLosContext.ScopeState __state)
        {
            BunkerLosContext.Pop(__state);
            return __exception;
        }
    }
}
