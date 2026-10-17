using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;
using Verse.AI;

namespace AllenRX97BridgeDiagnostics
{
  // Diagnostic only. NEVER mutate VF/VMF map pathing or change bridge definitions.
  public static class BridgeDiagnostics
  {
    private static int failuresLogged;
    private static int acceptedOrdersLogged;
    private static int startPathsLogged;
    private static int patherFailuresLogged;
    private static bool snapshotOnFailureDone;
    private static bool initialized;
    private const string TargetName = "RX97_HorseCart";

    public static void Initialize()
    {
      if (initialized) return;
      initialized = true;
      try
      {
        var provider = AccessTools.TypeByName("Vehicles.FloatMenuOptionProvider_OrderVehicle");
        var method = provider == null ? null : AccessTools.Method(provider, "VehicleCanGoto");
        if (method == null)
        {
          Log.Warning("[RX97Diag] VF VehicleCanGoto not found; use Mod Settings -> RX97 Bridge Diagnostics -> Dump snapshot.");
          return;
        }
        var harmony = new Harmony("allen.rx97.bridge.diagnostics");
        var postfix = AccessTools.Method(typeof(BridgeDiagnostics), nameof(AfterVehicleCanGoto));
        harmony.Patch(method, postfix: new HarmonyMethod(postfix));
        // Unlike VehicleCanGoto, these hooks capture failures AFTER a move was accepted.
        // They are called per order/path failure, not per pawn tick; all logs are capped.
        var followerType = AccessTools.TypeByName("Vehicles.VehiclePathFollower");
        var startPath = followerType == null ? null : AccessTools.Method(followerType, "StartPath");
        var patherFailed = followerType == null ? null : AccessTools.Method(followerType, "PatherFailed");
        if (startPath != null)
          harmony.Patch(startPath, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(BridgeDiagnostics), nameof(BeforeStartPath))));
        else
          Log.Warning("[RX97Diag] StartPath hook unavailable");
        if (patherFailed != null)
          harmony.Patch(patherFailed, prefix: new HarmonyMethod(
            AccessTools.Method(typeof(BridgeDiagnostics), nameof(BeforePatherFailed))));
        else
          Log.Warning("[RX97Diag] PatherFailed hook unavailable");
        Log.Message("[RX97Diag] v1.2 read-only hooks active: VehicleCanGoto, StartPath, PatherFailed; button: Mod Settings -> RX97 Bridge Diagnostics.");
      }
      catch (Exception ex)
      {
        Log.Warning("[RX97Diag] Patch unavailable: " + ex);
      }
    }

    // Postfix is read-only, captures both ACCEPTED and REJECTED Horse Cart click orders.
    private static void AfterVehicleCanGoto(object[] __args, AcceptanceReport __result)
    {
      try
      {
        if (__args == null) return;
        var vehicle = __args.OfType<Thing>().FirstOrDefault(t => IsHorseCart(t));
        if (vehicle == null) return;
        if (__result.Accepted)
        {
          if (acceptedOrdersLogged++ < 25)
            Log.Message("[RX97Diag] Horse Cart VehicleCanGoto ACCEPTED pos=" + vehicle.Position +
              " args=" + ShortArgs(__args));
        }
        else
        {
          if (failuresLogged++ >= 30) return;
          Log.Warning("[RX97Diag] Horse Cart VehicleCanGoto REJECTED: " + __result +
            " pos=" + vehicle.Position + " args=" + ShortArgs(__args));
          if (!snapshotOnFailureDone)
          {
            snapshotOnFailureDone = true;
            Log.Message("[RX97Diag] Automatic snapshot from first REJECTED move");
            DumpSnapshot();
          }
        }
      }
      catch (Exception ex)
      {
        if (failuresLogged++ < 3) Log.Warning("[RX97Diag] VehicleCanGoto logging failed: " + ex);
      }
    }

    // Only happens when a path order is issued, never on the per-tick hot path.
    private static void BeforeStartPath(object __instance, LocalTargetInfo dest,
      PathEndMode peMode, bool ignoreReachability)
    {
      try
      {
        if (startPathsLogged >= 30) return;
        var vehicle = HorseCartForFollower(__instance);
        if (vehicle == null) return;
        startPathsLogged++;
        Log.Message("[RX97Diag] Horse Cart StartPath pos=" + vehicle.Position +
          " dest=" + dest + " peMode=" + peMode + " ignoreReachability=" + ignoreReachability +
          " moving=" + ReadProperty(__instance, "Moving") +
          " requestStatus=" + ReadProperty(__instance, "RequestStatus"));
      }
      catch (Exception ex)
      {
        if (startPathsLogged++ < 3) Log.Warning("[RX97Diag] StartPath logging failed: " + ex);
      }
    }

    private static void BeforePatherFailed(object __instance)
    {
      try
      {
        if (patherFailuresLogged >= 20) return;
        var vehicle = HorseCartForFollower(__instance);
        if (vehicle == null) return;
        patherFailuresLogged++;
        Log.Warning("[RX97Diag] Horse Cart PatherFailed pos=" + vehicle.Position +
          " destination=" + ReadProperty(__instance, "Destination") +
          " moving=" + ReadProperty(__instance, "Moving") +
          " requestStatus=" + ReadProperty(__instance, "RequestStatus") +
          " stack=" + new System.Diagnostics.StackTrace(1, false));
        if (!snapshotOnFailureDone)
        {
          snapshotOnFailureDone = true;
          Log.Message("[RX97Diag] Automatic snapshot from first PatherFailed");
          DumpSnapshot();
        }
      }
      catch (Exception ex)
      {
        if (patherFailuresLogged++ < 3) Log.Warning("[RX97Diag] PatherFailed logging failed: " + ex);
      }
    }

    private static Thing HorseCartForFollower(object follower)
    {
      if (follower == null) return null;
      var f = AccessTools.Field(follower.GetType(), "vehicle");
      var thing = f == null ? null : f.GetValue(follower) as Thing;
      return IsHorseCart(thing) ? thing : null;
    }

    private static bool IsHorseCart(Thing thing)
    {
      return thing?.def?.defName == TargetName;
    }

    private static string ShortArgs(object[] args)
    {
      return string.Join(", ", args.Select(a => a == null ? "<null>" : a.ToString()).ToArray());
    }

    [DebugAction("RX97 Bridge", "Dump vehicle region owners", actionType = DebugActionType.Action, allowedGameStates = AllowedGameStates.PlayingOnMap)]
    public static void DumpSnapshot()
    {
      try
      {
        var map = Find.CurrentMap;
        if (map == null) { Log.Warning("[RX97Diag] No active map"); return; }
        Log.Message("[RX97Diag] Snapshot BEGIN; map=" + map);
        var selected = Find.Selector.SingleSelectedThing;
        Log.Message("[RX97Diag] selected=" + (selected == null ? "<none>" :
            selected.def.defName + " pos=" + selected.Position + " spawned=" + selected.Spawned));
        var carts = map.listerThings.AllThings
          .Where(t => t.def != null && (t.def.defName == TargetName || t.def.defName == "RX97_Animal_Cart"))
          .Take(30).ToList();
        Log.Message("[RX97Diag] main-map carts=" + carts.Count + " " +
            string.Join("; ", carts.Select(t => t.def.defName + " at " + t.Position).ToArray()));

        var compsField = AccessTools.Field(typeof(Map), "components");
        var components = compsField == null ? null : compsField.GetValue(map) as IEnumerable;
        object mapping = null;
        if (components != null)
        {
          foreach (var component in components)
          {
            if (component != null && component.GetType().FullName == "Vehicles.VehiclePathingSystem")
            {
              mapping = component;
              break;
            }
          }
        }
        if (mapping == null) { Log.Warning("[RX97Diag] VehiclePathingSystem not located on this map"); return; }
        var gridOwners = ReadProperty(mapping, "GridOwners");
        Log.Message("[RX97Diag] GridOwners=" + (gridOwners == null ? "<null>" : gridOwners.GetType().FullName));
        var vehicleType = AccessTools.TypeByName("Vehicles.VehicleDef");
        if (vehicleType == null) { Log.Warning("[RX97Diag] Vehicles.VehicleDef not found"); return; }

        var db = typeof(DefDatabase<>).MakeGenericType(vehicleType);
        var listProp = db.GetProperty("AllDefsListForReading", BindingFlags.Public | BindingFlags.Static);
        var defs = listProp == null ? null : listProp.GetValue(null, null) as IEnumerable;
        if (defs == null) { Log.Warning("[RX97Diag] VF def list unavailable"); return; }

        var getter = gridOwners == null ? null : gridOwners.GetType().GetMethod(
          "GetOwner", BindingFlags.Public | BindingFlags.Instance, null, new[] { vehicleType }, null);
        var indexer = mapping.GetType().GetProperty("Item", BindingFlags.Public | BindingFlags.Instance,
          null, null, new[] { vehicleType }, null);

        foreach (var def in defs)
        {
          var d = def as Def;
          if (d == null || (d.defName != TargetName && d.defName != "RX97_Animal_Cart")) continue;
          string size = def is ThingDef td ? td.size.ToString() : "?";
          object owner = getter == null ? null : getter.Invoke(gridOwners, new[] { def });
          object pathData = indexer == null ? null : indexer.GetValue(mapping, new[] { def });
          var dirtyer = ReadProperty(pathData, "VehicleRegionDirtyer");
          var grid = ReadProperty(pathData, "VehiclePathGrid");
          Log.Message("[RX97Diag] def=" + d.defName +
            " size=" + size + " owner=" + ((owner as Def)?.defName ?? "<null>") +
            " pathDataSuspended=" + ReadProperty(pathData, "Suspended") +
            " regionDirty=" + ReadProperty(dirtyer, "AnyDirty") +
            " gridEnabled=" + ReadProperty(grid, "Enabled"));
        }

        foreach (var mod in LoadedModManager.RunningModsListForReading)
        {
          string id = mod.PackageId ?? "";
          if (id.IndexOf("rx97", StringComparison.OrdinalIgnoreCase) >= 0 ||
              id.IndexOf("vehicle", StringComparison.OrdinalIgnoreCase) >= 0)
            Log.Message("[RX97Diag] active-mod=" + mod.Name + " [" + id + "]");
        }
        Log.Message("[RX97Diag] Snapshot END");
      }
      catch (Exception ex)
      {
        Log.Warning("[RX97Diag] Snapshot failed: " + ex);
      }
    }

    private static object ReadProperty(object obj, string name)
    {
      if (obj == null) return null;
      try
      {
        var prop = AccessTools.Property(obj.GetType(), name);
        return prop == null ? null : prop.GetValue(obj, null);
      }
      catch (Exception ex) { return "<read-error " + ex.GetType().Name + ">"; }
    }
  }
}