using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMTCleanPathfindingD22A2
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.rimmt.cleanpathfinding.d22a2";
        private static long transpilerPasses;
        private static long transpilerReplacements;
        private static long regionMethodsPatched;
        private static long regionAllowsReplacements;
        private static long nullRegionLinksRejected;
        private static long nativeRegionFixMethods;
        private static long failures;
        private static bool installed;

        static Bootstrap()
        {
            try
            {
                Harmony harmony = new Harmony(HarmonyId);
                MethodInfo makeNewToils = AccessTools.Method(typeof(JobDriver_ConstructFinishFrame), "MakeNewToils");
                MethodInfo moveNext = makeNewToils == null ? null : AccessTools.EnumeratorMoveNext(makeNewToils);
                if (moveNext == null)
                {
                    Log.Warning("[RimMT Clean Pathfinding A2.5 Exact Endpoint] FinishFrame iterator was not found; patch remains inert.");
                    return;
                }

                harmony.Patch(moveNext, transpiler: new HarmonyMethod(
                    typeof(Bootstrap), nameof(FinishFrameEndpointTranspiler))
                {
                    priority = Priority.Last
                });
                InstallRegionLinkGuards(harmony);
                installed = true;
                Log.Message("[RimMT Clean Pathfinding A2.6 Region Link Guard] Exactly one FinishFrame path-endpoint branch may change to Touch. RegionCostCalculator null links are rejected before Region.Allows; Clean Pathfinding's native region fix remains authoritative when present. No PathFinder, StartPath, Root_Play, Diagnostics, cache, queue, telemetry, or per-frame patch is installed.");
            }
            catch (Exception ex)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT Clean Pathfinding A2.6 Region Link Guard] Installation failed closed: " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        public static IEnumerable<CodeInstruction> FinishFrameEndpointTranspiler(IEnumerable<CodeInstruction> instructions)
        {
            Interlocked.Increment(ref transpilerPasses);
            MethodInfo attachmentGetter = AccessTools.PropertyGetter(
                typeof(JobDriver_ConstructFinishFrame), "IsBuildingAttachment");
            MethodInfo replacement = AccessTools.Method(typeof(Bootstrap), nameof(UseTouchEndpoint));

            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            int match = -1;
            int matches = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                CodeInstruction code = codes[i];
                if (attachmentGetter != null && replacement != null && code.Calls(attachmentGetter))
                {
                    match = i;
                    matches++;
                }
            }
            if (matches != 1)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT Clean Pathfinding A2.5 Exact Endpoint] Expected exactly one FinishFrame endpoint branch, found " + matches + "; original IL retained.");
                return codes;
            }

            codes[match].opcode = OpCodes.Call;
            codes[match].operand = replacement;
            Interlocked.Increment(ref transpilerReplacements);
            return codes;
        }

        public static bool UseTouchEndpoint(JobDriver_ConstructFinishFrame driver)
        {
            return false;
        }

        private static void InstallRegionLinkGuards(Harmony harmony)
        {
            string[] methods = new string[] {
                "Init",
                "GetRegionDistance",
                "GetRegionBestDistances",
                "GetPreciseRegionLinkDistances"
            };
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo target = AccessTools.Method(typeof(RegionCostCalculator), methods[i]);
                if (target == null)
                {
                    Interlocked.Increment(ref failures);
                    Log.Warning("[RimMT Clean Pathfinding A2.6 Region Link Guard] Missing RegionCostCalculator." + methods[i] + "; that guard remains inert.");
                    continue;
                }

                Patches patches = Harmony.GetPatchInfo(target);
                bool nativeFix = false;
                if (patches != null)
                {
                    foreach (Patch patch in patches.Transpilers)
                    {
                        if (!string.IsNullOrEmpty(patch.owner) &&
                            patch.owner.IndexOf("cleanpathfinding", StringComparison.OrdinalIgnoreCase) >= 0)
                        {
                            nativeFix = true;
                            break;
                        }
                    }
                }
                if (nativeFix)
                {
                    Interlocked.Increment(ref nativeRegionFixMethods);
                    continue;
                }

                harmony.Patch(target, transpiler: new HarmonyMethod(
                    typeof(Bootstrap), nameof(RegionLinkNullGuardTranspiler))
                {
                    priority = Priority.Last
                });
                Interlocked.Increment(ref regionMethodsPatched);
            }
        }

        public static IEnumerable<CodeInstruction> RegionLinkNullGuardTranspiler(
            IEnumerable<CodeInstruction> instructions,
            MethodBase __originalMethod)
        {
            List<CodeInstruction> codes = new List<CodeInstruction>(instructions);
            MethodInfo allows = AccessTools.Method(typeof(Region), "Allows",
                new Type[] { typeof(TraverseParms), typeof(bool) });
            MethodInfo safeAllows = AccessTools.Method(typeof(Bootstrap), nameof(SafeAllows));
            int matches = 0;
            int match = -1;
            for (int i = 0; i < codes.Count; i++)
            {
                if (allows != null && codes[i].Calls(allows))
                {
                    matches++;
                    match = i;
                }
            }
            if (matches != 1 || safeAllows == null)
            {
                Interlocked.Increment(ref failures);
                Log.Warning("[RimMT Clean Pathfinding A2.6 Region Link Guard] Expected exactly one Region.Allows call in " +
                    (__originalMethod == null ? "?" : __originalMethod.Name) + ", found " + matches + "; original IL retained.");
                return codes;
            }

            codes[match].opcode = OpCodes.Call;
            codes[match].operand = safeAllows;
            Interlocked.Increment(ref regionAllowsReplacements);
            return codes;
        }

        public static bool SafeAllows(Region region, TraverseParms traverseParms, bool isDestination)
        {
            if (region == null)
            {
                Interlocked.Increment(ref nullRegionLinksRejected);
                return false;
            }
            return region.Allows(traverseParms, isDestination);
        }

        public static string Summary()
        {
            return "RimMT Clean Pathfinding A2.6 Region Link Guard: installed=" + installed +
                ", transpilerPasses=" + Interlocked.Read(ref transpilerPasses) +
                ", replacements=" + Interlocked.Read(ref transpilerReplacements) +
                ", regionMethodsPatched=" + Interlocked.Read(ref regionMethodsPatched) +
                ", regionAllowsReplacements=" + Interlocked.Read(ref regionAllowsReplacements) +
                ", nativeRegionFixMethods=" + Interlocked.Read(ref nativeRegionFixMethods) +
                ", nullRegionLinksRejected=" + Interlocked.Read(ref nullRegionLinksRejected) +
                ", failures=" + Interlocked.Read(ref failures) +
                ". Runtime hot-path work=NONE; PathFinder/StartPath/Root_Play/Diagnostics patches=NONE.";
        }
    }
}
