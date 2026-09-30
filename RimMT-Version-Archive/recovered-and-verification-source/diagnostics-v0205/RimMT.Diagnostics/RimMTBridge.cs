using System;
using System.Reflection;
using System.Text;

namespace RimMT.Diagnostics;

internal static class RimMTBridge
{
	private static readonly string[] SummaryTypes = new string[20]
	{
		"RimMT.AggressiveParallelScanner093T34D", "RimMT.HaulToInventoryParallelEligibility093T34C11", "RimMT.TargetCountParallelFabric093T34C10", "RimMT.DoBillParallelReadinessFabric093T34C9", "RimMT.RootFrameStallCensus093T34C8", "RimMT.CandidateClassificationFabric093T34C", "RimMT.ScannerParallelFabric093T34B", "RimMT.CandidateFabric093T34A", "RimMT.PersistentMapSearchFabric", "RimMT.GlobalHaulAccelerator",
		"RimMT.JobSearchPackageContext093T28", "RimMT.ReservationTransaction093T32A", "RimMT.JobSearchTransaction093T20", "RimMT.GenClosestTransactionIndex093T22", "RimMT.SimulationEpochCoordinator093T26", "RimMT.WorkGiverParallelSafety093T27_2", "RimMT.JobGiverSlowSearch0419S", "RimMT.DoBillTailFabric092", "RimMT.PersistentDoBillIndex092", "RimMT.WorkGiverMergePartnerIndex093T4"
	};

	internal static string BuildSummary()
	{
		StringBuilder stringBuilder = new StringBuilder(12288);
		Assembly assembly = null;
		try
		{
			Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
			foreach (Assembly assembly2 in assemblies)
			{
				if (assembly2.GetName().Name == "RimMT")
				{
					assembly = assembly2;
					break;
				}
			}
		}
		catch
		{
		}
		if (assembly == null)
		{
			stringBuilder.AppendLine("RimMT assembly not found.");
			return stringBuilder.ToString();
		}
		stringBuilder.AppendLine("Assembly=" + assembly.FullName);
		for (int j = 0; j < SummaryTypes.Length; j++)
		{
			Type type = null;
			try
			{
				type = assembly.GetType(SummaryTypes[j], throwOnError: false);
			}
			catch
			{
			}
			if (!(type == null))
			{
				AppendMethod(stringBuilder, type, "Summary");
				if (SummaryTypes[j].EndsWith("WorkGiverParallelSafety093T27_2", StringComparison.Ordinal))
				{
					AppendMethod(stringBuilder, type, "ApiMatrixSummary");
				}
			}
		}
		return stringBuilder.ToString();
	}

	private static void AppendMethod(StringBuilder sb, Type type, string methodName)
	{
		try
		{
			MethodInfo method = type.GetMethod(methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);
			if (!(method == null) && !(method.ReturnType != typeof(string)))
			{
				object obj = method.Invoke(null, null);
				if (obj != null)
				{
					sb.AppendLine(obj.ToString());
				}
			}
		}
		catch (Exception ex)
		{
			sb.AppendLine(type.FullName + "." + methodName + " reflection failed: " + ex.GetType().Name);
		}
	}
}
