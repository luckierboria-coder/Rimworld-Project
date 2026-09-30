using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using Verse;
using Verse.Profile;

namespace RimMT;

internal static class MemoryUnloadCoalescerD23
{
	private const double DuplicateWindowMinutes = 15.0;

	private static long entryRequestTicks;

	private static int entryRequestSeen;

	private static int duplicateSuppressed;

	private static int installed;

	private static int failures;

	internal static void Apply(Harmony harmony)
	{
		//IL_004c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0051: Unknown result type (might be due to invalid IL or missing references)
		//IL_0064: Expected O, but got Unknown
		try
		{
			MethodBase methodBase = AccessTools.Method(typeof(MemoryUtility), "UnloadUnusedUnityAssets", (Type[])null, (Type[])null);
			if (methodBase == null)
			{
				throw new MissingMethodException(typeof(MemoryUtility).FullName, "UnloadUnusedUnityAssets");
			}
			harmony.Patch(methodBase, new HarmonyMethod(typeof(MemoryUnloadCoalescerD23), "Prefix", (Type[])null)
			{
				priority = 800
			}, (HarmonyMethod)null, (HarmonyMethod)null, (HarmonyMethod)null);
			installed = 1;
			Log.Message("[RimMT] D.2.3 redundant post-load Unity asset cleanup coalescer installed (one suppression, 15-minute entry-to-play window).");
		}
		catch (Exception ex)
		{
			Interlocked.Increment(ref failures);
			Log.Warning("[RimMT] D.2.3 asset cleanup coalescer failed closed: " + ex.GetType().Name + ": " + ex.Message);
		}
	}

	public static bool Prefix()
	{
		//IL_0000: Unknown result type (might be due to invalid IL or missing references)
		//IL_0005: Unknown result type (might be due to invalid IL or missing references)
		//IL_000c: Unknown result type (might be due to invalid IL or missing references)
		//IL_0083: Unknown result type (might be due to invalid IL or missing references)
		//IL_0085: Invalid comparison between Unknown and I4
		//IL_0087: Unknown result type (might be due to invalid IL or missing references)
		//IL_0089: Invalid comparison between Unknown and I4
		try
		{
			ProgramState programState = Current.ProgramState;
			long timestamp = Stopwatch.GetTimestamp();
			if ((int)programState == 0)
			{
				Volatile.Write(ref entryRequestTicks, timestamp);
				Volatile.Write(ref entryRequestSeen, 1);
				return true;
			}
			if (Volatile.Read(ref entryRequestSeen) == 0 || Volatile.Read(ref duplicateSuppressed) != 0)
			{
				return true;
			}
			double num = (double)(timestamp - Volatile.Read(ref entryRequestTicks)) / (double)Stopwatch.Frequency / 60.0;
			if (num < 0.0 || num > 15.0)
			{
				return true;
			}
			if ((int)programState == 1 || (int)programState == 2)
			{
				Interlocked.Exchange(ref duplicateSuppressed, 1);
				Log.Message("[RimMT] D.2.3 skipped one redundant post-load Unity asset cleanup after a completed entry cleanup; later cleanup requests remain enabled.");
				return false;
			}
		}
		catch
		{
			Interlocked.Increment(ref failures);
		}
		return true;
	}

	internal static string Summary()
	{
		return "D.2.3 asset cleanup coalescer: installed=" + (Volatile.Read(ref installed) != 0) + ", entryRequestSeen=" + (Volatile.Read(ref entryRequestSeen) != 0) + ", duplicateSuppressed=" + Interlocked.CompareExchange(ref duplicateSuppressed, 0, 0) + ", failures=" + Interlocked.CompareExchange(ref failures, 0, 0) + ", duplicateWindowMinutes=" + 15.0.ToString("F0") + ".";
	}
}
