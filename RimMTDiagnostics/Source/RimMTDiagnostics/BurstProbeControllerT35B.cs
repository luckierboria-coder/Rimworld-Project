using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using RimWorld.Planet;
using Verse;
using Verse.AI;

namespace RimMT.Diagnostics
{
    /// <summary>
    /// T35-B low-overhead diagnostics controller.
    /// Only DoSingleTick, DetermineNextJob and Root_Play stay permanently patched.
    /// High-frequency probes are installed only after a >=100ms game tick, remain
    /// armed for six ticks, then are removed. A 120-tick cooldown bounds duty cycle.
    /// </summary>
    internal static class BurstProbeControllerT35B
    {
        private const long TriggerUs = 100000L;
        private const int BurstTicks = 6;
        private const int CooldownTicks = 120;

        private static readonly List<MethodBase> HotTargets = new List<MethodBase>();
        private static Harmony harmony;
        private static bool initialized;
        private static bool hotInstalled;
        private static bool deepActive;
        private static int burstRemaining;
        private static int cooldownRemaining;
        private static long triggers;
        private static long installs;
        private static long removes;
        private static long installFailures;
        private static long hotTicks;
        [ThreadStatic] private static long tickStarted;

        internal static bool DeepActive { get { return deepActive; } }

        internal static void Initialize(Harmony h)
        {
            if (initialized || h == null) return;
            harmony = h;
            initialized = true;
        }

        internal static void OnTickBegin()
        {
            tickStarted = Stopwatch.GetTimestamp();
            deepActive = hotInstalled && burstRemaining > 0;
            if (deepActive) hotTicks++;
        }

        internal static void OnTickEnd()
        {
            long started = tickStarted;
            tickStarted = 0L;
            long dt = started == 0L ? 0L : Stopwatch.GetTimestamp() - started;
            long us = dt <= 0L ? 0L : dt * 1000000L / Stopwatch.Frequency;

            if (deepActive && burstRemaining > 0)
            {
                burstRemaining--;
                if (burstRemaining <= 0)
                {
                    RemoveHotProbes();
                    deepActive = false;
                }
            }

            if (cooldownRemaining > 0) cooldownRemaining--;

            if (us < TriggerUs || cooldownRemaining > 0) return;

            triggers++;
            cooldownRemaining = CooldownTicks;
            burstRemaining = BurstTicks;
            if (!hotInstalled) InstallHotProbes();
        }

        private static void InstallHotProbes()
        {
            if (hotInstalled || harmony == null) return;
            HotTargets.Clear();

            Patch(AccessTools.Method(typeof(Pawn), "Tick"),
                nameof(DiagnosticsPatches.PawnPrefix), nameof(DiagnosticsPatches.PawnPostfix), typeof(DiagnosticsPatches));
            Patch(AccessTools.Method(typeof(Pawn_JobTracker), "JobTrackerTick"),
                nameof(DiagnosticsPatches.JobTrackerPrefix), nameof(DiagnosticsPatches.JobTrackerPostfix), typeof(DiagnosticsPatches));
            Patch(AccessTools.Method(typeof(Pawn_PathFollower), "PatherTick"),
                nameof(DiagnosticsPatches.PatherPrefix), nameof(DiagnosticsPatches.PatherPostfix), typeof(DiagnosticsPatches));
            Patch(AccessTools.Method(typeof(Map), "MapPostTick"),
                nameof(DiagnosticsPatches.MapPostPrefix), nameof(DiagnosticsPatches.MapPostPostfix), typeof(DiagnosticsPatches));
            Patch(AccessTools.Method(typeof(World), "WorldTick"),
                nameof(DiagnosticsPatches.WorldPrefix), nameof(DiagnosticsPatches.WorldPostfix), typeof(DiagnosticsPatches));
            Patch(AccessTools.Method(typeof(Storyteller), "StorytellerTick"),
                nameof(DiagnosticsPatches.StorytellerPrefix), nameof(DiagnosticsPatches.StorytellerPostfix), typeof(DiagnosticsPatches));

            PatchPatherChildren();

            if (RimMTDiagnosticsSettings.EnableSearchTiming)
            {
                PatchNamed(typeof(GenClosest), "ClosestThingReachable",
                    nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix), typeof(DiagnosticsPatches));
                PatchNamed(typeof(GenClosest), "ClosestThing_Global",
                    nameof(DiagnosticsPatches.GenClosestPrefix), nameof(DiagnosticsPatches.GenClosestPostfix), typeof(DiagnosticsPatches));
                PatchNamed(typeof(Reachability), "CanReach",
                    nameof(DiagnosticsPatches.ReachPrefix), nameof(DiagnosticsPatches.ReachPostfix), typeof(DiagnosticsPatches));
            }

            hotInstalled = HotTargets.Count > 0;
            if (hotInstalled) installs++;
        }

        private static void PatchPatherChildren()
        {
            string[] names = new string[] { "TryEnterNextPathCell", "StartPath", "PatherFailed" };
            List<MethodInfo> methods;
            try { methods = AccessTools.GetDeclaredMethods(typeof(Pawn_PathFollower)); }
            catch { installFailures++; return; }

            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo m = methods[i];
                if (m == null || Array.IndexOf(names, m.Name) < 0) continue;
                Patch(m, nameof(DiagnosticsV02.PatherChildPrefix), nameof(DiagnosticsV02.PatherChildPostfix), typeof(DiagnosticsV02));
            }
        }

        private static void PatchNamed(Type type, string name, string prefix, string postfix, Type patchType)
        {
            List<MethodInfo> methods;
            try { methods = AccessTools.GetDeclaredMethods(type); }
            catch { installFailures++; return; }
            for (int i = 0; i < methods.Count; i++)
            {
                MethodInfo m = methods[i];
                if (m != null && m.Name == name) Patch(m, prefix, postfix, patchType);
            }
        }

        private static void Patch(MethodBase target, string prefix, string postfix, Type patchType)
        {
            if (target == null) { installFailures++; return; }
            try
            {
                harmony.Patch(target,
                    prefix: new HarmonyMethod(patchType, prefix) { priority = Priority.First },
                    postfix: new HarmonyMethod(patchType, postfix) { priority = Priority.Last });
                HotTargets.Add(target);
            }
            catch { installFailures++; }
        }

        private static void RemoveHotProbes()
        {
            if (!hotInstalled || harmony == null) return;
            for (int i = 0; i < HotTargets.Count; i++)
            {
                MethodBase target = HotTargets[i];
                if (target == null) continue;
                try
                {
                    harmony.Unpatch(target, HarmonyPatchType.Prefix, DiagnosticsBootstrap.HarmonyId);
                    harmony.Unpatch(target, HarmonyPatchType.Postfix, DiagnosticsBootstrap.HarmonyId);
                }
                catch { installFailures++; }
            }
            HotTargets.Clear();
            hotInstalled = false;
            removes++;
        }

        internal static string Summary()
        {
            return "T35-B burst probes: initialized=" + initialized +
                ", hotInstalled=" + hotInstalled +
                ", deepActive=" + deepActive +
                ", triggerMs=100, burstTicks=6, cooldownTicks=120" +
                ", burstRemaining=" + burstRemaining +
                ", cooldownRemaining=" + cooldownRemaining +
                ", triggers=" + triggers +
                ", installs/removes=" + installs + "/" + removes +
                ", hotTicks=" + hotTicks +
                ", installFailures=" + installFailures +
                ". Permanent hot-path Harmony probes are not installed; WorkGiver broad timing is disabled.";
        }
    }
}
