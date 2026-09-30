using System;
using System.Collections.Generic;
using UnityEngine;
using Verse;

namespace RimMT;

internal static class TextMetricCache
{
	private struct HeightKey : IEquatable<HeightKey>
	{
		private readonly string text;

		private readonly float width;

		private readonly GameFont font;

		private readonly bool wordWrap;

		internal HeightKey(string text, float width, GameFont font, bool wordWrap)
		{
			//IL_0018: Unknown result type (might be due to invalid IL or missing references)
			//IL_0019: Unknown result type (might be due to invalid IL or missing references)
			this.text = text ?? string.Empty;
			this.width = width;
			this.font = font;
			this.wordWrap = wordWrap;
		}

		public bool Equals(HeightKey other)
		{
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			if (width.Equals(other.width) && font == other.font && wordWrap == other.wordWrap)
			{
				return string.Equals(text, other.text, StringComparison.Ordinal);
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is HeightKey)
			{
				return Equals((HeightKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0027: Unknown result type (might be due to invalid IL or missing references)
			//IL_002c: Unknown result type (might be due to invalid IL or missing references)
			//IL_0032: Unknown result type (might be due to invalid IL or missing references)
			//IL_0041: Unknown result type (might be due to invalid IL or missing references)
			//IL_0043: Expected I4, but got Unknown
			return (((((text.GetHashCode() * 397) ^ width.GetHashCode()) * 397) ^ font) * 397) ^ wordWrap.GetHashCode();
		}
	}

	private struct SizeKey : IEquatable<SizeKey>
	{
		private readonly string text;

		private readonly GameFont font;

		internal SizeKey(string text, GameFont font)
		{
			//IL_0011: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			this.text = text ?? string.Empty;
			this.font = font;
		}

		public bool Equals(SizeKey other)
		{
			//IL_0001: Unknown result type (might be due to invalid IL or missing references)
			//IL_0007: Unknown result type (might be due to invalid IL or missing references)
			if (font == other.font)
			{
				return string.Equals(text, other.text, StringComparison.Ordinal);
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is SizeKey)
			{
				return Equals((SizeKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0017: Unknown result type (might be due to invalid IL or missing references)
			//IL_0019: Expected I4, but got Unknown
			return (text.GetHashCode() * 397) ^ font;
		}
	}

	private const int MaxEntries = 8192;

	private static readonly object Sync = new object();

	private static readonly Dictionary<HeightKey, float> Heights = new Dictionary<HeightKey, float>();

	private static readonly Dictionary<SizeKey, Vector2> Sizes = new Dictionary<SizeKey, Vector2>();

	private static long hits;

	private static long misses;

	internal static long Hits
	{
		get
		{
			lock (Sync)
			{
				return hits;
			}
		}
	}

	internal static long Misses
	{
		get
		{
			lock (Sync)
			{
				return misses;
			}
		}
	}

	public static bool CalcHeightPrefix(string text, float width, ref float __result, ref bool __state)
	{
		//IL_0015: Unknown result type (might be due to invalid IL or missing references)
		__state = false;
		if (!FeatureGate.IsEnabled("ui.textCache"))
		{
			return true;
		}
		HeightKey key = new HeightKey(text, width, Text.Font, Text.WordWrap);
		lock (Sync)
		{
			if (Heights.TryGetValue(key, out __result))
			{
				hits++;
				__state = true;
				return false;
			}
			misses++;
		}
		return true;
	}

	public static void CalcHeightPostfix(string text, float width, float __result, bool __state)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		if (__state || !FeatureGate.IsEnabled("ui.textCache"))
		{
			return;
		}
		HeightKey key = new HeightKey(text, width, Text.Font, Text.WordWrap);
		lock (Sync)
		{
			if (Heights.Count >= 8192)
			{
				Heights.Clear();
			}
			Heights[key] = __result;
		}
	}

	public static bool CalcSizePrefix(string text, ref Vector2 __result, ref bool __state)
	{
		//IL_0014: Unknown result type (might be due to invalid IL or missing references)
		__state = false;
		if (!FeatureGate.IsEnabled("ui.textCache"))
		{
			return true;
		}
		SizeKey key = new SizeKey(text, Text.Font);
		lock (Sync)
		{
			if (Sizes.TryGetValue(key, out __result))
			{
				hits++;
				__state = true;
				return false;
			}
			misses++;
		}
		return true;
	}

	public static void CalcSizePostfix(string text, Vector2 __result, bool __state)
	{
		//IL_0013: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		if (__state || !FeatureGate.IsEnabled("ui.textCache"))
		{
			return;
		}
		SizeKey key = new SizeKey(text, Text.Font);
		lock (Sync)
		{
			if (Sizes.Count >= 8192)
			{
				Sizes.Clear();
			}
			Sizes[key] = __result;
		}
	}
}
