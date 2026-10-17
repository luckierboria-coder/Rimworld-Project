using UnityEngine;
using Verse;

namespace AllenRX97BridgeDiagnostics
{
  /// <summary>
  /// An in-game Mod Settings entry guarantees a manual snapshot control even if
  /// the developer Debug Actions menu does not register custom actions.
  /// </summary>
  public sealed class BridgeDiagnosticMod : Mod
  {
    public BridgeDiagnosticMod(ModContentPack content) : base(content)
    {
      Log.Message("[RX97Diag] v1.2 mod loaded from " + content.RootDir);
      BridgeDiagnostics.Initialize();
    }

    public override string SettingsCategory() => "RX97 Bridge Diagnostics";

    public override void DoSettingsWindowContents(Rect inRect)
    {
      base.DoSettingsWindowContents(inRect);
      var area = new Rect(inRect.x, inRect.y, inRect.width, 110f);
      Widgets.Label(new Rect(area.x, area.y, area.width, 30f),
        "RX97 Horse Cart - read-only region/path diagnostics (RimWorld 1.6)");
      var button = new Rect(area.x, area.y + 40f, 250f, 36f);
      if (Widgets.ButtonText(button, "Dump vehicle region owners"))
      {
        Log.Message("[RX97Diag] User requested snapshot from Mod Settings");
        BridgeDiagnostics.DumpSnapshot();
      }
      Widgets.Label(new Rect(area.x, area.y + 84f, area.width, 26f),
        "Results appear in Player.log with prefix [RX97Diag].");
    }
  }
}