using System;
using System.Reflection;
using HarmonyLib;

namespace RimMT.Diagnostics;

internal static class AccessToolsCompat
{
	internal static FieldInfo Field(Type type, string name)
	{
		try
		{
			return AccessTools.Field(type, name);
		}
		catch
		{
			return null;
		}
	}
}
