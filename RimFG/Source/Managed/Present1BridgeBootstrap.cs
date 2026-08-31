using System;
using System.Runtime.InteropServices;
using Verse;

namespace RimFG
{
    [StaticConstructorOnStartup]
    internal static class Present1BridgeBootstrap
    {
        private const string NativeLibrary = "RimFG.Native";

        [DllImport(NativeLibrary, CallingConvention = CallingConvention.Cdecl)]
        private static extern int RimFG_StartPresent1Bridge();

        static Present1BridgeBootstrap()
        {
            LongEventHandler.ExecuteWhenFinished(StartBridge);
        }

        private static void StartBridge()
        {
            try
            {
                if (RimFG_StartPresent1Bridge() != 0)
                    Log.Message("[RimFG] DXGI Present1 compatibility bridge armed.");
                else
                    Log.Warning("[RimFG] DXGI Present1 compatibility bridge could not be armed.");
            }
            catch (DllNotFoundException)
            {
                // Primary RimFG bootstrap will report the missing native DLL.
            }
            catch (EntryPointNotFoundException)
            {
                Log.Warning("[RimFG] Native DLL is older than the managed Present1 bridge; update RimFG.Native.dll.");
            }
            catch (Exception ex)
            {
                Log.Warning("[RimFG] Present1 bridge startup failed: " + ex.Message);
            }
        }
    }
}
