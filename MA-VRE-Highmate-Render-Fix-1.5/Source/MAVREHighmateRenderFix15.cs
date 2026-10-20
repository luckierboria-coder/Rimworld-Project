using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;

namespace Allen.MAVREHighmateRenderFix15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.ma.vre.highmate.renderfix15";
        private const string HighmatePatchTypeName = "VanillaRacesExpandedHighmate.VanillaRacesExpandedHighmate_PawnRenderNodeWorker_Apparel_Head_HeadgearVisible_Patch";
        private const string AnimRendererTypeName = "AM.AnimRenderer";

        [ThreadStatic]
        private static int meleeAnimationDrawDepth;

        private static long invalidParmsSkipped;
        private static long meleeScopeNreSuppressed;

        static Bootstrap()
        {
            try
            {
                Type highmatePatchType = AccessTools.TypeByName(HighmatePatchTypeName);
                Type animRendererType = AccessTools.TypeByName(AnimRendererTypeName);

                if (highmatePatchType == null)
                {
                    Log.Message("[MA - VRE Highmate Render Fix 1.5] VRE Highmate not detected; hotfix remains inert.");
                    return;
                }

                MethodInfo highmatePostfix = highmatePatchType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "Postfix" && m.GetParameters().Length >= 1);
                if (highmatePostfix == null)
                {
                    Log.Warning("[MA - VRE Highmate Render Fix 1.5] Highmate HeadgearVisible postfix was not found; no patch applied.");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    highmatePostfix,
                    prefix: new HarmonyMethod(typeof(Bootstrap), nameof(HighmatePostfixPrefix)) { priority = Priority.First },
                    finalizer: new HarmonyMethod(typeof(Bootstrap), nameof(HighmatePostfixFinalizer)) { priority = Priority.Last });

                if (animRendererType != null)
                {
                    MethodInfo drawPawns = animRendererType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static)
                        .FirstOrDefault(m => m.Name == "DrawPawns" && m.GetParameters().Length == 1);
                    if (drawPawns != null)
                    {
                        harmony.Patch(
                            drawPawns,
                            prefix: new HarmonyMethod(typeof(Bootstrap), nameof(AnimDrawPrefix)) { priority = Priority.First },
                            finalizer: new HarmonyMethod(typeof(Bootstrap), nameof(AnimDrawFinalizer)) { priority = Priority.Last });
                    }
                    else
                    {
                        Log.Warning("[MA - VRE Highmate Render Fix 1.5] AM.AnimRenderer.DrawPawns was not found. Invalid Highmate parms are still guarded, but MA-scope exception suppression is unavailable.");
                    }
                }
                else
                {
                    Log.Warning("[MA - VRE Highmate Render Fix 1.5] Melee Animation was not detected. Invalid Highmate parms are still guarded; MA-scope exception suppression remains inert.");
                }

                Log.Message("[MA - VRE Highmate Render Fix 1.5] ACTIVE. Guards VRE Highmate HeadgearVisible against incomplete PawnDrawParms and suppresses only NullReferenceException thrown by that Highmate postfix while inside Melee Animation DrawPawns. Other render exceptions remain visible.");
            }
            catch (Exception ex)
            {
                Log.Error("[MA - VRE Highmate Render Fix 1.5] Initialization failed; hotfix is inert. " + ex);
            }
        }

        public static bool HighmatePostfixPrefix(object[] __args)
        {
            try
            {
                if (__args == null || __args.Length == 0 || !(__args[0] is PawnDrawParms parms))
                    return true;

                Pawn pawn = parms.pawn;
                if (pawn != null && pawn.health != null && pawn.health.hediffSet != null)
                    return true;

                long count = Interlocked.Increment(ref invalidParmsSkipped);
                if (count == 1)
                {
                    Log.Warning("[MA - VRE Highmate Render Fix 1.5] Skipped VRE Highmate HeadgearVisible postfix for incomplete PawnDrawParms. This is expected for some Melee Animation temporary render passes; further identical skips are silent.");
                }
                return false;
            }
            catch
            {
                // Fail open: if our guard cannot inspect the arguments, preserve the original patch.
                return true;
            }
        }

        public static Exception HighmatePostfixFinalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            if (meleeAnimationDrawDepth > 0 && __exception is NullReferenceException)
            {
                long count = Interlocked.Increment(ref meleeScopeNreSuppressed);
                if (count == 1)
                {
                    Log.Warning("[MA - VRE Highmate Render Fix 1.5] Suppressed a VRE Highmate HeadgearVisible NullReferenceException inside Melee Animation DrawPawns. Further identical exceptions are silent.");
                }
                return null;
            }

            return __exception;
        }

        public static void AnimDrawPrefix()
        {
            meleeAnimationDrawDepth++;
        }

        public static Exception AnimDrawFinalizer(Exception __exception)
        {
            if (meleeAnimationDrawDepth > 0)
                meleeAnimationDrawDepth--;
            return __exception;
        }
    }
}
