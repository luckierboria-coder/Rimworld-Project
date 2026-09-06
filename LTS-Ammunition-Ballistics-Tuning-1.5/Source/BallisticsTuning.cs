using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using RimWorld;
using Verse;

namespace LTSAmmoBallisticsTuning15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony("allen.ltsammo.ballisticstuning.1.5");
                PatchRegistry.Apply(harmony);
                Log.Message("[LTS Ammo Ballistics Tuning 1.5] loaded");
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] startup failed: " + e);
            }
        }
    }

    internal static class PatchRegistry
    {
        internal static readonly Type AmmoLogicType = AccessTools.TypeByName("Ammunition.Logic.AmmoLogic");
        internal static readonly Type InventoryAmmoFallbackType = AccessTools.TypeByName("LTSAmmoInventoryFallback15.AmmoFallback");
        internal static readonly FieldInfo ShotAmmoField = InventoryAmmoFallbackType == null
            ? null
            : AccessTools.Field(InventoryAmmoFallbackType, "ShotAmmo");

        internal static void Apply(Harmony harmony)
        {
            if (AmmoLogicType == null)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] LTS Ammunition framework not found");
                return;
            }

            if (InventoryAmmoFallbackType == null || ShotAmmoField == null)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] LTS Inventory System Patch not found or incompatible");
                return;
            }

            AmmoClassifier.BuildProjectileFallbackMap();

            var launch = typeof(Projectile).GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(m => m.Name == "Launch" && m.GetParameters().Length == 8);
            if (launch != null)
                harmony.Patch(launch, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ProjectileLaunchPostfix)));

            var damageGetter = AccessTools.PropertyGetter(typeof(Projectile), "DamageAmount");
            if (damageGetter != null)
                harmony.Patch(damageGetter, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.DamageAmountPostfix)));

            var armorGetter = AccessTools.PropertyGetter(typeof(Projectile), "ArmorPenetration");
            if (armorGetter != null)
                harmony.Patch(armorGetter, postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ArmorPenetrationPostfix)));

            var bulletImpact = AccessTools.Method(typeof(Bullet), "Impact");
            if (bulletImpact != null)
                harmony.Patch(bulletImpact,
                    prefix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ImpactPrefix)),
                    postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ImpactPostfix)));

            var fireArrowImpact = AccessTools.Method(typeof(FireArrow), "Impact");
            if (fireArrowImpact != null)
                harmony.Patch(fireArrowImpact,
                    prefix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ImpactPrefix)),
                    postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.ImpactPostfix)));

            var injuryPostAdd = AccessTools.Method(typeof(Hediff_Injury), "PostAdd");
            if (injuryPostAdd != null)
                harmony.Patch(injuryPostAdd,
                    postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.InjuryPostAddPostfix)));

            var injuryRemoved = AccessTools.Method(typeof(Hediff_Injury), "PostRemoved");
            if (injuryRemoved != null)
                harmony.Patch(injuryRemoved,
                    postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.InjuryPostRemovedPostfix)));

            var bleedGetter = AccessTools.PropertyGetter(typeof(Hediff_Injury), "BleedRate");
            if (bleedGetter != null)
                harmony.Patch(bleedGetter,
                    postfix: new HarmonyMethod(typeof(BallisticsRuntime), nameof(BallisticsRuntime.BleedRatePostfix)));
        }
    }

    internal enum AmmoEffectClass
    {
        BallisticOrShell,
        ShortArrow,
        RecurveArrow,
        GreatArrow,
        OtherArrow,
        Bolt,
        Unknown
    }

    internal sealed class ShotContext
    {
        internal ThingDef AmmoDef;
        internal AmmoEffectClass EffectClass;
    }

    internal static class AmmoClassifier
    {
        private static readonly HashSet<string> ShortArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_ShortArrow",
            "LTS_ShortFlameArrow"
        };

        private static readonly HashSet<string> RecurveArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_RecurveArrow",
            "LTS_RecurveFlameArrow"
        };

        private static readonly HashSet<string> GreatArrows = new HashSet<string>(StringComparer.Ordinal)
        {
            "LTS_GreatArrow",
            "LTS_GreatFlameArrow"
        };

        private static readonly Dictionary<ThingDef, ThingDef> ProjectileToAmmo = new Dictionary<ThingDef, ThingDef>();

        internal static AmmoEffectClass Classify(ThingDef ammoDef)
        {
            if (ammoDef == null) return AmmoEffectClass.Unknown;
            string name = ammoDef.defName ?? string.Empty;

            if (ShortArrows.Contains(name)) return AmmoEffectClass.ShortArrow;
            if (RecurveArrows.Contains(name)) return AmmoEffectClass.RecurveArrow;
            if (GreatArrows.Contains(name)) return AmmoEffectClass.GreatArrow;
            if (name.IndexOf("Arrow", StringComparison.OrdinalIgnoreCase) >= 0) return AmmoEffectClass.OtherArrow;
            if (name.IndexOf("Bolt", StringComparison.OrdinalIgnoreCase) >= 0) return AmmoEffectClass.Bolt;

            // In the LTS ammo packs, non-arrow/non-bolt ammunition is the bullet/shell family:
            // cartridges, shotgun shells, musket balls, railgun ammunition, cannon/RCL/rocket rounds, etc.
            return AmmoEffectClass.BallisticOrShell;
        }

        internal static float DamageMultiplier(AmmoEffectClass effectClass)
        {
            switch (effectClass)
            {
                case AmmoEffectClass.BallisticOrShell: return 1.50f;
                case AmmoEffectClass.GreatArrow: return 1.25f;
                case AmmoEffectClass.ShortArrow: return 0.90f;
                default: return 1.00f;
            }
        }

        internal static float ArmorPenetrationMultiplier(AmmoEffectClass effectClass)
        {
            // User rule: ONLY short arrows (normal or flame) get +10% penetration.
            return effectClass == AmmoEffectClass.ShortArrow ? 1.10f : 1.00f;
        }

        internal static void BuildProjectileFallbackMap()
        {
            ProjectileToAmmo.Clear();
            foreach (ThingDef ammoDef in DefDatabase<ThingDef>.AllDefsListForReading)
            {
                ThingDef projectile = GetAmmoProjectile(ammoDef);
                if (projectile == null) continue;

                // If multiple ammo defs share one projectile, do not guess after save/load.
                ThingDef existing;
                if (ProjectileToAmmo.TryGetValue(projectile, out existing) && existing != ammoDef)
                    ProjectileToAmmo[projectile] = null;
                else if (!ProjectileToAmmo.ContainsKey(projectile))
                    ProjectileToAmmo.Add(projectile, ammoDef);
            }
        }

        internal static ThingDef AmmoFromProjectile(ThingDef projectileDef)
        {
            if (projectileDef == null) return null;
            ThingDef ammo;
            return ProjectileToAmmo.TryGetValue(projectileDef, out ammo) ? ammo : null;
        }

        private static ThingDef GetAmmoProjectile(ThingDef ammoDef)
        {
            if (ammoDef == null || ammoDef.modExtensions == null) return null;
            foreach (DefModExtension ext in ammoDef.modExtensions)
            {
                if (ext == null || ext.GetType().FullName != "Ammunition.DefModExtensions.AmmunitionExtension") continue;
                FieldInfo field = AccessTools.Field(ext.GetType(), "bulletDef");
                if (field == null) return null;
                return field.GetValue(ext) as ThingDef;
            }
            return null;
        }
    }

    public sealed class RecurveBleedTracker : GameComponent
    {
        private List<int> markedInjuryIds = new List<int>();
        private HashSet<int> markedSet = new HashSet<int>();

        public RecurveBleedTracker(Game game)
        {
        }

        public override void ExposeData()
        {
            base.ExposeData();
            Scribe_Collections.Look<int>(ref markedInjuryIds, "ltsRecurveBleedInjuries", LookMode.Value);

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                if (markedInjuryIds == null) markedInjuryIds = new List<int>();
                markedSet = new HashSet<int>(markedInjuryIds);
            }
        }

        internal bool Contains(int id)
        {
            return id >= 0 && markedSet.Contains(id);
        }

        internal void Mark(int id)
        {
            if (id < 0 || !markedSet.Add(id)) return;
            markedInjuryIds.Add(id);
        }

        internal void Unmark(int id)
        {
            if (id < 0 || !markedSet.Remove(id)) return;
            markedInjuryIds.Remove(id);
        }

        internal static RecurveBleedTracker Current
        {
            get { return Current.Game == null ? null : Current.Game.GetComponent<RecurveBleedTracker>(); }
        }
    }

    internal static class BallisticsRuntime
    {
        private static readonly ConditionalWeakTable<Projectile, ShotContext> ProjectileContexts = new ConditionalWeakTable<Projectile, ShotContext>();

        [ThreadStatic]
        private static ShotContext activeImpactContext;

        public static void ProjectileLaunchPostfix(Projectile __instance, Thing equipment)
        {
            if (__instance == null) return;

            try
            {
                Thing weapon = equipment;
                if (weapon == null && __instance.Launcher is Pawn pawn && pawn.equipment != null)
                {
                    ThingWithComps primary = pawn.equipment.Primary;
                    if (primary != null && (__instance.EquipmentDef == null || primary.def == __instance.EquipmentDef))
                        weapon = primary;
                }

                ThingDef ammoDef = null;
                if (weapon != null)
                {
                    IDictionary shotAmmo = PatchRegistry.ShotAmmoField.GetValue(null) as IDictionary;
                    if (shotAmmo != null && shotAmmo.Contains(weapon))
                        ammoDef = shotAmmo[weapon] as ThingDef;
                }

                if (ammoDef == null)
                    ammoDef = AmmoClassifier.AmmoFromProjectile(__instance.def);

                if (ammoDef == null) return;

                var context = new ShotContext
                {
                    AmmoDef = ammoDef,
                    EffectClass = AmmoClassifier.Classify(ammoDef)
                };

                ProjectileContexts.Remove(__instance);
                ProjectileContexts.Add(__instance, context);
            }
            catch (Exception e)
            {
                Log.Error("[LTS Ammo Ballistics Tuning 1.5] projectile context capture failed: " + e);
            }
        }

        private static ShotContext GetContext(Projectile projectile)
        {
            if (projectile == null) return null;

            ShotContext context;
            if (ProjectileContexts.TryGetValue(projectile, out context)) return context;

            ThingDef ammoDef = AmmoClassifier.AmmoFromProjectile(projectile.def);
            if (ammoDef == null) return null;

            context = new ShotContext
            {
                AmmoDef = ammoDef,
                EffectClass = AmmoClassifier.Classify(ammoDef)
            };
            ProjectileContexts.Add(projectile, context);
            return context;
        }

        public static void DamageAmountPostfix(Projectile __instance, ref int __result)
        {
            ShotContext context = GetContext(__instance);
            if (context == null) return;

            float multiplier = AmmoClassifier.DamageMultiplier(context.EffectClass);
            if (Math.Abs(multiplier - 1f) < 0.0001f) return;
            __result = Math.Max(0, (int)Math.Round(__result * multiplier, MidpointRounding.AwayFromZero));
        }

        public static void ArmorPenetrationPostfix(Projectile __instance, ref float __result)
        {
            ShotContext context = GetContext(__instance);
            if (context == null) return;

            float multiplier = AmmoClassifier.ArmorPenetrationMultiplier(context.EffectClass);
            if (Math.Abs(multiplier - 1f) < 0.0001f) return;
            __result *= multiplier;
        }

        public static void ImpactPrefix(Projectile __instance, out ShotContext __state)
        {
            __state = activeImpactContext;
            activeImpactContext = GetContext(__instance);
        }

        public static void ImpactPostfix(ShotContext __state)
        {
            activeImpactContext = __state;
        }

        public static void InjuryPostAddPostfix(Hediff_Injury __instance)
        {
            if (__instance == null || activeImpactContext == null) return;
            if (activeImpactContext.EffectClass != AmmoEffectClass.RecurveArrow) return;
            if (__instance.def == null || __instance.def.defName != "Stab") return;

            RecurveBleedTracker tracker = RecurveBleedTracker.Current;
            if (tracker != null) tracker.Mark(__instance.loadID);
        }

        public static void InjuryPostRemovedPostfix(Hediff_Injury __instance)
        {
            if (__instance == null) return;
            RecurveBleedTracker tracker = RecurveBleedTracker.Current;
            if (tracker != null) tracker.Unmark(__instance.loadID);
        }

        public static void BleedRatePostfix(Hediff_Injury __instance, ref float __result)
        {
            if (__instance == null || __result <= 0f) return;
            RecurveBleedTracker tracker = RecurveBleedTracker.Current;
            if (tracker != null && tracker.Contains(__instance.loadID))
                __result *= 1.25f;
        }
    }
}
