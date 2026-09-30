using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class ReachabilityNoCache
{
	private struct ReachKey : IEquatable<ReachKey>
	{
		private readonly int reachabilityId;

		private readonly IntVec3 start;

		private readonly IntVec3 dest;

		private readonly PathEndMode mode;

		private readonly int parmsHash;

		private readonly int generation;

		internal unsafe ReachKey(Reachability reachability, IntVec3 start, IntVec3 dest, PathEndMode mode, TraverseParms parms, int generation)
		{
			//IL_000d: Unknown result type (might be due to invalid IL or missing references)
			//IL_000e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0014: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			//IL_001b: Unknown result type (might be due to invalid IL or missing references)
			//IL_001d: Unknown result type (might be due to invalid IL or missing references)
			reachabilityId = RuntimeHelpers.GetHashCode(reachability);
			this.start = start;
			this.dest = dest;
			this.mode = mode;
			parmsHash = ((object)(*(TraverseParms*)(&parms))/*cast due to .constrained prefix*/).GetHashCode();
			this.generation = generation;
		}

		public bool Equals(ReachKey other)
		{
			//IL_000f: Unknown result type (might be due to invalid IL or missing references)
			//IL_0015: Unknown result type (might be due to invalid IL or missing references)
			//IL_0022: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_0035: Unknown result type (might be due to invalid IL or missing references)
			//IL_003b: Unknown result type (might be due to invalid IL or missing references)
			if (reachabilityId == other.reachabilityId && start == other.start && dest == other.dest && mode == other.mode && parmsHash == other.parmsHash)
			{
				return generation == other.generation;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is ReachKey)
			{
				return Equals((ReachKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			//IL_000d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0012: Unknown result type (might be due to invalid IL or missing references)
			//IL_0028: Unknown result type (might be due to invalid IL or missing references)
			//IL_002d: Unknown result type (might be due to invalid IL or missing references)
			//IL_0043: Unknown result type (might be due to invalid IL or missing references)
			//IL_0048: Unknown result type (might be due to invalid IL or missing references)
			//IL_004e: Unknown result type (might be due to invalid IL or missing references)
			//IL_0055: Unknown result type (might be due to invalid IL or missing references)
			//IL_005b: Unknown result type (might be due to invalid IL or missing references)
			//IL_0062: Unknown result type (might be due to invalid IL or missing references)
			//IL_0064: Expected I4, but got Unknown
			return (((((((((reachabilityId * 397) ^ ((object)start/*cast due to .constrained prefix*/).GetHashCode()) * 397) ^ ((object)dest/*cast due to .constrained prefix*/).GetHashCode()) * 397) ^ mode) * 397) ^ parmsHash) * 397) ^ generation;
		}
	}

	private const int MaxEntries = 8192;

	private static readonly object Sync = new object();

	private static readonly Dictionary<ReachKey, int> NoUntilTick = new Dictionary<ReachKey, int>();

	private static long hits;

	private static long stores;

	private static int topologyGeneration;

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

	internal static long Stores
	{
		get
		{
			lock (Sync)
			{
				return stores;
			}
		}
	}

	internal static int TopologyGeneration => Volatile.Read(ref topologyGeneration);

	internal static void InvalidateTopology()
	{
		Interlocked.Increment(ref topologyGeneration);
	}

	public static bool Prefix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, ref bool __result, ref bool __state)
	{
		//IL_002e: Unknown result type (might be due to invalid IL or missing references)
		//IL_0031: Unknown result type (might be due to invalid IL or missing references)
		//IL_0036: Unknown result type (might be due to invalid IL or missing references)
		//IL_0037: Unknown result type (might be due to invalid IL or missing references)
		__state = false;
		if (!FeatureGate.IsEnabled("ai.reachNoCache") || Find.TickManager == null || !((LocalTargetInfo)(ref dest)).IsValid || ((LocalTargetInfo)(ref dest)).HasThing)
		{
			return true;
		}
		ReachKey key = new ReachKey(__instance, start, ((LocalTargetInfo)(ref dest)).Cell, peMode, traverseParams, TopologyGeneration);
		int ticksGame = Find.TickManager.TicksGame;
		lock (Sync)
		{
			if (NoUntilTick.TryGetValue(key, out var value))
			{
				if (ticksGame <= value)
				{
					hits++;
					__result = false;
					__state = true;
					return false;
				}
				NoUntilTick.Remove(key);
			}
		}
		return true;
	}

	public static void Postfix(Reachability __instance, IntVec3 start, LocalTargetInfo dest, PathEndMode peMode, TraverseParms traverseParams, bool __result, bool __state)
	{
		//IL_0046: Unknown result type (might be due to invalid IL or missing references)
		//IL_0049: Unknown result type (might be due to invalid IL or missing references)
		//IL_004e: Unknown result type (might be due to invalid IL or missing references)
		//IL_004f: Unknown result type (might be due to invalid IL or missing references)
		if (__state || __result || !FeatureGate.IsEnabled("ai.reachNoCache") || Find.TickManager == null || !((LocalTargetInfo)(ref dest)).IsValid || ((LocalTargetInfo)(ref dest)).HasThing)
		{
			return;
		}
		int num = ((RimMTMod.Settings == null) ? 20 : RimMTMod.Settings.ReachNoCacheTtl);
		ReachKey key = new ReachKey(__instance, start, ((LocalTargetInfo)(ref dest)).Cell, peMode, traverseParams, TopologyGeneration);
		lock (Sync)
		{
			if (NoUntilTick.Count >= 8192)
			{
				NoUntilTick.Clear();
			}
			NoUntilTick[key] = Find.TickManager.TicksGame + num;
			stores++;
		}
	}
}
