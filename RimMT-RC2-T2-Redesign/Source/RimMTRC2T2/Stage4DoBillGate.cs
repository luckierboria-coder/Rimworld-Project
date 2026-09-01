using System;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTRC2T2
{
    /// <summary>
    /// RC2-T2 Stage 4A: conservative DoBill hard-negative gate.
    ///
    /// The gate is intentionally narrow:
    /// - only WorkGiver_DoBill.JobOnThing;
    /// - only while the current JobGiver_Work package has already spent >=16ms;
    /// - only when the target is an IBillGiver with a live BillStack;
    /// - only when BillStack.AnyShouldDoNow is definitively false.
    ///
    /// Every unknown/maybe-positive case falls through to the original method. Ingredient
    /// selection, reservations, reachability, Performance Fish prepatches and final Job
    /// construction remain authoritative. If a foreign Harmony patch owns JobOnThing,
    /// Stage 4A fails closed and does not install.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class Stage4DoBillGate
    {
        private const string HarmonyId = "allen.rimmt";
        private const double AdmissionMs = 16.0;
        private static readonly Harmony Harmony = new Harmony(HarmonyId);

        private static bool installed;
        private static bool patched;
        private static string disabledReason = "not-installed";

        private static long observed;
        private static long pre16Bypass;
        private static long nonBillGiverBypass;
        private static long nullStackBypass;
        private static long activeBillBypass;
        private static long hardNegativeSkips;
        private static long failures;

        static Stage4DoBillGate()
        {
            LongEventHandler.ExecuteWhenFinished(Install);
        }

        private static void Install()
        {
            if (installed) return;
            installed = true;

            try
            {
                MethodInfo target = FindJobOnThing();
                if (target == null)
                {
                    disabledReason = "JobOnThing-not-found";
                    return;
                }

                if (HasUnsafeForeignPatch(target))
                {
                    disabledReason = "foreign-Harmony-authority";
                    Log.Message("[RimMT] RC2-T2 Stage 4A DoBill gate disabled: foreign Harmony authority detected on WorkGiver_DoBill.JobOnThing. Vanilla/mod authority retained.");
                    HookReport();
                    return;
                }

                Harmony.Patch(target,
                    prefix: new HarmonyMethod(typeof(Stage4DoBillGate), nameof(JobOnThingPrefix)) { priority = Priority.First });
                patched = true;
                disabledReason = "active";
                HookReport();

                Log.Message("[RimMT] RC2-T2 Stage 4A DoBill Hard-Negative Gate installed: after a live JobPackage reaches >=16ms, an IBillGiver with BillStack.AnyShouldDoNow=false is rejected before JobOnThing. Unknown/positive cases remain original-authority; ingredient search is untouched.");
            }
            catch (Exception ex)
            {
                disabledReason = "install-failure:" + ex.GetType().Name;
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT] RC2-T2 Stage 4A DoBill gate failed closed: " + ex.GetType().Name + ": " + ex.Message);
                HookReport();
            }
        }

        private static MethodInfo FindJobOnThing()
        {
            MethodInfo[] methods = typeof(WorkGiver_DoBill).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method == null || method.Name != "JobOnThing" || method.ReturnType != typeof(Job)) continue;
                ParameterInfo[] ps = method.GetParameters();
                if (ps.Length < 2) continue;
                bool hasPawn = false;
                bool hasThing = false;
                for (int p = 0; p < ps.Length; p++)
                {
                    if (ps[p].ParameterType == typeof(Pawn)) hasPawn = true;
                    else if (typeof(Thing).IsAssignableFrom(ps[p].ParameterType)) hasThing = true;
                }
                if (hasPawn && hasThing) return method;
            }
            return null;
        }

        public static bool JobOnThingPrefix(object[] __args, ref Job __result)
        {
            Interlocked.Increment(ref observed);

            try
            {
                double elapsed = PreTailStructureProfiler.CurrentJobElapsedMs;
                if (elapsed < AdmissionMs)
                {
                    Interlocked.Increment(ref pre16Bypass);
                    return true;
                }

                Thing thing = null;
                if (__args != null)
                {
                    for (int i = 0; i < __args.Length; i++)
                    {
                        Thing candidate = __args[i] as Thing;
                        if (candidate != null)
                        {
                            thing = candidate;
                            break;
                        }
                    }
                }

                IBillGiver billGiver = thing as IBillGiver;
                if (billGiver == null)
                {
                    Interlocked.Increment(ref nonBillGiverBypass);
                    return true;
                }

                BillStack stack = billGiver.BillStack;
                if (stack == null)
                {
                    Interlocked.Increment(ref nullStackBypass);
                    return true;
                }

                if (stack.AnyShouldDoNow)
                {
                    Interlocked.Increment(ref activeBillBypass);
                    return true;
                }

                __result = null;
                Interlocked.Increment(ref hardNegativeSkips);
                return false;
            }
            catch
            {
                Interlocked.Increment(ref failures);
                return true;
            }
        }

        private static bool HasUnsafeForeignPatch(MethodBase target)
        {
            Patches info = HarmonyLib.Harmony.GetPatchInfo(target);
            if (info == null) return false;
            return HasForeign(info.Prefixes) || HasForeign(info.Postfixes) || HasForeign(info.Transpilers) || HasForeign(info.Finalizers);
        }

        private static bool HasForeign(System.Collections.Generic.IList<Patch> patches)
        {
            if (patches == null) return false;
            for (int i = 0; i < patches.Count; i++)
            {
                Patch patch = patches[i];
                if (patch == null) continue;
                if (string.Equals(patch.owner, HarmonyId, StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        private static void HookReport()
        {
            try
            {
                Type diagnostics = AccessTools.TypeByName("RimMT.RimMTDiagnostics");
                MethodInfo report = diagnostics == null ? null : AccessTools.Method(diagnostics, "LogRuntimeReport");
                if (report != null)
                    Harmony.Patch(report, postfix: new HarmonyMethod(typeof(Stage4DoBillGate), nameof(ReportPostfix)) { priority = Priority.Last });
            }
            catch
            {
                Interlocked.Increment(ref failures);
            }
        }

        public static void ReportPostfix()
        {
            Log.Message("[RimMT] RC2-T2 Stage 4A DoBill Gate report: patched=" + patched +
                ", admissionMs=" + AdmissionMs.ToString("F0") +
                ", state=" + disabledReason +
                ", observed=" + Interlocked.Read(ref observed) +
                ", pre16Bypass=" + Interlocked.Read(ref pre16Bypass) +
                ", nonBillGiverBypass=" + Interlocked.Read(ref nonBillGiverBypass) +
                ", nullStackBypass=" + Interlocked.Read(ref nullStackBypass) +
                ", activeBillBypass=" + Interlocked.Read(ref activeBillBypass) +
                ", hardNegativeSkips=" + Interlocked.Read(ref hardNegativeSkips) +
                ", failures=" + Interlocked.Read(ref failures) + ".");
        }
    }
}
