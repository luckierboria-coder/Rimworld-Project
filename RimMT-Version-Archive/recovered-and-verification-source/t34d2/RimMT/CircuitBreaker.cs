using System;
using System.Collections.Generic;
using Verse;

namespace RimMT;

public static class CircuitBreaker
{
	private sealed class State
	{
		public int Failures;

		public bool Open;

		public string LastError;
	}

	private static readonly object Sync = new object();

	private static readonly Dictionary<string, State> States = new Dictionary<string, State>();

	private const int FailureThreshold = 2;

	public static bool IsOpen(string featureId)
	{
		lock (Sync)
		{
			State value;
			return States.TryGetValue(featureId, out value) && value.Open;
		}
	}

	public static void RecordFailure(string featureId, Exception exception)
	{
		lock (Sync)
		{
			if (!States.TryGetValue(featureId, out var value))
			{
				value = new State();
				States.Add(featureId, value);
			}
			value.Failures++;
			value.LastError = exception.GetType().Name + ": " + exception.Message;
			if (!value.Open && value.Failures >= 2)
			{
				value.Open = true;
				FeatureGate.Suppress(featureId, "circuit breaker opened after repeated worker exceptions");
				Log.Warning("[RimMT] Disabled feature '" + featureId + "' for this session after repeated failures. Vanilla fallback should be used by the caller.");
			}
		}
	}
}
