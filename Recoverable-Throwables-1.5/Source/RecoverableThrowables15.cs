using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.RecoverableThrowables15
{
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.recoverablethrowables.1.5.v13";
        private static bool initialized;

        internal static void EnsureInitialized(string source)
        {
            if (initialized)
                return;

            initialized = true;

            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                GrenadeOneUseCompat.TryPatch(harmony);

                LongEventHandler.ExecuteWhenFinished(LogEligibleDefs);
                Log.Warning("[Recoverable Throwables 1.5 v1.3] ACTIVE bootstrap=" + source +
                    ". Projectile.Launch is authoritative; physical thrown weapons consume one real item and recover it at projectile destruction.");
            }
            catch (Exception e)
            {
                initialized = false;
                Log.Error("[Recoverable Throwables 1.5 v1.3] startup failed bootstrap=" + source + ": " + e);
            }
        }

        private static void LogEligibleDefs()
        {
            try
            {
                List<ThingDef> eligible = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(RecoverableClassifier.DefLooksRecoverable)
                    .OrderBy(d => d.defName)
                    .ToList();

                Log.Warning("[Recoverable Throwables 1.5 v1.3] eligible defs=" + eligible.Count +
                    (eligible.Count == 0 ? "." : " [" + string.Join(", ", eligible.Select(d => d.defName)) + "]"));
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5 v1.3] eligible-def audit failed: " + e);
            }
        }
    }

    // Primary bootstrap: RimWorld constructs Mod subclasses for every active mod.
    // This avoids relying only on StaticConstructorOnStartup discovery in very large mod lists.
    internal sealed class RecoverableThrowablesMod : Mod
    {
        public RecoverableThrowablesMod(ModContentPack content) : base(content)
        {
            Bootstrap.EnsureInitialized("Mod.ctor");
        }
    }

    // Fallback bootstrap for environments where another loader path reaches static startup first.
    [StaticConstructorOnStartup]
    internal static class StaticBootstrapFallback
    {
        static StaticBootstrapFallback()
        {
            Bootstrap.EnsureInitialized("StaticConstructorOnStartup");
        }
    }

    internal static class RecoverableClassifier
    {
        internal const string ThrownRulePackDefName = "Combat_RangedFire_Thrown";

        internal static bool DefLooksRecoverable(ThingDef weaponDef)
        {
            if (weaponDef == null || !weaponDef.IsWeapon || weaponDef.IsApparel || weaponDef.Verbs == null)
                return false;

            for (int i = 0; i < weaponDef.Verbs.Count; i++)
            {
                if (IsPhysicalThrowVerb(weaponDef, weaponDef.Verbs[i]))
                    return true;
            }

            return false;
        }

        internal static bool IsRecoverableShot(ThingWithComps source, ThingDef projectileDef)
        {
            if (source == null || source.Destroyed || source.stackCount <= 0)
                return false;
            if (source is Apparel || source.def == null || !source.def.IsWeapon || source.def.IsApparel)
                return false;
            if (!IsRecoverableProjectile(projectileDef) || source.def.Verbs == null)
                return false;

            for (int i = 0; i < source.def.Verbs.Count; i++)
            {
                VerbProperties props = source.def.Verbs[i];
                if (!IsPhysicalThrowVerb(source.def, props))
                    continue;

                // For vanilla-style thrown verbs, the rulepack itself is enough evidence.
                if (IsThrownRulepack(props))
                    return true;

                // For mods such as VFE Medieval 2, match the real projectile to the weapon verb.
                if (props.defaultProjectile == projectileDef)
                    return true;

                if (NameLooksPhysicalThrow(projectileDef.defName))
                    return true;
            }

            return false;
        }

        internal static bool IsRecoverableDefinition(ThingWithComps source, Verb_LaunchProjectile verb)
        {
            if (source == null || source.def == null || source is Apparel)
                return false;

            ThingDef projectile = null;
            try { projectile = verb?.Projectile; }
            catch { }

            if (projectile != null)
                return IsRecoverableShot(source, projectile);

            return DefLooksRecoverable(source.def);
        }

        private static bool IsPhysicalThrowVerb(ThingDef weaponDef, VerbProperties props)
        {
            if (weaponDef == null || props == null)
                return false;

            Type verbClass = props.verbClass;
            if (verbClass != null && !typeof(Verb_LaunchProjectile).IsAssignableFrom(verbClass))
                return false;

            ThingDef projectile = props.defaultProjectile;
            if (!IsRecoverableProjectile(projectile))
                return false;

            if (IsThrownRulepack(props))
                return true;

            // Some real thrown weapons do not use Combat_RangedFire_Thrown.
            // VFE Medieval 2's VFEM2_ThrowingAxe is a concrete example: Verb_Shoot,
            // no thrown rulepack, but its weapon/projectile defs explicitly describe a thrown object.
            if (NameLooksPhysicalThrow(weaponDef.defName))
                return true;

            if (weaponDef.weaponTags != null &&
                weaponDef.weaponTags.Any(NameLooksPhysicalThrow))
                return true;

            // Conservative fallback: projectile name says it is thrown AND the weapon has melee tools.
            // This catches throwable melee/ranged hybrids without misclassifying bows/guns.
            if (NameLooksPhysicalThrow(projectile.defName) &&
                weaponDef.tools != null && weaponDef.tools.Count > 0)
                return true;

            return false;
        }

        private static bool IsThrownRulepack(VerbProperties props)
        {
            return props?.rangedFireRulepack != null &&
                   string.Equals(props.rangedFireRulepack.defName, ThrownRulePackDefName, StringComparison.Ordinal);
        }

        private static bool NameLooksPhysicalThrow(string value)
        {
            if (string.IsNullOrEmpty(value))
                return false;

            string s = value.ToLowerInvariant();
            return s.Contains("throw") ||
                   s.Contains("javelin") ||
                   s.Contains("pilum") ||
                   s.Contains("tomahawk") ||
                   s.Contains("francisca") ||
                   s.Contains("chakram") ||
                   s.Contains("shuriken") ||
                   s.Contains("kunai");
        }

        internal static bool IsRecoverableProjectile(ThingDef projectileDef)
        {
            if (projectileDef?.projectile == null)
                return false;

            Type projectileClass = projectileDef.thingClass;
            if (projectileClass != null && typeof(Projectile_Explosive).IsAssignableFrom(projectileClass))
                return false;

            if (projectileDef.projectile.explosionRadius > 0.01f)
                return false;

            return true;
        }
    }

    internal sealed class RecoverableRecord : IExposable
    {
        public Projectile projectile;
        public ThingWithComps payload;
        public bool forbidOnRecover;

        public RecoverableRecord() { }

        public RecoverableRecord(Projectile projectile, ThingWithComps payload, bool forbidOnRecover)
        {
            this.projectile = projectile;
            this.payload = payload;
            this.forbidOnRecover = forbidOnRecover;
        }

        public void ExposeData()
        {
            Scribe_References.Look(ref projectile, "projectile");
            Scribe_References.Look(ref payload, "payload");
            Scribe_Values.Look(ref forbidOnRecover, "forbidOnRecover", false);
        }
    }

    internal sealed class RecoverableThrowablesComponent : GameComponent, IThingHolder
    {
        private List<RecoverableRecord> records = new List<RecoverableRecord>();
        private ThingOwner<Thing> payloads;
        private readonly Dictionary<Projectile, RecoverableRecord> byProjectile =
            new Dictionary<Projectile, RecoverableRecord>();

        public RecoverableThrowablesComponent(Game game)
        {
            payloads = new ThingOwner<Thing>(this, false, LookMode.Deep);
        }

        public IThingHolder ParentHolder => null;
        public ThingOwner GetDirectlyHeldThings() => payloads;

        public void GetChildHolders(List<IThingHolder> outChildren)
        {
            if (payloads != null)
                ThingOwnerUtility.AppendThingHoldersFromThings(outChildren, payloads);
        }

        private static RecoverableThrowablesComponent Current
        {
            get
            {
                Game game = Verse.Current.Game;
                return game?.GetComponent<RecoverableThrowablesComponent>();
            }
        }

        public override void ExposeData()
        {
            Scribe_Deep.Look(ref payloads, "recoverableThrowablePayloads", new object[] { this, false, LookMode.Deep });
            Scribe_Collections.Look(ref records, "recoverableThrowables", LookMode.Deep);

            if (payloads == null)
                payloads = new ThingOwner<Thing>(this, false, LookMode.Deep);
            if (records == null)
                records = new List<RecoverableRecord>();

            if (Scribe.mode == LoadSaveMode.PostLoadInit)
                RebuildRuntimeIndex();
        }

        private void RebuildRuntimeIndex()
        {
            byProjectile.Clear();

            for (int i = records.Count - 1; i >= 0; i--)
            {
                RecoverableRecord record = records[i];
                if (record == null || record.payload == null || record.payload.Destroyed)
                {
                    records.RemoveAt(i);
                    continue;
                }

                if (record.projectile == null || record.projectile.Destroyed)
                {
                    if (payloads.Contains(record.payload))
                        payloads.Remove(record.payload);
                    if (!record.payload.Destroyed)
                        record.payload.Destroy(DestroyMode.Vanish);
                    records.RemoveAt(i);
                    continue;
                }

                byProjectile[record.projectile] = record;
            }
        }

        internal static bool IsTracked(Projectile projectile)
        {
            RecoverableThrowablesComponent c = Current;
            return c != null && projectile != null && c.byProjectile.ContainsKey(projectile);
        }

        internal static bool Register(Projectile projectile, ThingWithComps payload, bool forbidOnRecover)
        {
            RecoverableThrowablesComponent c = Current;
            if (c == null || projectile == null || payload == null || payload.Destroyed)
                return false;
            if (c.byProjectile.ContainsKey(projectile))
                return false;

            if (payload.ParentHolder != null)
                payload.ParentHolder.GetDirectlyHeldThings()?.Remove(payload);

            if (!c.payloads.TryAdd(payload, false))
                return false;

            var record = new RecoverableRecord(projectile, payload, forbidOnRecover);
            c.records.Add(record);
            c.byProjectile[projectile] = record;
            return true;
        }

        internal static void RecoverBeforeProjectileDestroy(Projectile projectile)
        {
            RecoverableThrowablesComponent c = Current;
            if (c == null || projectile == null)
                return;
            if (!c.byProjectile.TryGetValue(projectile, out RecoverableRecord record))
                return;

            c.byProjectile.Remove(projectile);
            c.records.Remove(record);

            ThingWithComps payload = record?.payload;
            if (payload == null || payload.Destroyed)
                return;

            Map map = projectile.Map;
            IntVec3 pos = projectile.Position;

            if (c.payloads.Contains(payload))
                c.payloads.Remove(payload);

            if (map == null || !pos.IsValid || !pos.InBounds(map))
            {
                payload.Destroy(DestroyMode.Vanish);
                Log.Warning("[Recoverable Throwables 1.5 v1.3] LOST weapon=" + payload.def.defName + " because projectile left the map.");
                return;
            }

            bool placed = false;
            Thing resultingThing = null;

            try
            {
                placed = GenPlace.TryPlaceThing(
                    payload,
                    pos,
                    map,
                    ThingPlaceMode.Near,
                    out resultingThing,
                    null,
                    null);
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5 v1.3] failed to recover " + payload + ": " + e);
            }

            if (!placed)
            {
                if (!payload.Destroyed)
                    payload.Destroy(DestroyMode.Vanish);
                Log.Error("[Recoverable Throwables 1.5 v1.3] RECOVER FAILED weapon=" +
                    payload.def.defName + " near=" + pos + "; item treated as lost.");
                return;
            }

            Thing recoveredThing = resultingThing ?? payload;
            try { recoveredThing.SetForbidden(record.forbidOnRecover, false); }
            catch { }

            Log.Warning("[Recoverable Throwables 1.5 v1.3] RECOVER weapon=" +
                recoveredThing.def.defName + " at=" + recoveredThing.Position);
        }
    }

    internal static class LaunchHandler
    {
        internal static void Handle(Projectile projectile, Thing launcher, Thing equipment)
        {
            if (projectile == null || projectile.Destroyed)
                return;
            if (RecoverableThrowablesComponent.IsTracked(projectile))
                return;

            Pawn pawn = launcher as Pawn;
            ThingWithComps source = equipment as ThingWithComps;

            // Some modded launch paths do not pass equipment through Projectile.Launch.
            // Fall back to the launcher's currently equipped primary only when it matches
            // this projectile as a physical throwable.
            if (source == null && pawn?.equipment?.Primary != null)
            {
                ThingWithComps primary = pawn.equipment.Primary;
                if (RecoverableClassifier.IsRecoverableShot(primary, projectile.def))
                    source = primary;
            }

            if (source == null || !RecoverableClassifier.IsRecoverableShot(source, projectile.def))
                return;

            if (!(source.ParentHolder is Pawn_EquipmentTracker tracker))
            {
                Log.Warning("[Recoverable Throwables 1.5 v1.3] MATCHED but source is not in Pawn_EquipmentTracker: " +
                    source.def.defName + " holder=" + (source.ParentHolder?.GetType().FullName ?? "<null>"));
                return;
            }

            int before = source.stackCount;
            ThingWithComps thrown = null;

            try
            {
                if (before > 1)
                {
                    thrown = source.SplitOff(1) as ThingWithComps;
                }
                else
                {
                    // Use the equipment tracker API for the last physical item so verb/equipment
                    // notifications remain consistent.
                    tracker.Remove(source);
                    thrown = source;
                }
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5 v1.3] failed to detach thrown weapon " +
                    source.def.defName + ": " + e);
                return;
            }

            if (thrown == null || thrown.Destroyed)
                return;

            bool forbid = pawn?.Faction != null && pawn.Faction != Faction.OfPlayer;

            if (!RecoverableThrowablesComponent.Register(projectile, thrown, forbid))
            {
                Log.Error("[Recoverable Throwables 1.5 v1.3] register failed for " + thrown.def.defName);
                Map fallbackMap = pawn?.Map;
                if (fallbackMap != null && pawn.Position.InBounds(fallbackMap))
                    GenPlace.TryPlaceThing(thrown, pawn.Position, fallbackMap, ThingPlaceMode.Near);
                else if (!thrown.Destroyed)
                    thrown.Destroy(DestroyMode.Vanish);
                return;
            }

            int remaining = source == thrown ? 0 : source.stackCount;
            Log.Warning("[Recoverable Throwables 1.5 v1.3] THROW pawn=" +
                SafePawnLabel(pawn) +
                " weapon=" + thrown.def.defName +
                " before=" + before +
                " remaining=" + remaining +
                " projectile=" + projectile.def.defName);
        }

        private static string SafePawnLabel(Pawn pawn)
        {
            try { return pawn == null ? "<null>" : pawn.LabelShortCap; }
            catch { return pawn?.thingIDNumber.ToString() ?? "<null>"; }
        }
    }

    [HarmonyPatch(typeof(Projectile), nameof(Projectile.Launch),
        new Type[]
        {
            typeof(Thing),
            typeof(Vector3),
            typeof(LocalTargetInfo),
            typeof(LocalTargetInfo),
            typeof(ProjectileHitFlags),
            typeof(Thing),
            typeof(ThingDef)
        })]
    internal static class Patch_Projectile_Launch_Recoverable
    {
        private static void Postfix(Projectile __instance, Thing launcher, Thing equipment)
        {
            LaunchHandler.Handle(__instance, launcher, equipment);
        }
    }

    [HarmonyPatch(typeof(Thing), nameof(Thing.Destroy))]
    internal static class Patch_Thing_Destroy_RecoverThrowable
    {
        private static void Prefix(Thing __instance)
        {
            if (__instance is Projectile projectile)
                RecoverableThrowablesComponent.RecoverBeforeProjectileDestroy(projectile);
        }
    }

    internal static class GrenadeOneUseCompat
    {
        internal static void TryPatch(Harmony harmony)
        {
            try
            {
                Type t = AccessTools.TypeByName(
                    "GrenadeOneUse15.Patch_VerbLaunchProjectile_TryCastShot_ConsumeThrowable");

                if (t == null)
                {
                    Log.Warning("[Recoverable Throwables 1.5 v1.3] Grenade One-Use type not detected.");
                    return;
                }

                MethodInfo grenadePostfix = AccessTools.Method(t, "Postfix");
                if (grenadePostfix != null)
                {
                    harmony.Patch(
                        grenadePostfix,
                        prefix: new HarmonyMethod(
                            typeof(GrenadeOneUseCompat),
                            nameof(GrenadePostfixGuardPrefix)));
                }

                MethodInfo shouldConsume = AccessTools.Method(t, "ShouldConsume");
                if (shouldConsume != null)
                {
                    harmony.Patch(
                        shouldConsume,
                        prefix: new HarmonyMethod(
                            typeof(GrenadeOneUseCompat),
                            nameof(ShouldConsumePrefix)));
                }

                Log.Warning("[Recoverable Throwables 1.5 v1.3] Grenade One-Use compatibility guard installed.");
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5 v1.3] Grenade One-Use compatibility failed: " + e);
            }
        }

        // Patch the old Grenade One-Use postfix itself. If the shot came from a physical
        // recoverable weapon, skip the entire old consumption postfix.
        public static bool GrenadePostfixGuardPrefix(Verb_LaunchProjectile __instance, bool __result)
        {
            if (!__result || __instance == null)
                return true;

            ThingWithComps source = __instance.EquipmentSource;
            return !RecoverableClassifier.IsRecoverableDefinition(source, __instance);
        }

        // Secondary guard for older builds that route through ShouldConsume.
        public static bool ShouldConsumePrefix(
            Verb_LaunchProjectile verb,
            ThingWithComps source,
            ref bool __result)
        {
            if (!RecoverableClassifier.IsRecoverableDefinition(source, verb))
                return true;

            __result = false;
            return false;
        }
    }
}
