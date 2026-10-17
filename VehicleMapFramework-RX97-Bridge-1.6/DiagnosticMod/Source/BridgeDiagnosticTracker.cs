using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Verse;

namespace AllenRX97BridgeDiagnostics
{
  // Passive sampler. The only per-tick operation is one modulo check;
  // reflection and path state observation occur once per 60 in-game ticks.
  // No VehiclePathFollower.PatherTick Harmony patch is installed.
  internal sealed class BridgeDiagnosticTracker : GameComponent
  {
    private sealed class Tracked
    {
      public Thing Vehicle;
      public int LastOrderTick;
      public int LastPositionChangeTick;
      public int LastLogTick;
      public int LogCount;
      public IntVec3 LastPosition;
      public string LastState;
    }

    private static readonly List<Tracked> Watches = new List<Tracked>();
    private static int lastTick;
    private static int sampleErrors;

    public BridgeDiagnosticTracker(Game game) { }

    internal static void Watch(Thing vehicle)
    {
      try
      {
        if (vehicle == null || vehicle.def?.defName != "RX97_HorseCart") return;
        int now = Find.TickManager?.TicksGame ?? 0;
        for (int i = 0; i < Watches.Count; i++)
        {
          if (ReferenceEquals(Watches[i].Vehicle, vehicle))
          {
            Watches[i].LastOrderTick = now;
            return;
          }
        }
        if (Watches.Count >= 5) Watches.RemoveAt(0);
        Watches.Add(new Tracked
        {
          Vehicle = vehicle,
          LastOrderTick = now,
          LastPositionChangeTick = now,
          LastPosition = vehicle.Position,
          LastLogTick = -10000,
          LastState = ""
        });
        Log.Message("[RX97Diag] v1.3 monitoring Horse Cart pos=" + vehicle.Position +
          " for 3600 game ticks after last StartPath");
      }
      catch (Exception ex) { Log.Warning("[RX97Diag] watch setup failed: " + ex); }
    }

    public override void GameComponentTick()
    {
      int tick = Find.TickManager?.TicksGame ?? 0;
      if (tick == lastTick || tick % 60 != 0 || Watches.Count == 0) return;
      lastTick = tick;
      for (int i = Watches.Count - 1; i >= 0; i--)
      {
        Tracked watch = Watches[i];
        if (watch.Vehicle == null || !watch.Vehicle.Spawned ||
            tick - watch.LastOrderTick > 3600)
        {
          Watches.RemoveAt(i);
          continue;
        }
        try
        {
          var vehicle = watch.Vehicle;
          if (vehicle.Position != watch.LastPosition)
          {
            watch.LastPositionChangeTick = tick;
            watch.LastPosition = vehicle.Position;
          }
          string state = Describe(vehicle, tick - watch.LastPositionChangeTick);
          bool changed = state != watch.LastState;
          // Log changes immediately, otherwise once every 5 seconds.
          if ((changed || tick - watch.LastLogTick >= 300) && watch.LogCount < 90)
          {
            watch.LastState = state;
            watch.LastLogTick = tick;
            watch.LogCount++;
            Log.Message("[RX97Diag] v1.3 sample tick=" + tick +
              " sinceMove=" + (tick - watch.LastPositionChangeTick) +
              " sinceOrder=" + (tick - watch.LastOrderTick) + " " + state);
          }
        }
        catch (Exception ex)
        {
          if (sampleErrors++ < 4)
            Log.Warning("[RX97Diag] tracking sample failed: " + ex);
        }
      }
    }

    internal static void DumpSelected()
    {
      try
      {
        Thing vehicle = Find.Selector?.SingleSelectedThing;
        if (vehicle?.def?.defName != "RX97_HorseCart")
        {
          foreach (Tracked watch in Watches)
          {
            if (watch.Vehicle != null && watch.Vehicle.Spawned)
            {
              vehicle = watch.Vehicle;
              break;
            }
          }
        }
        if (vehicle?.def?.defName == "RX97_HorseCart")
          Log.Message("[RX97Diag] v1.3 MANUAL follower state: " + Describe(vehicle, -1));
        else
          Log.Message("[RX97Diag] v1.3 MANUAL follower state: no selected/tracked Horse Cart");
      }
      catch (Exception ex) { Log.Warning("[RX97Diag] manual follower dump failed: " + ex); }
    }

    private static string Describe(Thing vehicle, int sinceMove)
    {
      object follower = GetMember(vehicle, "vehiclePather");
      object path = GetMember(follower, "curPath");
      object nextObj = GetMember(follower, "nextCell");
      IntVec3 next = nextObj is IntVec3 cell ? cell : IntVec3.Invalid;
      string nextTerrain = "<invalid>";
      string nextThings = "<invalid>";
      var map = vehicle.Map;
      if (map != null && next.IsValid && next.InBounds(map))
      {
        var terrain = map.terrainGrid.TerrainAt(next);
        nextTerrain = terrain?.defName ?? "<null>";
        List<Thing> things = map.thingGrid.ThingsListAt(next);
        nextThings = things == null ? "<none>" :
          string.Join(",", things.ConvertAll(t => t?.def?.defName ?? "?").ToArray());
        if (nextThings.Length > 130) nextThings = nextThings.Substring(0, 130) + "...";
      }
      return "def=" + vehicle.def.defName + " pos=" + vehicle.Position +
        " next=" + next + " dest=" + GetMember(follower, "Destination") +
        " moving=" + GetMember(follower, "Moving") +
        " waiting=" + GetMember(follower, "Waiting") +
        " drafted=" + GetMember(vehicle, "Drafted") +
        " request=" + GetMember(follower, "RequestStatus") +
        " pathFound=" + GetMember(path, "Found") +
        " pathNodesLeft=" + GetMember(path, "NodesLeft") +
        " nextCellCostLeft=" + GetMember(follower, "nextCellCostLeft") +
        " waitTicks=" + GetMember(follower, "waitTicks") +
        " nextTerrain=" + nextTerrain + " nextThings=" + nextThings +
        (sinceMove >= 0 ? " unchangedTicks=" + sinceMove : "");
    }

    private static object GetMember(object obj, string name)
    {
      if (obj == null) return "<null>";
      try
      {
        Type t = obj.GetType();
        PropertyInfo p = AccessTools.Property(t, name);
        if (p != null) return p.GetValue(obj, null) ?? "<null>";
        FieldInfo f = AccessTools.Field(t, name);
        return f?.GetValue(obj) ?? "<no-member-or-null>";
      }
      catch (Exception ex) { return "<error " + ex.GetType().Name + ">"; }
    }
  }
}
