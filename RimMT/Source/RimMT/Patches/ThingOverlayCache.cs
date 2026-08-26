using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimMT
{
    internal static class ThingOverlayCache
    {
        private static readonly List<Thing> Visible = new List<Thing>(128);
        private static Map cachedMap;
        private static CellRect cachedView;
        private static int lastRefreshFrame = -100000;
        private static long sourceScans;
        private static long cachedFrames;

        internal static long SourceScans { get { return sourceScans; } }
        internal static long CachedFrames { get { return cachedFrames; } }

        public static bool Prefix()
        {
            if (!FeatureGate.IsEnabled("ui.overlayCache")) return true;
            if (Event.current.type != EventType.Repaint) return false;

            Map map = Find.CurrentMap;
            if (map == null) return false;
            CellRect view = Find.CameraDriver.CurrentViewRect;
            int refreshFrames = RimMTMod.Settings == null ? 30 : RimMTMod.Settings.OverlayRefreshFrames;
            bool refresh = cachedMap != map || !cachedView.Equals(view) || Time.frameCount - lastRefreshFrame >= refreshFrames;

            if (refresh)
            {
                Visible.Clear();
                List<Thing> source = map.listerThings.ThingsInGroup(ThingRequestGroup.HasGUIOverlay);
                for (int i = 0; i < source.Count; i++)
                {
                    Thing thing = source[i];
                    if (thing != null && thing.Spawned && thing.Map == map && view.Contains(thing.Position))
                        Visible.Add(thing);
                }
                cachedMap = map;
                cachedView = view;
                lastRefreshFrame = Time.frameCount;
                sourceScans++;
            }
            else
            {
                cachedFrames++;
            }

            for (int i = 0; i < Visible.Count; i++)
            {
                Thing thing = Visible[i];
                if (thing == null || !thing.Spawned || thing.Map != map || !view.Contains(thing.Position)) continue;
                if (map.fogGrid.IsFogged(thing.Position)) continue;
                try
                {
                    thing.DrawGUIOverlay();
                }
                catch (Exception ex)
                {
                    Log.Error("[RimMT] Exception drawing ThingOverlay for " + thing + ": " + ex);
                }
            }
            return false;
        }
    }
}
