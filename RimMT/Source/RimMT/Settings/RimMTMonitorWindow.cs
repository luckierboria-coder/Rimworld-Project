using UnityEngine;
using Verse;

namespace RimMT
{
    /// <summary>
    /// Optional, user-opened monitor. It is not installed as a resident Harmony overlay and is
    /// closed by default. Text is rebuilt only every 30 rendered frames so observing RimMT does
    /// not become a new hot path.
    /// </summary>
    internal sealed class RimMTMonitorWindow : Window
    {
        private Vector2 scrollPosition;
        private string cachedText = "RimMT monitor initializing...";
        private int lastRefreshFrame = -1000;

        public override Vector2 InitialSize { get { return new Vector2(760f, 560f); } }

        internal RimMTMonitorWindow()
        {
            doCloseX = true;
            doCloseButton = false;
            draggable = true;
            resizeable = true;
            absorbInputAroundWindow = false;
            closeOnClickedOutside = false;
            forcePause = false;
        }

        public override void DoWindowContents(Rect inRect)
        {
            if (Time.frameCount - lastRefreshFrame >= 30)
            {
                lastRefreshFrame = Time.frameCount;
                cachedText = RimMTDiagnostics.BuildCompactMonitorText();
            }

            Rect titleRect = new Rect(inRect.x, inRect.y, inRect.width, 30f);
            GameFont oldFont = Text.Font;
            Text.Font = GameFont.Small;
            Widgets.Label(titleRect, "RimMT_RealtimeMonitorTitle".Translate());

            Rect outRect = new Rect(inRect.x, inRect.y + 34f, inRect.width, inRect.height - 34f);
            Rect viewRect = new Rect(0f, 0f, outRect.width - 18f, 1250f);
            Widgets.BeginScrollView(outRect, ref scrollPosition, viewRect);
            Text.Font = GameFont.Tiny;
            Widgets.Label(new Rect(0f, 0f, viewRect.width, viewRect.height), cachedText);
            Text.Font = oldFont;
            Widgets.EndScrollView();
        }
    }
}
