using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using UnityEngine;
using Verse;

namespace Allen.RecoverableThrowables15
{
    [StaticConstructorOnStartup]
    internal static class Bootstrap
    {
        private const string HarmonyId = "allen.recoverablethrowables.1.5";

        static Bootstrap()
        {
            try
            {
                var harmony = new Harmony(HarmonyId);
                harmony.PatchAll(Assembly.GetExecutingAssembly());
                GrenadeOneUseCompat.TryPatch(harmony);

                LongEventHandler.ExecuteWhenFinished(LogEligibleDefs);
                Log.Message("[Recoverable Throwables 1.5] loaded. Physical no-ammo thrown weapons are consumed one-at-a-time and recovered from the projectile landing point.");
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5] startup failed: " + e);
            }
        }

        private static void LogEligibleDefs()
        {
            try
            {
                var eligible = DefDatabase<ThingDef>.AllDefsListForReading
                    .Where(RecoverableClassifier.DefLooksRecoverable)
                    .Select(d => d.defName)
                    .OrderBy(x => x)
                    .ToList();

                Log.Message("[Recoverable Throwables 1.5] eligible defs=" + eligible.Count +
                    (eligible.Count == 0 ? "." : " [" + string.Join(", ", eligible) + "]"));
            }
            catch (Exception e)
            {
                Log.Warning("[Recoverable Throwables 1.5] eligible-def audit failed: " + e.Message);
            }
        }
    }

    internal static class LtsAmmoGuard
    {
        private static readonly Type AmmoLogic = AccessTools.TypeByName("Ammunition.Logic.AmmoLogic");
        private static readonly Type Settings = AccessTools.TypeByName("Ammunition.Settings.Settings");
        private static readonly MethodInfo CanUseAmmo = AmmoLogic == null
            ? null
            : AccessTools.Method(AmmoLogic, "WeaponDefCanUseAmmoDef");
        private static readonly PropertyInfo AvailableAmmoProperty = Settings == null
            ? null
            : AccessTools.Property(Settings, "AvailableAmmo");

        private static readonly Dictionary<ThingDef, bool> Cache = new Dictionary<ThingDef, bool>();

        internal static bool LtsPresent => AmmoLogic != null && Settings != null && CanUseAmmo != null;

        internal static bool RequiresAmmo(ThingDef weaponDef)
        {
            if (weaponDef == null || !LtsPresent)
                return false;

            if (Cache.TryGetValue(weaponDef, out bool cached))
                return cached;

            bool result = false;
            try
            {
                IEnumerable available = AvailableAmmoProperty?.GetValue(null, null) as IEnumerable;
                if (available != null)
                {
                    foreach (object obj in available)
                    {
                        ThingDef ammo = obj as ThingDef;
                        if (ammo == null)
                            continue;

                        try
                        {
                            if ((bool)CanUseAmmo.Invoke(null, new object[] { weaponDef, ammo }))
                            {
                                result = true;
                                break;
                            }
                        }
                        catch
                        {
                            // Ignore one malformed ammo entry; do not disable the whole guard.
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Recoverable Throwables 1.5] LTS ammo check failed for " +
                    weaponDef.defName + ": " + e.Message + ". Treating it as ammo-managed for safety.");
                result = true;
            }

            Cache[weaponDef] = result;
            return result;
        }
    }

    internal static class RecoverableClassifier
    {
        internal const string ThrownRulePackDefName = "Combat_RangedFire_Thrown";

        internal static bool DefLooksRecoverable(ThingDef weaponDef)
        {
            if (weaponDef == null || !weaponDef.IsWeapon || weaponDef.IsApparel)
                return false;
            if (LtsAmmoGuard.RequiresAmmo(weaponDef))
                return false;
            if (weaponDef.Verbs == null)
                return false;

            for (int i = 0; i < weaponDef.Verbs.Count; i++)
            {
                VerbProperties props = weaponDef.Verbs[i];
                if (!IsThrownVerb(props))
                    continue;
                if (IsRecoverableProjectile(props.defaultProjectile))
                    return true;
            }

            return false;
        }

        internal static bool IsRecoverableShot(ThingWithComps source, ThingDef projectileDef)
        {
            if (source == null || source.Destroyed || source.stackCount <= 0)
                return false;
            if (source is Apparel)
                return false;
            if (source.def == null || !source.def.IsWeapon || source.def.IsApparel)
                return false;
            if (LtsAmmoGuard.RequiresAmmo(source.def))
                return false;
            if (!HasThrownProjectileVerb(source.def))
                return false;

            return IsRecoverableProjectile(projectileDef);
        }

        internal static bool IsRecoverableDefinition(ThingWithComps source, Verb_LaunchProjectile verb)
        {
            if (source == null || source.def == null || source is Apparel)
                return false;
            if (!source.def.IsWeapon || source.def.IsApparel)
                return false;
            if (LtsAmmoGuard.RequiresAmmo(source.def))
                return false;
            if (!HasThrownProjectileVerb(source.def))
                return false;

            ThingDef projectile = null;
            try { projectile = verb?.Projectile; }
            catch { }

            if (projectile == null && source.def.Verbs != null)
            {
                for (int i = 0; i < source.def.Verbs.Count; i++)
                {
                    VerbProperties props = source.def.Verbs[i];
                    if (IsThrownVerb(props) && IsRecoverableProjectile(props.defaultProjectile))
                    {
                        projectile = props.defaultProjectile;
                        break;
                    }
                }
            }

            return IsRecoverableProjectile(projectile);
        }

        private static bool HasThrownProjectileVerb(ThingDef weaponDef)
        {
            if (weaponDef?.Verbs == null)
                return false;

            for (int i = 0; i < weaponDef.Verbs.Count; i++)
            {
                if (IsThrownVerb(weaponDef.Verbs[i]))
                    return true;
            }
            return false;
        }

        private static bool IsThrownVerb(VerbProperties props)
        {
            if (props == null || props.verbClass == null)
                return false;
            if (!typeof(Verb_LaunchProjectile).IsAssignableFrom(props.verbClass))
                return false;
            return props.rangedFireRulepack != null &&
                   string.Equals(props.rangedFireRulepack.defName, ThrownRulePackDefName, StringComparison.Ordinal);
        }

        internal static bool IsRecoverableProjectile(ThingDef projectileDef)
        {
            if (projectileDef?.projectile == null)
                return false;

            Type projectileClass = projectileDef.thingClass;
            if (projectileClass != null && typeof(Projectile_Explosive).IsAssignableFrom(projectileClass))
                return false;

            // Grenades, molotovs, EMP, smoke/gas and similar area-effect throwables stay one-use.
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
                    // The projectile is no longer in the save. The physical weapon cannot be
                    // assigned a reliable landing cell, so treat it as lost rather than duplicating it.
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
                // A throwable that leaves the map is physically lost.
                payload.Destroy(DestroyMode.Vanish);
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
                Log.Error("[Recoverable Throwables 1.5] failed to recover " + payload + ": " + e);
            }

            if (!placed)
            {
                if (!payload.Destroyed)
                    payload.Destroy(DestroyMode.Vanish);
                Log.Warning("[Recoverable Throwables 1.5] could not place recovered throwable near " +
                    pos + "; item was treated as lost.");
                return;
            }

            try
            {
                (resultingThing ?? payload).SetForbidden(record.forbidOnRecover, false);
            }
            catch
            {
                // Not every modded weapon necessarily has a forbiddable comp.
            }
        }
    }

    internal sealed class PendingLaunch
    {
        public Projectile projectile;
        public ThingWithComps source;
        public bool forbidOnRecover;
    }

    internal static class LaunchCapture
    {
        [ThreadStatic]
        private static PendingLaunch pending;

        internal static void Clear()
        {
            pending = null;
        }

        internal static void Capture(Projectile projectile, Thing launcher, Thing equipment)
        {
            ThingWithComps source = equipment as ThingWithComps;
            if (projectile == null || source == null)
                return;

            if (!RecoverableClassifier.IsRecoverableShot(source, projectile.def))
                return;

            // Only physical equipment currently held by a pawn is consumed.
            // This excludes turrets, buildings and ghost references to a weapon that has
            // already been thrown as the last item in its stack.
            if (!(source.ParentHolder is Pawn_EquipmentTracker))
                return;

            Pawn pawn = launcher as Pawn;
            bool forbid = pawn?.Faction != null && pawn.Faction != Faction.OfPlayer;

            pending = new PendingLaunch
            {
                projectile = projectile,
                source = source,
                forbidOnRecover = forbid
            };
        }

        internal static void Commit(Verb_LaunchProjectile verb, bool shotSucceeded)
        {
            PendingLaunch launch = pending;
            pending = null;

            if (!shotSucceeded || launch == null || launch.projectile == null || launch.source == null)
                return;
            if (launch.projectile.Destroyed || RecoverableThrowablesComponent.IsTracked(launch.projectile))
                return;

            // Require the same physical equipment object that performed the shot.
            if (verb?.EquipmentSource != launch.source)
                return;
            if (!(launch.source.ParentHolder is Pawn_EquipmentTracker))
                return;
            if (launch.source.Destroyed || launch.source.stackCount <= 0)
                return;

            ThingWithComps thrown = null;
            try
            {
                thrown = launch.source.SplitOff(1) as ThingWithComps;
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5] failed to detach one thrown weapon from stack: " + e);
                return;
            }

            if (thrown == null || thrown.Destroyed)
                return;

            if (!RecoverableThrowablesComponent.Register(
                launch.projectile,
                thrown,
                launch.forbidOnRecover))
            {
                // Registration should normally be impossible to fail. If it does,
                // place the detached item by the shooter instead of deleting it.
                Pawn pawn = verb?.CasterPawn;
                Map map = pawn?.Map;
                if (map != null && pawn.Position.InBounds(map))
                    GenPlace.TryPlaceThing(thrown, pawn.Position, map, ThingPlaceMode.Near);
                else if (!thrown.Destroyed)
                    thrown.Destroy(DestroyMode.Vanish);
            }
        }
    }

    [HarmonyPatch(typeof(Verb_LaunchProjectile), "TryCastShot")]
    internal static class Patch_VerbLaunchProjectile_TryCastShot
    {
        private static bool Prefix(Verb_LaunchProjectile __instance, ref bool __result)
        {
            LaunchCapture.Clear();

            // Safety for the final item of a burst: once the weapon has physically left the
            // equipment tracker, do not allow the old Verb instance to launch ghost copies.
            ThingWithComps source = __instance?.EquipmentSource;
            if (source != null &&
                RecoverableClassifier.IsRecoverableDefinition(source, __instance) &&
                __instance.CasterPawn != null &&
                !(source.ParentHolder is Pawn_EquipmentTracker))
            {
                __result = false;
                return false;
            }

            return true;
        }

        private static void Postfix(Verb_LaunchProjectile __instance, bool __result)
        {
            LaunchCapture.Commit(__instance, __result);
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
    internal static class Patch_Projectile_Launch_CaptureRecoverable
    {
        private static void Postfix(Projectile __instance, Thing launcher, Thing equipment)
        {
            LaunchCapture.Capture(__instance, launcher, equipment);
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
                MethodInfo target = t == null ? null : AccessTools.Method(t, "ShouldConsume");

                if (target == null)
                {
                    Log.Message("[Recoverable Throwables 1.5] Grenade One-Use not detected; no compatibility patch needed.");
                    return;
                }

                MethodInfo prefix = AccessTools.Method(typeof(GrenadeOneUseCompat), nameof(ShouldConsumePrefix));
                harmony.Patch(target, prefix: new HarmonyMethod(prefix));
                Log.Message("[Recoverable Throwables 1.5] Grenade One-Use compatibility active; recoverable physical weapons are excluded from grenade consumption.");
            }
            catch (Exception e)
            {
                Log.Error("[Recoverable Throwables 1.5] Grenade One-Use compatibility failed: " + e);
            }
        }

        // Target signature:
        // private static bool ShouldConsume(Verb_LaunchProjectile verb, ThingWithComps source)
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
