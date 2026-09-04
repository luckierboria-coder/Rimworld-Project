using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace InfinityRelationLimit15
{
    public sealed class InfinityRelationLimitSettings : ModSettings
    {
        public int minGoodwill = -250;
        public int maxGoodwill = 250;

        public override void ExposeData()
        {
            Scribe_Values.Look(ref minGoodwill, "minGoodwill", -250);
            Scribe_Values.Look(ref maxGoodwill, "maxGoodwill", 250);
            Normalize();
            base.ExposeData();
        }

        public void Normalize()
        {
            if (minGoodwill > maxGoodwill)
            {
                int oldMin = minGoodwill;
                minGoodwill = maxGoodwill;
                maxGoodwill = oldMin;
            }
        }
    }

    public sealed class InfinityRelationLimitMod : Mod
    {
        public static InfinityRelationLimitSettings Settings;

        private string minBuffer;
        private string maxBuffer;

        public InfinityRelationLimitMod(ModContentPack content) : base(content)
        {
            Settings = GetSettings<InfinityRelationLimitSettings>();
            Settings.Normalize();
            minBuffer = Settings.minGoodwill.ToString();
            maxBuffer = Settings.maxGoodwill.ToString();

            Harmony harmony = new Harmony("shangruo.infinityrelation.limit.1.5");
            harmony.PatchAll();
            Log.Message("[Infinity Relation Limit] Loaded. Default/configured bounds: " + Settings.minGoodwill + " to " + Settings.maxGoodwill + ".");
        }

        public override string SettingsCategory()
        {
            return "IRL_SettingsCategory".Translate().ToString();
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            float y = inRect.y;
            float labelWidth = Math.Min(360f, inRect.width * 0.58f);
            float fieldWidth = Math.Min(180f, inRect.width * 0.30f);
            float fieldX = inRect.x + labelWidth + 16f;

            Widgets.Label(new Rect(inRect.x, y, labelWidth, 30f), "IRL_MinLabel".Translate().ToString());
            minBuffer = Widgets.TextField(new Rect(fieldX, y, fieldWidth, 30f), minBuffer ?? Settings.minGoodwill.ToString());
            int parsedMin;
            if (int.TryParse(minBuffer, out parsedMin))
            {
                Settings.minGoodwill = parsedMin;
            }
            y += 38f;

            Widgets.Label(new Rect(inRect.x, y, labelWidth, 30f), "IRL_MaxLabel".Translate().ToString());
            maxBuffer = Widgets.TextField(new Rect(fieldX, y, fieldWidth, 30f), maxBuffer ?? Settings.maxGoodwill.ToString());
            int parsedMax;
            if (int.TryParse(maxBuffer, out parsedMax))
            {
                Settings.maxGoodwill = parsedMax;
            }
            y += 42f;

            int effectiveMin;
            int effectiveMax;
            RelationLimiter.GetBounds(out effectiveMin, out effectiveMax);
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 30f), "IRL_EffectiveBounds".Translate(effectiveMin, effectiveMax).ToString());
            y += 34f;

            float buttonWidth = Math.Min(190f, (inRect.width - 12f) * 0.5f);
            if (Widgets.ButtonText(new Rect(inRect.x, y, buttonWidth, 32f), "IRL_ApplyNow".Translate().ToString()))
            {
                Settings.Normalize();
                minBuffer = Settings.minGoodwill.ToString();
                maxBuffer = Settings.maxGoodwill.ToString();
                RelationLimiter.ClampAllKnownRelations();
            }

            if (Widgets.ButtonText(new Rect(inRect.x + buttonWidth + 12f, y, buttonWidth, 32f), "IRL_Reset".Translate().ToString()))
            {
                Settings.minGoodwill = -250;
                Settings.maxGoodwill = 250;
                minBuffer = "-250";
                maxBuffer = "250";
                RelationLimiter.ClampAllKnownRelations();
            }

            y += 44f;
            Widgets.Label(new Rect(inRect.x, y, inRect.width, 72f), "IRL_Description".Translate().ToString());
        }
    }

    internal static class RelationLimiter
    {
        internal static void GetBounds(out int min, out int max)
        {
            InfinityRelationLimitSettings settings = InfinityRelationLimitMod.Settings;
            if (settings == null)
            {
                min = -250;
                max = 250;
                return;
            }

            min = settings.minGoodwill;
            max = settings.maxGoodwill;
            if (min > max)
            {
                int swap = min;
                min = max;
                max = swap;
            }
        }

        internal static void Clamp(FactionRelation relation)
        {
            if (relation == null)
            {
                return;
            }

            int min;
            int max;
            GetBounds(out min, out max);

            if (relation.baseGoodwill > max)
            {
                relation.baseGoodwill = max;
            }
            else if (relation.baseGoodwill < min)
            {
                relation.baseGoodwill = min;
            }
        }

        internal static void ClampAllKnownRelations()
        {
            try
            {
                if (Find.FactionManager == null)
                {
                    return;
                }

                List<Faction> factions = Find.FactionManager.AllFactionsListForReading;
                if (factions == null)
                {
                    return;
                }

                for (int i = 0; i < factions.Count; i++)
                {
                    Faction a = factions[i];
                    if (a == null)
                    {
                        continue;
                    }

                    for (int j = i + 1; j < factions.Count; j++)
                    {
                        Faction b = factions[j];
                        if (b == null || a == b)
                        {
                            continue;
                        }

                        Clamp(a.RelationWith(b));
                        Clamp(b.RelationWith(a));
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Warning("[Infinity Relation Limit] Could not clamp every existing faction relation: " + ex.Message);
            }
        }
    }

    [HarmonyPatch]
    internal static class RelationWithPatch
    {
        private static MethodBase TargetMethod()
        {
            return AccessTools.Method(typeof(Faction), "RelationWith", new Type[] { typeof(Faction) });
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(FactionRelation __result)
        {
            RelationLimiter.Clamp(__result);
        }
    }

    [HarmonyPatch]
    internal static class TryAffectGoodwillPatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            MethodInfo[] methods = typeof(Faction).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            for (int i = 0; i < methods.Length; i++)
            {
                MethodInfo method = methods[i];
                if (method.Name == "TryAffectGoodwillWith")
                {
                    yield return method;
                }
            }
        }

        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix(Faction __instance, object[] __args)
        {
            if (__instance == null || __args == null)
            {
                return;
            }

            for (int i = 0; i < __args.Length; i++)
            {
                Faction other = __args[i] as Faction;
                if (other != null && other != __instance)
                {
                    RelationLimiter.Clamp(__instance.RelationWith(other));
                    return;
                }
            }
        }
    }

    [HarmonyPatch(typeof(Game), "FinalizeInit")]
    internal static class GameFinalizeInitPatch
    {
        [HarmonyPostfix]
        [HarmonyPriority(Priority.Last)]
        private static void Postfix()
        {
            RelationLimiter.ClampAllKnownRelations();
        }
    }
}
