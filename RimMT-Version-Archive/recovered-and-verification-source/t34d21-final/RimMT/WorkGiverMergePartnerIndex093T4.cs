using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using RimWorld;
using Verse;
using Verse.AI;

namespace RimMT;

internal static class WorkGiverMergePartnerIndex093T4
{
	private sealed class GroupIndex
	{
		internal readonly Dictionary<StackKey, PartnerStats> ByKey = new Dictionary<StackKey, PartnerStats>();

		internal bool UnsafeCustomSemantics;
	}

	private sealed class PartnerStats
	{
		private Thing first;

		private Thing second;

		private int firstCount = -1;

		private int secondCount = -1;

		internal void Add(Thing thing)
		{
			int stackCount = thing.stackCount;
			if (stackCount > firstCount)
			{
				second = first;
				secondCount = firstCount;
				first = thing;
				firstCount = stackCount;
			}
			else if (stackCount > secondCount)
			{
				second = thing;
				secondCount = stackCount;
			}
		}

		internal bool HasPartnerFor(Thing source)
		{
			if (source == null)
			{
				return true;
			}
			int stackCount = source.stackCount;
			if (first != source)
			{
				if (first != null)
				{
					return firstCount >= stackCount;
				}
				return false;
			}
			if (second != null)
			{
				return secondCount >= stackCount;
			}
			return false;
		}
	}

	private struct StackKey : IEquatable<StackKey>
	{
		private readonly ThingDef def;

		private readonly ThingDef stuff;

		internal StackKey(ThingDef def, ThingDef stuff)
		{
			this.def = def;
			this.stuff = stuff;
		}

		public bool Equals(StackKey other)
		{
			if (def == other.def)
			{
				return stuff == other.stuff;
			}
			return false;
		}

		public override bool Equals(object obj)
		{
			if (obj is StackKey)
			{
				return Equals((StackKey)obj);
			}
			return false;
		}

		public override int GetHashCode()
		{
			return (((def != null) ? ((Def)def).shortHash : 0) * 397) ^ ((stuff != null) ? ((Def)stuff).shortHash : 0);
		}
	}

	private const string HarmonyOwner = "allen.rimmt";

	private const string DiagnosticsHarmonyOwner = "allen.rimmt.diagnostics";

	private const int AuthorityRecheckMask = 4095;

	[ThreadStatic]
	private static long scopeStamp;

	[ThreadStatic]
	private static Dictionary<ISlotGroup, GroupIndex> groupCache;

	private static MethodInfo target;

	private static MethodInfo thingCanStack;

	private static MethodInfo thingWithCompsCanStack;

	private static MethodInfo minifiedCanStack;

	private static readonly Dictionary<Type, bool> SupportedTypeCache = new Dictionary<Type, bool>();

	private static int authorityState;

	private static bool installed;

	private static long calls;

	private static long inScopeCalls;

	private static long groupBuilds;

	private static long groupHeldScans;

	private static long negativeRejects;

	private static long survivorCalls;

	private static long unsupportedGroupBypass;

	private static long unsupportedCandidateBypass;

	private static long foreignPatchBypass;

	private static long commonSenseCompatibleCalls;

	private static long commonSenseIngestibleBypass;

	private static long authorityTargetForeign;

	private static long authorityThingWithCompsForeign;

	private static long authorityMinifiedForeign;

	private static long authorityThingUnsafe;

	private static long forcedBypass;

	private static long invalidBypass;

