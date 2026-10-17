using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using LudeonTK;
using Verse;

namespace AllenRX97BridgeDiagnostics
{
  // Diagnostic only. NEVER mutate VF/VMF map pathing or change bridge definitions.
  [StaticConstructorOnStartup]
  internal static class BridgeDiagnostics
  {
    private static int failuresLogged;
    private const string TargetName = "RX97_HorseCart";

    static BridgeDiagnostics()
    {
      try
      {
        var provider = AccessTools.TypeByName("Vehicles.FloatMenuOptionProvider_OrderVehicle");
        var method = provider == null ? null : AccessTools.Method(provider, "VehicleCanGoto");
        if (method == null)
        {
          Log.Warning("[RX97Diag] VF VehicleCanGoto not found. Snapshot debug action remains available.");
          return;
        }
        var harmony = new Harmony("allen.rx97.bridge.diagnostics");
        var postfix = AccessTools.Method(typeof(BridgeDiagnostics), nameof(AfterVehicleCanGoto));
        harmony.Patch(method, postfix: new HarmonyMethod(postfix));
        Log.Message("[RX97Diag] Read-only VehicleCanGoto diagnostic enabled. Use Dev Mode -> Debug actions -> RX97 Bridge -> Dump snapshot.");
      }
      catch (Exception ex)
      {
        Log.Warning("[RX97Diag] Patch unavailable: " + ex);
      }
    }

    // Postfix is read-only. It logs only failed Horse Cart player movement commands.
    private static void AfterVehicleCanGoto(object[] __args, AcceptanceReport __result)
    {
      try
      {
        if (__result.Accepted || failuresLogged >= 30 || __args == null) return;
        var vehicle = __args.OfType<Thing>().FirstOrDefault(t => t.def != null && t.def.defName == TargetName);
        if (vehicle == null) return;
        failuresLogged++;
        Log.Warning("[RX97Diag] Horse Cart movement REJECTED: " + __result +
                    " pos=" + vehicle.Position + " map=" + vehicle.Map +
                    " args=" + string.Join(", ", __args.Select(a => a == null ? "<null>" : a.ToString()).ToArray()));
      }
      catch (Exception ex)
      {
        if (failuresLogged++ < 3) Log.Warning("[RX97Diag] Postfix error: " + ex);
      }
    }

    [DebugAction("RX97 Bridge", "Dump vehicle region owners", allowedGameStates = AllowedGameStates.PlayingOnMap)]
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