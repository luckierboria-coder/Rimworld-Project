using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Allen.LargePawnsCaiStartupHotfix
{
    public sealed class LargePawnsCaiStartupHotfixMod : Mod
    {
        internal const string HarmonyId = "allen.largepawns.cai.startuphotfix";
        private static bool attempted;
        private static bool installed;

        public LargePawnsCaiStartupHotfixMod(ModContentPack content) : base(content)
        {
            TryInstall();
        }

        private static void TryInstall()
        {
            if (attempted) return;
            attempted = true;

            try
            {
                Type targetType = null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (assembly == null) continue;
                    try
                    {
                        targetType = assembly.GetType("LargePawns.Patches.CaiCompatHelper", false);
                    }
                    catch
                    {
                        targetType = null;
                    }
                    if (targetType != null) break;
                }

                if (targetType == null)
                {
                    Log.Warning("[LargePawns CAI Startup Hotfix] CaiCompatHelper type not found. No patch installed.");
                    return;
                }

                MethodInfo target = targetType.GetMethod(
                    "EnsureApplied",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

                if (target == null)
                {
                    Log.Warning("[LargePawns CAI Startup Hotfix] EnsureApplied method not found. No patch installed.");
                    return;
                }

                Harmony harmony = new Harmony(HarmonyId);
                harmony.Patch(
                    target,
                    prefix: new HarmonyMethod(typeof(LargePawnsCaiStartupHotfixMod), nameof(SkipBrokenCaiCompat))
                    {
                        priority = Priority.First
                    });

                installed = true;
                Log.Message("[LargePawns CAI Startup Hotfix] Installed. Only CaiCompatHelper.EnsureApplied is bypassed before LargePawns.Main construction.");
            }
            catch (Exception ex)
            {
                Log.Error("[LargePawns CAI Startup Hotfix] install failed: " + ex);
            }
        }

        private static bool SkipBrokenCaiCompat()
        {
            return false;
        }

        public override string SettingsCategory()
        {
            return "LargePawns CAI Startup Hotfix";
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            Widgets.Label(inRect,
                "Startup guard status: " + (installed ? "ACTIVE" : "NOT INSTALLED") +
                "\nOnly LargePawns.Patches.CaiCompatHelper.EnsureApplied is bypassed.");
        }
    }
}