	private static long failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_0123: Unknown result type (might be due to invalid IL or missing references)
		//IL_0129: Expected O, but got Unknown
		if (harmony == null)
		{
			return;
		}
		try
		{
			target = AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing", new Type[3]
			{
				typeof(Pawn),
				typeof(Thing),
				typeof(bool)
			}, (Type[])null);
			thingCanStack = AccessTools.Method(typeof(Thing), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null);
			thingWithCompsCanStack = AccessTools.Method(typeof(ThingWithComps), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null);
			minifiedCanStack = AccessTools.Method(typeof(MinifiedThing), "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null);
			if (target == null || thingCanStack == null || thingWithCompsCanStack == null || minifiedCanStack == null)
			{
				Log.Warning("[RimMT] T4 HaulMerge partner index not installed: required Vanilla method missing.");
				return;
			}
			HarmonyMethod val = new HarmonyMethod(typeof(WorkGiverMergePartnerIndex093T4), "Prefix", (Type[])null);
			val.priority = 950;
			harmony.Patch((MethodBase)target, val, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = true;
			Log.Message("[RimMT] T4 HaulMerge package-local partner index installed. Proven negatives skip only impossible merge candidates; survivors remain Vanilla-authoritative.");
		}
		catch (Exception ex)
		{
			installed = false;
			Log.Warning("[RimMT] T4 HaulMerge partner index failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix(Pawn pawn, Thing t, bool forced, ref Job __result)
	{
		//IL_0026: Unknown result type (might be due to invalid IL or missing references)
		//IL_002c: Invalid comparison between Unknown and I4
		calls++;
		if (forced)
		{
			forcedBypass++;
			return true;
		}
		if (!RimMTThreadGuard.IsMainThread || (int)Current.ProgramState != 2 || !JobGiverGlobalNearest04181.InJobGiverScope)
		{
			invalidBypass++;
			return true;
		}
		inScopeCalls++;
		int num = AuthorityMode();
		if (num < 0)
		{
			foreignPatchBypass++;
			return true;
		}
		if (num == 2)
		{
			if (t != null && t.def != null && t.def.IsIngestible)
			{
				commonSenseIngestibleBypass++;
				return true;
			}
			commonSenseCompatibleCalls++;
		}
		if (t == null || t.Destroyed || t.def == null || t.stackCount <= 0 || t.stackCount >= t.def.stackLimit)
		{
			invalidBypass++;
			return true;
		}
		if (!UsesSupportedVanillaStackSemantics(t))
		{
			unsupportedCandidateBypass++;
			return true;
		}
		try
		{
			ISlotGroup slotGroup = (ISlotGroup)(object)StoreUtility.GetSlotGroup(t);
			if (slotGroup == null)
			{
				invalidBypass++;
				return true;
			}
			ISlotGroup storageGroup = (ISlotGroup)(object)slotGroup.StorageGroup;
			ISlotGroup val = storageGroup ?? slotGroup;
			long currentScopeStartTicks = JobGiverGlobalNearest04181.CurrentScopeStartTicks;
			if (currentScopeStartTicks <= 0)
			{
				return true;
			}
			EnsureScope(currentScopeStartTicks);
			if (!groupCache.TryGetValue(val, out var value))
			{
				value = BuildGroupIndex(val);
				groupCache[val] = value;
			}
			if (value == null || value.UnsafeCustomSemantics)
			{
				unsupportedGroupBypass++;
				return true;
			}
			StackKey key = new StackKey(t.def, t.Stuff);
			if (!value.ByKey.TryGetValue(key, out var value2) || value2 == null || !value2.HasPartnerFor(t))
			{
				negativeRejects++;
				__result = null;
				return false;
			}
			survivorCalls++;
			return true;
		}
		catch
		{
			failures++;
			return true;
		}
	}

	private static void EnsureScope(long stamp)
	{
		if (groupCache == null)
		{
			groupCache = new Dictionary<ISlotGroup, GroupIndex>();
		}
		if (scopeStamp != stamp)
		{
			scopeStamp = stamp;
			groupCache.Clear();
		}
	}

	private static GroupIndex BuildGroupIndex(ISlotGroup group)
	{
		groupBuilds++;
		GroupIndex groupIndex = new GroupIndex();
		try
		{
			foreach (Thing heldThing in group.HeldThings)
			{
				groupHeldScans++;
				if (heldThing != null && !heldThing.Destroyed && heldThing.def != null && heldThing.stackCount > 0 && heldThing.stackCount < heldThing.def.stackLimit)
				{
					if (!UsesSupportedVanillaStackSemantics(heldThing))
					{
						groupIndex.UnsafeCustomSemantics = true;
						return groupIndex;
					}
					StackKey key = new StackKey(heldThing.def, heldThing.Stuff);
					if (!groupIndex.ByKey.TryGetValue(key, out var value))
					{
						value = new PartnerStats();
						groupIndex.ByKey[key] = value;
					}
					value.Add(heldThing);
				}
			}
		}
		catch
		{
			groupIndex.UnsafeCustomSemantics = true;
			failures++;
		}
		return groupIndex;
	}

	private static bool UsesSupportedVanillaStackSemantics(Thing thing)
	{
		if (thing == null)
		{
			return false;
		}
		Type type = ((object)thing).GetType();
		if (SupportedTypeCache.TryGetValue(type, out var value))
		{
			return value;
		}
		bool flag = false;
		try
		{
			MethodInfo methodInfo = AccessTools.Method(type, "CanStackWith", new Type[1] { typeof(Thing) }, (Type[])null);
			Type type2 = ((methodInfo == null) ? null : methodInfo.DeclaringType);
			flag = type2 == typeof(Thing) || type2 == typeof(ThingWithComps) || type2 == typeof(MinifiedThing);
		}
		catch
		{
			flag = false;
		}
		SupportedTypeCache[type] = flag;
		return flag;
	}

	private static int AuthorityMode()
	{
		long num = calls;
		if (authorityState != 0 && (num & 0xFFF) != 1)
		{
			return authorityState;
		}
		try
		{
			bool num2 = HasForeignPatch(target);
			bool flag = HasForeignPatch(thingWithCompsCanStack);
			bool flag2 = HasForeignPatch(minifiedCanStack);
			int num3 = ThingCanStackAuthorityMode();
			if (num2)
			{
				Interlocked.Increment(ref authorityTargetForeign);
			}
			if (flag)
			{
				Interlocked.Increment(ref authorityThingWithCompsForeign);
			}
			if (flag2)
			{
				Interlocked.Increment(ref authorityMinifiedForeign);
			}
			if (num3 < 0)
			{
				Interlocked.Increment(ref authorityThingUnsafe);
			}
			if (num2 || flag || flag2 || num3 < 0)
			{
				authorityState = -1;
				return authorityState;
			}
			authorityState = num3;
			return authorityState;
		}
		catch
		{
			authorityState = -1;
			return authorityState;
		}
	}

	private static int ThingCanStackAuthorityMode()
	{
		if (thingCanStack == null)
		{
			return -1;
		}
		Patches patchInfo = Harmony.GetPatchInfo((MethodBase)thingCanStack);
		if (patchInfo == null)
		{
			return 1;
		}
		if (HasForeign(patchInfo.Prefixes) || HasForeign(patchInfo.Transpilers) || HasForeign(patchInfo.Finalizers))
		{
			return -1;
		}
		if (patchInfo.Postfixes == null)
		{
			return 1;
		}
		bool flag = false;
		foreach (Patch postfix in patchInfo.Postfixes)
		{
			if (postfix != null && !string.Equals(postfix.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				MethodInfo patchMethod = postfix.PatchMethod;
				string a = ((patchMethod == null || patchMethod.DeclaringType == null) ? null : patchMethod.DeclaringType.FullName);
				if (!string.Equals(postfix.owner, "net.avilmask.rimworld.mod.CommonSense", StringComparison.Ordinal) || !string.Equals(a, "CommonSense.CompIngredients_CanStackWith_CommonSensePatch", StringComparison.Ordinal) || !string.Equals(patchMethod.Name, "Postfix", StringComparison.Ordinal))
				{
					return -1;
				}
				flag = true;
			}
		}
		if (!flag)
		{
			return 1;
		}
		return 2;
	}

	private static bool HasForeignPatch(MethodBase method)
	{
		if (method == null)
		{
			return true;
		}
		Patches patchInfo = Harmony.GetPatchInfo(method);
		if (patchInfo == null)
		{
			return false;
		}
		if (!HasForeign(patchInfo.Prefixes) && !HasForeign(patchInfo.Postfixes) && !HasForeign(patchInfo.Transpilers))
		{
			return HasForeign(patchInfo.Finalizers);
		}
		return true;
	}

	private static bool HasForeign(IEnumerable<Patch> patches)
	{
		if (patches == null)
		{
			return false;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal) && !string.Equals(patch.owner, "allen.rimmt.diagnostics", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	internal static string Summary()
	{
		return "T4 HaulMerge partner index: installed=" + installed + ", authorityMode=" + authorityState + ", calls=" + calls + ", inScope=" + inScopeCalls + ", groupBuilds=" + groupBuilds + ", groupHeldScans=" + groupHeldScans + ", negativeRejects=" + negativeRejects + ", survivors=" + survivorCalls + ", rejectRate=" + ((inScopeCalls == 0L) ? "0.00" : ((double)negativeRejects * 100.0 / (double)inScopeCalls).ToString("F2")) + "%, unsupportedGroupBypass=" + unsupportedGroupBypass + ", unsupportedCandidateBypass=" + unsupportedCandidateBypass + ", foreignPatchBypass=" + foreignPatchBypass + ", commonSenseCompatibleCalls=" + commonSenseCompatibleCalls + ", commonSenseIngestibleBypass=" + commonSenseIngestibleBypass + ", authorityForeign[target/thingWithComps/minified/thingUnsafe]=" + authorityTargetForeign + "/" + authorityThingWithCompsForeign + "/" + authorityMinifiedForeign + "/" + authorityThingUnsafe + ", forcedBypass=" + forcedBypass + ", invalidBypass=" + invalidBypass + ", failures=" + failures + ". Cache lifetime=one synchronous JobGiver_Work package; negative proof=same def/stuff + other partial stack count >= source; survivors run original JobOnThing.";
	}
}
