using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using HarmonyLib;
using RimWorld;
using Verse;

namespace RimMT;

internal static class HaulMergePatchCensus093T5
{
	private sealed class TargetSpec
	{
		internal readonly string Name;

		internal readonly MethodBase Method;

		internal TargetSpec(string name, MethodBase method)
		{
			Name = name;
			Method = method;
		}
	}

	private const string RimMTOwner = "allen.rimmt";

	private static bool scheduled;

	internal static void Apply()
	{
		if (scheduled)
		{
			return;
		}
		scheduled = true;
		try
		{
			LongEventHandler.ExecuteWhenFinished((Action)delegate
			{
				try
				{
					Log.Message("[RimMT] T5 HaulMerge Harmony patch census (post-load, measurement-only):\n" + DetailedSummary());
				}
				catch (Exception ex2)
				{
					Log.Warning("[RimMT] T5 HaulMerge patch census failed: " + ex2.GetType().Name + ": " + ex2.Message);
				}
			});
		}
		catch (Exception ex)
		{
			Log.Warning("[RimMT] T5 HaulMerge patch census scheduling failed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	internal static string DetailedSummary()
	{
		try
		{
			TargetSpec[] array = ResolveTargets();
			StringBuilder stringBuilder = new StringBuilder(2048);
			int num = 0;
			int num2 = 0;
			int num3 = 0;
			for (int i = 0; i < array.Length; i++)
			{
				bool blocked;
				int total;
				int foreign;
				string value = DescribeTarget(array[i], out blocked, out total, out foreign);
				num += total;
				num2 += foreign;
				if (blocked)
				{
					num3++;
				}
				if (i != 0)
				{
					stringBuilder.AppendLine();
				}
				stringBuilder.Append(value);
			}
			stringBuilder.Insert(0, "T5 HaulMerge patch census: blocked=" + (num3 != 0) + ", blockedTargets=" + num3 + "/" + array.Length + ", totalPatches=" + num + ", foreignPatches=" + num2 + ". T4 remains unchanged and still fails open on any foreign patch.\n");
			return stringBuilder.ToString();
		}
		catch (Exception ex)
		{
			return "T5 HaulMerge patch census failed: " + ex.GetType().Name + ": " + ex.Message;
		}
	}

	private static TargetSpec[] ResolveTargets()
	{
		Type[] array = new Type[3]
		{
			typeof(Pawn),
			typeof(Thing),
			typeof(bool)
		};
		Type[] array2 = new Type[1] { typeof(Thing) };
		return new TargetSpec[4]
		{
			new TargetSpec("WorkGiver_Merge.JobOnThing", AccessTools.Method(typeof(WorkGiver_Merge), "JobOnThing", array, (Type[])null)),
			new TargetSpec("Thing.CanStackWith", AccessTools.Method(typeof(Thing), "CanStackWith", array2, (Type[])null)),
			new TargetSpec("ThingWithComps.CanStackWith", AccessTools.Method(typeof(ThingWithComps), "CanStackWith", array2, (Type[])null)),
			new TargetSpec("MinifiedThing.CanStackWith", AccessTools.Method(typeof(MinifiedThing), "CanStackWith", array2, (Type[])null))
		};
	}

	private static string DescribeTarget(TargetSpec spec, out bool blocked, out int total, out int foreign)
	{
		blocked = false;
		total = 0;
		foreign = 0;
		StringBuilder stringBuilder = new StringBuilder(512);
		stringBuilder.Append(" - ").Append(spec.Name).Append(": ");
		if (spec.Method == null)
		{
			blocked = true;
			stringBuilder.Append("MISSING [BLOCKER]");
			return stringBuilder.ToString();
		}
		Patches patchInfo = Harmony.GetPatchInfo(spec.Method);
		if (patchInfo == null)
		{
			stringBuilder.Append("no patches; foreign=0; blocker=False");
			return stringBuilder.ToString();
		}
		List<string> list = new List<string>();
		AddEntries(list, "Prefix", patchInfo.Prefixes, ref total, ref foreign);
		AddEntries(list, "Postfix", patchInfo.Postfixes, ref total, ref foreign);
		AddEntries(list, "Transpiler", patchInfo.Transpilers, ref total, ref foreign);
		AddEntries(list, "Finalizer", patchInfo.Finalizers, ref total, ref foreign);
		blocked = foreign != 0;
		stringBuilder.Append("patches=").Append(total).Append(", foreign=")
			.Append(foreign)
			.Append(", blocker=")
			.Append(blocked);
		if (list.Count == 0)
		{
			stringBuilder.Append("; none");
		}
		else
		{
			for (int i = 0; i < list.Count; i++)
			{
				stringBuilder.Append("\n    ").Append(list[i]);
			}
		}
		return stringBuilder.ToString();
	}

	private static void AddEntries(List<string> entries, string kind, IEnumerable<Patch> patches, ref int total, ref int foreign)
	{
		if (patches == null)
		{
			return;
		}
		foreach (Patch patch in patches)
		{
			if (patch != null)
			{
				total++;
				bool flag = !string.Equals(patch.owner, "allen.rimmt", StringComparison.Ordinal);
				if (flag)
				{
					foreign++;
				}
				MethodInfo patchMethod = patch.PatchMethod;
				string text = ((patchMethod == null) ? "<unknown>" : (((patchMethod.DeclaringType == null) ? "<global>" : patchMethod.DeclaringType.FullName) + "." + patchMethod.Name));
				string[] obj = new string[8]
				{
					kind,
					" owner=",
					patch.owner ?? "<null>",
					", priority=",
					null,
					null,
					null,
					null
				};
				int priority = patch.priority;
				obj[4] = priority.ToString();
				obj[5] = ", method=";
				obj[6] = text;
				obj[7] = (flag ? " [FOREIGN/BLOCKER]" : " [RimMT]");
				entries.Add(string.Concat(obj));
			}
		}
	}
}
