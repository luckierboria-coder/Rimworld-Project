using System;
using System.Reflection;
using HarmonyLib;
using Verse;
using Verse.AI;

namespace RimMT
{
    internal static class TailPathfinderPatches093T3
    {
        internal static void Apply(Harmony harmony)
        {
            if (harmony == null) return;
            int patched = 0;
            try
            {
                MethodInfo[] methods = typeof(PathFinder).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                for (int i = 0; i < methods.Length; i++)
                {
                    MethodInfo method = methods[i];
                    if (method == null || method.Name != "FindPath") continue;
                    HarmonyMethod prefix = new HarmonyMethod(typeof(TailPathfinderPatches093T3), nameof(Prefix)) { priority = Priority.First };
                    HarmonyMethod postfix = new HarmonyMethod(typeof(TailPathfinderPatches093T3), nameof(Postfix)) { priority = Priority.Last };
                    harmony.Patch(method, prefix: prefix, postfix: postfix);
                    patched++;
                }
                Log.Message("[RimMT] T3 bounded PathFinder attribution installed on " + patched + " FindPath overload(s); Stopwatch is active only inside T2 deep windows.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimMT] T3 PathFinder attribution failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static void Prefix(ref long __state)
        {
            __state = TailPawnAttribution093T2.DeepActive ? TailPathfinderAttribution093T3.BeginCall() : 0L;
        }

        public static void Postfix(long __state)
        {
            if (__state != 0L) TailPathfinderAttribution093T3.EndCall(__state);
        }
    }
}
