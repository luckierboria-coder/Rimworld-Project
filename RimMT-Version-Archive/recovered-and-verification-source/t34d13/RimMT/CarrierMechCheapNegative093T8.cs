using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class CarrierMechCheapNegative093T8
{
	private sealed class SlowDetermineStats
	{
		internal long Calls;

		internal long Rejects;

		internal double MaxMs;
	}

	private const string HarmonyOwner = "allen.rimmt";

	private const int AuthorityRecheckMask = 1023;

	private const int MaxSlowDetermineKeys = 24;

	private static readonly Type[] JobArgs = new Type[3]
	{
		typeof(Pawn),
		typeof(Thing),
		typeof(bool)
	};

	private static readonly MethodInfo HaulToCarrierHasJob = AccessTools.Method(typeof(WorkGiver_HaulResourcesToCarrier), "HasJobOnThing", JobArgs, (Type[])null);

	private static readonly MethodInfo HaulToCarrierJobOnThing = AccessTools.Method(typeof(WorkGiver_HaulResourcesToCarrier), "JobOnThing", JobArgs, (Type[])null);

	private static readonly MethodInfo HaulMechToChargerHasJob = AccessTools.Method(typeof(WorkGiver_HaulMechToCharger), "HasJobOnThing", JobArgs, (Type[])null);

	private static readonly MethodInfo HaulMechToChargerJobOnThing = AccessTools.Method(typeof(WorkGiver_HaulMechToCharger), "JobOnThing", JobArgs, (Type[])null);

	private static readonly Dictionary<string, SlowDetermineStats> SlowDetermines = new Dictionary<string, SlowDetermineStats>();

	private static int carrierAuthorityState;

	private static int chargerAuthorityState;

	private static long prepareCalls;

	private static long scannerUnresolved;

	private static long carrierPrepares;

	private static long carrierAuthorityBypass;

	private static long carrierChecks;

	private static long carrierRejects;

	private static long carrierSurvivors;

	private static long chargerPrepares;

	private static long chargerAuthorityBypass;

	private static long chargerChecks;

	private static long chargerRejects;

	private static long chargerSurvivors;

	private static long failures;

	private static long slowDetermine20;

	private static long slowDetermineWithHeavyEvidence;

	private static long slowDetermineWithoutHeavyEvidence;

	internal static bool TryPrepare(WorkGiver_Scanner scanner, out CarrierPrunerKind093T8 kind)
	{
		kind = CarrierPrunerKind093T8.None;
		prepareCalls++;
		try
		{
			if (scanner == null)
			{
				scannerUnresolved++;
				return false;
			}
			Type type = ((object)scanner).GetType();
			string a = ((((WorkGiver)scanner).def == null) ? null : ((Def)((WorkGiver)scanner).def).defName);
			if (type == typeof(WorkGiver_HaulResourcesToCarrier) && string.Equals(a, "HaulToCarrier", StringComparison.Ordinal))
			{
				carrierPrepares++;
				if (!AuthoritySafe(CarrierPrunerKind093T8.HaulToCarrier))
				{
					carrierAuthorityBypass++;
					return false;
				}
				kind = CarrierPrunerKind093T8.HaulToCarrier;
				return true;
			}
			if (type == typeof(WorkGiver_HaulMechToCharger) && string.Equals(a, "HaulMechsToCharger", StringComparison.Ordinal))
			{
				chargerPrepares++;
				if (!AuthoritySafe(CarrierPrunerKind093T8.HaulMechsToCharger))
				{
					chargerAuthorityBypass++;
					return false;
				}
				kind = CarrierPrunerKind093T8.HaulMechsToCharger;
				return true;
			}
		}
		catch
		{
			failures++;
		}
		return false;
	}

	internal static bool Reject(CarrierPrunerKind093T8 kind, Pawn worker, Thing thing)
	{
		try
		{
			Pawn val = (Pawn)(object)((thing is Pawn) ? thing : null);
			if (val == null || worker == null)
			{
				return false;
			}
			switch (kind)
			{
			case CarrierPrunerKind093T8.HaulToCarrier:
			{
				carrierChecks++;
				if (!val.IsColonyMech || !((Thing)val).Spawned || val.Downed)
				{
					carrierRejects++;
					return true;
				}
				CompMechCarrier comp = ((ThingWithComps)val).GetComp<CompMechCarrier>();
				if (comp == null || comp.AmountToAutofill <= 0)
				{
					carrierRejects++;
					return true;
				}
				carrierSurvivors++;
				return false;
			}
			case CarrierPrunerKind093T8.HaulMechsToCharger:
				chargerChecks++;
				if (val.RaceProps == null || !val.RaceProps.IsMechanoid || !val.IsColonyMech)
				{
					chargerRejects++;
					return true;
				}
				if (val.needs != null && val.needs.energy != null && !val.Downed && !val.needs.energy.IsLowEnergySelfShutdown)
				{
					chargerRejects++;
					return true;
				}
				if (val.CurJobDef == JobDefOf.MechCharge)
				{
					chargerRejects++;
					return true;
				}
				chargerSurvivors++;
				return false;
			}
		}
		catch
		{
			failures++;
		}
		return false;
	}

	internal static void RecordSlowDetermine(string workGiver, int rejects, double elapsedMs)
	{
		slowDetermine20++;
		if (string.IsNullOrEmpty(workGiver) || rejects <= 0)
		{
			slowDetermineWithoutHeavyEvidence++;
			return;
		}
		slowDetermineWithHeavyEvidence++;
		if (SlowDetermines.Count < 24 || SlowDetermines.ContainsKey(workGiver))
		{
			if (!SlowDetermines.TryGetValue(workGiver, out var value))
			{
				value = new SlowDetermineStats();
				SlowDetermines[workGiver] = value;
			}
			value.Calls++;
			value.Rejects += rejects;
			if (elapsedMs > value.MaxMs)
			{
				value.MaxMs = elapsedMs;
			}
		}
	}

	private static bool AuthoritySafe(CarrierPrunerKind093T8 kind)
	{
		switch (kind)
		{
		case CarrierPrunerKind093T8.HaulToCarrier:
		{
			long num2 = carrierPrepares;
			if (carrierAuthorityState != 0 && (num2 & 0x3FF) != 1)
			{
				return carrierAuthorityState > 0;
			}
			carrierAuthorityState = ((!HasForeignPatch(HaulToCarrierHasJob) && !HasForeignPatch(HaulToCarrierJobOnThing)) ? 1 : (-1));
			return carrierAuthorityState > 0;
		}
		case CarrierPrunerKind093T8.HaulMechsToCharger:
		{
			long num = chargerPrepares;
			if (chargerAuthorityState != 0 && (num & 0x3FF) != 1)
			{
				return chargerAuthorityState > 0;
			}
			chargerAuthorityState = ((!HasForeignPatch(HaulMechToChargerHasJob) && !HasForeignPatch(HaulMechToChargerJobOnThing)) ? 1 : (-1));
			return chargerAuthorityState > 0;
		}
		default:
			return false;
		}
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
			if (patch != null && !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal))
			{
				return true;
			}
		}
		return false;
	}

	internal static string Summary()
	{
		return "T8 carrier/mech cheap-negative (T5 baseline): prepares=" + prepareCalls + ", scannerUnresolved=" + scannerUnresolved + "; HaulToCarrier[authoritySafe=" + (carrierAuthorityState > 0) + ", prepares=" + carrierPrepares + ", authorityBypass=" + carrierAuthorityBypass + ", checks=" + carrierChecks + ", rejects=" + carrierRejects + ", survivors=" + carrierSurvivors + ", rejectRate=" + Rate(carrierRejects, carrierChecks) + "%]; HaulMechsToCharger[authoritySafe=" + (chargerAuthorityState > 0) + ", prepares=" + chargerPrepares + ", authorityBypass=" + chargerAuthorityBypass + ", checks=" + chargerChecks + ", rejects=" + chargerRejects + ", survivors=" + chargerSurvivors + ", rejectRate=" + Rate(chargerRejects, chargerChecks) + "%]; failures=" + failures + ". S4-only after the existing 32ms tail threshold; no query-time collection/index build; survivors run original live validator.";
	}

	internal static string SlowDetermineSummary()
	{
		return "T8 sampled DetermineNextJob >=20ms WorkGiver evidence: calls=" + slowDetermine20 + ", withHeavyS4Evidence=" + slowDetermineWithHeavyEvidence + ", withoutHeavyS4Evidence=" + slowDetermineWithoutHeavyEvidence + ", top=" + BuildTopSlowDetermineSummary() + ". Bounded to existing T2 deep windows; selects the >=64-reject S4 WorkGiver with the most rejects inside that DetermineNextJob call.";
	}

	private static string BuildTopSlowDetermineSummary()
	{
		if (SlowDetermines.Count == 0)
		{
			return "none";
		}
		List<KeyValuePair<string, SlowDetermineStats>> list = new List<KeyValuePair<string, SlowDetermineStats>>(SlowDetermines);
		list.Sort(delegate(KeyValuePair<string, SlowDetermineStats> a, KeyValuePair<string, SlowDetermineStats> b)
		{
			int num3 = b.Value.Calls.CompareTo(a.Value.Calls);
			return (num3 != 0) ? num3 : b.Value.Rejects.CompareTo(a.Value.Rejects);
		});
		int num = Math.Min(8, list.Count);
		string text = string.Empty;
		for (int num2 = 0; num2 < num; num2++)
		{
			if (num2 != 0)
			{
				text += "; ";
			}
			SlowDetermineStats value = list[num2].Value;
			text = text + list[num2].Key + "(calls=" + value.Calls + ",rejects=" + value.Rejects + ",maxMs=" + value.MaxMs.ToString("F2") + ")";
		}
		return text;
	}

	private static string Rate(long numerator, long denominator)
	{
		if (denominator > 0)
		{
			return ((double)numerator * 100.0 / (double)denominator).ToString("F2");
		}
		return "0.00";
	}
}
