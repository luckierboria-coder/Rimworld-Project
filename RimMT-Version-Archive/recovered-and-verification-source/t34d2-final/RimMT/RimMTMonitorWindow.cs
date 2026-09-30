using UnityEngine;
using Verse;

namespace RimMT;

internal sealed class RimMTMonitorWindow : Window
{
	private Vector2 scrollPosition;

	private string cachedText = "RimMT monitor initializing...";

	private int lastRefreshFrame = -1000;

	public override Vector2 InitialSize => new Vector2(760f, 560f);

	internal RimMTMonitorWindow()
		: base((IWindowDrawing)null)
	{
		base.doCloseX = true;
		base.doCloseButton = false;
		base.draggable = true;
		base.resizeable = true;
		base.absorbInputAroundWindow = false;
		base.closeOnClickedOutside = false;
		base.forcePause = false;
	}

	public override void DoWindowContents(Rect inRect)
	{
		//IL_0040: Unknown result type (might be due to invalid IL or missing references)
		//IL_0045: Unknown result type (might be due to invalid IL or missing references)
		//IL_004a: Unknown result type (might be due to invalid IL or missing references)
		//IL_0056: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b2: Unknown result type (might be due to invalid IL or missing references)
		//IL_00b9: Unknown result type (might be due to invalid IL or missing references)
		//IL_00de: Unknown result type (might be due to invalid IL or missing references)
		//IL_00ee: Unknown result type (might be due to invalid IL or missing references)
		if (Time.frameCount - lastRefreshFrame >= 30)
		{
			lastRefreshFrame = Time.frameCount;
			cachedText = RimMTDiagnostics.BuildCompactMonitorText();
		}
		Rect val = new Rect(((Rect)(ref inRect)).x, ((Rect)(ref inRect)).y, ((Rect)(ref inRect)).width, 30f);
		GameFont font = Text.Font;
		Text.Font = (GameFont)1;
		Widgets.Label(val, Translator.Translate("RimMT_RealtimeMonitorTitle"));
		Rect val2 = default(Rect);
		((Rect)(ref val2))._002Ector(((Rect)(ref inRect)).x, ((Rect)(ref inRect)).y + 34f, ((Rect)(ref inRect)).width, ((Rect)(ref inRect)).height - 34f);
		Rect val3 = default(Rect);
		((Rect)(ref val3))._002Ector(0f, 0f, ((Rect)(ref val2)).width - 18f, 1250f);
		Widgets.BeginScrollView(val2, ref scrollPosition, val3, true);
		Text.Font = (GameFont)0;
		Widgets.Label(new Rect(0f, 0f, ((Rect)(ref val3)).width, ((Rect)(ref val3)).height), cachedText);
		Text.Font = font;
		Widgets.EndScrollView();
	}
}
