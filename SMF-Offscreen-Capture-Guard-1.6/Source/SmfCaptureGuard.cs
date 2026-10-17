using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Allen.SmfCaptureGuard
{
    /// <summary>
    /// Minimal standalone extraction of the offscreen capture protection in
    /// Allen.SmfGrowthWindowGuard 1.0.0.0.
    /// No child growth dialog, letter, window-stack or rendering-policy hooks.
    /// </summary>
    [StaticConstructorOnStartup]
    internal static class SmfCaptureGuard
    {
        private static readonly Guid SupportedSmfMvid =
            new Guid("b5a8419b-43bc-496c-aafd-434185e80478");

        static SmfCaptureGuard()
        {
            try
            {
                var hybridSessionType =
                    AccessTools.TypeByName("SimplyMoreFPS.Rendering.HybridSession");

                if (hybridSessionType == null)
                {
                    Log.Warning("[SMF Capture Guard] SMF HybridSession not found; inactive.");
                    return;
                }

                if (hybridSessionType.Module.ModuleVersionId != SupportedSmfMvid)
                {
                    Log.Warning("[SMF Capture Guard] Unsupported SMF binary version; inactive.");
                    return;
                }

                var sessionType = AccessTools.Inner(hybridSessionType, "Session");
                MethodInfo beginCapturedFrame = sessionType == null
                    ? null
                    : AccessTools.Method(sessionType, "BeginCapturedFrame");

                if (beginCapturedFrame == null ||
                    beginCapturedFrame.ReturnType != typeof(bool))
                {
                    Log.Warning("[SMF Capture Guard] Expected BeginCapturedFrame(bool) layout unavailable; inactive.");
                    return;
                }

                var harmony = new Harmony("allen.smf.offscreen-capture-guard");
                harmony.Patch(beginCapturedFrame,
                    prefix: new HarmonyMethod(
                        typeof(SmfCaptureGuard),
                        nameof(BeforeCapturedFrame)));

                Log.Message("[SMF Capture Guard] Offscreen RenderTexture ownership guard installed.");
            }
            catch (Exception ex)
            {
                Log.Error("[SMF Capture Guard] Patch installation failed: " + ex);
            }
        }

        // Preserve exact behavior of original BeforeCapturedFrame:
        // UnityEngine.Object overloaded equality treats destroyed textures as null.
        // If another renderer currently owns the active render target, fail closed
        // for this attempted SMF capture, without changing any game window behavior.
        private static bool BeforeCapturedFrame(ref bool __result)
        {
            if (RenderTexture.active == null)
                return true;

            __result = false;
            return false;
        }
    }
}
